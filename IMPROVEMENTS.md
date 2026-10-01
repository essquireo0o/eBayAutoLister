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

## 2026-10-01 — Hosted: saved drafts answered 500 on app.inglisting.com; now work, one set per seller

- **Task:** `/api/local-drafts/*` answers 500 on the hosted app. Reproduce on the hosted code
  path, fix the root cause, add a regression test.
- **Reproduced first, on the real image.** `deploy-local-drafts-check.sh` (new) starts a throwaway
  container of `ing-listing-engine:latest` (a23442b) with empty data and no secrets, signs up two
  sellers and uses the drafts: **9 of 14 checks failed**, list and save both 500. The container's
  own log gives the cause: `UnauthorizedAccessException: Access to the path '/app/eBayListing' is denied`.
- **Root cause (two faults in `DraftStore`, one visible, one waiting behind it):**
  1. It kept drafts in `Desktop\eBayListing`. The container has no Desktop, .NET answers an empty
     string for a folder that does not exist, so the path became the relative `eBayListing` =
     `/app/eBayListing`, and the image runs as an unprivileged account that cannot write to `/app`.
  2. It knew nothing about users. Had that folder been writable, every seller's unpublished drafts
     (photos included) would have been listed to, and deletable by, every other seller.
- **What changed:**
  - `Services/DraftStore.cs`: takes the same `UserScope` the other stores use. Hosted: each seller
    gets `<data home>/App_Data/drafts/<user id>` on the `/data` volume, resolved per call. With
    nobody signed in (background work) it reads nothing and refuses to save rather than hand back a
    filename for a file it never wrote. Desktop build: unchanged, still `Desktop\eBayListing`.
  - The five routes moved out of `Program.cs` into `DraftEndpoints.Map(app)` (same file as the
    store) so the tests run the handlers the app really maps. On hosted, `ensure-folder` no longer
    tells the browser a path on the server's disk.
  - Second bug found by the new tests and fixed: the drafts list read `title`/`savedAt` from files
    the store itself writes as `Title`/`SavedAt`, so every draft was listed as "Untitled" with no
    date and "newest first" sorted nothing (desktop too, since v1; the screen only used the
    filename, which is why nobody saw it).
- **How verified:**
  - New `LocalDraftsHostedTests.cs`, 14 tests: the store with the signed-in user switched under it
    (own folder, invisible to the other seller through list/load/delete/save, `../1/x.json` style
    names cannot climb out, nobody signed in), and the real endpoints behind the real hosted
    sign-in over HTTP (200 not 500, two sellers two lists, survives signing back in, 401 without a
    session), plus a pin that `Program.cs` maps `DraftEndpoints` and holds no second inline copy.
  - `dotnet test … --artifacts-path obj/drafts-scratch`: **6,069 passed, 0 failed** (6,055 + 14).
    Built into a scratch folder; the app on 9332 was never stopped, started or rebuilt.
  - Hosted image rebuilt in WSL from this tree under a temporary tag (not `:latest`, not
    `:staging`; tag removed afterwards) and the same container check re-run: **14 of 14 ok**.
- **Left:**
  - **Not deployed.** app.inglisting.com still runs the old image and still answers 500 until the
    next hosted deploy (`deploy-build-image.sh` + `deploy-ship-image.sh`). After it, run
    `bash deploy-local-drafts-check.sh` in WSL: it must print 14 `ok` lines.
  - **Hosted drafts are not in the nightly backup.** `ing-backup.sh` archives the database and the
    session keys only; `App_Data/drafts/` sits on the same volume but outside the archive. Adding
    the folder to the tarball is a few lines plus a reinstall of the script on the box.
  - No size cap: a draft can carry a photo, and nothing limits how many a hosted seller keeps.
  - Wording: on hosted the screen still says drafts are in "Desktop\eBayListing" (two messages in
    `app.js`). Harmless but wrong there; not touched because `app.js` belongs to other lanes.
  - Not checked: the "Copilot SEO rewrite" job saves its fallback drafts from a background run.
    If that save happens after the request that started it has ended there is no signed-in
    seller, and it is now refused with a plain reason (before: the same 500 as everything
    else). If that job is used on hosted it needs to carry the seller who started it.

