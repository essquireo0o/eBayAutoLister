#!/usr/bin/env bash
# Ship the locally built image to the server. Built here rather than on the box because the
# cpx11 has 2 GB of RAM and the .NET SDK build stage wants more than that is comfortable.
set -euo pipefail
docker save ing-listing-engine:latest \
  | gzip -1 \
  | ssh -i ~/.ssh/ing_hetzner -o StrictHostKeyChecking=accept-new \
        -o ServerAliveInterval=30 root@178.156.154.41 \
        'gunzip | docker load'
echo "--- image on server ---"
ssh -i ~/.ssh/ing_hetzner root@178.156.154.41 \
  "docker image inspect ing-listing-engine:latest --format '{{.Id}} {{.Os}}/{{.Architecture}}'"
