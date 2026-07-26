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
