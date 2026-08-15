#!/usr/bin/env bash
# Acceptance for live sold-comps lookups on the HOSTED deployment, run from off the server so it
# crosses the real internet, the real proxy and the real session.
#
# Two things are being proved, and the second matters as much as the first: that a lookup answers
# with real rows (which no hosted deployment could do while the source was a browser scraper), and
# that asking again for the same model does NOT spend a second call off a finite paid budget.
#
# Credentials come from the environment — LIVECHECK_EMAIL / LIVECHECK_PASSWORD — so nothing here
# writes an account password into the repository.
set -u
H=https://app.inglisting.com
JAR=$(mktemp)
Q=${1:-"antminer s19 pro"}

cleanup() { rm -f "$JAR"; }
trap cleanup EXIT

# The token every unsafe request has to echo back. See Csrf: the cookie is deliberately readable.
TOKEN=$(curl -s -c "$JAR" -b "$JAR" "$H/api/auth/csrf" | sed -E 's/.*"token":"([^"]*)".*/\1/')
echo "csrf token: ${#TOKEN} chars"

post() { curl -s -c "$JAR" -b "$JAR" -H "X-CSRF-Token: $TOKEN" -H 'Content-Type: application/json' "$@"; }

echo "=== sign in ==="
BODY="{\"email\":\"$LIVECHECK_EMAIL\",\"password\":\"$LIVECHECK_PASSWORD\",\"name\":\"Live comps check\"}"
CODE=$(post -o /tmp/signin.json -w '%{http_code}' -X POST -d "$BODY" "$H/api/auth/sign-in")
echo "sign-in -> $CODE"
if [ "$CODE" != "200" ]; then
  CODE=$(post -o /tmp/signup.json -w '%{http_code}' -X POST -d "$BODY" "$H/api/auth/sign-up")
  echo "sign-up -> $CODE ($(cat /tmp/signup.json))"
  # Signing up does not sign you in — it answers with the sign-in page to go to — so the session
  # this check needs still has to be established here.
  CODE=$(post -o /tmp/signin.json -w '%{http_code}' -X POST -d "$BODY" "$H/api/auth/sign-in")
  echo "sign-in -> $CODE"
fi

if [ "$CODE" != "200" ]; then
  echo "no session — everything below would be a 401. Stopping."
  cat /tmp/signin.json; echo
  exit 1
fi

echo
echo "=== 1. live lookup for \"$Q\" ==="
RUN=$(post -X POST "$H/api/comps/live/start?q=$(printf %s "$Q" | sed 's/ /%20/g')")
echo "start   : $RUN"
ID=$(printf %s "$RUN" | sed -E 's/.*"id":"([^"]*)".*/\1/')

for _ in $(seq 1 40); do
  sleep 1
  STATUS=$(curl -s -c "$JAR" -b "$JAR" "$H/api/comps/live/status?id=$ID")
  case "$STATUS" in *'"finished":true'*) break;; esac
done
echo "finished: $STATUS"

echo
echo "=== 2. the same model again — must NOT spend a call ==="
sleep 1
post -X POST "$H/api/comps/live/start?q=$(printf %s "$Q" | sed 's/ /%20/g')"
echo
