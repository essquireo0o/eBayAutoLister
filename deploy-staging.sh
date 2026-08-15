#!/usr/bin/env bash
# Build the CURRENT COMMITTED tree as ing-listing-engine:staging and deploy it to the TEST site
# (https://ingtest.178.156.154.41.sslip.io), leaving production's :latest image untouched. Run in WSL.
#
# Builds from `git archive HEAD` — the committed state only — so a task the autonomous bot is midway
# through (uncommitted working-tree edits) never leaks onto the test site; only finished, committed
# work does. Prod is a separate tag and a separate deliberate deploy; nothing here can touch it.
set -euo pipefail
REPO="/mnt/c/Users/nsquires/source/repos/ING eBay AutoLister"
KEY="$HOME/.ssh/ing_hetzner"; H="root@178.156.154.41"

cd "$REPO"
REV="$(git rev-parse --short HEAD)"
BUILD="/tmp/ing-staging-build"
rm -rf "$BUILD"; mkdir -p "$BUILD"
git archive HEAD | tar -x -C "$BUILD"

cd "$BUILD"
docker build --provenance=false --sbom=false -t ing-listing-engine:staging .
docker save ing-listing-engine:staging | ssh -i "$KEY" -o StrictHostKeyChecking=no "$H" 'docker load'
ssh -i "$KEY" -o StrictHostKeyChecking=no "$H" 'cd /opt/ing-listing-staging && docker compose up -d --force-recreate'

cd "$REPO"; rm -rf "$BUILD"
echo "[deploy-staging] test site updated to $REV"
