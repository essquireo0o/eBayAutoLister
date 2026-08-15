#!/usr/bin/env bash
# Waits for the server to come back from the reboot, then proves the app came back with it and
# that nothing was started by hand.
set -u
SSH="ssh -i $HOME/.ssh/ing_hetzner -o StrictHostKeyChecking=accept-new -o ConnectTimeout=5"

echo "waiting for the box to come back..."
for i in $(seq 1 60); do
  if $SSH root@178.156.154.41 'true' 2>/dev/null; then
    echo "ssh answered after ~$((i*5))s"
    break
  fi
  sleep 5
done

echo
echo "=== uptime (proves this is post-reboot) ==="
$SSH root@178.156.154.41 'uptime -p; uptime -s'

echo
echo "=== waiting for the healthcheck to settle ==="
for i in $(seq 1 30); do
  s=$($SSH root@178.156.154.41 "docker inspect ing-listing-engine --format '{{.State.Health.Status}}'" 2>/dev/null)
  echo "  health=$s"
  [ "$s" = "healthy" ] && break
  sleep 5
done

echo
echo "=== docker compose ps, with nothing started by hand ==="
$SSH root@178.156.154.41 'cd /opt/ing-listing-engine && docker compose ps'

echo
echo "=== the site, from off the server ==="
curl -sSI https://app.inglisting.com | head -3
printf '/health              -> %s\n' "$(curl -s -o /dev/null -w '%{http_code}' https://app.inglisting.com/health)"
printf '/api/earnings/summary -> %s\n' "$(curl -s -o /dev/null -w '%{http_code}' https://app.inglisting.com/api/earnings/summary)"
printf 'http:// redirect      -> %s\n' "$(curl -s -o /dev/null -w '%{http_code}' http://app.inglisting.com)"
