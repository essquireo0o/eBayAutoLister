#!/usr/bin/env bash
# The live security audit, run from OFF the server so it crosses the real internet, the real DNS
# and the real certificate. Every claim in SECURITY.md comes from this script's output.
#
# Phased with waits, because the auth endpoints share one per-IP budget of 20 requests per 5
# minutes and this audit makes far more than 20 auth requests. The waits are the audit obeying the
# very rate limit it is checking for.
set -u
H=https://app.inglisting.com
STAMP=$(date -u +%Y%m%d%H%M%S)
A=sec-audit-a-$STAMP@inglisting.test          # gets locked out by the brute-force phase
B=sec-audit-b-$STAMP@inglisting.test          # used for the session and CSRF phases
PW='a-long-enough-audit-password'

jar() { echo "/tmp/ing-audit-$1-$STAMP.txt"; }
tok() { grep -i "ing_csrf" "$(jar "$1")" | awk '{print $NF}'; }

# A GET first to be issued a token, exactly as a browser loading the page would be.
prime() { curl -sS -o /dev/null -c "$(jar "$1")" $H/signin.html; }

# POST the way the app's own pages do: session cookie jar + the token echoed in the header.
post() { # post <jar> <path> <json>
  curl -sS -o- -w '\nHTTP %{http_code}\n' -b "$(jar "$1")" -c "$(jar "$1")" \
       -X POST "$H$2" -H 'Content-Type: application/json' \
       -H "X-CSRF-Token: $(tok "$1")" -d "$3"
}

echo "############ ING Listing Engine — live security audit"
echo "############ target: $H"
echo "############ run at: $(date -u +'%Y-%m-%dT%H:%M:%SZ')"
echo

echo "================================================================"
echo "1. RESPONSE HEADERS"
echo "================================================================"
curl -sS -D- -o /dev/null $H/ | grep -iv '^date:\|^etag:\|^last-modified:\|^content-length:\|^accept-ranges:'
echo
echo "--- http:// redirects to https:// ---"
curl -sSI http://app.inglisting.com | head -4
echo
echo "--- certificate ---"
echo | openssl s_client -connect app.inglisting.com:443 -servername app.inglisting.com 2>/dev/null \
  | openssl x509 -noout -subject -issuer -dates
echo

echo "================================================================"
echo "2. COOKIE FLAGS"
echo "================================================================"
prime B
echo "--- antiforgery cookie, set on a plain page load ---"
curl -sS -D- -o /dev/null $H/signin.html | grep -i '^set-cookie'
echo
echo "--- creating the audit account, then signing in ---"
post B /api/auth/sign-up "{\"email\":\"$B\",\"password\":\"$PW\",\"name\":\"Audit B\"}"
echo "--- session cookie, set on sign-in ---"
curl -sS -D- -o /dev/null -b "$(jar B)" -c "$(jar B)" -X POST $H/api/auth/sign-in \
     -H 'Content-Type: application/json' -H "X-CSRF-Token: $(tok B)" \
     -d "{\"email\":\"$B\",\"password\":\"$PW\"}" | grep -i '^set-cookie'
echo
echo "--- the signed-in session works ---"
curl -sS -o /dev/null -w 'GET /api/earnings/summary -> HTTP %{http_code}\n' -b "$(jar B)" $H/api/earnings/summary
echo

echo "================================================================"
echo "3. CSRF ON STATE-CHANGING ENDPOINTS"
echo "================================================================"
echo "--- 3a. signed-in POST with NO token (this is the attack) ---"
curl -sS -o- -w '\nHTTP %{http_code}\n' -b "$(jar B)" -X POST $H/api/earnings/log \
     -H 'Content-Type: application/json' -d '{"title":"CSRF probe","salePrice":1}'
