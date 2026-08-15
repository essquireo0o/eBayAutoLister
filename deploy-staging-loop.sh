#!/usr/bin/env bash
# Endless test-site deployer. Whenever a NEW commit lands on the branch — i.e. the autonomous
# upgrades bot finished and committed a task — rebuild and redeploy the TEST site so the owner can
# try each upgrade as it lands. Production (:latest) is NEVER touched here; promoting to prod stays a
# separate, deliberate act. Run in WSL, detached; stop by killing this process.
#
# Coalescing: it tracks the last-deployed commit, so several commits landing during one build collapse
# into a single deploy to the newest HEAD rather than a backlog of builds. The build+ship is heavy on
# a ~20 Mbps uplink, so the poll interval is deliberately unhurried.
REPO="/mnt/c/Users/nsquires/source/repos/ING eBay AutoLister"
LOG="/mnt/c/Users/nsquires/source/repos/staging-deploy.log"
cd "$REPO"
LAST=""
echo "[staging-loop] started $(date)" >> "$LOG"
while true; do
  HEAD="$(git rev-parse HEAD 2>/dev/null || echo '')"
  if [ -n "$HEAD" ] && [ "$HEAD" != "$LAST" ]; then
    echo "[staging-loop] $(date) new HEAD $HEAD -> deploying" >> "$LOG"
    if bash "$REPO/deploy-staging.sh" >> "$LOG" 2>&1; then
      LAST="$HEAD"
      echo "[staging-loop] $(date) deployed $HEAD" >> "$LOG"
    else
      echo "[staging-loop] $(date) deploy FAILED, will retry next cycle" >> "$LOG"
      sleep 60
    fi
  fi
  sleep 180
done
