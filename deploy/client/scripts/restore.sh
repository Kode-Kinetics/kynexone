#!/usr/bin/env bash
# Restore KynexOne from a backup folder written by backup.sh. This REPLACES the current database and documents.
#
#   docker compose stop backend web     # restore refuses to run while the API is running
#   scripts/restore.sh backups/20261025T020000Z [--yes]
#
# The backup comes back with the version it was taken from (manifest.txt): restoring the backup upgrade.sh took
# returns the installation to the previous version and data, which is how a failed upgrade is rolled back.
# On a new server: install the kit, copy env.backup to .env (it carries the secrets the data needs), then restore.
source "$(dirname "$0")/_common.sh"

SRC="${1:-}"; YES="${2:-}"
[ -n "$SRC" ] && [ -d "$SRC" ] || die "Usage: scripts/restore.sh <backup folder> [--yes]"
SRC="$(cd "$SRC" && pwd)"
[ -s "$SRC/database.dump" ] || die "$SRC/database.dump is missing."
require_docker; require_env

backend_running && die "The API is running. Stop it first: docker compose stop backend web"

if [ -f "$SRC/manifest.txt" ]; then
  (cd "$SRC" && grep -E '^[0-9a-f]{64} ' manifest.txt | sha256sum -c --quiet -) || die "The backup's checksums do not match its files."
  say "Backup checksums verified"
else
  warn "The backup has no manifest.txt; its files cannot be verified."
fi
BACKUP_VERSION="$(sed -n 's/^kynexone_version=//p' "$SRC/manifest.txt" 2>/dev/null || true)"

if [ "$YES" != "--yes" ]; then
  read -r -p "Replace ALL current KynexOne data with the backup from $(basename "$SRC")? Type RESTORE: " answer
  [ "$answer" = "RESTORE" ] || die "Cancelled."
fi

compose stop proxy >/dev/null 2>&1 || true
if [ -n "$BACKUP_VERSION" ] && [ "$BACKUP_VERSION" != "$(env_get KYNEXONE_VERSION)" ]; then
  say "Returning to version $BACKUP_VERSION (the backup's version)"
  env_set KYNEXONE_VERSION "$BACKUP_VERSION"
fi

compose up -d --wait postgres redis
say "Restoring the database"
compose exec -T postgres psql -U kynexone -d postgres -v ON_ERROR_STOP=1 \
  -c 'DROP DATABASE IF EXISTS kynexone WITH (FORCE);' -c 'CREATE DATABASE kynexone OWNER kynexone;'
compose exec -T postgres pg_restore -U kynexone -d kynexone --no-owner --exit-on-error < "$SRC/database.dump"

if [ -f "$SRC/documents.tar.gz" ]; then
  say "Restoring the documents"
  docker run --rm -i -v kynexone_documents:/documents postgres:16-alpine \
    sh -c 'find /documents -mindepth 1 -delete && tar xzf - -C /documents && chown -R 1654 /documents' < "$SRC/documents.tar.gz"
fi

say "Starting KynexOne"
api_command --maintenance off
compose up -d
wait_ready 60 || die "KynexOne did not become ready after the restore. See: docker compose logs backend"
smoke_check || warn "Smoke checks reported a problem (above)."
say "Restore complete."
