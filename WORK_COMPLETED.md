# WORK COMPLETED — ING Listing Engine

Autonomous development session. This file is the running record of everything
inspected, changed, built and tested.

**No secrets, keys, tokens, passwords or connection strings appear in this file.**

---

## 1. Session baseline

| Item | Value |
|---|---|
| Repository path | `C:\Users\nsquires\source\repos\ING eBay AutoLister` |
| Starting branch | `main` |
| Working branch | `feature/edit-drawer-market-images-ui` |
| Git remote | `github.com/essquireo0o/eBayAutoLister` |
| HEAD at start | `1220f59` Rewrite README for the MSI installer and add Opportunity Finder docs |

### Starting git status (all preserved, nothing discarded)

```
 M build-installer.ps1
 M installer.wxs
?? .wix/
?? ING eBay AutoLister/wwwroot/.wix/
?? license.rtf
```

These pre-existing uncommitted changes were **carried onto the feature branch untouched**
and are unrelated to this session's work.

---

## 2. Technologies detected

| Layer | Technology |
|---|---|
| Framework | ASP.NET Core minimal API (`Microsoft.NET.Sdk.Web`), .NET 10 (`net10.0-windows`) |
| Host style | `WinExe` + WinForms — tray application, also runs as a Windows Service |
| Assembly | `AutoListerB1` |
| Frontend | Vanilla JS + HTML + CSS, no framework, no build step |
| UI delivery | `wwwroot` files are **`EmbeddedResource`** — UI changes require a rebuild |
| Database | SQLite (`Microsoft.Data.Sqlite` 10.0.8) |
| App database | `ING eBay AutoLister/App_Data/ing_listing_engine.db` |
| External market DB | `C:\INGListing\Data\Marketplace.db` — read-only, externally maintained |
| AI provider | `Anthropic.SDK` 5.10.0 (`ClaudeService.cs`); OpenAI path present for images |
| Payments | `Stripe.net` 51.2.0 |
| Tests | xUnit 2.9.3, 13 test files |

### Source inventory

| File | Lines |
|---|---|
| `Program.cs` | 2,589 |
| `wwwroot/app.js` | 4,788 |
| `wwwroot/style.css` | 3,723 |
| `wwwroot/index.html` | 1,538 |
| `wwwroot/editor.html` | 773 |

### Commands

```
Build : dotnet build "ING eBay AutoLister.slnx" -c Debug
Test  : dotnet test  "ING eBay AutoLister.slnx" -c Debug --no-build
Run   : ./bin/Debug/net10.0-windows/AutoListerB1.exe
```

The app always binds port 9332 — there is no port override. If a copy is already running it focuses
that one instead of starting a second server; stop the installed app first to run a dev build.

---

## 3. Backup (Phase 2) — VERIFIED

| Item | Value |
|---|---|
| Location | `G:\My Drive\ING_Backups\2026-07-22_165803\ListingEngine\` |
| Report | `G:\My Drive\ING_Backups\2026-07-22_165803\BackupReport.txt` |
| Files | 1,882 (matches source count exactly) |
| Size | 587.97 MB |
| Excluded | `bin\`, `obj\` (rebuildable artifacts only) |
| `.git` included | Yes — full history restorable |
| App database included | Yes |
| Verification | **SUCCEEDED** |

**Blocker recorded once:** `G:\ING_Backups\` could not be created — `G:\` is a Google
Drive File Stream mount point that rejects directory creation at its root. Backup was
written to `G:\My Drive\ING_Backups\` instead, which is writable. Verified, then continued.

---

## 4. Baseline build and tests (Phase 4)

Two app instances were found running:

- **PID 8580** — the installed ING AutoLister app instance, port 9332. **Left running, untouched.**
- **PID 54804** — dev build from `bin\Debug`, port 9332, holding a file lock that blocked
  the build. Stopped (dev instance only).

| Check | Result |
|---|---|
| `dotnet build` (solution) | **Succeeded** — 0 errors, 4 warnings |
| `dotnet test` | **86 passed**, 0 failed, 0 skipped |
| App launches on 9332 | Yes — HTTP 200 |
| Listings render | Yes — 88 cards |
| Browser console errors | **None** |

### Baseline problems (pre-existing, NOT caused by this session)

1. `NU1903` — `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 has a known **high severity**
   vulnerability (GHSA-2m69-gcr7-jv3q). Transitive via `Microsoft.Data.Sqlite` 10.0.8.
   4 warnings, both projects.
2. Single-clicking a listing card does nothing — editing requires the card's **Edit**
   button or a double-click. Not obvious to users.

---

## 5. Changes made

### Phase 5 — Edit Listing drawer (complete)

**Approach.** The existing listing form (`#form-section`) is large and fully wired, with
every field, collector and save handler working. Rather than duplicating or rewriting it —
which would risk silent data loss — the drawer **hosts the existing form node**. `app.js`
relocates `#form-section` into the drawer body once at startup, so all existing field ids,
validation, collectors and save paths keep working untouched. The drawer only owns
visibility, focus and unsaved-change safety.

| File | Change |
|---|---|
| `wwwroot/style.css` | Added `.edit-drawer*` styles: right-side sliding panel, `min(980px, 92vw)`, overlay with backdrop blur, ING teal header with gold accent border, responsive full-width below 720px, `prefers-reduced-motion` support |
| `wwwroot/index.html` | Added drawer shell before `#setup-overlay`: overlay div plus `<aside role="dialog" aria-modal="true">` with title, subtitle, "Unsaved" badge and close button |
| `wwwroot/app.js` | Added drawer module (`initEditDrawer`, `openEditDrawer`, `closeEditDrawer`, `snapshotDrawerState`, `refreshDrawerDirty`, `markDrawerClean`); registered `initEditDrawer()` in `init()`; `loadListingIntoForm()` now opens the drawer; `btn-new-listing` force-closes it; `applyLocalEdit()` marks it clean after save |

**Behaviour delivered**

- Opens as a right-side drawer from card **and** table Edit actions; listings stay visible behind
- Closes via close button, overlay click, or **Escape** (Escape defers to nested modals)
- Warns before closing when there are unsaved changes
- Restores page scroll position and keyboard focus on close
- Focus is kept inside the drawer while open (`aria-modal`, focus-in guard)
- Falls back to the original inline scroll behaviour if the drawer markup is absent

**Data safety.** No change to how listing data is loaded, collected or saved. The dirty
check reads control values only to detect "has anything been touched" — it never persists
or transforms listing data. Local save, draft save and live eBay revision remain distinct,
and `canReviseOnEbay()` still blocks revision of SAMPLE placeholder listings.

**Verification (real browser, Playwright)**

| Check | Result |
|---|---|
| Form relocated into drawer | Pass |
| Opens on Edit click | Pass |
| Header binds real listing title + ID + status | Pass |
| Dirty flag sets on edit | Pass |
| Unsaved-changes dialog fires on Escape | Pass |
| Drawer closes after confirm | Pass |
| Console errors | **None** |
| Build after change | 0 errors |
| Tests after change | 86 passed |

---

### Phase 6 — Market Research in the drawer (complete)

Added a **Market Research** collapsible panel to the Edit Listing form, between
Product Identifiers and Item Specifics.

**Reuses existing services — nothing duplicated.** All sold data comes from the existing
`GET /api/sold-comps`, which already layers a connected Terapeak session over the
Marketplace Insights API and falls back to eBay research deep links.

| File | Change |
|---|---|
| `wwwroot/index.html` | `#mr-panel` with 4 actions, query line, status line, 6 stat tiles, recommendation bar, comparables list |
| `wwwroot/style.css` | `.mr-*` styles — responsive stat grid, teal/gold recommendation bar, outlier highlighting |
| `wwwroot/app.js` | `bindMarketResearch`, `buildResearchQuery`, `runSoldResearch`, `renderResearch`, `recommendedPrice`, `setResearchStatus`; registered in `init()` |

**Query building** uses the strongest identifier available, in order:
UPC → EAN → ISBN → Brand+MPN → MPN → Brand+Title → Title. The basis used is shown to the
user, so the result is never a black box.

**Displayed:** average, median, low, high, sold count, data source, recommended price,
confidence note, and up to 12 comparable sales with links.

**Recommended price anchors on the median, not the mean** — on low sold counts a couple of
parts-only or mislabelled comps skew an average badly. Comparables more than 2x or less
than 0.5x the median are flagged as outliers rather than silently averaged in.

**Actions:** Research Sold Prices, Open in Terapeak, Compare Active Listings, Open
Opportunity Finder, Apply Recommended Price, Copy Average.

`Apply Recommended Price` writes to the local price field only and says so explicitly —
it never touches the live eBay listing.

**Verification (real browser)**

| Check | Result |
|---|---|
| Panel renders in drawer | Pass |
| Query built from correct basis | Pass — fell back to Title (listing had no UPC/MPN) |
| API called, response handled | Pass |
| Empty state when no data | Pass — explains unavailability, does not crash or re-prompt |
| Console errors | **None** |
| Build / tests | 0 errors / 86 passed |

---

## 6. Credential-dependent blockers

1. **Terapeak session not connected** in this environment, and the eBay account does not
   have Marketplace Insights scope approved, so `/api/sold-comps` returns `source: "none"`.
   This is expected and handled: the panel shows an explanatory empty state and still
   offers the Terapeak and eBay research deep links, which work in the seller's own
   logged-in browser. **Not retried repeatedly.** Live sold-data rendering (stat tiles,
   comparables, outlier flags) could not be visually confirmed against real data for this
   reason — the no-data path is confirmed working.

eBay tokens, Anthropic and Stripe keys are read from the app's own credential store at
runtime and were not needed for this work.

---

## 7. Session-end state

| Item | Value |
|---|---|
| Branch | `feature/edit-drawer-market-images-ui` |
| Final build | **Succeeded — 0 errors** |
| Final tests | **86 passed**, 0 failed, 0 skipped |
| Repository usable | Yes |
| Partially implemented code enabled | None — both phases are complete and verified |
| Experiments reverted | None needed |
| `INGAutoLister` service | Running, untouched throughout |
| Backup | `G:\My Drive\ING_Backups\2026-07-22_165803\` (verified, 1,882 files) |

### Commits on this branch

| SHA | Description |
|---|---|
| `e2c707e` | Add right-side Edit Listing drawer and session baseline documentation |
| `74b4ce0` | Integrate market research into the listing editor |

### Pre-existing uncommitted work — deliberately untouched

```
 M build-installer.ps1
 M installer.wxs
?? .wix/
?? ING eBay AutoLister/wwwroot/.wix/
?? license.rtf
```

Unrelated to this session (MSI installer work). Preserved exactly as found; not staged,
not committed, not reverted.

---

## 8. Exact next steps

1. **Phase 7 — stock-photo discovery.** Create `IProductImageProvider` in `Services/`.
   Start with providers that need no new credentials: existing listing images, eBay catalog
   data via the connected account, and user-supplied product URLs (the app already has
   `POST /api/photos/fetch-url` and `POST /api/photos/remove-bg` to build on).
   **Security requirement before shipping any URL fetch:** block localhost, private ranges
   (10/8, 172.16/12, 192.168/16, 169.254/16), and cloud metadata endpoints; verify
   `Content-Type` is a real image; cap download size and timeout. Treat discovered images
   as supplemental and label their source — never auto-publish them to a live listing.
2. **Phase 8** — AI Listing workflow: visible steps, review-before-publish stage.
3. **Phase 9** — GUI polish pass across cards, tables, modals, empty states.
4. **Verify Market Research against real data** once a Terapeak session is connected —
   only the no-data path has been confirmed so far (see blockers above).
5. **Address `NU1903`** — `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 high-severity advisory.
   Pre-existing; bump `Microsoft.Data.Sqlite` when a patched version is available.

### How to resume

```
cd "C:\Users\nsquires\source\repos\ING eBay AutoLister"
git checkout feature/edit-drawer-market-images-ui
dotnet build "ING eBay AutoLister.slnx" -c Debug
./ING\ eBay\ AutoLister/bin/Debug/net10.0-windows/AutoListerB1.exe
```

Port 9332 is the only port the app uses. Stop the installed app before running a dev build — a
second launch detects the running one and opens the browser at it rather than starting a server.
`wwwroot` files are embedded resources, so **UI edits require a rebuild** to take effect.
- Phase 8 — AI Listing workflow improvements
- Phase 9 — GUI polish pass
- Phase 10+ — Additional tests, bug fixes, accessibility

---

## 9. Cross-Listing Exporter (autonomous session, 2026-07-26)

Turns any finished eBay draft into ready-to-post listings for **Facebook Marketplace, Mercari
and Amazon**, from inside the Edit Listing drawer.

### Why this makes the seller money

1. **Three times the buyer pool for work already done.** The seller already wrote the title,
   description, specifics and photos. Cross-listing is pure incremental sell-through on
   inventory that's already sitting there — this is the single feature Vendoo, List Perfectly
   and Crosslist are built around, and it was the largest gap versus them.
2. **It stops the silent margin leak.** Copying an eBay price straight onto Amazon is how
   sellers quietly lose money: on a $1,250 item, Amazon's 15% referral fee nets $1,062.50
   versus eBay's $1,083.98 — $21.48 gone per unit, invisibly. Every marketplace card shows a
   **net-parity price**: the price that leaves the same take-home after *that* site's fees.
3. **It finds headroom the seller didn't know they had.** The math runs both ways. Mercari
   charges the seller no selling fee, so the panel reports that the same item can be listed
   **$108.96 below** the eBay price and still take home an identical $1,083.98 — which is how
   you win the cheapest-listing slot without earning a cent less.
4. **It prevents rejected uploads before they happen.** Amazon flat-file rejections for a
   missing GTIN, a missing brand, or unapproved used conditions are flagged per-marketplace
   with a count badge on the tab, so the seller fixes them before wasting an upload cycle.

### What it does that a copy-paste wouldn't

| Problem | Handling |
|---|---|
| Title limits differ (Mercari 80, Facebook 100, Amazon 200) | Truncated **on a word boundary**, never mid-word, with a warning naming the limit |
| Every other marketplace bans cross-site references | "eBay", "Free Shipping", "L@@K", "Buy It Now" etc. stripped from titles; description lines mentioning eBay removed, and the count is reported rather than done silently |
| eBay descriptions are HTML | Converted to plain text with entity decoding and bullet preservation |
| Facebook and Mercari have **no Item Specifics fields** | Brand / MPN / UPC / specifics folded into the description as a Details block, so structured data isn't silently lost |
| Condition vocabularies don't overlap | Explicit per-site mapping; where a target has fewer grades, the item lands on the **lower** one rather than being oversold |
| Amazon shows bullets, not prose | Up to 5 bullets derived from Item Specifics, topped up from description sentences |
| Photos may be local app URLs | Warns that a CSV import can't fetch them |

### Files

| File | Change |
|---|---|
| `Models/CrossListingModels.cs` | **New** — `CrossListRequest`, `CrossListingResult`, `CrossListingExport` |
| `Services/CrossListingExporter.cs` | **New** — title/description/condition adaptation, net-parity pricing, per-site warnings, CSV generation |
| `Services/CrossListingFeeProfile.cs` | **New** — tunable fee assumptions per marketplace, same POCO-singleton pattern as `FeeProfile` |
| `Program.cs` | DI registration + `POST /api/crosslist/export` |
| `wwwroot/index.html` | `#xl-panel` in the listing form: target checkboxes, tabs, per-site card. `app.js?v=29`, `style.css?v=24` |
| `wwwroot/style.css` | `.xl-*` styles — tabs with warning-count badges, teal/gold price card, missing-field highlighting |
| `wwwroot/app.js` | `bindCrossListing`, `runCrossListing`, `renderCrossList*`, `crossListPriceNote`, `xlCopy`, `xlDownloadCsv`; plus `moneyExact()` because the existing `money()` rounds to whole dollars and would misstate a to-the-cent parity price |
| `ING eBay AutoLister.Tests/CrossListingExporterTests.cs` | **New** — 27 tests |

### CSV honesty

Each export states what its CSV actually is, because two of the three are real and one isn't:

- **Facebook** — `catalog_csv`, ingested directly by Commerce Manager's data-feed importer.
- **Amazon** — `flat_file`, column names matching the Inventory Loader template.
- **Mercari** — `manual`. Mercari has **no public bulk importer**, so the file is labelled a
  worksheet and the copy buttons are presented as the real path. It is not passed off as an
  import file.

Fee rates are published standard rates, not the seller's account-specific ones — no marketplace
exposes a fee API — so every derived figure is labelled an estimate in the UI.

### Safety

- Purely local text/CSV generation. **No request ever reaches Facebook, Mercari or Amazon**, and
  the eBay listing is never read or modified.
- Exported CSV cells are RFC 4180 quoted **and** formula-neutralised (`=`, `+`, `-`, `@` prefixed
  with `'` unless the value is numeric). Listing text can originate from scraped supplier pages,
  and these files are opened in Excel and Google Sheets by definition.
- One deterministic SKU is shared across all three exports so inventory stays reconcilable.

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **148 passed**, 0 failed, 0 skipped (121 pre-existing + 27 new) |
| Endpoint smoke test (live app, port 9345) | Parity math verified: Amazon $1,275.28 × 0.85 = $1,083.99 ≈ eBay net $1,083.98; Mercari parity = eBay net exactly at 0% fee |
| Real browser (Playwright) | Panel opens in the Edit drawer, generates, switches tabs, re-renders per-site pricing, highlights missing required fields, renders warnings |
| Browser console errors | **None** |

One test failure was found and **fixed in the code rather than the test**: the Mercari worksheet
was missing the shared `sku` column that makes cross-site inventory reconcilable.

---

## 10. Facebook Marketplace local sourcing (autonomous session, 2026-07-26)

Searches **local** Facebook Marketplace supply by zip code + radius from inside the Opportunity
Finder, and prices what locals are asking against real eBay sold comps. Section 9 pushes finished
drafts *out* to Facebook; this reads inventory *in*.

### Why this makes the seller money

Cheap local supply is where reseller margin actually comes from — an item bought at a local ask of
$450 that sells on eBay for a $1,150 average is a $700 gross spread, and Marketplace is the largest
local supply pool in the US. Until now the app could only tell the seller what things sell *for*;
this tells them what they can *buy* today, within driving distance, and hands each result straight
to the existing sold-comps pipeline.

### Why it's a browser session and not an API

Facebook publishes **no** Marketplace search API — not a restricted one, none. So this takes the
exact posture `TerapeakService` already established, for the same reason:

- One **visible** browser window, once, where the seller logs into **their own** Facebook account.
  The app never sees or stores a password; the session cookie jar is saved to `facebook-session.json`.
- Every later search is **headless and user-driven**. Nothing is scheduled, nothing runs in the
  background, and no other feature can trigger a search as a side effect.
- One search = one page load. No crawling, no enumeration; results are capped at 120 tiles.
- An expired session is **reported**, never silently re-authenticated — reconnecting is the
  person's decision, made in Settings (identical rule to Terapeak, see §Program.cs comments).
- Per-card sold-comp lookups are one click each, never automatic for a whole result set.

### Design: selectors isolated, meaning testable

Facebook rewrites its DOM constantly, so the three concerns are split so that churn only ever
touches one small file:

| File | Role |
|---|---|
| `Services/FacebookMarketplaceSelectors.cs` | **New** — the only file with Facebook-specific strings. URL shape, login-detection, location-dialog and result-tile selectors, all as *candidate lists* tried in order (Facebook ships several layouts at once), plus the radii its dropdown actually offers |
| `Services/FacebookMarketplaceService.cs` | **New** — browser plumbing only. Visible one-time login (waits on the `c_user` cookie, not a URL, so a half-finished 2FA never gets saved), then headless search: set location from the zip via the real dialog, scroll the virtualised grid, return each tile as `{href, imageUrl, lines[]}` |
| `Services/FacebookMarketplaceParser.cs` | **New** — all interpretation, no browser. A tile has no field labels, so every line is classified by shape: price / price-drop / distance / posted-time / place / prose, with the longest prose line as the title |
| `Services/NodeRuntime.cs` | **New** — node.exe + Playwright resolution and run-a-throwaway-script, extracted from `TerapeakService` so the Windows PATH-inheritance workaround isn't duplicated. `TerapeakService` now calls it (behaviour unchanged) |
| `Models/FacebookMarketplaceModels.cs` | **New** — `FacebookRawCard`, `FacebookMarketplaceListing`, `FacebookMarketplaceSearchResult` |
| `Program.cs` | DI + `POST /api/facebook/connect`, `GET /api/facebook/status`, `POST /api/facebook/disconnect`, `GET /api/facebook/search?q=&zip=&radius=` |
| `wwwroot/index.html` | Settings card "Facebook Marketplace (Local Sourcing)" + `.fb-panel` in the Opportunity Finder. `app.js?v=30`, `style.css?v=25` |
| `wwwroot/app.js` | `loadFacebookStatus` / `facebookConnect` / `facebookDisconnect` (same shape as the Terapeak trio, both banners painted from one status call), `runFacebookSearch`, `renderFacebookResults`, `facebookCheckComp`; zip + radius remembered in `localStorage` |
| `wwwroot/style.css` | `.fb-*` — card grid, price-drop strike-through, spread colouring |
| `ING eBay AutoLister.Tests/FacebookMarketplaceParserTests.cs` | **New** — 29 tests |

### Judgement calls worth knowing about

- **Radius is snapped, then echoed back.** Facebook only offers 1/2/5/10/20/40/60/80/100/250/500
  miles, so a request for 45 is snapped to 40 and the UI reports *what was actually searched*.
  Exact ties round **up** — one extra town beats missing one.
- **A price drop is read, not ignored.** Two price lines on a tile means the lower is the live
  price and the higher is the struck-through original, i.e. a motivated seller.
- **"Free" is not $0.** Free items are listed but excluded from the min/median/max, which would
  otherwise report a local floor of zero.
- **Loosely-related padding is filtered out.** Facebook tops up thin results with unrelated items;
  those are dropped by word-match — but if *nothing* matches, everything is returned rather than
  reporting a false "no local supply".
- **Zero results + location dialog never opened** is reported as probable selector drift, not as
  an empty local market.
- Distances are normalised to miles (Facebook shows km outside the US).

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **177 passed**, 0 failed, 0 skipped (148 pre-existing + 29 new) |
| Live endpoints (dev port 9347) | `/api/facebook/status` → not connected; `/api/facebook/search?radius=45` → snapped to 40 mi, URL built as `radius_in_km=64` |
| Real browser (Playwright) | Settings card and Opportunity Finder panel render; Search disabled while disconnected; searching while disconnected shows the connect prompt instead of failing; with a stubbed API the result cards, summary, price-drop badge and per-card sold-comp lookup all render |
| Browser console errors | **None** |

One bug was found by that browser pass and fixed: the per-card comp lookup re-parsed the local
ask out of the rendered price text, so a price-drop card ("$450 was $700") read as $450,700 and
reported a nonsense spread. The card now carries the numeric price as a data attribute.

**Not verified against a live Facebook session** — that needs the user's own account and an
interactive login, which this session can't and shouldn't do. The selectors are therefore
best-effort against Facebook's current published markup; if a real search returns zero results,
`FacebookMarketplaceSelectors.cs` is the one file to tune.

---

## 11. Local Arbitrage — the "goldmine" ranking (autonomous session, 2026-07-26)

Section 10 answers *"what is for sale near me?"*. This answers the question that actually makes
money: **"which of those is worth driving to?"** — one zip code + radius + keyword, every local
Facebook Marketplace result priced against real eBay sold data, ranked by what's left **after
fees**.

`GET /api/facebook/arbitrage?q=&zip=&radius=` → the `💰 Find Goldmines` button in the Opportunity
Finder.

### Why it's a net number, not a spread

The per-card check added in §10 shows the gross spread (sold average − local ask) and says so.
Gross is the number that talks people into bad buys: a $600 local ask against a $900 sold average
looks like $300 until eBay's 13.25% + $0.40 and the shipping take $120 of it. Every row here goes
through the **same `ProfitCalculator` + `FeeProfile`** as the dropship and supplier-file paths — a
local flip is worth exactly what a dropship of the same item is worth, so it is costed by the same
rules rather than a second, friendlier formula.

Each row carries **net profit, ROI, margin** and a **max-to-pay** price: the highest local ask that
still breaks even. Net profit falls exactly one dollar per dollar paid, so that ceiling is exact
arithmetic, not a heuristic — it's the number to walk into a driveway negotiation with, which a
bare profit figure doesn't give you.

### Reuse, not a second pricing engine

`FindLocalArbitrageAsync` calls the existing `AnalyzeProductAsync`, so a local listing is priced by
the identical stack the rest of the app uses: `ProductNormalizer` → hosted sold-comps database
(`HostedMarketplaceRepository`, or the local `Marketplace.db` when the hosted API isn't configured)
→ `ComparableMatcher` → `MarketPriceEstimator` (which is where **Terapeak** enters, and where the
adaptive local-vs-Terapeak blend is decided) → `ProfitCalculator` / `ConfidenceScoringService`.
Nothing about pricing is reimplemented here; the new code is the local half, the rationing and the
ranking.

### Rationing — one click must not become hundreds of lookups

| Rule | Why |
|---|---|
| Comp lookups are per distinct **product**, not per tile | Five listings of the same drill are one lookup. Grouped on `TerapeakMarketService.BuildCacheKey(normalized title)` — the same signature Terapeak caches on — with the **fullest** title in each group used for the lookup, since the matcher can only work with the words it's given |
| Pass 1 is **cache-only** (`allowRealScrape: false`) | A product Terapeak already knows costs nothing and must not consume the budget — the same pre-check the Opportunity Finder uses |
| Pass 2 spends **≤ 5 real Terapeak scrapes** (hard cap 10) | `SelectScrapeTargets` corroborates the biggest preliminary profits first, then products the comps DB couldn't price at all, biggest local ask first. Known losers are never worth a scrape |
| `maxItems` clamped 1–60, default 30 | Bounds the fan-out of a single click |
| Skipped entirely when Terapeak isn't connected | The sold-comps DB alone still produces a full ranking |

### Verdicts are earned by evidence, not by arithmetic

`💎 Goldmine` requires **all** of: ROI ≥ 75%, net ≥ $75, ≥ 5 sold comps, confidence ≥ 50. Anything
profitable on fewer than 3 comps is `⚠️ Thin` however large the number — the same lesson as the
sell-through badge in commit `b65e570`. 400% ROI on a $5 buy is `⚠️ Thin` too: $20 is a real margin
and a pointless drive. Losers show as `✕ Pass` and unpriceable rows as `? No data` rather than
being hidden — silently dropping listings from a *sourcing* search is how a real deal gets missed.

### Judgement calls worth knowing about

- **Shipping is booked on both sides.** Buyers paid it (revenue, and eBay charges its final value
  fee on it) and it costs the seller the same to ship. Booking it on one side only is how an
  estimate ends up either inflated or double-charged. When the comps sold with free shipping there
  is no observed figure, so it falls back to `FeeProfile.DefaultShippingCost` like every other
  profit path in the app.
- **Free items are the best cost basis, not a missing one.** ROI shows as `∞` rather than 0%
  (undefined, not zero) and they still rank on net profit.
- **Sorting and filtering are pure client-side views** over the response already in hand — changing
  the sort must never re-run a multi-minute scan. Verified in the browser.
- **"Priced as" is exposed** (title attribute on the resell cell) whenever the group's lookup title
  differs from the row's own wording, rather than implying the match was against that exact tile.
- **Not modelled: your drive, your time, or condition risk on a used local item.** Said plainly in
  the panel footnote instead of being buried in a fabricated "estimated cost" column.

### Files

| File | Change |
|---|---|
| `Models/LocalArbitrageModels.cs` | **New** — `LocalArbitrageOpportunity` (local buy / eBay resale / money / verdict) and `LocalArbitrageResult` (+ what the run actually did: products priced, scrapes used, which sources were available) |
| `Services/LocalArbitrageAnalyzer.cs` | **New** — `ResalePricing`, `LocalArbitrageGroup`, and the analyzer itself: `Build` (the money, via `ProfitCalculator`) plus the pure `Judge` / `GroupByProduct` / `SelectScrapeTargets` / `Rank` / `SourceLabel` |
| `Program.cs` | DI + `GET /api/facebook/arbitrage`, and `FindLocalArbitrageAsync` — the two-pass orchestration over `AnalyzeProductAsync` |
| `wwwroot/index.html` | `💰 Find Goldmines` beside the existing search, `#fb-arb-results` ranked table, sort/filter toolbar, honesty footnote. `app.js?v=31`, `style.css?v=26` |
| `wwwroot/app.js` | `runLocalArbitrage`, `renderArbitrage`, `renderArbitrageRows`, `arbitrageRowHtml`; `handleFacebookNonResult` extracted so the plain list and the ranking share one not-connected/expired path |
| `wwwroot/style.css` | `.fb-arb-*` table, `.fb-verdict-*` badges — badge colour tracks evidence strength, not the size of the number |
| `ING eBay AutoLister.Tests/LocalArbitrageAnalyzerTests.cs` | **New** — 33 tests |

### Degradation

- **Facebook not connected / session expired** — the status passes straight through and the UI shows
  the same connect prompt the plain local search shows, never an empty ranking table. Both buttons
  are disabled while disconnected.
- **No pricing source at all** — when nothing could be priced, the response probes whether the
  sold-comps database is even reachable and returns a `dataWarning` naming what to connect
  (Terapeak, the comps DB, or both). The probe runs only on that path, since the hosted one costs a
  real HTTP request.
- **Nothing profitable** — says so ("this search has no local flip worth driving to") instead of
  rendering an empty table.

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **210 passed**, 0 failed, 0 skipped (177 pre-existing + 33 new) |
| Live endpoint (dev port 9351) | `/api/facebook/arbitrage?q=antminer s19&zip=89101&radius=45` while disconnected → `not_connected`, radius snapped 45 → 40, empty ranking, no exception |
| Real browser (Playwright, stubbed API) | Table renders and ranks; goldmine row gold-edged; free item shows `∞` ROI; unpriced row shows dashes and sorts last; sort by profit/ROI/margin/distance/price and "only show items that profit" all re-render **without re-fetching**; a disconnected response shows the connect prompt and re-disables both buttons |
| Browser console errors | **None** |

Two bugs were found by that browser pass and fixed in the code: the sort toolbar's `<select>` was
inheriting the panel's full-width form styling and pushing the controls onto three lines, and an
ROI sort put a free item (unbounded ROI) *below* a listing that loses money — free now sorts first,
with `Infinity - Infinity` guarded so two free items compare equal instead of `NaN`.

**Not verified against a live Facebook session or a populated comps database** — that needs the
user's own Facebook login and the hosted API credentials, neither of which this session can or
should use. The pricing stack it delegates to is the one the Opportunity Finder already exercises;
what is unproven end-to-end is the two-pass orchestration over real search results.

---

## 12. Craigslist sourcing + a source-pluggable arbitrage view (autonomous session, 2026-07-26)

Sections 10 and 11 could only answer "what's near me, and which of it is worth driving to" **if the
seller had connected a Facebook account first**. This adds Craigslist — which needs **no login at
all** — and makes the ranking source-pluggable, so both sites land in one ranked table with a source
label per row.

`GET /api/local/arbitrage?q=&zip=&radius=&sources=craigslist,facebook` behind the same
`💰 Find Goldmines` button.

### Why Craigslist is the source that actually gets used

The goldmine table was gated on a Facebook login: a fresh install had a local-sourcing feature that
did nothing until the seller logged a browser into their own account. Craigslist search results are
**public**, so this path is a plain HTTPS GET — no account, no session, no Playwright, no cookies.
`CraigslistService` is ~150 lines against `FacebookMarketplaceService`'s ~390 for the same job, and
the source picker now defaults to whatever can answer *right now*, which on a clean install is
Craigslist alone.

### What Craigslist actually serves today (checked against the live site, not assumed)

The plan was the RSS feed. Reality, verified during this session:

| URL | Result |
|---|---|
| `…/search/sss?query=iphone&format=rss` | **403 Forbidden** — every user agent, including Chrome's |
| `…/search/sss?query=iphone&postal=89101&search_distance=25` | **200**, 95 posts, filter honoured |

Craigslist renders results **twice**: a JavaScript grid, and an `ol.cl-static-search-results` list
emitted server-side and hidden by CSS for clients that run JS. That static list is complete, needs
no browser, and honours `postal` + `search_distance`. So the static list is the **primary** path and
RSS is the fallback, tried only when the page came back fine and empty — the opposite of the
intended order, because that's what the site does. The RSS parser is kept and tested: it's one cheap
request on an otherwise-empty search, it still works on some boards, and it carries a real timestamp
and a thumbnail the static list doesn't.

The app identifies itself honestly (`ING-AutoLister/1.0 …`) rather than impersonating Chrome —
checked, and it gets the same 200. One search is one request (two if the first found nothing): no
paging, no crawling, no following into post pages.

### Source-pluggable, meaning the pipeline no longer knows what a Facebook is

| Before | After |
|---|---|
| `FacebookMarketplaceListing` | `LocalSupplyListing` (+ `Source`, `SourceLabel`, `PostedUtc`) |
| `FacebookMarketplaceSearchResult` | `LocalSupplySearchResult` (+ `SourceId`, `ScopeLabel`) |
| `LocalArbitrageAnalyzer.Build(FacebookMarketplaceListing, …)` | `Build(LocalSupplyListing, …)` |
| `facebook.SearchAsync(...)` called directly | `ILocalSupplySource.SearchAsync(...)` over a registry |

Adding OfferUp is now: implement `ILocalSupplySource`, register it, done. The picker in the UI is
rendered from `/api/local/sources`, so a new source appears there **without an HTML change**.

Products are grouped **across** sources, which is where multi-source pays for itself: the same drill
listed on Craigslist *and* Facebook is one sold-comp lookup, not two.

### Judgement calls worth knowing about

- **`$0` is "no price stated", not free.** Found by running a live search: craigslist prints `$0` for
  every post whose seller left the price blank, and they're common. Read literally, each one is a
  free item with unbounded ROI — a whole class of fake goldmines at the top of the table. A `$0` post
  is dropped unless the wording says it's free.
- **One site failing must never blank a search another site answered.** `RollUpStatus` returns `ok`
  if *any* source returned results; the connect/expired prompts only appear when nothing answered.
  The search buttons are no longer gated on the Facebook connection — only on "is any source ticked".
- **The analysis cap is shared round-robin, not applied to one flat list.** Craigslist returns ~50
  rows from one cheap call and Facebook a handful from an expensive page load, so cheapest-first over
  the merged list would spend the whole budget on one site and report the other as having no local
  supply.
- **Craigslist is organised by metro, so a zip picks a board.** `CraigslistSites` maps ~230 sites by
  ZIP3 prefix, falls back to the numerically nearest prefix (USPS assigned them geographically), and
  **reports which board it picked** — with a manual override, because that fallback is a heuristic
  and a seller on a boundary knows their own metro. Craigslist itself does the real distance
  filtering from `postal` + `search_distance`, so the site choice only has to land on the right city.
- **Craigslist publishes no per-post distance** (it filters server-side instead), so those rows show
  a dash rather than a fabricated number, and the panel footnote says so.
- **Post ids are unique per site, not across sites**, so dedupe keys on `(source, id)`. Both live
  permalink shapes are parsed — the classic `7712345678.html` and the current
  `craigslist.org/view/d/<slug>/<id>`.

### Files

| File | Change |
|---|---|
| `Models/LocalSupplyModels.cs` | **New** — `LocalSupplyListing`, `LocalSupplySearchResult`, `LocalSupplySourceOutcome`, `LocalSupplyMultiResult`, `LocalSupplySourceInfo` |
| `Services/ILocalSupplySource.cs` | **New** — the interface + `LocalSupplySources` registry (resolves `sources=`, decides what "no preference" means) |
| `Services/CraigslistService.cs` | **New** — public search, no login: static-list fetch, RSS fallback, rate-limit handling |
| `Services/CraigslistParser.cs` | **New** — search URL, RSS/RDF parse, static-HTML parse, title/price/place cleanup. All pure |
| `Services/CraigslistSites.cs` | **New** — ~230 craigslist metros with their ZIP3 coverage, and the zip → site resolution |
| `Services/LocalSupplyMerger.cs` | **New** — status roll-up, cross-source merge, round-robin cap sharing |
| `Services/LocalSupplyResults.cs` | **New** — relevance filter, dedupe and ask-spread summary, shared by both sources (lifted out of `FacebookMarketplaceParser`) |
| `Services/FacebookMarketplaceService.cs` | Now implements `ILocalSupplySource`; returns the shared result type |
| `Services/FacebookMarketplaceParser.cs` | Emits `LocalSupplyListing`; relevance/dedupe/summary delegated to `LocalSupplyResults` |
| `Services/LocalArbitrageAnalyzer.cs` | Source-agnostic: `Build`/`GroupByProduct` take `LocalSupplyListing`; rows carry the source |
| `Models/LocalArbitrageModels.cs` | `Source`/`SourceLabel`/`PostedUtc` on the opportunity, `Sources[]` on the result |
| `Program.cs` | DI for both sources + registry; `GET /api/local/sources`, `/api/local/search`, `/api/local/arbitrage`, `/api/craigslist/search`, `/api/craigslist/sites`. `FindLocalArbitrageAsync` now takes `IReadOnlyList<ILocalSupplySource>`. `/api/facebook/arbitrage` kept as a Facebook-only alias |
| `wwwroot/index.html` | Source picker, Craigslist metro override, Source column, renamed panel. `app.js?v=32`, `style.css?v=27` |
| `wwwroot/app.js` | `loadLocalSources`, `selectedSourceIds`, `refreshLocalSearchButtons`, `loadCraigslistSites`, `renderSourceOutcomes`; `runFacebookSearch` → `runLocalSearch`, `handleFacebookNonResult` → `handleLocalNonResult`; source badges on cards and rows |
| `wwwroot/style.css` | `.local-source*`, `.cl-site-row`, `.local-badge*` |
| `ING eBay AutoLister.Tests/CraigslistParserTests.cs` | **New** — 27 tests |
| `ING eBay AutoLister.Tests/CraigslistSitesTests.cs` | **New** — 20 tests |
| `ING eBay AutoLister.Tests/LocalSupplyMergerTests.cs` | **New** — 12 tests |
| `ING eBay AutoLister.Tests/LocalSupplySourcesTests.cs` | **New** — 6 tests |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **278 passed**, 0 failed, 0 skipped (210 pre-existing + 68 new) |
| Live Craigslist search (dev port 9363) | `/api/craigslist/search?q=iphone&zip=89101&radius=25` → **48 real listings**, $1–$950, Las Vegas board, real titles/prices/locations |
| Live multi-source (dev port 9364) | `sources=craigslist,facebook` while Facebook is disconnected → `status: ok`, Craigslist results returned, Facebook reported `not_connected` — **the disconnected site did not blank the search** |
| Live arbitrage end-to-end | 2 listings found → analyzed → grouped into 2 products → priced → ranked, with the honest `no_data` verdict and data warning (no comps DB configured here) |
| Real browser (Playwright) | Picker renders from the API with correct badges; both search buttons **enabled while Facebook is disconnected**; 301-entry site override populated and width-constrained; Source column and badges render; source chips report each site's outcome; footnote and warning render |
| Browser console errors | **None** |

**Not verified:** the Facebook half of a mixed-source ranking against a live Facebook session, and
profit numbers against a populated comps database — both need credentials this session can't use.
The Craigslist half was exercised end-to-end against the real site.

---

## 13. Inventory Health — repricing the money already stuck in live listings (autonomous session, 2026-07-26)

A whole-app audit was run first: home → AI research → pricing → listing editor → publish → post-sale.
Every screen in the app points **forward** at inventory the seller has not bought yet — research it
(§Market Research), source it (§10–12), price it, list it, cross-list it (§9). Past the Publish
button there was **nothing**. `EbayService.GetListingsAsync()` fetched the seller's live listings and
the app only ever used them to populate cards, and `ReviseInventoryStatusAsync` — the ability to
change a live price — existed and nothing called it.

That gap is where a working reseller's money actually is. A seller with 200 live listings has a large
fraction of their capital in items that stopped selling months ago at a price the market has since
drifted below, and **nothing in eBay Seller Hub ever tells them so.**

`GET /api/inventory/health` → the new **💸 Inventory Health** page.

### Why this and not something else

| Candidate | Why not |
|---|---|
| Order / P&L dashboard | Reporting. Tells the seller what already happened; changes no decision. |
| More sourcing sources | §10–12 already cover the inbound side; a fourth site is incremental. |
| **Aged-inventory repricing** | **Acts on capital already spent, on the one axis the seller controls today.** |

It is also the widest competitive gap. Vendoo, List Perfectly and Crosslist are cross-listing tools —
they move listings between sites and never price them against sold data. ZIK researches products the
seller does not own yet. eBay's own **Markdown Manager takes a blanket percentage off with no market
data, no cost basis and no floor** — it will happily discount an item straight through a loss. This
app already owned every piece needed to do it properly (`MarketPriceEstimator`, the hosted comps DB,
Terapeak, `ProfitCalculator`, `FeeProfile`); what was missing was the join.

### The money

Three distinct leaks, each reported as a dollar figure:

1. **Capital that has stopped moving.** Every listing over 90 days is counted at the truest basis
   available — what was paid where known, market value otherwise — so the headline reads
   "$185,664 sitting in listings older than 90 days", not "11 stale listings". A statistic is not a
   decision; a number with a dollar sign is.
2. **Prices the market has drifted below.** Each listing gets a suggested price from an age-laddered
   discount to *today's* market, floored at break-even, applied to eBay in one confirmed action.
3. **The quiet half nobody looks for: underpricing.** Listings sitting *below* market are reported
   with what they give away per sale. A markdown tool that only ever cuts prices misses this entirely.

### Cost basis — the number eBay cannot supply

`CostBasisStore` (new SQLite table in the app's own database) holds what the seller paid per unit,
plus inbound freight as a separate field, because sellers know those as two numbers and asking them
to pre-add is how the figure ends up blank or wrong.

Keyed on listing ID **with SKU as a secondary key**, because neither is reliable alone: listings
created on the eBay website often have no SKU, and an ended-and-relisted item gets a **new listing ID
while keeping its SKU**. A save supplying both collapses rows that had matched separately, so a
relist never silently loses the cost the seller already entered.

Without it the feature still works — it just says so, on the rows it is recommending a price for,
rather than pretending the floor was checked.

### What the analyzer refuses to do

The judgement rules are the product. Most of them exist to **not** make a confident recommendation:

| Rule | Why |
|---|---|
| Never below break-even | `ProfitCalculator.BreakEvenSalePrice` — the same fee model as every other profit path. A markdown through cost is the one failure mode that turns a repricing tool into a loss-making one. |
| Break-even above market → **no price at all** | `underwater`. There is no profitable price; saying so *is* the answer. A suggestion here would be a suggested loss. |
| Max **35%** cut per revision | A tool that answers a 90-day-old listing with "minus 60%" is a panic button. Deep cuts are reached over successive scans, with the seller seeing each step. |
| Max **25%** raise per revision | Same reasoning inverted. |
| Under a 2% / $1 change → nothing | A one-cent revision churns the listing and changes no buyer's mind. |
| Under 3 sold comps → nothing | The same evidence bar the local-arbitrage verdicts use. |
| Fresh listings (<30 days) are not marked down | A listing that has not had a fair run at its price does not need a discount — **unless** it is 25%+ over market, which is mispricing rather than newness. |
| Fresh listings *are* checked for **underpricing** | The grace period exists to avoid cutting early; it has no bearing on a listing priced too low. That is the one case where waiting costs money — once it sells, the difference is gone. |
| Raises are never bulk-applied | `RequiresReview`. A raise makes a stronger claim than a markdown and is a per-item judgement. |
| Below market and unsold 90+ days → **no raise** | That is evidence the comp match is wrong for this item (different condition, missing parts, weaker photos), not evidence the price is too low. |
| 3+ watchers at a fair price → **hold** | Watchers are the only free demand signal eBay gives. The item is close; a markdown there gives away margin the seller did not have to spend. |
| 5+ watchers on an aged listing → **meet market, don't undercut it** | An audience that size says price is the only remaining blocker. |
| Charm pricing (`.99`) | Rounds **down**, so it errs cheap on a markdown and conservative on a raise, and never crosses the floor to get there. |

### Three defects the real-inventory run caught

The scan was pointed at the connected account's **87 live listings** rather than reasoned about in
the abstract. Each of these produced a confident, expensive, wrong answer, and each is now a test:

1. **Multi-unit lots priced against per-unit comps.** "Lot of 20 — Antminer S19" at $3,000 matched a
   $35 single-unit comp: an 8,471% gap and a recommended 35% cut. Lot quantity is now read off the
   title by the existing `ProductNormalizer`, and a lot listing gets **no recommendation** — the comps
   are per unit and the ask is for twenty of them. Scaling by N would be worse, since lots trade at a
   discount to N× the single-unit price.
2. **Comp-match failures read as mispricing.** A gap past **±300%** is not a seller listing at four
   times the going rate by accident; it is the matcher having found something else. Those rows now
   report a matching failure and recommend nothing, and their fictional gaps are kept out of the
   headline "priced above market" total.
3. **A proven seller marked for a 27.6% cut.** The worst one. A listing 138 days old and 38% "above
   market" — that had **sold 44 units** and had **64 watchers**. The ladder wanted $105 off each of
   the 80 remaining: **$8,400 of margin off a listing that was demonstrably working.** A listing that
   has sold units has settled the question the comps were only estimating, so `QuantitySold > 0` now
   suppresses markdowns entirely, and age stops meaning "stuck" on a multi-quantity listing (it means
   how long the *listing* has been up, not how long the *stock* has sat). Verdict `🔁 Selling` —
   "leave it alone" — and those listings no longer count towards stale capital, which is why that
   figure fell from $260,218 to $185,664 on the same inventory.

Sales rate is reported per month but labelled a **lifetime average**: eBay returns a cumulative sold
count with no dates, so it cannot distinguish a steady seller from one that sold out fast and stopped.

### Live-price writes: three independent brakes

`POST /api/inventory/reprice` is the only endpoint in the app that changes prices on listings buyers
can already see.

1. **Previews by default.** `dryRun` must be explicitly false.
2. **`confirmed` must also be true** — the same posture `/api/listing/update` takes with
   `ManualRevisionConfirmed`. Either flag alone yields a preview.
3. **The floor is recomputed server-side**, never trusted from the request body, and a price below it
   is refused. A seller who has decided to clear stock at a loss can opt in per batch, and that
   override is recorded in the action log.

Plus a 100-listing batch cap, and a typed confirmation dialog listing every price change rather than
a one-click bulk button.

### A pre-existing bug fixed on the way

`GetListingsAsync` merged the Inventory API over the Trading API **whole-object**. The Inventory API
has no concept of a watch count, view-item URL, category name or start time, so every API-created
listing had all four silently blanked. The merge is now field-level. Those are precisely the fields
this feature runs on.

### Rationing

Identical posture to `FindLocalArbitrageAsync`, because one click over a 300-listing inventory would
otherwise be 300 comp lookups: lookups are **per distinct product** (grouped on the signature Terapeak
also caches on), pass 1 is **cache-only**, and pass 2 spends a capped scrape budget ordered by
**dollars at stake, not percent wrong** — a 40% gap on a $12 item is not worth a page load and a 16%
gap on a $1,400 one is. When the item cap bites, the highest-value listings are scanned first.

### Files

| File | Change |
|---|---|
| `Models/InventoryHealthModels.cs` | **New** — `InventoryHealthItem`, `InventoryHealthSummary`, `InventoryHealthResult`, and the reprice request/result types |
| `Services/InventoryHealthAnalyzer.cs` | **New** — `Build` (the money, via `ProfitCalculator`) plus the pure `SuggestPrice` / `Judge` / `Charm` / `DaysListed` / `Summarize` / `Rank` / `SelectScrapeTargets` |
| `Services/CostBasisStore.cs` | **New** — per-listing cost basis, dual-keyed on listing ID and SKU |
| `Services/EbayService.cs` | Captures `StartTime` / `QuantitySold` / `HitCount`; **field-level merge fix** |
| `Models/ListingData.cs` | `StartTimeUtc`, `QuantitySold`, `HitCount` on `EbayListingSummary` |
| `Program.cs` | DI + `GET /api/inventory/health`, `POST /api/inventory/reprice`, `GET`/`POST`/`DELETE /api/inventory/cost-basis`, and `ScanInventoryHealthAsync` |
| `wwwroot/index.html` | Nav entry, `#inventory-section` overlay, summary tiles, ranked table, confirmation dialog. `app.js?v=33`, `style.css?v=28` |
| `wwwroot/app.js` | `bindInventoryHealth`, `runInventoryScan`, `renderInventorySummary`, `renderInventoryRows`, `invRowHtml`, selection/override/cost-basis handlers, `openRepriceConfirm`, `submitReprice` |
| `wwwroot/style.css` | `.inv-*` — tiles, table, verdict badges, confirmation dialog |
| `ING eBay AutoLister.Tests/InventoryHealthAnalyzerTests.cs` | **New** — 53 tests |
| `ING eBay AutoLister.Tests/CostBasisStoreTests.cs` | **New** — 11 tests |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **342 passed**, 0 failed, 0 skipped (278 pre-existing + 64 new) |
| Live scan, real account (dev ports 9371–9375) | **87 active listings** read; ages parsed (median 199d); 48 distinct products priced from the hosted comps DB; lots and bad matches correctly refused; two proven sellers correctly left alone |
| Reprice safety gates (live endpoint, dry runs only) | Default → preview; `dryRun:false` + `confirmed:false` → **still preview**; $850 against a recomputed $922.65 break-even → **skipped**; 101 items → **HTTP 400** |
| Real browser (Playwright, live data) | Page renders; tiles, 50 rows, verdict badges, `n/a` gaps on uncomparable rows; filtering re-renders with **0 refetches**; cost-basis entry persists and produces a real break-even; price override recalculates; preview reports "nothing sent to eBay"; confirmation dialog lists each change with the loss override defaulting off; empty-filter state |
| Confirmed live write | **Never executed.** The browser test asserts zero requests carrying `confirmed: true`. |
| Browser console errors | **None** |

**Not verified:** an actual applied price change against live eBay — that is a real, buyer-visible,
hard-to-reverse write on the seller's own account and is theirs to make. The `ReviseInventoryStatus`
call it delegates to is the one already used elsewhere in the app. Test cost-basis rows created during
verification were deleted afterwards.

**Known limitations, stated in the UI rather than hidden:** storage cost, the seller's time and return
risk are not modelled; fee rates are eBay's published ones rather than account-specific, since no
marketplace exposes a per-account fee API; and listings whose age eBay does not report are counted
separately rather than assumed new.

---

## 14. Premium design pass — making it look like something worth paying for (autonomous session, 2026-07-26)

Thirteen sessions of features had each brought their own spacing, their own greys and their own
idea of what a heading weighs. Individually every screen was fine; together they read as thirteen
screens. This session added no capability — it made the existing capability look like one product.

Nothing server-side changed. `Program.cs`, every service and every model are untouched; the diff is
three files under `wwwroot`.

### A token layer, and the six variables that were never declared

`:root` now carries the whole system — a 4px space scale, a radius scale, a type scale, four
elevation levels, semantic colour pairs (`--success` / `--success-soft` / `--success-line` and the
same for danger, warning, info), motion easings, and one focus ring. Component rules consume those
instead of inventing hex values, which is what let one pass fix spacing everywhere rather than
screen by screen.

While auditing it, six variables turned up that were **used but never defined**: `--bg`, `--surface`,
`--card-alt`, `--border`, `--text`, `--text-muted`. Later features had been written against generic
names that this stylesheet never had. An unresolved `var()` invalidates the entire declaration, so
each of those rules had been silently dropped by the parser the whole time — the Terapeak and
Facebook status banners, among others, had been rendering with no border colour at all. They are now
aliases onto the real tokens, which repairs the rules and keeps both vocabularies pointing at one
palette.

### Empty, loading and error states — the actual product gap

Every data view in the app has three non-happy moments, and all of them were being rendered as a
grey sentence. "Connect eBay, then import listings." is indistinguishable from a defect, and
`Import failed: <raw API text>` in a thin bar is the app's worst moment presented as its smallest.

`stateBlockHtml` / `renderState` build one state block — icon, headline, one line of plain
explanation, and the buttons that resolve it — and it is now generic enough that any future screen
gets the treatment for free:

| Where | Before | After |
|---|---|---|
| Listings, not connected | one grey line | "Connect eBay to see your listings" + **Log into eBay** / **Create an AI listing** |
| Listings, connected but empty | one grey line | "No active listings on this account" + **Create an AI listing** / **Import again** |
| Listings, import failed | red-ish text in the bar | full error panel, raw API text kept as evidence in a monospace block, + **Try again** / **Open logs** |
| Listings, search matched nothing | one grey line | "No listings match X", says what was searched across how many, + **Clear search** |
| Inventory Health, before a scan | one grey sentence on a blank page | the page's resting state, with what a scan does and that it is read-only |
| Photo Library, empty model | an orange sentence squeezed into a 160px grid column | a state block spanning the grid |
| Logs, empty / unreachable | a fake log row reading "Loading" | a real empty state; errors offer **Try again** |
| Activity, nothing yet | blank panel | "No activity yet" with what will appear there |

Loading is now **skeletons** rather than the word "Loading": eight card-shaped placeholders in the
listings grid, rows in the table and the log. A skeleton can't be mistaken for content — the log's
old placeholder was a log row that said `Info | Loading` — and the page doesn't jump when data lands.

### Defects the pass turned up, each visible in a screenshot

Every screen was driven in a real browser against the connected account's 87 live listings rather
than reasoned about. That found things no amount of reading the CSS would have:

1. **The 135° page gradient was sized by document length.** On a long page the dark wedge dragged
   down past the content and left a black void beside the short activity rail. Replaced with a fixed
   dark band that fades out by 250px — the same header at any scroll depth.
2. **`.opp-hint { margin-top: -10px }` printed the hint on top of the heading above it.** Visible on
   both Opportunity Finder panels.
3. **The listings table couldn't fit its twelve columns.** In the dashboard's two-column grid it had
   ~880px of a needed ~1,000, so titles wrapped to five lines, rows were three times taller than
   necessary and the last two columns were off-screen. Table view now drops the activity rail below
   the table, clamps titles to two lines, and right-aligns price / qty / watches.
4. **The listing card footer held four items and wrapped.** Watch count moved onto the photo as an
   overlay badge — it is a property, not an action — and the link and Edit button now group right.
5. **The setup checklist was dark-on-dark**, in a `#1e3a5f` blue belonging to no other part of the
   product, stacked directly above the equally dark hero. It is now a white card with a gold top
   rail, on brand and in the correct weight for the first thing a new user is asked to do.
6. **The log page fit nine entries on a 1000px screen.** Each was a bordered card with a 190px title
   column that wrapped every title. Now one framed list with rules between rows and monospace
   detail: sixteen entries in the same space.
7. **The setup checklist's "done" styling was one-way.** Inline styles were written onto the step
   icons and never removed, so a step stayed ticked after its key was cleared. It is a class now.

### The rest of the pass

- **Emoji icons replaced by an inline SVG sprite** for the sidebar, search, overlay home buttons and
  state blocks. Emoji rendered as OS-coloured pictures at inconsistent weights; these are one stroke
  weight, inherit `currentColor`, and stay crisp at any zoom.
- **Ten flat nav entries grouped** into Sell / Grow / Account, with a gold rail marking the active row.
- **One focus ring across the app** via `:focus-visible` — invisible to mouse users, always there for
  keyboard users. Fields keep their own tighter ring rather than doubling up.
- **Ctrl+K focuses search** (the box advertised the shortcut; now it exists) and Escape clears it.
- **Tabular figures** on money and metrics so columns stop jittering between renders.
- **Sticky table headers** and a sticky activity rail.
- **`prefers-reduced-motion`** honoured; the shimmer stops rather than merely slowing.
- Buttons no longer lift on hover (it shifted layout under the cursor) — depth and brightness instead;
  `.btn.is-busy` gives any button a spinner without markup changes.
- Responsive breakpoints at 1180 / 900 / 640px: both fixed grids now have a way down, which the
  installed app needed since its window opens smaller than a design mock.
- Loose sections wrapped in the panel their neighbours use — the Auction Sniper search, the Inventory
  Health controls and the Photo Library create-row were each floating on the page background.

### Files

| File | Change |
|---|---|
| `wwwroot/style.css` | Token layer, alias repair, and a new `Design system` section: `.state*`, `.skeleton*`, `.setup-*`. Sidebar, topbar, buttons, badges, stat cards, panels, cards, tables, logs, inputs, modals and overlay chrome refitted onto the tokens |
| `wwwroot/index.html` | SVG sprite (15 symbols), grouped nav, SVG search / gear / home icons, setup checklist rebuilt on classes, `#listings-state`, activity and Inventory Health empty states, panel wrappers. `app.js?v=34`, `style.css?v=29` |
| `wwwroot/app.js` | `stateBlockHtml` / `renderState` / `skeletonCardsHtml` / `skeletonRowsHtml` / `setListingsFeedback` / `clearListingsState` / `markSetupStep`; states and skeletons wired into listings, logs, activity and the photo library; Ctrl+K and Escape; table view drops the activity rail; watch badge moved onto the card photo |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **342 passed**, 0 failed, 0 skipped — unchanged from baseline, as expected for a frontend-only change |
| Real browser (Playwright, live account, dev ports 9391–9396) | Dashboard, listings cards, table view, no-match empty state, Opportunity Finder, Inventory Health, Photo Library, Logs, Settings and License all rendered and screenshotted against **87 real listings** |
| Ctrl+K | Focuses `#global-search` |
| Browser console errors | **None**, on every screen visited |

**Not verified:** the AI Listing overlay's internals and the reprice confirmation dialog were not
driven to completion — both need a live analysis run or a live write. Their shared chrome (modals,
buttons, fields, panels) is covered by the token pass; their own layouts were not individually
re-checked.

---

## 15. Local Deals that can't dead-end (autonomous session, 2026-07-26)

The Local Deals panel had one failure mode that made the whole feature look broken rather than
merely unlucky: a scan could end at `Failed to fetch`. No results, no reason, no way forward — and
the seller had usually waited a minute or two to get there.

The cause was a chain of unguarded links, and every one of them is a link this feature genuinely
has to cross:

* `CraigslistService` identified itself honestly (`ING-AutoLister/1.0 …`), which is the request
  shape craigslist refuses first. And craigslist refuses with a **200 and a block page**, not a
  status code — so the block parsed to zero listings and was reported as *an empty local market*.
  A wrong answer that looks like an answer, which is the worst kind this app can give.
* A source that threw anything outside `HttpRequestException` — a DNS failure, a TLS error, a
  `NullReferenceException` in a parser — escaped to the HTTP edge, where ASP.NET answered 500 with
  an HTML body. `fetch(...).then(r => r.json())` rejects on that, and every call in the panel was
  written exactly that way.
* A disconnected Facebook still launched the headless browser and spent its full budget to
  discover what `IsAvailable` already knew.
* Nothing bounded a source in wall-clock time, so a Node child process that hung held the response
  open until the browser gave up on its own — with nothing to show for the wait.

This session made a dead end structurally impossible, on both sides of the wire, and left the
happy path exactly as it was.

### `LocalSupplyGuard` — one choke point, and a result in every case

Every source is now searched through `Services/LocalSupplyGuard.cs`, which returns a
`LocalSupplySearchResult` in all circumstances and never throws:

| Source behaviour | What comes back |
|---|---|
| Needs a login it doesn't have | `not_connected` **before any work starts**, with the sentence and the reason |
| Throws — sync, async, or wrapped in an `AggregateException` | `error` naming the site, carrying the innermost message |
| Hangs, ignoring its cancellation token | `error` after the budget, enforced by `Task.WhenAny` on the clock |
| Returns null, a blank status, or an unknown one | normalised to `error`; missing labels/query/radius filled in |
| Returns `ok` with no listings | left alone — an empty local market is a real answer |

Budgets are per kind of source: 45s for a public one (an HTTP call), 3 minutes for a session-based
one (a real browser loading a page and scrolling a grid). One timeout for both would either cut
Facebook off or let Craigslist stall.

The single exception that still propagates is the caller's own cancellation. The browser has hung
up; there is nothing left to render for, and pricing results nobody will see is work for nobody.

### Craigslist: asking the way a browser asks, and noticing when it's refused

* **Browser headers** — a real Chrome `User-Agent`, `Accept`, `Accept-Language`,
  `Upgrade-Insecure-Requests` and the `Sec-Fetch-*` set. Deliberately **no `Accept-Encoding`**:
  this client isn't configured to decompress, and advertising gzip would hand the parser a binary
  blob that reads as zero results — the silent wrong answer again.
* **`CraigslistParser.DetectBlock`** reads the body, not just the status. It recognises craigslist's
  own block page and the interstitials served in front of it, and only scans the first 4,000
  characters — a real results page is hundreds of kilobytes, and somebody's ad genuinely does say
  "verify you are a human".
* **Two timeouts**: 15s per request, 35s for the whole search, so the results page plus the RSS
  fallback can't add up to twice what one request is allowed.
* **Every status distinguished**: 403/429 → rate-limited, 5xx → craigslist's end, 404 → wrong site.
  A new `Retryable` flag separates "come back in a minute" from "this will fail identically", and
  only the former gets a Retry button.

### The endpoints always answer 200 with a valid body

`/api/local/search`, `/api/local/arbitrage`, `/api/facebook/arbitrage`, `/api/craigslist/search`,
`/api/facebook/search` and `/api/local/sources` each return a renderable JSON body no matter what
fails inside them — including partial results from the sources that did answer.

`FindLocalArbitrageAsync` gained a second guard around its **pricing half** specifically. The
search and the sold-comp pricing are separate halves of that endpoint, and when pricing breaks the
seller still gets the local listings that were found plus a sentence saying pricing is what failed
— the status stays `ok`, because the sites really did answer.

### Frontend: partial results, per-source status, and never a raw rejection

`localFetchJson` replaces every `fetch(...).then(r => r.json())` in the panel. It always resolves to
`{ data, error }` — it handles non-OK responses, unparseable bodies, a dropped connection, and its
own `AbortController` ceiling (4 min for a search, 8 for a scan), so a request that will never come
back stops looking like one that's still working.

Per-source chips now read as status rather than as counts, and each chip that describes a fixable
state carries the button that fixes it:

| State | Chip | Action |
|---|---|---|
| Answered | `Craigslist 58 results · Las Vegas craigslist (NV)` | open ↗ |
| Answered, nothing found | `Craigslist no results` | — |
| Needs a login | `Facebook Marketplace connect required` | **Connect** |
| Session gone | `Facebook Marketplace session expired` | **Connect** |
| Blocked / timed out | `Craigslist blocked — retry` | **Retry** |
| Failed for good | `Craigslist unavailable` | — |

And `partialLocalNote` says out loud what a bare count hides: *"Showing Craigslist only — Facebook
Marketplace needs connecting."* The ranked table underneath is the full Craigslist ranking; one
site being unavailable no longer costs the seller the other site's results.

### Files

| File | Change |
|---|---|
| `Services/LocalSupplyGuard.cs` | **New.** Per-source timeout, total exception containment, not-connected short-circuit, result normalisation |
| `Services/CraigslistService.cs` | Browser headers, per-request + per-search timeouts, block-page handling, status-specific messages, `Retryable` |
| `Services/CraigslistParser.cs` | **New** `DetectBlock` — block pages and interstitials served with a 200 |
| `Services/FacebookMarketplaceService.cs` | Real messages on `not_connected` / `session_expired`, `Retryable` on timeouts and launch failures |
| `Models/LocalSupplyModels.cs` | `Retryable` on `LocalSupplySearchResult` and `LocalSupplySourceOutcome` |
| `Program.cs` | All six local endpoints wrapped; `SearchLocalSourceAsync` routed through the guard; arbitrage pricing guarded separately so a pricing failure still returns the listings |
| `wwwroot/app.js` | `localFetchJson`, `setLocalStatus`, `partialLocalNote`; chips rebuilt with Connect/Retry; source picker gets its own retry. `?v=35` |
| `wwwroot/style.css` | `.local-chip-btn`, `.local-retry-btn`, `.local-sources-retry`. `?v=30` |
| Tests | **New** `LocalSupplyGuardTests` (13 cases); 5 `DetectBlock` cases added to `CraigslistParserTests` |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **360 passed**, 0 failed, 0 skipped (was 342; +18 new) |
| `node --check app.js` | Clean |
| Live `/api/local/search`, Facebook only (disconnected) | 200 in **0s** — was a full browser launch — `not_connected` + "Connect Facebook Marketplace first." |
| Live `/api/local/search`, Craigslist only | 200 in 1s, **58 real listings** off the live site with the new headers |
| Live `/api/local/search`, both sources | 200, **58 Craigslist listings returned alongside** Facebook's connect-required state |
| Live `/api/local/arbitrage`, both sources | 200 in 5s: 58 found, 24 products priced, **30 ranked rows**, Facebook reported not connected |
| Edge cases: empty query, junk zip, unknown source name | All 200 with a valid body and an actionable message; none reached an exception |
| Real browser (Playwright, dev port 9397) | Status line *"Showing Craigslist only — Facebook Marketplace needs connecting."*, both chips correct, **Connect** button present, 30 rows rendered, **no console errors** |
| The original bug, observed live | With the server stopped mid-scan the panel rendered *"The local scan didn't complete. Couldn't reach the app (Failed to fetch). Check it's still running, then try again."* **+ a Try again button** — instead of dead-ending |

**Not verified:** the blocked-Craigslist path was exercised only through `DetectBlock`'s unit tests
and the 403/429 branches — craigslist did not actually block this machine during the session, so
the end-to-end "blocked — retry" chip was not seen against the live site. The Facebook search path
past the connect gate is also unverified here: no account is connected on this machine, which is
exactly the state the new short-circuit answers.

---

## Onboarding checklist: an eBay-only refresh no longer wipes the "AI key saved" tick

### The bug

`updateSetupChecklist(hasAiKey, hasEbay, hasOpenAi)` in `wwwroot/app.js` treated a
missing argument as **false** for steps 1 and 3:

```js
const step1Done = hasAiKey !== null && hasAiKey !== undefined ? hasAiKey : false;
```

`updateAuthUI()` — the eBay-only refresh — calls `updateSetupChecklist(null, connected, null)`,
because it knows the eBay state and nothing about the keys. So every auth refresh
re-rendered step 1 as *not done*: the green tick was replaced by the number, the
button went back to "Enter Key →", and the whole "2 steps to activate" checklist
un-hid itself — on a machine with the Anthropic key saved and setup complete.
Step 2 never had this problem; it already fell back to `isConnected`.

At startup this is a race between two calls that both land on the checklist:
`checkSetupOnLoad()`'s full refresh (line ~2405) and `updateAuthUI(!!tokenStatus.hasToken)`
from the token-status poll (line 48). On this machine the auth poll won every time,
so the tick was wiped on every page load.

### The fix

An omitted argument now **preserves that step's current state** rather than
collapsing it to false, mirroring step 2's existing fallback. The state is read
back off the row, which is where `markSetupStep` records it — `.setup-step.is-done`
is set only there, so the class is a faithful record of the last known state.

```js
const step1Done = hasAiKey !== null && hasAiKey !== undefined ? !!hasAiKey : isSetupStepDone('step1');
const step2Done = hasEbay  !== null && hasEbay  !== undefined ? !!hasEbay  : isConnected;
const step3Done = hasOpenAi !== null && hasOpenAi !== undefined ? !!hasOpenAi : isSetupStepDone('step3');
```

plus a small `isSetupStepDone(prefix)` helper. Explicit `false` still clears a
step, so the one-way-tick problem the original comment warns about does not come back.

### Files touched

| File | Change |
|---|---|
| `wwwroot/app.js` | `updateSetupChecklist` preserves omitted steps; new `isSetupStepDone` helper. `?v=36` |
| `wwwroot/index.html` | `app.js?v=35` → `?v=36` |

### Verification

Live, against the real app on dev port 9398 (`AUTOLISTER_DEV_PORT`), driven by
Playwright. `/api/setup/status` reported `hasAnthropicKey: true`, `isComplete: true`,
and eBay was connected — i.e. exactly the state the bug ruined.

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **360 passed**, 0 failed, 0 skipped |
| `node --check app.js` | Clean |
| **Before the fix** (fix stashed, app rebuilt and re-run) | `step1Done: false`, button `"Enter Key →"`, `checklistHidden: false` — **bug reproduced**, on load and after an auth refresh |
| **After the fix** | `step1Done: true`, button `"✓ Key saved"`, `step2Done: true`, `checklistHidden: true` — on load and after a token-status refresh + reload |
| Console errors | None, either build |

**Not verified:** the not-yet-configured path (no Anthropic key saved) was not exercised
live — this machine has the key saved, and that state is what the bug was about. Its
behaviour is unchanged by this fix: `checkSetupOnLoad` passes an explicit `false`, which
still marks the step pending. Step 3 (OpenAI) has no key saved here, so its preserved-state
branch was only observed staying correctly *un*-ticked.

---

## 16. Listing Readiness — what eBay requires, before you press Publish (autonomous session, 2026-07-26)

The core workflow is item → live listing, and it had one dominant piece of friction: **eBay does
not tell a seller what a category requires until after the publish fails.**

The app's Publish button checked two things — a title, and a price above zero — and handed the rest
to eBay, which answers minutes later with `The item specific Model is missing`. The only handling
for that was reactive: `nlHighlightMissingSpecifics` parsed the *failure text* and highlighted a row
after the fact. Meanwhile the Item Specifics panel was a stack of blank name/value rows, so a
seller filling it in was guessing at both halves of every pair.

The Taxonomy API has known the answer the whole time. Nothing in the app was asking it.

`POST /api/listing/readiness` and `GET /api/ebay/category-aspects` → the **Item Specifics** panel and
the readiness bar above Publish.

### Why this makes the seller money

1. **It removes the failed-publish round trip.** A rejected publish costs the listing, the wait and
   the re-read of a raw API error. Every required aspect is now a labelled field with a red
   Required pill, on screen while the listing is being written.
2. **It fills them in, from the seller's own words.** eBay publishes the *legal values* per aspect.
   The seller has already written a title and a description. So most answers are already in the
   listing and can be found rather than typed. On a real iPhone draft, one click filled **Brand,
   Model, Storage Capacity, Lock Status and Network** — five specifics, none typed.
3. **The empty ones are the quiet leak.** eBay's search filters *run on* Item Specifics. A blank
   "Storage Capacity" doesn't look bad — it removes the listing from every buyer who filtered by
   storage, which is most of them. Those are reported as warnings with that sentence attached,
   because a checklist without the "why" gets clicked past.
4. **It scores the rest of the listing too** — title length, photo count, product identifiers,
   description — with each finding naming what it costs.

Vendoo, List Perfectly and Crosslist move listings between sites and never check a category's
requirements. eBay's own Seller Hub only tells you after you fail.

### Blockers and warnings are not the same thing, and never blur

| | Meaning | Behaviour |
|---|---|---|
| **Blocker** | eBay's rule. The listing cannot go live. | Stops Publish **once**, lists every one, offers **Publish anyway** |
| **Warning** | The app's opinion about what sells. | Never stops anything |

The override matters: the app can be wrong about a category, and it is the seller's account. Drafts
skip the gate entirely — an unfinished draft is the point of a draft.

### Matching: the part that has to refuse

`AspectMatcher` is pure and has three jobs, separated because each is independently wrong-able.

**Names.** "GPU Model" and eBay's "Chipset/GPU Model" are the same field; treating them as different
publishes a listing with a required specific "missing" while the value sits right there. Exact match
first, then an alias table, then token overlap ≥ 0.6 — and every step after the first insists the
answer is *unique*:

- An alias only fires when it lands on **exactly one** aspect the category actually has. "Capacity"
  means Storage Capacity in one category and Battery Capacity in another; where both exist, no call
  is made.
- **"Compatible Brand" is not "Brand"** (Jaccard 0.5, below the bar). A phone case whose compatible
  brand is Apple is not an Apple-branded product — the most damaging near-miss in the feature.

**Values.** On a `SELECTION_ONLY` aspect eBay rejects anything outside its published list, so
"bitmain" → `Bitmain`, "wall-mount" → `Wall Mount`, "N/A" → `Does Not Apply` are lookups, not
guesses. On a `FREE_TEXT` aspect eBay's values are *popular suggestions*, not the whole set, so a
seller's real value is accepted — rejecting it would be the app inventing a problem eBay doesn't have.

**Inference.** Where eBay named the legal values, look for one of them in the seller's own text.
Two matches mean two different things, and the difference decides everything:

- *Refinement* — "S19" and "S19 Pro" both hit on "Antminer S19 Pro". Longest wins; taking the
  shorter lists a $2,000 machine as a $700 one.
- *Ambiguity* — "Red" and "Blue" both hit on "Red and Blue pair". Neither refines the other, so the
  answer is **null**. Picking the longer string would be deciding a colour on its spelling.

Matching is whole-token, or "Red" matches "Prepared". Descriptions are stripped of HTML first, or
"Table" matches `<table>`.

### What it refuses to answer at all

- **Country of Origin / Country of Manufacture.** Guessing it from a brand puts a false legal claim
  on a live listing. Left blank however required it is.
- **A required MPN/UPC with no number** is offered as the literal `Does Not Apply` — eBay's own
  answer, which passes where blank fails.
- **A `SELECTION_ONLY` aspect whose list doesn't contain the seller's value** gets no suggestion,
  because offering it back produces a publish failure.
- **Low-confidence suggestions are never auto-applied.** They sit on the field as an offer with
  their source stated ("found in your title", "from the Brand field"). A one-click button that
  writes something uncertain into a live listing makes the seller's data worse.
- **Nothing is ever written without the seller applying it**, and an existing value is never
  overwritten — except one eBay would reject outright, where keeping it means a failed publish.

### Three defects the real-inventory runs caught

Driven against the connected account and live eBay categories, not reasoned about:

1. **A junk Model, offered at medium confidence.** `ProductIdentityExtractor.Model` is "whatever
   words are left after every known field is claimed", which on a real title reads
   **"S19 Pro ASIC with PSU"** — leftover prose. `Fill from my listing` would have written that into
   a listing. Identity-derived values are now refused above 30 characters or 3 words.
2. **The readiness bar broke the modal footer.** `.new-listing-footer` was one flex row of
   `[message | buttons]`; adding the bar as a third item made a very tall row that overlapped the
   scrollable form above it and **swallowed clicks on the bottom of the form**. The row now lives in
   `.nl-footer-row` inside a column, and the footer is capped at `52vh`.
3. **The blocker gate froze.** It snapshots the blockers at the moment Publish is pressed, so after
   the autofill cut them 5 → 2 it still read "5 things need fixing", directly under a bar saying
   otherwise. It now tracks each readiness pass, and clears itself when the last blocker goes.

Plus: a non-leaf category returns eBay's `errorId 62009`, which was surfacing as "HTTP 400". eBay
refuses the *publish* for the same reason, so it is now reported as the real blocker it is —
"Category 175673 is a parent category… pick one further down the tree."

### Rationing and degradation

- Aspects are **cached 12 hours per category** — a seller listing a dozen items in one category
  would otherwise repeat the same call a dozen times.
- Readiness is **debounced 600ms** while typing, with a sequence guard so a stale response can never
  overwrite a newer one. A category change re-checks immediately, since it changes which specifics
  exist at all.
- **eBay not connected, an error, or no category yet** — the rest of the listing is still scored and
  the headline says which half wasn't checked ("Nothing here blocks a publish — but eBay's required
  specifics weren't checked"). Reporting "ready" on an unchecked listing is this feature's most
  dangerous failure, and it is a test.
- A readiness request that fails leaves publishing entirely alone: "Couldn't reach the app for the
  pre-publish check — publishing still works."

### Files

| File | Change |
|---|---|
| `Models/ListingReadinessModels.cs` | **New** — `CategoryAspect`, `AspectField`, `AspectState`, `ReadinessFix`, `FixSeverity`, `ListingReadinessResult`, `ReadinessRequest` |
| `Services/AspectMatcher.cs` | **New** — name matching, value canonicalisation, inference, `Evaluate`, `AutoFillable`, `StripHtml`. Pure |
| `Services/ListingReadinessAnalyzer.cs` | **New** — score, grade, headline, ordered fix list, `AspectFieldId`. Pure |
| `Services/EbayService.cs` | `GetCategoryAspectsAsync` (Taxonomy `get_item_aspects_for_category`, 12h cache), `ParseAspects`, `DescribeAspectFailure` |
| `Program.cs` | `GET /api/ebay/category-aspects`, `POST /api/listing/readiness` |
| `wwwroot/index.html` | Item Specifics panel rebuilt (status, autofill, required/recommended/optional groups, custom rows); readiness bar in the footer; `.nl-footer-row`. `app.js?v=39`, `style.css?v=33` |
| `wwwroot/app.js` | `initListingReadiness`, `nlRunReadiness`, `nlRenderAspects`, `nlAspectRow`, `nlAutofillAspects`, `nlRenderReadiness`, `nlFocusField`, `nlBlockersStopPublish`, `nlRenderBlockerGate`, `nlSyncBlockerGate`, `nlCollectAspectValues`, `nlAspectFieldId`; `nlCollectSpecifics` merges aspect fields; `bindCategorySearch` announces its hidden input |
| `wwwroot/style.css` | `.asp-*`, `.rd-*`, footer split into a column |
| `ING eBay AutoLister.Tests/AspectMatcherTests.cs` | **New** — 40 tests |
| `ING eBay AutoLister.Tests/ListingReadinessAnalyzerTests.cs` | **New** — 24 tests |
| `ING eBay AutoLister.Tests/CategoryAspectParsingTests.cs` | **New** — 10 tests |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **445 passed**, 0 failed, 0 skipped (was 360; +85 new) |
| `node --check app.js` | Clean |
| Live eBay, category 179171 (Miners) | 10 real aspects parsed; required/recommended/optional, `SELECTION_ONLY` and `MULTI` flags all read correctly |
| Live eBay, category 9355 (Cell Phones) | 31 aspects, **4 genuinely required**; a draft with `Manufacturer`/`Colour` matched them onto eBay's `Brand`/`Color`, and the two real gaps came back as blockers |
| Live inference, real draft | Bitcoin, SHA-256, ASIC and Bitmain all found in the seller's own title against eBay's value lists |
| Non-leaf category (175673) | Reported as a parent category with what to do, not as HTTP 400 |
| Real browser (Playwright, live account, dev ports 9401–9404) | Bar scores a blank form (`0 · Won't publish · 4 things will stop this publishing`); category pick loads 31 aspects into 4/18/9 rows with the `4 required missing` badge; 29-row fix list; **Go to it** focuses the right field; Publish gate lists all 5 blockers with **Publish anyway**; **Fill 5 from my listing** wrote `Brand=Apple, Model=Apple iPhone 13 Pro, Storage Capacity=256GB, Lock Status=Factory Unlocked, Network=Unlocked`; gate tracked 5 → 2 → cleared; score 0 → 36 → 60 |
| Browser console errors | **None**, across every step |
| Anything published to eBay | **Nothing.** No publish was executed; the gate was exercised, never passed through to a live write |

**Not verified:** the not-connected degradation path was not exercised live — this machine has eBay
connected, which is the state the feature is *for*. Its behaviour is covered by unit test
(`A_listing_that_was_not_checked_against_eBay_does_not_claim_it_was`) and by the same
`store.GetUserToken()` check every other endpoint uses. Also unverified end-to-end: that a listing
scoring "Ready to publish" then actually publishes — that is a real, buyer-visible write on the
seller's own account and is theirs to make.

**Known limitation, stated in the UI rather than hidden:** the aspect check is only as current as
eBay's Taxonomy API, cached 12 hours. A category whose requirements change inside that window is
checked against the previous set — **Recheck** forces a fresh look.

---

## 17. The login window that was never hidden — it was just behind you (autonomous session, 2026-07-26)

**The bug, precisely:** clicking Connect for Terapeak or Facebook Marketplace worked. node ran,
Playwright launched Chrome, the login page loaded, `loginInProgress` stayed true. And the seller saw
a status that said "connecting" and nothing else, because the window had opened **behind the app**.
Nothing was broken. The window was buried, and the feature read as dead.

### Why one raise wasn't enough

The old code raised the window once at launch and once after navigation. Both attempts land in the
first second of Chrome's life — the exact window in which Chrome is still doing its own startup
placement, the app's own window can take focus back, and Windows' focus-stealing rules are at their
least cooperative. Losing that race once is enough to bury the window for the whole six-minute wait.

Three cooperating nudges now, none of them alone sufficient:

| Layer | What it does |
|---|---|
| `LoginWindowFocus.Grant()` | The existing `AllowSetForegroundWindow(ASFW_ANY)` grant — **kept**. Without it the spawned Chrome may not legally raise itself at all |
| `LoginWindowFocus.PinNewBrowserWindowBriefly()` | **New.** Watches for the Chrome process started after the click, pins its window topmost + foreground, releases it after 5s |
| `NodeRuntime.RaiseToFrontJs` | The in-browser side: hard `minimize`→`normal` CDP cycles at 0/400/1200/2400/4000ms, `bringToFront()` every 250ms in between, then **stop** |

### The part that is deliberately restrained

All three are short-lived by design, and that is the whole trade-off. A window that keeps forcing
itself forward eats the keystrokes of the password it is asking for and interrupts a CAPTCHA mid-
attempt. So the attention-grabbing is a **startup behaviour only** — it happens in the first ~5
seconds, before anyone is typing. The 6-minute wait loops that follow were left as they were: a bare
`raise(false)` (tab focus, no window movement) roughly every 8 seconds, which does not disturb typing.
The topmost pin also always releases in a `finally`, so a login window can never end up permanently
stuck above the user's own windows.

The pin only ever touches Chrome processes whose `StartTime` is later than the Connect click — the
seller's own existing Chrome windows are left alone.

### And it says where to look

Raising a window is best-effort on Windows, so the status text no longer pretends it's guaranteed.
Both banners and both service messages now name the fallback: *"Don't see it? Alt+Tab, or check the
taskbar for the login window."* That sentence is the difference between a seller waiting on a status
they think is stuck and a seller finding the window that is already open.

### Files

| File | Change |
|---|---|
| `Services/LoginWindowFocus.cs` | **New** — `Grant`, `PinNewBrowserWindowBriefly`, new-Chrome-window detection, always-released topmost pin |
| `Services/NodeRuntime.cs` | **New** `RaiseToFrontJs` — `raise(hard)` / `raiseBurst()`, shared verbatim by both login scripts (was duplicated, and drifting) |
| `Services/TerapeakService.cs` | Embeds the shared snippet, fires `raiseBurst()` alongside the page load, uses `LoginWindowFocus` for the grant + pin; own `DllImport` removed; Alt+Tab hint in `StartLogin` |
| `Services/FacebookMarketplaceService.cs` | Same, via a `%%RAISE%%` placeholder in `LoginScript` |
| `wwwroot/app.js` | Alt+Tab / taskbar hint in the Terapeak banner, the Facebook banner and the sold-comps Connect prompt |
| `wwwroot/index.html` | `app.js?v=40` |
| `ING eBay AutoLister.Tests/NodeRuntimeRaiseScriptTests.cs` | **New** — 4 tests: both helpers defined, the hard cycle is the minimize/normal one, the burst is bounded to a few seconds, every scheduled hard raise lands inside it |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **451 passed**, 0 failed, 0 skipped (was 445; +6) |
| `node --check` on the rendered Facebook login script and on the shared raise snippet | Clean — run through a temporary test that renders the real embedded scripts, then removed so the suite does not require Node installed |

**Not verified live:** the window actually coming to the front. That needs a real Terapeak/Facebook
login on a machine with someone watching the screen, and it is inherently timing-dependent — the
whole reason for three layers is that no single one is reliable. What is verified is that the shipped
JavaScript parses, that the burst is bounded (a test fails if anyone extends it into the typing
window), and that the topmost pin is released on every path.

## 18. Bulletproof reliability — no silent failures, no lost work, no duplicate listings (autonomous session, 2026-07-26)

An audit of the four critical paths — research, pricing, photos, publish — looking for failures the
seller either never sees or cannot recover from. It found three structural holes, each of which cost
real money, and this session closed all three.

### The three holes

**1. An unhandled exception was the normal outcome, not the exceptional one.** `/api/analyze` — the
main AI listing path — had **no try/catch at all**. Neither did `/api/photos/save-uploaded` or
`/api/listing/update` (a live price revision the seller had just explicitly confirmed). An Anthropic
rate limit, an expired eBay token, or a truncated image paste therefore reached ASP.NET's edge, which
answered **500 with an HTML error page** — and every caller in `app.js` did `await res.text()` on the
failure path, so the seller's error message was a fragment of HTML or a stack trace.

**2. A single transient blip destroyed paid-for work.** There were **zero retries** anywhere on the
AI path. Anthropic answering `529 overloaded` — the most common real failure on that path, and the one
that clears fastest — discarded a listing analysis the seller had waited minutes and real API spend
for, and told them `529`.

**3. A publish that failed might have succeeded.** `AddFixedPriceItem` is not idempotent, and a
timeout or a lost response does **not** mean eBay declined to create the listing. The seller pressed
Publish again and got **two live listings for one physical item**: two insertion fees, two audiences,
and an oversell the moment one sold. Nothing anywhere prevented this.

Plus the quiet one: a listing in progress existed **only in the DOM**. One accidental tab close, one
refresh, one crash, and an AI-written title, description and item specifics were gone with no trace.

### What each critical path now guarantees

| Path | Before | After |
|---|---|---|
| **Research** (`/api/sold-comps`) | A Terapeak scrape that threw — missing Node, crashed browser — escaped to a 500 | Guarded; a `source: none` answer now carries a `dataNote` saying **which source failed**, because "no sold comps" and "the lookup broke" are opposite facts and looked identical |
| **Pricing** | Same 500, and a failed lookup was indistinguishable from a genuinely empty market | Same; no price is ever applied on failure, and the panel says so |
| **Photos** | `catch { /* non-fatal */ }` on every save. It is not non-fatal: the listing published **with no photograph** and nobody was told | Every photo failure is reported. A publish where **all** photo uploads failed now **stops and asks**, instead of a 4-second status line that auto-hid |
| **Publish** | One unguarded round trip; a lost response invited a duplicate | Three independent brakes, then reconciliation against the live account |

### The publish path: three brakes, then look before you speak

`PublishGuard` (new) sits in front of every publish:

1. **An in-flight lease on the content fingerprint** — catches the double-click and the impatient
   second press, with no storage round trip. Self-releasing after 5 minutes so a crashed request can
   never lock publishing permanently.
2. **A recent-publish record** in `WorkRecoveryStore` — catches a retry after the first attempt
   already succeeded, *including one made after the app restarted*.
3. **Reconciliation** — on a timeout, network drop, or eBay 5xx (and **only** those; an eBay
   rejection is a definite no, and looking anyway would risk matching an older listing), the app
   queries the seller's active listings **before it says anything**. If the listing is there, it
   reports success with the real listing ID and says the confirmation was simply lost.

Where the app still cannot be sure, it offers **Check eBay** rather than **Try again** — a retry
there is precisely what creates the duplicate. `/api/listing/check-published` is read-only.

The fingerprint deliberately **excludes photo URLs**: the publish path uploads photos to eBay first,
so the same listing legitimately carries different URLs on a second attempt, and folding them in
would make every retry look like new content and defeat the whole guard. `AddFixedPriceItem`'s
timeout was also **raised** to 3 minutes — the one place a longer timeout is the safer choice, since
giving up does not cancel it at eBay's end.

### Never losing work

`WorkRecoveryStore` (new SQLite table) keeps the listing being written alive outside the browser tab.
Autosave is debounced 2.5s, flushed on `pagehide`/`beforeunload` via `sendBeacon` (a `fetch` is
cancelled when the document goes away), and bounded — oversized payloads refused, repeated saves of
one draft overwrite rather than accumulate, published records pruned.

Server-side rather than `localStorage` for two reasons: it survives a cleared cache, a different
browser and the app restarting mid-publish, and it puts the recovery record in the same place as the
publish journal — which is what lets the guard answer "did this already go live?" after a restart.

A row still marked `publishing` means the app went down between sending the listing and hearing back.
That row is **shown**, flagged `publish outcome unknown`, with a **Check eBay** button — rather than
hidden on the assumption it worked.

### Retries that are honest about what is worth retrying

`ResilientCall` gives every AI call three attempts with exponential backoff and jitter, honouring a
server's `Retry-After` whenever it asks for longer, capped at 30 seconds because a seller is sitting
in front of it. It retries **only** kinds `FailureTranslator.IsTransient` agrees are transient — a
rejected API key fails immediately and says what to fix, rather than making the seller wait out three
attempts first.

The JSON parse runs **inside** the retried block, deliberately: a truncated or prose-wrapped reply is
a bad sample, not a bad request, and a fresh sample almost always parses.

Nothing retries a write to eBay. That path uses `PublishGuard` instead.

### One way to describe a failure

`FailureTranslator` (new, pure, 60 tests) turns any exception into a headline, what happened, what to
do, whether a retry is honest, and the raw text kept as evidence. `renderFailure` in `app.js` renders
all of it the same way everywhere, with the button that resolves it — **Open Settings**, **Log into
eBay**, **Open Logs**, **Check eBay** — and the technical detail folded away rather than used as the
headline.

`retryable` is the load-bearing field. A Retry button on a permanent failure teaches sellers to click
Retry on everything; no Retry on a transient one throws away work that would have succeeded. Both
cost money, so the distinction is decided once, in one place.

Classification is **domain-scoped**, which prevents a real defect: `Contains("429")` would read eBay's
own rejection text — which routinely quotes prices — as a rate limit, and tell a seller to wait when
what they need to do is fix the listing. `MentionsHttpStatus` requires the number to actually be used
as a status, and there is a test for `"Item price 429.00 exceeds the maximum"`.

### Five defects the live runs caught

Every one came from pointing the running app at the real thing, not from reading the code:

1. **Anthropic's `invalid_request_error` was reported as a network failure.** A real `/api/analyze`
   call on a file that was not an image came back "Could not reach Anthropic — check your connection"
   after **three retries**. The SDK raises it as an `HttpRequestException`, so it fell into the
   network branch. Anthropic had answered instantly and correctly; it was the input that could never
   work. Now `BadInput`, not retryable, **1 attempt instead of 3** (0.8s instead of ~5s), and it
   surfaces Anthropic's own sentence: *"Anthropic rejected it: Could not process image."*
2. **eBay's JSON error body was pasted at the seller as the explanation.** The prefix strip only
   matched a single-word call name, so `Inventory item failed (HTTP 400): {"errors":[...]}` kept its
   prefix, the JSON check never fired, and the whole payload became the message. Now multi-word names
   are stripped and eBay's own `longMessage` is extracted — verified live against the real API.
3. **The failure panel rendered where nobody could read it.** Measured in a browser at 950px tall:
   the panel was at y=989 in a 950px viewport, **behind the sticky footer**. Adding `scrollIntoView`
   then exposed the real cause — it was inside the left column, which is a **115px scroller holding
   864px of content**, so a 240px panel showed a ~115px slice with its headline hidden behind the
   strip above. Moved to full width above the two-column body.
4. **`quick-fill` finding no product photo was a log line the seller never saw**, leaving them a
   complete listing with an empty photo grid. Now a visible notice that does not auto-hide.
5. **A second publish had three routes past the disabled buttons** — the draft path wires up its own
   "Publish to eBay Live" button, the readiness gate can re-enter, and Enter submits the form. Now a
   module-level in-flight lock as well.

### Files

| File | Change |
|---|---|
| `Models/FailureModels.cs` | **New** — `FailureDomain`, `FailureKind`, `FailureInfo`, `AppFailureException` |
| `Services/FailureTranslator.cs` | **New** — exception to actionable failure. Pure, domain-scoped, string-driven so an unrecognised error degrades gracefully instead of falling through to a 500 |
| `Services/ResilientCall.cs` | **New** — bounded retries, backoff + jitter, `Retry-After`, transient-only |
| `Services/PublishGuard.cs` | **New** — content fingerprint, in-flight lease, duplicate window, reconciliation match |
| `Services/WorkRecoveryStore.cs` | **New** — autosave + publish journal in the app's own SQLite database |
| `Services/ClaudeService.cs` | All six model calls funnel through one `CallModelAsync` — retries, a 4-minute wall-clock bound, and the parse inside the retried block |
| `Services/EbayService.cs` | `AddFixedPriceItem` timeout raised to 3 minutes, with the reasoning recorded |
| `Models/ListingData.cs` | `PostListingRequest.WorkKey`; `WorkAutosaveRequest`, `WorkDiscardRequest` |
| `Program.cs` | `FailureJson` / `BadInputJson` / `Guarded` helpers; guarded `analyze`, `analyze-url`, `improve-seo`, `ai-modify`, `quick-fill`, `sold-comps`, `photos/*`, `listing/post`, `listing/publish`, `listing/update`, `local-listings/save-edit`; new `listing/check-published`, `work/autosave`, `work/recoverable`, `work/discard` |
| `wwwroot/app.js` | `callApi` (always resolves, with timeouts), `renderFailure`/`hideFailure`, autosave + recovery banner, `publishInFlight` lock, `nlCheckPublished`, `nlPhotoNotice`, photoless-publish gate. `?v=41` |
| `wwwroot/index.html` | Recovery banner, full-width `#nl-failure`. `app.js?v=41`, `style.css?v=34` |
| `wwwroot/style.css` | `.failure-*`, `.recovery-*`, `.nl-photo-upload-status.warn`, `.nl-result-note`. `?v=34` |
| Tests | **New** `FailureTranslatorTests` (60), `ResilientCallTests` (17), `PublishGuardTests` (22), `WorkRecoveryStoreTests` (21) |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **561 passed**, 0 failed, 0 skipped (was 451; +110 new) |
| `node --check app.js` | Clean |
| Live `/api/analyze`, no image | 400 with a renderable body — was `Results.BadRequest("ImageBase64 is required")` |
| Live `/api/analyze`, non-image file | `BadInput`, 1 attempt, 0.8s, Anthropic's own sentence — was `Network`, 3 attempts, "check your connection" |
| Live `/api/listing/update`, confirmed, bad offer | `EbayRejected` with eBay's `longMessage` — **was an unguarded 500 with an HTML body** |
| Live `/api/photos/save-uploaded`, corrupt base64 | `BadInput`, "That image did not arrive intact" — **was an unguarded 500** |
| Live `/api/photos/fetch-url`, dead host | `Network`, retryable |
| Live `/api/sold-comps?q=antminer s19` | Still real data: 50 comps, $167.57 average, $128.75 median |
| Live publish gates | No title → refused locally, nothing sent; price 0 → refused locally |
| Live `/api/listing/check-published` | Read-only against the real 87-listing account; correctly `found: false` for a title that isn't there |
| Live autosave round trip | Save → recover → discard; a 300KB payload refused as `too_large` **without throwing** |
| Real browser (Playwright, live account, dev ports 9411–9415) | Recovery banner renders 2 rows with the `publish outcome unknown` flag and **Check eBay** only on the row that needs it; Restore populates title, price and description; autosave captures a typed edit within 4s; failure panel shows headline / what happened / what to do / attempts / buttons / folded detail; **Try again** re-issues the request (verified by request count); **Open Settings** navigates; every page still renders |
| Browser console | **No JS errors.** The only console entries are the browser's own notes about the HTTP 400 status our failure responses deliberately return |
| Anything published to eBay | **Nothing.** No publish was executed; the gates were exercised, never passed through to a live write |
| Test data | All rows created during verification were deleted afterwards; the app database is untracked by git |

**Not verified:** the duplicate-publish guard against a real duplicate, and reconciliation against a
real lost response. Both need an actual live publish on the seller's account — a buyer-visible,
hard-to-reverse write that is theirs to make. Both are covered by unit tests over the pure logic
(fingerprint stability, lease reclaim, duplicate window, title matching including the "don't mistake
a listing from last month for the one just published" case), and the reconciliation lookup delegates
to the same `GetListingsAsync` the dashboard already exercises against 87 real listings. Also
unverified live: the blocked-Craigslist and Anthropic-529 paths, since neither service failed that way
during the session — both are covered by tests, and the 529 handling is the same code path the live
`invalid_request_error` case exercised end to end.

---

## 19. Roll the Dice — for the seller who doesn't know what to sell (autonomous session, 2026-07-26)

Every money feature in this app until now started with something the seller already had: a keyword for
Local Deals, a price list for the Supplier Analyzer, their own listings for Inventory Health. The
seller who doesn't know **what to look for** — the one who most needs the app — had nothing to press.

**Roll the Dice** is that button. It sweeps whole categories of the sold-comps database, keeps only the
products with a real margin AND real demand, then goes and finds where those products can be bought
today — pricing the flip with the same arithmetic the rest of the app already uses.

### The money impact

For a seller with no idea what to source, the alternative is guessing. A roll turns that into a ranked
board of specific products, each with:

* **net profit after fees on a buy that exists right now** — eBay Buy It Now, plus Craigslist and
  Facebook Marketplace when a zip code is given;
* **the most they can pay** (break-even) and **the price to pay** (`TargetBuyPrice` — the ask that
  clears $75 net at 75% ROI, *the same bar Local Deals already calls a goldmine*);
* **days to cash** and monthly sales velocity, so profit that takes six months to realise doesn't read
  like profit that takes a week; and
* **the evidence** — sold-comp count, confidence level, liquidity — on every single row.

"Roll again" advances the sweep onto categories the last roll didn't touch, so seven rolls (at four
categories a roll) have dug all 28. Every roll is reproducible from the seed printed on it.

### Where the numbers come from — reused, not re-derived

| Step | Reuses |
|---|---|
| Mine sold history per category | `IMarketplaceRepository.SearchByKeywordAsync` — hosted comps API or local SQLite, same matcher either way |
| Price each surviving product | `AnalyzeProductAsync`, i.e. `ProductNormalizer` → `ComparableMatcher` → `MarketPriceEstimator` (**including its identity guard** and the local/Terapeak precision blend) → `SellThroughCalculator` → `ConfidenceScoringService` → `OpportunityScoringService` |
| Ration live Terapeak scrapes | `LocalArbitrageAnalyzer.SelectScrapeTargets`, with a cache-only first pass — a product Terapeak already knows about costs nothing |
| Cost every buy | `LocalArbitrageAnalyzer.Build` → `ProfitCalculator` + `FeeProfile`. An eBay listing is turned into a `LocalSupplyListing` (`JackpotHunter.AsSupplyListing`) precisely so buying on eBay and buying on Craigslist go through one profit path, not two |
| Verdict tiers | `LocalArbitrageAnalyzer.Judge`'s goldmine/solid/thin/pass, mapped to jackpot/strong/thin/pass. Its four thresholds are now `public` so nothing here can invent a friendlier bar |

New code is only the part that genuinely didn't exist: the category sweep and its rotation, and the
clustering, screening, buy-side identity guard and play construction.

### Six guards, because "jackpot" must never mean "hype"

The first live run against the real comps database produced exactly the numbers this feature must never
print: a $21 six-pack of HEPA filters booked as a **$97 flip**, and a $150 robot vacuum priced at
**$504** off a keyword-tier comp match. Both are fixed, and every guard below is pinned by tests.

1. **Pre-pricing screen** (`JackpotHunter.Screen`) — accessories, multi-unit lots, broken/for-parts comp
   sets, medians under $40, products with no sale in 120 days, and clusters whose comps run more than
   5x from cheapest to dearest (not one product) are dropped **before** a lookup is spent on them, each
   with a recorded reason.
2. **Vague clusters cost more evidence** — a cluster with no model-shaped token ("vitamix blender")
   needs 6 sold comps where one keyed on a model number ("s19j") needs 4.
3. **Minimum history to appear at all** (`HasEnoughHistoryToShow`) — 5+ comps. A two-comp estimate makes
   the resale price, the break-even *and* the supply price floor meaningless, so the product is dropped
   rather than shown with a caveat nobody reads.
4. **The two reads must agree** (`EstimateAgreesWithSweep`) — the sweep's own cluster median and the
   per-product estimate have to be within 2x of each other. This is what killed the $504 robot vacuum.
5. **Buy-side identity guard** (`IsPlausibleSupply`) — a listing found on the buy side must not be an
   accessory/parts/broken unit, must not say it merely *fits* the product, must not name a component the
   product itself doesn't ("Dirty Water Tank to iRobot Roomba Combo 10 Max" — same brand, same model
   number, $40 of plastic), must name the model, and must not be priced under **25% of the product's own
   quick-sale price**. On one real roll this rejected 20 of the cheapest listings found.
6. **Thin evidence can't wear a badge** — profit on fewer than 5 comps, or under 50 confidence, is capped
   at "thin", and the "watch" note names the actual blocker (comp count vs. match confidence).

The UI reports the whole funnel — comps mined → products worth a look → priced → sourced, plus how many
were dropped and how much supply was rejected — because **a short board is the guards working**, and the
seller has to be able to see that rather than assume the market is empty.

### Files

* `Services/CategorySweep.cs` (new) — 28 curated niches x 3 keyword probes each; deterministic,
  seed-driven rotation (`Select`, `ProbesFor`, `NextSeed`, `RollsToCoverEverything`).
* `Services/JackpotHunter.cs` (new) — `ProductSignature`/`Cluster` (sold listings → products, priced
  per unit), `Screen`, `ShoppingQuery`, `IdentityTokens`/`IsPlausibleSupply`/`SupplyPriceFloor`,
  `EstimateAgreesWithSweep`, `HasEnoughHistoryToShow`, `BreakEvenBuyPrice`, `TargetBuyPrice`,
  `BuildPlay`, `JudgePlay`, `Rank`.
* `Models/JackpotModels.cs` (new) — `JackpotCandidate`, `JackpotSourceOption`, `JackpotPlay`,
  `JackpotNicheOutcome`, `JackpotResult`.
* `Program.cs` — `GET /api/opportunities/roll-the-dice`, the four-phase `RollTheDiceAsync`
  orchestration (sweep → screen → price → source), and DI registration.
* `Services/LocalArbitrageAnalyzer.cs` — goldmine thresholds made public; `ResalePricing` now also
  carries days-to-sell, monthly sales and the opportunity score, all already computed upstream.
* `wwwroot/index.html` — the dashboard **Roll the Dice** band, the Opportunity Finder panel (zip /
  radius / categories-per-roll, board, funnel, footnote), `app.js?v=42`, `style.css?v=35`.
* `wwwroot/app.js` — `bindRollTheDice`, `rollTheDice(seed)`, `renderDice`, `renderDiceBoard`,
  `dicePlayHtml`, and **Hunt this locally**, which hands a play's keyword straight to the existing
  Local Deals scan.
* `wwwroot/style.css` — dice band, board, tier badges, supply rows, `local-badge-ebay`.
* Tests: `CategorySweepTests.cs` (13 cases), `JackpotHunterTests.cs` (51 cases).

### Verified

* `dotnet build` — 0 errors. `dotnet test` — **625 passed, 0 failed** (561 before; +64 new).
* **Live, against the real hosted comps database and the real eBay Browse API**, at four different
  seeds. A representative roll mined 527 sold comps across 4 categories → 11 products worth a look → 10
  priced → 5 sourced → **9 plays**, with 1 product dropped on the evidence gates and 20 cheap listings
  rejected as parts/accessories. Ranking, tiering, the funnel line, niche chips and supply rows all
  rendered — driven through the real UI with Playwright (dashboard band → one click → board).
* **Not verified live:** the local-classifieds half of sourcing (no zip was used, so no
  Craigslist/Facebook scrape ran this session) and the Terapeak second pass (Terapeak isn't connected on
  this machine). Both reuse `SearchLocalSourceAsync` / `SelectScrapeTargets` exactly as the Local Deals
  scan already exercises them, and both are covered by that feature's existing tests.
* Nothing was published, listed, or written to eBay. Every eBay call this feature makes is a read-only
  Browse search.

---

## 20. Offers to Watchers — the money that already found you (autonomous session, 2026-07-26)

Every other money feature in this app goes looking for a buyer: Roll the Dice hunts products, Local
Deals hunts supply, Inventory Health hunts mispriced stock. This one doesn't have to look. The buyer
already found the item, opened it, and told eBay to remember it — and then didn't buy.

**Offers to Watchers** finds those people and sends them a private, time-limited discount through
eBay's Send Offer to Interested Buyers, sized so it still clears the seller's own profit floor.

### The money impact

On the seller's real account, the first live scan found **1,527 people watching 53 live listings** —
warm demand that was sitting there converting into nothing. eBay confirmed 31 of those listings could
carry an offer right now, reaching **1,362 watchers in one click**.

The reason this is the highest-leverage sales action on eBay, and why it is not a markdown:

* a markdown gives margin to **everyone**, including the buyer who would have paid full price;
* an offer gives it **only to people who already hesitated**, only for ~48 hours, and **only if they
  accept** — the public price never moves, so an offer nobody takes costs the seller nothing at all.

The board is priced on that difference. It shows what one accepted offer per listing is worth
($22,619 gross on the 17-offer run above), what it would cost in margin **if every single one were
accepted** ($2,501), and the net after fees on every row where the seller has recorded what they
paid. Both the revenue and the margin figures are per-listing, not per-watcher — 688 watchers do not
buy 688 units because an offer went out, and a headline that said so would be a lie with a dollar
sign in front of it.

### How deep each offer goes, and what stops it

`WatcherOfferAdvisor` starts at eBay's 5% minimum and builds from there:

| Input | Effect | Why |
|---|---|---|
| Age | +3 / +5 / +8 points at 30 / 90 / 180 days | The same age bands the repricer uses; the longer it sits, the more the price is the blocker |
| Audience size | −2 at 10+ watchers, −1 at 5+, **+2 at 1-2** | A crowd is evidence the item is wanted, so it takes less to close. A single watcher is a maybe |
| Priced above market | Deep enough to **reach the going rate** | A discount to a price that was never competitive closes nothing; the watchers can see the comps |
| Already under market | Capped at 8% | The hesitation isn't about price; don't pay for it |
| No cost basis recorded | Capped at 10% | Without a floor there is no way to know a 20% offer isn't a 20% loss |
| Everything at once | Hard cap 25% | Past a quarter off, that's a repricing decision the seller looks at, not a default the app picked |

Then four guards, each pinned by tests:

1. **The break-even floor.** The offer price is never below what the item has to clear after every
   fee, using the same `ProfitCalculator`/`FeeProfile` pair every other screen costs an item with.
2. **A minimum-profit floor on top of it.** "Keep at least $25 per sale" raises the floor by
   `$25 / (1 − fee fraction)`, not by $25 — buying back profit costs more than the profit, because
   eBay's cut scales with the sale. A test asserts a sale at that floor really does leave $30.
3. **The quick-sale price is a floor in its own right.** That's the price the comps say the item moves
   at with no offer at all; discounting under it is paying for a sale the listing was already getting.
4. **A failed comp match can't move anything.** `MarketComparable` comes straight from the
   inventory-health scan, and the first live run proved why it matters: lot listings matching stray
   $35 accessories. Those rows show no market price and get no market-driven discount.

When even eBay's 5% minimum would land under the floor, the row says **"Under my floor"** and names
the number rather than suggesting a smaller offer that loses money quietly.

### Nothing reaches eBay by accident

Same three brakes as the repricer, the app's only other buyer-visible write:

1. **Previews by default** — `dryRun` must be explicitly false;
2. **`confirmed` must also be true** — verified live: `dryRun:false, confirmed:false` still came back
   as a preview with nothing sent;
3. **The floor is recomputed on the server** from the stored cost basis, never trusted from the
   browser. Verified live: a $189 offer on a $180-cost item was refused against its $207.95
   break-even, and a $315 offer was refused against a $323.22 minimum-profit floor. The override
   ("clearing stock at a deliberate loss") works and is written to the action log.

eBay's own eligibility answer is respected before any of that — `find_eligible_items` is asked once
per scan, and 21 of the seller's watched listings came back not eligible (lots, repeat offers inside
eBay's cooldown) and are shown as such rather than failed on send.

Sending needs the `sell.negotiation` OAuth scope, which is now requested at login. A connection saved
before this feature existed keeps working everywhere else; the offers board still renders its full
board off the watcher counts and tells the seller to reconnect, because the numbers are true either
way and only the send is blocked.

### Files

* `Services/WatcherOfferAdvisor.cs` (new) — the ladder, `ProfitFloorPrice`, `Floor`, `NetProfitAt`,
  `Suggest`, `OfferPriceFor`, `Build`, `Summarize`, `Rank`, `CleanMessage`.
* `Models/WatcherOfferModels.cs` (new) — `WatcherOfferItem`, `WatcherOfferSummary`,
  `WatcherOfferResult`, and the send request/result pair.
* `Services/EbayService.cs` — `GetOfferEligibleListingIdsAsync`, `SendOfferToWatchersAsync`,
  `EbayPermissionException`, `ExtractRestError`; the `sell.negotiation` OAuth scope added to
  `GetAuthorizationUrl`.
* `Program.cs` — `GET /api/offers/watchers`, `POST /api/offers/send`, and two optional parameters on
  `ScanInventoryHealthAsync` (`minWatchers`, `watchersFirst`) so the offers board reuses that whole
  scan rather than re-deriving market price, break-even and cost basis a second way.
* `wwwroot/index.html` — the **Offers to Watchers** overlay, the sidebar entry, the cross-link from
  Inventory Health, the send-confirmation gate, `app.js?v=43`, `style.css?v=36`.
* `wwwroot/app.js` — `bindWatcherOffers`, `runOfferScan`, `renderOfferSummary`, `renderOfferRows`,
  per-row discount editing (clamped to 5-25%), select-all-in-filter, preview, and the confirm gate.
* `wwwroot/style.css` — the handful of classes genuinely new to this screen; the table, tiles and
  bulk bar are Inventory Health's, because it is the same inventory seen from a different angle.
* Tests: `WatcherOfferAdvisorTests.cs` (35 cases).

### Verified

* `dotnet build` — 0 errors. `dotnet test` — **660 passed, 0 failed** (625 before; +35 new).
* **Live, against the seller's real eBay account**: 87 active listings → 53 with watchers → 1,527
  watchers → eBay confirmed eligibility on 31 → board, tiles, filters, select-all, per-row discount
  editing, preview and the confirmation gate all driven through the real UI with Playwright. No
  console errors. The eligibility call succeeded on the existing token, so the reconnect path was
  exercised only by its unit-level logic.
* **Live server-side guards**: break-even refusal, minimum-profit refusal, the deliberate-loss
  override, the 5-25% range check, the missing-listing-ID skip, and the "not confirmed means preview"
  brake — all exercised end to end. The one temporary cost-basis row created for this was deleted.
* **Not verified live: an actual send.** `POST /api/offers/send` with `dryRun:false, confirmed:true`
  puts a real, buyer-visible, non-recallable offer in front of real watchers on the seller's account —
  that is their click to make, not this session's. Every layer under it was exercised: the request
  body, the floor re-check, the range check and the preview path all ran; only the final
  `send_offer_to_interested_buyers` POST was not fired.

---

## 21. Liquidation Lot Analyzer — the one decision with the most money on it (autonomous session, 2026-07-26)

Every money feature in this app so far prices **one item**: one product to source (§19), one local
listing to flip (§11–12), one live listing to reprice (§13), one watcher to convert (§20). But the
single biggest wins and the single biggest losses in reselling are not made one item at a time —
they are made on **pallets, wholesale lots and estate/auction lots**, where a reseller commits
hundreds or thousands of dollars in one go, usually against a "retail value" number that means
nothing, and finds out whether they were right over the following six months.

Paste the manifest (or photograph it), enter what the lot costs, and get the answer:
**per-item resale → total lot resale → total cost → net profit → BUY / SKIP**, plus the highest
price at which it is still a buy, and which handful of lines actually carry the value.

`POST /api/lots/analyze` → the new **📦 Lot Analyzer** page.

### The money

Five numbers a pallet buyer cannot get anywhere else, each of which routinely decides a lot:

1. **The fees and shipping on every unit.** This is what kills pallet math. On the sample manifest
   below, 67 sellable units carry **$218 of eBay fees and $533 of shipping** — $751 that never
   appears on the spreadsheet the lot was bought on, against a $1,446 resale.
2. **The buyer's premium, the tax and the freight.** A $650 hammer price at a 15% premium, 8.375%
   tax and $180 freight is **$990.10 all-in** — 52% more than the number the bidder was looking at.
   The panel recomputes this live as those fields are typed.
3. **The max bid.** Exact arithmetic, not a rule of thumb: net recovery is fixed by the manifest and
   cost scales linearly with the ask, so `A = (R / (1 + r) − freight) / ((1 + premium)(1 + tax))`.
   A test asserts that bidding exactly the max produces exactly the requested ROI. This is the
   number to walk into the auction with.
4. **The "retail value" test.** Manifests lead with MSRP. The sample's stated **$4,121 retail** is
   worth **35% of that** on eBay. The retail column is *never* used as a value here — only as a
   cross-check on whether the comp match makes sense.
5. **Which lines carry the lot.** On the sample, **3 lines are 90.7% of the value**. Those are the
   items to physically inspect before paying; the rest of the manifest is padding. A buyer who does
   not know which three is buying blind.

Plus **time to clear** — the slowest line sets the date, because the capital is not back until the
last item sells — and **cost per sellable unit**, which is what each usable item really costs.

### Reuse, not a second pricing engine

| Step | Reuses |
|---|---|
| Read a CSV/TSV/pipe manifest | `ManifestParser` — **new**, deterministic, and it goes first |
| Read a photo or prose lot description | `ClaudeService.AnalyzeManifestAsync` — **new**, fallback only |
| Group lines into products | `TerapeakMarketService.BuildCacheKey` — the same key Terapeak caches on |
| Price each product | `AnalyzeProductAsync`: `ProductNormalizer` → hosted comps DB → `ComparableMatcher` → `MarketPriceEstimator` → `SellThroughCalculator` → `ConfidenceScoringService` |
| Cost every unit | `ProfitCalculator` + `FeeProfile` — a unit out of a pallet is costed by the same rules as a dropship, a local flip or a repriced listing |
| Ration Terapeak scrapes | `LocalArbitrageAnalyzer.SelectScrapeTargets`, with a cache-only first pass |
| Multi-pack detection | `ProductNormalizer`'s existing "lot of N" / "N pcs" quantity read |

New code is only what genuinely did not exist: reading a manifest, recovery by grade, the cost
allocation, the max-bid solve, the concentration analysis, and the verdict.

### The deterministic parser goes first, on purpose

Most manifests are spreadsheet exports. Columns can be read **exactly**: a parser cannot hallucinate
a quantity or invent a line that was never on the pallet, and a 400-row manifest costs nothing to
read. Claude is the fallback for what that cannot do — a photo of a printed manifest, or prose
("estate lot: two Dewalt drills, a box of assorted cables"). A dropped `.csv`/`.txt` file is loaded
straight into the paste box rather than sent to the model. The AI prompt is told plainly not to
invent lines, because an imagined $400 item is a $400 error in a real purchase decision.

Three parser traps are pinned by tests, because each produces a confident wrong number:
**totals rows** counted as items (doubles a lot's apparent value), **"Extended Retail"** read as a
unit price (multiplies every line by its quantity — `MatchHeader` now explicitly blocks line-total
columns from the unit-price slot, a bug the tests caught), and **model years** read as quantities
("2024 Ford F-150" is not 2,024 units).

### Recovery is two numbers, shown and editable — not one hidden fudge

A returns pallet does not yield 100% working units, and pretending otherwise is how these tools
flatter bad lots. `LotAnalyzer.Grades` publishes **seven grades**, each with two separate figures,
because they are two separate risks: **units sellable** (the dead, the missing, the empty boxes) and
**price vs comps** (what the survivors fetch). Both are on screen, both are editable, and the deep
discount on a returns pallet lives in the units that never sell rather than in a fictional haircut
on the ones that do — the comps are already used-goods sales.

The UI defaults to **tested customer returns (80% / 88%)**, not to the first grade in the list. The
grades are ordered best-recovery first, so defaulting to index 0 would have defaulted a pallet tool
to the rosiest assumptions in the app. Caught in the browser and fixed.

### What it refuses to do

The guards are the product. Most exist to **not** make a confident call:

| Rule | Why |
|---|---|
| Multi-pack lines get **no price** | "Case of 12" against per-unit comps is the mistake that produced a 27% markdown on a working listing in §13. Multiplying by N would be worse — packs trade at a discount to N× |
| Comp > 3× the stated retail → **excluded** | A mismatched product, not a bargain |
| Comp < 5% of stated retail → **excluded** | An accessory match |
| Comp < 15% of retail **on under 5 comps** → **excluded** | See below |
| Coverage under 40% → **no verdict at all** | "Only 22% of this manifest could be priced" is not a skip, it is no answer |
| Coverage under 60% → **"a lead, not a decision"** | The numbers are real for what was priced; the lot call is not |
| Under 3 comps on a line → **"thin"**, never "priced" | The same evidence bar as §11 and §13 |
| Net recovery ≤ 0 → **"dead lot"** | "Even free, this lot loses money" — said plainly |
| Unprofitable but positive break-even → **"buy it lower"** | The useful answer is not "no", it is "yes, at $290" |
| Ask price of 0 → **"add the ask"** | A resale total is not a verdict, and is not presented as one |
| Lines the app refused to value are **never dropped** from the table | A line it would not price is exactly the one the buyer must eyeball |

### Two defects the live runs caught

Both were found by pointing this at the **real hosted comps database**, not reasoned about in the
abstract, and both are now tests:

1. **A $169 DeWalt DCD771C2 drill kit priced at $14 off two sold comps.** That is a spare battery,
   not a drill kit. The original low-side guard sat at 2% of retail, so 8% sailed through — and it
   *understates* a line, dragging the whole lot toward a wrong SKIP. Understating costs a buyer a
   lot they should have bought, exactly as overstating costs them one they shouldn't. The low-side
   check is now **scaled by evidence**: under 15% of stated retail is refused on fewer than 5 comps
   and accepted with real history behind it, because some categories genuinely resell at a tenth of
   MSRP. Excluding it dropped coverage from 79% to 58.7% — which is the honest number, and is
   reported on screen.
2. **A max bid built from half a manifest was being stated as a ceiling.** Every line the app
   refuses to price can only *add* to what the lot returns, so at low coverage that number is a
   **floor**, not a cap. Saying "bid there or walk" without the caveat would talk someone out of a
   lot whose unpriced half was the good half. The verdict now says so.

### A pre-existing navigation bug fixed on the way

Closing any overlay (`showDashboard`) left `location.hash` still pointing at the closed section, so
clicking that sidebar entry again set the hash to what it already was, fired no `hashchange`, and
did **nothing** — and a reload landed on a section the user had already closed. This affected
**Offers to Watchers, Inventory Health, Opportunity Finder and Photo Library**, not just the new
page. Fixed at the shared source: the hash is cleared on close (`replaceState`, so no loop), and a
nav click whose hash already matches navigates directly. Verified open → close → reopen on all five
overlays.

### Files

| File | Change |
|---|---|
| `Models/LotAnalysisModels.cs` | **New** — `ManifestLine`, `LotAnalysisRequest`, `LotGradeAssumption`, `LotLineAnalysis`, `LotTotals`, `LotConcentration`, `LotAnalysisResult` |
| `Services/ManifestParser.cs` | **New** — delimiter detection, RFC 4180 splitting, header mapping (line-total columns blocked from the unit-price slot), headerless column inference, free-list parsing, money/quantity scalars |
| `Services/LotAnalyzer.cs` | **New** — `Grades`/`Assumptions`, `BuildLine`, `RetailSanityCheck`, `AllocateCost`, `CostOf`, `Summarize`, `MaxAsk`, `Concentrate`, `Judge`, `Coverage`, `Rank` |
| `Services/ClaudeService.cs` | `AnalyzeManifestAsync` — the photo/prose fallback extraction |
| `Program.cs` | DI + `POST /api/lots/analyze`, `GET /api/lots/grades`, and the `AnalyzeLotAsync` orchestration (read → group → cache-only pass → rationed Terapeak pass → cost → judge) |
| `wwwroot/index.html` | The **Lot Analyzer** section, sidebar entry, manifest input, cost/recovery controls. `app.js?v=44`, `style.css?v=37` |
| `wwwroot/app.js` | `bindLotAnalyzer`, `runLotAnalysis`, `renderLot*`, `refreshLotCostLine`, the grade picker, file/photo drop; plus the shared nav-hash fix |
| `wwwroot/style.css` | `.lot-*` — manifest input, cost fieldsets, verdict banner, concentration callout, value-carrying row edge |
| `ING eBay AutoLister.Tests/ManifestParserTests.cs` | **New** — 28 cases |
| `ING eBay AutoLister.Tests/LotAnalyzerTests.cs` | **New** — 56 cases |

### Verified

* `dotnet build` — **0 errors** (2 pre-existing `NU1903` warnings). `dotnet test` — **744 passed,
  0 failed** (660 before; +84 new).
* **Live, against the real hosted comps database** (dev ports 9371–9375). A 7-line returns manifest
  at a $650 ask, 15% premium, $180 freight and 8.375% tax: 7 lines read → 7 products looked up →
  4 priced → 1 excluded on the retail cross-check → **`buy_below`, break-even $412.92, max bid
  $253.68 at 40% ROI**, 3 lines carrying 90.7% of the value, and resale at 35% of the manifest's
  claimed retail. The plain-list path was exercised separately ("3x Ninja BL610", "…(qty 4)") with
  no ask price → the `no_ask` verdict, quantities read correctly, and one line honestly showing a
  **negative** net once $8/unit shipping is charged.
* **Real browser (Playwright)**: sidebar entry, the live all-in cost line, grade switching resetting
  both recovery figures, the empty-input guard, the verdict banner, chips, all 8 tiles, the
  10-column table with value-carrying rows gold-edged and the excluded row labelled, the
  concentration callout, and open/close/reopen on all five overlays. **No console errors.**
* **Read-only end to end.** Nothing is listed, published, bought, bid on or sent anywhere; the only
  outbound calls are the sold-comp lookups the app already makes.

### Not verified / known limits

* **The AI extraction path was not exercised live** — every live run used `useAi: false`, so the
  deterministic parser was proven on its own. The prompt and its post-processing are covered only at
  unit level.
* **Without a retail column there is no cross-check.** In the plain-list run an Instant Pot priced at
  $15.78/unit is plainly an accessory match, and with no stated MSRP the guard has nothing to test it
  against. The row still shows its comp count and coverage still reflects what was priced, but a
  manifest with a retail column is materially better protected than one without.
* Recovery rates by grade are **published industry starting points**, not measurements of any
  supplier — labelled as such on screen, and editable.
* Not modelled: the buyer's time, storage, or units arriving different from the manifest. Said
  plainly in the panel footnote rather than buried in a fabricated cost column.

---

## 22. All-in net, break-even and the offer floor — on every price the app shows (autonomous session, 2026-07-26)

### The money problem

The app had a correct profit calculator and almost never showed it to the person setting the price.

`ProfitCalculator` was wired into the *sourcing* screens — local arbitrage, Roll the Dice, the lot
analyzer, inventory health, watcher offers. The screens where a price is actually decided showed
gross numbers and nothing else:

* **The listing editor** — both of them — had a "Buy It Now Price" box and no fees anywhere near it.
* **Market Research** recommended a median with the words "Recommended Price" over it.
* **The sold-comps strip** showed Average / Median / Low / High and no net.

So the app could say **$120** three different ways and the seller could bank **$84** without ever
seeing the $36. That is the exact failure mode this task names: sellers lose money to fees they were
never shown.

Worse, the fees themselves were fiction. `FeeProfile` was a hardcoded object with **shipping,
packaging, handling, promoted rate and return reserve all sitting at zero**, and no way to change
any of them. Every "net profit" the whole app printed — including the sourcing screens that *did*
use it — was net of eBay's cut and nothing else. On a $120 sale with a $9 label, $1.25 of packaging,
$3 of handling and a 2% ad rate, that is **$15.65 the old numbers said the seller kept**.

### What was built

**One calculation, shown everywhere, from numbers the seller owns.**

1. **`Services/FeeProfileStore.cs` (new)** — the seller's real fees and costs, persisted in the app's
   own SQLite database next to the cost basis they are combined with, and loaded into the
   `FeeProfile` singleton at startup. Stored one value per row, so a profile written by an older
   build simply lacks the newer keys rather than failing to deserialise. Because every analyzer
   holds that one instance, **saving the form re-prices the entire app at once** — no re-scan, no
   restart.

2. **`Services/NetProceedsCalculator.cs` (new)** — turns an asking price into the three numbers a
   seller needs, in the order they need them:
   * **Net profit** — what this sale is worth, itemised down to every deduction.
   * **Break-even** — the price below which making the sale costs money.
   * **Minimum offer to accept** — the lowest number to say yes to in a negotiation.

   It does not re-derive fee math. Break-even comes from `ProfitCalculator`; the floors come from
   the identity that falls out of it, `Net(P) = (P - breakEven) x KeepFraction`, which is also what
   `WatcherOfferAdvisor` negotiates with — so the floor in the editor and the floor on the watchers
   screen **cannot drift apart**. `WatcherOfferAdvisor.ProfitFloorPrice`/`NetProfitAt` now delegate
   to it rather than keeping a second copy.

3. **The floor policy** — `FeeProfile` gained `MinimumNetProfit` and `MinimumMarginPercent`.
   Break-even answers *"am I losing money"*; these answer *"is this worth doing"*. Whichever binds
   harder becomes the minimum offer, and it now bounds the inventory-health markdown ladder and the
   watcher-offer depth as well, so a repricing run cannot walk an item down to a
   technically-profitable $0.40. Both default to 0, which reproduces the previous behaviour exactly.

### Two real bugs fixed on the way

* **Payment processing was modelled as a fixed cost inside the break-even.** `ProfitCalculator`
  charged it as a percentage of revenue when computing net profit but folded it into *fixed* costs
  when solving for break-even — so any seller who billed processing separately got a break-even, and
  therefore an offer floor, that was **too low**. Harmless while the rate defaulted to 0 and nothing
  could set it; wrong the moment the Fees & Costs screen existed. `WatcherOfferAdvisor`'s own fee
  fraction had the same omission. Both now use `FeeProfile.RevenueFeeFraction`. Covered by a test
  that asserts net profit is still zero *at* the new break-even.
* **The local-arbitrage row under-reported its own fees**, summing eBay + promoted + other while
  leaving the return and testing reserves out of the displayed "fees" figure (net profit was right;
  the breakdown was not). Both call sites now use one `ProfitBreakdown.MarketplaceFeeTotal` property
  instead of a sum re-typed per screen. `PaymentProcessingFees` is also its own field now rather than
  hidden inside `OtherCosts` — a fee the seller cannot see is a fee they cannot price around.

### What the seller sees

* **A take-home panel under the price field in both editors**, live as they type. The verdict carries
  the colour, so a price below break-even is rendered as a warning and never in the same style as a
  profitable one: *"You lose $6.20 on this sale — you need $65.63 just to break even."* Below it, two
  floors, and a **"Use as auto-decline"** button that turns the computed floor into eBay's own
  auto-decline rule — the point of computing a floor is never having to see the offers beneath it.
  A collapsible breakdown shows where every dollar of the gap went, **including the $0.00 lines**: a
  missing row reads as "handled", a zero row reads as "a knob you have not turned".
* **Cost entered in the editor is written to the shared cost-basis store**, so Inventory Health and
  Watcher Offers gain a break-even for that listing — entered once, in the place the seller is
  already looking at the price.
* **Market Research and the sold-comps strip now say what the median is worth**: *"list at the
  $120.00 median and you keep $44.45 after fees, floor $83.98."*
* **Inventory Health** shows the floor beneath the break-even in the same cell, labelled with which
  rule set it.
* **Settings → Fees & Costs** — eleven fields and a plain-English summary of what they cost per sale.
  Rates that would make the math unsolvable are clamped server-side and the corrected value is echoed
  back into the form, so the number on screen is always the number in force.

### Files

| File | Change |
|---|---|
| `Models/NetQuoteModels.cs` | **New** — `NetQuote`, `NetQuoteLine`, `NetQuoteRequest`, `NetQuoteResponse`, `FeeProfileView` |
| `Services/NetProceedsCalculator.cs` | **New** — `Quote`, `MinimumOffer`, `ProfitFloorPrice`, `MarginFloorPrice`, `NetProfitAt`, `Describe` |
| `Services/FeeProfileStore.cs` | **New** — SQLite persistence, `Apply`/`SaveAndApply` into the live singleton, `ToView`/`FromView` |
| `Services/FeeProfile.cs` | `MinimumNetProfit`, `MinimumMarginPercent`, `RevenueFeeFraction`, `KeepFraction`, `Clone`, `CopyFrom`, `Sanitize` |
| `Services/ProfitCalculator.cs` | Payment processing charged as a revenue percentage in break-even; `PaymentProcessingFees` reported separately |
| `Services/WatcherOfferAdvisor.cs` | Floor math delegated to `NetProceedsCalculator` — one implementation |
| `Services/InventoryHealthAnalyzer.cs` | `MinimumOfferPrice`/`Basis`/`NetProfitAtMinimumOffer`; the markdown ladder is bounded by the floor, not bare break-even |
| `Services/LocalArbitrageAnalyzer.cs`, `Services/LotAnalyzer.cs` | Use `MarketplaceFeeTotal`/`FulfilmentCostTotal` |
| `Models/MarketAnalysisModels.cs` | `ProfitBreakdown.PaymentProcessingFees`, `MarketplaceFeeTotal`, `FulfilmentCostTotal` |
| `Models/InventoryHealthModels.cs` | The three minimum-offer fields |
| `Program.cs` | DI + startup `Apply`; `GET`/`POST /api/fees/profile`, `POST /api/pricing/net-quote`; `pricing` block on all three `/api/sold-comps` paths; watcher floors default to the seller's policy |
| `wwwroot/index.html` | Take-home panel in both editors, the Fees & Costs settings section. `app.js?v=45`, `style.css?v=38` |
| `wwwroot/app.js` | `bindTakeHome`/`refreshTakeHome`/`renderTakeHome`, cost-basis read/write from the drawer, fee settings load/save, net on Market Research + the sold-comps strip, the Inventory Health floor cell |
| `wwwroot/style.css` | `.th-*` take-home panel, `.fees-summary`, `.inv-floor` |
| `ING eBay AutoLister.Tests/NetProceedsCalculatorTests.cs` | **New** — 17 cases |
| `ING eBay AutoLister.Tests/FeeProfileStoreTests.cs` | **New** — 8 cases |

### Verified

* `dotnet build` — **0 errors** (2 pre-existing `NU1903` warnings). `dotnet test` — **770 passed,
  0 failed** (744 before; +26 new). `node --check app.js` clean.
* **Live against the running app** (dev port 9451). Saved a realistic profile (13.25% + $0.40, 2% ads,
  $9 label, $1.25 packaging, $3 handling, 3% returns, $15 minimum profit) and priced a $120 sale on a
  $40 item: **$35.55 of deductions (29.6% of gross), $84.45 landing, $44.45 net, break-even $65.63,
  floor $83.98** — and the floor nets **exactly $15.00**, confirming the "buying back $15 of profit
  costs more than $15 of price" algebra end to end. `GET`/`POST /api/fees/profile` round-tripped,
  clamped a 1325% typo back to a solvable profile, and `/api/sold-comps?cost=&ask=` returned the
  costed `pricing` block on the links-only path as well as the data paths.
* The test profile written during live verification was **reset to defaults afterwards**, so no
  surprise fee assumptions were left in the local database.
* **Read-only end to end.** Nothing was listed, published, priced on eBay or sent anywhere.

### Not verified / known limits

* **Not exercised in a real browser this session.** The panel, the settings form and the sold-comps
  net line were verified through the API and by JS syntax check, not by clicking them. The endpoints
  they call are proven; the wiring between input and endpoint is not.
* **The fee rates are still the seller's estimate, not their account.** eBay exposes no API for a
  seller's actual negotiated final value fee, so the profile starts at the typical 13.25% + $0.40 and
  the screen tells the seller to check a recent payout. A wrong rate in gives a wrong net out — but
  it is now *visible* and *editable*, which it was not before.
* **One fee profile, not one per category.** eBay's final value fee varies by category, and this
  models a single blended rate. Per-category rates are the obvious next step and would slot into
  `FeeProfileStore` without touching anything downstream.
* **Shipping cost is a default, overridable per listing, not calculated.** The app does not rate-shop
  a label; the seller supplies the typical cost and can override it in the take-home panel.

---

## 23. Promoted Listings ROI Advisor — the ad rate that keeps the most money (autonomous session, 2026-07-26)

### The money problem

Promoted Listings is the one cost a seller **opts into**, on a screen that has never seen what they
paid for the item.

eBay's suggested ad rate is computed from what the rest of the category is paying. On a listing with
a 7.6% margin it will suggest 8%, present it as a recommendation, and charge it **on the whole sale,
shipping included** — a rate bigger than the entire profit, one click away. Sales go up. Money goes
down. Nothing on eBay ever tells the seller that happened.

The app already had the missing half: `ProfitCalculator` + `FeeProfile` know exactly what one sale of
one item is worth. `FeeProfile.PromotedListingRatePercent` existed as a single global assumption and
nothing in the app ever advised on it. This joins the two.

`POST /api/promoted/advise` (one listing, no eBay account needed) and `GET /api/promoted/board`
(every live listing) → the new **📣 Ad Rate Advisor** page, plus a live strip under the price field
in both listing editors.

### The three numbers behind every answer

1. **The margin ceiling** — `net ÷ gross`. Above it the ad fee is bigger than the whole profit on the
   sale, and no amount of extra volume fixes that. Reported on every row, because it is the number
   that turns eBay's suggested rate from "aggressive" into "arithmetically impossible".
2. **The break-even lift** — `L* = c·f / (n − f)`. The extra sales a rate must actually buy just to
   leave the seller no worse off. **No lift model appears in it**, so it stays true even if the model
   is wrong. That is why it sits next to the modelled figure on every rung rather than behind it.
3. **Take-home per 100 organic sales** — `100·[(1+L)·n − (c+L)·f]`. The rate that maximises this is
   the recommendation, found by walking every half point from 2% to 20%.

Expressed per 100 sales on purpose: **the optimal rate does not depend on how many units the listing
moves**, so the recommendation is just as valid for a listing that has never sold — where any monthly
projection would be an invented number with a dollar sign in front of it.

### The two assumptions, reported rather than buried

* **Lift saturates.** `lift(r) = maxLift · r / (r + k)`, with `k` = the category's typical rate. The
  first points buy the most, and 2% in a category where the field runs 11% buys almost no placement
  while the same 2% in a 4.5% category is a real bid. That is the entire reason category norms are in
  here — not as a recommendation to copy, but as the competitive floor that decides what a rate buys.
* **You pay for sales you were getting anyway.** A buyer who would have found the listing regardless
  still arrives through the ad, and eBay bills the rate on that sale. That share is why a small ad
  rate is not close to free, and it is the reason a **proven seller is advised a lower rate, not a
  higher one** — the opposite of what eBay's suggestion does.

Both are shown as numbers in the tradeoff panel ("up to 20% more sales, half of it bought by the 11%
category rate… you also pay the fee on the 65% of sales you would have made anyway"), so a seller who
disagrees can see exactly what they are disagreeing with.

### What it refuses to do

| Rule | Why |
|---|---|
| No cost basis → **no rate at all** | A rate is sized against a margin. Without one it shows what an ad costs per sale and says what is missing, rather than sizing against a guess. |
| Net ≤ 0 → **no rate** (`no_margin`) | Promoted Listings multiplies whatever the margin already is. On a loss it multiplies the loss. |
| Thin margin → **don't promote** | On a $22 item with $1.68 of margin, even eBay's 2% minimum takes $0.44 of it — charged on the sales it was already making. Keep the margin. |
| No sold history → **held at the category norm** | Bidding above the field is a bet worth making on proven demand, not on a listing the app knows nothing about. |
| Priced 15%+ above market → **fix the price first** | Ads cannot sell a price buyers are already beating; they only put it in front of more of them. |
| The seller's **minimum profit floor bounds the ad rate** | An ad rate spends margin exactly like a markdown does, so the floor that bounds the repricer's ladder and the watcher-offer depth bounds this too. A seller who said "never under $15 a sale" should not be talked under it by a campaign instead of by a price. |
| Recommendations capped at **20%** | eBay allows far more. A rate that size is a deliberate clearance decision, and the app says so instead of picking it for someone. |
| Gain under **$10 per 100 sales** → "leave it" | Optimal and worth doing are different questions. A board full of dime-sized wins is how the real ones get scrolled past — the same materiality rule the repricer applies to a one-cent markdown. |

### What the seller sees

* **A ranked board**, biggest money first: net per sale, what the category pays, what they run, what
  they should run, the ad fee per sale before and after, and — the honest heart of the row — **needs
  vs expects**: the lift the rate must buy (arithmetic) beside the lift the model predicts (estimate).
* **The tradeoff, per listing** — every rate from "no ads" to 20% with the fee, the take-home, both
  lift figures, the net per 100 sales and the delta against not advertising. Where the two lift
  columns cross is where the rate stops paying for itself, and it is visible rather than asserted.
* **Board totals on a "one sale of each" basis**, not a projected month — eBay reports no per-listing
  sales rate, and a fabricated volume would put a made-up dollar sign on the headline. Monthly money
  appears only for the listings whose own sales history supports one, labelled that way.
* **"Assume X% everywhere"** — the revenue-weighted recommended rate written into Fees & Costs, which
  re-prices every net figure in the app at once. The button says plainly that it changes what the app
  assumes; **eBay campaigns are set in Seller Hub**, and nothing here touches one.
* **A live strip under the price field in both editors**: *"Run ads at 4.5% — $10.80 a sale. Leaves
  $107.00 of your $117.80. It has to lift sales 5.0% to pay for itself."* Typing a price that kills
  the margin flips it to "No margin to advertise" as you type.

### Files

| File | Change |
|---|---|
| `Models/PromotedListingModels.cs` | **New** — `AdRatePoint`, `PromotedAssumptions`, `PromotedAdvice`, `PromotedBoardSummary`, `PromotedBoardResult`, `PromotedAdviceRequest` |
| `Services/PromotedRateNorms.cs` | **New** — published typical ad rates for 23 category groups, matched on the category **name** (the Trading API returns a leaf id; mapping leaf → top level would need a live Taxonomy call per listing to answer a question accurate to the nearest point), competition labels, seller override |
| `Services/PromotedListingAdvisor.cs` | **New** — `LiftPercentAt`, `BreakEvenLiftPercent`, `MarginCeilingRatePercent`, `NetPer100Sales`, `AssumptionsFor`, `SearchBestRate`, `BuildLadder`, `Build`, `Rank`, `Summarize` |
| `Program.cs` | DI + `POST /api/promoted/advise`, `GET /api/promoted/board`, `GET /api/promoted/categories`. The board reuses `ScanInventoryHealthAsync` whole rather than re-deriving market price, cost basis and break-even a second way — the same posture the offers-to-watchers board takes |
| `wwwroot/index.html` | Nav entry, the Ad Rate Advisor overlay, the tradeoff modal, the ads strip in both editors. `app.js?v=46`, `style.css?v=39` |
| `wwwroot/app.js` | `bindPromoted`, `runAdRateScan`, `renderAdScan`/`renderAdSummary`/`renderAdRows`/`renderAdBlendBar`, `openAdLadder`, `applyBlendedAdRate`, `refreshAdRateStrip` (hung off the existing `refreshTakeHome`, so one price change costs one extra call) |
| `wwwroot/style.css` | `.ad-*` verdicts and ladder, `.th-ads*` editor strip — reuses the Inventory Health table, tiles and pills wholesale |
| `ING eBay AutoLister.Tests/PromotedListingAdvisorTests.cs` | **New** — 33 cases |
| `ING eBay AutoLister.Tests/PromotedRateNormsTests.cs` | **New** — 17 cases |

Net profit is never re-derived here: it comes from the same `ProfitCalculator`/`FeeProfile` pair as
`NetProceedsCalculator`, the repricer and the watcher-offer floors, with the ad rate zeroed on a clone
so the ad fee is the only thing this varies.

### Verified

* `dotnet build` — **0 errors** (2 pre-existing `NU1903` warnings). `dotnet test` — **820 passed, 0
  failed** (770 before; +50 new). `node --check app.js` clean.
* **Live against the running app** (dev ports 9461–9463). `/api/promoted/board` over the connected
  account's **87 real listings**: 200, ranked, and correctly reporting that not one of them has a
  recorded cost basis — so no rate was sized, and the board says why instead of advising on a guess.
  `/api/promoted/advise` checked across six scenarios: a fat-margin miner (4.5%, break-even lift 6.6%
  against a modelled 22.5%), a mid-margin headphone at 12% (**drop to 4% — overpaying $14.40 a sale**),
  a proven card seller at 11% (**turn the ads off — $33.00 a sale for nothing**), a 7.6%-margin phone
  case (**don't promote**), an unproven clothing listing (**held at the 11% category norm**), and a
  listing with no cost (**refuses to size a rate**).
* **Real browser (Playwright)** — the page opens from the nav, the scan renders summary tiles, the
  warning banner, ranked rows and the blended-rate bar; the tradeoff modal renders all 13 rungs with
  the best rung and the seller's current rung marked distinctly; changing the filter re-renders
  **without re-fetching**; the editor strip updates live as price and cost change, and flips to "No
  margin to advertise" when the price drops through break-even. **No console errors.**
* **Read-only end to end.** No eBay campaign, price, listing or offer was created or changed. The only
  write the feature can make is to the app's own fee profile, on an explicit click.

### Two things the live run changed

* The status line read the compared rate off the first row, which is only correct while every row
  shares a rate. The board now returns `comparedRatePercent` and says what it judged against.
* The editor strip rendered "don't promote" copy for a listing that was already **losing** money —
  two different problems reading the same. Below zero it now uses the server's own `no_margin` copy
  ("this sale already loses $0.18 before any ad spend").

### Not verified / known limits

* **The lift curve is a model, not measured data.** eBay publishes no per-listing attribution data
  through any API available here, so the curve is calibrated on published category behaviour and
  reported as an assumption on screen. The break-even lift beside it is arithmetic and needs no such
  trust — that asymmetry is the design.
* **The rate the seller "runs today" is self-reported.** eBay exposes no API for a listing's live ad
  rate, so the board compares against the Fees & Costs assumption or the box on the page, and says
  which. Per-listing current rates would need the Marketing API and a campaign-management scope.
* **Category norms are published typicals, not this account's trending rate.** Overridable per
  request; a per-row override in the board UI is the obvious next step.
* **Category came back empty on the real inventory scanned here**, so those rows fell to the
  cross-category average — honest, and labelled "eBay average", but a Taxonomy lookup per listing
  would do better.
* **Nothing sets a rate on eBay.** Applying a rate is a Seller Hub action, said plainly in the
  footnote and on the apply button.

---

## 24. Rising-Demand / Price-Trend Radar — buying at last month's price (autonomous session, 2026-07-26)

Every pricing screen in this app answers **"what is this worth?"** — a snapshot, in the present
tense. A sourcer's money is made one tense earlier: buying the thing whose price is on its way up,
before the price gets there. The sold-comps database already held the evidence — every row carries a
sold price **and a sold date** — and nothing had ever read it as a time series.

`GET /api/trends/radar` → the new **📈 Rising Now** page ("Buy Now — Prices Climbing").

### Why this makes the seller money

Getting ahead of a price move is the cheapest margin a reseller ever gets: the same unit, bought at
last month's price and sold at next month's, with no extra work, no extra listing and no negotiation.
On the representative RTX 3080 row the radar renders, the sold median moved $500 → $600 in 45 days
while sales went 8 → 14. That is **$52 per unit of extra break-even headroom** on a buy that already
cleared 75% ROI at today's price — and the whole point is that the seller only knows to buy it
*because* the trend was measured.

Nothing else in the app, and nothing in Vendoo / List Perfectly / ZIK / eBay Seller Hub, answers
"which of these is worth **more** than it was?" Terapeak charts one product you already thought of;
this sweeps categories and ranks what moved.

### The measurement

Two windows back to back (30/45/60/90 days, seller's choice), per product cluster:

| Signal | Meaning |
|---|---|
| 🔥 **Rising demand** | Price up ≥8% **and** velocity up ≥20% — selling for more AND more often |
| 🌱 **Volume building** | Velocity up, price hasn't followed yet — the early half of a move |
| **Supply squeeze** | Price up, volume **down** — scarcity, not demand. Good for whoever already has one, hard to buy into. Deliberately *not* dressed up as a buy |
| **Cooling / Flat** | Reported, not hidden — the "everything measured" view exists so a scan can say "nothing here is climbing" and be believed |

Backed by a **Theil–Sen slope** (median of pairwise slopes, in $/month) across every dated sale, as a
second opinion on the two-window comparison. Least-squares would let one parts-only sale tilt a whole
trend; a median of slopes can't.

### The four ways a price-trend tool prints confident nonsense — and the guard for each

This is the product. Most of `PriceTrendAnalyzer` is an argument with itself about when a number is
real:

1. **The database's own drift.** If the collector ingested twice as many rows this month, every
   product's velocity doubles and the whole board reads as a boom. Velocity is therefore
   **detrended against a scan-wide baseline** (`BuildCorpus`), multiplicatively. Not theoretical: the
   live scan run during this session came back with the corpus **up 211.8%** (220 → 686 comps between
   windows). Undetrended, every product on that board would have looked like rising demand.
2. **The collector stopping.** A comps database whose newest row predates the recent window hasn't
   said the market went quiet — it has said it stopped being updated. The scan **refuses entirely**
   (`status: stale_data`) rather than reporting a market-wide collapse. This is the single most
   expensive lie the feature could tell, so it is a refusal, not a caveat.
3. **Missing dates.** `SoldDate` is free text and absent on a real share of rows. Coverage is
   measured, shown on a summary tile, and gates the verdict (≥6 dated comps and ≥60% coverage).
4. **Mix shift.** A cluster that quietly took in the Pro variant shows a price "rise" that is a change
   of product. Wide or widening dispersion demotes the reading to **tentative**, and the cluster has
   already been through `JackpotHunter.Screen` (accessories, lots, broken-item comps and too-wide
   clusters dropped) before it is measured at all.

### The load-bearing money rule

**Every buy number comes from TODAY's sold price.** `MaxBuyToday`, `TargetBuyPrice` and
`ProfitAtTarget` are computed from the estimator's current price through the same `ProfitCalculator`
+ `FeeProfile` as every other profit path in the app. The trend contributes exactly one number —
`MaxBuyIfTrendHolds` — and the gap between the two is reported separately as **upside on a buy that
already works**. The climb can never be the reason a buy is affordable.

The projection itself is clamped three ways: one window forward, **never compounded**, never above the
highest price anyone actually paid in the recent window, and never more than 1.5x.

### Reuse, not a second engine

| Borrowed | From |
|---|---|
| Category universe + seed rotation ("scan different categories") | `CategorySweep` (Roll the Dice) |
| Product clustering signature and the pre-pricing screen | `JackpotHunter.ProductSignature` / `.Screen` |
| Resale price, confidence, liquidity, days-to-cash | `AnalyzeProductAsync` → `ResalePricing` |
| Break-even and goldmine target buy price | `JackpotHunter.BreakEvenBuyPrice` / `.TargetBuyPrice` |
| Evidence bar for a green badge (≥5 comps, ≥50 confidence) | `LocalArbitrageAnalyzer` constants |
| Terapeak scrape rationing (cache-only pass, then ≤N real) | `LocalArbitrageAnalyzer.SelectScrapeTargets` |
| The two drop gates (thin history, sweep-vs-estimate disagreement) | Roll the Dice's board |

A product is the same product here as it is on the Roll the Dice board, and a "buy now" clears the
same bar a "goldmine" does. The genuinely new code is the time-series read, the corpus baseline and
the ranking.

**Ordering matters and is the cost control:** the trend read is free (arithmetic over comps already
fetched), so *everything* gets measured, and the expensive per-product pricing lookup is only spent on
the products that already showed a move.

### Files

| File | Change |
|---|---|
| `Models/PriceTrendModels.cs` | **New** — `TrendWindow`, `TrendPoint`, `PriceTrendReading`, `TrendRadarRow`, `TrendNicheOutcome`, `TrendCorpus`, `TrendRadarResult` |
| `Services/PriceTrendAnalyzer.cs` | **New** — `BuildCorpus`, `Measure`, `Judge`, `JudgeRow`, `SlopePerMonth` (Theil–Sen), `WeeklySeries`, `Detrend`, `TrendMultiplier`, `Rank`. Pure and clock-free — `nowUtc` is always passed in |
| `Program.cs` | `GET /api/trends/radar` + `ScanPriceTrendsAsync` (sweep → baseline → measure → price) + `BuildTrendRow` (the money) |
| `wwwroot/index.html` | "Rising Now" nav entry, `#trends-section` page, honesty footnote. `app.js?v=48`, `style.css?v=41` |
| `wwwroot/app.js` | `bindTrendRadar`, `runTrendScan`, `renderTrendScan`, `renderTrendCorpus`, `renderTrendSummary`, `renderTrendRows`, `trendRowHtml`, `trendSparkline`, `huntTrendProduct` |
| `wwwroot/style.css` | `.tr-*` — signal/verdict badges, tentative pill, gap-aware sparkline toned per signal |
| `ING eBay AutoLister.Tests/PriceTrendAnalyzerTests.cs` | **New** — 38 tests |

### Judgement calls worth knowing about

- **A weekly sparkline keeps its gaps.** Weeks with no sales break the line rather than being closed
  up; joining straight through them draws a steady seller out of an intermittent one.
- **The sparkline is toned from the server's signal**, never re-derived from first-vs-last in the
  browser — a squeeze whose newest week happened to be high would otherwise be drawn "rising green"
  while the sentence beside it said the opposite.
- **A flat trend line does not demote a rise.** A price that stepped up and held reads as a zero median
  slope; only a *falling* line contradicts a window-on-window jump. (Caught by a test that failed
  against the first, stricter version of this rule.)
- **Growth from zero is left null, not printed as +100% or infinity.** No baseline, no percentage.
- **Sales stopping is `cooling`, not `no data`** — a product that sold 12 last window and none since
  has told you something, and it is not that the data is missing.
- **"Find one" hands the product to Local Deals** with the same short keyword the server built, so the
  radar says *what* and the existing sourcing screens say *where*. No new scraping.
- **Seasonality is not modelled**, and the footnote says so outright: a product that climbs every
  November will show as climbing every November.

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **858 passed**, 0 failed, 0 skipped (820 pre-existing + 38 new) |
| Live endpoint, small scan (dev port 9371) | 16 comps, 0 products with enough dated history → honest "scan again" warning, no exception |
| Live endpoint, wide scan (10 niches x 3 probes) | 1,064 comps, 12 products measured, 2 rising, 10 priced, 6 dropped by the two gates; **corpus detrending fired for real** (+211.8% baseline removed, pushing relative velocities negative) and a genuine `supply_squeeze` was detected on a Snap-on drill |
| Real browser (Playwright, stubbed API) | Nav opens the page; 3 rows rank buy-now → get-in-early → watch; buy row gold-edged; sparklines drawn with gaps split into 5 polylines; tentative pill on the dispersed row; summary tiles; baseline banner; "Find one" prefills Local Deals with `nvidia geforce rtx 3080` |
| Real browser, **stale-data path** | A refused scan renders the "can't be read as a trend" banner, **hides the summary and renders no table at all** |
| Browser console errors | **None** |

Screenshot: `docs/screenshots/trend-radar.png`.

One wording bug was found by the live run and fixed in the code: the thin-evidence verdict said
*"The price is moving, but…"* on rows the scan had classified as **cooling** or unreadable — exactly
backwards in front of a product whose sales had stopped. Now worded from the evidence, with a test.

### Not verified

- **Against a populated hosted comps database.** The local `Marketplace.db` available here is thin
  (1,064 comps across ten categories, most clusters under the 6-dated-comp bar), so the *rows* in the
  browser check come from a stub. The measurement, the corpus guards and the refusal paths were all
  exercised against the real database; what is unproven is a full board of real rising products.
- **Terapeak-blended rows** — needs a connected Terapeak session. The rationing is the same code path
  Roll the Dice and Local Deals already use.

---

## 25. Days to Cash — ranking by how fast the money comes back (autonomous session, 2026-07-26)

Every board in this app ranked opportunities by **how much**. None of them said **how long**. A
seller working from one pot of cash doesn't care that a pallet flip nets $300 if the money is gone
until Christmas — the $45 flip that clears in two weeks buys the next three deals in the same time,
and until now the app had no way to say so and no way to sort by it.

The velocity was already measured (`LiquidityScoringService` → `SellThroughCalculator`), already
carried on `ResalePricing`, and — in the local ranking — explicitly thrown away. Now every priced
opportunity carries an expected **days to cash**, a **$/day** rate, and a speed tier, on all three
boards, with the same definition on each.

### Why this makes the seller money

Two rows the app used to rank in the wrong order, from the real numbers a live roll produced:

| Play | Net profit | Days to cash | $ per day |
|---|---|---|---|
| Antminer Z11 (fast mover) | ~$278 | **13 days** | **$21.43** |
| A fat-margin slow mover at 150 days to sell | more per unit | **158 days** | ~$2 |

Same $100 of working capital, one year: the 13-day flip turns **~28 times**, the 150-day one turns
**twice**. The bigger margin loses by an order of magnitude, and no profit column can show that.
That is the whole feature — *fast + profitable beats a bigger-but-stale margin.*

### Days to cash is the whole wait, not the time to sell

The honest number is not "how long until it sells" — a sale isn't money. `DaysToCashEstimator` adds a
fixed **8-day pipeline** to every estimate:

| Stage | Days | Why |
|---|---|---|
| Handling | 2 | pack it and get it to the carrier |
| Transit | 4 | typical ground delivery |
| Payout | 2 | eBay initiates payout after delivery, then it settles |

So an item that sells the day it's listed is still a **9-day** turnaround, and it says 9, not 1.

### The rules that keep it honest

- **No velocity evidence → `unknown`, never fast.** A product with no dated sold history gets a dash
  and sorts *last* in both velocity sorts (`SortableDaysToCash` → `int.MaxValue`). Defaulting it to
  anything would rank unmeasured products against measured ones.
- **Losers stay below winners in every sort.** A one-day route to a loss is not a fast flip.
- **A loss is never annualized.** The daily bleed is reported (`profitPerDay` goes negative); an
  "annualized ROI" on a loss states a rate of return that isn't one, so it stays null.
- **$/day is per row, not per product.** Two listings of the same drill at different asks share a
  velocity but recycle the seller's cash at different rates, and the column shows that.
- **Colour is by speed, never by size.** A $300 margin parked for 188 days renders in the danger tone
  next to a $45 one in green — the picture has to agree with the maths.

### Where it shows up

| Board | What's new |
|---|---|
| **Local Deals** (`/api/local/arbitrage`) | New **Days to cash** column (`15d` + `$3.00/day`), two new sorts — *Fastest profit ($ per day)* and *Days to cash* — an **"Only money back in 3 weeks"** filter, and a `fastCashCount` headline in the summary |
| **Roll the Dice** (`/api/opportunities/roll-the-dice`) | Speed badge on every play (`~13d to cash · $21.43/day`), a board sort control (Best play / Fastest profit / Days to cash / Net profit), the same 3-week filter, and `fastCashCount` in the summary |
| **Rising Now** (`/api/trends/radar`) | Days-to-cash + $/day under the target buy price — a climbing price is worth less if the money is stuck for five months getting it |

Sorting is client-side over the response already in hand (a re-sort must never re-run a multi-minute
scan), and the same sort name is sent on a *fresh* scan so the server returns it pre-ordered.

### Files

| File | Change |
|---|---|
| `Models/DaysToCashModels.cs` | **New** — `DaysToCashEstimate` |
| `Services/DaysToCashEstimator.cs` | **New** — pure/static: `DaysToSell`, `Estimate`, `SortableDaysToCash`, the pipeline constants and the speed bands (21 / 45 / 90 days) |
| `Models/LocalArbitrageModels.cs` | `DaysToSell`, `CashPipelineDays`, `DaysToCash`, `ProfitPerDay`, `CapitalTurnsPerYear`, `AnnualizedRoiPercent`, `SpeedTier/Label/Note` on the row; `FastCashCount` on the result |
| `Services/LocalArbitrageAnalyzer.cs` | `ApplyDaysToCash` in `Build`; `Rank(rows, sort)` with `SortByProfit` / `SortByFastestCash` / `SortByProfitPerDay` + `NormalizeSort`; days-to-cash as the tie-break in the money-first default |
| `Models/JackpotModels.cs` | Play-level speed fields (`DaysToCash` now means the whole wait); `ProfitPerDay` per source option; `FastCashCount` on the result |
| `Services/JackpotHunter.cs` | `BuildPlay` prices the wait on the live buy, or on the target buy where there's no supply yet; `Rank(plays, sort)` shares the local ranking's sort names |
| `Models/PriceTrendModels.cs` | Radar rows carry the same speed fields |
| `Program.cs` | `sort` on the two arbitrage endpoints and on Roll the Dice; `FastCashCount` on both results; `BuildTrendRow` prices the wait; both scan logs report it |
| `wwwroot/index.html` | Days-to-cash column, two sort controls, two filters, footnote explaining the 8-day pipeline. `app.js?v=49` |
| `wwwroot/app.js` | `SPEED_TIERS`, `perDay`, `daysToCashCell`; new comparators on both boards; dice board sorting; radar cell |
| `wwwroot/style.css` | `.speed-days` / `.speed-rate` / `.speed-badge` + the four tiers; dice toolbar select |
| `ING eBay AutoLister.Tests/DaysToCashEstimatorTests.cs` | **New** — 11 tests |
| `ING eBay AutoLister.Tests/LocalArbitrageAnalyzerTests.cs` | +8 tests (wiring, the three sorts, the honesty rules) |
| `ING eBay AutoLister.Tests/JackpotHunterTests.cs` | +3 tests, and the existing `DaysToCash` assertion updated to the new meaning |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **887 passed**, 0 failed, 0 skipped (865 pre-existing + 22 new) |
| Live `GET /api/local/arbitrage?...&sort=perday` | 200, pipeline ran end to end, `fastCashCount` present |
| Live `GET /api/opportunities/roll-the-dice?...&sort=perday` | 924 comps → 5 plays, **4 fast-cash**, board ordered $8.41 → $6.31 → $3.32 → $3.22 → $2.50 per day; `speedNote` rendered as a real sentence ("Cash back in ~13 days — $21.43 a day while it's tied up: about 28.1 turns of this money a year") |
| Real browser (Playwright, stubbed arbitrage response) | Default sort keeps the $300/188-day row first; **$/day sort promotes the $45/15-day row to #1**; days sort orders 15 → 60 → 188; the unmeasured row sorts last in both and shows a dash; the 3-week filter leaves "1 of 4 shown"; **no console errors** |

Screenshot: `docs/screenshots/days-to-cash.png`.

### Not verified

- **Terapeak-blended velocities** — needs a connected session. The velocity input is the same
  `ResalePricing` field every other board already consumed.
- **The 8-day pipeline is an assumption, not a measurement.** It is deliberately conservative and
  identical for every item; it is not read from the seller's actual handling time or eBay payout
  history (neither is available here). It shifts every row equally, so it cannot change a ranking —
  only the absolute number.

---

## 26. Buy-side negotiation — what to offer, and what to actually say (autonomous session, 2026-07-26)

Every pricing screen in this app worked the **sell** side: what to list at, when to cut, what to
offer a watcher. On a local pickup there is no sell-side lever at all — the flip is decided the
moment money changes hands in a driveway — and the app went quiet at exactly that moment. It would
tell a seller a drill was worth buying at $180 and then leave them to invent a number and a sentence
on their own.

Now every priced row on **Local Deals** carries the buy side: the number to open at, the number to
stop at, the counter-offer ladder, and three drafted messages anchored on the same sold comps that
priced the flip.

### Why the buy side is the cheap side

A dollar talked off the ask and a dollar added to the sale price are not worth the same thing:

| | Talk $55 off the buy | Add $55 of profit on the sell |
|---|---|---|
| What it takes | one polite message | raise the sale price **$63.40** (eBay's 13.25% takes the rest) |
| eBay's cut | **none** | $8.40 |
| When it lands | at the handover | after it sells, ships and pays out |
| Effect on how fast it sells | none | a higher price sells **slower** |

From the live run: a DeWalt DCD996 kit asking **$180**, netting **$114.55** after fees. Open at
**$125** and that becomes **$169.55** — the same flip, **+48% profit**, for one message.

Across a four-row board the summary line now reads *"**$279 more** if all 3 sellers took your
opening offer"* — stated as a ceiling, because nobody accepts every opening offer.

### The four numbers

Every number is borrowed, never re-derived. The break-even is the row's own `MaxBuyPrice`, already
costed by the shared `ProfitCalculator`/`FeeProfile`, and net profit at any other price is
break-even minus that price — **exact**, because net profit falls one dollar for every extra dollar
paid. So a whole ladder of counter-offers costs one subtraction each and cannot drift away from the
money columns beside it.

| | On a $180 ask, $295 break-even |
|---|---|
| **Open at** | $125 — low enough to leave room, high enough to get a reply |
| **A great buy** | $168.31 — `LocalArbitrageAnalyzer`'s own goldmine bar (75% ROI / $75 cash) |
| **Stop at** | $180 — the "worth the drive" bar (30% ROI / $25 cash), capped at their ask |
| **Break-even** | $295 — pay this and you worked for free |

The great-buy and worth-doing bars are the *same constants the board judges by* (`GoldmineProfit`,
`GoldmineRoiPercent`, and `SolidProfit`/`SolidRoiPercent`, promoted to public for this). A price this
calls "great buy" is a price that board would have badged a goldmine — there is not a second,
friendlier definition for the feature that does the talking.

### The rules that keep it from losing money

- **A lowball has a floor, and the floor is politeness.** Past 35% off, a stranger's offer stops
  reading as a negotiation and starts reading as an insult — and an insult gets **ignored, not
  countered**, which costs the whole deal rather than the difference. When the number that makes the
  deal work sits below that floor, the plan says so instead of drafting a message nobody will answer.
- **A deal that can't be made drafts nothing.** If even 35% off is above break-even, the verdict is
  `walk`, there is no offer price, and there is **no message and no button** on the row. A message
  you shouldn't send is worse than no message, because sending it is how a bad deal gets talked into.
- **On an already-great deal, the ask is made risk-free.** When the ask is already under the
  great-buy price the danger is losing it to the next person over $20, not overpaying. So the
  discount is asked for *and the asking price is accepted in the same message* — "would you do $100?
  If not, no problem, I'll take it at your $120 either way." There is no version of that where a
  $180 flip is lost over $20.
- **Concede once, in the middle.** The counter message steps halfway, not to the ceiling. Going
  straight to your maximum teaches the other side that your numbers move when pushed, and leaves
  nothing to give when they push again.
- **When the ceiling is their own ask, the last message is a yes.** Telling someone "$180 is as far
  as I can go" about their own $180 listing loses a deal that was already worth doing, for nothing.
  (Caught by the live browser run, not by a test.)
- **Thin sold history quotes no figure at a stranger.** Under 3 comps the draft cites nothing and
  leads on cash-and-pickup, and the opener is capped at 12% — without evidence there is no argument
  for a low number, only an assertion.
- **The ceiling is one number.** Said out loud rounded down ($230, not $230.76) and carried at that
  value through the tiles and the ladder. A table showing $230.76 beside a draft saying $230 is two
  limits on one screen, and the seller has to work out which is real.
- **A missing post date makes no staleness claim.** Craigslist publishes one, Facebook doesn't. No
  date means the argument simply isn't made — never that the listing is fresh.

### The drafts

The persuasive part is not the number, it's the **reason** for the number — and the reason is true:

> I've been looking at these for a while. Similar ones sell for around **$340**, but by the time fees
> and shipping come out that's roughly **$294.55** in hand, and it's **2 months or so** before that
> money actually turns up. So I have to be careful about what I pay up front.
>
> Would you take **$125**? That's cash, picked up, no messing you around.

Every figure there is real and already on the row. The drafts never invent urgency or a deadline,
never disparage the item to justify the number, and never mention noticing the seller's own price cut
(that stays in "your leverage", where it belongs — pointing it out is how a negotiation starts badly).
Short waits are left out entirely: "it takes about twelve days" is a detail, not an argument, and
padding the draft with it makes the parts that *are* arguments read like padding too.

Drafts render in editable text boxes and **Copy takes whatever is in the box now**, not the original.
A message that reads like a form letter gets treated like one. Nothing is ever sent for the seller.

### Files

| File | Change |
|---|---|
| `Models/NegotiationModels.cs` | **New** — `NegotiationPlan`, `NegotiationRung`, `NegotiationMessage`, `NegotiationRequest` |
| `Services/NegotiationAdvisor.cs` | **New** — pure/static: `BuyPriceAt`, `NetAt`/`RoiAt`/`ToneAt`, `RoundOffer`, the opening ladder, the five verdicts and every draft |
| `Services/LocalArbitrageAnalyzer.cs` | `ApplyNegotiation` on every priced row; `SolidProfit`/`SolidRoiPercent` made public |
| `Models/LocalArbitrageModels.cs` | `Negotiation` on the row; `NegotiableCount` + `NegotiationUpside` on the result |
| `Program.cs` | Board totals; new `POST /api/local/negotiate` for a deal found off-app |
| `wwwroot/index.html` | "Offer them" column, the negotiation modal, footnote. `app.js?v=50` |
| `wwwroot/app.js` | `NEG_VERDICTS`/`NEG_TONES`, `offerCell`, `openNegotiation`, the ladder and draft renderers, clipboard |
| `wwwroot/style.css` | The offer cell, the modal, tiles, ladder tones, message boxes |
| `ING eBay AutoLister.Tests/NegotiationAdvisorTests.cs` | **New** — 31 tests |
| `ING eBay AutoLister.Tests/LocalArbitrageAnalyzerTests.cs` | +5 tests (wiring, the unpriced row, the signals) |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **923 passed**, 0 failed, 0 skipped (887 pre-existing + 36 new) |
| Live `POST /api/local/negotiate` | 200, full plan: verdict, four numbers, 5-rung ladder, 3 drafts, all figures consistent with the fee profile |
| Live, walk case ($620 ask on a $400 item) | verdict `walk`, **0 messages**, no offer price |
| Real browser (Playwright, stubbed scan, **real server-built plans**) | Offer column renders `$690 / saves $210 / Take it`, `$125 / saves $55 / Haggle`, and `— / Walk` with **no button**; modal shows tiles, leverage, 4-rung ladder and 3 editable drafts; Esc closes; **no console errors** |
| Board summary | "$279 more if all 3 sellers took your opening offer" — the walk row correctly excluded |

Screenshot: `docs/screenshots/buy-side-negotiation.png`.

Two wording bugs were found by the live run and fixed in the code, both in the case where the ceiling
equals the seller's own asking price: the headline said "stop at $180" about a $180 listing, and the
final message refused a price that was already worth paying.

### Not verified

- **Against a real Craigslist/Facebook scan.** The plans in the browser check are real server output
  from the live endpoint, but the rows carrying them are stubbed — a real scan needs a logged-in
  Facebook session and minutes of scraping. The wiring from `LocalArbitrageAnalyzer.Build` is covered
  by unit tests against the same `MaxBuyPrice` the board displays.
- **Whether these drafts actually close deals.** The price ladder is arithmetic and is verified; the
  *wording* is a judgement call. The 15% base opener, the 35% politeness floor and the concession
  step are reasoned defaults, not measured conversion rates, and the app cannot see whether a message
  was answered.
- **The drafts are transparent about resale economics** ("by the time fees and shipping come out…").
  A savvy seller reading that knows they are dealing with a reseller. That is deliberate — it is the
  entire persuasive force behind the number, and the alternative is a draft that either lies or gives
  no reason at all — but it is a trade-off, and a seller who would rather not say it can edit the box
  before sending.

---

## 27. Money Made — the earnings tracker (autonomous session, 2026-07-26)

Sections 9–26 all answer the same shape of question: *what would this make?* Every one of them is a
forecast. Past the sale there was nothing — the app could tell a seller what to buy, what to charge,
what to mark down and what to offer, and then had no idea whether any of it had worked.

`GET /api/earnings` → the new **💰 Money Made** page, plus a running total on the dashboard.

### The number, and why it is smaller than it could be

**"You've made $X this month / $Y all-time with ING"**, from real completed sales: sale price, the
fee **eBay actually charged**, what the seller paid, net profit — with a profit-over-time chart,
best flips, and every sale line by line.

The whole feature turns on one decision. **A sale with no recorded cost basis contributes nothing to
the headline.** It is counted as a sale, its proceeds are reported, and it is listed separately with
what it *would* add. Counting proceeds as profit is what makes most "you've earned $X" dashboards
worthless, and it would have been trivially easy here — the connected account's 50 imported sales
carry $10,784 of proceeds after fees, and a dishonest version of this page would lead with that
number today.

Instead the empty state is the growth path: *"$10,784 after fees isn't counted above, because
there's no record of what you paid. Enter the cost and the total goes up by whatever you actually
made."* The number only ever grows by becoming true.

### Real fees, not the 13.25% everything else has to assume

The Sell Fulfillment API's Order resource carries **`totalMarketplaceFee`** — what eBay really
charged on the real sale. Every other money screen in this app works from a published-rate estimate
because eBay exposes no per-account fee API; this is the one place that doesn't have to. On the
connected account the measured rate came back at **10.6%** on a $2,100 sale, not 13.25% — a $56
difference on one sale that no forecast in the app could have known about.

Where eBay reports no fee, the estimate is used and **flagged per row**, and the honesty block states
the split: *"$1,176 of fees is eBay's own figure, across 41 of 50 sales."* That split is reported
over **fees**, not over profit — a sale can carry a measured fee and still contribute no profit
because its cost is missing, and reporting by profit made the page announce "$0 uses eBay's real
fees" on an account where every fee had been measured.

### One cost basis, typed once

Costs resolve through the existing `CostBasisStore`, so a cost entered in **Inventory Health** counts
the profit here, and a cost typed here gives Inventory Health a real break-even floor on the next
unit. Imported lines key on `legacyItemId`, which is the same listing ID the rest of the app uses, so
that join needs nothing from the seller. A per-flip `UnitCost` override exists for manual flips and
one-off corrections, and is cleared when the cost goes to the shared table — two copies of one number
drift apart the moment either is edited.

### What the calculator refuses to do

| Rule | Why |
|---|---|
| No cost basis → **no profit contribution at all** | The proceeds are known; the profit is not. Reporting one as the other is the lie this feature exists to avoid. |
| **Return and testing reserves are not charged** | Every forward-looking screen applies them, correctly — they price the risk of a return that hasn't happened. On a completed sale the refund column *is* the outcome, and charging both understates every sale that went fine. |
| Sales tax is excluded throughout | eBay collects and remits it; it never reaches the seller. `lineItemCost` is used rather than `total` for exactly this reason. |
| Unrecorded shipping cost on a **paid-shipping** sale → assumed equal to what the buyer paid | A pass-through nets to zero. The alternative books buyer-paid shipping as revenue against no cost, which is phantom profit on every sale. |
| Unrecorded shipping on a **free-shipping** sale → flagged and counted | There is no honest number to assume, so the row is counted at full value and the total says how many rows are flattered that way (41 of 50 on the connected account). |
| A partial refund **scales the fee back** proportionally | Charging the full fee on a refunded order invents a loss; waiving it entirely invents profit. |
| A cancelled order is worth **nothing**, not a loss of its costs | It is not a sale that made nothing. It is not a sale. |
| An **unpaid** order is not imported | A promise to pay is not money made. Counting it puts profit in the headline that can evaporate — and taking it back out later reads as the tracker being broken. |
| Average ROI is **capital-weighted** | A $2 buy returning 400% alongside a $1,000 buy returning 10% is an 11% portfolio, not a 205% one. The mean of the percentages reports the latter. |
| Month-over-month is **omitted** when last month was zero or negative | There is no meaningful percentage against an empty base, and "+∞%" is noise. |
| Best flips contains **only profitable** flips | See below — this was found by pointing it at real data. |

### Three defects the real-inventory run caught

The scan was pointed at the connected account's **50 real imported sales** rather than reasoned about
in the abstract:

1. **A trophy on a loss.** With no costs entered and one loss-making cost typed in, "🏆 Best flips"
   proudly ranked the *least bad loss* at −$557. Best flips now requires `NetProfit > 0`, and when
   nothing is profitable the second column changes job from "📈 Best returns" to "🩹 Sold below
   cost" — the losses are the most useful rows on the page, and hiding them would make this a
   highlight reel.
2. **A silent cascade.** Typing one cost moved the all-time total by **$19,447**, because that
   listing had sold twelve times and one cost basis priced all twelve. That is the correct answer,
   and it is a shocking amount of money to move without a word — the status line said "*that sale*
   lost $19,447". It now reports the scope: *"It also priced 11 other sales of the same item."*
3. **A truncated list that lied about itself.** The header read "50 sales with no record of what you
   paid" above 25 rows. It now says which 25.

### eBay's `lineItemCost` is the extended line total, not a unit price

Verified against the real orders rather than assumed: a 10-unit line reports `1499.90`, which divides
into a clean `$149.99` each. Reading it as per-unit would have understated revenue on every
multi-quantity sale **by the quantity**. Cost of goods is scaled by quantity to match, so both sides
of the subtraction are line totals. Now a test.

### Files

| File | Change |
|---|---|
| `Models/EarningsModels.cs` | **New** — `FlipRecord` (the stored sale), `FlipProfit` (its money), `EarningsSummary`, `EarningsMonthPoint`, `EarningsResult`, `EarningsImportResult`, `EbayOrderSummary`/`EbayOrderLineItem`, `FlipUpsertRequest` |
| `Services/EarningsStore.cs` | **New** — SQLite `flips` table beside `listing_cost_basis`. Unique index on `(order_id, line_item_id)`, field-level edits, and the validation that keeps a running total from being corrupted by a typo |
| `Services/EarningsCalculator.cs` | **New** — the money and the roll-up. Pure. Fee estimation routes through `ProfitCalculator` so there is one fee model in the app |
| `Services/EarningsImporter.cs` | **New** — order → flips, and the pro-rata fee allocation. Pure |
| `Services/EbayService.cs` | `GetOrdersAsync` + `ParseOrder` + `Amount` — the Sell Fulfillment order search. Uses the `sell.fulfillment` scope this app has always requested, so nobody has to reconnect |
| `Program.cs` | DI + `GET /api/earnings`, `POST /api/earnings/import`, `POST /api/earnings/flips`, `DELETE /api/earnings/flips/{id}`, `POST /api/earnings/cost`, and `BuildEarnings` so every mutation answers with recomputed totals |
| `wwwroot/index.html` | `#earnings-section` (hero, awaiting-cost block, stat tiles, chart, leaderboards, ledger, log-a-flip modal), `Money Made` nav entry, `#i-money` icon, `#dash-earnings` dashboard band. `app.js?v=51`, `style.css?v=42` |
| `wwwroot/app.js` | `bindEarnings`, `loadEarnings`, `importEarnings`, `renderEarnings*`, `renderEarningsChart`, `niceTicks`, `renderEarningsLedger`, `saveFlipCost`, `saveManualFlip`, `renderDashboardEarnings`, `renderEarningsSparkline` |
| `wwwroot/style.css` | `.er-*` and `.dash-earnings*` |
| `ING eBay AutoLister.Tests/EarningsCalculatorTests.cs` | **New** — 30 tests |
| `ING eBay AutoLister.Tests/EarningsImporterTests.cs` | **New** — 15 tests |
| `ING eBay AutoLister.Tests/EarningsStoreTests.cs` | **New** — 15 tests |

### The chart

One series, monthly net profit, so no legend — the card title names what is plotted. Columns capped
at 24px with a 4px rounded data-end **square at the baseline**, growing from a single zero line and
going **below** it when a month lost money, which is the one thing a "money made" chart has to be
able to show without arguing. Only the peak is directly labelled; the hover band (the whole column
slot, not the 24px bar) and a **Show as table** view carry the rest. Gridlines are hairline and
recessive, ticks round to clean numbers, and no text wears the data colour.

The dashboard band carries a 12-point sparkline in the same idiom — de-emphasised history, current
month in the accent — and **stays hidden until there is a non-zero total behind it**. A "$0.00
earned" banner on a fresh install is a worse first impression than no banner.

### Safety

- **Read-only against eBay.** Importing calls `GET /sell/fulfillment/v1/order` and nothing else.
  Nothing here lists, relists, reprices, publishes or messages anybody.
- **Re-importing is idempotent** — verified against the live account: a second import over the same
  90 days reported `0 added, 51 updated` with the totals unchanged. The natural way to use that
  button is to press it again, and an import that appended would inflate the headline every time.
- **The seller's own figures survive a re-import.** eBay stays authoritative for price, fee, refunds
  and title; costs and notes are the seller's and are carried across.
- Paging is capped (1,000 orders, 730 days) so one click can't become hundreds of requests.

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **983 passed**, 0 failed, 0 skipped (923 pre-existing + 60 new) |
| Live eBay import (dev port 9371) | **51 real orders**, `feesReportedByEbay: true`, $14,852.75 gross / $1,504.66 in real eBay fees |
| Re-import idempotency (live) | `added 0, updated 51`, totals identical |
| Refund handling (live) | Fully refunded → gross $0, fee $0. Partial ($33 sale, $31.34 back) → gross $1.66, fee $0.17 — scaled, not waived |
| Real browser (Playwright) | Hero, awaiting-cost block, 6 stat tiles, chart + table view, ledger (51 rows), inline cost entry (click **and** Enter), log-a-flip modal with live net preview, dashboard band + sparkline |
| Browser console errors | **None** |

**Test data cleaned up.** Three synthetic manual flips and two fabricated cost-basis rows created
during verification were deleted from the app database; the 50 genuinely imported eBay sales were
left in place, since that is the feature operating normally on the seller's own data.

**Not verified:** an account where eBay reports *no* `totalMarketplaceFee` (the estimate path is
covered by tests but not by live data), and profit figures against a seller who has actually filled
in their cost basis — the connected account has none, which is precisely why the awaiting-cost block
is the largest thing on the page today.

---

## 28. Deal Pipeline — what money is in motion, and what to do next (autonomous session, 2026-07-26)

Sections 9–26 forecast. Section 27 reports the past. **Nothing joined them.** A single flip existed
as four unconnected facts on four different screens — a goldmine row in Local Deals, a price paid
that lived only in the seller's head, a listing in the grid, and eventually an order line in Money
Made — and the thread between them was carried entirely by the seller's memory. Close the
Opportunity Finder tab and the deal was gone.

`GET /api/deals` → the new **🧭 Deal Pipeline** board, plus a **Money in motion** band on the
dashboard and a **＋ Track** column on the goldmine table.

### Why this makes the seller money

1. **It finds cash that has stopped moving, which nothing else in the app can see.** Inventory
   Health finds capital stuck in *live listings*. The money stuck **before** the listing — bought,
   in a box, never photographed — was invisible to every screen. The board reports it as a dollar
   figure with a clock on it: *"$170 has been sitting unlisted for 202 days. It earns nothing until
   it's up."* On the live run that prompt was generated from a real purchase date, unprompted.
2. **It types the cost basis once, at the moment it is actually known.** Reaching **Listed** writes
   the purchase price to the shared `CostBasisStore`, which is the one number eBay cannot supply and
   the one thing standing between the seller and a real profit figure. On the connected account,
   moving one deal to Listed priced a completed sale that Money Made had been carrying as
   uncountable — the all-time total went from **$0 to $186.99** with no number typed twice.
3. **It grades the app's own forecasts, in public.** Every closed deal is measured against the
   projection that justified buying it: *"Across 1 closed deal with a forecast, the app's projections
   came in 25% better than forecast — $187 realized against $150 projected."* No other screen can
   tell a seller whether the numbers they're acting on are worth acting on.
4. **It catches paying over the ceiling.** A deal bought above its `maxBuyPrice` is flagged with the
   overage. The Opportunity Finder computes that ceiling; until now nothing ever checked whether it
   was honoured.
5. **It credits the haggling.** Ask minus paid, per deal — *"Haggled $50 off the asking price —
   that's profit with no fee and no wait on it."*

### The rule the whole feature is built on

**A projection is never money.** Projected and realized profit are separate fields on the card,
separate totals in the summary, separate colours in the CSS (gold = forecast, green = banked), and
nothing anywhere adds them. The hero figure is deliberately the *least* flattering number on the
page — **capital at risk**, money that has actually left the bank — because it is the only figure
here that isn't an estimate. A dishonest version of this feature would lead with projected upside,
and it would have been the easy thing to build.

### What the calculator refuses to do

| Rule | Why |
|---|---|
| A one-unit deal claims **one** sale from a listing that has sold fourteen times | Attributing all fourteen reports a $300 flip as $4,200 of realized profit. Matching is capped at the deal's quantity, bounded to the deal's own lifetime, and a sale already claimed by an earlier deal is never claimed twice. |
| A **part-sold lot is not a closed deal** | Found by a test: two of four units sold auto-advanced the card to Sold and retired the other two units' capital, so $200 vanished from "at risk" while it was still in the garage. Only a fully sold deal moves. |
| A half-sold lot is **not graded** | A 2-of-10 partial measured against a 10-unit forecast reports an 80% miss on a deal going exactly to plan. |
| A sale with no cost basis contributes **no realized profit** | Same rule as Money Made, for the same reason — the proceeds are known, the profit is not. |
| Accuracy is **omitted** against a zero or negative forecast | There is no meaningful percentage there, only noise. |
| **Nobody is told to go and buy something the forecast says loses money** | The most useful thing a pipeline can do with a bad deal is fail to produce a prompt for it. |
| A card with nothing wrong with it gets **no prompt** | A board that nags about everything is one where the genuinely stuck $1,200 goes unnoticed. |
| A listing selling inside **its own** forecast window is left alone | A part that was always going to take four months is not overdue at day 46. Where the deal carries no forecast, 45 days applies. |
| Three days of grace before a stall is flagged | Photographing and writing a listing is real work; flagging the morning after the buy trains the seller to ignore the flag. |
| **Dropped** capital is spent, not at risk | A write-off is a settled loss. Leaving it in "money in motion" keeps reporting cash that can't come back. |
| A median cash cycle needs **three** closed deals | Below that it's one of the two numbers wearing a statistic's name. |
| The frozen forecast is **never re-run** | Re-pricing an old deal against today's comps would quietly rewrite history and make the accuracy figure above worthless. |
| Projected profit is **rebased** on the price actually paid | Net profit moves exactly one dollar per dollar paid — the identity behind `LocalArbitrageAnalyzer`'s max-buy price — so this is arithmetic, not a second forecast. The sale-side estimate is untouched. |

### Two defects the live run caught

The board was pointed at the connected account's **50 real imported sales** rather than reasoned
about in the abstract.

1. **A retroactively dated purchase reported zero days in stage.** Logging a January buy today
   measured the stall from the *click*, not the purchase — hiding the oldest, most stuck money on
   the board behind the newest card. `StageSince` now takes the date the stage was reached. That one
   deal went from "0 days, normal" to "**202 days, urgent**". Now a test.
2. **A part-sold lot auto-closed and hid its remaining capital** (above). Found by a test before it
   reached the browser; fixed in the calculator, not the test.

### Files

| File | Change |
|---|---|
| `Models/DealModels.cs` | **New** — `DealRecord` (the frozen forecast + what has happened since), `DealCard`, `DealAction`, `DealStageSummary`, `DealPipelineSummary`, `DealPipelineResult`, `DealUpsertRequest`, `DealStageChangeResult`, `DealStages` |
| `Services/DealStore.cs` | **New** — SQLite `deals` table beside `flips` and `listing_cost_basis`. Unique index on `(source, source_item_id)` so pressing Track twice updates rather than duplicating the capital; field-level edits so a partial write can't blank the frozen projection |
| `Services/DealPipelineCalculator.cs` | **New** — pure. Sale matching, the money, stage derivation, the flags and the ranked next actions. Computes no profit of its own: realized figures arrive already worked out from `EarningsCalculator` |
| `Program.cs` | DI + `GET /api/deals`, `POST /api/deals`, `POST /api/deals/{id}/stage`, `POST /api/deals/{id}/apply-cost`, `DELETE /api/deals/{id}`; `ApplyDealCostBasis` (the write into the shared cost table, and how many completed sales it just priced) and `BuildPipeline` |
| `wwwroot/index.html` | `#pipeline-section` (hero, do-this-next, stat tiles, 4-column board, add/move modals), `Deal Pipeline` nav entry, `#i-pipeline` icon, `#dash-pipeline` band, `Track` column on the goldmine table. `app.js?v=52`, `style.css?v=43` |
| `wwwroot/app.js` | `bindPipeline`, `loadPipeline`, `renderPipeline*`, `dealCardHtml`, `openDealForm`/`saveDealForm`, `openStageForm`/`saveStageForm`, `applyDealCost`, `deleteDeal`, `renderDashboardPipeline`; `trackArbitrageRow` and `trackCell` on the arbitrage table |
| `wwwroot/style.css` | `.dp-*`, `.dash-pipeline*`, `.fb-arb-track*`; `.fb-arb-table` min-width raised for the new column |
| `ING eBay AutoLister.Tests/DealPipelineCalculatorTests.cs` | **New** — 43 tests |
| `ING eBay AutoLister.Tests/DealStoreTests.cs` | **New** — 21 tests |

### Where the stages come from, and what each one costs

**Sourced** arrives from the goldmine table's `＋ Track`, carrying the forecast *and its basis*
("14 sold comps · High confidence · Fast") frozen at that moment — a projection with no stated basis
is impossible to argue with later, which makes it impossible to learn from. Rows with no sold
history get no Track button: there is nothing honest to freeze. **Bought** asks for the price paid
and the extras (gas, freight, parts) as two numbers, because that is how sellers know them.
**Listed** asks for the listing ID — the join that lets the sale find its way home — and writes the
cost basis. **Sold** happens by itself: an imported eBay sale moves the card, and the card says so
(*"moved by an imported sale"*) rather than pretending someone clicked it.

Nothing is written back to the stored stage on a read. The stored stage stays the record of what the
seller said; the derived one is what the board shows.

### Safety

- **Nothing here touches eBay.** No listing, relisting, repricing, publishing or messaging. The
  whole feature is the app's own SQLite database.
- Deleting a card **leaves the cost basis behind** — it belongs to the listing, other sales may
  already be priced by it, and removing a card off a board is not a statement about what an item cost.
- A stage move that will change a number on another screen **says so before it happens**:
  *"This will also record $170.00 as what this listing cost you."*

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **1,047 passed**, 0 failed, 0 skipped (983 pre-existing + 64 new) |
| Live lifecycle (dev port 9375, real database) | Track → re-track (no duplicate) → Bought ($150 + $20 extras → $170 at risk, $150 expected after rebasing) → Listed against real listing `278057369628` → cost basis written, **1 real sale priced**, card auto-advanced to Sold, **$186.99 realized** from eBay's own fee, variance **+$36.99 (124.7%)**, 177-day cash cycle, capital at risk back to $0 |
| Money Made cross-check (live) | All-time profit moved $0 → $186.99 off one cost the pipeline supplied |
| Error paths (live) | Blank title, bought-with-no-price and a missing deal id all return 400/404 with a plain-English reason |
| Real browser (Playwright) | Hero, do-this-next list, 6 stat tiles, 4-column board, card money rows, flags, add-a-deal modal with a live net preview, stage modal with prefills, dashboard band, and the empty state on a cleared board |
| Track column (Playwright, stubbed search) | 14 cells per row; the priced row offers `＋ Track` and the no-sold-history row correctly offers nothing; tracking posts the frozen forecast and the deal appears in Sourced with its basis intact |
| Browser console errors | **None** |

**Test data cleaned up.** The three deals and the one cost-basis row created during verification were
deleted; the database is back to 50 imported sales, 50 awaiting a cost, $0 counted profit, 0
cost-basis rows and 0 deals — exactly as found.

**Not verified:** a board with enough closed deals for the median cash cycle (needs three), and the
"Apply what you paid" button end-to-end in the browser — its endpoint was exercised live and returned
the correct message, and the rule behind it is covered by tests, but the connected account had only
one sale available to close and it was consumed by the lifecycle run above.

---

## 29. The Auction Sniper — buying on eBay to sell on eBay (autonomous session, 2026-07-26)

Every sourcing screen built so far sends the seller somewhere else to buy: Craigslist, Facebook
Marketplace, a pallet auction, a supplier file. Each one costs a drive, a negotiation, or a wait on
a stranger to answer a message. Meanwhile eBay's own auction format regularly closes items below
what the same item's fixed-price comps settle at, and the app already knew what those comps were.

`GET /api/snipes` → the new **🎯 Auction Sniper** board, plus a **Closing soon, under market** band
on the dashboard and a **＋ Track** button that hands the deal straight to the Deal Pipeline.

### Why this makes the seller money

1. **It is the shortest flip there is.** Buy at auction, relist at market — same marketplace, days
   apart, no drive, no haggling, no waiting on a reply. The resale side is priced by the very
   marketplace the buy happens on.
2. **It hunts what the seller already sells.** With no keyword, the watch list is built from their
   own completed sales in `EarningsStore`, grouped by product: *"You've sold 18 of these."* Those
   are the only products on eBay whose demand, shipping cost, condition grading and buyer the seller
   has first-hand knowledge of — and the ones where "I could have bought that for half of what I
   got" is money they have demonstrably left on the table before.
3. **It answers the only question a bidder has.** Not "is this a good deal" but **"what is the most
   I can bid"** — one number, entered once into eBay's own max-bid box, walked away from. It comes
   from `JackpotHunter.BreakEvenBuyPrice` and `LocalArbitrageAnalyzer`'s own "worth doing" bars, so
   a flip won at auction is judged by exactly the same arithmetic as one bought off Craigslist.
4. **Shipping comes out of the bid, not out of the profit.** Winning costs the bid *plus* the
   shipping, so a $40-shipping miner's ceiling is $40 lower. On the live run this was the difference
   between a $97 ceiling and a $32 one on two listings of the same machine.

### The rule the whole feature is built on

**A current bid is not a closing price.** A $12 auction with three days left is not a $12 item — it
is an auction that hasn't happened yet. Rows more than `PriceIsRealHours` (24) out are marked
`too_early` however good the arithmetic looks, contribute **nothing** to any total on the board, and
carry the ceiling forward as the number to come back with. This is the single defect that makes
naive "underpriced auction finders" useless, and refusing to score those rows is the reason the rest
of the board can be believed. A fixed-price listing is never too early: its price does not move.

The hero figures are deliberately the least flattering honest ones — *profit if you won every row at
your ceiling*, labelled in the tile itself as an upper bound that falls every time somebody bids,
next to the cash it would take to win them all.

### What the analyzer refuses to do

| Rule | Why |
|---|---|
| An auction more than 24h out is **not priced** | Its current price is not a price. See above. |
| The ceiling is **truncated to the cent, never rounded up** | $173.10 / 1.30 is $133.1538. Rounding it to $133.16 — or, as the UI first did, to a whole `$134` — gives away the last cent of the margin the ceiling exists to protect. Both the server and the max-bid column now use exact cents. |
| Profit at the ceiling sits **beside** profit at the current bid | Winning at your ceiling is the worst case of a bid you would actually place. Leading with profit at a current bid advertises money on a price that will move. |
| **Unstated shipping is not free shipping** | eBay's Browse API omits `shippingOptions` entirely on some listings. `ShippingStated` now carries that separately, the row is flagged, and the seller is told what an unknown $20 would do to the ceiling. |
| The **price floor does not apply to auctions** | "Nothing this cheap is the real thing" is right for a Buy It Now and exactly backwards for an auction, which legitimately opens at $0.99. Auctions are guarded on identity alone; a suspiciously cheap one is flagged to go and read the description, not silently dropped. |
| A row that has already **passed the ceiling** shows the overshoot | "$17 past it" in red, not a headroom clamped at zero. |
| Nothing is **auto-bid** | `EbayService.PlaceMaxBidAsync` exists and is called from nowhere in this feature. The app never spends the seller's money. |
| Only `snipe` rows reach the totals | A too-early row's apparent $161 of profit must not be added to anything. |
| Urgency, not profit, is the default sort | The biggest margin on the board is worth nothing if it closes while the seller is reading about a different row. A Buy It Now with no clock sorts *after* everything on one, never as "ending now". |
| The evidence bars are the **same ones the rest of the app uses** | `GoldmineMinComps` / `GoldmineMinConfidence`. Thin data cannot wear the snipe badge however large the arithmetic. |
| Risks are **listed, never scored** | A score hides the exact thing the bidder needs to go and look at. |

### Four defects the live run caught

The scan was pointed at the seller's real connected account and the live eBay Browse API rather than
reasoned about in the abstract. Three of the four were in the app's **shared** buy-side identity
guard (`JackpotHunter.IsPlausibleSupply`), which Roll the Dice uses too:

1. **"fit" as a bare verb walked straight through.** `CompatibilityMarkers` knew "fits" but not
   "fit", so *"Cooling Fan 4 pin **fit** Bitmain Antminer S19"* — $15, naming the brand and the
   model — was priced against a $148 machine. Now matched as a word rather than a substring, because
   "benefit " ends in "fit ".
2. **A whole class of component nouns was missing.** A keyword search for a miner returns fans, fan
   *speed controllers*, fan *simulators*, rubber *standoffs* and *hashboards* — all around $15, all
   naming the model. `fan/fans/controller/simulator/emulator/standoff/hashboard/riser/shroud/duct/
   harness/supply` joined the list. Rejections on one live scan went from 48 to 58 of 71 listings.
3. **Services were being priced as machines.** *"ASIC Miner Hosting Europe — Antminer S21"* costs
   **$1.00**, names the product exactly, and cannot be resold at all; nor can an *"Overclock Antminer
   S21 — adds 10-20%"* firmware listing. A `ServiceWords` check now rejects hosting/rental/firmware/
   overclock/repair/service listings, compared against the product's own title so a seller who really
   does flip service plans isn't locked out of their own market.
4. **An empty board blamed the wrong thing.** With no live auctions for a term, the warning read
   *"the sold-comps database has no history for them yet"* — sending the seller to mend a database
   that was never broken. The three failures (nothing live / nothing priceable / nothing underpriced)
   are now told apart and named separately.

### Files

| File | Change |
|---|---|
| `Models/SnipeModels.cs` | **New** — `SnipeCandidate` (the listing, the money, the clock, the verdict, the risks), `SnipeWatchTerm`, `SnipeSummary`, `SnipeScanResult` |
| `Services/AuctionSniperAnalyzer.cs` | **New** — the ceiling (`MaxBidFor`), the clock bands, `PriceIsRealFor`, the auction-aware identity guard, the verdicts, the risks, ranking, and the watch list built from the seller's own sales. Pure except `Build`, which routes every dollar through `ProfitCalculator` via `JackpotHunter` |
| `Services/JackpotHunter.cs` | The three identity-guard fixes above — shared, so Roll the Dice gets them too |
| `Services/EbayService.cs` | `SearchEndingSoonAsync` gains a `sortOverride` (BIN sweeps go cheapest-first, which is where an underpriced fixed-price listing actually is) and now reads `feedbackPercentage`, `legacyItemId`, `condition` and **whether shipping was stated at all** |
| `Models/ListingData.cs` | `ShippingStated`, `SellerFeedbackPercent`, `ItemId`, `Condition` on `EbayOpportunityItem` |
| `Program.cs` | DI + `GET /api/snipes`, `ScanSnipesAsync` (terms → one comp lookup per term → the guard → a budgeted per-item recheck), `SnipeHonesty` |
| `wwwroot/index.html` | `#snipe-section`, the `Auction Sniper` nav entry, `#dash-snipe` band. `app.js?v=53`, `style.css?v=44` |
| `wwwroot/app.js` | `bindSniper`, `runSnipeScan`, `renderSnipe*`, `snipeRowHtml`, the one-second countdown ticker, `trackSnipeRow`, `renderDashboardSnipes` |
| `wwwroot/style.css` | `.sn-*` and `.dash-snipe` |
| `ING eBay AutoLister.Tests/AuctionSniperAnalyzerTests.cs` | **New** — 69 tests |
| `ING eBay AutoLister.Tests/JackpotHunterTests.cs` | +5 tests pinning the guard fixes the live run found |

### What one click costs

Bounded on every axis, because a keyword board fans out fast: ≤8 terms, ≤50 listings per term per
format, **one** comp lookup per term (every result of a keyword search is nominally the same
product — 25 lookups for one answer is 24 wasted), ≤15 per-item rechecks, ≤10 real Terapeak scrapes,
and a cache hit never consumes the scrape budget. The recheck is ranked by profit at the ceiling
rather than by discount: the deepest discount on the board is routinely a $9 item, and a recheck
spent there is one not spent on the $200 one.

### Safety

- **Read-only against eBay.** `item_summary/search` and nothing else. Nothing lists, relists,
  reprices, publishes, messages or bids.
- Tracking writes only to the app's own SQLite, through the existing `/api/deals` endpoint.
- The same listing arriving under two terms — or as both an auction and a Buy It Now — is
  deduplicated on item id, so it cannot be counted twice in any total.

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **1,121 passed**, 0 failed, 0 skipped (1,047 pre-existing + 74 new) |
| Live scan, no keyword (dev port 9381, real account) | Watch list built from **real sales**: "antminer s19 95th…" *(You've sold 18 of these)*, "…s19k pro 120th" *(7)*, "…ks5 pro 21th" *(3)* |
| Live scan, typed terms | 121 live listings across 3 searches, 48 rejected by the guard, every surviving row priced and judged. After the guard fixes: 71 listings, 58 rejected, and **no accessory or service row left on the board** |
| Live money check | Two listings of the same miner correctly carried different ceilings ($97.03 / $57.03 / $31.85) purely from their own shipping cost; one row's ceiling came back **$0** because shipping alone exceeded the resale value — `pass`, which is the right answer |
| Real browser, real scan (Playwright) | Section, tiles, term chips, 8 rows, live clocks, 4 honesty lines, **no console errors** |
| Real browser, synthetic rows (Playwright) | All four verdict states, the risks lists, exact-cent ceilings, the red overshoot, Track on snipe/too-early rows only, bid-at times, and the dashboard band |
| The countdown | Ticks every second (`0m 42s → 0m 40s`) and flips to **ended** when a row runs out under the seller's eyes; the dashboard band drops the expired row |
| Track → Deal Pipeline (live endpoint) | Posts the ceiling as the ask with the basis frozen, the card lands in Sourced, and the status line reports `$133.15` to the cent |

Screenshots: `docs/screenshots/auction-sniper.png` (all verdict states) and
`auction-sniper-live.png` (a real scan).

**Test data cleaned up.** The one deal created by the Track check was deleted from the dev database;
`/api/deals` is back to 0 deals and $0 capital at risk.

### Not verified

- **A live `snipe` row.** Three real scans across six terms produced no auction priced under its own
  comps — every live miner was *above* comp value, which the board reported as `pass` rather than
  inventing a deal. That is the feature working, but it means the bid-worthy path is verified against
  the analyzer's own arithmetic (69 tests) and a synthetic browser payload, not against a real
  underpriced auction.
- **Whether a sniped buy actually wins.** The app cannot see the bidding and does not try to: it
  never places a bid, so the 20-second snipe time is advice about eBay's mechanics, not a scheduled
  action.
- **Non-ASIC categories.** The hosted comps database is this seller's own niche, so consumer terms
  price thinly or not at all. The watch list is built from whatever the seller has actually sold, so
  the board follows their inventory rather than needing new data.

---

## Where to Sell Highest — the venue that pays most, not the one you default to

### The money

Every pricing screen in this app answers "what is this worth?" with an **eBay** number, and then eBay
takes 13.25% plus the per-order fee plus the shipping label out of it. A local cash sale takes
**none** of that. So the venue showing the highest price and the venue handing over the most money
are routinely different venues, and until now nothing in the app ever said so.

On the seller's own default profile, a $200 item nets **$173.10** on eBay. The same item collected
locally at a $220 median ask — marked down to $198 because an ask is not a sale — nets **$198.00**.
That is **$24.90 a flip**, on an item already sourced, already photographed, already priced. No new
inventory, no new capital, no extra work: the money comes entirely from not paying a fee that a
different buyer would not have charged.

The screen answers three questions at once:

1. **Where does this net the most, right now?** eBay priced from real sold comps, Facebook
   Marketplace and Craigslist priced from live listings near the seller's zip, each costed by the
   seller's own fee profile.
2. **How much is the difference worth?** One number, on this one item, at the top of the screen.
3. **What would it take to beat eBay anywhere?** Every off-eBay venue carries the exact price it
   must fetch to match eBay's take-home — pure fee arithmetic, so it is answerable even for Mercari,
   which this app cannot see prices on at all.

### What it will not do

The comparison can move a seller off the marketplace their whole workflow lives on, so four rules
are enforced in the analyzer rather than left to the copy:

- **An ask is not a sale.** eBay's figure is what buyers *paid*; every off-eBay figure is what
  sellers are *asking* (no site outside eBay publishes sold prices). Local asks are marked down 10%
  before they compete, and the evidence word — `sold` / `asking` / `no price data` — is rendered on
  every card, never as a footnote.
- **Three listings minimum.** A venue with fewer matching listings is shown, and its number is
  shown, but it can never be crowned. Two hopeful listings priced at double the eBay comp lose to
  eBay, by rule.
- **A win has to be worth the move.** The gap must clear **both** $5 and 4% of the eBay take. A
  $6.90 edge on a $173 sale reads "about the same — stay where you are", not a recommendation.
- **Missing is not losing.** A source that is disconnected, expired or broken says which, and never
  appears as a venue that came last. Mercari is never given a price estimate at all.

Speed is reported the same way: eBay gets a real days-to-cash because it has dated sold history; a
local venue reports only the half it genuinely knows — the money arrives at handoff with no
ship-and-payout wait — and says plainly that how long it takes to find a local buyer is not something
this data can measure.

### Built on what was already there

No new fee math. Each venue is a **clone of the seller's own `FeeProfile`** with the four things a
venue actually changes overridden (who takes a cut, who ships, whether returns exist, whether a
processor is involved), run through the same `ProfitCalculator` as every other screen — so the eBay
column here cannot disagree with the eBay number anywhere else in the app. "The price that beats
eBay" reuses `NetProceedsCalculator`'s existing floor identity rather than re-deriving it. eBay is
priced by `AnalyzeProductAsync` (the Opportunity Finder / Local Deals pipeline, identity guard and
all); the other venues are searched through the same `ILocalSupplySource` registry Local Deals uses,
then filtered through `ComparableMatcher` so a nearby shelf does not price an $800 miner.

### Files

| File | Change |
|---|---|
| `Models/WhereToSellModels.cs` | **New** — `VenueOutlook` (price + evidence + itemised deductions + net + speed + verdict), `VenueCostLine`, `WhereToSellReport` |
| `Services/WhereToSellAnalyzer.cs` | **New** — the venue catalogue and their economics, the per-venue fee-profile derivation, the ask→sold haircut, the sample and materiality bars, the price-to-beat-eBay solve, ranking, verdicts and copy. Pure: no I/O |
| `Program.cs` | DI + `GET /api/where-to-sell`, `WhereToSellAsync` (one comps lookup + one search per source), `RelevantLocalPrices` (match filter + per-unit normalisation) |
| `wwwroot/index.html` | `#wts-section`, the `Where to Sell` nav entry, the footnote. `app.js?v=54`, `style.css?v=45` |
| `wwwroot/app.js` | `bindWhereToSell`, `runWhereToSell`, `renderWtsBanner` / `renderWtsWarnings` / `renderWtsVenues`, `wtsVenueHtml`. Uses `moneyExact` throughout — the cents are the entire argument on this screen |
| `wwwroot/style.css` | `.wts-*` |
| `ING eBay AutoLister.Tests/WhereToSellAnalyzerTests.cs` | **New** — 24 tests |

### What one click costs

One comp lookup (the item is one product, so one lookup answers it) plus one search per selected
local source, run sequentially because one of them drives a real browser. Terapeak stays opt-in per
request and is skipped entirely unless a session is saved. Read-only throughout: it searches and
compares, and posts, lists or sells nothing anywhere.

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **1,145 passed**, 0 failed, 0 skipped (1,121 pre-existing + 24 new) |
| Live endpoint (dev port 9344, real hosted comps + real Craigslist search) | Returned a complete report: eBay priced off **20 real sold comps** ($85.00 sale, $12.24 fees, $4.35 label → **$72.76** net, 11 days to cash), Craigslist searched live (`lasvegas.craigslist.org`, 0 matches → "no evidence", not "loses"), Facebook reported as not searched, Mercari carried **"Get $72.76 here and you match eBay's $72.76 take-home"** with no invented price |
| Fee identity, per venue | eBay $200 → $26.90 fees → $173.10; local pickup at the same take keeps the label, packaging and returns reserve at $0 while keeping the seller's own handling cost |
| Materiality rule | $6.90 on a $173.10 base → `too_close`; $29.40 → `move` |
| Two live findings, fixed | A loss was being narrated as *"-$727.24 of profit"* (now *"the best venue here still loses $726.90"*), and unpriced venues were rendering an "unknown" clock under an empty column (now no clock at all) |

### Not verified

- **A live local win.** The Craigslist search for the seller's own niche returned no matching local
  supply, so the "move off eBay" path is verified against the analyzer's arithmetic (24 tests) and
  the endpoint's real eBay half, not against a real nearby listing that beat eBay. Facebook
  Marketplace needs a saved session to contribute at all.
- **Off-eBay fee rates are published standard rates, not the seller's account.** Facebook's 5%
  shipped rate and Mercari's 0% seller fee come from `CrossListingFeeProfile`, which already
  documents them as estimates; every figure derived from them is labelled an estimate.
- **How fast a local sale actually happens.** Deliberately unmeasured — see above.

## Recover Lost Sales — relist + Second Chance Offers (autonomous session, 2026-07-26)

### The money

Every other screen in this app is about a sale that has not happened yet. This one is about sales
that **nearly happened and then quietly expired**. That inventory is invisible everywhere else: an
ended, unsold listing is not in the listings import (it ended), never reached Money Made (it never
sold), and on eBay's own Unsold page it comes with a Relist button and **nothing else** — no market
read, no cost basis, no reason it failed. So the platform's default behaviour is to put the price
that already failed straight back up, and fail again.

It is also the cheapest money in the business. The item is already bought, already photographed,
already written up and already being paid to store. On the seller's own account this scan found
**$20,128.20 of stock that was asked for and never sold**, sitting in two listings nothing else in
the app can see.

Two different kinds of money come back here, and they are worth very different amounts:

1. **A relist** is a second run at a maybe — with the price corrected by real sold comps instead of
   repeated.
2. **A Second Chance Offer** goes to somebody who publicly bid a specific dollar amount on this
   exact item and lost. That is the shortest distance in the whole app between one click and money
   in the account, so it outranks every relist on the board by rule.

### The part that isn't a relist button

The whole feature is the diagnosis, not the button. The scan asks *why* each one didn't sell and
answers it from the listing's own record, because the answers point in opposite directions:

- **Priced above market** — back up at the going rate. The price was the blocker; comps say by how much.
- **At market, watched, no sale** — a small step down. A queue of watchers is close, so it takes
  *less* of a discount, not more: the same ladder shape as Offers to Watchers, for the same reason.
- **At market, viewed, nobody saved it** — a sharper step. They saw the price and left.
- **At market, nobody watched, barely seen** — **relist unchanged**. The price was never the
  blocker; almost nobody found it. Marking this down is paying for a problem that isn't price. The
  relist is still right on its own — it buys a fresh run in eBay search, which is exactly what a
  listing nobody found needs — and the row says the real fix is the title and the first photo.
- **Already under market and still unsold** — never raised. The listing's own record contradicts the
  comps for this exact item, and raising it would act on the one reading the evidence denies.

That is the difference from the repricer, and it is structural: there, "no change" means do nothing,
because the listing is live. Here the listing is **down**, so "no change" still means relist.

### What it will not do

- **No relist price ever lands under the floor.** Break-even plus the profit the seller set in
  Fees & Costs, recomputed **server-side** from the stored cost basis on every send — never trusted
  from the browser. A listing that was under water the whole time it was up goes back up at the
  floor, and says so, rather than repeating the loss.
- **A Second Chance Offer is never priced above what the bidder bid** — eBay will not carry it — and
  never below the floor. When their bid is under the floor there is simply no price that works for
  both parties, and the row says that instead of offering something.
- **A masked bidder ID is never sent to.** eBay withholds bidder IDs on some responses; that is
  reported as unreachable rather than guessed at, in the analyzer *and* again in the endpoint.
- **A listing eBay has already relisted is never relisted again** — that is a duplicate on the site.
- **"eBay didn't say" is not "nobody looked."** Missing watcher and view counts produce a different
  recommendation and different copy from a genuine zero. The same rule applies to listing age below.
- **No profitable price means say so.** Where the floor sits above the market, the verdict is
  `underwater` and there is no relist — a relist there is a relisted loss.
- **A failed comp match never moves a price.** Lot listings and 300%+ gaps are marked
  not-comparable and the relist price is left alone.

### Built on what was already there

No fee math is re-derived. Break-even comes from the same `ProfitCalculator`/`FeeProfile` pair every
other screen costs an item with, the floor from `NetProceedsCalculator.MinimumOffer`, the charm
rounding from `InventoryHealthAnalyzer.Charm`, the scrape rationing from
`InventoryHealthAnalyzer.SelectScrapeTargets`, and the market read from `AnalyzeProductAsync` — the
same hosted sold-comps + Terapeak pipeline as the Opportunity Finder. A break-even is a break-even
whichever screen asks.

### Files

| File | Change |
|---|---|
| `Models/RelistModels.cs` | **New** — `EbayEndedListing`, `RelistCandidate`, `SecondChanceBidder`, `RelistSummary`, `RelistRecoveryResult`, and the request/result types for both write paths |
| `Services/RelistAnalyzer.cs` | **New** — the relist ladder, the "why didn't it sell" diagnosis, the floor binding, second-chance pricing, ranking, verdicts, bidder-lookup budgeting. Pure: no I/O |
| `Services/EbayService.cs` | `SendTradingAsync` (one shared Trading API call path), `GetUnsoldListingsAsync`, `ParseEndedListing`, `GetSecondChanceBiddersAsync`, `RelistListingAsync`, `SendSecondChanceOfferAsync` |
| `Program.cs` | DI + `GET /api/relist/recover`, `POST /api/relist/run`, `POST /api/relist/second-chance`, `ScanRelistRecoveryAsync` |
| `wwwroot/index.html` | `#relist-section`, the `Lost Sales` nav entry, both confirmation gates. `app.js?v=55`, `style.css?v=46` |
| `wwwroot/app.js` | `bindRelist`, `runRelistScan`, `renderRelistSummary` / `renderRelistRows` / `rlRowHtml` / `rlBiddersHtml`, `submitRelists`, `submitSecondChance` |
| `wwwroot/style.css` | `.rl-*` — only the two things this screen has that the others don't; the table is `.inv-*` reused |
| `ING eBay AutoLister.Tests/RelistAnalyzerTests.cs` | **New** — 37 tests |

### Why this is Trading-API-only

eBay's modern Sell APIs have **no** concept of an ended-unsold listing, **no** relist call, and
**no** Second Chance Offer at all. The entire surface is XML-only (`GetMyeBaySelling/UnsoldList`,
`GetAllBidders`, `RelistFixedPriceItem` / `RelistItem`, `AddSecondChanceItem`), which is a large part
of why so few tools touch it. No new OAuth scope is needed — `sell.inventory` already covers it —
and a token that predates a permission raises the same one-click reconnect message as elsewhere.

### What one click costs

One `GetMyeBaySelling` page set, one comp lookup **per distinct product** (not per listing), a
Terapeak scrape budget of 3 spent where the most money hangs on the answer, and one
`GetAllBidders` call per ended auction with bids — budgeted, biggest bids first. Every write path
previews by default and needs `dryRun:false` **and** `confirmed:true`.

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **1,182 passed**, 0 failed, 0 skipped (1,145 pre-existing + 37 new) |
| Live `GET /api/relist/recover` (dev port 9346, real eBay account) | `status=ok`, 2 real ended-unsold listings, **$20,128.20** asked and never sold, both correctly judged `relist_as_is` |
| Live `POST /api/relist/run` (dry run) | `preview: Would relist at $349.99 (was $379.99)`, `listedValue=$17,499.50` across 50 units — nothing sent to eBay |
| Live `POST /api/relist/second-chance` (dry run) | Masked bidder `b***r` **skipped** with the reason; real bidder previewed at their own $250 bid |
| Served assets | `relist-section`, nav entry, both gates, `bindRelist`, `.rl-bidders` all present at `v=55` / `v=46` |
| Comp-match guard, live | The $1,128.70 listing matched comps **653% away**; marked not-comparable and its price left alone rather than cut to $149.97 |

### One live finding, fixed

The first run reported both listings as having **"ran 0-1 days"**, which is not credible for
listings sitting in a 60-day unsold window. Cause: `StartTime` only means "when this listing went
up" on a **fixed-duration** listing. A Good Til Cancelled listing renews itself and reports the
start of its **last renewal cycle**, so subtracting it from the end date measures the renewal, not
the listing. Rendering that as "ran 0 days" about a listing that had been up for months is a
falsehood with a number on it, and it also risked the ended-early derivation wrongly labelling a
lost sale as "you ended this yourself" — which would drop it from the total it belongs in. Now the
start date is used only where it means what it says, and the age is reported as **unknown**
otherwise. Both real listings are GTC and now correctly carry no age at all.

### Not verified

- **A real relist and a real Second Chance Offer.** Both write paths are verified in dry run only —
  publishing to the live eBay account is outside what this session is allowed to do. The XML calls,
  the fee parsing and the error/permission handling are unexercised against eBay's live responses.
- **A real losing bidder.** The seller's ended listings are both fixed-price, so no ended auction
  existed to look bidders up on. The second-chance half is verified against the analyzer's rules
  (masked IDs, withheld bids, floor blocking, ranking — 11 tests) and the endpoint's own guards,
  not against a real `GetAllBidders` response.
- **Multi-page unsold lists.** Only two ended listings existed, so pagination is untested live.

---

## Aging-Inventory Rescue — getting the money back off the shelf (autonomous session, 2026-07-27)

### The money problem

Inventory Health (`b40b30e`) already finds listings the market has drifted out from under, and
prices them. But on an old listing it deliberately answers with **one capped step** and the line
*"re-run the scan after this one to go further"*. That is the correct answer for a repricer and a
useless one for dead stock, because it depends on the seller **coming back** — and not coming back
is precisely how inventory ages in the first place. A listing that needed a 40% cut got 35% of one,
and then sat there.

The second half of the problem has no answer anywhere in the app or in eBay Seller Hub: some items
will not sell alone at **any** price above their break-even, and the only way out of them is to
attach them to something that already sells.

Both are the same underlying loss. Capital parked in stock that is not moving cannot buy the next
flip, and unlike a bad price it is invisible — nothing in Seller Hub ever says *"$4,200 of your
money has been sitting on a shelf for four months."*

### What was built

`GET /api/inventory/rescue` — reuses `ScanInventoryHealthAsync` **whole** (so market price, cost
basis, break-even, floors and comp-match guards are computed exactly once, one way, app-wide) and
adds two things on top:

**1. A dated markdown ladder per stuck listing.** The whole plan is decided up front, while the
seller is looking at what the item is costing them, instead of one step at a time:

- Walks linearly from today's asking price to a **clearance target** — the lower of the comps' own
  quick-sale price and 85% of market — in evenly spaced drops, each dated forward.
- **Urgency sets the shape, never the depth.** Past 180 days (or a `dead_capital` verdict) it is 2
  steps 10 days apart; past 120 days, 3 steps at 14; otherwise 4 steps at 14. Both ladders finish at
  the *same* price — being old buys you speed, not a deeper discount (pinned by a test).
- **No rung of any ladder goes under the floor**, which is break-even raised by whatever profit or
  margin policy the seller set in Fees & Costs. Reuses `NetProceedsCalculator.MinimumOffer` and
  `InventoryHealthAnalyzer.Charm`, so a rescue price is costed identically to a local flip.
- Steps too small to change a buyer's mind (under 3% or under $1) are **dropped, not shipped**.
- Every step carries the date, the price, the cut, the listing's age when it lands, and the
  take-home at that price.

**2. Bundles.** Pairs each stuck listing with a fast mover from the same inventory:

- "Fast" is **evidence, never assumption**: units actually sold, or 3+ watchers, or measured
  days-to-sell inside 21. A stale item can never be the fast half — two slow movers in a box is a
  bigger slow mover.
- The slow half goes in at **its own clearance price** — the same number its ladder walks to, so one
  item never has two values. The gain over the ladder: the seller reaches that price *inside a
  bundle* without publicly cutting the standalone listing.
- Scored the only honest way: **against what actually happens today**, which is the fast item selling
  alone and the slow one continuing to sit. A bundle that nets less than that is **not suggested**.
- Guards: category must fit, a partner under 10% of the slow item's price is refused (a $4 cable
  does not pull a $900 miner), and **no listing appears in two bundles** — it can only be sold once.

### The numbers the seller sees

| Tile | What it is |
|---|---|
| Money stuck on the shelf | Real capital in listings past the age line, at cost basis where known |
| Oldest one / median age | How long this has been going on |
| Drops to make today | The work actually in front of them this morning |
| Cash back if the plans clear | Take-home at the last step — **and the profit given up to get it**, stated plainly next to it |
| Bundles found / Bundles add | Capital freed, and net (or revenue, without cost basis) over selling the fast item alone |

Every conditional figure is labelled conditional, the same posture as
`ProjectedNetIfRepricedSells`. Nothing is inflated: with no cost basis the board reports **added
revenue** rather than inventing a profit.

### Deliberate limits

- **Only the drop due today is ever sent to eBay.** The later rungs stay in the UI as a plan. This
  app is not running while it is closed, and scheduling a price change it cannot keep would be a lie
  with a date on it. The footnote says exactly this.
- **Applying reuses `POST /api/inventory/reprice`** rather than adding a second way to change a live
  price — so preview-by-default, explicit `confirmed`, and the server-side break-even re-check stay
  in exactly one place.
- **Bundles are advisory.** Nothing is published; the seller lists the pair by hand.
- A listing whose comps did not match, or that is underwater, or already at clearance, gets **no
  plan and an explanation** — never a markdown off a comparison that failed.

### Files

| File | Change |
|---|---|
| `Models/RescueModels.cs` | **New** — `RescueStep`, `RescuePlan`, `BundleSuggestion`, `RescueSummary`, `RescueResult` |
| `Services/AgingInventoryRescuer.cs` | **New** — the ladder, the clearance target, urgency, bundle pairing and pricing, ranking, totals. Pure except the shared `ProfitCalculator` |
| `Models/InventoryHealthModels.cs` | Carries `EstimatedDaysToSell` / `EstimatedMonthlySales` through, so a fast mover can be told from an unmeasured one |
| `Services/InventoryHealthAnalyzer.cs` | Populates the two new velocity fields from `ResalePricing` |
| `Program.cs` | DI + `GET /api/inventory/rescue` |
| `wwwroot/index.html` | `#rescue-section`, the `Rescue Aging Stock` nav entry, the confirm gate, an `inv-to-rescue` cross-link from Inventory Health. `app.js?v=56`, `style.css?v=47` |
| `wwwroot/app.js` | `bindRescue`, `runRescueScan`, `renderRescueSummary` / `renderRescuePlans` / `renderRescueCard`, `submitRescueDrops`, `renderRescueBundles` / `renderBundleCard` |
| `wwwroot/style.css` | `.rsc-*` — the plan card and the side-by-side bundle pair; tiles, bulk bar and confirm gate are `.inv-*` reused |
| `ING eBay AutoLister.Tests/AgingInventoryRescuerTests.cs` | **New** — 39 tests |

### One bug the tests found

The first version could skip the early rungs as too small and then leave the **first worthwhile drop
dated four weeks out** — the plan had already decided the cut was worth making and then sat on it.
The schedule is now pulled forward so the first surviving step is due today, with the rest keeping
their spacing behind it (`A_drop_worth_making_is_not_left_waiting_for_a_schedule_slot`).

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **1,221 passed**, 0 failed, 0 skipped (1,182 pre-existing + 39 new) |
| `node --check app.js` | Syntax OK |

The 39 tests pin the things that would cost real money: that **no step of any ladder goes under the
break-even floor**, that a listing which is actually selling units is never dragged into a rescue,
that both urgency levels finish at the same price, that a bundle is only suggested when it beats
what already happens today, and that no listing is promised to two different buyers at once.

### Not verified

- **A live scan against the real eBay account.** The board is verified by unit tests and a clean
  build only; no live `GET /api/inventory/rescue` was run this session, so the plan cards, bundle
  cards and summary tiles are unexercised against real listing data and real comp matches.
- **A real applied price drop.** The apply path reuses the already-shipped repricer endpoint, but no
  drop was sent to eBay from this board.

---

## Sourcing Budget Optimizer — spending the money, not just ranking it (autonomous session, 2026-07-27)

### The money problem

Every sourcing screen in this app ranks deals: Local Deals, Roll the Dice, the Auction Sniper, the
trend radar. Ranking answers **"which is the best deal"**. A seller standing at a cash machine with
$500 has a different question — **"which SET"** — and the two answers are routinely different.

The reason is arithmetic, not judgement. Buying down a ranked list takes the biggest profit first,
and the biggest profit is usually also the biggest price, so the top row eats the budget and
everything behind it becomes unaffordable. Three smaller flips that each make less can, together,
make more. Nothing in the app (or in eBay Seller Hub) solved that, so the seller was left doing it
by eye — which is the greedy answer, and the greedy answer leaves money on the table.

Verified live this session on a realistic pool: a $450 goldmine netting $300 is the top row, so
buying down the list spends $450 to make **$300**. The same $500 across two $250 deals makes
**$400**. That $100 was invisible before this screen existed.

### What was built

`POST /api/sourcing/budget` — an exact 0/1 knapsack over the deals the seller is already looking at.

**It re-prices nothing.** Candidates arrive already costed by the stack that costed them on the
board they came from (`LocalArbitrageAnalyzer` → `ProfitCalculator` → the seller's `FeeProfile`),
plus anything tracked at **Sourced** in the Deal Pipeline. A basket whose profit figures disagreed
with the table the seller was just looking at would be worse than no basket, so this only decides
which of those deals the money buys.

**Three definitions of "best", all solved every time** so the trade-off is visible rather than
argued — the seller picks, and the other two stay on screen with their numbers:

| Objective | What it buys |
|---|---|
| Most money | The largest total profit the budget can buy, whenever each piece lands |
| Fastest cash back | Only deals inside the 21-day `DaysToCashEstimator.FastCashDays` bar, then the most profit among those |
| Hardest-working cash | The most profit per day of tied-up capital — needs a measured speed, so unmeasured deals sit it out |

Live example of the trade being made honestly: **$260 net, all back Oct 30** versus **$165 net, all
back Aug 15**, from the same $350.

### The numbers the seller sees

| Tile | What it is |
|---|---|
| Buy these | How many deals, under which definition of best, out of how many were in play |
| Cash deployed | What actually leaves the wallet, and what stays in it |
| Net profit | Total after fees and shipping, with blended ROI on the cash put in |
| All your money back | A real date — **only when every pick has a measured speed** |
| Tied up for | Days-to-cash weighted by the **capital in each line**, not by line count, plus turns a year |
| Earning per day | What the whole basket earns per day of the wait, annualized at that pace |

Plus the line the whole feature stands on: **"+$100 more than buying straight down the list"**,
computed against the greedy basket rather than asserted — and it renders the tie honestly ("buying
straight down the list lands on the same money here") when there is no lift to claim.

### Deliberate limits, and the honesty rules

- **The basket can never cost more than the seller said they have.** The knapsack grid is in cents
  and item costs round **up** into it, so the rounding error is always spent on safety.
- **A held-back reserve is never touched.** Holding back everything answers "nothing to buy with"
  rather than dipping into it.
- **A lot is bought whole or not at all** — the basket never buys four of the six units to make the
  money fit, because that is not how the thing is sold.
- **The same post is never bought twice.** A deal tracked last week and scanned again this morning
  is one item; the live scan's price wins, and the merge is counted and reported.
- **Nothing thin gets the seller's cash by default.** Under 3 sold comps (the same `ThinCompCount`
  bar the arbitrage board uses) is out unless the seller ticks the box knowingly. The exception is
  a tracked deal carrying no recorded comp count: the seller put that on their own board, so the
  pick is **labelled** "tracked — frozen forecast" rather than overruled.
- **Unmeasured speed is never fast and never dead.** One unmeasured pick in the basket and there is
  no "all your money back by" date at all.
- **Asking prices, not hoped-for prices.** The negotiation upside is reported as a separate ceiling
  ("if all 3 sellers took your opening offer"), never folded into any profit total.
- **Nothing is bought, tracked, offered or sent anywhere.** The screen answers with a shopping list.

### Two things it says that nothing else in the app could

- **"Another $125 would buy $190 more profit."** Read straight off the knapsack's own value
  frontier — which is why deals priced just past the budget stay in the pool: unbuyable, but
  measurable. Beyond a stretch of the budget they are dropped as out of reach.
- **"You're $30 short of this one."** Every deal left out keeps its reason, and the near-misses
  sort to the top: `not_enough_left` (buy it next), `objective_excluded` (it's in the other basket),
  `crowded_out`, `thin_evidence`, `too_slow`, `loses_money`, `over_budget`.

### Files

| File | Change |
|---|---|
| `Models/SourcingBudgetModels.cs` | **New** — `BudgetCandidate`, `BudgetPick`, `BudgetPlan`, `BudgetComparison`, `BudgetStretch`, `BudgetSkip`, request/result |
| `Services/SourcingBudgetOptimizer.cs` | **New** — screening, dedupe, the exact knapsack, the three objectives, the greedy comparison, the stretch, plan totals. Pure |
| `Services/DaysToCashEstimator.cs` | `TierFor` and `MaxAnnualizedRoiPercent` made public — a basket bands its speed and caps its annualized return by the same rules one deal does |
| `Program.cs` | DI + `POST /api/sourcing/budget` + `TrackedDealCandidates` (Sourced deals, frozen forecasts, comp count read out of the stored basis line) |
| `wwwroot/index.html` | `#budget-section`, the `Spend My Budget` nav entry, and a `Spend a budget on these…` cross-link on the Local Deals board. `app.js?v=57`, `style.css?v=48` |
| `wwwroot/app.js` | `bindBudget`, `runBudgetPlan`, `budgetCandidateFromArbRow`, `renderBudgetSummary` / `renderBudgetLift` / `renderBudgetBasket` / `renderBudgetAlternatives` / `renderBudgetLeftOut` |
| `wwwroot/style.css` | `.bud-*` — the lift callout, basket table, alternative cards and the left-out list; tiles and speed pills are reused |
| `ING eBay AutoLister.Tests/SourcingBudgetOptimizerTests.cs` | **New** — 37 tests |

### What the tests pin

The knapsack is the only part of this app whose correctness is a mathematical claim rather than a
judgement call, so it is checked **against the definition**: one test brute-forces all 2^8 subsets
and asserts the basket equals the true optimum. The rest pin the money rules above — the budget is
never exceeded (including with awkward cent prices and a coarse large-budget grid), the reserve is
never spent, a lot is never part-bought, the same post is never bought twice, a losing or thin deal
never gets the cash, an unmeasured deal is never counted as fast, and **the claimed lift over
buying down the list can never be negative**.

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **1,258 passed**, 0 failed, 0 skipped (1,221 pre-existing + 37 new) |
| `node --check app.js` | Syntax OK |
| Live `POST /api/sourcing/budget` (dev port 9347) | `status=ok` — the $450-goldmine trap sprung correctly: **$400 net from $500** vs **$300** buying down the list, `+$100` lift reported |
| Live objective switch + reserve | `$50` held back → `spendable=$350`; fast-cash basket `$165 net, all back Aug 15` beside the profit basket's `$260 net, all back Oct 30` |
| Live left-out reasons | `not_enough_left` on the $450 miner, `thin_evidence` on the 1-comp synth, `loses_money` on the laptop lot, `objective_excluded` naming the basket each one did land in |
| Live empty pool | `no_candidates` with the sentence that says what to do about it |
| Served assets | `budget-section`, nav entry, `bud-plan-btn`, `fb-arb-budget-btn`, `bindBudget`, `.bud-lift` all present at `v=57` / `v=48` |

### Not verified

- **The tracked-deal fold-in against real pipeline data.** This machine's Deal Pipeline has no cards
  at Sourced, and seeding one would have written into the seller's real board. The mapping, the
  comp-count-from-basis parse and the scan-wins dedupe are covered by unit tests and by a live call
  with `includeTrackedDeals=true` returning cleanly, but not against a real tracked deal.
- **A basket built from a real local scan in the browser.** The endpoint is verified live with real
  request/response payloads; the rendering of the summary tiles, basket table, alternative cards and
  left-out list was verified as served assets, not by driving the UI against a live scan.

---

## Web Deal Scanner — retail/clearance arbitrage (autonomous session, 2026-07-27)

### The money problem

Every sourcing screen in this app assumed the seller has local supply. Local Deals searches
Craigslist and Facebook Marketplace by zip and radius; Roll the Dice, the Auction Sniper and the
Budget Optimizer all shop from what those found. That works right up until the seller's own city
has nothing — which, verified live this session, is most searches: a `dyson v11` scan of Las Vegas
craigslist within 40 miles returned **0 listings**.

The supply exists; it just isn't local. Slickdeals, DealNews and TechBargains publish what is
discounted at Amazon, Walmart, Best Buy, Costco and Woot right now, as public RSS — no account, no
key, no browser. The same `dyson v11` scan against those feeds returned **21 buyable deals**, at
prices set by a clearance shelf rather than by what the item still fetches used. That gap is the
whole trade, and nothing in this app could see it.

### What was built

`DealFeedService` — a fourth `ILocalSupplySource`, ticked on in the same picker, ranked in the same
table, priced by the same sold-comps → `ProfitCalculator` → `FeeProfile` stack. **Nothing
downstream changed**: grouping, comp lookups, the profit maths, `LocalSupplyMerger.TakeBalanced`,
`Rank`, the Deal Pipeline and the Budget Optimizer were already written against
`LocalSupplyListing`, so retail supply arrives as one more source and lands in one ranked list
beside the Craigslist rows. That is the payoff of the pluggable design, collected.

Six feeds, in `DealFeedCatalog` — the only file to edit when one moves:

| Feed | What it contributes |
|---|---|
| Slickdeals search | The only feed that honours `?q=` server-side — the seller's actual words |
| Slickdeals front page | The community's own filter: deals voted up enough to be promoted |
| Slickdeals clearance & closeout | End-of-line stock priced below what it still fetches used |
| DealNews today's deals | Editorially curated, and the only feed with **structured** price/retailer/deal-type |
| DealNews electronics | Depth where resale value concentrates |
| TechBargains | The deepest feed (hundreds live) and the most clearance-heavy |

### The two things retail costs that a cash pickup doesn't

Both are priced in, because leaving either out makes every row on the board flattering:

- **Sales tax.** `RetailBuyCosts`. A $200 clearance item at 7.5% costs $215, and on a flip netting
  $60 that tax is a quarter of the margin — worst exactly on the rows nearest break-even, which are
  the rows a verdict flips on. Cost basis, ROI and the verdict are all computed from the all-in
  figure. Deliberately **not** on `FeeProfile`: everything there applies to every item regardless of
  origin, and putting tax there would quietly start charging it on Craigslist cash buys too. The
  rate is the seller's, sent with the scan and remembered by the browser like the zip and radius.
  It defaults to 7.5% rather than 0, because 0 is a rate nobody actually pays.
- **Nobody to haggle with.** Retail rows carry **no** negotiation plan and contribute **nothing** to
  the board's `negotiationUpside` — money that cannot be won must not appear as money. What they get
  instead is `MaxBuyPrice`, corrected for tax: net profit falls `(1 + rate)` dollars per sticker
  dollar, so the untaxed `ask + profit` identity would name a shelf price at which the seller
  actually loses money.

`ILocalSupplySource.IsLocationBased` (a **default** member, so no existing source was edited) stops
the UI promising "within 40 miles of 89101" for a scan that searched the whole country.

### The parser's real job is refusal

A deal feed is advertising. Most entries are not one buyable object and many dollar figures in them
are not prices, so `DealFeedParser` drops anything it cannot read as "this item, for this amount" —
a guessed cost basis reaches the seller as a confident, badged, ranked number that is simply false.

Two of these were found **live, against the real feeds**, not imagined:

- **"…Laptop $499.99 +$14.99 Shipping"** parsed to **$14.99**. A $500 laptop with a $15 cost basis
  is a fabricated goldmine at the very top of the ranking — the one place a wrong number does the
  most damage. Anything added to a price is now never read as the price.
- **Posting conventions wrapped around the product** — `[Lightning Deal]`, a leading
  `$19.99* | `, `Woot! App: $155.99 | `, a trailing `Bestbuy.com` or `at Woot!` — all reached
  `ProductNormalizer` as part of the product identity, and a comp lookup for "Backpack at Amazon"
  matches nothing.

Also refused: gift cards, subscriptions, memberships, cruises, credit-card offers, "free w/
purchase", and category sale roundups ("Up to 60% off Tools, from $9" — that $9 belongs to something
unnamed). DealNews' own `dealType=sale` is trusted over any wording. A DealNews price of `0.00` is
"not stated", never free — the same rule Craigslist's rendered `$0` gets, and for the same reason.
**Refurbished and open-box are deliberately kept**: they are the best margins on the board.

### Deliberate limits, and the honesty rules

- **Never inflated.** Every ambiguity resolves toward a higher cost or no row at all.
- **A feed that fails, fails alone.** Verified live: TechBargains rate-limited mid-scan and the
  result came back `ok` with 21 real deals and one sentence naming the feed that didn't answer.
- **Never dead-ends.** Per-feed errors, a scan budget that returns partial results, a block-page
  detector for a challenge served with HTTP 200 (which parses to zero deals and would otherwise
  read as "nothing is on sale"), and a bounded read so a feed cannot exhaust memory.
- **No crawling.** One GET per feed per click, for the seller's own query, against the feeds these
  sites publish for the purpose. Nothing scheduled, nothing stored, no account, no key, no retries.
- **The same deal is not counted three times.** These aggregators repost each other, and the
  per-source id dedupe can't see it — one Amazon price is a Slickdeals thread, a DealNews page and a
  direct Amazon link, with three different ids.
- **A feed that searched is trusted; a firehose is not.** Slickdeals' server-side results get the
  same lenient treatment Craigslist's do (`FilterByRelevance`, which falls back rather than report a
  false empty); browse feeds must match every real word of the query, because there the input is
  hundreds of deals nobody asked about.
- **Nothing is bought.** The screen answers with a ranked list and a link.

### Files

| File | Change |
|---|---|
| `Services/DealFeedCatalog.cs` | **New** — the six feed URLs and nothing else. The one file to edit when a feed moves |
| `Services/DealFeedSelectors.cs` | **New** — every pattern, isolated for tuning: price vs. saving vs. shipping vs. "was", junk phrases, title conventions, block phrases |
| `Services/DealFeedParser.cs` | **New** — RSS across three dialects, price/retailer/coupon/image reading, junk refusal, query matching, cross-feed dedupe. Pure |
| `Services/DealFeedService.cs` | **New** — the `ILocalSupplySource`: six GETs, per-feed failure isolation, scan budget, bounded reads |
| `Services/RetailBuyCosts.cs` | **New** — sales tax, all-in cost, and the tax-corrected break-even sticker price |
| `Services/ILocalSupplySource.cs` | `IsLocationBased` default member + carried into `Describe()` |
| `Models/LocalSupplyModels.cs` | `IsRetail`, `Retailer`, `FreeShipping`, `CouponCode`, `LocationBased` |
| `Models/LocalArbitrageModels.cs` | `IsRetail`, `Retailer`, `FreeShipping`, `CouponCode`, `SalesTax`, `BuyCostAllIn` |
| `Services/LocalArbitrageAnalyzer.cs` | Retail branch: tax into the cost basis, tax-corrected `MaxBuyPrice`, no negotiation plan |
| `Program.cs` | DI registration + `salesTax` on `/api/local/arbitrage`, threaded to `analyzer.Build` |
| `wwwroot/index.html` | Panel retitled, sales-tax field (shown only when a retail source is ticked), `Deal` / `You pay` headers. `app.js?v=58`, `style.css?v=49` |
| `wwwroot/app.js` | `scopeTextFor`, `isRetailSource`, `buyCostCell`, `paidFor`, retail branch in `offerCell`, retail meta (store / list price / ships free / coupon code), all-in cost into the budget basket and the tracked deal |
| `wwwroot/style.css` | `.local-badge-dealfeeds`, `.retail-tax`, `.retail-code`, `.retail-tax-row` |
| `ING eBay AutoLister.Tests/DealFeedParserTests.cs` | **New** — 43 tests, mostly pinning refusal |
| `ING eBay AutoLister.Tests/RetailArbitrageTests.cs` | **New** — 21 tests on the retail money rules |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **1,332 passed**, 0 failed, 0 skipped (1,258 pre-existing + 74 new) |
| `dotnet test` × 3 more full runs | 1,332 passed each time — see the flake note below |
| `node --check app.js` | Syntax OK |
| Live `GET /api/local/sources` (dev port 9351) | `dealfeeds` present, `available=true`, `locationBased=false` |
| Live `GET /api/local/search?q=laptop&sources=dealfeeds` | `status=ok`, 12 deals, $19.99–$1,449.99, stores named (Amazon, Woot, Best Buy, Costco, Staples, Lenovo) |
| Live mixed scan `craigslist,dealfeeds` for `dyson v11` | craigslist **0**, dealfeeds **21** — the case the feature exists for |
| Live partial failure | TechBargains rate-limited → `status=ok`, 21 results kept, one sentence naming the feed |
| Live `GET /api/local/arbitrage?...&salesTax=8.25` | Tax applied to the cent ($56.96 + $4.70 = $61.66), ROI on the all-in, `maxBuyPrice=118.79` (vs. $123.89 untaxed), `negotiation=null` and `negotiableCount=0` on every retail row |
| Live thin-evidence gating | A 108% ROI / $66.93 row badged **thin**, not goldmine, on 1 sold comp — the min-comp gate holding |
| Served assets | `buyCostCell`, `scopeTextFor`, `retail — no haggling`, `.local-badge-dealfeeds`, `retail-tax-input` all present at `v=58` / `v=49` |

### Not verified

- **The rendered board in a browser.** The endpoint is verified live with real request/response
  payloads and the assets were verified as served, but the retail row (tax line, store, coupon
  chip, "retail — no haggling" cell) was not driven in the UI against a live scan.
- **A retail deal carried through to a tracked deal or a budget basket.** The all-in cost is wired
  into both and covered by unit tests, but no retail row was tracked onto the pipeline or put
  through the knapsack from a live scan.
- **Comp-match quality on long retail titles.** Live scans showed the existing `ComparableMatcher`
  pricing a $500 laptop against a $35.99 comp and a $269.99 Dyson V8 against $83.75 — pre-existing
  behaviour shared with every other board, and it errs **conservative** (those rows came back
  `pass` / `no_data`, zero goldmines, zero claimed profit), so it understates rather than inflates.
  Long, spec-heavy retail titles hit it harder than short classifieds titles do; worth its own pass.

### One flaky test, recorded rather than glossed over

`EarningsStoreTests.Different_lines_of_the_same_order_are_separate_sales` failed **once** across six
full runs of the suite this session, and passed on the other five plus in isolation. Nothing in this
change touches `EarningsStore` — the class gives every test its own GUID-named temp SQLite file, and
its `Dispose` calls the process-global `SqliteConnection.ClearAllPools()`, which is the plausible
cause when xUnit runs collections in parallel on Windows. Pre-existing, unrelated to the deal
scanner, and worth its own look rather than being quietly ignored.

## Going-Out-of-Business Finder — buying from businesses that are emptying themselves (autonomous session, 2026-07-27)

### The money problem

Every sourcing board in this app buys **one object from one seller**: a drill off Craigslist, a
clearance vacuum off a deal feed. All of them shop the same market — retail-adjacent supply, priced
by someone who wants a good price for it.

The cheapest stock in the world is not priced that way. When a shop closes, a warehouse clears its
returns or a company is dispersed, the seller's goal is an **empty building**, and the price is
whatever the room will pay on the day. That is where pallets go for the price of one item, and this
app could not see any of it.

### What was built

`LiquidationSourceService` — a fourth `ILocalSupplySource`, ticked on in the same picker, ranked in
the same table, priced by the same sold-comps → `ProfitCalculator` → `FeeProfile` stack. **Nothing
downstream changed**: grouping, comp lookups, `LocalSupplyMerger.TakeBalanced`, `Rank`, the Deal
Pipeline and the Budget Optimizer were already written against `LocalSupplyListing`, so a store
closing in Lindon, Utah lands in one ranked list beside the Craigslist rows.

Four search slices (`LiquidationCatalog`, the one file to edit when one moves): the seller's own
words, then `+ lot`, `+ pallet` and `+ closeout` to surface multi-unit stock. One word appended,
never a phrase — the site ANDs every token, so `headphones lot` returns a full page and
`lot of headphones` returns nothing at all because of the "of" (verified live).

### Why HiBid, and not liquidation.com

The obvious names were tried **first**, and all of them refuse to be read:

| Site | What it actually answers with |
|---|---|
| liquidation.com | HTTP **403** on every path, including the front page |
| B-Stock | **403** and a Cloudflare interstitial |
| Direct Liquidation | A Vue shell with no stock in it |
| GovDeals / AllSurplus | An Angular SPA behind an API key embedded in their own bundle |
| BidFTA | **429**, and its API host does not resolve |

Building a source on those would ship a permanently red chip. They are in the catalogue as
**`ManualSites`** instead — a prefilled one-click search the seller opens themselves, rendered under
the form. A smaller promise, and one the app can keep.

HiBid is where the closing businesses themselves list, and it publishes each search as
**server-rendered state**: a machine-readable island holding the lot, the current bid, the bid
count, the closing countdown, the auction house, the pickup city **and the buyer's premium**. That
last field is the reason this board can be honest at all — see below. No CSS selectors, so a
redesign cannot silently empty the board.

### The three things an auction costs that a shelf doesn't

All three are priced in, because leaving any of them out makes every row flattering:

- **The price is a bid, not a cost.** It is the floor. So the headline output is not "the profit at
  this price" but **the highest bid still worth making** — `LotAnalyzer.MaxAsk`, the same exact
  arithmetic the manifest analyzer has always used to answer "bid to here or walk", with the premium
  and the tax already taken out of it. Every verdict says where to stop, because a profit quoted
  against a price that is still climbing is only honest with a ceiling attached to it.
- **The buyer's premium, and tax on top of it.** A $100 bid at 15% + 8% is **$124.20** — charged
  through `LotAnalyzer.CostOf`, which already bills tax on hammer + premium the way an auction house
  does. A test pins that the identical item costs exactly $24.20 more at auction than off a
  stranger; a board that showed them level would be lying about a quarter of the margin.
  When an auction publishes **no** rate and prints no percentage, a premium is **assumed rather than
  waived** and the row says `(assumed)`: a published zero and an unpublished premium are
  indistinguishable in this data, and only one of the two possible mistakes buys a loser.
- **It may be several things.** A "Lot of 8" priced against one comp is wrong by **8×** in the
  direction that invents a goldmine. Lots are priced per unit through `LotAnalyzer.Grades` — the
  same recovery assumptions a pasted manifest gets — and the row reports **cost per sellable unit**,
  because "$240 for a pallet" means nothing until you know it is $6.83 an item.

### This is the Lot Analyzer, called rather than re-derived

`LiquidationLotPricer` writes no money maths of its own. It calls `LotAnalyzer.CostOf`,
`LotAnalyzer.MaxAsk`, `LotAnalyzer.Grades`/`Assumptions` and `LotAnalyzer.RetailSanityCheck`, plus
the shared `ProfitCalculator`. **A pallet found by this scan and the same pallet pasted into the
Liquidation Lot Analyzer cannot disagree about what it is worth** — including the "$4,200 retail
value!" cross-check, which refuses a comp several times the listing's own claimed retail because on
a lot that mismatch is multiplied by the unit count before it reaches the seller.

### The parser's real job is refusal — and here the stakes are multiplied

Every rule below was **measured against 801 live auction lots** pulled across eight searches, not
guessed. The counts are recorded in `LiquidationSelectors` beside each rule.

- **`bidAmount: 123.45` is a placeholder, not a price.** All **801** lots carried the identical
  value. Reading it would have given every row on the board the same invented cost basis — mildly
  flattering on the expensive lots and catastrophic on the cheap ones, where the real opening bid is
  $1. The real money is `highBid`, falling back to `minBid`, which is flagged as an *opening* bid
  because "nobody has bid yet" is the difference between a floor and a contest.
- **"Pallet" alone never implies a quantity.** Of the 37 lots whose titles contained the word,
  nearly all were pallet **jacks, forks, racks** and a pallet **shed** — single products named after
  the thing. Only "pallet **of**" counts, and even then a count nobody stated is refused with
  *"open the lot and count"* rather than invented.
- **`(N)` beats "Lot of N".** The bracketed form led **62** titles (7.7%) against **9** (1.1%) for
  the wordier one. Reading only the obvious form would have missed seven eighths of the multi-unit
  stock — and missing a count prices eight units as one.
- **"As-is" is not a refusal.** It appears in **56** lots (7%) as boilerplate stapled to everything;
  refusing on it would delete a large slice of a perfectly good board. "For parts" and "not working"
  appeared **once each** — said only when they are meant, and refused.
- **A bare "new" is graded as a shelf pull, not as factory-sealed.** 122 lots (15%) say it, and at
  an auction it describes the packaging far more often than it guarantees a seal.
- Also refused: **assorted / various / mixed** contents (78 lots, 9.7% — no single product to comp),
  multi-item titles ("(3) NASCAR Headphones, New Glue Gun, Small Tripod"), closed lots, floor-only
  lots with no internet bidding, and things **eBay does not allow at all** — firearms, ammunition,
  alcohol, vehicles, real estate, livestock.

### The evidence bar rises with the unit count

A single item's profit is one comp's worth of guess; a lot's is that guess **multiplied**. A 20%
comp error on one $60 item is $12 and on forty of them is $480 — so a lot must clear
`RequiredCompsForLot(units)`: *don't claim to know the market for N units from fewer than N observed
sales*, floored at the board's ordinary 5-comp goldmine bar and capped at 15, where the demand would
stop being meetable for any real product.

This started as "reuse `LotAnalyzer.GoodEvidenceComps`", and a test caught that being **exactly**
the board's existing 5-comp bar — i.e. no bar at all. Fixed rather than kept as decoration.

### Honesty rules kept

- **Never inflated.** Every ambiguity resolves toward a higher cost or no row at all.
- **A slice that fails, fails alone.** Verified live: HiBid rate-limited the scan and it came back
  naming all four slices, `retryable: true`, with a "wait a minute and scan again" sentence — never
  a dead end.
- **The radius is not quietly widened.** Auctions are far sparser than classifieds, so the seller's
  radius is a **floor** of 250 miles — and `MinRadiusMiles` (a new default interface member) makes
  the panel say "within 40–250 miles" instead of promising the 40 the form said. The same honesty
  `IsLocationBased` bought for the nationwide feeds.
- **`ChargesSalesTax` replaced an inference.** The tax field used to appear based on "is this source
  nationwide", which was only ever right by coincidence — a liquidation auction is **local and
  taxed**, and the inference would have priced it tax-free.
- **No haggling at an auction.** An auctioneer takes bids, not offers, so these rows carry no
  negotiation plan and contribute nothing to the board's `negotiationUpside` — money that cannot be
  won must not appear as money. What they get instead is the max bid.
- **The closing time is computed from the countdown**, not from the site's printed close time, which
  is written in the auction house's local zone with no offset on it.
- **A closeout row expires.** `closingSoonCount` headlines the profitable auctions closing inside
  48h, because one read on Thursday for a sale that ended Wednesday is a deal the seller never had.
- **No crawling.** One GET per slice per click, for the seller's own query, against the same search
  page a person would open. Nothing scheduled, nothing stored, no account, no key, no retries.

### Files

| File | Change |
|---|---|
| `Models/LiquidationModels.cs` | **New** — `LiquidationLotDetails` (what a lot is) and `LiquidationLotEconomics` (what it's worth) |
| `Services/LiquidationCatalog.cs` | **New** — the four search slices, the radius floor, and the walled sites offered as manual searches |
| `Services/LiquidationSelectors.cs` | **New** — every pattern isolated for tuning, each with the live count that justified it |
| `Services/LiquidationParser.cs` | **New** — the state island, unit counts, grades, premium, claimed retail, refusals, dedupe. Pure |
| `Services/LiquidationSourceService.cs` | **New** — the `ILocalSupplySource`: four GETs, per-slice failure isolation, scan budget, bounded reads, block detection |
| `Services/LiquidationLotPricer.cs` | **New** — the auction/lot money, entirely on top of `LotAnalyzer` + `ProfitCalculator` |
| `Services/ILocalSupplySource.cs` | `ChargesSalesTax`, `ManualSites`, `MinRadiusMiles` default members + carried into `Describe()` |
| `Models/LocalSupplyModels.cs` | `Liquidation` on the listing; `ChargesSalesTax` / `ManualSites` / `MinRadiusMiles` + `LocalSupplyManualSite` |
| `Models/LocalArbitrageModels.cs` | `Liquidation` on the row; `LiquidationCount` / `ClosingSoonCount` on the board |
| `Services/LocalArbitrageAnalyzer.cs` | `BuildLiquidation` branch; `ApplyResale` extracted so both paths share it; no negotiation plan on an auction row |
| `Program.cs` | DI registration + the two liquidation board counts |
| `wwwroot/index.html` | Panel retitled, manual-sites row, header notes. `app.js?v=59`, `style.css?v=50` |
| `wwwroot/app.js` | `chargesSalesTax`, `renderManualSites`, `liquidationMeta`, premium + per-unit lines in `buyCostCell`, max-bid branch in `offerCell`, closing-soon headline, honest `scopeTextFor` |
| `wwwroot/style.css` | `.local-badge-liquidation`, `.liq-event`, `.liq-units`, `.liq-closing-soon`, `.liq-per-unit`, `.manual-site-link` |
| `ING eBay AutoLister.Tests/LiquidationParserTests.cs` | **New** — 60 tests, mostly pinning refusal |
| `ING eBay AutoLister.Tests/LiquidationArbitrageTests.cs` | **New** — 24 tests on the auction and lot money rules |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **1,416 passed**, 0 failed, 0 skipped (1,332 pre-existing + 84 new) |
| `dotnet test` × 3 further full runs | 1,413–1,416 passed each time, 0 failures — no flakes seen this session |
| `node --check app.js` | Syntax OK |
| Live `GET /api/local/sources` | `liquidation` present, `available=true`, `locationBased=true`, `chargesSalesTax=true`, `minRadiusMiles=250`, 3 manual sites |
| **Parser over 8 real captured search pages** (1.6–2.2 MB each) | **661 lots parsed**; **0** priced at the `123.45` sentinel; 80 lots detected, 51 refused with reasons; 112 liquidation events flagged; 555 published premiums vs 106 assumed; 661/661 with a pickup location, 653 with an image, 604 with a close time |
| Live `GET /api/local/search?...&sources=liquidation` | `status=ok`, **37 lots**, $5–$275, correctly geo-filtered to Las Vegas / Boulder City NV / Youngtown AZ / Lancaster CA for zip 89101, real premiums (15%, 19.5%), real countdowns |
| Live `GET /api/local/arbitrage?...&salesTax=8.375` | 25 rows priced. Premium and tax to the cent: a $10 bid → $1.50 premium → $0.96 tax → **$12.46** all-in, ROI measured on that. `maxBuyPrice` (break-even bid) and `maxBidForTargetRoi` both returned; `negotiation=null`, `negotiableCount=0`, `negotiationUpside=0` on **every** row; `closingSoonCount=4` |
| Live thin-evidence gating | Rows at 1,775% and 1,109% ROI badged `solid` / `thin`, `goldmineCount=0` — the min-comp gate holding on a board where every bid starts at $5 |
| Live partial failure | HiBid rate-limited an earlier scan: came back naming all four slices, `retryable=true`, "wait a minute and scan again" — never a dead end |

### Two bugs the live run found, and fixed

- **`LOT OF (2) DeWalt Rotary Hammer Drills` was refused** as "no count stated", while stating its
  count perfectly clearly — `(N)` was only read at the *front* of a title. A bracketed count is now
  read anywhere **inside bulk wording** ("lot of", "pallet of"), where the context guarantees it is
  a quantity rather than a voltage or a size. Refusing cost coverage on a lot the seller could have
  bought.
- **"Bid up to $29.60" on a lot already standing at $37** — technically true, and an instruction to
  bid when the honest answer is to stop. A bid past the target now reads *"already past the $29.60
  that clears 40%. Let it go."*

### Not verified

- **The rendered board in a browser.** The endpoints are verified live with real request/response
  payloads and the assets were bumped and syntax-checked, but the liquidation row (premium line,
  per-unit cost, event badge, closing countdown, max-bid cell) and the manual-site links were not
  driven in the UI against a live scan.
- **A multi-unit lot carried all the way through a live scan.** The unit/grade/recovery path is
  covered by unit tests and was exercised against 661 real lots through the parser, but no live
  arbitrage scan in this session happened to return a priceable multi-unit lot with sold comps
  behind it — the `dewalt` board near 89101 was all single items.
- **A liquidation row tracked onto the Deal Pipeline or put through the Budget Optimizer.** The
  all-in cost flows into both through the existing `buyCostAllIn` path that the retail rows already
  use, but that was not driven end to end here.
- **Comp-match quality on auction titles.** Shared, pre-existing behaviour: live rows priced
  "S1 - DeWALT POWER TOOLS (G17?)" at $249.99 on 2 comps. It errs conservative (badged `thin`, no
  goldmines claimed), but auction titles carry lot codes and abbreviations that classifieds do not,
  and it is worth its own pass.

## Free / Free-after-coupon Finder — the flip with nothing on the line (autonomous session, 2026-07-27)

### The money problem

Every sourcing board in this app ranks by the gap between what an item costs and what it sells for.
All of them assume the first number is greater than zero: the Deal Scanner buys clearance, the
Liquidation finder bids on pallets, Local Deals haggles with a stranger. Each one asks the seller to
put money at risk before they make any.

There is supply that doesn't. Craigslist's free-stuff board in **one metro, verified live this
session, carried 186 posts** — a 65" LG smart TV, a Craftsman tool chest, a DeWalt circular saw, a
Yamaha grand piano, a Proform treadmill, a Samsung washer/dryer — all at $0, all collected in person.
Alongside it, retailers publish items that reach $0 after a coupon or a rebate. None of it was
visible to this app, and it is the only stock a seller with no cash at all can buy.

A free item cannot lose money. Its ROI has no ceiling. The only questions left are **whether it is
worth the trip** and **whether it is still there** — and both are answered on the row.

### What was built

`FreebieSourceService` — a fifth `ILocalSupplySource`, ticked on in the same picker, ranked in the
same table, priced by the same sold-comps → `ProfitCalculator` → `FeeProfile` stack. **Nothing
downstream changed**: grouping, comp lookups, `LocalSupplyMerger.TakeBalanced`, `Rank`, the Deal
Pipeline and the Budget Optimizer were already written against `LocalSupplyListing`, so a free oak
wall unit lands in one ranked list beside the Craigslist and clearance rows.

Two legs, because free supply lives in two very different places:

| Leg | What it contributes |
|---|---|
| **Craigslist free-stuff board** (`/search/zip`) | The best free supply there is: furniture, appliances, tools, exercise equipment. Local pickup, genuinely $0 |
| **Slickdeals × 5 slices** | The seller's own words + free, free-after-rebate, free-after-coupon, freebies, 100% off — nationwide, shipped |

The local leg is read **through `CraigslistService`**, not a second copy of it, so the headers
craigslist expects, the block detection, the RSS fallback and the search budget are the ones already
trusted. `CraigslistParser.BuildSearchUrl` gained a `category` parameter (defaulted to the existing
`sss`, so every existing caller is byte-identical) and rejects anything that isn't one of
craigslist's own short lowercase codes, because it goes into the URL path.

**The one board where a blank search is the best search.** `AllowsBlankQuery` (a new **default**
interface member, so no existing source was edited) lets the seller press the button with the
keyword box empty and get *everything being given away near them* — a seller shopping for free
things does not care what they are. On every other source a blank query is the whole classifieds
section, and they all still refuse it.

### The parser's real job is refusal, and here the bar is highest in the app

A $0 cost basis makes ROI unbounded, which means **anything the classifier lets through lands at the
very top of the profit ranking**. There is no board where a wrong answer is more visible.

`FreebieClassifier` is therefore almost entirely refusal, and every rule was **measured against live
data** — 186 craigslist free posts and 81 Slickdeals titles pulled this session — not imagined:

- **"Free" is usually attached to something other than the item.** Free *shipping* is the single
  commonest phrase on any deal feed; free *gift with purchase*, free *trial*, free *credit*, free
  *checked bag* and buy-one-get-one all followed. These are **stripped before** the free test runs
  rather than refused, because "Free 65in TV + free shipping" is a genuine freebie whose title
  happens to contain both.
- **Hyphenated compounds are not giveaways.** Found live and fixed: `BPA-free jugs` was read as free
  **and had the word cut out of its own title**, reaching the comp matcher as "BPA- jugs". Oil-free
  face wash and aluminum-free deodorant did the same. Every "free" pattern now carries a
  `(?<![A-Za-z]-)` guard.
- **Free because it is broken.** `FreeBecauseBroken` is deliberately wider than
  `LiquidationSelectors.ForPartsOnly`, which was tuned narrow against auction catalogues. A free
  board is the opposite: damage is the commonest *reason* something is being given away and people
  say so plainly — "Damaged panel/screen", "good for parts", "Repair or Project", "Scrap
  Metal/Parts", all live. The first is a **75" television that would otherwise have been priced
  against sold comps for televisions that work**.
- **A pile is not a product.** "Cleaning out garage and have a lot of free stuff", "Free stuff pile",
  "Curb Alert — Help Yourself", and a post titled just "Free". All genuinely worth driving to, none
  of them priceable: there is no one item to comp, so the row would be a confident number about
  nothing.
- **Free and worthless.** Bulk materials and haul-away were the largest single slice of the live free
  board — firewood, dirt, drywall, scrap metal, pallets, moving boxes, railroad ties, used tires.
  Plus groceries and toiletries from the deal feeds (a scan came back with mustard, deodorant, cat
  litter liners and peanuts), digital goods eBay won't let anyone resell, livestock, and replicas
  ("Free knock off yeezy", live — priced against genuine comps it reads as the best flip on the
  board).
- **"Giveaway" is not "sweepstakes".** Refusing the word would have deleted "Giveaway
  instruments — Charles Walter Piano". The refusal is on *entering something* ("enter to win",
  "sweepstakes", "raffle"), not on giving something away.
- **An expired offer is not an offer.** Expiry dates are parsed out of the title — "exp 7/19/26",
  "ex 7/11", "(Valid 7/10 Only)" were all live — and a dead one is dropped. A year-less date resolves
  to whichever side of today it is nearer, which is what makes an already-dead offer detectable.
- **The free-stuff board's posts are free without saying so.** "Oak wall unit" on that board is a
  free oak wall unit; most of those 186 posts never used the word. `freeBoard: true` makes a missing
  price mean free rather than unreadable — without it the parser threw away most of the best supply
  this feature has.

### Free is not free, and the three costs that survive the word

`FreebiePricer` writes the cost basis. Passing zero would have been easy and wrong:

- **Sales tax survives a rebate.** A $49.99 item with a $49.99 mail-in rebate is **not free**: the
  register charged tax on the full $49.99 and the cheque only ever covers the price. At 7.5% that is
  $3.75 gone for good. A test pins that the identical item is worth exactly **$11.25 less** bought on
  rebate than found on a kerb.
- **A rebate is a claim, not a discount.** 15% of it is held back as a **reserve, not a forecast** —
  stated as policy rather than as an invented denial statistic: the row only earns its verdict if it
  still works when roughly one claim in seven is never paid.
- **The wait is real.** A mail-in rebate holds the money ~8 weeks after the item has already sold, so
  it is added to the days-to-cash figure the whole board ranks by (`DaysToCashEstimator.Estimate`
  gained an optional `extraPipelineDays`; every existing caller passes nothing and is unchanged).
  An app-paid rebate (Venmo, PayPal, Ibotta — named on the listing) waits days instead.
- **Fronting too much isn't a freebie.** Above $100 out of pocket it is a loan, and it belongs on a
  board that ranks by capital at risk. Refused.

### Two verdicts the arithmetic alone would have got wrong

Because ROI is unbounded, **every unflagged free row clears the goldmine bar on return alone** and
the badge would stop distinguishing a free 65" television from a free mouse mat. `JudgeFreebie`
lowers verdicts and never raises one:

- **An unstated delivery cost on a $0 item is the entire cost basis.** A "free" thing with $18
  shipping is an $18 item, and the listing didn't say. Capped, with the reason on the row.
- **Freight-sized stock priced by parcel comps.** The sold history behind a free sofa was set by
  people who did not post it in a box. Kept — a free treadmill is real money — but capped, and told
  to sell it locally.
- **Free does not make a $6 flip worth a Saturday.** Fetching, photographing, listing and packing
  cost the same hour whatever the item cost to buy.

### A bug this board turned from rare into normal

Live output read: `$736.46 net after fees (79228162514264337593543950335% ROI)`. That is
`decimal.MaxValue` — the sentinel standing in for "no cost basis" — leaking out of the comparison it
exists for and into a sentence. Pre-existing (a free Craigslist row could always hit it), but this
board makes the zero-cost case the normal one rather than the rare one. `Judge` now says
"it cost nothing to buy" and "Even at nothing, this doesn't clear eBay's cut", and a test pins that
the 29-digit number never reaches a sentence again.

### One post, one row

A free craigslist post is on the for-sale board **and** the free-stuff board, and the two arrive
under different source ids — which the per-source `(source, id)` dedupe cannot see. `LocalSupplyMerger`
gained a cross-source URL dedupe: one post at one address is one thing to go and collect. Verified
live on a mixed scan — freebies returned 4 rows, 2 survived, zero duplicate URLs on the board.

### Shared rather than copied

`PublicFeedHttp` was extracted from `DealFeedService` when this became the second source reading the
same feeds off the same sites. Two copies would eventually disagree about what a 403 means.

### Honesty rules kept

- **Never inflated.** Every ambiguity resolves toward a higher cost or no row at all.
- **A leg that fails, fails alone.** No zip? The local board is skipped with one sentence and the
  nationwide feeds still answer — verified live, `status=ok`.
- **Never dead-ends.** Per-feed errors, a scan budget returning partial results, block-page
  detection, bounded reads.
- **Nothing is bought, and nothing is haggled.** A free row carries no negotiation plan and
  contributes nothing to the board's `negotiationUpside` — there is nothing to talk down. What it
  gets instead is the deadline.

### Files

| File | Change |
|---|---|
| `Services/FreebieCatalog.cs` | **New** — the craigslist free category, five Slickdeals slices, two manual sites. The one file to edit when one moves |
| `Services/FreebieSelectors.cs` | **New** — every pattern, isolated for tuning: the four kinds of fake "free", the broken detector, bulk/consumable/digital refusals, expiry, bulky, offer noise |
| `Services/FreebieClassifier.cs` | **New** — is it free, what kind, how long is left, and the title cleanup. Pure |
| `Services/FreebiePricer.cs` | **New** — unrefundable tax, the rebate reserve, the refund wait, the verdict caps |
| `Services/FreebieSourceService.cs` | **New** — the `ILocalSupplySource`: two legs, per-leg failure isolation, scan budget |
| `Services/PublicFeedHttp.cs` | **New** — one GET against a public feed, extracted from `DealFeedService` and now shared |
| `Models/FreebieModels.cs` | **New** — `FreebieKinds`, `FreebieUrgency`, `FreebieDetails`, `FreebieEconomics` |
| `Services/ILocalSupplySource.cs` | `AllowsBlankQuery` default member + carried into `Describe()` |
| `Services/CraigslistParser.cs` | `category` on `BuildSearchUrl`/`BuildResult` (defaulted, validated); `freeBoard` on both parsers |
| `Services/CraigslistService.cs` | `SearchCategoryAsync`; the existing `SearchAsync` delegates to it |
| `Services/DealFeedParser.cs` | `requirePrice` (defaulted true) — a freebie has no price |
| `Services/DealFeedService.cs` | ~90 lines of HTTP replaced by `PublicFeedHttp` |
| `Services/DaysToCashEstimator.cs` | `extraPipelineDays` (defaulted 0) for the rebate wait |
| `Services/LocalArbitrageAnalyzer.cs` | Freebie branch: real cost basis, `JudgeFreebie` caps, no negotiation plan; unbounded-ROI wording fixed in `Judge` |
| `Services/LocalSupplyMerger.cs` | `DedupeByUrl` across sources |
| `Models/LocalSupplyModels.cs` | `Freebie`, `AllowsBlankQuery` |
| `Models/LocalArbitrageModels.cs` | `Freebie`, `FreebieCount`, `FreeMoneyOnTheTable`, `ExpiringTodayCount` |
| `Program.cs` | DI registration (ahead of the paid sources) + the three board counts |
| `wwwroot/index.html` | Panel retitled, blank-query hint. `app.js?v=60`, `style.css?v=51` |
| `wwwroot/app.js` | `allowsBlankQuery`, blank-query gates, `freebieMeta`, freebie branches in `buyCostCell` / `offerCell`, free-money headlines |
| `wwwroot/style.css` | `.local-badge-freebies`, `.free-kind`, `.free-price`, `.free-clock*`, `.free-caveat`, `.fb-blank-query-hint` |
| `ING eBay AutoLister.Tests/FreebieClassifierTests.cs` | **New** — tests that mostly pin refusal, mostly on live titles |
| `ING eBay AutoLister.Tests/FreebieMoneyTests.cs` | **New** — what free still costs and what the board may claim |
| `ING eBay AutoLister.Tests/FreebieSourceTests.cs` | **New** — the source, the craigslist category and the dedupe |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **1,514 passed**, 0 failed, 0 skipped (1,416 pre-existing + 98 new) |
| `node --check app.js` | Syntax OK |
| Live `GET /api/local/sources` | `freebies` present, `available=true`, `locationBased=true`, `chargesSalesTax=true`, `allowsBlankQuery=true`, 2 manual sites |
| Live blank-query scan (89101, 40mi) | `status=ok`, **137 free items** — 127 local pickups + 10 online |
| Live priced board (`/api/local/arbitrage`, blank query) | 134 found, 25 priced, **$1,364.57 of free money**, **18 gone today**, **0 goldmines** (every candidate capped or gated) |
| Live verdict notes | "$160.46 net after fees, and it cost nothing to buy. First come, first served — …" — no sentinel, deadline on every row |
| Live bulky cap | Free dresser/sofa/toilet rows carry "the eBay comps behind this price assume it fits in a box" and are held at `solid` |
| Live no-zip scan | `status=ok`, local leg skipped with "no zip code, so the local free-stuff board wasn't searched" |
| Live mixed scan (`craigslist,freebies`) | 163 rows, **zero duplicate URLs** — the cross-source dedupe holding |
| Live refusals confirmed absent from results | damaged TV, firewood, dirt, pallets, curb alerts, rooster, free food, knock-off yeezys, Kindle books, BOGO footlong, mustard/deodorant |
| Served assets | `freebieMeta`, `local-badge-freebies` present at `v=60` / `v=51` |

### Not verified

- **The rendered board in a browser.** The endpoints are verified live with real request/response
  payloads and the assets were verified as served, but the free row (the kind chip, the ⏱ clock, the
  ⚠ caveat, the rebate "up front / back in ~56d" line) was not driven in the UI against a live scan.
- **A rebate row end to end from a live scan.** The rebate arithmetic is covered by unit tests and a
  live scan did classify a real free-after-rebate deal, but no rebate row was carried through to a
  tracked deal or a budget basket.
- **Comp-match quality on short free-board titles.** A live scan priced a free "Dresser" against an
  **$850** sold comp — pre-existing `ComparableMatcher` behaviour shared with every other board.
  Short classifieds titles ("Dresser", "tool box", "Fridge") give the matcher almost nothing to work
  with, and this board has more of them than any other. It errs toward caution here — the row was
  capped to `solid` by the bulky rule and told to sell locally — but it is worth its own pass.
- **A misspelled refusal word gets through**, by design and pinned by a test: the live post said
  "Railroad Tries". The row survives, finds no comp for a misspelling, and is reported as "no sold
  history" rather than becoming a fabricated goldmine — the honest failure mode.
- **Product names containing "Free".** "Tide Free & Gentle" reaches the comp lookup as "Tide &
  Gentle". The hyphen guard fixed the large class (BPA-free, oil-free, aluminum-free); the
  space-separated case is rarer and unsolved.

---

## Coupon / Promo-Code Stacker — cutting the buy price before the profit is computed (autonomous session, 2026-07-27)

### The money problem

Every sourcing board in this app works the sell side: what an item resells for, what eBay takes, how
fast the cash comes back. The buy side had exactly one tool — `NegotiationAdvisor`, which drafts an
offer to a private seller — and it does not work on a retail row, because nobody at Walmart is
reading your offer.

That leaves the entire retail half of the Deal Scanner with no buy-side lever at all, and the buy
side is where the cheaper dollar is:

> **A dollar taken off the buy price is worth more than a dollar added to the sale price.** eBay
> takes none of it, nothing has to ship for it, and it lands today rather than after a sale. On a
> $200 clearance item flipped for $320, a 20% code is **$43** — the code's $40 plus the $3 of sales
> tax that was sitting on top of it — and that $43 is the difference between "thin margin for the
> drive" and a deal worth doing.

Those codes are already published, for free, by the same aggregators this app already reads for
clearance. Nothing in the app looked at them.

### What was built

A coupon lookup and a stacker, wired into the existing pipeline rather than beside it:

| File | What it owns |
|---|---|
| `Services/CouponCatalog.cs` | Every store name, every list URL, the manual (blocked) sites. The only file to edit when one moves |
| `Services/CouponSelectors.cs` | Every pattern, isolated for tuning — the same posture as `DealFeedSelectors` |
| `Services/CouponParser.cs` | Feed entry to `CouponOffer`. Pure; the clock is passed in |
| `Services/CouponStacker.cs` | The best legal combination at one price, and the cost basis it leaves. Pure |
| `Services/CouponService.cs` | The HTTP half: per store, budgeted, cached 30 min, never throws for a site's benefit |
| `LocalArbitrageAnalyzer.ApplyCoupons` | Re-runs the same flip through the same `ProfitCalculator` at the discounted cost |

Three public lists are read (through `PublicFeedHttp`, so the headers, the block detection, the byte
ceiling and the failure sentences are the ones already trusted): two Slickdeals store searches and
the DealNews front page. **RetailMeNot, Coupons.com, Rakuten and TopCashback answer an automated
request with a block page**, so they are offered as prefilled per-store links instead — the seller
opening RetailMeNot is the feature working, not a fallback. The cashback portals are additionally
link-only because their rates are account-specific and change daily; a rate read an hour ago and
printed as money is a number nobody can be held to.

### The rule the whole feature rests on: a public code is a claim, not a price

The row's own `NetProfit`, `RoiPercent`, `BuyCostAllIn`, `Verdict` and `MaxBuyPrice` are **not
touched**. The coupon numbers land in a separate `CouponSavings` block beside them.

This is the difference between a useful feature and a dangerous one. A public code may be dead,
regional, category-limited or new-customers-only, and nothing short of checking out can test it. If
the row's own profit were quietly recomputed at the discounted cost, that claim would sit underneath
the **ranking**, the **verdict badge**, the **goldmine count** and the board's **profit total** — so
one dead code would promote a deal that does not exist to the top of the table. Pinned by a test
(`ACouponCannotMoveARowUpTheTable`).

What the seller gets is both numbers: what this makes at the shelf price, and what it makes if the
code works — with the code, its conditions, its deadline and its confidence grade printed beside it.
The same posture `NegotiationAdvisor` takes with an offer nobody has accepted yet.

### The stacking rules, which are mostly refusals

1. **One code per order.** Every checkout takes a single promo code, so two 20% codes are not 40%.
   The best single one is applied; the rest are shown as alternatives. If the deal's advertised price
   *already* needs its own code (`LocalSupplyListing.CouponCode`), **nothing stacks on it at all** —
   the seller cannot type two.
2. **A discount with no code is already in the shelf price.** A sitewide sale needing no code is what
   the deal feed's price already reflects; subtracting it again would discount the item twice.
3. **A code posted against one deal cannot discount a different item.** *The most important rule, and
   it came from the live feeds rather than from imagination.* A "Newegg promo code" search returned
   **22 of 25 entries carrying a code**, and nearly every one read `"$40 off when you apply promo
   code LUSF2737 at checkout = $189.99"` — bound to that one motherboard. Only order-wide codes
   ("sitewide", "your entire order", "$50 off $250") may cut a cost basis; item codes are surfaced in
   the store lookup, labelled *that deal only*, and never reach a row's stack.
4. **Cashback is a rebate, not a discount.** It is paid by a portal, on what was actually spent, weeks
   later — so it stacks with a code, but 15% is held in reserve and the ~60-day wait is stated.
   Shares `FreebiePricer.RebateReservePercent` rather than inventing a second opinion about the same
   risk.
5. **Tax follows the discount.** A retailer's code lowers what the register rings up, so it saves its
   face value *plus* the tax that would have sat on top. The one place this file is generous, and it
   is generous because it is true.
6. **Credibility bounds.** Percent above 50% is a clearance headline, not a sitewide code. An ungated
   "$100 off" beside a $120 item is "$100 off $1,000" with the threshold written somewhere the parser
   couldn't reach. "Up to 40% off" is a range whose top applies to one item in one department, so it
   carries no value at all.
7. **Mentioning a store is not being sold by it.** Live, and it was *every* result for one store: a
   Lenovo code search returned Amazon listings for "140W power bank for Lenovo", each with a working
   Amazon code. Attribution now runs through `DealFeedParser.ReadRetailer` — the same reader the deal
   board uses — so the two can never disagree about which shop a row is bought from.

### Confidence, and where it shows

Nothing public is graded `high` by default. A code earns it by naming no conditions, stating a
deadline that hasn't passed, and having been published recently. Exclusions ("select styles", "new
customers", "military only") hold a code at `low` — the seller's item may well qualify, and this app
has no way to know which half of the catalogue it is in, so it says so instead of guessing. A stack
is only as trustworthy as its weakest part, and a low-confidence stack prints *"Treat this as a lead
rather than a price"* beside its own money.

### Where it appears

- **Every retail row on the Deal Scanner**: a code chip in the row meta, the discounted all-in cost
  under "You pay", `+$43 with the code` under Net profit, and `only profits with the code` on a row
  that loses money at the shelf price.
- **The scan summary**: *"$128 more if the codes work on 3 of them · 1 only works with a code"*, kept
  out of `TotalPotentialProfit` for the reason above.
- **A per-store block**: which stores were checked, what each had, and the four manual links per
  store. Present even when nothing was found, because *"we checked Amazon and Amazon doesn't take
  typed codes"* is an answer and an empty column is not.
- **Coupon check** — a standalone lookup in the same panel for the item a seller is looking at in
  another tab, since most buying happens outside this app. `GET /api/coupons?store=&price=&salesTax=`.

### Cost of one click

One lookup per **store**, not per row — thirty Amazon deals are one read — capped at six stores per
scan, biggest-money-first so the cap drops the store with $30 on it rather than the one with $900.
Answers are cached 30 minutes per store, so re-running a scan with a different keyword costs nothing.
Nothing is scheduled, crawled or stored; a promo code is somebody else's publication, not this app's
data. `coupons=false` on the endpoint turns the whole thing off.

### Files touched

| File | Change |
|---|---|
| `Models/CouponModels.cs` | **New** — `CouponOffer`, `CouponStack`, `CouponSavings`, `CouponLookupResult`, `CouponStoreOutcome`, kinds and confidence |
| `Services/CouponCatalog.cs` | **New** — 36 stores with aliases, 3 readable lists, 4 manual sites per store |
| `Services/CouponSelectors.cs` | **New** — every pattern, isolated |
| `Services/CouponParser.cs` | **New** — entry to offers, and the refusals |
| `Services/CouponStacker.cs` | **New** — the stacking rules and the cost basis |
| `Services/CouponService.cs` | **New** — per-store lookup, cache, per-list status |
| `Services/LocalArbitrageAnalyzer.cs` | `Build` takes optional coupons; `ApplyCoupons` re-prices through the same `ProfitCalculator` |
| `Services/DealFeedParser.cs` | `Decode`, `StripHtml`, `ReadDate` made public so the coupon parser reuses them rather than copying |
| `Models/LocalArbitrageModels.cs` | `Coupons` on the row; `CouponedCount`, `CouponSavingsOnTheTable`, `CouponRescuedCount`, `CouponStores` on the result |
| `Program.cs` | DI, `CollectCouponsAsync` / `CouponsForListing`, the board counts, `GET /api/coupons`, `coupons=` on the scan |
| `wwwroot/index.html` | Coupon-check panel, per-store block. `app.js?v=61`, `style.css?v=52` |
| `wwwroot/app.js` | `couponMeta`, `couponProfitLine`, coupon line in `buyCostCell`, `renderCouponStores`, `bindCouponCheck`, `renderCouponLookup`, summary headlines |
| `wwwroot/style.css` | `.coupon-chip*`, `.coupon-extra`, `.coupon-cost`, `.coupon-rescue`, `.coupon-stores*`, `.coupon-check*`, `.coupon-offer*` |
| `Tests/CouponParserTests.cs` | **New** — tests that mostly pin refusal, several taken from live entries |
| `Tests/CouponStackerTests.cs` | **New** — the cost basis, rule by rule |
| `Tests/CouponArbitrageTests.cs` | **New** — that the row's own numbers never move |

### Verification

| Check | Result |
|---|---|
| `dotnet build` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test` | **1,571 passed**, 0 failed, 0 skipped (1,514 pre-existing + 57 new) |
| `node --check app.js` | Syntax OK |
| Live `GET /api/coupons?store=Kohls&price=250&salesTax=7.5` | `ok`, 12 offers, applied `GET20` 20% — **$250 to $200, $215.00 all-in against $268.75** — note states the $3.75 of tax saved *and* the "lead rather than a price" caveat |
| Live `GET /api/coupons?store=Newegg` | 12 offers, **every one item-only, nothing banked** — the rule doing its job on the store that motivated it |
| Live `GET /api/coupons?store=Amazon` | 8 offers plus the "Amazon almost never takes a typed code" note, discount 0 |
| Live `GET /api/coupons?store=Lenovo` | 12 wrong-store offers **before** the attribution fix, 2 after |
| Live `GET /api/local/arbitrage?q=dewalt&sources=dealfeeds` | 27 found, 12 priced, 4 stores checked (2/3/8/0 offers), per-row stacks attached, **0 fabricated discounts** |
| Feed URLs probed live | Slickdeals store search OK; DealNews front page OK; DealNews `search.html?rss=1` **204** and `/s<id>/?rss=1` **301** — dead, replaced |
| Served assets | `coupon-check`, `renderCouponStores`, `couponMeta` present at `v=61` / `v=52` |

### Not verified

- **The rendered board in a browser.** Endpoints and served assets are verified with live payloads;
  the coupon chip, the `+$43 with the code` line and the coupon-check panel were not driven visually.
- **A banked code end to end through the Deal Pipeline or the Budget Optimizer.** Both read the row's
  own figures, which are deliberately unchanged, so a tracked deal is frozen at the shelf price. That
  is the correct conservative behaviour, but a seller who *did* use the code will see a forecast that
  understates what they made.
- **An item-specific code matched to the row it belongs to.** The clearest remaining money: when a
  coupon entry's code is bound to the same product a board row is for, that code is real and usable.
  It is refused today because matching it needs the product matcher, and a wrong match here is a
  fabricated discount. This is the obvious next pass.
- **Cashback end to end.** The arithmetic (reserve, wait, applied to the discounted subtotal) is unit
  tested, and the parser reads a rate when a list states one, but no live entry in this session
  published a cashback percentage — the portals themselves are link-only by design.
- **Whether an applied code actually works at checkout.** Untestable without checking out, which is
  precisely why the row's own profit never depends on one.

---

## Used-with-warranty finder — used stock that still carries cover

### The money question

Every board in this app so far asks *"how far under resale can I buy this?"*. This one asks the
question that decides whether the answer is worth acting on: **and what happens if it's dead?**

A used item with time left on a manufacturer warranty is a different asset from the identical item
without one, in two ways that are both money:

1. **The downside shrinks.** A $600 laptop bought off a stranger that doesn't boot is a $600 loss.
   The same laptop with eight months of factory cover is a repair ticket and a wait. On a board that
   ranks by profit alone those two rows were indistinguishable, and one of them is a trap.
2. **The resale rises.** *"Still under manufacturer warranty until March 2027"* is a line the seller
   can put in their own eBay listing, and buyers pay for it. The sold comps behind the price estimate
   are a blend of covered and uncovered units, so that premium was not in the number the board
   started from.

### What was built

Not a new source — a capability that runs across **every** source at once. Detection happens in
`LocalArbitrageAnalyzer.Build`, so Craigslist, Facebook Marketplace, the deal feeds, the liquidation
auctions and the freebie board all gained it in the same commit, and no parser had to learn the word
"warranty". A future refurb-outlet `ILocalSupplySource` can override the reading by setting
`LocalSupplyListing.Warranty` itself; everything else is read from the listing's own text.

**Detected**, in the order the sources deserve to be trusted:

| Reading | Evidence | Worth money? |
|---|---|---|
| A stated end date — *"warranty until 3/2027"* | `stated` | **Yes** |
| A stated term plus a stated purchase date — *"3 year warranty, bought March 2025"* | `stated` | **Yes** |
| A named programme — Apple Certified Refurbished, Amazon Renewed, Best Buy Open-Box | `program` | **Yes** |
| A stated term with no start date — *"1 year warranty"* on a used phone | `stated` | **No** — how much is left is unknown |
| A purchase date plus the brand's standard term — *"bought 3 months ago"* on a Sony | `estimated` | **Never** |
| An unopened box plus the brand's term — *"brand new in box, never opened"* | `estimated` | **Never** |
| A stated absence — *"no warranty"*, *"as-is, no returns"*, *"all sales final"* | `stated` | Holds a verdict **down** |

### The one place prose is allowed to move a price, and its four fences

`WarrantyPricer` adds a premium to the resale estimate — the only place in the app where a listing's
own words can lift a price above what the sold comps produced. The argument for allowing it at all:
**the board already trusts the listing's price completely.** A row that says `$250` is costed at $250
with no corroboration whatsoever. Reading *"still under manufacturer warranty until March 2027"* out
of the same sentence and adding a capped few percent is strictly less credulous than what the board
already does — and it is a premium the reseller can actually realise, because that sentence is a line
they can repeat in their own listing.

It is fenced anyway, and when any fence bites the row's money is **identical to what it would have
been without this feature**, with `heldBackReason` saying which fence and why:

| Fence | Rule |
|---|---|
| Evidence | `estimated` readings earn **$0**, however plausible |
| Transferability | A seller's own guarantee, and brands whose terms name the original purchaser (DeWalt, Milwaukee, Makita, Ryobi, Samsung, Sony…) earn **$0** on resale |
| Believability | Under **3 sold comps** or **50 confidence** — the goldmine bar — earn **$0** |
| Size | **10%** of expected sale, then **$75** absolute. 10% of a $2,400 miner is $240 of unverified prose inside a profit ranking; the dollar cap is what stops it |

Bands: 12+ months → 10%, 6+ → 7%, 3+ → 4%, 1+ → 2%, under a month → nothing. A step function, because
that is how it works in a buyer's head — *"still covered"* is worth far more than *"isn't"*, and two
years left is worth barely more than one. Cover is credited to a maximum of 36 months.

**When a premium is paid, the row's own resale column moves with it.** A profit computed against a
price the row doesn't show is a row that doesn't add up, and the fees, net, ROI and max-buy-price
beside it are all meant to be checkable against that column. The premium is printed under it as
*"incl. +$20 still under warranty"* so it can be seen and subtracted.

### What the verdict does with it

The uplift has already had its say through the money by the time verdicts are judged, so
`WarrantyPricer.JudgeWarranty` only handles what money cannot express — and, like `JudgeFreebie`,
**every correction it makes lowers a verdict and none raises one**. The single case: a buy over
**$150** the listing states is sold as-is with no returns drops from `goldmine` to `solid`. The profit
on that row may well be real; a green badge on it is an instruction to commit four figures to a
stranger's word about a thing that cannot be returned, and that instruction should come with a hand
on the arm.

### What the seller sees

- **A chip per row** in four weights, and the difference between them is the whole feature:
  green — stated, transferable, running (*"Manufacturer warranty · 17 months left — worth $20 more on
  resale"*); grey solid — real cover that protects the buy and moves no price (*"Seller warranty ·
  1 month left"*); grey dashed — *"(estimated)"*, worth exactly $0; amber — *"No warranty — sold
  as-is"*.
- **"no cover, no returns — test it first"** on the expensive as-is rows, matching the held-down verdict.
- **"receipt mentioned"**, because that is the difference between a warranty claim and a conversation.
- **A filter — *"Only items still under warranty"*** — the finder itself. Client-side over the response
  already in hand, like every other filter on this board: it must never re-run a multi-minute scan.
- **Two scan headlines**: *"6 still under warranty — $94 of the profit above is what that cover is
  worth on resale"* and *"2 sold as-is with no returns — test before you pay"*.

`WarrantyUpliftOnTheTable` **is** inside `TotalPotentialProfit`, unlike the coupon and negotiation
figures. Those are claims about a code that may be dead; this is a claim about the goods that the
seller repeats in their own listing. It is reported separately anyway so the bare-comps number stays
recoverable.

### Files touched

| File | Change |
|---|---|
| `Models/WarrantyModels.cs` | **New** — `WarrantyKinds`, `WarrantyEvidence`, `WarrantyDetails`, `WarrantyEconomics` |
| `Services/WarrantySelectors.cs` | **New** — every pattern and every bound, isolated for tuning |
| `Services/WarrantyCatalog.cs` | **New** — 16 refurb/open-box programmes, 40 brand terms, each with a transferability answer |
| `Services/WarrantyDetector.cs` | **New** — pure detection and dating; refusal runs first |
| `Services/WarrantyPricer.cs` | **New** — the uplift, the four fences, the risk note, the verdict correction |
| `Services/LocalArbitrageAnalyzer.cs` | Detects once per row; uplift feeds the profit and the resale column; `JudgeWarranty` on both the ordinary and the auction path |
| `Services/CraigslistParser.cs` | Fills `DetailText` from the RSS body (+ shared `Truncate`) — the warranty is almost never in the title |
| `Services/DealFeedParser.cs` | Fills `DetailText` from the deal's write-up, where *"certified refurbished"* lives |
| `Models/LocalSupplyModels.cs` | `Warranty` and `[JsonIgnore] DetailText` on the listing |
| `Models/LocalArbitrageModels.cs` | `Warranty` on the row; `WarrantyCount`, `TransferableWarrantyCount`, `WarrantyUpliftOnTheTable`, `AsIsRiskCount` on the result |
| `Program.cs` | The four board counts, and the warranty figures in the scan log line |
| `wwwroot/index.html` | *"Only items still under warranty"* filter. `app.js?v=62`, `style.css?v=53` |
| `wwwroot/app.js` | `warrantyMeta`, `warrantyResaleLine`, the filter, the empty-state copy, two summary headlines |
| `wwwroot/style.css` | `.warranty-chip*` (four weights), `.warranty-risk`, `.warranty-proof`, `.warranty-extra` |
| `Tests/WarrantyDetectorTests.cs` | **New** — 23 cases, most of them refusal |
| `Tests/WarrantyMoneyTests.cs` | **New** — 15 cases: one premium paid, the rest of the fences, and four end-to-end through the board's own arithmetic |

### Verification

| Check | Result |
|---|---|
| `dotnet build "ING eBay AutoLister/ING eBay AutoLister.csproj" -c Debug` | **Succeeded** — 0 errors (2 pre-existing `NU1903` warnings) |
| `dotnet test "ING eBay AutoLister.Tests/ING eBay AutoLister.Tests.csproj"` | **1,613 passed**, 0 failed, 0 skipped (1,580 pre-existing + 33 new) |
| `node --check app.js` | Syntax OK |
| A covered flip end to end | $50 buy, $200 comps, *"3 year manufacturer warranty, bought last month"* → resale **$220**, net **$140.45** vs **$123.10** bare, max buy price **$190.45** |
| An estimated one end to end | Sealed Dyson, same comps → chip shown, **every number identical** to a row with no warranty |
| An expensive as-is buy | $400 buy, $1,000 comps → net **$467.10** kept, verdict **`goldmine` → `solid`**, risk note attached |

#### The same flaky-store-test family again, recorded rather than glossed over

`DealStoreTests.Hand_entered_deals_are_never_collapsed_together` failed **once** in five full runs of
the suite this session, and passed on the other four plus in isolation (21/21). It is the same shape
as the `EarningsStoreTests` flake recorded earlier and has the same plausible cause: the class gives
every test its own GUID-named temp SQLite file but calls the process-global
`SqliteConnection.ClearAllPools()` in `Dispose`, which collides when xUnit runs collections in
parallel on Windows. Nothing in this change touches `DealStore`. Two store test classes now sharing
one failure mode is enough to say the pattern itself is the bug and not the luck — worth a pass of
its own.

### Bugs found and fixed while building this

- **`3/2027` read as `3/20`.** A day-first alternation matched two digits of the year and turned an
  eight-month expiry into a date three weeks away — inverting the answer on the commonest way anyone
  writes a warranty end date. The month/year form is now tried first.
- **`under factory warranty` did not match.** The qualifier group consumed *"factory"* and then
  demanded *"warranty"* with no space between them, so the single most common phrasing in the whole
  feature silently produced nothing. Caught by the transferability test, not by the happy path.

### Not verified

- **The rendered board in a browser.** The endpoint shape, the counts and the row arithmetic are
  covered by tests and the served assets are at `v=62`/`v=53`; the chip, the *"incl. +$20"* line and
  the filter were not driven visually.
- **Live supply carrying warranty text.** Every detection case is drawn from how these listings are
  actually worded, but no live scan was run in this session to measure the hit rate — how many
  Craigslist rows in a real search mention cover at all is unknown, and Facebook tiles have no body
  text to read, so those rows can only ever be read from their titles.
- **Whether a warranty a seller claims is actually live.** Untestable without the serial number and
  the manufacturer's lookup, which is exactly why the row prints *"ask for the receipt"* on every
  covered row rather than treating the claim as settled.
- **The premium's real size.** 10%/$75 is a bounded, defensible policy, not a measurement. Nobody
  here has a dataset of covered-versus-uncovered sold pairs; if one is ever built, this is the number
  to replace with it.

## Ship Smart — the Shipping Profit Engine (autonomous session, 2026-07-27)

### The gap this closes

Every profit number this app has ever shown was computed against **one flat shipping figure** —
`FeeProfile.DefaultShippingCost` — applied identically to a phone case and a 40 lb miner. That one
assumption sits underneath the local-arbitrage board, the lot analyzer, the deal scanner, inventory
health, break-even, the offer floors and every sourcing verdict. When it is wrong it is wrong
*everywhere at once*, and always in the seller's favour, which is the worst direction for a number
to be wrong.

Running the finished scan against the real account made the point better than any argument: **87
live listings, all ASIC miners, with `DefaultShippingCost` set to $0.** Every net-profit figure in
the app was assuming it is free to ship a 44 lb box. The board reports the gap at **$3,241.77 per
sale across 86 listings**, and the single best finding is not even the flat figure — it is that UPS
Ground carries a miner for **$50.36 against USPS Ground Advantage's $70.20**, which is $19.84 a sale
on every unit, forever, for choosing a different carrier.

`ListingData` has carried `WeightLbs`/`PackageLengthIn`/… since the beginning. The app collected the
data and never once turned it into money.

### What was built

**`Models/ShippingModels.cs`** — `PackageSpec` (with girth, volume, longest side),
`ShippingServiceQuote`, `ShipModeOption`, `PackagingTip`, `ShippingRecommendation`, `ZoneShare`,
`ShippingLeak`, `ShippingScanResult`.

**`Services/ShippingZones.cs`** — turns "where do I ship from" into "what does an average sale cost".
Quoting a single zone is the mistake most shipping calculators make: a seller in Ohio quoting zone 4
understates half their sales, and one in California quoting zone 8 scares themselves off items that
are fine. Instead this produces a *population-weighted distribution* over zones from the seller's ZIP
prefix, and every downstream cost is the expectation over it, with the tails kept alongside — because
the tails are where free shipping loses money. Ten ZIP-prefix regions with population shares and
centroids; great-circle distance into USPS distance bands. No data file, no network, no lookup service.

**`Services/ShippingRateBook.cs`** — eight services with real eligibility rules: eBay Standard
Envelope (4 oz, quarter-inch, category-restricted), USPS Ground Advantage, Priority, all four
Priority Flat Rate containers, and UPS Ground (the only one that goes past the 70 lb USPS ceiling,
which is what every miner on this seller's account needs). Rate anchors interpolated on weight, held
per zone band, dense where the card is steep. **Dimensional weight** with the right rules per carrier
— USPS only above one cubic foot at divisor 166, UPS at any size at 139 — plus oversize surcharges.

**`Services/PackageEstimator.cs`** — 50 packing profiles keyed by ~180 title keywords, longest match
first. This is what lets the engine answer from a *title alone*, which is the only way it can ever be
useful on a 200-row sourcing board or standing in a thrift store. Measured input always wins outright;
the estimate only fills holes, and an unrecognised item is deliberately guessed **upward** (a parcel,
not an envelope) because guessing downward is the exact failure this feature exists to end.

**`Services/ShippingAdvisor.cs`** — the cheapest eligible service, the packing tips worth money, and
the four ways to charge, all priced **at a constant buyer outlay**.

**`Services/ShippingLeakScanner.cs`** — the same maths across every live listing, one finding per
listing (ranked: label gap, then packing fix, then zone exposure, then the item simply being too
heavy for its price), read-only, no comp lookups, so unlike the pricing scans it needs no budget.

**Endpoints** — `POST /api/shipping/quote`, `GET /api/shipping/services`, `GET /api/shipping/leaks`.
**`POST /api/pricing/net-quote` now estimates a real label** when the caller sends a title/package and
no explicit shipping cost, and returns a `ShippingEstimateSummary` saying what it assumed and why — so
a break-even that moved $9 can explain itself rather than silently being a better-or-worse number.

**UI** — new *Ship Smart* page: a two-tab panel (price one item / scan my listings), the package strip
that looks visibly different for a guessed box than a weighed one, the mode comparison table, every
service priced with ineligible ones *shown* and reasoned, the packing tips, and a collapsible zone mix.
Plus the leak board with tiles and per-sale impact. Assets bumped to `app.js?v=64` / `style.css?v=55`.

### The correction at the centre of it

Sellers overwhelmingly believe that charging shipping separately avoids eBay's cut on it. **It does
not** — the final value fee is charged on the order total, shipping included. So at a fixed buyer
outlay, free / flat / worst-case all net within pennies of each other. The mode table proves this on
screen (free at average and flat both show $323.68 on the miner), and there is a test asserting it
that will fail the day the app starts repeating the folk myth.

What *actually* differs between the modes is **who carries the risk that the buyer lives far away** —
which nothing else in this category reports. On the miner: free shipping at the average cost loses
money on **42.2% of US buyers**; calculated shipping cuts the seller's exposure from $45.46 to $6.02.

### Three things the tests found that I had backwards

- **Calculated shipping does not remove the seller's exposure.** I wrote copy saying "your take-home
  is the same on every sale" and a test asserting `ZoneRisk == 0`. The engine returned $4.91, and the
  engine was right: eBay charges the fee on the shipping line too, so a dearer label still costs the
  seller the *fee on the difference*. The copy was corrected to say so, and the test now pins the
  residual to exactly `spread x feeRate` — a sharper claim than the wrong one it replaced.
- **A flat-rate tip that was physically impossible.** The rate book rejects flat rate on a strict
  dimension check, so a 14x12x10 carton is technically only "too big" for the *padded envelope* — and
  the tips engine cheerfully advised repacking ten inches of depth into three quarters of an inch.
  Fixed with a volume-plausibility gate (`CouldBeRepackedInto`), with a regression test.
- **The heavy-shipping headline quoted actual weight where it meant billable.** On a dimensional-weight
  item those differ by an order of magnitude — it read "$59.75 to ship… at 3 lb billable", which makes
  the app look broken rather than the box. Now quotes billable, with a test.

### Verified

- `dotnet build` — 0 errors. `dotnet test` — **1689 passed, 0 failed** (73 new across five test classes:
  `ShippingZonesTests`, `PackageEstimatorTests`, `ShippingRateBookTests`, `ShippingAdvisorTests`,
  `ShippingLeakScannerTests`).
- **Driven in a real browser** (Playwright, the app's own runtime) against the live account: quote panel,
  mode table, service table, tips, and the leak board over all 87 listings. Zero page errors.
- Endpoints exercised directly for the flat-rate win ($9.75/sale), the eBay Standard Envelope case
  ($0.62), and dimensional weight (a 3 lb lampshade billing as 56 lb).

### Two UI bugs caught only by looking at it

- **The tables rendered at zero height.** I reused `.table-wrap`, which is `display: none` until a
  *listings* panel opts into table mode — so every table on the page was invisible while present in the
  DOM. No test would ever have caught this. Own wrapper class added.
- **Calculated shipping displayed as "free shipping"**, because its `BuyerPaidShipping` is 0 by design
  (it varies per buyer). Now shows the by-zone range.

### Not verified / known limits

- **Rates are calibrated estimates of eBay/commercial label pricing, not live carrier quotes.** Stated
  in the footnote, in the API response, and on the reference table. Expect within about a dollar. A live
  rate API needs per-carrier credentials the seller does not have and would fail closed exactly when
  this is most useful. If those credentials ever exist, `ShippingRateBook` is the single seam to replace.
- **Zone resolution is the ZIP prefix, not the ZIP.** A zone band is 300+ miles wide, so full
  ZIP-to-zone precision would move the expected cost by cents; being approximate and saying so beats
  looking exact and being stale.
- **The seller's `DefaultPostalCode` is currently blank**, so the live scan above ran on the
  national-average zone mix. Filling it in (Settings) will sharpen every number on the page — and for
  a coastal seller, sharpen them upward.
- **Package estimates are unmeasured for all 87 live listings** (`0 weighed, 87 estimated`). The board
  labels every such row, and the tiles report the split, but the dollar figures are only as good as the
  profiles until someone puts a box on a scale.
- **Nothing writes to eBay.** No labels are bought, no listing is edited, no shipping policy is changed.

---

## Trustworthy percentages — comp-backed ROI on the local-arbitrage board (autonomous session, 2026-07-27)

### The complaint

A $60 *"toyota trailer hitch"* on the Local Deals board showed a **$554 resell price and a 698% ROI**,
off **one loose sold comp**. Nothing on the row was arithmetically wrong — `ProfitCalculator` did
exactly what it was asked with the price it was handed — but that price rested on a single sale of
something that may not even have been the same product, and the board printed the resulting percentage
in the same typeface as a percentage backed by twenty sales.

The board's job is to tell a seller which listing is worth driving to. A jackpot number off one bad
match is worse than no number: it is the one row they *will* drive to.

### Two separate failures, fixed separately

**1. The comp count on screen was never the comp count behind the price.**
`ResalePricing.SoldCompCount` carried `localComparables.Count` — everything the *lookup returned*.
By the time `MarketPriceEstimator` had run per-unit normalization, the identity guard, outlier removal
and the strong-match filter, the set that actually produced the median could be a fraction of that. A
twelve-comp search that priced off one comp displayed "12 sold comps" and cleared every evidence gate
in the app on that basis.

`PriceEstimate.PricedOnCompCount` now states how many comps produced the figures, carried through
`SourceBreakdown` → `ResalePricing.PricedCompCount` → the row. **Every evidence gate on the board now
counts in that number** (`ResalePricing.EvidenceCompCount`): the verdict tiering, the warranty uplift
gate, the liquidation retail sanity check, and the comps the negotiation draft is allowed to cite.

**2. The identity guard was quietly stepping aside exactly when it mattered.**
`MarketPriceEstimator.ApplyIdentityGuard` filtered comps to those whose title carries the target's
model/part token — then **fell back to the unfiltered set whenever fewer than three survived.** That
fallback is the bug: the case where two comps match and eighteen don't is precisely the case where
pricing off the eighteen is wrong. A $74 FANUC fuse priced off $1,050 FANUC drives is the same failure
the guard was written to stop, reintroduced by its own escape hatch.

The guard now **narrows rather than steps aside** — any survivor wins, because two real sales of this
item price it and twenty sales of the pricier thing beside it do not. When *nothing* carries the
identifier the set is still handed back whole (an unpriced row helps nobody), but the new
`IdentityGuardResult.Verified` comes back **false**, and a false there caps the row at "thin" at any
comp count and dims its percentages.

### Confidence gating — `LocalArbitrageAnalyzer.GradeEvidence`

One pure function, three tiers, and the rule that only the top tier may make a claim:

| Tier | When | What the row is allowed to say |
|---|---|---|
| `confident` | ≥ 3 matching comps priced it **and** identity verified **and** confidence ≥ 50 | 💎 Goldmine / ✅ Worth it; ROI and margin shown as rates |
| `low` | < 3 comps, **or** no comp carries the model/part number, **or** the history is too scattered | Capped at ⚠️ Thin; ROI/margin/resale dimmed and labelled *"estimate — too few comps"* |
| `none` | nothing matched | ? No data, as before |

Three is the floor because two sales cannot disagree with each other, so two sales cannot be checked.
An unverified identity is checked **before** the count and is never rescued by it.

The numbers are **not hidden**. The lead is still worth chasing by hand, and a blank cell would just
hide the thing the seller needs to judge. They are demoted: same column, same figure, dimmed and
italic, with the reason in the tooltip.

### What the seller now sees

- **`3 sold comps priced it · of 12`** — the honest count leads, the search's count sits behind it.
- The **source named** on every row (`sold-comps DB`, `Terapeak`, or both) rather than implied.
- A dimmed resale/ROI/margin with an **`ESTIMATE — TOO FEW COMPS`** flag, or **`— no comp matches this
  model`** when the identity guard couldn't verify it.
- A board-level line: *"4 rows are estimates — too few matching sold comps to trust, so the ROI and
  margin on them are dimmed rather than shown as real rates."*
- **Deal Pipeline** forecasts freeze the priced-on count and an `ESTIMATE ONLY` marker, so a projection
  is graded later against the evidence it actually had.

### The one behaviour change to be aware of

`ApplyIdentityGuard` used to return a **larger** set (the unfiltered one) when few comps matched; it now
returns the **smaller, matching** one. Every screen that prices through `MarketPriceEstimator` — not
just local arbitrage — will therefore price some thin-comp items differently, and closer to the item in
front of it. The existing test that pinned the old fallback was rewritten to pin the new rule, which is
the honest thing to do with a test that was encoding the defect.

### Verified

- `dotnet build` — 0 errors.
- `dotnet test` — **1706 passed, 0 failed** (17 new: 14 gating cases across `GradeEvidence` / `Judge` /
  `Build`, and 3 rewritten or added identity-guard cases).
- The end-to-end regression is pinned directly: `Build_OneLooseComp_KeepsTheMoneyButRefusesToCallItWorthIt`
  builds the reported row ($60 ask, one comp at $554), asserts the money is **unchanged** ($420.20 net,
  ~700% ROI) and that the verdict is `thin`, the tier is `low`, and the note names the single comp.
- `node --check` on `app.js`. Assets bumped to `app.js?v=65` / `style.css?v=56`.

### Not verified / known limits

- **An item with no model or part number cannot be identity-checked at all** — "toyota trailer hitch"
  has no token to require, so the guard reports `Verified` (silence is not a warning) and the row is
  held to account by the comp *count* alone. That is what catches the reported case, but a listing with
  four loose comps for four different hitches will still price and still read as confident. Closing that
  needs title-similarity gating on the comps themselves, which is `ComparableMatcher`'s territory.
- **The 3-comp floor is a judgement, not a measurement.** It is the point at which a median can be
  contradicted, and it is now stated in exactly one place (`LocalArbitrageAnalyzer.ThinCompCount`) so it
  can be tuned against real outcomes once the Deal Pipeline has graded enough forecasts.
- **`JackpotHunter` and `InventoryHealthAnalyzer` still count raw comps** in their own believe-it gates.
  They read the same `ResalePricing`, so `EvidenceCompCount` is a one-line swap on each — deliberately
  left out of this change to keep it to one board.
- Nothing writes to eBay. No pricing source, key or credential was touched.

---

## One feature per tab — the app becomes a workspace (autonomous session, 2026-07-27)

### The complaint

Every screen in this app is a fixed, full-viewport overlay, and opening one **closed** the one
before it. Find a pallet on the Lot Analyzer, go price the parts on Ship Smart, come back — the
manifest is gone and the scan runs again. The seller could hold exactly one thought at a time.

Worse, those overlays sit **on top of the sidebar** (`position: fixed; inset: 0; z-index: 900`), so
with any feature open the nav was not merely unhelpful, it was unclickable. The only way to a
second screen was to close the first.

### The change

The AI Listing modal already had the right idea one level down: `newDraftTab` / `switchDraftTab` /
`renderDraftTabs` / `closeDraftTab`, a browser-style strip of open drafts. That model is now lifted
one level up, so a **feature** is a tab.

| Piece | Draft tabs (existing) | Workspace tabs (new) |
|---|---|---|
| Model | `draftTabs[]`, `activeDraftTabId` | `workspaceTabs[]`, `activeWorkspaceTabId` |
| Create | `newDraftTab()` | `newWorkspaceTab(page)` |
| Switch | `switchDraftTab(id)` | `activateWorkspaceTab(tab)` |
| Render | `renderDraftTabs()` | `renderWorkspaceTabs()` |
| Close | `closeDraftTab(id)` | `closeWorkspaceTab(id)` |
| Opener | `nl-tab-new-btn` (+) | `ws-tab-new-btn` (+) → feature menu |

**Navigation collapsed to one line.** `handleNav` was a 90-line ladder — eighteen "hide that
section" lines followed by eighteen "if this page, show that section" branches. It is now
`openWorkspaceTab(page)`, and the ladder became one table, `WORKSPACE_PAGES`: route → section id →
the screen's existing opener. Adding a screen is one row.

**Switching back does not re-run the scan.** A tab's `open` function runs on the *first* visit
only; after that the tab is put back exactly as it was left. That is the whole point — a finished
Local Deals board cost eBay calls and comp lookups, and showing it again must not spend them
twice. Two escape hatches exist for screens where a stale view is the wrong answer:

- `refresh` — re-run on every return. Set for **Logs** and **License** only, because their entire
  content is server state. Deliberately **not** set for Settings: it would overwrite fields the
  seller may be halfway through typing.
- `onShow` / `onHide` — the Auction Sniper's countdown ticker starts and stops with its tab, so no
  timer runs for a screen nobody is looking at.

**A route to the second feature.** Since the overlays bury the sidebar, the `+` on the strip opens
a menu of every feature — built from the sidebar's own children, groups and all, so the two lists
cannot disagree. Already-open features carry an `OPEN` badge and switch to their tab rather than
opening a duplicate.

**✕ and 🏠 stopped meaning the same thing.** Every screen header had both, and both did *exactly*
the same thing: throw the screen away and go back to the Dashboard. Now ✕ closes that one tab
(landing on its neighbour, not on the Dashboard), and 🏠 brings the Dashboard forward while the
screen **stays open on the bar**. Both tooltips say so.

### Keyboard

The strip is a real ARIA tablist with **manual** activation — a screen can cost API calls to load,
so arrowing past one must not open it.

| Key | Effect |
|---|---|
| `Tab` | Reaches the strip first — it is the first element in the document |
| `←` `→` `Home` `End` | Move focus between tabs without activating them |
| `Enter` / `Space` | Open the focused tab |
| `Delete` / `Backspace` | Close the focused tab, keeping focus in the strip |
| `+` then `↑` `↓`, `Esc` | Feature menu: move, then dismiss back to the `+` |

Only the selected tab is in the page tab order (`tabindex="-1"` on the rest), so `Tab` does not
walk through twenty open features before reaching the screen being read.

### Files

| File | Change |
|---|---|
| `wwwroot/app.js` | `WORKSPACE_PAGES` table; `openWorkspaceTab` / `activateWorkspaceTab` / `markWorkspaceTabOpen` / `closeWorkspaceTab` / `closeWorkspacePage` / `renderWorkspaceTabs` / `renderWorkspaceMenu` / `syncWorkspaceHash`; `handleNav` reduced to one line; `navigateTo` and `setActiveNavItem` extracted and used by all three navigation routes; every `show*Section` ends in `markWorkspaceTabOpen`; every `close*Section` routes through `closeWorkspacePage` |
| `wwwroot/index.html` | `#ws-tab-bar` (role=tablist) + `#ws-tab-new-btn` + `#ws-open-menu` above the app shell; ✕/🏠 tooltips say what they now do. `app.js?v=66`, `style.css?v=57` |
| `wwwroot/style.css` | `.ws-*` — the strip, tabs, the `+` and its menu, on the existing tokens |
| `Tests/WorkspaceTabsAssetTests.cs` | **New** — the three lists that have to agree |

### The one thing worth knowing about the layout

`--ws-tabbar-h` is **0px until a second tab exists**. A seller who only ever uses the Dashboard
never pays for chrome they do not use, and nothing moves. Once the strip appears, the app shell,
the sticky sidebar and every full-screen overlay shift down by exactly that token — one number,
one place.

### Verified

- `dotnet build` — 0 errors.
- `dotnet test` — **1713 passed, 0 failed** (7 new).
- **Real browser (Playwright, dev port 9394) — 43 checks, all passing.** Four features open at
  once; a query typed into the Local Deals box survives switching away and back; the URL follows
  the active tab and a reload rebuilds it; ✕ on the tab and ✕ in the header each close exactly one
  tab and land on the neighbour; 🏠 leaves the tab open; the `+` menu marks what is open and
  switches rather than duplicating; `←`/`→`/`Home`/`End`/`Enter`/`Delete` all behave; the AI
  Listing screen is a tab whose *draft* tabs survive a round trip through another feature; the
  Dashboard has no ✕; **no page errors**.
- One real bug found and fixed by that run: dismissing the `+` menu with `Escape` also reached the
  document-level `Escape` handlers that close the Opportunity Finder and Photo Library, so
  cancelling a menu closed the screen behind it. The key now stops at the menu.

### Not verified / known limits

- **Open tabs do not survive a reload.** The URL hash restores the *one* screen it names, and the
  bar rebuilds as Dashboard + that screen. Persisting the whole set needs a session store and a
  decision about re-running each screen's loader on restore; deliberately out of this change.
- **Tabs cannot be reordered or dragged**, and there is no overflow menu — with many open, the
  strip scrolls horizontally, which can push the `+` off the right edge. Both are additions to the
  same render function rather than changes to the model.
- **Every open tab keeps its section in the DOM.** That is what makes switching free; it also means
  a dozen open boards hold a dozen boards' worth of nodes. No screen was measured for this, and
  nothing is evicted.
- Nothing writes to eBay. No pricing source, key or credential was touched, and no server code
  changed — this is entirely the three shipped web assets plus one test.

---

## Design system + identity pass — one palette, one type scale, one product

The design pass in `48924c2` established real tokens. The twenty-odd feature
commits that followed did not use them. Each new screen reached for whatever
hex was to hand, so the app had drifted into several products sharing a
sidebar: **157 distinct hex values** and **406 hard-coded `font-size` values**
across 10,147 lines of CSS, including generic web reds and greens on the
profit figures, a navy-and-pure-yellow photo editor, and a slate-grey setup
flow. This change makes the token layer real and applies it everywhere.

### What the token layer gained

| Group | Added |
|---|---|
| Type | `--font-sans` / `--font-display` / `--font-mono`; a 13-rung integer type scale (`--fs-3xs` … `--fs-display`); weight, line-height and tracking scales |
| Colour | Full five-step ramps for success / danger / warning / info / accent; brand teal and gold extended; `--wine`; dark-surface subsystem (`--dark-0…3`, `--on-dark*`); `--src-*` for third-party badge marks |
| Identity | `--grad-gold`, `--grad-gold-hot`, `--grad-brand`, `--grad-brand-deep`, `--grad-hairline`, `--grad-sheen`; `--glow-gold`, `--inset-hi` |
| Structure | Radius extended to nine rungs; space scale to `--s10`; `--e5`; `--ease-out` / `--ease-in-out` / `--ease-spring`; `--dur-0` / `--dur-4` |

**No web fonts.** The app is served from localhost and must render identically
with no network, so the stacks resolve to faces already on the machine. On
Windows 11 headings land on *Segoe UI Variable Display* — the optical size cut
for large text — which is most of what makes the hierarchy read as typeset
rather than as scaled-up body copy.

### What was applied

- **Colour** — 362 raw hex occurrences down to 88, and every one of those 88 is
  `#fff`. Generic Tailwind-ish greens, reds, violets and blues folded onto the
  semantic ramps; the values that were *deliberately* third-party (eBay blue,
  Facebook blue, Craigslist purple) are now named `--src-*` tokens rather than
  loose hex in the middle of a component.
- **Type** — 406 hard-coded sizes down to 1 (an 8px superscript below the
  scale). Sizes are integers now: fractional px rasterise between hinting
  steps, which is the difference between crisp text and slightly soft text.
- **Motion** — nine near-identical durations collapsed to the three tokens.
- **Radius** — nine ad-hoc corner values collapsed onto the scale.
- **Two off-brand islands rebranded.** `editor.html` is served standalone and
  cannot import `style.css`, so it carried its own navy/slate palette with a
  pure-yellow accent. Its local `:root` now names the same values by another
  name, which re-skins the whole page without touching one component rule. The
  in-app photo editor and the license banner got the same treatment, plus the
  gold hairline every other dark surface in the app already wore.
- **Setup flow** — the first screen a new install shows was the last one still
  styled in generic slate and green. Now on tokens.

### Two real bugs fixed on the way

1. `.failure-detail summary:focus-visible` passed `var(--focus-ring)` — a
   `box-shadow` value — to `outline`, which the parser drops. A `summary` is
   not covered by the global focus rule, so that control had **no visible
   focus state at all**. Now uses `box-shadow`.
2. `--text-strong` was used but never declared, so its rule was relying on a
   fallback. Declared alongside the other aliases.

### Verified

- `dotnet build` — 0 errors (the 2 `NU1903` SQLite advisory warnings are the
  pre-existing baseline).
- `dotnet test` — **1713 passed, 0 failed**.
- **Real browser (Playwright, dev port 9345).** Every token resolves in the
  live document; headings compute to the display face with display tracking;
  the primary button renders its gradient. Dashboard, Settings and the
  standalone photo editor all captured and inspected — **no console errors, no
  page errors**.

### Not verified / known limits

- **`#fff` was left alone** (88 occurrences). White is white; routing it
  through `--card` would be churn in the places it is a surface and wrong in
  the places it is text on a dark panel.
- **`text-wrap: balance`/`pretty` are progressive.** Modern Chromium and the
  WebView2 the app ships against honour them; anything older simply wraps as
  before.
- **This is CSS and two `<style>`/inline blocks only.** No server code, no
  `app.js`, no business logic, no test touched. `style.css?v=58`; `app.js`
  unchanged, so its `?v=66` stands.
- Sizes shifted by 1–2px in a handful of places where a raw value sat between
  two rungs of the new scale. That is the point of a scale, and every shift
  was toward the nearest rung.

---

## Motion pass — make the app answer when you touch it

The design pass in `48924c2` gave the app the right surfaces and the motion
*tokens* (`--dur-0…4`, `--ease`, `--ease-out`, `--ease-spring`) to move them
with. Almost nothing used them for anything but colour fades. What was left
was an app where every state change happened between two frames: a screen was
one screen and then it was another, a modal animated open and then ceased to
exist, four stat tiles held last run's numbers and then held this run's, and a
failure arrived as `alert()` — an OS dialog, with the page frozen behind it and
`localhost:9332` in its title bar.

Everything here is decoration. Nothing in this change decides anything, fetches
anything, or writes anything.

### The five things

| | Before | Now |
|---|---|---|
| **Screen switch** | `display:none` → `display:block`, same frame | 6px rise + fade, 200ms (`section-in`) |
| **Modal close** | element vanishes | overlay and card each animate out, 200ms |
| **Dashboard figures** | number swaps | counts to the new value, ease-out, 280–900ms by distance |
| **Stat tiles while loading** | last run's numbers, sat over a shimmering grid | shimmer, matching the grid below them |
| **Failure** | `alert()` — blocking, unstyleable, no action | toast, bottom-right, with the retry button on it |

### Two decisions worth keeping

**Modals close from ~30 call sites, and none of them were touched.** Every one
does `classList.add('hidden')` synchronously. `initOverlayMotion()` puts a
`MutationObserver` on the ten `.modal-overlay` nodes and adds `.closing`, which
the CSS uses to out-specify `.hidden { display: none !important }` for exactly
one animation. **`.hidden` is never removed**, so every `contains('hidden')`
check in the app still reports the truth while the exit plays — and an overlay
re-opened mid-exit simply drops `.closing` and replays its open animation.
A 600ms timeout backs up `animationend`, because a dropped event would
otherwise leave a modal stuck over the app.

**`countUp()` reads the figure off the screen, not from a cache.** Other code
paths write these same elements directly; counting from a remembered value
would run the wrong distance. It also refuses to animate when the value did not
change — refreshing a dashboard where nothing moved should be silent, not four
tiles popping.

### One bug caught before it shipped

The primary-button hover sheen was first written on `.btn-primary::after`.
`.btn.is-busy::after` is already the loading spinner, and the two rules merge:
the sheen's `opacity: 0` would have made **every busy button in the app spin
invisibly**. Moved to `::before`, which nothing else uses. There is now a
browser check asserting the spinner still computes to `opacity: 1`.

### Verified

- `dotnet build` — 0 errors (the 2 `NU1903` SQLite advisory warnings are the
  pre-existing baseline).
- `dotnet test` — **1713 passed, 0 failed.** No test was added or changed;
  this is entirely presentation.
- **Real browser (Playwright/Chromium, dev port 9412) — 24 checks, all
  passing.** A real failure path (drafts endpoint aborted) raises an error
  toast with `role="alert"`, a retry action and a running life bar; hovering it
  computes `animation-play-state: paused`; dismissing collapses the slot and
  removes the node. Opening a screen applies `section-enter` and clears it on
  `animationend`. Closing a modal holds it at `display: flex` with
  `overlay-out`/`modal-out` running while `.hidden` stays on, then lands at
  `display: none`; re-opening mid-exit cancels the close and settles at
  `opacity: 1`. A loading stat tile computes `skeleton-sweep` with transparent
  text, and no code path leaves it shimmering. **No page errors, no console
  errors.**
- **Re-run under `reducedMotion: 'reduce'`** as a separate browser context: no
  `section-enter` class is applied at all, modals close instantly, the sheen
  computes to `display: none`, and no page errors.

### Not verified / known limits

- **Only the four dashboard stat tiles and the three money bands count up.**
  Every other figure in the app still swaps. Extending it is one call site each
  (`countUpText(id, value, format)`), deliberately not done in bulk.
- **`playEnter()` skips a screen holding an open `.modal-overlay`.** A
  transform makes the section the containing block for `position: fixed`
  descendants, and four screens nest their confirm dialog inside themselves —
  so that screen enters without the animation rather than re-anchoring a
  dialog for 200ms.
- **The toast system has eight call sites**, all of them the former `alert()`s
  plus one success. The publish and eBay-revision paths still use their
  existing in-page `showResult`/`nlSetResult` surfaces, which sit right under
  the button that was pressed; toasting them as well would say it twice.
- **Screen-enter and modal-exit are not tested by `dotnet test`.** They are CSS
  and DOM behaviour; the browser run above is the only thing covering them, and
  it is not wired into CI.
- `style.css?v=59`, `app.js?v=67`. No server code, no business logic, no
  pricing, key or credential touched.

---

## Charts pass — turn the numbers into pictures (autonomous session, 2026-07-27)

Every money screen in this app was already computing the right numbers and then
printing them as text. Five percentages down an insight card are five separate
reads; a "gross sales / eBay fees / net profit" tile row leaves the seller to do
the one piece of arithmetic that actually matters. Nothing here changes a number,
an endpoint, or a rule — it changes what the existing numbers look like.

No capability changes. **No server code was touched.**

### A visualisation layer, not five more charts

The three charts that existed (the monthly profit columns, the dashboard
sparkline, the trend sparkline) had each been drawn by hand in the session that
needed them, with their own greens, their own stroke widths and their own idea of
a tooltip. This adds one layer they all draw from:

| Piece | What it is |
|---|---|
| `vizMeter` | one share of one whole; severity fill on a lighter step of its own ramp |
| `vizRankBars` | a top-five list drawn as the bar chart it always was |
| `vizDelta` | a signed change, growing out from a shared centre so `+8%` and `-8%` are the same length in opposite directions |
| `vizStack` | one whole broken into its parts, with a legend that carries every value |
| `vizSparkPaths` | gap-aware line + 10% wash + one surface-ringed end dot |
| `viz-tip` | one delegated tooltip for the whole app, driven by `data-viz-tip` |

Colour, marks and spacing live in `style.css` under **Data visualisation** as
`--viz-*` tokens. Nothing below that block invents a hex value.

### The palette was computed, not chosen

The four categorical slots were run through lightness-band, chroma-floor,
colour-blind-separation, normal-vision and contrast checks in both modes:

```
light (on #ffffff)   #00789c  #946d1f  #7f4c9e  #157a55
dark  (on --dark-1)  #2f9cbc  #b58c3c  #a274c6  #2fa878
```

Two results worth recording, because both were counter-intuitive:

- **The brand teal cannot be a chart colour.** `--teal-600` is OKLCH C .075,
  under the .10 chroma floor — which means a reader sees it as *grey* and the hue
  carries no identity at all. `#00789c` is the same teal pushed just over the
  floor.
- **`--success-600` was too light to hold a label.** The composition bar prints
  the value inside the fill, and white on `--success-600` measures 4.3:1 — under
  the 4.5 line for small text. Slot 4 is a step darker; the hue did not move and
  the palette still passes every check.

The grey "not measured" slot is deliberately *below* the chroma floor. It is
supposed to read as absent, and its label is set in ink rather than white,
because white on it lands at 2.5:1 — on the one segment a seller most needs to
notice.

### What was drawn

**Money Made — "Where the money went".** One bar: what you paid, eBay fees,
shipping, what is still waiting on a cost, and what you kept, against every
dollar taken in. Two honesty rules shape it. Net profit only counts sales whose
cost is known, so gross minus fees, shipping and cost does **not** equal net on a
real account; the difference is drawn in grey as its own segment rather than
folded into profit. And a loss cannot be a share of revenue — when costs exceeded
everything taken in, the card says so in a sentence instead of drawing a bar
whose parts sum past 100%.

**Rising Now — "Market pulse".** A new dark band above the results. One column
per measured product, from a shared zero line, sorted by how far it moved. The
tiles above say "3 climbing of 21", which is equally true of a board where
everything crept up 2% and one thing collapsed, and of a board where three things
doubled — and a sourcer should behave completely differently in those two
markets. This is the shape those tiles were hiding. Has a table twin.

**Opportunity Finder — the insight cards.** High Sell-Through, Low Competition,
Underpriced Auctions and Pricing Recommendations are now ranked bars with a note
under each saying what the bar length means (0-100% for a rate; relative to the
busiest category for a count; a diverging chip for a signed price gap). One hue
per card — each card is a single series, and colouring bars by size as well would
burn the only free channel on information the length already shows.

**Opportunity rows.** Sell-through gets a severity meter under the figure. A row
whose sell-through could not be verified gets the neutral tone: the bar must not
look more certain than the badge above it.

**Trend sparkline.** Rebuilt on the shared primitive — 2px stroke, a wash under
the line, one end dot ringed in the surface colour. The intermediate dots are
gone (at 32px tall they were ink on top of the shape) and every week now has a
full-height hover band instead, which is a target a mouse can hit. The gap logic
and the server-driven tone are unchanged.

### One bug caught in the browser, not in review

The tooltip was first hung off the invisible hit rect. The column paints **on top
of its own hit rect**, and `closest()` walks ancestors, not siblings — so
hovering the actual bar, the obvious thing to do, found no `data-viz-tip` and
showed nothing. Only the sliver of empty band above the bar worked. The tooltip
and the tab stop now sit on the enclosing `g` element, so the rect, the column
and the label all resolve to the same target. The same latent bug was in the
profit chart's new tooltip and is fixed there too.

A second one caught by looking at the render: `niceTicks` rounds outward, so on a
board topping out at +31.4% it returned a +40% tick that drew off the top of the
plot and clipped to half a label. Ticks are now filtered to the span the chart
actually covers.

### Files touched

| File | What changed |
|---|---|
| `wwwroot/style.css` | `--viz-*` token block (light + `.viz-dark` re-steps), `.viz-panel`, `.viz-meter`, `.viz-rank`, `.viz-delta`, `.viz-stack`, `.viz-spark`, `.viz-tip`, `.viz-table`, `.tr-pulse-*`; the old hand-rolled `.tr-spark` marks deleted in favour of the shared component |
| `wwwroot/app.js` | viz primitives + `initVizTooltips`; `renderEarningsComposition`; `renderTrendPulse`; `renderInsightBars` replacing `renderInsightList`; `trendSparkline` rebuilt; sell-through meter on opportunity rows; tooltips on the profit chart |
| `wwwroot/index.html` | `#er-composition-card`, `#tr-pulse` band + table toggle. `style.css?v=60`, `app.js?v=68` |

### Verified

- `dotnet build` — 0 errors (the 2 `NU1903` SQLite advisory warnings are the
  pre-existing baseline).
- `dotnet test` — **1713 passed, 0 failed.** No test added or changed; this is
  entirely presentation.
- **Real browser (Playwright/Chromium, wwwroot served on 9413 with every `/api`
  call stubbed).** Composition bar: 5 segments, widths proportional, every
  in-fill label measured as fitting, legend carries all five values, sub-line
  states the 22.5c-per-dollar read and the amount still awaiting a cost. Market
  pulse: 18 columns, dark tokens resolve on the dark panel (axis
  `rgba(246,251,251,.72)`, up `#3fbd8b`, down `#e0796a`), hover raises the
  styled tooltip with the band highlighted, table toggle renders all 18 rows.
  Profit chart: keyboard focus on a column raises the same tooltip. Trend rows:
  7 sparklines with 14 area runs (gaps preserved), 7 end dots, 7 delta chips.
  Insight cards: 14 ranked rows, 4 diverging chips. **No page errors, no console
  errors.**
- **Re-run at 1024px wide under `reducedMotion: 'reduce'`.** The 63px grey
  segment correctly drops its in-fill label (needs 66px) while the other four
  keep theirs; cards reflow to two columns; meter transitions compute to ~0s.

### Not verified / known limits

- **The dark chart tokens are exercised by one surface.** `.viz-dark` also
  applies inside `.hero-panel` and `.opportunity-overlay-header`, but the market
  pulse band is the only chart currently living on a dark surface, so that is the
  only place the dark steps were seen rendered.
- **The composition bar is all-time only.** The summary carries no per-month
  fee/cost split, so there is no honest way to draw it per month without a server
  change.
- **Seasonal Demand is still a text card.** It is a list of category names, not a
  measurement — there is nothing in it to plot.
- **None of this is covered by `dotnet test`.** It is CSS and DOM behaviour; the
  browser run above is the only thing covering it, and it is not wired into CI.
- `style.css?v=60`, `app.js?v=68`. No server code, no business logic, no pricing,
  key or credential touched.

---

## Data-table pass — make the dense tables scannable (autonomous session, 2026-07-27)

The app ranks money in tables: Local Arbitrage, Listings, Inventory Health,
Snipe, Budget Basket, Shipping, Earnings, the lot manifest and four ladders.
Every one of them had been styled by the session that shipped the feature, so
there were nine paddings, four header treatments and three different ideas of
what a number column looks like. The board that answers "which deal is best"
set Net profit in the same 12px regular as the seller's username.

Nothing here changes a number, a sort, an endpoint or a rule. It changes how
long it takes to find the one row worth acting on. **No server code, no
business logic, no pricing, no key and no credential was touched.**

### One reading grammar, shared by every table

A new closing section in `style.css` that six table classes now opt into
(`.listings-table`, `.fb-arb-table`, `.inv-table`, `.ship-table`, `.er-table`,
`.opp-comp-table` — and through `.inv-table`, the `sn-`, `tr-`, `lot-`,
`bud-`, `ad-ladder-` and `neg-ladder-` variants).

| Piece | What it does |
|---|---|
| Header rail | `position: sticky` on **every** table, not just the listings one. 11px caps, `--muted`, a `--line-strong` hairline and a soft lift below it |
| Figures | one right edge, `tabular-nums`, `calt` off so a digit is never reshaped by its neighbour |
| Row | zebra at 2.8% teal, hover at 6.2%, and a 3px left rail wherever the row already meant something |
| Money columns | a gold band with two hairline edges on Net profit, ROI and Price — set one step up in size and at 700 |
| Shell | one hairline card per table: bounded height so the sticky header has something to stick to, scroll shadows on the sides |
| Chips | five pill shapes at four sizes collapsed into one, with a hairline ring so a soft fill still has an edge on a striped row |

### Decisions worth recording

- **`border-collapse: separate`, spacing zero.** Collapsed borders drop out
  from under a sticky header as it scrolls, and a collapsed table renders no
  `box-shadow` on a `<tr>` at all — which is where the row-state rail lives.
  The grid itself is unchanged.
- **The money band is a `background-image`, not a `background`.** It layers
  over whatever the row is doing — stripe, state or hover — instead of losing
  to it. Verified: hovering a row keeps the band.
- **The band carries size and weight but deliberately no colour.**
  `.fb-arb-table tbody td.dt-money` outranks `.fb-arb-profit.good/.bad`, so a
  colour there repaints every net-profit figure a flat ink and takes the
  green/red read off the one column the board exists to answer. This was
  caught in the browser, not in review.
- **Row states outrank the zebra, and hover outranks both.** A goldmine row or
  a dead-capital row deepens its own colour on hover rather than losing it.
- **Ladders and comp lists stay compact,** and their padding overrides are
  written at `thead th` / `tbody td` depth on purpose: at plain `th, td` they
  lose the specificity contest with the shared rule and silently get the tall
  results-table rhythm.
- **`.inv-results:has(> table)`** is what bounds the scroll region, so the
  empty and resting states — which are messages, not tables — never scroll. A
  browser without `:has()` simply keeps today's unbounded table.
- **Opportunity Finder is not a table** (a scored result is carried by its
  photo) but is read like one, so it takes the same grammar: tabular figures,
  a rule under Total cost and Est. profit the way a receipt totals, and a
  hover lift.

### Files

| File | Change |
|---|---|
| `wwwroot/style.css` | new closing "Data tables" section (`--dt-*` tokens, shared header/cell/figure/row/money/shell/chip rules, reduced-motion and forced-colours fallbacks). Removed the two `.listings-table` cell-level hover/active rules the shared grammar replaces |
| `wwwroot/app.js` | `dt-money` on the Net profit and ROI cells in `arbitrageRowHtml` (new `estRoi`, so ROI keeps its thin-evidence hedge), on the Price cell in `renderListingRow`, and on the Net profit / ROI columns and totals row of the Budget Basket table |
| `wwwroot/index.html` | `dt-money` on the Net profit, ROI and Price headers. `style.css?v=61`, `app.js?v=69` |

### Verified

- `dotnet build` — **0 errors** (the 2 `NU1903` SQLite advisory warnings are
  the pre-existing baseline).
- `dotnet test` — **1713 passed, 0 failed.** No test added or changed; this is
  entirely presentation.
- **Real browser (Playwright/Chromium at 2× DPI, `wwwroot` served on 9421 with
  a fixture page carrying the exact markup the four renderers emit).**
  `border-collapse` resolves to `separate`; header computes `position: sticky`
  with the hairline and lift; scrolling the arbitrage wrapper 200px moved the
  rows and left the header at the same viewport y. Money band resolves on both
  `th` and `td`, right-aligned, 13px/700, and survives hover
  (`backgroundImage !== 'none'` under `tr:hover`). Profit green resolves to
  `--success` and loss to `--danger`; the thin-evidence ROI cell keeps its
  muted italic. Zebra alternates, the goldmine row holds `--gold-tint` plus
  the 3px gold rail against it, ranks 1–3 are gold/800 and rank 5 is
  faint/650. Inventory, Listings and Opportunity Finder screenshotted and
  read. **No page errors, no console errors.**
- The fixture page, its server and the screenshots were deleted after the run;
  nothing from the harness ships.

### Not verified / known limits

- **The fixture page is not the live app.** It carries the renderers' exact
  markup, but the tables were not driven through `/api` — the scan endpoints
  need eBay credentials and live comps. Sort, filter and the Track button were
  not exercised in this run.
- **`:has()` gates the bounded scroll region on Inventory Health.** Every
  current browser supports it; one that does not gets today's unbounded table
  and an inert sticky header, which is the pre-existing behaviour.
- **Horizontal scroll shadows were not seen firing.** The arbitrage table's
  1040px min-width fits a 1560px viewport, so there was nothing to scroll
  sideways to. The mechanism is pure CSS `background-attachment` and computes
  correctly (`local, local, scroll, scroll`), but it was not observed.
- **None of this is covered by `dotnet test`.** It is CSS and DOM appearance;
  the browser run above is the only thing covering it, and it is not in CI.

---

## Dashboard redesign — one front page instead of six bands (autonomous session, 2026-07-27)

The dashboard had grown by accretion: every feature session that wanted the front
page took a full-width band on it. Hero, Money made, Money in motion, Closing
soon, Roll the Dice, then the four stat tiles — six blocks of the same width,
roughly the same weight, stacked. Nothing on it was first, the four numbers a
seller actually checks were below the fold, and the market data the app measures
was not on it at all.

**No number, endpoint, rule or business logic changed here.** Server side is
untouched, and every id app.js binds is still on the element it was bound to.

### The order the page is read in now

| Block | What changed |
|---|---|
| Masthead | The hero at `--s8`/`--s10` padding with two lit corners and a 34px grid at 2.8%, the lockup set on the gold gradient, and three status chips (eBay, AI key, free beta) |
| Quick actions | Find Goldmines / New AI Listing / Roll the Dice as three cards lifted `--s9` over the hero's bottom edge |
| Money | The three money bands side by side in an auto-fit row instead of stacked full-width |
| Counts | The four stat tiles, with an icon chip each and the figure at `--fs-4xl` |
| Market pulse | New. The Rising Now measurement, on the front page |
| Listings + Activity | Unchanged |

### Decisions worth recording

- **The action cards overlap the hero on purpose.** The hero's bottom padding
  (`--s10 + --s6`) is the landing pad and the cards' negative margin (`--s9`) is
  the overlap; the two have to move together. They are separate elements rather
  than hero children because a gold card on deep teal loses the contrast that
  makes it the thing you press.
- **Roll the Dice lost its band and gained a card.** Its band was one of six
  competing blocks; as one of three cards it is a bigger target with the same
  copy. `btn-roll-dice` is unchanged, so the busy state and the dice board behind
  it are untouched. The dead `.dice-band*` rules were removed; `.btn-dice` stays,
  because the Opportunity Finder still uses it.
- **The money row costs nothing when empty.** Each card keeps its "hidden until
  there is a real figure" rule, and the row's margin is behind
  `:has(> :not(.hidden))`, so a fresh install renders no row and no gap.
- **The dashboard never fetches the radar.** A sweep is a minute of eBay and comp
  lookups; putting one behind page load would spend it for every seller who opens
  the app. `renderDashPulse()` draws only from a scan already in hand, and the
  resting state offers the click instead — `#dash-pulse-scan` opens Rising Now
  *first*, then starts the scan, so the seller watches it fill.
- **The resting watermark is deliberately not a chart.** Monochrome, unlabelled,
  masked to fade out, at 16%. A placeholder with plausible bars would be
  indistinguishable from a real reading of the market, which is the one thing
  this panel must never be.
- **The pulse meter is polarity, not a score.** 30% of products climbing is a
  market, not a failure, so it takes `--viz-up` rather than the good/mid/low
  ramp — which is a light-mode ramp whose track and fill land two steps apart on
  a dark panel.
- **A `#i-dice` symbol replaces the 🎲 in the card.** Beside two stroke icons the
  emoji read as a sticker. The dice board's own buttons keep their emoji.

### Fixed while verifying: every meter in the app drew an empty track

`vizMeter()` emits the fill as a `<span>` and sets its width as an inline style.
`.viz-meter-track` is a flex *item* but is not itself a flex container, so the
fill stayed `display: inline`, ignored both width and height, and rendered at
**0px** — measured in the browser, not read off the CSS. Every meter shipped by
the visualisation pass has been drawing an empty track since. One
`display: block` on `.viz-meter-fill` repairs all of them; the dashboard's own
meter then measured 147.28px of a 220.81px track for 16 of 24 (66.7%).

### Files

| File | Change |
|---|---|
| `wwwroot/index.html` | Dashboard section restructured (hero + status chips, `.dash-quick`, `.dash-money-row`, stat tiles with icons, `#dash-pulse`); `#i-dice` symbol; `style.css?v=62`, `app.js?v=70` |
| `wwwroot/style.css` | New closing "The Dashboard" section; `display:block` on `.viz-meter-fill`; `.dice-band*` removed |
| `wwwroot/app.js` | `renderDashStatus()`/`setDashChip()`, `dash-act-goldmines` binding, `renderDashPulse()` + `dashPulseChart()` + `setDashPulseScanning()`, and the four `renderDashPulse()` calls on the scan's own paths |

### Verified

- `dotnet build` — **0 errors** (the 2 `NU1903` SQLite advisory warnings are the
  pre-existing baseline).
- `dotnet test` — **1713 passed, 0 failed.** Nothing here is testable by it; it
  is CSS and DOM.
- **Real browser (Playwright/Chromium at 2× DPI)**, against a throwaway static
  server serving the real `wwwroot` with fixture `/api` responses: disconnected
  and connected states (87 listings), 1560 / 1280 / 980 widths, and the market
  pulse driven end to end — clicking `#dash-pulse-scan` opened Rising Now, ran
  the scan, and the dashboard panel came back with 24 columns and the right
  headline. Probed rather than eyeballed: hero actions resolve to grid column 2,
  the cards overlap the hero by exactly 56px, and the meter fill measures 66.7%
  of its track. **No page errors, no console errors.**
- The fixture server, the drive scripts and the screenshots were deleted after
  the run; nothing from the harness ships.

### Not verified / known limits

- **The live pulse was driven from a fixture radar response**, not a real sweep —
  that needs eBay credentials and live comps. The shape of the response is the
  one `/api/trends/radar` returns, and the panel is fed by the same
  `trendScan` object the full-size band reads.
- **The meter fix is only seen on the dashboard.** Its other consumers (Where to
  Sell, Inventory Health and the insight cards) need scans this harness cannot
  run; they take the same one-line repair and could not be watched doing it.
- **The setup checklist above the hero is untouched.** On a fresh install it is
  still the first thing on the page, which is correct — but it is a taller block
  than the masthead under it, and that is worth a session of its own.
- **`:has()` gates the money row's margin.** A browser without it renders the row
  with no bottom margin when a card is visible; every current browser has it.
- **None of this is covered by `dotnet test`.** The browser run above is the only
  thing covering it, and it is not wired into CI.

---

## Light and dark themes — one design system, two lightings (autonomous session, 2026-07-27)

The app had one theme. It was a light theme with dark chrome — deep-teal
masthead band, deep-teal sidebar, deep-teal hero panels on a near-white page —
and there was no `prefers-color-scheme` rule, no `data-theme` hook and no
`color-scheme` declaration anywhere in 12,100 lines of CSS. This adds a dark
theme built from the same tokens, fixes what measuring found wrong in the light
one, and puts a three-state control in the masthead.

**No number, endpoint, rule or business logic changed.** Server side is
untouched, every id `app.js` binds is on the element it was bound to, and
`dotnet test` is unchanged at 1713 passing.

### How it is built

Not as a second stylesheet and not as an inversion filter. The existing `:root`
stays the light theme; one `:root[data-theme="dark"]` block at the end of
`style.css` re-declares the ~70 tokens that are lighting-dependent. Everything
else — type scale, space, radius, motion, the gold ramp, the brand teal ramp —
is shared, because none of it changes when the lights go off.

That only works if components consume *roles* rather than palette entries, and
in several places they were not. The pass that made the dark theme possible:

| Was | Now | Why |
|---|---|---|
| `color: var(--gold-dark)` x44 | `var(--gold-ink)` | `--gold-dark` is also a gradient stop. Gold as *ink* and gold as a *surface* are different colours and only one of them flips |
| `color: var(--teal-600/700/800/900/950)` x49 | `--brand-ink` / `--brand-ink-2` / `--brand-ink-strong` | same split for brand teal |
| `color: #fff` on a filled semantic swatch x8 | `var(--on-solid)` | in dark the fills are the bright end of the hue, so the label has to flip to near-black |
| `background: #ffffff` x26 | `var(--card)` | a hardcoded white card is a hole in a dark page |
| `color: var(--gold-soft)` x8, `--*-line` as text x4 | `--gold-note`, `--pale-pos/neg/warn/accent` | these sit on the *permanently* dark panels and must not follow the theme |
| `rgba(255,255,255,...)` chrome, scrollbars, skeleton sheen, table stripes | `--search-*`, `--ghost-*`, `--scroll-thumb*`, `--skeleton-sheen`, `--dt-*` | one role each, two values each |

### Decisions worth recording

- **The dark theme is not an inversion.** Every surface, ink and semantic step
  was re-picked at a lightness that works on a dark panel and then measured.
  An inverted palette drops a light-mode mid-tone onto near-black, where it
  either glows or disappears.
- **Gold does not flip.** The one accent is the same gold in both themes — a
  gold button is the thing a seller recognises the app by. What flips is the
  gold *callout surface* (`--gold-soft`), because #fff4d7 on a dark page is a
  lamp; `--gold-ink` reads 9.4:1 on its dark replacement, better than the light
  theme manages.
- **The permanently-dark surfaces do not move at all.** Sidebar, masthead band,
  hero panels, drawer and overlay headers, the sold-comps strip: deep teal in
  both themes, verified identical (same 90deg teal-900 gradient, white text, in
  both). That is why their type had to move onto the constant `--gold-note` /
  `--pale-*` roles first — a themed token on a fixed panel is invisible in
  exactly one theme, which is the bug nobody catches.
- **Three states, not two.** Light / Dark / **Auto**, as a segmented radiogroup
  in the topbar. "Follow the OS" is a real answer and a two-way toggle cannot
  express it. Auto is the default, is stored as the *absence* of a key, and
  tracks `prefers-color-scheme` live — change the Windows app mode with this
  open and the page follows without a reload. Pick a side and the OS stops
  mattering.
- **The theme is applied by an inline script in the head, not by `app.js`.**
  Doing it after `DOMContentLoaded` would flash a full white page on every
  single load for anyone who chose dark. The script is six lines and owns only
  the first paint; `initTheme()` owns the switch, the persistence and the OS
  listener.
- **The switch animates one number.** `--theme-i` is 0/1/2 and CSS slides the
  gold pill; nothing measures or writes a pixel offset. A moving object says
  where the selection went — three crossfades do not.
- **The cross-fade is worn, not owned.** `html.theme-switching` carries a
  colour-only transition for 360 ms and is removed. A permanent global
  transition would put one on every hover in the app, and it sits behind
  `prefers-reduced-motion: no-preference` so the existing reduced-motion block
  is not fighting a later `!important`.
- **Product photography stays on white.** `.pl-photo img` and `.inv-thumb` keep
  a literal white matte in dark. A themed matte would show every JPEG's own
  white background as a bright rectangle inside a dark tile.

### Fixed while measuring: the light theme was under AA in three places

Running the palette through a contrast checker rather than looking at it:

- **`--muted` (#6c7a80) was 4.44:1 on white, 3.99:1 on `--soft-2`.** That is the
  app's most-used secondary colour — 266 rules — and it was under 4.5 on every
  surface it lands on. Now #606e74 (5.28 / 4.75).
- **`--faint` (#96a3a8) was 2.59:1 on white.** Every placeholder in the app.
  Now #6b797f (4.50 / 4.29), still a visible step lighter than `--muted`.
- **`--gold-dark` as text on `--gold-soft` was 4.29:1** — gold type in a gold
  callout. `--gold-ink` at #805d15 reads 5.49:1 there and 6.01:1 on white.
- Two rules were also painting white text on `--gold-light` (1.9:1) and on
  `--gold` (2.5:1); both now take `--on-gold`.

### Files

| File | Change |
|---|---|
| `wwwroot/style.css` | Role tokens added to `:root`; ~120 rules retargeted from palette entries to roles; `--muted`/`--faint` corrected; new closing "Themes" section (dark token block, 4 dark corrections, the switch, the cross-fade); `style.css?v=63` |
| `wwwroot/index.html` | Inline pre-paint theme script in the head; `#i-sun` / `#i-moon` / `#i-auto` symbols; the `#theme-switch` radiogroup in `.topbar-actions`; `app.js?v=71` |
| `wwwroot/app.js` | `initTheme()`, `readThemeChoice()`, `applyTheme()`, `renderThemeSwitch()` and one line in `init()` |

### Verified

- `dotnet build` — **0 errors** (the 2 `NU1903` SQLite advisory warnings are the
  pre-existing baseline).
- `dotnet test` — **1713 passed, 0 failed.**
- **Real browser (Playwright/Chromium at 2x DPI)**, against a throwaway static
  server serving the real `wwwroot` with fixture `/api` responses:
  - Cold load with the OS in light gives `light`, choice `system`. Cold load
    with the OS in dark gives `dark`, choice still `system`.
  - Clicking Dark sets `data-theme="dark"`, moves the pill to
    `matrix(1,0,0,1,30,0)`, stores `ingTheme=dark`, and sets `aria-checked` on
    exactly one segment. Reload comes back dark with no white frame.
  - Clicking Auto **removes** the stored key; emulating an OS switch to dark
    then flips the page live, no reload.
  - Token resolution probed, not assumed: `--page` #eef2f3 to #061518,
    `--card` #ffffff to #0e2327, `--ink` #131c20 to #ecf5f5,
    `--gold-ink` #805d15 to #e7c47e, `body` background `rgb(6,21,24)`.
  - The permanently-dark surfaces resolve **identically** in both themes, and
    `--gold-note` / `--pale-pos` hold their constant values in both.
  - The scrim deepens in dark (`rgba(3,19,22,.58)` to `rgba(2,12,14,.74)`).
  - **A contrast audit run inside the page**, not read off the stylesheet: every
    visible text element, composited against its real resolved background,
    checked at 4.5:1 (3:1 for large text). **12 sweeps — the dashboard in both
    themes plus ten screens in dark — 1,144 elements measured, 0 under AA.**
  - **No page errors, no console errors** in any run.
- The fixture server, the drive scripts and the screenshots were deleted after
  the run; nothing from the harness ships.

### Not verified / known limits

- **The audit can only measure text whose background resolves to an opaque
  colour.** Anything sitting directly on a gradient — the hero headline, the
  gold buttons, the masthead chips — returns "unknowable" and is skipped. Those
  were set by hand against the same targets and checked in the screenshots, not
  measured.
- **`editor.html` is not themed.** The photo editor carries its own inline copy
  of the tokens and is a permanently dark tool, which is consistent — but it
  does not respond to the switch, and its token copy will now drift from
  `style.css`. That is worth a session of its own.
- **Tables were audited empty.** The fixture listings do not match the shape
  `renderListings()` expects, so the dark `--dt-*` stripe, hover and money-band
  values were verified as computed token values rather than watched on a full
  grid.
- **No JS means no theme.** The head script is the only thing that applies
  `data-theme`; there is no CSS-only `prefers-color-scheme` fallback, because
  duplicating the 90-line token block to serve a page that renders nothing
  without `app.js` buys nothing.
- **`--muted` and `--faint` moved for every screen at once.** They are darker
  than they were. That is the correct fix for a failing ratio, but it is a
  visible change to secondary type everywhere in the light theme, not only where
  it was failing.
- **None of this is covered by `dotnet test`.** It is CSS, DOM and one small
  controller; the browser run above is the only thing covering it, and it is not
  wired into CI.

---

## Category-agnostic sourcing — cars, boats, RVs and everything else that isn't a parcel (autonomous session, 2026-07-27)

### The problem

The whole local-arbitrage stack assumed **one shape of flip**: something you can
box and post, priced against eBay sold comps and costed with eBay's percentage
final value fee plus a shipping label. That assumption is wrong in three
independent ways for the categories where the biggest local money actually is —
cars, boats, RVs, trailers, powersports, tractors, appliances, furniture — and
every one of them costs real money:

1. **Sourcing.** Those things live on their own craigslist boards. A keyword
   search of the for-sale board finds four posts that happen to contain the word
   "truck"; `search/cta` is the local truck market.
2. **Valuation.** The hosted sold-comps database is electronics-heavy. Asked
   what a 2011 Tundra sells for, it finds tow hitches, tail lights and floor
   mats, agrees with itself across a dozen of them, and hands back a confident
   $180. That is not thin evidence — it is evidence about a *different kind of
   thing*, and the existing evidence tiering cannot see the difference.
3. **The money.** eBay charges a **flat** successful-listing fee on a vehicle,
   not the percentage one. On an $8,500 truck the gap between the two models is
   over **$1,000** of fee that is never charged. There is no shipping (the buyer
   drives it away) and there is a title to transfer, which the parcel model has
   no line for.

Verified live against the Las Vegas cars board during this session: a **2006
Hyundai Sonata at $500** matched sold comps at **$93** — a parts match, caught
and refused rather than published.

### What was built

**Sourcing — the right board, and the facts in a vehicle title**

- `Services/ResaleCategoryCatalog.cs` — 11 categories (Anything, Cars & trucks,
  RVs, Boats, Motorcycles & powersports, Trailers, Tractors & heavy equipment,
  Appliances, Furniture, Tools, Electronics), each carrying its craigslist board
  code, its sale channel, its valuation provider and its keywords. Plus the one
  job nothing else can do: deciding which category a listing belongs to.
- `ILocalSupplySource.SearchAsync(…, ResaleCategory, …)` as a **default
  interface member**, and `SupportsCategoryBoards`. Craigslist overrides it and
  searches the board; every other source ignores it and its results are
  classified per listing afterwards. No existing source needed editing.
- **A blank query is now a real search** when a category board is picked:
  "everything on the cars board within 40 miles" is the search this feature is
  for, and it is a different request from a blank search of the whole for-sale
  section.
- `Services/VehicleTitleParser.cs` — year / make / model / mileage / engine
  hours, ~120 makes, "137k miles" and "137,000 miles" and "1,240 hours" alike.
  Feeds three things: the row's identity chip, the **group key** (the generic
  product signature keys a classifieds title on the brand, which would put a
  2011 Tundra and a 2003 Camry in one group and price them off one lookup), and
  the search query on a refused row.
- **The parts check**, which is the most expensive misread available:
  "2024 Ford F-150 tailgate" and "Toyota trailer hitch" both carry a year or a
  make, and costing either as a vehicle turns an ordinary $180 parcel flip into
  a loss the board would tell the seller to walk away from.

**Valuation — pluggable per category, and never invented**

- `Services/ResaleValuation.cs` — `IResaleValuationProvider`, three
  implementations, and a DI-registered `ResaleValuationRegistry` (with a static
  default so the pure paths need no container). A real eBay Motors feed or a
  book-value service drops in as a fourth provider with no other file changing.
  - `EbayCompsValuationProvider` — the original behaviour, unchanged, for
    everything the database is actually full of.
  - `GuardedCompsValuationProvider` — comps that must **earn** the right to
    price a big-ticket item. Refuses on an unverified identity, on under three
    comps, on a resale below `ask x 0.4` (a parts match), on a resale above
    `ask x 5` (a comp for a different vehicle), and on **an ad with no price at
    all** — that last one found on live data, where a dealer ad shouting "FREE
    SHIPPING!" parses as free, and a $0 cost basis makes ROI unbounded.
  - Two instances: `ebay_motors` for titled goods (the strictest bounds) and
    `bulky_local` for appliances/furniture/tractors (looser — a $150 dresser
    reselling for $600 is an ordinary flip).
- A refused row **keeps its listing, its ask, its category and its identity**,
  shows `estimate unavailable` where the money would be, and carries a prefilled
  eBay sold-listings search (`&_sacat=6001` for Motors) built from the parsed
  identity rather than the seller's ad copy. **Never a number.**

**The money — category-aware fees and costs**

- `Services/CategoryCosts.cs` maps a category onto arguments for the *existing*
  `ProfitCalculator`, rather than teaching it about vehicles: a cloned
  `FeeProfile` with the percentage rates zeroed and the flat fee in the fixed
  slot, zero shipping on both sides, and title + transport as `otherCosts`. One
  fee engine, one break-even solver, one set of rounding rules.
  - `EbayParcel` (default) — hands back the seller's **own** profile instance
    and the comp shipping, untouched. Every pre-existing row prices to the cent
    as it did before.
  - `EbayLocalPickup` — eBay's percentage fee still applies; no shipping, no
    packaging. Appliances, furniture, tractors.
  - `EbayMotors` — flat $125 (cars/RVs) or $60 (bikes/boats/trailers), no
    shipping, $85 title transfer. Transport is **$0 and says so**: inventing a
    tow bill would be a made-up number in the middle of a checkable sum.
- `row.EstimatedFees` is now the marketplace's cut **only** — the title shows on
  its own line, because eBay doesn't charge you to register a truck and folding
  the two together would make the row's stated fee basis uncheckable.

**The board**

- Category picker in the search row (grouped, from `/api/local/categories`) with
  a note that says, *before* the two-minute scan, whether this app can price the
  category at all and which of the ticked sources can search a board rather than
  a keyword.
- Category filter above the ranking, built from the scan's own tallies so an
  option that matches nothing can never appear — labelled
  `Cars & trucks (14, 2 priced)`.
- Per row: the category chip, the vehicle identity and its wear, `buyer
  collects · $125 flat vehicle fee`, the title cost under the fees, and — in the
  Evidence column — **the valuation source and its confidence together**, since
  a source with no confidence invites the number to be read as a fact and a
  confidence with no source invites it to be read as eBay's.
- `dataWarning` no longer blames the seller's setup for a limit the app knows
  about: when every row was *refused*, connecting Terapeak would change nothing,
  and the warning says so instead.

### Files

| File | Change |
|---|---|
| `Models/ResaleCategoryModels.cs` | **New** — `VehicleDetails`, `CategoryEconomics`, `ResaleValuation`, `ValuationStatuses`, `LocalArbitrageEvidence`, the picker/tally DTOs |
| `Services/ResaleCategoryCatalog.cs` | **New** — the 11 categories, whole-word keyword detection with vetoes, `Classify`/`ClassifyAll`/`Tally`/`Describe` |
| `Services/VehicleTitleParser.cs` | **New** — year/make/model/mileage/hours, the parts check, `GroupKey`, `SearchQuery`, `ContainsWord` |
| `Services/ResaleValuation.cs` | **New** — the provider interface, three providers, the registry, the sold-search link builder |
| `Services/CategoryCosts.cs` | **New** — the three sale channels as `ProfitCalculator` arguments |
| `Services/LocalArbitrageAnalyzer.cs` | Resolves the category, runs the valuation gate, costs through `CategoryCosts`; optional registry ctor param so every existing caller is unchanged |
| `Services/ILocalSupplySource.cs` | `SupportsCategoryBoards` + the category `SearchAsync` overload, both defaults |
| `Services/CraigslistService.cs` / `CraigslistParser.cs` | Searches a category board, allows a blank query on one, stamps its own answer onto every post |
| `Models/LocalSupplyModels.cs` / `LocalArbitrageModels.cs` | `CategoryId`/`CategoryLabel`/`Vehicle` on a listing; category, economics and valuation on a row; category tallies and the manual count on a result |
| `Program.cs` | Provider + registry registration, `/api/local/categories`, `category=` on both search endpoints, classification at the pipeline edge, vehicle-aware group key, tallies, category-aware `dataWarning` |
| `wwwroot/index.html` | Category picker, category note, category filter; `app.js?v=72`, `style.css?v=64` |
| `wwwroot/app.js` | `loadLocalCategories`, `renderCategoryNote`, `isBoardSearch`, `categoryMeta`, `categoryCostLine`, `valuationSourceLabel`, `valuationCell`, `manualResaleCell`, `renderArbitrageCategoryFilter`, category filtering and the summary line |
| `wwwroot/style.css` | Category / vehicle / valuation chips, the category note, the picker field width |
| `Tests/CategoryArbitrageTests.cs` | **New** — 51 cases |

### Verified

- `dotnet build` — **0 errors** (the 2 `NU1903` SQLite advisory warnings are the
  pre-existing baseline).
- `dotnet test` — **1764 passed, 0 failed** (1713 before this session; every
  pre-existing test still passes untouched).
- **The app was run and driven against live craigslist** on a dev port:
  - `/api/local/categories` and `/api/local/sources` serve the picker, with
    `supportsCategoryBoards: true` on Craigslist alone.
  - `/api/local/search?category=cars` with **a blank query** searched
    `search/cta`, returned **266 posts**, and stamped `categoryId: "cars"` on
    every one; `"$777/OBO - 2009 Kia Rondo"` parsed to `2009 Kia Rondo`.
  - `/api/local/arbitrage?category=cars` priced 6: **5 refused, 1 priced**. The
    refusals read as sentences — including the $500 Sonata against $93 of comps
    — each with its own `_sacat=6001` sold-listings link.
  - The one that passed (`2009 Volkswagen CC 2.0T *MECHANIC SPECIAL*`, 7 comps)
    came back with **`$125` fees, `$0` shipping, `$85` title**, net `-$651`,
    ROI `-72.3%`, max-to-pay `$249`, `eBay Motors · buyer collects`, valuation
    source `eBay Motors sold comps` at `low` confidence.
- `node --check` on `app.js`.
- The wording of every refusal was read on live output and corrected — the
  provider's "kind" is carried in two grammatical forms, because "that isn't a
  cheap a vehicle" makes a real warning read as a bug.

### Not verified / known limits

- **The fee and cost constants are estimates**, of exactly the same kind as the
  13.25% final value fee already in `FeeProfile`: $125/$60 flat vehicle fee,
  $85 title transfer. They are stated on every row they touch so they can be
  argued with, but they are **not** configurable from the Fees & Costs screen
  yet, and they are not fetched from eBay (there is no API for a seller's
  actual rates). Transport is $0 by design and says so.
- **Only craigslist can search a category board.** Facebook, the deal feeds and
  the liquidation sources take a keyword and nothing else; their results are
  sorted into categories after the fact. The picker says this rather than
  implying every source narrowed.
- **Classification is keyword-based and will be wrong sometimes.** The bar for
  a vehicle is deliberately high (a year *and* a make, unless a board said so),
  which errs toward leaving a vehicle in the parcel model rather than costing a
  hubcap as a car — the safe direction, since only the second one invents money.
- **The guard's bounds are judgement calls** (0.4x / 5x / 3 comps for titled
  goods). They will occasionally refuse a genuine deal. Every refusal costs the
  seller a click on the sold-listings link; the alternative costs them a
  fabricated profit on a four-figure buy.
- **No UI test covers any of this.** The browser side is DOM and CSS, verified
  by reading the live JSON the page renders from, not by driving the page.
- `queue_forever.py` was already untracked at the start of the session and is
  unrelated to this work; it was **left untracked** rather than swept into this
  commit.

---

## Setup that says what it needs — two required steps, everything else optional (autonomous session, 2026-07-27)

### The problem

A new seller opened Settings and the first thing on the screen was **Image
Generation** — a mode picker, a Stable Diffusion endpoint and a prompt template,
above everything the app actually needs to run. The Anthropic key was further
down inside a paragraph of prose headed "AI Key", the eBay business policies were
last, and neither said it was required. The optional thing looked mandatory and
the mandatory things looked like details.

Underneath the layout was a data bug with the same shape. `/api/setup/save` bound
its body to `Credentials`, whose properties are non-nullable with defaults, so a
screen posting only its own fields could not be told apart from a screen posting
*blank* ones. The policy IDs, the listing defaults and the image-generation mode
were "always update" — so:

- Saving the **optional** image-generation settings **cleared the required eBay
  business policies** and every listing default (ZIP, weights, dimensions, Best
  Offer).
- Activating a license — a post of `{ licenseKey }` alone — cleared the same
  fields.
- Pasting an eBay token did too.

The next publish then failed with an eBay error about a missing policy, on a
screen that had never mentioned policies.

### The change

**One partial-save model (`CredentialsPatch`)**

- Every property is nullable, and `null` means *this screen wasn't showing that
  field — leave it alone*. Absent is not empty, which is the whole fix.
- Secrets (`AnthropicApiKey`, `OpenAiApiKey`, client secret, refresh token,
  licence, Stripe, comps API) keep the "blank means keep" rule they had, and are
  now **trimmed** — a pasted key carries a trailing newline more often than not.
- Clearable fields (policy IDs, listing defaults, image-gen mode) still clear
  when an empty value was **actually sent**, so emptying the ZIP still empties it.
- The OAuth-redirect-URL-in-the-token-field guard is unchanged.
- `CredentialsStore` gained a `(string filePath)` constructor beside the
  `IWebHostEnvironment` one, so the store is testable without a web host.
- `SetupStatus` gained `HasBusinessPolicies`, `HasOpenAiKey` and
  `IsReadyToList` (key and policies) — the two required steps, named on the
  server rather than inferred in three places in the browser.

**The Settings modal — the two required steps, and nothing else**

- `REQUIRED — 2 STEPS`, then two numbered cards, each with a live state pill
  (`✓ SAVED` / `NEEDED`):
  1. **Enter your Claude (Anthropic) API key** — a full-width monospace field
     bound to the same `CredentialsStore.AnthropicApiKey` as before, a **Show /
     Hide** toggle (`aria-pressed`, keeps the value, returns focus to the input),
     and a *Where to get it* line: console.anthropic.com → API keys, what the key
     starts with, and roughly what it costs.
  2. **Choose your eBay business policies** — Load Policies, the three selectors,
     and the manual policy-ID boxes folded into a `<details>` so the normal path
     is three dropdowns rather than six controls.
- **Image generation is gone from this modal entirely.** In its place, one line
  naming the optional extras and a link to the Settings page.
- The eBay developer keys, the OpenAI key and the manual user token moved into a
  collapsed `Advanced` `<details>`.
- Keyboard: Escape closes it (it had no key handler at all), every field has a
  real `<label for>`, and opening from a checklist button scrolls the step into
  view and focuses its first *enabled, visible* control — the required cards
  carry `tabindex="-1"` as the fallback, because focusing a disabled Load
  Policies button silently left focus on the dashboard behind the modal.
- The save button reads **Save Settings** when eBay is already connected, rather
  than telling a connected seller to connect again.

**Image generation as an optional connect strip**

- Now a card on the Settings page in the same shape as Terapeak and Facebook:
  heading, `OPTIONAL` tag, a state chip (`OFF` / `ON · local ComfyUI` / …),
  a sentence saying the app works fine without it, then the controls.
- It owns the prompt template too (`pg-image-prompt`), so all four image-gen
  settings live in one place instead of two.
- Terapeak and Facebook gained matching `CONNECTED` / `NOT CONNECTED` chips.
- The Settings page opens with a **Required setup** card (gold edge, the same two
  pills) above the optional strips, so the page has the same shape as the modal.

**Onboarding**

- The dashboard checklist is now exactly two required rows plus a dashed
  *Optional extras* strip (`ANY TIME` — image generation · Terapeak · Facebook)
  that jumps to the Settings page and flags the card it promised.
- Step 3 was "OpenAI key", which is needed by nobody who isn't generating photos.
- Step 2 is eBay **and** its policies in one row, because policies can't be
  loaded before the account is connected; its copy and button swap through
  *Log into eBay →* to *Choose policies →* to *✓ Ready*.
- The hero chips read `Claude key saved` and `eBay connected · policies needed`,
  off the same state the checklist uses.

**Incidental fixes found on the way**

- `wwwroot/index.html` had an **unclosed `div`**: the Image Generation card was
  never closed, so Terapeak, Facebook, Fees & Costs and Listing Defaults were all
  nested inside it, with a stray closing tag at the end of the section. The page
  is now tag-balanced (checked with a stack parser over the whole file).
- `/api/setup/status` was already being read for `hasOpenAiKey`, which the server
  never sent — it does now.
- The image-generation "disabled" error pointed at *Settings → Image Generation*,
  a heading that no longer exists; it names the optional strip.

### Files

| File | Change |
|---|---|
| `Services/CredentialsStore.cs` | **`CredentialsPatch`** + patch-based `Save`; `(string)` ctor; `HasBusinessPolicies`, `HasOpenAiKey`, `IsReadyToList`; `HasBusinessPolicies` on `PublicFields` |
| `Program.cs` | `/api/setup/save` binds `CredentialsPatch` |
| `Services/ImageGenerationService.cs` | The disabled-mode message names the new location |
| `wwwroot/index.html` | Modal rebuilt around the two required steps; image gen moved out to its own optional strip; required-setup card; checklist rewritten; unclosed div fixed; `app.js?v=73`, `style.css?v=65` |
| `wwwroot/app.js` | `renderRequiredState` / `paintRequiredPill` / `refreshRequiredState`, `openSetupWithFocus` / `openSetupAt` / `focusSettingsCard`, `bindSetupChecklist`, `renderImageGenState`, `setConnectState`; `updateSetupChecklist` reworked to (key, eBay, policies); image gen reads and writes only the `pg-` fields; dead `s-image-gen-*` handling and `applyImageGenModeVisibility` / `applyComfyUiModelVisibility` / `isSetupStepDone` removed |
| `wwwroot/style.css` | Required cards and pills, optional connect strips and state chips, advanced/manual disclosures, the checklist extras row |
| `Tests/CredentialsStoreTests.cs` | **New** — 18 cases |

### Verified

- `dotnet build` — **0 errors** (the 2 `NU1903` SQLite advisory warnings are the
  pre-existing baseline).
- `dotnet test` — **1782 passed, 0 failed** (1764 before this session).
- `node --check` on `app.js`; a tag-stack parse of `index.html` reports balanced.
- **The app was built and driven in a real browser** (Playwright, dev port 9451)
  against this machine's live credentials — which have a Claude key and eBay
  connected but **no business policies**, exactly the half-finished state the
  redesign is for:
  - The checklist showed step 1 `✓ Key saved` and step 2 pending with *"eBay is
    connected. Now pick the shipping, payment and return policy…"* and a
    **Choose policies →** button; the hero chips read `Claude key saved` and
    `eBay connected · policies needed`.
  - That button opened the modal, loaded **42 fulfillment, 1 payment, 4 return
    policies** from the real account, and left focus on the fulfilment selector.
  - The modal contains **no image-generation controls** — its only selects are
    the three policy pickers.
  - Show/Hide flipped the key field to `text` with `aria-pressed="true"`;
    Escape closed the modal.
  - The Settings page listed `Required setup`, then `Image generation OPTIONAL`
    (`OFF`), `Terapeak sold comps OPTIONAL` (`NOT CONNECTED`),
    `Facebook Marketplace OPTIONAL` (`NOT CONNECTED`), then Fees and Defaults.
  - **Zero console errors** across the whole run, in both light and dark themes.

### Not verified / known limits

- **Nothing was saved during the browser run.** The dev instance writes to the
  real `credentials.json`, so every check was read-only; the partial-save
  semantics are covered by the 18 new unit tests against a temp file, not by a
  click in the live app.
- **The UI is served from embedded resources**, so `dotnet run --no-build` serves
  the wwwroot of the last *build* — an edit made after building is invisible
  until you rebuild. This cost a confusing round of "the fix didn't take".
- This machine's stored `imageGenMode` is `"openai"`, a legacy value no server
  branch matches — image generation was already off in practice, and the strip
  says `OFF` rather than inventing a state for it. The first save from the strip
  normalises it.
- **No UI test covers any of this.** The browser pass was manual driving, not an
  automated regression.
- `queue_forever.py` was already untracked at the start of the session and is
  unrelated to this work; it was **left untracked**.

---

## Deal Radar — the sourcing board that reads itself (autonomous session, 2026-07-27)

### The problem

Every sourcing screen in this app is a **button**. The local-arbitrage board is
the best of them — pluggable sources, hosted sold comps, category-aware fee
models, the evidence tiering — and it does absolutely nothing until somebody
remembers to open the right tab and press scan.

Which means the money it is best at finding is exactly the money it misses. A
classified is a race: a $400 S19 posted at 11pm is claimed by 8am. A seller who
scans twice a day is not competing with other flippers, they are competing with
whoever happened to be refreshing craigslist at the moment it went up. The board
can price that deal perfectly and still be looking at it a day late.

Nothing in the app ran on its own. There was no scheduler, no notification path,
and no saved search — a scan's parameters lived in form fields and died with the
page.

### What was built

A **watch** is the same local-arbitrage scan, saved with a profit bar on it, run
on a human cadence by a background service. When something clears the bar, a
**real Windows notification** appears from the tray icon — with no browser open
at all — saying the thing the feature exists to say:

> `$400 Antminer S19 · 3 mi away → resells ~$700 · $210 profit, 52% margin`

**Not a second pricing path.** The radar calls `FindLocalArbitrageAsync` — the
same function the board's own endpoint calls, with the same fourteen singletons
behind it. It is reached through a one-line `LocalArbitrageScan` delegate
registered in `Program.cs`, because the alternative (lifting that orchestration
into a class, or re-pricing rows a second way) is how a notification ends up
quoting a profit the screen it links to doesn't show. Every figure on an alert is
copied from the row the board produced; nothing is recomputed.

**The bar — three gates, in cost order** (`DealRadarMatcher`):

1. **Is there a number at all?** A row the app *refused* to value — a truck
   against tow-hitch comps, see `ResaleValuation` — never fires, at any
   threshold. The board can afford to show it with dashes and a sold-listings
   link; a toast has room for neither.
2. **Does the board believe it?** Only `goldmine` and `solid` verdicts, and by
   default only `confident` evidence. **This gate earned itself on live data
   during this session**: a "DeWalt Tool Box - No Lid" at $15 priced against 3
   comps at $283 — a 1471% ROI the board dims and explains, and that a
   notification would have published as a fact. With the gate on, it was
   correctly refused. `none` evidence never fires even with the gate off, because
   that is not thin evidence, it is a price for a different product.
3. **Is it this seller's deal?** Profit floor, ROI floor, cash ceiling, driving
   distance — all four ANDed. The cash ceiling measures `BuyCostAllIn`, so a
   retail row can't clear a $500 budget by exactly the sales tax. An *unstated*
   distance passes: the radius already bounded the search, and dropping every
   classified without a published mileage would empty the feature.

**Once per listing, ever.** A craigslist post sits up for a fortnight; a watch on
a three-hour interval re-finds it 112 times. Dedupe is on
`source:item_id`, and — the part that matters — the memory
(`radar_seen`) is a **separate table from the feed** (`radar_alerts`). Prune the
feed and every listing still up would look new again, so the seller gets last
month's deals pushed at them at 2am and switches the feature off. Clearing the
feed forgets nothing.

**The scraping posture, restated as code** (`DealRadarClock`, `DealRadarService`):

- **One scan at a time, process-wide** — a semaphore of one, which the "Scan now"
  button takes too. Six watches never become six concurrent sessions.
- **One watch per tick**, most overdue first, with a **5-minute global floor**
  between any two scans. That floor is what stops a restart — where every watch
  is instantly overdue — from firing twelve scans in twelve seconds.
- **A 30-minute floor under the interval**, so no UI setting can turn this into a
  polling loop.
- **Stable per-watch jitter** derived from the id, so watches don't march in
  lockstep and requests don't land on the hour forever.
- **No Terapeak scrapes unattended.** A Terapeak lookup drives a real logged-in
  browser session. Background runs are cache-only (`terapeakBudget: 0`); a manual
  run gets 3, because a person is at the keyboard. Coupon lookups are manual-only
  for the same reason.
- **Craigslist unless told otherwise.** A watch with no source list reads the
  public site — never "everything available", which would quietly enrol a
  connected Facebook session into a schedule nobody asked for. Facebook is opt-in
  per watch and reports `not_connected` rather than logging in on a timer.
- **Off until switched on.** The master setting ships disabled.

**Two notification paths, deliberately not one.** `DesktopNotifier` is a queue
and an event; `Program.cs` subscribes the existing tray `NotifyIcon` to it and
calls `AttachDesktopChannel()`. Nothing in `Services/` touches WinForms, because
this app also installs as a **Windows service**, which runs in session 0 and
physically cannot draw a balloon. So `/api/radar/status` reports
`desktopChannel: tray | browser`, and the screen says either *"finds appear even
with this tab closed"* or *"leave this tab open"* — rather than promising a
notification that cannot be shown. The page's own Notification API fires only for
alerts the tray did **not** already announce (`notified`), so nothing is
double-reported. A run with more than two finds sends **one summary balloon**
instead of a stack, because five notifications in five seconds teaches a person
to dismiss without reading.

**Quiet hours silence the ping, never the scan** (default 11pm–7am). The entire
promise is that it works overnight; what stops at 11pm is the popping, and the
badge is waiting at breakfast.

**Saying which kind of nothing happened.** "0 deals" has four meanings and the
card names the right one every time: nothing listed near you · N listings, none
profitable after fees · N listings, some profitable, none clearing your bar · N
still clearing your bar, all of them ones you've already been shown. That last
one is the normal state of a healthy watch, and reporting it as "nothing cleared
your bar" would send a seller to lower a threshold that is working perfectly.

**The screen** — a workspace tab like every other feature, with a live unread
badge in the sidebar (drawn from `data-count` in CSS so the tab bar keeps taking
its title from the button's text without picking the number up with it):

- The master switch, what is happening right now, and the channel note.
- Watch cards as instruments: state pill, the search in words, the bar in words,
  the last reading in its own sentence, next sweep, running totals, and Scan
  now / Pause / Edit.
- The feed as finds: image, the headline sentence sized as the heading (because
  on this screen it *is* the heading), six figures including **pay no more than**
  and **cash back in**, the evidence note, and three actions — See the listing,
  **Track this deal** (straight into the Deal Pipeline with the forecast frozen
  as the alert quoted it), and Dismiss.
- The editor in four short legends: what to look for · where · what's worth
  waking you for · how often.

### Files

| File | Change |
|---|---|
| `Models/DealRadarModels.cs` | **New** — `DealWatch`, `DealWatchRequest` (partial-save), `DealAlert`, `DealRadarSettings`, `DealRadarStatus`, `RadarRunStatuses`, `RadarChannels`, `DesktopNotification` |
| `Services/DealRadarClock.cs` | **New** — the cadence and the posture: interval floor, global gap, per-watch jitter, due selection, quiet-hours wrap |
| `Services/DealRadarMatcher.cs` | **New** — the three gates, the dedupe key, the headline sentence, the run summary |
| `Services/DealRadarStore.cs` | **New** — `radar_watches` / `radar_alerts` / `radar_seen` / `radar_settings`, partial-save semantics, prune |
| `Services/DealRadarService.cs` | **New** — the `BackgroundService` loop, the `LocalArbitrageScan` delegate + request record, one-at-a-time gate, run interpretation, announcement rules |
| `Services/DesktopNotifier.cs` | **New** — the seam between the radar and the tray icon; `Channel` states what can actually be delivered |
| `Program.cs` | Registration of the store, notifier, scan delegate and hosted service; ten `/api/radar/*` endpoints + `BuildRadarStatus`; tray balloon wiring, `Open Deal Radar` tray entry, `OpenBrowserAt` |
| `wwwroot/index.html` | `i-radar` sprite, sidebar entry with badge, the whole `radar-section`; `app.js?v=74`, `style.css?v=66` |
| `wwwroot/app.js` | The Deal Radar module (~600 lines): `WORKSPACE_PAGES.radar`, watch CRUD, the feed, the badge, two polling timers, browser notifications, `trackRadarAlert` |
| `wwwroot/style.css` | Nav badge, master strip and switch, channel note, editor, watch cards and pills, alert cards and figures, dark-mode overrides |
| `Tests/DealRadarTests.cs` | **New** — 85 cases across four classes |

### Verified

- `dotnet build` — **0 errors** (the 2 `NU1903` SQLite advisory warnings are the
  pre-existing baseline).
- `dotnet test` — **1867 passed, 0 failed** (1782 before this session; every
  pre-existing test still passes untouched).
- `node --check` on `app.js`; a tag-stack parse of `index.html` reports balanced.
- **The feature was run end to end against live craigslist** on dev port 9461,
  and against a real Windows desktop:
  - `/api/radar/status` reported `desktopChannel: "tray"`, confirming the tray
    icon attached to the notifier.
  - A watch for `"antminer"` within 100 mi of 89101 ran and answered
    *"Nothing was listed near you this time."*
  - A watch for `"dewalt"` scanned **63 live listings** and, with the default bar,
    answered *"63 listings — some profitable, none clearing $25 and 15% on
    evidence this app stands behind."*
  - Dropping the evidence gate on that same watch produced **2 alerts** — both
    `low` evidence, including the 1471%-ROI lidless tool box. **The default
    configuration had correctly refused both.**
  - Re-running it found the same 63 listings and reported **0 new**:
    *"2 still clearing your bar, all of them ones you've already been shown."*
  - **The background loop was left to fire on its own**: a `"milwaukee"` watch it
    had never run scanned 28 listings unattended, found 3, logged
    *"3 new deals worth $221"*, scheduled its next sweep 3h+jitter later, and
    **raised a real Windows balloon** — the highest-profit alert came back
    `notified: true`.
  - Driven in a real browser (Playwright, chromium): the tab opens as **Deal
    Radar**, the sidebar badge reads **5**, three watch cards render with their
    own state pills and sentences, five alert cards render with the headline and
    all six figures, the in-app toast fired with an *Open Deal Radar* action, the
    editor loads 11 categories and 4 sources with **Craigslist ticked and
    Facebook not**, and saving a blank watch returns the sentence *"Give the
    watch something to look for…"* in the form. **Zero unexpected console
    errors** (the only 4xx was the deliberate validation POST), in both light and
    dark themes.
- Two bugs were found by that live run and fixed: the run endpoint's anonymous
  object had `Status` and `status` colliding under the camelCase policy (a 500
  after a two-minute scan, which reads as "the scan failed"), and the re-run
  wording blamed the seller's thresholds for what was actually the dedupe working.

### Not verified / known limits

- **The desktop balloon only works in the interactive (tray) install.** Under the
  Windows service install there is no session to draw in; the app says so and
  falls back to the browser's Notification API with the tab open. That fallback
  path was exercised in chromium but not against a service-mode install.
- **Alerts are frozen snapshots.** A classified is deleted the hour it sells, and
  an alert that re-read its listing would blank itself exactly when the seller
  wants to know what they missed. The footnote says to check the listing before
  driving.
- **The evidence tiering is inherited, not improved.** Everything the board can
  get wrong about a comp set, the radar can get wrong more quietly — which is
  precisely why the default gate is on and why refused valuations never fire.
- **Craigslist rows frequently carry no thumbnail**, so many alert cards show the
  dashed placeholder. That is the source data, not a rendering fault.
- **No UI test covers the screen.** The browser pass was driven manually; only
  the sidebar/registry/section contract is locked by `WorkspaceTabsAssetTests`.
- **The cadence constants are judgement calls** (30-minute floor, 5-minute gap,
  12 watches, 25 items a scan). They are conservative on purpose and are stated
  in the UI rather than hidden.
- The live run wrote its watches and alerts to the **build-output** database
  under `bin/Debug`, not to any installed instance's data.
- `queue_forever.py` was already untracked at the start of the session and is
  unrelated to this work; it was **left untracked**.

---

## The recovery banner stops crying wolf — no more empty "Untitled listing" drafts (autonomous session, 2026-07-27)

### The problem

The crash-recovery banner is the app's promise that a Claude-written listing —
real API spend, a minute or two of waiting — cannot be lost to a stray Ctrl+W.
That promise is worth exactly what the banner's credibility is worth, and the
banner was undermining itself.

The client's "is this worth saving?" test was a **size** test:

```js
if (!title && payload.length < 400) return;
```

`buildNlPayload()` serialises every control on the form, and most of those
controls **hold a default before the seller touches anything**: `condition`,
`packageType`, `quantity`, `handlingTimeBusinessDays`, `itemLocationCountry`,
`listingFormat`, `durationDays`. An untouched blank tab is already ~500
characters of JSON. So opening the AI listing modal, looking at it, and closing
it again sailed past that check and wrote a row — and the next launch announced
*"An unfinished listing was recovered"* over a draft called **Untitled listing**
with nothing whatsoever in it.

That is the worst available failure mode for this feature. A seller shown a false
alarm twice stops reading the banner, and then the one launch where it is holding
a real listing goes unread too. It also compounds: every blank open added another
row, and clearing them was one `confirm()` per row, so nobody did — the banner
became permanent furniture on the dashboard.

### What was built

**1. A rule that names the fields instead of weighing the bytes.**
`WorkRecoveryStore.IsWorthRecovering(stage, label, payload)` is now the single
definition of "worth recovering", and it asks the question size cannot: *did the
seller put anything in?* It parses the payload and checks the fields that carry
the seller's own work — `title`, `subtitle`, `description`,
`conditionDescription`, `brand`, `mpn`, `upc`, `ean`, `isbn`, `sku`, `category`,
`categoryId`, `secondaryCategoryId`, `price`, `itemSpecifics`, `imageUrls` — and
nothing else. Everything omitted is a control with a default; a blank tab has
values for all of them and content in none.

Four deliberate edges:

- **A name is content.** A draft labelled `Antminer S19` is kept even if nothing
  else is filled in. But `Untitled listing`, `untitled`, `new listing`, `draft`
  and blank are recognised as *placeholders* — the client still sends
  `Untitled listing` as a display label, and older builds already wrote rows with
  it, so the label is matched against a placeholder list rather than trusted as
  evidence of work.
- **A `price` of `0` is not a price.** The form reports `parseFloat(…) || 0` for
  an untyped box, so zero is absence, not a free item.
- **Only `editing` rows are judged, ever.** A row at `publishing` or `failed`
  means something really was sent to eBay. The row still marked `publishing` is
  the most important row in the table — the app went down between sending a
  listing and hearing back — and it surfaces whatever its payload looks like.
  Skipping that exemption would have turned a UI-tidiness change into a
  lost-listing bug.
- **An unrecognised payload shape falls back to size.** If the JSON is some other
  caller's shape (none of the content fields present at all), it is judged on
  length rather than discarded, because throwing away work the field list has not
  heard of is the same bug pointed the other way.

The rule is enforced at **all four** places it can matter, which is the point of
having one predicate: the client skips the autosave, the `/api/work/autosave`
endpoint refuses the write (quietly — an untouched form is the normal case, not
an anomaly worth a log line), `Save()` refuses it again for any other caller, and
`Recoverable()` filters on read. That last one is what makes the fix retroactive:
rows written by earlier builds are already in the table, and judging them on read
means the banner is clean on the **next launch** rather than the next save.
`Prune()` then deletes them for good, so the table converges too. `Save()`
refuses the *write*, not the existing row — a seller who selects-all and deletes
keeps the last save that had something in it.

**2. "Discard all N".** A `/api/work/discard-all` endpoint over a new
`WorkRecoveryStore.DiscardAll()`, and a button below the list — offered only when
there is more than one row, so it never sits beside a single row's own Discard
saying the same thing twice. It is set apart under a divider and right-aligned,
away from the per-row **Restore** buttons, because a hurried click must not land
on it, and the count is in the confirmation prompt (`Discard all 6 recovered
drafts?`) because this is the one recovery action that cannot be undone a row at
a time.

`DiscardAll()` is scoped to `stage <> 'published'` — the same rows the banner
offers. The `published` rows in this table are the **publish journal**, which
`PublishGuard` reads to answer "did this already go live?" after a restart.
Tidying the banner must not cost the seller their duplicate-publish protection,
and there is a test that says so.

### Files

| File | Change |
|---|---|
| `Services/WorkRecoveryStore.cs` | `IsWorthRecovering` + `ContentFields` / `PlaceholderLabels` / `MinimumOpaquePayloadChars`; the `Save` guard; `Recoverable` filters on read and over-fetches so the banner is never left short; `DiscardAll()`; `Prune()` split into `PruneRetention` + `PruneEmptyDrafts` |
| `Program.cs` | `/api/work/autosave` refuses empty drafts before the write (`reason: "empty"`); new `/api/work/discard-all` |
| `wwwroot/app.js` | `AUTOSAVE_CONTENT_FIELDS` + `hasAutosaveContent()` replace the `length < 400` test; the `Discard all` button and `discardAllWork()` |
| `wwwroot/style.css` | `.recovery-bulk` — divider, right-aligned, below the list |
| `wwwroot/index.html` | `app.js?v=75`, `style.css?v=67` |
| `Tests/WorkRecoveryStoreTests.cs` | An **Empty drafts** section — the blank-tab payload, the placeholder labels, six "anything in it survives" cases, the retroactive read filter, the prune, the `publishing`/`failed` exemption, the unrecognised-shape fallback — plus two `DiscardAll` tests |

### Verified

- `dotnet build "ING eBay AutoLister/ING eBay AutoLister.csproj" -c Debug` —
  **0 errors** (the 2 `NU1903` SQLite advisory warnings are the pre-existing
  baseline).
- `dotnet test` — **1887 passed, 0 failed** (1867 before this change).
  `WorkRecoveryStoreTests` went 20 → 40 cases and **every pre-existing one still
  passes untouched**, including the round-trip, bounds and publish-journal tests
  that now run `Save` through the new guard.
- `node --check` on `app.js`.
- The load-bearing assertion is `A_blank_form_is_rejected_despite_being_large`:
  it asserts the blank-tab payload **is longer than 400 characters** and is still
  refused. That test fails against the old rule, which is the point of it.

### Not verified / known limits

- **Not driven in a browser.** The banner markup, the button and the confirmation
  were not exercised in a real page this session. The rule they sit on is covered
  by unit tests, but the `.recovery-bulk` layout is unreviewed at 640px and in
  dark mode beyond inheriting `--info-line` and `.btn-ghost`.
- **The content-field list is duplicated**, once in `app.js` and once in
  `WorkRecoveryStore.cs`, with a comment in each pointing at the other. Adding a
  new content-bearing field to the form and not to both lists means a draft
  holding only that field is treated as empty. The server copy is the one that
  decides, so the failure is a skipped save, not a lost row that was written.
- **A field the list omits is not recoverable on its own.** Someone who types
  only a package weight and closes the tab gets nothing back. That is the trade
  taken on purpose: retyping a weight costs seconds, and a banner that fires for
  one is the problem this change exists to remove.
- **`PruneEmptyDrafts` reads then deletes** rather than doing it in SQL, because
  "is there anything in this?" is a question about the shape of a JSON payload and
  a blank tab's payload is the same size as a filled one's. It scans drafts only,
  and drafts are capped at `MaxRecoverableRows` (40), so it stays a few dozen rows
  however long the app runs.
- **`Discard all` has no undo.** It is a `confirm()` with a count and nothing
  more — consistent with the per-row Discard it replaces, but a seller who clicks
  past the prompt loses every draft in the banner.
- `queue_forever.py` was untracked before this session and is unrelated to this
  work; it was **left untracked** again.

---

## Snap & Source — a Buy/Pass verdict in the two seconds you have, from a link or a photo (autonomous session, 2026-07-27)

### The problem

Every sourcing screen in this app answers *"what should I go and buy?"* — Local Deals,
Deal Radar, Roll the Dice, the Opportunity Finder, Spend My Budget. All of them are a
search, a scan and a ranked table: minutes of work, at a desk, with two hands.

None of them answers the question that actually decides whether money is made, which
gets asked **standing up, holding the thing, with the seller waiting**: *should I buy
THIS?* A seller at a yard sale, an estate sale or a thrift aisle has about ten seconds
and one hand free, and the app's answer was to go home, open a board and run a scan
against a search term — by which time the item is gone.

There was also no way in that matched the situation. Everything sourcing-related started
from a saved search or a typed keyword. Nobody standing in a driveway types "Bitmain
Antminer S19j Pro 104TH" into a keyword box; they point a camera at the thing, or they
paste the link the seller just texted them.

### What was built

**One screen, three ways in, one word out.**

**1. `POST /api/snap` — the same pricing, a different question.**
The pricing is deliberately *not* new: it is `AnalyzeProductAsync` → `ResalePricing` →
`LocalArbitrageAnalyzer.Build`, the same path Local Deals, Deal Radar and Roll the Dice
take. A snap and a board row for the same item at the same price **cannot disagree**,
because they are the same call. What is new is the input and the output.

- **A pasted listing URL.** Read for its metadata, not screenshotted. The app already has
  a URL route (`/api/analyze-url` → `TakeHeadlessScreenshot` → Claude vision) and it takes
  tens of seconds — right for writing a listing at a desk, useless in a driveway. Every
  listing site publishes Open Graph tags and JSON-LD for link previews, and **a link
  preview is exactly the amount of information a Buy/Pass answer needs**. Measured
  end-to-end: 0.7–2.2s.
- **A photo.** New `ClaudeService.IdentifyItemAsync` — six short fields at
  `ThinkingEffort.low` with a 1,024-token ceiling, against the same `claude-opus-4-8`
  every other call in that file uses. Contrast `AnalyzeImageAsync`, which writes a
  complete SEO listing (80-char title, 4,000 characters of HTML, item specifics, package
  dimensions) at high effort with an 8,192-token ceiling and a four-minute timeout. The
  seller is not going to publish anything; the only field that matters is the search
  query. Effort is the latency lever, not the model — "identify this object" is not the
  part worth economising on.
- **A typed name.** Also the correction path: the identified name renders as an **input**,
  and re-pricing a corrected name deliberately does **not** re-send the photo. The seller
  has already told the app what it is; paying for a second look at the same picture would
  buy nothing and cost a wait.

**2. `SnapPageParser` — pure, total, no network.** HTML in, `SnapPageFacts` out. Declared
metadata only (`og:*`, `product:price:amount`, `itemprop`, JSON-LD), with exactly one
markup exception: Craigslist's own price element, because Craigslist publishes no price
metadata at all and is the single most likely site this gets pointed at. A price scraped
from visible body text is as likely to be a "customers also bought" tile as the item's own
price, and a wrong price here does not produce a wrong number — it produces a **confident
BUY on a deal that doesn't exist**. So the price comes from a declared field or from
nowhere, and a page that declares none hands back a null the UI asks the seller to fill in.

**3. `SnapJudge` — translation, never a second opinion.** The verdict tiers, profit, ROI
and break-even were all decided upstream. This collapses the analyzer's four tiers into
the three answers a person can act on (`goldmine`/`solid` → BUY, `thin` → CLOSE CALL,
`pass` → PASS, `no_data` → CAN'T PRICE IT) and computes exactly one number of its own.

**The one number: `PayUpTo`.** Below break-even, net profit at a price `p` is
`breakEven - p` and ROI is `(breakEven - p) / p`. Solving each of
`LocalArbitrageAnalyzer`'s own public bars (`SolidRoiPercent`, `SolidProfit`) for `p` and
taking the tighter gives one price that clears both — floored to the cent, because a
number quoted as safe has to be safe at the number quoted. It is the twin of
`JackpotHunter.TargetBuyPrice`, which asks the same question against the goldmine bar.
**This is the number the seller acts on**, and it leads every priced answer.

**4. The case the app had never had an answer for: no price named.** Every board in this
app starts from an ask. At a yard sale there isn't one — nobody has said a number yet. The
row is still costed (against a zero, which is what makes `MaxBuyPrice` come back as the
break-even), but the profit and ROI that implies are **arithmetic about a price the seller
has not been offered**, and publishing them as "your profit" would be the most flattering
lie this screen could tell. They are dropped, and the answer becomes **"BUY UNDER $112"** —
with a PASS when no price clears the bar, distinguishing *"even free, this doesn't clear
eBay's cut and the cost of shipping it"* from *"only $10 of headroom even if they hand it
to you — not worth fetching, photographing, listing and packing"*.

**5. The screen.** The only one in this app written **mobile-first** — the base CSS rules
are the phone layout and the desktop widening is a `min-width` query, because the small
screen is the real one here. `capture="environment"` on the file input, which is the whole
difference between a field tool and a file picker. Drop, paste and tap all work. The
verdict renders at 34px, readable at arm's length; tap targets are 44–52px.

### Files

| File | Change |
|---|---|
| `Models/SnapModels.cs` | **New** — `SnapRequest`, `SnapResult`, `SnapIdentity`, `SnapPageFacts`, `SnapCalls` |
| `Services/SnapPageParser.cs` | **New** — pure metadata parse, challenge-page detection, title/price/image extraction |
| `Services/SnapJudge.cs` | **New** — `PayUpTo`, the four-tier → three-answer collapse, the no-price-named branch |
| `Services/ClaudeService.cs` | `IdentifyItemAsync` + `NormalizeIdentity` — fast vision ID at low effort |
| `Program.cs` | `POST /api/snap` and `SnapFetchPageAsync` |
| `wwwroot/index.html` | `snap` sidebar entry, `#snap-section`, `app.js?v=76`, `style.css?v=68` |
| `wwwroot/app.js` | `WORKSPACE_PAGES.snap`, `OVERLAY_SECTIONS`, `bindSnapSource` and the render path |
| `wwwroot/style.css` | `.snap-*` — mobile-first layout, verdict card, tiles, dark-mode overrides |
| `Tests/SnapPageParserTests.cs` | **New** — 45 cases |
| `Tests/SnapJudgeTests.cs` | **New** — 24 cases |

### Verified

- `dotnet build "ING eBay AutoLister/ING eBay AutoLister.csproj" -c Debug` — **0 errors**
  (the 2 `NU1903` SQLite advisory warnings are the pre-existing baseline).
- `dotnet test` — **1976 passed, 0 failed** (1887 before this change). `node --check` on `app.js`.
- `WorkspaceTabsAssetTests` passes unchanged, so the sidebar / `WORKSPACE_PAGES` / section-id
  wiring is locked for the new screen exactly the way it is for every other.
- **Driven end-to-end against the live hosted comps database**, dev instance on port 9347:
  a typed name at a known price returned PASS in 2.2s with the identity guard fired and the
  right warning; the same item with no price returned **BUY UNDER $112** in 0.7s.
- **Driven in a real browser (Playwright, chromium)** at 390x844 and 1440x900, light and
  dark: the sidebar opens the tab, a real snap runs through the real endpoint, the verdict
  renders at 34px, the phone button is 52px and the photo drop 132px, and the
  re-price-a-corrected-name path re-runs and returns a different verdict. **Zero console
  errors** in all three passes.

### Three defects the live and browser passes caught, that review had not

1. **A bot check returning HTTP 200.** Pointed at a live Walmart product page, the challenge
   page answered **200** with a complete set of Open Graph tags, and the screen priced an
   item called **"Robot or human?"** — coming back with a confident *"BUY UNDER $464"*
   against comps for whatever eBay thinks those words are worth. Nothing failed, so no
   status-code check could have caught it. Fixed with `IsChallengeTitle`: phrases no product
   is ever called are matched anywhere in a bounded title; words that *are* plausible in an
   item name (`blocked`, `security check`, `page not found`) only count as the whole title,
   so "Blocked Drain Auger 25ft" survives. A challenge page now yields no title, which routes
   into *"that page didn't say what it is — take a photo instead"*, the one route no CDN can
   block.
2. **`flex: 0 0 130px` on a column flex container.** On the phone the fields stack, so the
   flex-basis was read as a **height** — forcing the price field to 130px tall and leaving a
   76px dead band above the button, on the one layout that can least afford it. Moved into
   the `min-width` query; measured 76px → 12px.
3. **The evidence sentence rendered twice on one card**, once in the evidence strip and once
   as a bullet under it. `SnapJudge` no longer puts comp evidence in `Warnings` at all —
   `EvidenceTier`/`EvidenceNote` already carry it in the analyzer's own words with a tier the
   UI colours on, and the card already has a *See the sold listings* button, which is the
   action the second copy was spelling out. A caveat repeated is a caveat discounted.

A fourth was caught by a test rather than a browser: the site-name stripper derived the brand
from the second-to-last host label, which makes `someshop.co.uk` a site called **"co"**.

### Not verified / known limits

- **The photo path was never run against the real Anthropic API this session.**
  `IdentifyItemAsync` is exercised only through `SnapJudge.AddIdentityWarnings` in tests; the
  prompt, the JSON shape and the low-effort latency claim are unproven against the live model.
  Everything downstream of the identification — the pricing, the verdict, the warnings — is the
  same code the typed-name path runs, and that path was driven end-to-end.
- **The URL success path was proven on Wikipedia and Walmart, not on a listing site.**
  Craigslist's RSS, eBay's search and B&H all returned **403** to this machine during the
  session, and the two listing URLs tried were invented item ids that correctly 404'd. The
  failure sentences are therefore well tested and the success path is covered by the unit
  fixtures plus two live non-listing pages. How often the real listing sites refuse a plain
  client is the open question — and it is exactly why the photo route exists.
- **A photo verdict is a price on what the app thinks it is.** Nothing here sees a cracked
  screen, a missing charger, water damage or a counterfeit. `CheckThis` asks the model for the
  one thing to check by hand, and `Certainty: low` adds a caveat naming what was actually
  priced — but neither is a substitute for looking.
- **Terapeak is never scraped on this path** (`allowRealTerapeakScrape: false`). A real scrape
  is a browser page load against a logged-in session, and this screen exists to answer before
  the seller gets bored. A snap therefore rests on the hosted comps database alone, and can be
  thinner than the same item on a board that was allowed to spend a scrape.
- **The page is horizontally scrollable at 390px — pre-existing, not this feature.** The
  document's `scrollWidth` is 606px at a 390px viewport, and it is identical on the Dashboard
  and on Where to Sell. The offender is `#step1-row` in the setup wizard behind the overlay;
  no element with a `snap-` class overflows. Left alone as out of scope for one cohesive
  change, but it does undercut the mobile promise and is worth its own pass.
- **The parser reads metadata, so a site that publishes none is a photo job.** That is stated
  in the UI rather than worked around; no amount of markup scraping would make it reliable.
- **The `.co.uk`-style list is not a public-suffix list** and does not need to be — it only
  decides which word to strip off the end of a title, so a miss leaves the title slightly long
  rather than breaking anything.
- `queue_forever.py` was untracked before this session and is unrelated; it was **left
  untracked**.

---

## The whole listing, not the title — the Listing Copilot's SEO pass finished off

**Branch:** `auto/queue-features-20260726` · **Baseline:** `2ae06f5`

The seller asked for a bubble that rewrites their listings for SEO: pick the listings, then do
nothing, and Claude fills out the whole listing without touching the photos. The card, the
picker and the background job were already there from `4c47006`. Five things were not.

### 1. The rewrite pass only half-knew what it was rewriting

`ClaudeService.ImproveSeoAsync` was never shown the listing's condition description, MPN, UPC,
EAN or ISBN — it was asked to rewrite fields it could not see. Its instruction on item
specifics was one line (*"fill any missing item specifics"*) sitting under a schema written for
a completely different job: photo analysis, which tells the model to invent a price and to read
image URLs out of a screenshot.

The prompt now puts every seller-controlled text field in front of the model, names item
specifics as the most valuable thing in the pass and says why (eBay builds its left-hand
filters out of them, so a blank *Model* drops the listing out of every refined search), and
carries an explicit **NEVER INVENT** block: no measurement, capacity, speed, wattage,
compatibility, part number or year that the listing does not already state, no scarcity
language, and no condition claim more flattering than the listing's own.

> *"A missing item specific only costs a search filter. A wrong one gets the seller an
> item-not-as-described return, the item back, and a defect on their account."*

That asymmetry is the whole argument, so it is written into the prompt in those words rather
than left as a style note.

### 2. The photos could still be replaced by a hallucination

`improved.ImageUrls = req.ImageUrls.Count > 0 ? req.ImageUrls : improved.ImageUrls` kept the
seller's photos **except** when the request arrived carrying none — and then it took the
model's list, which for this schema means URLs the model was explicitly asked to invent. Now
unconditional. The model is also no longer shown a photo list at all and is told to return an
empty array: a list it never sees is a list it cannot rewrite.

Belt and braces on top, in `CopilotSeoJob.KeepSellerTerms`: the draft gets the live listing's
own photos, all of them, in the live listing's own order, whatever came back.

### 3. Money and postage were guarded by "the model usually leaves it alone"

Price and quantity were preserved *if non-zero*; `PackageType`, `ItemLocationCountry`, best-offer
settings, per-buyer limits, private-listing and charity fields were not preserved at all. For a
listing being composed in the editor, fill-if-blank is right — that is where the AI's weight and
price estimates are useful — so that behaviour stays on the shared call. For a **live** listing
the rule is absolute, and `KeepSellerTerms` now copies all nineteen commercial and logistics
fields back from what eBay returned. Business policies need no line: they are not on
`ListingData`, so a rewrite has nothing to say about them and the draft falls back to the
account's saved policies like any other.

### 4. The preview showed a title diff and hid everything else

The panel's own promise at the top is *"shows you the full list of changes first"*. The SEO
card showed a struck-through old title, a new title and a reason — while the description was
being replaced outright and twenty item specifics filled in behind it.

New `Services/CopilotSeoDiff.cs` describes the change across the whole listing and hands back a
one-line headline (*"New title, description rewritten, 11 item specifics filled in. Photos
unchanged."*) plus field-by-field detail in a `<details>` that opens on demand. Three kinds,
because they read very differently to a seller: `filled` (the point of the feature), `changed`,
and `removed` — a specific that disappears is a buyer filter the listing falls out of, and it
is the one outcome most worth catching before publishing.

Sizes, not bodies, for the description: the status endpoint returns up to sixty results on
every 2.5-second poll, and sixty copies of a 7 KB HTML description is half a megabyte each
time. Detail lines are capped at 24 per listing for the same reason.

### 5. Stop existed on the server and nothing could reach it

`/api/copilot/improve-seo/cancel` has been there since the job was written, with no button
anywhere. A seller who started an eighty-listing sweep by mistake could only close the tab and
let it keep spending. The button appears only while a run is live and says exactly what it
does — **"Stop after this listing"** — because the listing in progress is already paid for, so
it is finished rather than thrown away, and every draft made so far is kept.

### Also

- The scan's own preview now says in plain words that titles are all a free scan can see, and
  that the rewrite replaces the description and fills the item specifics on every listing
  picked. That gap in wording is how the card came to look like a title fixer in the first place.
- `Rewrite all my listings` / `Rewrite every listing (89)` — two different labels on the same
  button depending on which code path last touched it. Unified.

### Verification

| Check | Result |
|---|---|
| `dotnet build … -c Debug` | **succeeded**, 0 errors |
| `dotnet test` | **2195 passed**, 0 failed |
| `node --check wwwroot/app.js` | clean |
| Served HTML/JS/CSS from a restarted `AutoListerB1.exe` on `:9332` | new card text, picker, Stop button, `app.js?v=88`, `copilotChangeSummary`, `.copilot-chg-filled` all present; `"Rewrite every title for search"` gone |

New tests in `CopilotSeoCardAssetTests`: the photos survive a model that returns a different,
longer, shorter, reordered or empty list (`[Theory]`, five cases); all nineteen money and
postage fields survive a model that changed every one of them; a draft carries no business
policy of its own; filling previously-empty specifics counts as changed **and** is reported;
the summary carries the description and specifics rather than raw HTML; a dropped specific is
reported; the picker, its count and both start buttons are on the card; the prompt still
forbids invention.

### Known limits

- **The prompt is unproven against the live model this session.** No Anthropic call was made:
  every assertion here is on what the prompt says and on what the code does with the answer.
  The anti-fabrication instruction is an instruction, not a guarantee — which is exactly why
  the photos and the commercial terms are enforced in code afterwards rather than asked for.
- **A filled item specific is still the model's reading of the seller's own listing.** The
  prompt forbids going beyond it and the preview names every value written, but the review step
  before publishing is doing real work and the drafts-only design is not decoration.
- **Category and condition are still the model's to change.** They are search fields and this
  writes drafts, so it is deliberate — but it is a wider blast radius than the card's
  "price, quantity, shipping or policies" sentence spells out.

---

## The deals board fits the window — nine columns, no scrollbar (autonomous session, 2026-07-29)

**Branch:** `auto/queue-features-20260726` · **What the seller said, twice, scribbling over the
scrollbar in a screenshot:** *"this window sucks - I do not want the scroll bar - make it bigger -
has to be cosmetic"*

The Opportunity Finder's deals board carried **fourteen** columns: `# · Deal · Source · You pay ·
Resell on eBay · Fees · Net profit · Days to cash · ROI · Margin · Max to pay · Offer them ·
Evidence · Track`. Fourteen fit no window, so four things had been bolted on to cope, each making
the next one necessary:

| Where | What it did |
|---|---|
| `.fb-arb-table-wrap { overflow-x: auto }` | the horizontal scrollbar under the table — the one drawn over in the screenshot |
| `.fb-arb-table { min-width: 1040px }` | forced the overflow in the first place; its own comment admitted it was raised to stop the Evidence cell wrapping |
| `.opportunity-overlay-body > * { max-width: 1320px }` | capped the results at 1320px on a monitor with room to spare — why it felt small |
| `.fb-arb-table-wrap { max-height: min(76vh, 900px) }` | a box scrolling vertically inside a page that was already scrolling |

The right-hand columns arrived cut off mid-word — the **"What to say"** button rendered as
*"What…"*, the negotiation cell as *"LOI"*. Every number in that table exists to answer *is this
worth buying*, and a number nobody can see answers nothing.

### 1. Nine columns, being the buying decision and nothing else

`# · Deal · Source · You pay · Net profit · Days to cash · Max to pay · Offer them · Track` —
what it is, where it is, what it costs, what it nets, how long the money is gone, the ceiling,
and the two things you can do about it.

**Nothing was deleted.** The five that left are the supporting half of the same question and now
open under the row that raised it, from a **"› Resale, fees, ROI & evidence"** disclosure on the
row itself (`arbitrageRowDetailHtml`): the resale price every profit figure was computed from,
the fees taken back out of it, ROI, margin, and the evidence prose — which was never a column's
shape anyway, and had been wrapping four words deep in a 220px cell.

Two things were deliberately kept out of the fold:

- **ROI rides under Net profit** (`138% ROI`, the way `$/day` rides under the wait). The board is
  ranked by ROI by default; a ranking key nobody can see is a ranking nobody can check.
- **The estimate warning stays on the row.** `ESTIMATE — TOO FEW COMPS (2)` is the difference
  between a lead and a fact, and it must not need a click.

### 2. Room

`#fb-arb-results` breaks out of the overlay's shared 1320px prose column and takes the width the
window actually has — measured against the overlay body itself with a container query
(`min(100cqw, 2200px)`), so it lands exactly on the body's content edges at any window size and
stops short of becoming a runway past that. The rest of the overlay's layout is untouched; this
is the results block, not every panel.

On a 1920px window the board is now **1574px** wide against the old 1320px cap.

### 3. No scroll, in either axis

The wrapper is `overflow: clip` — not `auto`, not `hidden`. It never scrolls, it still rounds the
corners off the header rail, and unlike `hidden` it is **not a scroll container**, so the sticky
header keeps tracking the page the way it did when the box scrolled itself. `max-height` is gone:
the page scrolls, the results do not.

Column widths are **shares (%) rather than pixels**, so a wider window widens the whole board
instead of pouring every spare pixel into one enormous Deal column with a hole in it, and long
titles reflow rather than being cut to fit a fixed track.

### 4. Narrow windows stack; they never scroll sideways

A container query on the board itself (`@container dealboard (max-width: 1080px)`) — not a
viewport media query, because between the sidebar, the overlay padding and the cap, the viewport
is the wrong measure of whether the columns fit. Below that width each row becomes a card: the
header rail's labels come back above each figure from the cell's own `data-label`, the money band
drops (a band down a column means nothing once there are no columns) and the buy-side stack
left-aligns.

### 5. Cosmetics, which is what was actually asked for

- Row padding up to 14px, thumbnails 46 → 52px, and the verdict badge, the estimate chip and the
  disclosure share one line so the cut didn't cost a row of height.
- The buy-side cell is a right-aligned stack, so the **"What to say"** button shares the edge the
  figures beside it use instead of drifting a space to the left of everything above it.
- **A stray rule across the Deal column, fixed on the way past:** `.fb-arb-item` was `display:
  flex` *on the cell*. A cell that is itself a flex container stops stretching to the row's
  height, so its bottom border drew a line halfway up the row whenever another cell was taller.
  The flex moved to a span inside the cell.
- Every row state survives and still reads: goldmine gold edge, hover, zebra, `dt-money` band and
  emphasis, Thin / Worth it / Pass badges — in **both** themes, since everything new is written
  in existing tokens.
- Zebra and the top-three rank colour moved from `nth-child` to `.is-alt` / `.is-top3` set by the
  renderer: a deal is two rows now (the row and its detail), so every second *element* is a
  detail panel rather than every second *deal*.

The budget basket shares `.fb-arb-table` and is a modal table that should still scroll — it keeps
its own `min-width` and its own zebra rather than being dragged along.

**No figure, calculation, sort or filter changed.** Layout and presentation only.

### Verification

| Check | Result |
|---|---|
| `dotnet build … -c Debug` | **succeeded**, 0 errors |
| `dotnet test` | **2316 passed**, 0 failed |
| `node --check wwwroot/app.js` | clean |
| Rebuilt `AutoListerB1.exe` on `:9332`, 30-row board, screenshotted and read | see below |

Measured in the running app on a **30-row** board at 1920px, 1440px and 1000px windows, light and
dark: horizontal overflow **0px**, vertical overflow **0px**, `overflow: clip/clip`,
`max-height: none`, document horizontal scroll **0px**, cells whose content exceeds their box:
**none**, buttons clipped at the frame or rendered under 20px wide: **none**. At 1000px the rows
stack into cards with no sideways scroll. The row detail opens with the resale price, the fees,
ROI, margin and the evidence line all present.

New `DealBoardLayoutTests` (12 tests) locks what nothing in C# could otherwise fail on: the exact
column set and that none of the five folded ones returns as a column; that every figure taken out
of a column is still rendered in the row detail; that the `colspan`s match the column count; that
nothing winning the cascade for the board sets `overflow: auto` or a `min-width` (read in source
order, because the wrapper still shares a class with the budget basket); that `max-height` stays
`none`; that the 1320px cap is broken out of; that every cell carries a `data-label` to stack
under; and that the paired-row states and the disclosure's ARIA wiring are both present.

### Known limits

- **`overflow: clip` and container queries are Chromium-era CSS.** This is a WebView2 app, so
  that is the only engine it has to satisfy — but the board is not styled for an old browser.
- **The board is capped at 2200px.** An ultrawide monitor will show margins past that. Past that
  width a table stops being scannable and becomes a runway; the cap is a judgement, not a
  constraint.
- **The verification scan was a mocked 30-row response**, routed into the real page against the
  real embedded assets. It exercises the layout, not the scrapers.

---

# "Failed to fetch" — the browser's words, shown to a seller

**Reported:** the Listing Copilot ("Fix The Whole Account At Once"), clicking **Scan My Account**,
showed a red bar reading exactly `Could not scan: Failed to fetch`.

## What was actually happening

"Failed to fetch" is the browser's own words for *"this request never reached a server"*. It is a
sentence about the fetch API, shown to somebody who sells on eBay. At the time of the screenshot
the backend was not running — no `AutoListerB1` process, nothing listening on 9332 — and the page
was still open from earlier, so the UI kept working and every call it made died at the network
layer.

The Copilot handler was:

```js
catch (e) { err.textContent = 'Could not scan: ' + e.message + …; }
```

The exception printed straight through. The app already had the machinery to do better, and this
panel used none of it.

---

## Part 1 — never show a raw fetch error again

### The Copilot calls now go through the shared layer

`runCopilotScan`, `runCopilotSeoRewrite`, `stopCopilotSeoRewrite`, `refreshCopilotSeoStatus` and
`openCopilotDrafts` all call `callApi` and report through `renderFailure` — the same "what
happened / what to do / the button that does it / evidence folded away" panel the rest of the app
uses. There is no third error path; there are now none.

Two things the panel gained that it could not have had before:

- **A Try again that re-runs the scan**, wired by the shared renderer rather than hand-rolled.
- **A scan that re-runs itself when the app comes back.** A failed scan registers with
  `whenBackendReturns`, so a seller who restarts the app gets the thing they clicked. The rewrite
  deliberately does *not* self-restart — it costs money and was confirmed against a count that may
  by then be stale, so it stays a button.

### The four failures a seller would act on differently are told apart

`callApi` already distinguished them; it now says them properly:

| Situation | What the seller reads | What they do |
|---|---|---|
| App not reachable | *ING AutoLister is not running* | Restart the app (the page reconnects itself) |
| Request timed out | *That took too long and was stopped* | Try again |
| App answered an error | The server's own sentence, read out | Fix what it named |
| App answered something unreadable | *The app returned an error* | Try again, then Logs |

Two fixes went in on the way past. `callApi`'s own network branch was interpolating
`${err.message}` — *"The connection to ING AutoLister failed (Failed to fetch)"* — so the
reliability layer was leaking the same phrase it existed to prevent. And an endpoint that answers
`{ "error": "..." }` now has that sentence as the headline instead of it being buried under
`HTTP 400` with the real explanation folded away in Technical detail.

### The evidence block is written, not pasted

Technical detail is still shown, because sellers forward it when they ask for help — but pasting
"Failed to fetch" into it just puts the useless phrase one click deeper. The unreachable case
describes itself instead:

```
The request to /api/copilot/scan never reached http://localhost:9332
(TypeError — no response, no status).
```

Which request, and what kind of failure. Nothing to decode.

### The sweep — all 143 `fetch(` call sites, not just the one reported

`errorText(err, fallback)` is now the one place a caught exception becomes words a seller reads.
Server-written messages are already in their language and pass through unchanged; the two failures
the *browser* invents its own wording for — the app not being there, and a request given up on —
are replaced. `technicalDetail(err)` does the same for evidence blocks.

**107 call sites** were rewritten. Every `err.message` / `e.message` below the reliability layer is
gone; a test fails the build if one comes back.

Detection is on the **shape**, not the words: `fetch` only rejects with a `TypeError` when the
request could not be made at all. Chrome says "Failed to fetch", Firefox says "NetworkError when
attempting to fetch resource.", Safari says "Load failed" — matched only as a fallback.

### The page recovers on its own

Calling `errorText` on an unreachable failure is also what starts the watch, so every swept call
site gets the recovery for free. The page then knocks on `/api/app/instance` every 3s — the app's
cheapest endpoint, no services behind it, answers before setup is complete — and when it replies,
the offline banner turns green and says **"Reconnected"** and everything registered with
`whenBackendReturns` runs.

A bottom-pinned banner carries the state, because with the backend gone this is not the Copilot's
problem: nothing on any screen can do anything, and telling the seller about one panel leaves them
to guess about the rest of the page.

---

## Part 2 — why was the backend gone?

**It did not crash. It was closed.** Measured, not assumed:

| Check | Finding |
|---|---|
| `crash.log` in `%LOCALAPPDATA%\ING AutoLister` | **Does not exist.** `AppDomain.UnhandledException` writes it before the process dies, so no unhandled exception ever took this process down. |
| `/api/copilot/scan` | Whole handler inside `try` / `catch (Exception ex)` then `BadRequest(new { error = ex.Message })`. It cannot throw out. |
| `CopilotSeoJob.RunAsync` (the `Task.Run` body) | Whole body inside `try` / `catch` / `finally`. A failed rewrite sets `Stage = "Failed"` and `Error`, and the run still finishes. |
| Maintenance loop (`Program.cs`) | `catch { /* maintenance loop must never crash */ }` around every pass. |
| Facebook / Terapeak login, `LoginWindowFocus.PinLoopAsync` | All three `Task.Run` bodies are wrapped. |

The app is a tray application whose lifetime is `System.Windows.Forms.Application.Run()`. It exits
when someone picks **Quit** from the tray, or when the session that started it ends — which is what
happened here. There is no crash to fix on the Copilot path, and inventing one would have been
worse than saying so.

### What was hardened anyway

Two things were genuinely wrong, both of the exact shape that *would* have caused this:

1. **The background license check was the one fire-and-forget in the app with no catch**
   (`Program.cs`, `Task.Run(async () => { await Task.Delay(2000); await …CheckAsync(); })`). Nothing
   is gated on the answer — the app is free beta — so a check that cannot reach the network is now
   logged and ignored rather than left to fault a task nobody awaits.

2. **A `TaskScheduler.UnobservedTaskException` handler**, the net under all of them including any
   added later. It calls `SetObserved()`, so a faulted background task cannot escalate whatever
   `<ThrowUnobservedTaskExceptions>` is set to, and it writes to the same `crash.log` the
   process-level handler uses so the cause stays readable.

---

## Tests

**`FetchFailureMessageTests`** (9) — the Copilot scan calls `callApi` and not
`fetch(...).then(r => r.json())`; the exact line that produced the screenshot is gone; no
`e.message` / `String(e)` anywhere in the scan; all four Copilot endpoints go through the shared
caller; the retry re-runs the scan and registers `whenBackendReturns`; the three browser phrases
appear nowhere in `app.js`; the technical block is written rather than pasted; the four failure
kinds are distinguished; the probe target is `AppInstance.IdentityPath`; and — the sweep guard —
**every line below the reliability layer that reads `.message` off a caught exception fails the
test, by name**.

**`BackgroundWorkSurvivalTests`** (5) — behavioural, not just source-reading: a `CopilotSeoJob`
built with no eBay service at all (the most abrupt failure available) has to come back with
`Finished`, `Stage == "Failed"` and an `Error` rather than letting the exception escape the
background task; and a failed run must not poison the next one, because a seller whose first
attempt failed will press the button again. Plus the scan endpoint's guard, the license-check
catch, and the unobserved-exception net.

## Verification

| Check | Result |
|---|---|
| `dotnet build … -c Debug` | **succeeded**, 0 errors |
| `dotnet test` | **2331 passed**, 0 failed |
| `node --check wwwroot/app.js` | clean |
| Real app, real account, backend killed mid-session | see below |

`wwwroot` is an `EmbeddedResource`, so this was rebuilt and driven in the running app rather than
read off the source file. Playwright against `AutoListerB1.exe` on `:9332`: open the Listing
Copilot, `taskkill /IM AutoListerB1.exe /F`, then click **Scan My Account**.

What the seller is told, verbatim from the live page:

> **ING AutoLister is not running**
> This page is still open, but nothing answered it. The app behind it has been closed or has
> stopped, so nothing on this screen can reach your listings until it is back.
> **Start ING AutoLister again — the price-tag icon in your system tray, or the desktop shortcut.
> This page reconnects by itself the moment it does; there is no need to reload it.**
> Nothing you entered has been lost. \[Try again] \[Technical detail]

`contains "Failed to fetch": false` — panel, banner and technical detail.
See `docs/screenshots/backend-not-running-copilot-scan.png`.

Then the backend was restarted and **the page was not touched**. It noticed by itself: the banner
turned green and said "Reconnected. ING AutoLister is answering again — carry on where you left
off.", the red panel cleared, and the scan the seller had originally clicked re-ran against their
real account — **89 live listings read, 8 need work, 2 policies would be renamed**.
See `docs/screenshots/backend-not-running-recovered.png`.

## Known limits

- **A closed app is still a closed app.** The page can only explain and wait; nothing in a browser
  can restart a Windows tray process. What changed is that the seller is told which of those two
  things is true and what to do about it.
- **The recovery poll runs while the tab is open**, every 3s, and only after something has already
  failed. It stops the moment the app answers.
- **Server-written error messages are trusted as seller-readable** and pass through `errorText`
  unchanged. That is `FailureTranslator`'s job and it already does it; this change does not
  second-guess it.

---

# Every deal row carries the item's real photo

**Branch** `auto/queue-features-20260726` · **Reported as:** "these items have to have pictures"

A seller ran the local deals board with **Free & free-after-coupon** ticked, zip **02341**, radius
**40 miles**, blank keyword. 298 results came back, 30 were shown, and **every single row rendered
the brown 📦 placeholder instead of a photograph**. Nobody can judge a free pool table from the
words "Pool Table" — on this board the photo *is* the decision.

## What was actually wrong

Measured first, against the running build, before anything was changed.

| Measurement | Result |
|---|---|
| Rows returned by `/api/local/arbitrage` for that exact search | 30 |
| Rows with a non-empty `imageUrl` in the JSON | **0 of 30** |

So the images were never reaching the browser — this was upstream in the data, not hotlink
blocking or a rendering fault.

The path that was supposed to supply them read the schema.org `ld_searchpage_results` block out of
Craigslist's no-JavaScript results page and keyed the images by post title. The prior suspicion was
that the title key was missing. **It was not — there was nothing there to key.** Captured live from
the free-stuff board:

| Craigslist free board (`/search/zip`), 40 mi of 02341 | Measured |
|---|---|
| Static results rows in the page | 358 |
| `<img>` tags in the whole response | **0** |
| Occurrences of `images.craigslist.org` | **0** |
| `ld_searchpage_results` block present | yes |
| Entries inside it | **0** — a 79-byte document, `itemListElement: []` |
| RSS feed (`&format=rss`), the old thumbnail source | **blocked** — "Your request has been blocked" |

The same block on the *for-sale* board carries 279 populated entries, which is why the title-keyed
reader looked like it worked. On the free board it parsed cleanly and found an empty list, so every
row degraded to the placeholder in silence. Re-keying by post id would have fixed nothing: the
JSON-LD entries carry **no URL or post id field at all** (`item` keys are `@context`, `@type`,
`description`, `image`, `name`, `offers`).

## The fix

The photographs exist and Craigslist will serve them — its own results grid shows them. New
`Services/CraigslistSearchApi.cs` reads the search from the endpoint that grid calls, which returns
the image id for every post that has one.

**The cost model is unchanged.** This *replaces* the results-page GET rather than adding to it —
one search is still one request per source. Nothing follows a post into its own page and nothing
pages. `ParseStaticHtml` stays behind it as the fallback, so if the endpoint ever moves, behaviour
falls back to exactly what shipped before.

Rows are joined on the **post token**, which the endpoint states for 360 of 360 posts and which is
also the last segment of the post's permalink — exact and collision-proof, where a title cannot be
(the free board really does carry two different couches both titled "FREE couch").

Because the response is positional JSON from an endpoint Craigslist does not document, everything
that matters is read **by tag code or by shape, not by index**. The one exception, the numeric
price, was checked against the display price on **695 live posts across both boards — 695 agreed,
0 disagreed**. An unrecognised shape yields zero listings, which is the signal `CraigslistService`
already acts on by falling back to the HTML page.

One bug was found and fixed by measurement while writing it: the town is the geo index **after**
the colon, not before. The two are equal often enough to look interchangeable — across 120 live
posts the second index matched the post's own URL slug **80** times and the first matched **2**.

Also changed:

- **A miss is now visible.** `CraigslistService` logs photo coverage on every search, and logs it
  at **Warning** when rows came back and *none* of them have photos — the exact silent failure
  reported here. Live: `11 local listing(s) — 7 of 11 with photos.`
- **A broken URL falls back cleanly.** The thumbnail carries an `onerror` handler that swaps in the
  same "no photo" box, so a 404 or a refused hotlink never shows the browser's broken-image glyph,
  which reads as the app being broken rather than the post being bare.
- **The 📦 box now means one thing** — *this listing has no photo* — and says so on hover.

## Result — same search, same build, measured

| Free & free-after-coupon · 02341 · 40 mi · blank query | Before | After |
|---|---|---|
| Rows shown | 30 | 30 |
| **Rows with a real photograph** | **0** | **23** |
| Rows with no photo | 30 | 7 |

All 7 remaining blanks were checked against Craigslist itself and **genuinely have no photograph** —
the endpoint reports no image for them, and fetching one of the posts directly ("Box Fan") confirms
zero image URLs on its page. Those are the only rows the 📦 box is now allowed to describe.

## Every other source in the table was audited too

The rule is for the table, not for one source.

| Source | Rows with a photo | Verdict |
|---|---|---|
| Free & free-after-coupon | 0 → **23 of 30** | **fixed** |
| Craigslist (for-sale board) | **29 of 30** | already correct — the 1 blank has no photo on its post page |
| Retail deal feeds | **19 of 21** | already correct — the 2 blanks are the exact 2 Slickdeals items carrying no image in the feed |
| Facebook Marketplace | **8 of 8** | already correct |
| Liquidation & closeouts | **162 of 163 lots** carry `thumbnailLocation` | already correct — the parser reads that field. Could not be scanned live: HiBid was rate-limiting this IP throughout, before and after the change |
| eBay | reads the Browse API's `image.imageUrl` | already correct |

## Verification

| Check | Result |
|---|---|
| `dotnet build … -c Debug` | **succeeded**, 0 errors |
| `dotnet test` | **2424 passed**, 0 failed — 27 of them new here, no pre-existing test changed or removed |
| `node --check wwwroot/app.js` | clean |
| Rebuilt app, real search | see above — 0 → 23 photos |

`wwwroot` is an `EmbeddedResource`, so this was verified by rebuilding and driving the running
`AutoListerB1.exe` on `:9332`, not by reading the source file. The server confirmed it was serving
the new asset (`app.js` containing `arbThumbHtml` and `__arbThumbFailed`; `index.html` asking for
`app.js?v=92`) before the search was re-run.

New tests, all against **real captured fixtures**:

- `CraigslistSearchApiTests` — the free-board response verbatim, including **two different couches
  with the same title that must each get their own photo**, and a genuinely photo-less post that
  must not inherit somebody else's.
- `DealRowPhotoTests` — one photo assertion per source (Craigslist, deal feeds, liquidation,
  Facebook); the free-board results page kept as evidence that it carries no photograph at all; the
  photo surviving parser → `LocalSupplyListing` → `LocalArbitrageAnalyzer` → `row.imageUrl`; and the
  board rendering an `<img>`, falling back on error, and labelling the empty box.

## Known limits

- **Some free posts genuinely have no photograph.** 7 of these 30 do not, and no amount of parsing
  invents one. They show the 📦 box, which now says so.
- **The search endpoint is undocumented.** Mitigated three ways: read by tag rather than by
  position, the HTML page kept as the fallback, and photo coverage logged on every search so the
  next silent failure announces itself instead of showing 30 empty boxes.
- **Liquidation could not be measured live.** HiBid rate-limited this IP for the whole session,
  before and after the change alike. It was audited from a captured search page instead.
