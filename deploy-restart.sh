#!/usr/bin/env bash
# Recreates the container so it picks up a newly loaded image AND a newly pushed .env.
#
# `docker compose up -d` rather than `restart`: restart reuses the existing container, which keeps
# both the old image and the old environment — so a shipped image and a pushed secret would both
# appear to have been deployed while the live process still had neither.
set -euo pipefail
SSH="ssh -i $HOME/.ssh/ing_hetzner -o StrictHostKeyChecking=accept-new -o ConnectTimeout=20 root@178.156.154.41"

$SSH 'cd /opt/ing-listing-engine && docker compose up -d && sleep 12 && docker compose ps'
echo "--- health ---"
$SSH 'curl -fsS -o /dev/null -w "%{http_code}\n" http://127.0.0.1:8080/health'
echo "--- last log lines ---"
$SSH 'cd /opt/ing-listing-engine && docker compose logs --tail 15'
