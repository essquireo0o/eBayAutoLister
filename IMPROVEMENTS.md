# Improvements log

Dated entries from unattended queue tasks: what changed, how it was verified, what is left.

## 2026-09-30 — `dotnet test` sweep: already green, nothing to fix

- **Task:** find every failing test, decide whether the test or the code is wrong, fix the minimal side.
- **Result at a23442b (2.6.12):** 6,026 passed, 0 failed, 0 skipped. No source or test file was changed.
- **The two known-stale `PhoneAiListingTests`** (red since 4c4f549 removed the manual "write the
  listing" button from the phone page) were already repaired in ab79f16 on 2026-09-18. The tests were
  the wrong side: both were rewritten, not deleted, to pin what is true now — the button is gone and
  `/c/{token}/listing` still answers the phone. All 14 tests in that class pass.
- **`AutoBuyTests` after the Codex rewrite:** already reconciled in c27fb2c. All 55 Auto-Buy tests
  (rules, preview, deal judge) pass.
- **How verified:** `dotnet test "ING eBay AutoLister.Tests/ING eBay AutoLister.Tests.csproj"
  --artifacts-path testgreen-scratch` (default desktop configuration, not `HOSTED`), counts read from
  the trx file. The installed app on port 9332 was not touched.
- **Gotcha for the next run:** the scratch output folder must sit INSIDE the repo. About 1,000 asset
  tests find the repository root by walking up from the test binary; built into `%TEMP%` they all
  fail with "could not find the repository root" — a false alarm, not a regression. `bin/` and
  `obj/` under any in-repo folder are already gitignored.
- **Left:** nothing for this task. The `HOSTED` build was not tested here.

## 2026-10-01 — Logs page: level filter, "Copy for support" with secrets hidden, obvious warnings/errors

- **Task:** on the Logs screen, add a filter by level and a "Copy for support" button that redacts
  tokens/keys, and make Warnings/Errors obvious.
- **What changed (wwwroot `index.html`, `app.js`, `style.css`; stamps `app.js?v=174`, `style.css?v=142`):**
  - **Verdict line** above the list. Red "3 errors and 2 warnings in the last 9 actions" with a
    "Show only errors" button, amber when there are only warnings, green "Nothing has gone wrong"
    when the log is clean — the answer before a single row is read.
  - **Filter** as four big buttons in plain sight (no dropdown, no fold): Everything / Errors /
    Warnings / Normal activity, each with a live count; a non-zero error or warning count stays
    red/amber. Research, Sourcing and Negotiation entries count as normal activity. An empty
    filter says "No errors" with a "Show everything" button instead of a blank list.
  - **Warning and error rows** now carry it across the whole row: solid pill, 5px coloured bar on
    the left, tinted background, red title on errors (light and dark theme). The level column went
    72px -> 116px because WARNING, RESEARCH and NEGOTIATION were running out of their pill. An
    entry from an earlier day now shows its date, not just a time.
  - **Copy for support** copies the entries currently shown as plain text with a header (time,
    app version, which filter, error/warning totals, browser). Every title and detail goes through
    `redactSecrets` first: Bearer/Basic headers, eBay `v^1.1#` tokens, `sk-` AI keys, JWTs, eBay
    App ID / Cert ID, Telegram bot tokens, any `token= / api_key= / client_secret= / password=`
    style pair, `?code=`/`&state=` in a URL, and any other 20+ character run mixing letters and
    digits. A toast says how many values were hidden. If the browser refuses to copy, the same
    redacted text appears in a box, already selected, with "press Ctrl+C".
- **How verified:**
  - `dotnet test … --artifacts-path testgreen-scratch`: **6,055 passed, 0 failed** (6,026 before +
    29 new in `LogsPageAssetTests.cs`). Built into a scratch folder; the installed app on 9332 was
    never stopped, started or rebuilt.
  - The scratch-built `AutoListerB1.dll` contains the three edited UI files byte-for-byte (embed check).
  - `node verification/logs-page-check.mjs`: **45 of 45** browser checks (Playwright, Chromium). It
    serves the working-tree UI over the running app, refuses every non-GET, and stubs the two
    requests that would call eBay. Covers counts, each filter, row styling, clean log, endpoint
    down, dark theme, a browser that refuses to copy, 390px and 820px widths, and the real log.
    Eleven made-up credentials in real shapes: none (and no tail of one) reached the clipboard;
    item numbers, file names, URLs and error text all survived. On the real log (100 entries) the
    redactor hid nothing, i.e. no false alarms on real data. Screenshots: `verification/logs-page/`.
- **Side effect to know about:** my first three browser runs (before the eBay stubs went in) each
  made the running app re-import listings and policies — read-only eBay calls, the same as opening
  the app in a new tab — and those "Import complete" lines pushed 5 older warnings out of the
  100-entry in-memory log. Nothing else in the app was touched; no data changed.
- **Left:**
  - **Not shipped.** Committed and pushed only. It reaches the desktop with the next MSI
    (`publish-update.ps1`) and app.inglisting.com with the next hosted deploy; the guardrails for
    this queue forbid installing or restarting the app.
  - The log **on screen** still shows details exactly as received (only the copy is redacted). If
    screenshots of this page get sent to people, redacting the screen too is a one-line change.
  - Pre-existing, not touched: at phone width (390px) the whole app shell is ~178px wider than the
    window and the sidebar leaves the Logs panel about 126px wide. The new controls wrap inside it,
    but the screen is not pleasant on a phone. That is a shell layout job, not a Logs one.
  - The log is in memory only: 100 entries, emptied on every app restart. A failure from before a
    restart cannot be copied for support. Persisting it would be a server change.
