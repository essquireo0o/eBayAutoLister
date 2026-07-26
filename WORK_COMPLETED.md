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
Run   : AUTOLISTER_DEV_PORT=9332 ./bin/Debug/net10.0-windows/AutoListerB1.exe
```

`AUTOLISTER_DEV_PORT` runs a second instance on another port beside the installed app (which uses port 9332).

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
AUTOLISTER_DEV_PORT=9332 ./ING\ eBay\ AutoLister/bin/Debug/net10.0-windows/AutoListerB1.exe
```

Port 9332 belongs to the installed app — set AUTOLISTER_DEV_PORT to another port for a dev instance.
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
