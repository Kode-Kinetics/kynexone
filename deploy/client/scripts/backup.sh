#!/usr/bin/env bash
# Back up KynexOne: the database, the documents and the settings.
#
#   scripts/backup.sh [--to /path/to/backups]      (default ./backups)
#
# Each backup is a folder named after its UTC time holding
#   database.dump      pg_dump custom format. It includes the data-protection keys (DataProtectionKeys table):
#                      without them, encrypted MFA secrets cannot be read and every user must enrol MFA again.
#   documents.tar.gz   every document (the documents volume; an external S3 store is backed up with its own tools)
#   env.backup         the settings and secrets
#   manifest.txt       version, time and SHA-256 of each file
# Store backups as carefully as the server, and copy them off it: a backup on the same disk does not survive it.
source "$(dirname "$0")/_common.sh"

DEST="$KIT_DIR/backups"
while [ $# -gt 0 ]; do
  case "$1" in
    --to) DEST="${2:?--to needs a directory}"; shift 2 ;;
    -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
done

require_docker; require_env
mkdir -p "$DEST"; DEST="$(cd "$DEST" && pwd)"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="$DEST/$STAMP"
mkdir -p "$OUT"; chmod 700 "$OUT"

say "Dumping the database"
compose exec -T postgres pg_dump -U kynexone -d kynexone -Fc > "$OUT/database.dump"
[ -s "$OUT/database.dump" ] || die "The database dump is empty."

if [ "$(env_get STORAGE_PROVIDER)" = "s3" ]; then
  warn "Documents are in an external S3 store ($(env_get STORAGE_ENDPOINT)); back it up with that store's own tools."
else
  say "Archiving the documents"
  docker run --rm -v kynexone_documents:/documents:ro postgres:16-alpine tar czf - -C /documents . > "$OUT/documents.tar.gz"
fi

cp .env "$OUT/env.backup"; chmod 600 "$OUT/env.backup"
{
  echo "created_utc=$STAMP"
  echo "kynexone_version=$(env_get KYNEXONE_VERSION)"
  echo "domain=$(env_get KYNEXONE_DOMAIN)"
  echo "storage_provider=$(env_get STORAGE_PROVIDER)"
  (cd "$OUT" && for f in database.dump documents.tar.gz env.backup; do [ -f "$f" ] && sha256sum "$f"; done; true)
} > "$OUT/manifest.txt"

echo "$OUT" > "$DEST/LATEST"
say "Backup written to $OUT"
