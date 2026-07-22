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

`AUTOLISTER_DEV_PORT` runs a second instance beside the installed service (port 9331).

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

- **PID 8580** — `INGAutoLister` Windows **service**, port 9331. **Left running, untouched.**
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

Port 9331 belongs to the installed Windows service — always use a dev port instead.
`wwwroot` files are embedded resources, so **UI edits require a rebuild** to take effect.
- Phase 8 — AI Listing workflow improvements
- Phase 9 — GUI polish pass
- Phase 10+ — Additional tests, bug fixes, accessibility
