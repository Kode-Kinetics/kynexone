#!/usr/bin/env bash
# Restore KynexOne from a backup folder written by backup.sh. This REPLACES the current database and documents.
#
#   scripts/restore.sh backups/20261025T020000Z [--yes]
#
# The backup is restored with the version it was taken from (manifest.txt): restoring an older backup also
# returns the installation to that version, which is how an upgrade is rolled back.
source "$(dirname "$0")/_common.sh"

SRC="${1:-}"; YES="${2:-}"
[ -n "$SRC" ] && [ -d "$SRC" ] || die "Usage: scripts/restore.sh <backup folder> [--yes]"
SRC="$(cd "$SRC" && pwd)"
[ -s "$SRC/database.dump" ] || die "$SRC/database.dump is missing."
require_docker; require_env

if [ -f "$SRC/manifest.txt" ] && (cd "$SRC" && grep -E '^[0-9a-f]{64} ' manifest.txt | sha256sum -c --quiet -); then
  say "Backup checksums verified"
else
  warn "Could not verify the backup checksums."
fi

BACKUP_VERSION="$(grep -E '^kynexone_version=' "$SRC/manifest.txt" 2>/dev/null | cut -d= -f2- || true)"
if [ "$YES" != "--yes" ]; then
  read -r -p "Replace ALL current KynexOne data with the backup from $(basename "$SRC")? Type RESTORE: " answer
  [ "$answer" = "RESTORE" ] || die "Cancelled."
fi

DB="$(env_get POSTGRES_DB)"; DB="${DB:-kynexone}"
DB_USER="$(env_get POSTGRES_USER)"; DB_USER="${DB_USER:-kynexone}"

say "Stopping the application"
compose stop caddy web api

if [ -n "$BACKUP_VERSION" ] && [ "$BACKUP_VERSION" != "$(env_get KYNEXONE_VERSION)" ]; then
  say "Returning to version $BACKUP_VERSION (the backup's version)"
  env_set KYNEXONE_VERSION "$BACKUP_VERSION"
fi

compose up -d --wait postgres
say "Restoring the database"
compose exec -T postgres psql -U "$DB_USER" -d postgres -v ON_ERROR_STOP=1 \
  -c "DROP DATABASE IF EXISTS \"$DB\" WITH (FORCE);" -c "CREATE DATABASE \"$DB\" OWNER \"$DB_USER\";"
compose exec -T postgres pg_restore -U "$DB_USER" -d "$DB" --no-owner --exit-on-error < "$SRC/database.dump"

if [ -f "$SRC/documents.tar.gz" ]; then
  say "Restoring the documents"
  docker run --rm -i -v kynexone_documents:/documents postgres:16-alpine \
    sh -c 'find /documents -mindepth 1 -delete && tar xzf - -C /documents && chown -R 1654 /documents' < "$SRC/documents.tar.gz"
fi

say "Starting the application"
api_command --maintenance off
compose up -d
wait_ready 60 || die "KynexOne did not become ready after the restore. See: docker compose logs api"
say "Restore complete."
