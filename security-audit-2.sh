#!/usr/bin/env bash
# Second pass. The first run proved the refusals but used two endpoint paths that do not exist on
# this build, so "allowed" and "not found" both came back 404 and the passing case proved less than
# it should have. This one uses real endpoints: GET /api/earnings to read and POST /api/deals to
# write, both of which are ordinary signed-in seller actions.
set -u
H=https://app.inglisting.com
STAMP=$(date -u +%Y%m%d%H%M%S)
B=sec2-$STAMP@inglisting.test
PW='a-long-enough-audit-password'

jar() { echo "/tmp/ing-sec2-$1-$STAMP.txt"; }
tok() { grep -i "ing_csrf" "$(jar "$1")" | awk '{print $NF}'; }
sid() { grep -i "ing_session" "$(jar "$1")" | awk '{print $NF}'; }
post() {
  curl -sS -o- -w '\nHTTP %{http_code}\n' -b "$(jar "$1")" -c "$(jar "$1")" \
       -X POST "$H$2" -H 'Content-Type: application/json' \
       -H "X-CSRF-Token: $(tok "$1")" -d "$3"
}

echo "############ second pass, real endpoints — $(date -u +'%Y-%m-%dT%H:%M:%SZ')"
echo

curl -sS -o /dev/null -c "$(jar B)" $H/signin.html
post B /api/auth/sign-up "{\"email\":\"$B\",\"password\":\"$PW\",\"name\":\"Audit\"}" > /dev/null
post B /api/auth/sign-in "{\"email\":\"$B\",\"password\":\"$PW\"}" > /dev/null

echo "================================================================"
echo "3. CSRF, WITH ENDPOINTS THAT ACTUALLY EXIST"
echo "================================================================"
echo "--- reading is allowed and works (GET needs no token) ---"
curl -sS -o /dev/null -w 'GET  /api/earnings                       -> HTTP %{http_code}\n' -b "$(jar B)" $H/api/earnings
echo
echo "--- 3a. POST /api/deals signed in, NO token (the attack) ---"
curl -sS -o- -w '\nHTTP %{http_code}\n' -b "$(jar B)" -X POST $H/api/deals \
     -H 'Content-Type: application/json' -d '{"title":"CSRF probe","askingPrice":1}'
echo
echo "--- 3b. the very same POST WITH the token (the app's own page) ---"
post B /api/deals '{"title":"CSRF probe","askingPrice":1}'
echo
echo "--- 3c. token good, Origin is another site ---"
curl -sS -o- -w '\nHTTP %{http_code}\n' -b "$(jar B)" -X POST $H/api/deals \
     -H 'Content-Type: application/json' -H 'Origin: https://evil.example' \
     -H "X-CSRF-Token: $(tok B)" -d '{"title":"CSRF probe","askingPrice":1}'
echo
echo "--- 3d. token good, Origin merely starts with ours ---"
curl -sS -o- -w '\nHTTP %{http_code}\n' -b "$(jar B)" -X POST $H/api/deals \
     -H 'Content-Type: application/json' -H 'Origin: https://app.inglisting.com.evil.test' \
     -H "X-CSRF-Token: $(tok B)" -d '{"title":"CSRF probe","askingPrice":1}'
echo
echo "--- 3e. token good, Origin IS ours (what the real page sends) ---"
curl -sS -o- -w '\nHTTP %{http_code}\n' -b "$(jar B)" -X POST $H/api/deals \
     -H 'Content-Type: application/json' -H "Origin: $H" \
     -H "X-CSRF-Token: $(tok B)" -d '{"title":"CSRF probe","askingPrice":1}'
echo
echo "--- 3f. a wrong token of the right shape ---"
curl -sS -o- -w '\nHTTP %{http_code}\n' -b "$(jar B)" -X POST $H/api/deals \
     -H 'Content-Type: application/json' \
     -H "X-CSRF-Token: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" \
     -d '{"title":"CSRF probe","askingPrice":1}'
echo
echo "--- 3g. DELETE with no token ---"
curl -sS -o /dev/null -w 'DELETE /api/deals/999999                 -> HTTP %{http_code}\n' -b "$(jar B)" -X DELETE $H/api/deals/999999
echo "--- 3h. DELETE with the token (404 = it reached the endpoint) ---"
curl -sS -o /dev/null -w 'DELETE /api/deals/999999 (token)         -> HTTP %{http_code}\n' \
     -b "$(jar B)" -H "X-CSRF-Token: $(tok B)" -X DELETE $H/api/deals/999999
echo
echo "--- 3i. CORS preflight from a foreign origin: every header returned ---"
curl -sS -D- -o /dev/null -X OPTIONS $H/api/deals \
     -H 'Origin: https://evil.example' -H 'Access-Control-Request-Method: POST' \
     | grep -i '^HTTP\|^access-control' || true
echo "(no Access-Control-Allow-Origin line above = a foreign page cannot read any response)"
echo

echo "================================================================"
echo "4. SESSION FIXATION AND SIGN-OUT, ON A REAL ENDPOINT"
echo "================================================================"
S1=$(sid B)
echo "session cookie #1 sha256: $(printf '%s' "$S1" | sha256sum | cut -c1-32)"
echo "session cookie #1 length: ${#S1}"

cp "$(jar B)" "$(jar THIEF)"
echo
echo "--- a copy of the cookie, taken while the seller is signed in ---"
curl -sS -o /dev/null -w 'thief GET /api/earnings BEFORE sign-out  -> HTTP %{http_code}\n' -b "$(jar THIEF)" $H/api/earnings
echo "--- the seller signs out on their own machine ---"
post B /api/auth/sign-out '{}' > /dev/null
curl -sS -o /dev/null -w 'thief GET /api/earnings AFTER  sign-out  -> HTTP %{http_code}\n' -b "$(jar THIEF)" $H/api/earnings
echo "(401 = the session was killed server-side, not merely cleared in the seller's browser)"
echo
echo "--- signing back in ---"
post B /api/auth/sign-in "{\"email\":\"$B\",\"password\":\"$PW\"}" > /dev/null
S2=$(sid B)
echo "session cookie #2 sha256: $(printf '%s' "$S2" | sha256sum | cut -c1-32)"
if [ "$S1" = "$S2" ]; then echo "RESULT: IDENTICAL  <-- fixation NOT fixed"
else                       echo "RESULT: DIFFERENT  <-- a new session id on every sign-in"; fi
echo
echo "--- and the pre-sign-out cookie is still dead after the new sign-in ---"
curl -sS -o /dev/null -w 'thief GET /api/earnings                  -> HTTP %{http_code}\n' -b "$(jar THIEF)" $H/api/earnings
echo
echo "############ second pass complete: $(date -u +'%Y-%m-%dT%H:%M:%SZ')"
