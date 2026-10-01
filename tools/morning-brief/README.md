# ING Morning Brief

One ordered to-do list for the owner every morning: what to ship, who to answer, what is in
transit, open returns and cancel requests, money, appointments, packages — built from the
Microsoft 365 inbox, the eBay Sell APIs and the ING Telegram alert bot, then e-mailed to
ns@ingmining.com and pushed to Telegram.

| file | role |
|---|---|
| `brief.py` | builds the brief, saves it, sends it (`--no-send` to only print, `--days N` for the mail window) |
| `ebay_orders.py` | orders / shipments / returns / cancellations via Sell Fulfillment + Post-Order APIs, read-only |
| `install-task.ps1` | registers the daily 06:30 scheduled task "ING Morning Brief" |

Runtime pieces that are NOT in this repo (per-machine, per-user):

* `%LOCALAPPDATA%\ING\OutlookMCP\outlook_mcp.py` + `config.json` — Microsoft Graph access to the
  mailbox through the Entra app "ING Outlook Reader (MCP)" (application permissions
  Mail.ReadWrite + Mail.Send, client secret DPAPI-encrypted). No sign-in, survives reboots.
* `%LOCALAPPDATA%\ING AutoLister\credentials.json` — the desktop app's eBay token; this tool only
  reads it and keeps its own refreshed token in `morning-brief\ebay-token.json`.
* `%LOCALAPPDATA%\ING AutoLister\antminer-watch\telegram.dpapi` — the alert bot pairing.

Output lands in `%LOCALAPPDATA%\ING AutoLister\morning-brief\` (`YYYY-MM-DD.md`, `latest.md`,
`send.log`, `error.log`).

Not covered yet: Amazon orders (SP-API is not live — Amazon's e-mails are used instead), personal
Telegram chats and WhatsApp (both need a one-time sign-in by the owner; the brief says so daily).
