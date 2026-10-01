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