## 2026-10-01 — Hosted: "3 of 5 AI listings left today" is now on the page

- **What was wrong:** the web app gives each account a few AI generations a day, and the server
  has always answered "how many are left" at `/api/ai-quota` — but nothing on the page asked. A
  seller found out the limit by hitting it, halfway through a listing.
- **What changed:**
  - `wwwroot/app.js`: reads `/api/ai-quota` on load and writes the sentence in two places — on
    the **New AI Listing** card on the dashboard, and in a bar across the top of the AI Listing
    screen, above Auto-Fill / Analyze / Import All / Apply. "3 of 5 AI listings left today";
    amber on the last one; red "No AI listings left today" with when more arrive (in the seller's
    own clock) and that the work already written is saved.
  - Kept current without a list of AI buttons: the one fetch wrapper every request already goes
    through re-reads the number after any request that could have spent one (a write, a slow
    answer, or a 429 refusal), once per burst. Also re-read when the AI Listing screen opens, when
    the tab comes back to the front, and at the reset time, so a page left open overnight is right
    in the morning. A read that fails leaves the number as it was, never "none left".
  - `wwwroot/index.html`: the two elements, hidden in the markup; `app.js?v=175`, `style.css?v=143`.
  - `wwwroot/style.css`: `.ai-quota` (+ `--bar`, `--low`, `--out`).
  - **Desktop app: nothing shows.** It answers `enforced: false`, the elements stay hidden, and it
    never asks again. Same for a hosted account the limit does not apply to (the owner's).
  - No server code changed. The endpoint was already right; it now has tests on its shape.
- **How verified:**
  - New `AiQuotaMeterTests.cs`, 13 tests. The endpoint over real HTTP behind the real hosted
    sign-in: exactly the six field names the page reads (`enforced, limit, used, remaining,
    exhausted, resetsAt`), the reset is a parseable next-UTC-midnight instant, the number drops by
    one per generation and reading it costs nothing, the desktop build answers the same fields
    with `enforced: false` / `remaining: null`, and it is 401 without a session. Plus pins on the
    page (where it sits, starts hidden, re-read hook) and the real sentence function run under Node.
  - `dotnet test … --artifacts-path obj/quota-scratch`: **6,082 passed, 0 failed** (6,069 + 13).
    Scratch build; the app on 9332 was never stopped, started or rebuilt.
  - The real page in a real browser (Playwright, every `/api` call stubbed, nothing live touched),
    18 of 18 checks: shows "3 of 5", goes to "2 of 5" by itself after a generation, three requests
    at once cost one re-read, a quick status poll costs none, last one amber, zero red with the
    reset time, sits above Auto-Fill, a failed read keeps the old number, and on the desktop answer
    both places stay empty and the page never asks again. Looked at screenshots at desktop and
    phone width.
- **Left:**
  - **Not deployed to app.inglisting.com** (by instruction). The staging bot picks the commit up;
    production gets it with the next hosted deploy. Not yet seen against the live server with a
    real account — the browser check used stubbed answers.
  - A generation spent by a background job that outlives the request which started it would only
    show at the next re-read (next AI request, opening the AI Listing screen, or returning to the
    tab). I found no such path: no AI endpoint streams its answer, and the quota gate refuses a
    job with no signed-in seller. Not exhaustively traced through every `Task.Run`.
  - The count is per generation, and the bar says "Each AI listing, rewrite or AI check uses one".
    If the owner wants different words for what counts, it is one line in `aiQuotaWords`.

## 2026-10-01 — Hosted security audit of every endpoint: CSRF, per-account data, secrets in responses

- **Task:** go through every `app.Map*` endpoint on the hosted build (app.inglisting.com) and check
  three things: can another website make a signed-in seller's browser change something (CSRF), can
  one account read or change another account's data, and does any response hand out a secret. Fix
  what can be fixed safely with tests; list everything found.
- **What was looked at:** 242 routes on the app (209 in `Program.cs`, 33 in `Services/*`): 114 GET,
  121 POST, 6 DELETE, 1 GET+HEAD. No PUT or PATCH exists. Plus the 18 routes on the phone-camera
  listener, which only starts from an endpoint the hosted build refuses. Auto-Buy's 7 routes are
  not mapped on hosted at all.

### Findings that were FIXED in this change

| # | Severity | What was wrong | Fix |
|---|----------|----------------|-----|
| 1 | **High** | **An eBay sign-in could connect the wrong account.** The link eBay sends a seller back on (`/api/ebay/finish?session=…`) was accepted from ANY signed-in account, as long as this server had started that sign-in for somebody. A person could sign in to their own eBay, keep the link, and get another seller to open it: that seller's account would then be connected to the stranger's eBay, and their next listing would publish there. | The sign-in is now tied to the account that started it (`EbayOAuthSessionLedger` remembers the owner). Anybody else opening the link is told this account did not start that sign-in; nothing is stored and the one-time pickup is not spent. |
| 2 | **High** | Same hole, second door: `GET /api/ebay/callback?code=…` stored an eBay grant with **no check at all** on who started it. | On hosted the code is only exchanged when `state` is a sign-in this account started (`EbayService.IsSignInStartedHere`). Desktop unchanged. |
| 3 | **High** | **`/api/logs/recent` showed every account everybody's log** — item titles, eBay errors, saved-photo names, watch names — because the action log was one list for the whole server. | `ActionLog` now remembers who was signed in for each line. An account reads only its own. The server's own lines (data folder, database found) are shown to no account. The owner dashboard (admin key) still reads all of it. Each account keeps its own 100 lines. |
| 4 | Medium | **Any account could switch Facebook Marketplace off for everyone.** `POST /api/facebook/disconnect` deletes the saved Facebook login, and on hosted that login is the ONE the owner set up for all accounts. Same for `POST /api/terapeak/disconnect`, `/api/terapeak/connect`, and `GET /api/terapeak/debug-scrape` (a developer probe that drives the saved login on a plain GET). | All four answer 403 "connected once for everyone on the web version" on hosted (`HostedShared.Refusal`). Desktop unchanged. |
| 5 | Medium | `/api/ebay/status` told every account about the LAST sign-in anybody started (one status for the process), so a waiting tab could report another seller's result. | Sign-in progress is kept per account. |
| 6 | Low | A pasted-redirect eBay sign-in wrote the whole redirect URL (it contains eBay's authorization code) and the state into the log. | The log line now says only that tokens were stored and which redirect URI was used. |
| 7 | Low | `/api/setup/fields` showed every account the first 8 characters of the OWNER's licence key and the owner's eBay developer id. (The DevId item was already on the open list.) | Both are blank on hosted. Client ID and RuName stay: they are in every eBay consent link anyway. |
| 8 | Low | The identity endpoint and `/api/local-db/status` told every account the server's data folder, process id and database path. | Blank / 0 on hosted. Desktop unchanged. |

### CSRF — result: no gap found in the mechanism

- Every POST and DELETE (127 of them) passes through one check in front of the whole app
  (`Csrf.UseCsrf`): right `Origin`, plus a token the page must echo back. It has **no path
  exemptions**; a new test fails if one is ever added.
- Exactly **8 endpoints are open without signing in**: `/health`, sign-up, sign-in, the CSRF token,
  `/owner` and `/api/owner/stats` (admin key), calibration read and write (admin key). A new test
  fails if a ninth appears.
- The risk that was real was GETs that change something, because a plain link carries the session
  cookie: the two eBay sign-in endings (fixed above) and the Terapeak debug scrape (refused on
  hosted above). The other GETs with a side effect are harmless: `/api/local-drafts/ensure-folder`
  creates the caller's own empty folder; `/api/update/check?force=true` only re-asks GitHub.

### Findings still OPEN (not fixed here — each needs its own piece of work)

1. **Stores that are still ONE set for all hosted accounts** (they have no owner column yet; only
   earnings, deals, cost basis, saved listings, drafts and crash-recovery are per account):
   - **Deal Radar** (`DealRadarStore`): watches (search words and ZIP code), alerts and settings.
     Any account sees, edits and deletes everyone's. *Medium.*
   - **WhatsNot buy sheet and room book** (`LiveBuySheet`, `LiveRoomBook`): what was won and paid.
     One file each for the whole server. *Medium.*
   - **Photo Library** (`PhotoLibrary`, and the `/photos/...` files): every account sees and can
     delete every photo. *Medium.*
   - **Fees & Costs, tax bracket, Store Plan settings** (`FeeProfileStore` + the single in-memory
     `FeeProfile`): one account saving the form re-prices every screen for everyone. *Medium —
     wrong numbers rather than a leak.*
   - **Getting-started progress** (`OnboardingStore`), **category memory** (`CategoryMemoryStore`),
     **the "improve SEO" job** (`CopilotSeoJob`: one job, any account can see its results or cancel
     it). *Low.* The held live-bid comps (`LiveBidBoard`) were not examined in depth.
   - The fix for each is the pattern `DealStore`/`DraftStore` already use (`UserScope` +
     `UserOwnedTable.Migrate`, or a folder per account).
2. **`/generated-photos/*`** is readable by any signed-in account that knows the file name. The
   names are random (GUID), so they cannot be guessed. *Low.*
3. **Server-side fetch of a URL the caller supplies** (outside this task's three questions, noted
   because it was seen): `POST /api/photos/fetch-url` fetches any address with no private-address
   check; `/api/image-gen/test-endpoint`, `/api/image-gen/comfyui-models`, `/api/analyze-url` and
   `/api/bulk-import/extract-links` also take a URL (not traced to the fetch). On a server that is
   a way to reach things only the server can reach. `FrameEmbedPolicy` already has a guard that
   could be reused. *Medium.*
4. **The admin key travels in the address** (`/owner?k=…`, `/api/owner/stats?k=…`, calibration), so
   it lands in proxy logs and browser history. It is compared safely and rate-limited. *Low.*
5. **Calibration POST from the arb-bot**: the CSRF check has no exemption for it, so the bot must
   fetch a token first or it gets 403. Not checked whether it does — if calibration has silently
   stopped updating on hosted, this is why. (It fails closed, so it is not a security gap.)
6. `/api/diagnostics/connections`, `/api/facebook/status`, `/api/terapeak/status` tell every
   account the state of the shared logins (connected / why not). *Low.*

- **Files:** `Services/EbayAuthFlow.cs`, `Services/EbayService.cs`, `Services/ActionLog.cs`,
  new `Services/HostedShared.cs`, `Program.cs` (10 small edits), new test file
  `HostedEndpointAuditTests.cs` (24 tests).
- **How verified:**
  - New tests: 24 pass. They drive ONE service with the signed-in account switched underneath it
    (the same shape the app has), and read `Program.cs` to prove each endpoint asks its guard
    BEFORE it acts.
  - Full suite: 6,106 passed, 0 failed, 0 skipped (desktop configuration, built into
    `audit-scratch/`; the running app on 9332 was not touched).
  - The hosted configuration compiles (`-p:Hosted=true -r linux-x64`, 0 warnings, 0 errors).
- **Left:**
  - **Not deployed to app.inglisting.com.** Production gets it with the next hosted deploy
    (`deploy-build-image.sh` + `deploy-ship-image.sh`). Until then findings 1–8 are still live.
  - **Not tested against real eBay.** The sign-in tests use a stubbed relay. After the deploy, do
    one real "Log into eBay" on the hosted site: it must still connect (the same account starts
    and finishes it, so it should).
  - The endpoint-level guards in `Program.cs` are proved by reading the source, not by HTTP calls
    against a hosted build — the test project compiles the desktop configuration.
  - On hosted Settings the licence box now reads "(saved: — leave blank to keep)" with no
    preview. Cosmetic.
  - The open list above, most worthwhile first: Deal Radar, WhatsNot sheets, Photo Library,
    the URL-fetch guard.
