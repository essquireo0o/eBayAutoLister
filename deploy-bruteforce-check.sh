#!/usr/bin/env bash
# Proves, against the LIVE site, that guessing a password does not work.
#
# Measured before any of this existed: eight wrong passwords in a row for one account answered 401
# eight times at full speed. This is the check that the wall is up, run where it matters — across
# the real internet, the real proxy and the real container, not against a test host.
#
# Throwaway accounts only. Nothing here touches the owner's account, because the lockout is per
# account and locking the owner out of his own app for a quarter of an hour to prove a point is a
# poor trade. What IS shared is the per-IP budget: this spends about ten of the twenty attempts one
# address gets per five minutes, so do not run it twice in a row and then wonder why the sign-in
# page is refusing you.
#
#   bash deploy-bruteforce-check.sh          # the burst: 5 x 401, then 429, and a neighbour at 200
#   bash deploy-bruteforce-check.sh lift     # 15+ minutes later, against the email it printed
#
set -u
H=${HOST:-https://app.inglisting.com}
STATE=${TMPDIR:-/tmp}/ing-bruteforce-check

# Status code only. No cookie jar: every call here is meant to arrive as a stranger, and a session
# picked up from the sign-up would make the later attempts something other than what they look like.
code() {
  curl -s -o /dev/null -w '%{http_code}' -X POST "$H/api/auth/sign-in" \
       -H 'Content-Type: application/json' \
       -d "{\"email\":\"$1\",\"password\":\"$2\"}"
}

body() {
  curl -s -X POST "$H/api/auth/sign-in" -H 'Content-Type: application/json' \
       -d "{\"email\":\"$1\",\"password\":\"$2\"}"
}

signup() {
  curl -s -o /dev/null -w '%{http_code}' -X POST "$H/api/auth/sign-up" \
       -H 'Content-Type: application/json' \
       -d "{\"email\":\"$1\",\"password\":\"$2\"}"
}

if [ "${1:-check}" = "lift" ]; then
  [ -f "$STATE" ] || { echo "no earlier run to follow up — run without arguments first"; exit 1; }
  read -r VICTIM PASSWORD LOCKED_AT LOCKED_EPOCH < "$STATE"
  NOW_EPOCH=$(date -u +%s)
  ELAPSED=$(( NOW_EPOCH - LOCKED_EPOCH ))

  echo "=== The lock lifts on its own ==="
  echo "locked at $LOCKED_AT; it is now $(date -u +%H:%M:%SZ) — $((ELAPSED / 60))m ${ELAPSED}s elapsed"

  # Refuses to run early rather than printing a 429 under a heading that says it lifted. Fifteen
  # minutes is the lock and this is the check that it ENDS, so asking at fourteen proves nothing
  # and reads exactly like a failure.
  if [ "$ELAPSED" -lt 930 ]; then
    echo "TOO EARLY: the lock runs for 15 minutes. Wait another $(( (930 - ELAPSED) / 60 + 1 )) minute(s)."
    exit 2
  fi

  GOT=$(code "$VICTIM" "$PASSWORD")
  printf '%-46s -> %s\n' "$VICTIM with the RIGHT password" "$GOT"
  if [ "$GOT" = "200" ]; then
    echo "PASS — the fifteen minutes are up and nobody had to clear anything by hand."
    exit 0
  fi
  echo "FAIL — expected 200. The lock did not lift on its own."
  exit 1
fi

STAMP=$(date -u +%Y%m%d%H%M%S)
VICTIM="throwaway-victim-$STAMP@example.com"
NEIGHBOUR="throwaway-neighbour-$STAMP@example.com"
PASSWORD="throwaway-password-$STAMP"

echo "=== 0. Two throwaway accounts ==="
printf '%-46s -> %s\n' "sign up $VICTIM"    "$(signup "$VICTIM" "$PASSWORD")"
printf '%-46s -> %s\n' "sign up $NEIGHBOUR" "$(signup "$NEIGHBOUR" "$PASSWORD")"
echo

echo "=== 1. Five wrong passwords: each one genuinely was wrong ==="
for i in 1 2 3 4 5; do
  printf '  attempt %d  -> %s\n' "$i" "$(code "$VICTIM" "wrong-guess-$i")"
done
echo "  (401 each. The fifth is what sets the lock.)"
echo

echo "=== 2. The sixth is not answered at all ==="
printf '  attempt 6  -> %s\n' "$(code "$VICTIM" "wrong-guess-6")"
echo "  body: $(body "$VICTIM" "wrong-guess-7")"
echo

echo "=== 3. The RIGHT password, during the lock, is refused with it ==="
printf '%-46s -> %s\n' "$VICTIM with the right password" "$(code "$VICTIM" "$PASSWORD")"
echo "  A lock a correct guess opens is not a lock."
echo

echo "=== 4. The account next door signs in normally ==="
printf '%-46s -> %s\n' "$NEIGHBOUR with the right password" "$(code "$NEIGHBOUR" "$PASSWORD")"
echo "  The lockout is per account. One seller under attack does not take the userbase with them."

printf '%s %s %s %s\n' "$VICTIM" "$PASSWORD" "$(date -u +%H:%M:%SZ)" "$(date -u +%s)" > "$STATE"
echo
echo "Fifteen minutes from now, run:  bash deploy-bruteforce-check.sh lift"
