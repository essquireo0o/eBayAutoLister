#!/usr/bin/env bash
# Acceptance checks, run from OFF the server so they cross the real internet, the real DNS and
# the real certificate. No -k anywhere: a TLS warning must fail the check, not be waved through.
set -u
H=https://app.inglisting.com

echo "=== 1. HTTPS serves the app, valid certificate ==="
curl -sSI $H | head -12
echo "--- certificate ---"
echo | openssl s_client -connect app.inglisting.com:443 -servername app.inglisting.com 2>/dev/null \
  | openssl x509 -noout -subject -issuer -dates
echo
echo "=== 2. http:// redirects to https:// ==="
curl -sSI http://app.inglisting.com | head -6
echo
echo "=== 3. Unauthenticated API requests are refused ==="
for p in /api/earnings/summary /api/listings /api/credentials /api/deals /owner; do
  printf '%-28s -> %s\n' "$p" "$(curl -s -o /dev/null -w '%{http_code}' $H$p)"
done
echo
echo "=== 4. The three anonymous endpoints still answer ==="
for p in /health /signin.html /signup.html; do
  printf '%-28s -> %s\n' "$p" "$(curl -s -o /dev/null -w '%{http_code}' $H$p)"
done
echo
echo "=== 5. No secret in the served sign-in page ==="
if curl -s $H/signin.html | grep -iE 'sk-ant|sk-live|sk-proj|-----BEGIN'; then
  echo "!!! SECRET FOUND IN SERVED PAGE !!!"
else
  echo "clean (no match)"
fi
echo
echo "=== 6. The app is NOT reachable on the public interface except through the proxy ==="
printf 'direct :8080 -> %s\n' "$(curl -s -o /dev/null -m 8 -w '%{http_code}' http://178.156.154.41:8080/health || echo 'refused/timed out')"
printf 'http by IP    -> %s\n' "$(curl -s -o /dev/null -m 8 -w '%{http_code}' http://178.156.154.41/health || echo 'refused/timed out')"
