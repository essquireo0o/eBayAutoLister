#!/usr/bin/env bash
# What the hosted deployment has actually spent, read from the app's own audit trail rather than
# from the vendor's dashboard. Every call is a row, including the ones that failed.
#
# The database is copied out and read here because the runtime image carries no sqlite3 and no
# python — deliberately, it is an aspnet image and nothing more.
set -euo pipefail
SSH="ssh -i $HOME/.ssh/ing_hetzner -o StrictHostKeyChecking=accept-new -o ConnectTimeout=20 root@178.156.154.41"

$SSH 'docker exec ing-listing-engine sh -lc "ls -la \"/data/ING AutoLister/App_Data/\""'

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
$SSH 'docker cp "ing-listing-engine:/data/ING AutoLister/App_Data/ing_listing_engine.db" /tmp/audit.db >/dev/null && cat /tmp/audit.db && rm -f /tmp/audit.db' > "$TMP/audit.db"

python3 - "$TMP/audit.db" <<'EOF'
import sqlite3, sys
con = sqlite3.connect(sys.argv[1])
print("--- calls spent (newest first) ---")
for row in con.execute("""SELECT user_id, query, outcome, rows_found, rows_new, http_status, at
                          FROM live_comps_calls ORDER BY id DESC LIMIT 10"""):
    print(" ", row)
print("--- per-account allowance used today ---")
for row in con.execute("SELECT * FROM live_comps_usage"):
    print(" ", row)
EOF