echo
echo "--- 3b. same POST WITH the token (the app's own page) ---"
post B /api/earnings/log '{"title":"CSRF probe","salePrice":1}'
echo
echo "--- 3c. token present but Origin is another site ---"
curl -sS -o- -w '\nHTTP %{http_code}\n' -b "$(jar B)" -X POST $H/api/earnings/log \
     -H 'Content-Type: application/json' -H 'Origin: https://evil.example' \
     -H "X-CSRF-Token: $(tok B)" -d '{"title":"CSRF probe","salePrice":1}'
echo
echo "--- 3d. an origin that merely starts with ours ---"
curl -sS -o- -w '\nHTTP %{http_code}\n' -b "$(jar B)" -X POST $H/api/earnings/log \
     -H 'Content-Type: application/json' -H 'Origin: https://app.inglisting.com.evil.test' \
     -H "X-CSRF-Token: $(tok B)" -d '{"title":"CSRF probe","salePrice":1}'
echo
echo "--- 3e. a DELETE with no token ---"
curl -sS -o /dev/null -w 'DELETE /api/earnings/1 -> HTTP %{http_code}\n' -b "$(jar B)" -X DELETE $H/api/earnings/1
echo
echo "--- 3f. CORS: what a foreign origin is now told ---"
curl -sS -D- -o /dev/null -X OPTIONS $H/api/earnings/log \
     -H 'Origin: https://evil.example' -H 'Access-Control-Request-Method: POST' \
     | grep -i '^HTTP\|access-control' || echo "(no access-control headers returned)"
echo

echo "================================================================"
echo "4. SESSION FIXATION AND SIGN-OUT"
echo "================================================================"
SESSION_1=$(grep -i 'ing_session' "$(jar B)" | awk '{print $NF}')
echo "session id after first sign-in : ${SESSION_1:0:24}..."

# Copy the jar: this is the thief, holding a cookie lifted while the seller was signed in.
cp "$(jar B)" "$(jar THIEF)"
echo "--- the copied cookie works before sign-out ---"
curl -sS -o /dev/null -w 'thief GET /api/earnings/summary -> HTTP %{http_code}\n' -b "$(jar THIEF)" $H/api/earnings/summary

echo "--- the seller signs out ---"
post B /api/auth/sign-out '{}'
echo "--- the copied cookie AFTER sign-out (was the gap) ---"
curl -sS -o /dev/null -w 'thief GET /api/earnings/summary -> HTTP %{http_code}\n' -b "$(jar THIEF)" $H/api/earnings/summary
echo
echo "--- signing back in issues a DIFFERENT session id ---"
post B /api/auth/sign-in "{\"email\":\"$B\",\"password\":\"$PW\"}" > /dev/null
SESSION_2=$(grep -i 'ing_session' "$(jar B)" | awk '{print $NF}')
echo "session id after second sign-in: ${SESSION_2:0:24}..."
if [ "$SESSION_1" = "$SESSION_2" ]; then echo "RESULT: SAME  <-- session fixation NOT fixed"
else                                     echo "RESULT: DIFFERENT  <-- a new session id per sign-in"; fi
echo

echo "waiting 5 minutes for the per-IP auth budget to roll over..."
sleep 310

echo "================================================================"
echo "5. SIGN-UP ENUMERATION"
echo "================================================================"
prime E
NEW=sec-audit-new-$STAMP@inglisting.test
echo "--- 5a. sign-up with an address that ALREADY has an account ($B) ---"
curl -sS -D- -o /tmp/dup-$STAMP.txt -b "$(jar E)" -c "$(jar E)" -X POST $H/api/auth/sign-up \
     -H 'Content-Type: application/json' -H "X-CSRF-Token: $(tok E)" \
     -d "{\"email\":\"$B\",\"password\":\"$PW\",\"name\":\"Probe\"}" \
     | grep -i '^HTTP\|^set-cookie'
echo "body: $(cat /tmp/dup-$STAMP.txt)"
echo
echo "--- 5b. sign-up with an address that has NEVER been seen ($NEW) ---"
curl -sS -D- -o /tmp/new-$STAMP.txt -b "$(jar E)" -c "$(jar E)" -X POST $H/api/auth/sign-up \
     -H 'Content-Type: application/json' -H "X-CSRF-Token: $(tok E)" \
     -d "{\"email\":\"$NEW\",\"password\":\"$PW\",\"name\":\"Probe\"}" \
     | grep -i '^HTTP\|^set-cookie'
