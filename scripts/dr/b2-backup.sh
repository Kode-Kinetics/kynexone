#!/usr/bin/env bash
# KynexOne documents-bucket backup: copy the production documents bucket to a SECOND bucket,
# ideally at another provider/region, using any S3-compatible endpoint (Backblaze B2 S3 API, R2,
# AWS S3). See docs/runbooks/disaster-recovery.md (scenario: B2 loss) for object-lock guidance.
#
# Uses rclone with remotes defined purely from env vars (nothing on disk, nothing in the repo).
# It runs `rclone copy`, never `sync`: an object deleted or overwritten in the source is NOT removed
# from the backup, so accidental or malicious deletion cannot propagate. Retention comes from
# bucket versioning / object lock on the destination, not from this script.
#
# Required env (credentials supplied by the owner, as environment secrets):
#   SRC_ENDPOINT SRC_BUCKET SRC_ACCESS_KEY SRC_SECRET_KEY [SRC_REGION]
#   DST_ENDPOINT DST_BUCKET DST_ACCESS_KEY DST_SECRET_KEY [DST_REGION]
#   Use a READ-ONLY application key for the source and a WRITE-ONLY (no delete) key for the destination.
# Optional env: RCLONE (binary, default rclone)  DR_TRANSFERS (default 8)  DR_MAX_AGE (e.g. 3d for an
#   incremental window; empty = full compare every run)
# Flags: --dry-run  list what would be copied, change nothing
set -euo pipefail

DRY=0
for a in "$@"; do
  case "$a" in
    --dry-run) DRY=1 ;;
    -h|--help) sed -n 2,17p "$0"; exit 0 ;;
    *) echo "unknown argument: $a" >&2; exit 2 ;;
  esac
done

die() { echo "ERROR: $*" >&2; exit 1; }
for v in SRC_ENDPOINT SRC_BUCKET SRC_ACCESS_KEY SRC_SECRET_KEY DST_ENDPOINT DST_BUCKET DST_ACCESS_KEY DST_SECRET_KEY; do
  [ -n "${!v:-}" ] || die "$v is required"
done
[[ "$SRC_BUCKET" =~ ^[A-Za-z0-9._-]+$ && "$DST_BUCKET" =~ ^[A-Za-z0-9._-]+$ ]] || die "bucket names have unexpected characters"
[ "$SRC_ENDPOINT/$SRC_BUCKET" != "$DST_ENDPOINT/$DST_BUCKET" ] || die "source and destination are the same bucket"
[ "$SRC_ACCESS_KEY" != "$DST_ACCESS_KEY" ] || echo "WARNING: same access key for source and destination; use separate least-privilege keys." >&2

RCLONE="${RCLONE:-rclone}"
command -v "$RCLONE" >/dev/null || die "rclone is required (https://rclone.org/install/)"

export RCLONE_CONFIG_SRC_TYPE=s3 RCLONE_CONFIG_SRC_PROVIDER=Other RCLONE_CONFIG_SRC_ENDPOINT="$SRC_ENDPOINT"
export RCLONE_CONFIG_SRC_ACCESS_KEY_ID="$SRC_ACCESS_KEY" RCLONE_CONFIG_SRC_SECRET_ACCESS_KEY="$SRC_SECRET_KEY"
export RCLONE_CONFIG_SRC_REGION="${SRC_REGION:-}"
export RCLONE_CONFIG_DST_TYPE=s3 RCLONE_CONFIG_DST_PROVIDER=Other RCLONE_CONFIG_DST_ENDPOINT="$DST_ENDPOINT"
export RCLONE_CONFIG_DST_ACCESS_KEY_ID="$DST_ACCESS_KEY" RCLONE_CONFIG_DST_SECRET_ACCESS_KEY="$DST_SECRET_KEY"
export RCLONE_CONFIG_DST_REGION="${DST_REGION:-}"
# Object lock buckets reject the default no-checksum multipart behaviour on some providers.
export RCLONE_CONFIG_DST_NO_CHECK_BUCKET=true

ARGS=(--transfers "${DR_TRANSFERS:-8}" --checksum --stats-one-line --stats 30s)
[ -z "${DR_MAX_AGE:-}" ] || ARGS+=(--max-age "$DR_MAX_AGE")

echo "Backup $SRC_BUCKET -> $DST_BUCKET at $(date -u +%Y-%m-%dT%H:%M:%SZ) (mode: $([ "$DRY" = 1 ] && echo DRY-RUN || echo LIVE))"
if [ "$DRY" = 1 ]; then
  "$RCLONE" copy "SRC:$SRC_BUCKET" "DST:$DST_BUCKET" "${ARGS[@]}" --dry-run
  exit 0
fi

"$RCLONE" copy "SRC:$SRC_BUCKET" "DST:$DST_BUCKET" "${ARGS[@]}"
# Every source object must exist in the destination with the same size (one-way check).
"$RCLONE" check "SRC:$SRC_BUCKET" "DST:$DST_BUCKET" --one-way --size-only
echo "Backup verified (every source object present in destination)."
