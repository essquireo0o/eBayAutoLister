#!/usr/bin/env bash
# Pushes the generated .env to the server over ssh stdin, so no secret is ever an argument to a
# command (argv is world-readable in ps) and none of it reaches this terminal.
set -euo pipefail

SRC="/mnt/c/Users/nsquires/AppData/Local/Temp/ing-listing-engine.env"
SSH="ssh -i $HOME/.ssh/ing_hetzner -o StrictHostKeyChecking=accept-new root@178.156.154.41"

$SSH 'umask 077; mkdir -p /etc/ing-listing-engine; cat > /etc/ing-listing-engine/.env; chown root:root /etc/ing-listing-engine/.env; chmod 600 /etc/ing-listing-engine/.env' < "$SRC"

# Remove the local copy: it held the Anthropic key in the clear in the Windows temp folder.
shred -u "$SRC" 2>/dev/null || rm -f "$SRC"

echo "--- permissions ---"
$SSH 'ls -l /etc/ing-listing-engine/.env; stat -c "%a %U:%G" /etc/ing-listing-engine/.env'
echo "--- contents, values redacted ---"
$SSH "sed -E 's/^([A-Za-z0-9_]+)=(.+)\$/\\1=<set, \${#2} chars>/' /etc/ing-listing-engine/.env | sed -E 's/<set, [0-9]+ chars>/<set>/'"
echo "--- CR check (should be 0) ---"
$SSH "grep -c \$'\\r' /etc/ing-listing-engine/.env || true"
