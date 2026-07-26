#!/usr/bin/env bash
# ---------------------------------------------------------------------------------------------
# Loads a large, varied batch of guardrailed improvement/feature tasks so the autonomous worker
# runs CONTINUOUSLY until the Claude plan hits its hard limit (100%). Every task is safe: it must
# build + pass tests and commit-or-revert, on the isolated branch auto/queue-features-20260726.
#
# Usage:  bash queue-fill-to-limit.sh [COUNT]     (default COUNT=40)
# ---------------------------------------------------------------------------------------------
set -uo pipefail
COUNT="${1:-40}"
PROJ="C:/Users/nsquires/source/repos/ING eBay AutoLister"
CQ='python C:/Users/nsquires/source/repos/claude-queue/claude-queue.py'
cd "$PROJ"

read -r -d '' GUARD <<'EOF' || true
GUARDRAILS — follow EXACTLY. You have FULL AUTONOMY; NEVER ask the user anything, NEVER wait for
input, NEVER stop to confirm. Make the best reasonable decision and proceed (answer "yes" to any
choice you would otherwise ask about).

- FIRST: run `git log --oneline -25` and read WORK_COMPLETED.md. Do NOT repeat work already done —
  pick something genuinely NEW or unfinished for this task.
- Repo: C:\Users\nsquires\source\repos\ING eBay AutoLister on branch auto/queue-features-20260726.
  Work ONLY inside this repo, on this branch. ONE focused, cohesive, high-quality change.
- BEFORE finishing you MUST run BOTH:
    dotnet build "ING eBay AutoLister/ING eBay AutoLister.csproj" -c Debug
    dotnet test  "ING eBay AutoLister.Tests/ING eBay AutoLister.Tests.csproj"
- If build OR any test FAILS: revert everything (git checkout -- . ; git clean -fd) and STOP.
  NEVER commit code that doesn't build or has failing tests.
- If BOTH pass: git add -A && git commit -m "<clear message>". If you changed wwwroot/app.js, bump
  the ?v= number in wwwroot/index.html.
- NEVER: git push; read/edit/print credentials.json, web-credentials.json, or any secret/key/token;
  publish a live eBay listing; delete drafts or user data; change any API key; run rm -rf or
  `git reset --hard`; touch files outside this repo.
- Keep the app buildable and ALL tests green at every commit.
EOF

POOL=(
 "Improve error handling / user-facing messages somewhere the app currently fails silently or shows a raw exception."
 "Add or expand unit tests for an under-tested service (a meaningful new test class or several cases)."
 "Focused performance pass: remove redundant work, add caching, or speed up a slow path; note the win in the commit."
 "Improve accessibility of one screen (labels, focus states, keyboard nav, aria) without changing business logic."
 "Improve listing SEO: title optimization, or item-specifics completeness scoring with actionable hints."
 "Add a small, genuinely useful NEW feature for used-electronics sellers not already present; document it in WORK_COMPLETED.md."
 "Refactor a messy or duplicated area for clarity WITHOUT changing behavior; ensure tests still pass."
 "Improve the New Listing or Edit drawer UX: validation, inline feedback, sensible defaults."
 "Harden the hosted comps API integration: timeouts, retries, graceful degradation, clearer logs."
 "Improve the photo pipeline: background-removal quality, upscaling, or representative-library UX."
 "Add helpful empty-states, loading indicators, or tooltips where the UI is confusing."
 "Make pricing/confidence explanations transparent so the numbers are trustworthy to the seller."
 "Add input validation + friendly messages to a form that currently accepts bad input."
 "Improve mobile/responsive layout of one section of the app."
 "Write or improve developer docs / code comments for a complex area (no behavior change)."
 "Add an analytics insight or mini-dashboard that helps the seller decide what to list next."
 "Improve resilience of the eBay publish flow: clearer errors, pre-flight validation of required fields."
 "Add keyboard shortcuts / quality-of-life niceties to the listing editor."
 "Improve the Opportunity Finder result presentation, sorting, and filtering."
 "Find any TODO or rough edge in the code and polish it properly with tests."
)

add() { eval "$CQ add \"\$GUARD

TASK: \$1\" --session \"\$2\"" >/dev/null 2>&1 && echo "  + $2"; }

echo "Seeding $COUNT continuous improvement tasks..."
ts=$(date +%s)
for ((i=0; i<COUNT; i++)); do
  idx=$(( i % ${#POOL[@]} ))
  add "${POOL[$idx]}" "auto-cont-${ts}-${i}"
done

echo
echo "== Queue status =="
eval "$CQ status" | grep -E "Total|Queued|Running|Completed"
