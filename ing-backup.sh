#!/usr/bin/env bash
#
# Nightly backup of the hosted deployment's database. Runs ON THE SERVER as root, installed at
# /usr/local/bin/ing-backup.sh by deploy-install-backup.sh and fired by
# /etc/cron.d/ing-listing-engine-backup. Safe to run by hand at any time.
#
# What it produces: one file per calendar day in /var/backups/ing-listing-engine/ —
#
#   ing-listing-engine-YYYY-MM-DD.tar.gz
#     ├── ing_listing_engine.db   the database: users, listings, encrypted eBay credentials
#     └── auth-keys/              the data-protection key ring the session cookies are signed with
#
# auth-keys/ is in here because a database restored without it signs every user out on the way
# back up — the two are one recovery unit, and separating them turns one restore into two.
#
# The database is copied with sqlite3's .backup and NOT with cp. A SQLite database in WAL mode
# keeps committed transactions in a -wal sidecar until a checkpoint moves them, so copying the
# .db alone from underneath a running app silently loses the most recent writes and the loss is
# not visible until the restore. `.backup` takes the same locks the app does and produces one
# consistent file whether or not anything is being written at the time.
#
# Nothing here stops the container. A backup that requires downtime is a backup that gets skipped.

set -euo pipefail

VOLUME=ing-listing-engine_ing-listing-data
DEST=/var/backups/ing-listing-engine
KEEP=7                       # nightly, one file per day — so this is seven days

fail() { echo "$(date -Is) FAILED: $*" >&2; exit 1; }

# The volume's location is asked for rather than assumed: /var/lib/docker is the default and not
# a promise, and a hard-coded path that quietly stops matching would back up nothing at all while
# still reporting success.
mount=$(docker volume inspect "$VOLUME" --format '{{.Mountpoint}}' 2>/dev/null) \
  || fail "docker volume $VOLUME does not exist — is this the right box?"

DB="$mount/ING AutoLister/App_Data/ing_listing_engine.db"
KEYS="$mount/ING AutoLister/App_Data/auth-keys"
[ -f "$DB" ] || fail "no database at $DB"

mkdir -p "$DEST"
chmod 700 "$DEST"            # the database holds every user's encrypted eBay grant

# Staged inside $DEST so the final move is a rename on the same filesystem — atomic, so a crash
# or a full disk mid-run leaves no half-written archive that a restore would trust.
tmp=$(mktemp -d "$DEST/.staging.XXXXXX")
trap 'rm -rf "$tmp"' EXIT

sqlite3 "$DB" ".backup '$tmp/ing_listing_engine.db'" \
  || fail "sqlite3 .backup refused the database"

# A backup nobody has read is a hope, not a backup. This is the cheapest possible read of every
# page, and it runs against the COPY — so a corrupt archive is caught tonight rather than on the
# morning it is needed.
check=$(sqlite3 "$tmp/ing_listing_engine.db" 'PRAGMA integrity_check;' | head -1)
[ "$check" = "ok" ] || fail "integrity check on the backup said: $check"

[ -d "$KEYS" ] && cp -a "$KEYS" "$tmp/auth-keys"

stamp=$(date +%F)
archive="$DEST/ing-listing-engine-$stamp.tar.gz"
tar czf "$tmp/archive.tar.gz" -C "$tmp" ing_listing_engine.db $([ -d "$tmp/auth-keys" ] && echo auth-keys)
mv "$tmp/archive.tar.gz" "$archive"
chmod 600 "$archive"

# Keep the newest $KEEP and delete the rest. Two deliberate choices:
#
# By COUNT rather than by age (`find -mtime +7`), because count is what survives the case that
# matters: if the job stops running, age-based pruning deletes the last good backup on day eight
# and leaves nothing at all.
#
# By NAME rather than by mtime (`ls -1t`), because the names are ISO dates and therefore already
# sort chronologically, while an mtime can be rewritten by anything that touches the file — a
# copy pulled back from off-box for a restore rehearsal is newer than tonight's real backup and
# would push it off the end of the list.
mapfile -t stale < <(ls -1 "$DEST"/ing-listing-engine-*.tar.gz 2>/dev/null | sort -r | tail -n +$((KEEP + 1)))
if [ ${#stale[@]} -gt 0 ]; then
  rm -f -- "${stale[@]}"
  echo "$(date -Is) pruned ${#stale[@]} backup(s) beyond the newest $KEEP"
fi

echo "$(date -Is) ok $archive ($(stat -c %s "$archive") bytes, integrity_check=$check, $(ls -1 "$DEST"/ing-listing-engine-*.tar.gz | wc -l) kept)"
