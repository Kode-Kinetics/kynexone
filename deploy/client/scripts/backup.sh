#!/usr/bin/env bash
# Back up KynexOne: the database (pg_dump custom format), the document store and the settings.
#
#   scripts/backup.sh [--to /path/to/backups]      (default ./backups)
#
# Each backup is a folder named after its UTC time holding database.dump, documents.tar.gz, env.backup and
# manifest.txt. env.backup holds the secrets: store the backups as carefully as the server itself. Copy them
# off this server; a backup on the same disk does not survive the disk.
source "$(dirname "$0")/_common.sh"

DEST="$KIT_DIR/backups"
while [ $# -gt 0 ]; do
  case "$1" in
    --to) DEST="${2:?--to needs a directory}"; shift 2 ;;
    -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
done

require_docker; require_env
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="$DEST/$STAMP"
mkdir -p "$OUT"; chmod 700 "$OUT"
DB="$(env_get POSTGRES_DB)"; DB_USER="$(env_get POSTGRES_USER)"

say "Dumping the database"
compose exec -T postgres pg_dump -U "${DB_USER:-kynexone}" -d "${DB:-kynexone}" -Fc > "$OUT/database.dump"
[ -s "$OUT/database.dump" ] || die "The database dump is empty."

if [ "$(env_get STORAGE_PROVIDER)" = "s3" ]; then
  warn "Documents are in an S3 store ($(env_get STORAGE_BUCKET)); back that bucket up with the store's own tools."
else
  say "Archiving the documents"
  docker run --rm -v kynexone_documents:/documents:ro postgres:16-alpine tar czf - -C /documents . > "$OUT/documents.tar.gz"
fi

cp .env "$OUT/env.backup"; chmod 600 "$OUT/env.backup"
{
  echo "created_utc=$STAMP"
  echo "kynexone_version=$(env_get KYNEXONE_VERSION)"
  echo "domain=$(env_get KYNEXONE_DOMAIN)"
  (cd "$OUT" && sha256sum database.dump documents.tar.gz 2>/dev/null || true)
} > "$OUT/manifest.txt"

say "Backup written to $OUT"
echo "$OUT" > "$DEST/LATEST"
