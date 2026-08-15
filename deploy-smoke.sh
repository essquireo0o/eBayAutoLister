#!/usr/bin/env bash
# Local smoke test of the hosted image before it is shipped to the server.
set -u
docker rm -f smoke >/dev/null 2>&1
KEY=$(openssl rand -base64 32)
docker run -d --name smoke -p 127.0.0.1:18080:8080 \
  -e CREDENTIALS_ENCRYPTION_KEY="$KEY" \
  ing-listing-engine:latest >/dev/null
sleep 25
echo "--- probes ---"
for path in /health /signin.html /signup.html /api/earnings/summary /api/listings; do
  code=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:18080$path")
  echo "$path -> $code"
done
echo "--- logs ---"
docker logs smoke 2>&1 | tail -20
echo "--- health status ---"
docker inspect --format '{{.State.Health.Status}}' smoke 2>/dev/null || echo "(no healthcheck state yet)"
docker rm -f smoke >/dev/null 2>&1
