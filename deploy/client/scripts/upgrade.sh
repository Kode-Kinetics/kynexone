#!/usr/bin/env bash
# Upgrade KynexOne to a new version.
#
#   scripts/upgrade.sh 1.2.0 [--images kynexone-1.2.0.tar]
#
# 1. takes a backup (the rollback point); 2. switches maintenance mode on, so people see a notice instead of
# errors; 3. loads the new images; 4. applies the new version's migrations; 5. starts it and waits until it is
# ready; 6. switches maintenance off. If a step fails, the installation stays in maintenance and the script
# prints the one command that rolls back.
source "$(dirname "$0")/_common.sh"

TARGET="${1:-}"; [ $# -gt 0 ] && shift
IMAGES=""
while [ $# -gt 0 ]; do
  case "$1" in
    --images) IMAGES="${2:?--images needs a file}"; shift 2 ;;
    *) die "Unknown option: $1" ;;
  esac
done
echo "$TARGET" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || die "Usage: scripts/upgrade.sh <MAJOR.MINOR.PATCH> [--images file.tar]"
require_docker; require_env

CURRENT="$(env_get KYNEXONE_VERSION)"
[ "$CURRENT" != "$TARGET" ] || die "Already on $TARGET."
if [ "$(printf '%s\n%s\n' "$CURRENT" "$TARGET" | sort -V | head -n1)" = "$TARGET" ]; then
  die "$TARGET is older than $CURRENT. Going back is a restore (scripts/restore.sh <backup>), not an upgrade."
fi

say "Upgrading KynexOne $CURRENT -> $TARGET"
"$KIT_DIR/scripts/backup.sh"
BACKUP="$(cat "$KIT_DIR/backups/LATEST")"

rollback_hint() {
  warn "The upgrade did not finish. KynexOne is still in maintenance mode."
  warn "To return to $CURRENT with the data as it was before the upgrade:"
  warn "  scripts/restore.sh $BACKUP --yes"
}
trap rollback_hint ERR

say "Switching maintenance mode on"
api_command --maintenance on --message "KynexOne is being upgraded to $TARGET. Please try again in a few minutes."

[ -z "$IMAGES" ] || docker load -i "$IMAGES"
env_set KYNEXONE_VERSION "$TARGET"
[ -n "$IMAGES" ] || compose pull api web

say "Applying the $TARGET database changes"
api_command --migrate

say "Starting $TARGET"
compose up -d
wait_ready 60 || { trap - ERR; rollback_hint; exit 1; }

api_command --maintenance off
trap - ERR
say "KynexOne is now on $TARGET. Rollback point: $BACKUP"
