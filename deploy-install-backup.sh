#!/usr/bin/env bash
#
# Installs the nightly database backup on the server: the script itself, the cron entry that
# fires it, and a logrotate rule so its own log cannot become the thing that fills the disk.
# Idempotent — running it again just re-installs the same three files.
#
# Run from the repository root (in WSL, where the deploy key lives):
#     bash deploy-install-backup.sh
set -euo pipefail

SSH="ssh -i $HOME/.ssh/ing_hetzner -o StrictHostKeyChecking=accept-new root@178.156.154.41"

# The working directory, not dirname "$0": the CRLF strip these scripts need copies this file to
# /tmp first, which would make dirname point at /tmp and the payload unfindable.
HERE="$(pwd)"
[ -f "$HERE/ing-backup.sh" ] || { echo "run this from the repository root (no ing-backup.sh in $HERE)"; exit 1; }

# The repository is checked out on Windows, so ing-backup.sh may carry CRLF line endings. A
# shebang line ending in \r makes the kernel look for an interpreter named "bash\r" and the only
# error is "no such file or directory" naming a file that plainly exists.
echo "--- installing /usr/local/bin/ing-backup.sh ---"
tr -d '\r' < "$HERE/ing-backup.sh" | $SSH 'cat > /usr/local/bin/ing-backup.sh && chmod 700 /usr/local/bin/ing-backup.sh'

echo "--- sqlite3 (the backup uses .backup, not cp) ---"
$SSH 'command -v sqlite3 >/dev/null || { apt-get update -qq && apt-get install -y -qq sqlite3; }; sqlite3 --version'

echo "--- cron entry + logrotate ---"
$SSH 'cat > /etc/cron.d/ing-listing-engine-backup' <<'CRON'
# Nightly backup of the hosted deployment's database. See /usr/local/bin/ing-backup.sh.
#
# 03:17 rather than 03:00: every other cron job on every other box in the world runs on the hour,
# and this one shares a 2 GB machine with the app.
#
# MAILTO is empty because there is no MTA here — without it cron's own log fills with "sendmail:
# not found" and the actual backup output is what gets lost. The output goes to the file below;
# a failure is a line in it that starts with FAILED.
SHELL=/bin/bash
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
MAILTO=""
17 3 * * * root /usr/local/bin/ing-backup.sh >> /var/log/ing-listing-engine-backup.log 2>&1
CRON

# A cron.d file must be root-owned and not group- or world-writable, or cron ignores it silently.
# It must also NOT be executable, and its name must contain no dot — run-parts skips both.
$SSH 'chown root:root /etc/cron.d/ing-listing-engine-backup && chmod 644 /etc/cron.d/ing-listing-engine-backup'

$SSH 'cat > /etc/logrotate.d/ing-listing-engine-backup' <<'ROTATE'
/var/log/ing-listing-engine-backup.log {
    monthly
    rotate 6
    compress
    missingok
    notifempty
    create 640 root adm
}
ROTATE

echo "--- installed ---"
$SSH 'ls -l /usr/local/bin/ing-backup.sh /etc/cron.d/ing-listing-engine-backup /etc/logrotate.d/ing-listing-engine-backup'
