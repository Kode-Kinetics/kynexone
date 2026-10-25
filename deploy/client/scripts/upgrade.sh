#!/usr/bin/env bash
# Upgrade KynexOne to a new version.
#
#   scripts/upgrade.sh kynexone-1.2.0.tar.gz     an offline bundle (images and kit, verified by CHECKSUMS.sha256)
#   scripts/upgrade.sh 1.2.0                     pull that version's images from KYNEXONE_REGISTRY
#
#  1. verifies the bundle's checksums;
#  2. refuses when the licence's support period has ended (upgrades are part of support);
#  3. takes a backup (the rollback point) and stops if it fails;
#  4. switches maintenance mode on: people see a notice, not errors, and background jobs pause;
#  5. loads the new images (and the bundle's kit files: compose, Caddyfile, scripts; .env is kept);
#  6. applies the new version's migrations (`migrate` with the new image);
#  7. starts the new backend and web; 8. waits for /health/ready; 9. runs the smoke checks;
# 10. switches maintenance off.
# If a step after 5 fails, KynexOne stays in maintenance and the script prints how to roll back. Migrations are
# additive (no drop or rename in the release that stops using a column), so the previous images can usually run
# on the upgraded database; when they cannot, restore the backup. EF down-migrations are never used.
source "$(dirname "$0")/_common.sh"

TARGET="${1:-}"
[ -n "$TARGET" ] || die "Usage: scripts/upgrade.sh <bundle.tar.gz | MAJOR.MINOR.PATCH>"
require_docker; require_env
CURRENT="$(env_get KYNEXONE_VERSION)"

# ── 1. The new release ───────────────────────────────────────────────────────
BUNDLE=""
if [ -f "$TARGET" ]; then
  WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
  say "Unpacking $(basename "$TARGET")"
  tar xzf "$TARGET" -C "$WORK"
  BUNDLE="$(find "$WORK" -mindepth 1 -maxdepth 1 -type d | head -n1)"
  [ -f "$BUNDLE/CHECKSUMS.sha256" ] && [ -f "$BUNDLE/VERSION" ] || die "Not a KynexOne bundle (no CHECKSUMS.sha256 / VERSION)."
  (cd "$BUNDLE" && sha256sum -c --quiet CHECKSUMS.sha256) || die "The bundle's checksums do not match. Download it again."
  say "Bundle checksums verified"
  NEW="$(cat "$BUNDLE/VERSION")"
else
  NEW="$TARGET"
fi
echo "$NEW" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || die "'$NEW' is not a MAJOR.MINOR.PATCH version."
[ "$CURRENT" != "$NEW" ] || die "Already on $NEW."
if [ "$(printf '%s\n%s\n' "$CURRENT" "$NEW" | sort -V | head -n1)" = "$NEW" ]; then
  die "$NEW is older than $CURRENT. Going back is a restore (scripts/restore.sh <backup>), not an upgrade."
fi

# ── 2. Support period ────────────────────────────────────────────────────────
SUPPORT_UNTIL="$(api_command --licence-status 2>/dev/null | sed -n 's/^Support until *: *//p' | tr -d '\r')"
if echo "$SUPPORT_UNTIL" | grep -Eq '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' && [ "$SUPPORT_UNTIL" \< "$(date -u +%Y-%m-%d)" ]; then
  die "The licence's support period ended on $SUPPORT_UNTIL. Renew support with your KynexOne supplier to upgrade."
fi

say "Upgrading KynexOne $CURRENT -> $NEW"

# ── 3. Backup ────────────────────────────────────────────────────────────────
"$KIT_DIR/scripts/backup.sh" || die "The backup failed; nothing was changed."
BACKUP="$(cat "$KIT_DIR/backups/LATEST")"

rollback_help() {
  warn "The upgrade to $NEW did not finish. KynexOne is in maintenance mode on the upgraded database."
  warn "Roll back:"
  warn "  1. set KYNEXONE_VERSION=$CURRENT in .env and run: docker compose up -d backend web"
  warn "     then: docker compose run --rm --no-deps backend --maintenance off"
  warn "  2. if $CURRENT does not start on the upgraded database, restore the pre-upgrade backup:"
  warn "     docker compose stop backend web && scripts/restore.sh $BACKUP --yes"
}

# ── 4. Maintenance on ────────────────────────────────────────────────────────
say "Switching maintenance mode on"
api_command --maintenance on --message "KynexOne is being upgraded to $NEW. Please try again in a few minutes."
trap 'rollback_help' ERR

# ── 5. Images (and kit) ──────────────────────────────────────────────────────
if [ -n "$BUNDLE" ]; then
  say "Loading images"
  docker load -i "$BUNDLE/images/kynexone-images.tar"
  say "Updating the kit files (your .env, certs and backups are kept)"
  for f in docker-compose.yml Caddyfile .env.example README.md; do cp "$BUNDLE/deploy/client/$f" "$KIT_DIR/$f"; done
  cp "$BUNDLE"/deploy/client/scripts/* "$KIT_DIR/scripts/"
  chmod +x "$KIT_DIR"/scripts/*.sh
fi
env_set KYNEXONE_VERSION "$NEW"
if [ -z "$BUNDLE" ]; then
  if docker image inspect "$(env_get KYNEXONE_REGISTRY)/kynexone-api:$NEW" >/dev/null 2>&1      && docker image inspect "$(env_get KYNEXONE_REGISTRY)/kynexone-web:$NEW" >/dev/null 2>&1; then
    say "Using the $NEW images already on this server"
  else
    compose pull migrate backend web
  fi
fi

# ── 6. Migrations ────────────────────────────────────────────────────────────
say "Applying the $NEW database changes"
compose up migrate --exit-code-from migrate

# ── 7–9. Start, wait, check ──────────────────────────────────────────────────
say "Starting $NEW"
compose up -d
wait_ready 60 || { trap - ERR; rollback_help; exit 1; }
smoke_check || { trap - ERR; rollback_help; exit 1; }

# ── 10. Maintenance off ──────────────────────────────────────────────────────
api_command --maintenance off
trap - ERR
say "KynexOne is now on $NEW. Pre-upgrade backup: $BACKUP"
