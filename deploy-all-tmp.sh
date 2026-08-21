#!/usr/bin/env bash
set -euo pipefail
cd "/mnt/c/Users/nsquires/source/repos/ING eBay AutoLister"
for f in deploy-build-image.sh deploy-ship-image.sh deploy-restart.sh deploy-smoke.sh; do
  tr -d '\r' < "$f" > "/tmp/$f"
done
bash /tmp/deploy-build-image.sh
bash /tmp/deploy-ship-image.sh
bash /tmp/deploy-restart.sh
bash /tmp/deploy-smoke.sh
