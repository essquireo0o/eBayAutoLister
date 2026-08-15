#!/usr/bin/env bash
# Lists the variable NAMES currently set in the server's .env, with value lengths only.
#
# Exists because the env file is regenerated wholesale by deploy-make-env.ps1 and pushed over the
# top of the live one. Anything the server has that the generator does not know about would be
# silently deleted by that push — so the names are read first, every time, before pushing.
set -euo pipefail
SSH="ssh -i $HOME/.ssh/ing_hetzner -o StrictHostKeyChecking=accept-new -o ConnectTimeout=20 root@178.156.154.41"
$SSH 'awk -F= "/^[A-Za-z0-9_]+=/ {printf \"%s = <%d chars>\n\", \$1, length(\$0)-length(\$1)-1}" /etc/ing-listing-engine/.env'
