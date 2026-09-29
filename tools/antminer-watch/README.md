# Antminer deal email watcher

Uses the installed ING Listing Engine's read-only eBay search, individual sold-comps records, and listing-detail endpoints. It never arms Auto-Buy or places an order.

- One scan per minute while this Windows account is signed in, awake, and ING Listing Engine is running on port 9332.
- First scan records the current newest 50 listings without sending backlog alerts. Subsequent newly observed listings are evaluated once. eBay search indexing can delay availability; this is polling, not instant push.
- US fixed-price listings; seller feedback >=20 and >=98%; stated shipping required.
- Exact parsed Antminer variant and hashrate; at least three unique, dated sales in the last 60 days. Price plus shipping must be >=30% and >=$100 below the sold-item median. Tax is excluded. Sold shipping is omitted, making the threshold conservative.
- Reject parts, repair, untested, accessories, lots, and unclear model/hashrate. Before email, retrieve full description and require working-condition and PSU evidence. Broad negative-word screening can suppress valid deals; seller claims are not hardware verification.
- Windows DPAPI protects mail credentials for this account. Runtime status, SQLite deduplication, and logs live in `%LOCALAPPDATA%\ING AutoLister\antminer-watch`. No credentials belong in this repository.
- `--test-email` sends one clearly labeled test. `--monitor-only` collects qualifying evidence without sending mail. `--once` runs one scan.
- Stop/pause with Task Scheduler task `ING Antminer Deal Email Watch`.

Verification: `python -m unittest discover -s tools/antminer-watch -p test_watch.py`.
