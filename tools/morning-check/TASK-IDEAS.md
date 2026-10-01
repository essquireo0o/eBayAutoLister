# Morning list — task ideas (from session a167, 2026-09-30)

For the Morning Desk queue (session 340d owns it: `~/.claude-queue/morning-desk.json`) and for the
sessions that own the collectors. I own none of the files named here; these are tasks, not edits.

**Done means:** `python tools/morning-check/check_morning_tasks.py --live` prints `OK` (exit 0).
Each task names the check rule it turns green. State on 2026-09-30 21:33 ET: 6 fail, 4 warn.

## Who has what (as found tonight)

| Piece | Where | Session |
|---|---|---|
| 06:30 brief, e-mailed + sent to Telegram; eBay orders/returns/cancels | `tools/morning-brief/` (f2ac7a8), task "ING Morning Brief" | 2125a |
| Record everything + `morning_tasks` table + Desktop HTML page | `Documents\ING Assistant\Morning\ing_morning.py` | 585b |
| Queue, dashboard, iPhone texts/WhatsApp, personal Telegram, Amazon to-ship | new repo `ING Morning Desk` | 340d |
| Pass/fail check of the list | `tools/morning-check/` | a167 (this) |

## Tasks

1. **One list, not three.** The 06:30 brief, the Desktop HTML page and the coming dashboard each
   work out "do first" on their own, from their own collectors, and already disagree: the brief has
   3 returns and 1 cancel request, `morning_tasks` has none. Make `morning_tasks` the only place a
   line is decided; the brief and the dashboard only render it. *Rules: missing-return,
   missing-cancel request.*
2. **Returns and cancel requests into `morning_tasks`.** `tools/morning-brief/ebay_orders.py`
   already reads both (Post-Order API, `IAF` auth); the recorder does not call it. A cancel request
   has a respond-by date and is the costliest line to miss. *Rules: missing-return, missing-cancel
   request.*
3. **The brief must not become a task.** The 06:30 e-mail is sent from and to ns@ingmining.com; the
   recorder then lists "Reply: Morning brief 2026-09-30: 7 things to do". Skip mail from the owner's
   own address. *Rule: own-mail.*
4. **Answered means gone.** Amazon sends "Your e-mail to Jose" when the owner replies; the brief
   already uses that, the recorder still shows Jose as open. Same for eBay: "ingmining sent a message
   about…" is the owner's own message (lukewarmiq), and `Replied=true` alone should clear a thread
   (today it needs `Replied` AND `Read`). *Rules: answered, own-thread.*
5. **Dates in the owner's day.** Rows are dated by the first 10 characters of a UTC timestamp, so
   everything after 8 PM Eastern is dated tomorrow. `brief.py` hard-codes UTC-4, which is wrong
   from 2026-11-01. Use `zoneinfo("America/New_York")` in both. *Rule: utc-date.*
6. **Advertising is not "Reply:".** SoFi, Rex MD, Valvoline, Airgun Depot, Ace Rewards sit at
   priority 3 as things to reply to. `brief.py` has the better filter (`NOISE_SENDERS` + the
   subject test); use one filter in both. *Rule: ad-as-task.*
7. **A link that opens the item.** All 8 eBay questions link to the same inbox URL. Link each to its
   own message or to the listing it is about. *Rule: shared-link.*
8. **Tick it off from the phone.** Nothing lets the owner mark a line done or "tomorrow". The
   Telegram bot is already paired: send each do-first line with Done / Tomorrow buttons and write
   the answer to `morning_tasks.status`. Without this the list only shrinks when the source changes.
9. **Do not record secrets.** The recorder stores 1,500 characters of every e-mail in a database
   that the form-filler's local bridge, the chat-memory MCP server and Codex all read. Sign-in
   codes, password resets and bank mail are in that stream. Store the subject only for those (the
   brief's `security` and `payment` buckets), never the body.
10. **Outbound packages: exceptions only.** The brief lists all 10 shipments in transit every day.
    The owner needs the ones that are late, returned to sender or delivered-but-disputed.
11. **Drafts, never sends.** For each open buyer question, leave a draft answer (Outlook draft via
    `outlook_create_draft`; for eBay, text to paste). Sending stays with the owner.
12. **Run the check after every collect** and put its last line at the top of the brief, so a broken
    collector shows up as "list not verified" instead of as a quiet, short list.

## Only the owner can do these (every session is blocked on them)

- **Amazon orders:** the real LWA client secret, and authorising the app in Seller Central.
- **Personal Telegram:** API id + hash from my.telegram.org, then the one-time phone code.
- **WhatsApp:** scan one QR code (linked device).
- **iPhone texts:** plug the phone into this PC, tap Trust, and make one local backup with the
  Apple Devices app; texts are read from that backup. Windows has no dependable way to read
  iMessage history live, so the backup is the route.
