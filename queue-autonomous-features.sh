#!/usr/bin/env bash
# ---------------------------------------------------------------------------------------------
# Seeds the claude-queue with autonomous feature + improvement tasks for ING eBay AutoLister.
# Each task is guardrailed: build + tests must pass before committing, or it reverts. Runs fully
# hands-off (the queue invokes `claude -p --dangerously-skip-permissions`, and every prompt tells
# the agent to decide and proceed, never ask questions).
#
# Usage:
#   bash queue-autonomous-features.sh          # seed tasks, then print the queue
#   bash queue-autonomous-features.sh --run    # seed, then run the worker until limits/empty
# ---------------------------------------------------------------------------------------------
set -euo pipefail

PROJ="C:/Users/nsquires/source/repos/ING eBay AutoLister"
CQ='python C:/Users/nsquires/source/repos/claude-queue/claude-queue.py'
cd "$PROJ"

read -r -d '' GUARD <<'EOF' || true
GUARDRAILS — follow EXACTLY. You have FULL AUTONOMY; NEVER ask the user anything, NEVER wait for
input, NEVER stop to confirm. Make the best reasonable decision and proceed. Answer "yes" to any
choice you would otherwise ask about.

- Repo: C:\Users\nsquires\source\repos\ING eBay AutoLister on branch auto/queue-features-20260726.
  Work ONLY inside this repo, on this branch.
- Deliver ONE focused, cohesive, high-quality change for THIS task. Match the surrounding code
  style. Add or update comments where the codebase already does.
- BEFORE finishing you MUST run BOTH:
    dotnet build "ING eBay AutoLister/ING eBay AutoLister.csproj" -c Debug
    dotnet test  "ING eBay AutoLister.Tests/ING eBay AutoLister.Tests.csproj"
- If the build OR any test FAILS: revert everything you changed
    git checkout -- . ; git clean -fd
  and STOP. NEVER commit code that doesn't build or has failing tests.
- If BOTH pass: git add -A && git commit -m "<clear message>". If you changed wwwroot/app.js,
  bump the ?v= number in wwwroot/index.html so the browser reloads it.
- NEVER: git push; read, edit, or print credentials.json / web-credentials.json / any secret,
  key, token, or session; publish a live eBay listing; delete drafts or user data; change any API
  key; run rm -rf or `git reset --hard`; or touch any file outside this repo.
- Keep the app buildable and ALL tests green at every commit.
EOF

add() {
  eval "$CQ add \"\$GUARD

TASK: \$1\" --session \"\$2\"" >/dev/null && echo "  queued: $2"
}

echo "Seeding autonomous feature tasks..."

# ── Improve existing features ────────────────────────────────────────────────────────────────
add "Mirror the condition-aware used-photo gate to the IMAGE-UPLOAD analyze path (nlAnalyze in wwwroot/app.js). For USED items, do NOT use a found stock/online image as the listing photo — pull from /api/photos/library/for-listing (model+title) or prompt for a real photo, exactly like the Auto-Fill (nlQuickFill) path already does via nlApplyResearchPhotos." used-photo-analyze-gate

add "Build a Representative-Photo Library Manager UI in wwwroot (app.js + index.html + style.css) that uses the existing endpoints /api/photos/library (list), /api/photos/library/create, /api/photos/library/upload, and /api/photos/remove-bg. Let the seller view each model's folder, add photos (with optional background removal), create a new model folder, and delete a photo. Keep it consistent with the existing UI." photo-library-manager-ui

add "In the Opportunity Finder rows (wwwroot/app.js render + Program.cs mapping), when sell-through is unverified/degenerate (SellThroughPercent is null / RateIsUnbounded) drop the green 'Terapeak-matched' confidence badge and instead show a clear 'low confidence — thin data' indicator, so degenerate-denominator rows can't look like guaranteed flips." honest-confidence-badge

add "Add unit tests (ING eBay AutoLister.Tests) for the MarketPriceEstimator identity guard (a cheap item is NOT priced off comps of a different, pricier model that only shares a brand token) and for HostedMarketplaceRepository/HostedMarketplaceClient JSON mapping (string Price/Shipping parse to decimals, SoldDate parsing, missing fields tolerated)." tests-identity-guard-hosted

add "Add a hosted sold-comps health indicator to the Settings page: a small panel that calls the hosted comps API (MarketCompsApiUrl in credentials) with a lightweight query and shows reachable/unreachable plus an approximate result count. Do NOT print the API key. Backend endpoint + front-end display." hosted-comps-health

add "Add friendly inline validation to the New Listing form (wwwroot/app.js): before publish/draft, validate required fields (title length, price>0, quantity, weights/dimensions when shipping calc needs them) and highlight the offending field using the existing field-flagged style + scroll-to-field pattern (like nlHighlightMissingSpecifics), instead of only a bottom error bar." listing-form-validation

add "Accessibility + keyboard polish pass on the New Listing editor and Edit drawer: proper labels/aria on inputs and icon buttons, visible focus states, Escape closes drawers/modals, and logical tab order. CSS + minimal JS only; do not change business logic." a11y-editor-polish

add "Comp reference gallery quality: when showing sold-comp images, request the higher-resolution eBay CDN variant (rewrite s-l500/s-l140 to s-l1600 in the image URL) and show a clean placeholder when an image 404s (old sold-listing image rot). Reference gallery only — never as the seller's own listing photo." comp-gallery-hires

# ── New features (open-ended, guardrailed) ───────────────────────────────────────────────────
add "RESEARCH & IMPLEMENT: think about what high-volume eBay sellers of used mining/electronics gear actually need that this app does NOT already have. Choose ONE well-scoped, genuinely useful NEW feature, implement it end-to-end (backend + UI), and document what you built and why in WORK_COMPLETED.md. Respect all guardrails; keep it buildable and tested." invent-feature-1

add "RESEARCH & IMPLEMENT a second, DIFFERENT high-value feature than invent-feature-1 — focus on listing quality / SEO / conversion (e.g. title optimization, item-specifics completeness scoring, or best-offer strategy helper). Implement end-to-end and document in WORK_COMPLETED.md. Respect all guardrails." invent-feature-2

add "Add a 'bulk re-price' action: for saved drafts (Desktop\\eBayListing\\*.json via the existing DraftStore/local-drafts API), recompute a suggested price from the hosted sold-comps API and let the user review/apply. Backend + UI. Respect guardrails." bulk-reprice-drafts

add "RESEARCH & IMPLEMENT a third feature of your choice that meaningfully improves the seller's day-to-day workflow (your call — surprise us). End-to-end, documented in WORK_COMPLETED.md, guardrails respected." invent-feature-3

echo
echo "== Queue status =="
eval "$CQ status" | grep -E "Total|Queued"

if [ "${1:-}" = "--run" ]; then
  echo
  echo "Starting worker on branch $(git rev-parse --abbrev-ref HEAD) — running until limits/empty..."
  exec $CQ worker
fi