echo "body: $(cat /tmp/new-$STAMP.txt)"
echo
if diff -q /tmp/dup-$STAMP.txt /tmp/new-$STAMP.txt >/dev/null; then
  echo "RESULT: IDENTICAL bodies  <-- sign-up does not say which addresses exist"
else
  echo "RESULT: DIFFERENT bodies  <-- still enumerable"; diff /tmp/dup-$STAMP.txt /tmp/new-$STAMP.txt
fi
echo
echo "--- 5c. and sign-in still refuses both in the same words ---"
curl -sS -o- -b "$(jar E)" -X POST $H/api/auth/sign-in -H 'Content-Type: application/json' \
     -H "X-CSRF-Token: $(tok E)" -d "{\"email\":\"$B\",\"password\":\"wrong-password\"}"
echo " <- registered address, wrong password"
curl -sS -o- -b "$(jar E)" -X POST $H/api/auth/sign-in -H 'Content-Type: application/json' \
     -H "X-CSRF-Token: $(tok E)" -d '{"email":"no-such-person-at-all@inglisting.test","password":"wrong-password"}'
echo " <- address that has never signed up"
echo

echo "waiting 5 minutes for the per-IP auth budget to roll over..."
sleep 310

echo "================================================================"
echo "6. EIGHT BAD PASSWORDS IN A ROW"
echo "================================================================"
prime A
post A /api/auth/sign-up "{\"email\":\"$A\",\"password\":\"$PW\",\"name\":\"Audit A\"}" > /dev/null
echo "account $A created; now guessing its password eight times"
echo
for i in $(seq 1 8); do
  printf 'attempt %d: ' "$i"
  curl -sS -o- -w ' [HTTP %{http_code}]' -b "$(jar A)" -X POST $H/api/auth/sign-in \
       -H 'Content-Type: application/json' -H "X-CSRF-Token: $(tok A)" \
       -d "{\"email\":\"$A\",\"password\":\"guess-number-$i\"}"
  echo
done
echo
echo "--- and now the CORRECT password, during the lockout ---"
post A /api/auth/sign-in "{\"email\":\"$A\",\"password\":\"$PW\"}"
echo

echo "================================================================"
echo "7. OWNER DASHBOARD ADMIN KEY"
echo "================================================================"
echo "--- 7a. no key at all ---"
curl -sS -o /dev/null -w 'GET /owner              -> HTTP %{http_code}\n' $H/owner
echo "--- 7b. a wrong key, not signed in ---"
curl -sS -o /dev/null -w 'GET /owner?k=wrong      -> HTTP %{http_code}\n' $H/owner?k=deadbeefdeadbeefdeadbeefdeadbeef
echo "--- 7c. a wrong key, WITH a signed-in session ---"
curl -sS -o /dev/null -w 'GET /owner?k=wrong (in) -> HTTP %{http_code}\n' -b "$(jar B)" $H/owner?k=deadbeefdeadbeefdeadbeefdeadbeef
echo "--- 7d. the stats API with a wrong key ---"
curl -sS -o- -w '\nHTTP %{http_code}\n' -b "$(jar B)" "$H/api/owner/stats?k=deadbeefdeadbeefdeadbeefdeadbeef"
echo
echo "--- 7e. is it rate limited? 30 guesses in a row ---"
for i in $(seq 1 30); do
  code=$(curl -sS -o /dev/null -w '%{http_code}' -b "$(jar B)" "$H/owner?k=guess-number-$i")
  printf '%s ' "$code"
done
echo
echo "(429 appearing = the key is behind the same per-IP budget as the sign-in form)"
echo
echo "############ audit complete: $(date -u +'%Y-%m-%dT%H:%M:%SZ')"
