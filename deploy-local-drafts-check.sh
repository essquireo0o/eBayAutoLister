#!/usr/bin/env bash
# Local check of the hosted image's saved-drafts endpoints (/api/local-drafts/*), per user.
# Run inside WSL, like deploy-smoke.sh:   bash deploy-local-drafts-check.sh [image-tag]
#
# Starts a THROWAWAY container of the image (own empty data, port 18091, no secrets, so it can
# reach nothing and e-mail nobody), signs up two sellers, and asks the questions every image built
# before 2026-10-01 gets wrong (a23442b: 9 of these 14 lines FAIL):
#   - does the drafts list answer 200 rather than 500 (the image has no Desktop and /app is
#     read-only to the app user, which is where the desktop build's drafts folder landed);
#   - does a saved draft come back to the seller who saved it;
#   - and is it invisible to, and undeletable by, the other seller.
# Exit 0 = all good. Exit 1 = something is wrong; the container's last log lines are printed.
set -u
IMG="ing-listing-engine:${1:-latest}"
NAME=local-drafts-check
BASE=http://localhost:18091
TMP=$(mktemp -d)
fail=0

docker rm -f "$NAME" >/dev/null 2>&1
docker run -d --name "$NAME" -p 127.0.0.1:18091:8080 \
  -e CREDENTIALS_ENCRYPTION_KEY="$(openssl rand -base64 32)" \
  "$IMG" >/dev/null || { echo "could not start $IMG"; exit 1; }

for _ in $(seq 1 60); do
  [ "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/health")" = 200 ] && break
  sleep 1
done

# "localhost" and not 127.0.0.1 on purpose: the session cookie is Secure, and curl only sends a
# Secure cookie over plain http to the name localhost.
csrf() { awk '$6 == "ing_csrf" { print $7 }' "$TMP/$1.jar"; }

# call <seller> <method> <path> [json-body]  ->  prints the status, leaves the body in $TMP/body
call() {
  local who=$1 method=$2 path=$3 body=${4:-}
  curl -s -o "$TMP/body" -w '%{http_code}' -X "$method" "$BASE$path" \
    -b "$TMP/$who.jar" -c "$TMP/$who.jar" -H "X-CSRF-Token: $(csrf "$who")" \
    ${body:+-H 'Content-Type: application/json' --data "$body"}
}

expect() {  # expect <what> <got> <wanted>
  if [ "$2" = "$3" ]; then echo "ok    $1 -> $2"; else echo "FAIL  $1 -> $2 (wanted $3)"; fail=1; fi
}

stamp=$(date +%s)
for who in a b; do
  : > "$TMP/$who.jar"
  call "$who" GET /api/auth/csrf >/dev/null
  creds="{\"email\":\"drafts-$who-$stamp@example.test\",\"password\":\"Drafts-$who-$stamp-check!\",\"name\":\"Seller $who\"}"
  expect "sign up $who" "$(call "$who" POST /api/auth/sign-up "$creds")" 200
  expect "sign in $who" "$(call "$who" POST /api/auth/sign-in "$creds")" 200
done

expect "A lists drafts (the call that answered 500)" "$(call a GET /api/local-drafts/list)" 200
expect "A's list starts empty" "$(cat "$TMP/body")" "[]"

expect "A saves a draft" "$(call a POST /api/local-drafts/save '{"title":"Seller A only","data":{}}')" 200
file=$(sed -E 's/.*"filename":"([^"]+)".*/\1/' "$TMP/body")

expect "A lists drafts again" "$(call a GET /api/local-drafts/list)" 200
grep -q "Seller A only" "$TMP/body" && echo "ok    A sees the draft" || { echo "FAIL  A cannot see their own draft"; fail=1; }
expect "A opens the draft" "$(call a GET "/api/local-drafts/load/$file")" 200

expect "B lists drafts" "$(call b GET /api/local-drafts/list)" 200
expect "B's list is empty" "$(cat "$TMP/body")" "[]"
expect "B cannot open A's draft" "$(call b GET "/api/local-drafts/load/$file")" 404
call b DELETE "/api/local-drafts/delete/$file" >/dev/null
expect "A's draft survives B's delete" "$(call a GET "/api/local-drafts/load/$file")" 200

if [ "$fail" != 0 ]; then
  echo "--- container log (tail) ---"
  docker logs "$NAME" 2>&1 | grep -v -i "password\|secret\|token" | tail -15
fi
docker rm -f "$NAME" >/dev/null 2>&1
rm -rf "$TMP"
exit "$fail"
