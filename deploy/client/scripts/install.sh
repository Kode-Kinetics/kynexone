#!/usr/bin/env bash
# First installation of KynexOne on this server.
#
#   scripts/install.sh [--domain hr.example.local] [--licence organisation.licence] [--force-reconfigure]
#
# Run from an extracted offline bundle (kynexone-<version>/deploy/client), it loads the bundle's images after
# verifying CHECKSUMS.sha256; otherwise it pulls them from KYNEXONE_REGISTRY.
#
#  1. checks Docker and that ports 80/443 are free;
#  2. writes .env (mode 600) with generated secrets; asks for the address and the TLS choice;
#  3. loads or pulls the images;
#  4. starts postgres and redis and applies the migrations;
#  5. creates the organisation and its first administrator (asks for the name, country, admin email, password);
#  6. imports the licence when --licence is given, otherwise prints the installation id the vendor needs;
#  7. starts everything, waits for /health/ready and prints the address.
#
# Unattended (CI): set KYNEXONE_DOMAIN and the OrganisationSetup__* variables (OrganisationName, HomeCountry,
# AdminEmail, AdminFullName, AdminPassword) in the environment and nothing is asked. KYNEXONE_VERSION and
# KYNEXONE_REGISTRY in the environment are written to .env; images already on this server are used as they are.
source "$(dirname "$0")/_common.sh"

DOMAIN="${KYNEXONE_DOMAIN:-}"; LICENCE=""; FORCE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --domain)  DOMAIN="${2:?--domain needs a value}"; shift 2 ;;
    --licence) LICENCE="${2:?--licence needs a file}"; shift 2 ;;
    --force-reconfigure) FORCE=1; shift ;;
    -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
done
INTERACTIVE=""; [ -t 0 ] && INTERACTIVE=1
ask() { # $1 prompt, $2 default → the answer (the default when unattended)
  local answer=""
  if [ -n "$INTERACTIVE" ]; then read -r -p "$1${2:+ [$2]}: " answer; fi
  echo "${answer:-$2}"
}

# ── 1. Prerequisites ─────────────────────────────────────────────────────────
require_docker
if [ -f .env ] && [ -z "$FORCE" ]; then
  die "KynexOne is already configured here (.env exists). Use scripts/upgrade.sh to upgrade, or --force-reconfigure to change the address and TLS settings (the secrets are kept)."
fi
if ! compose ps -q proxy 2>/dev/null | grep -q .; then
  for port in 80 443; do
    if (command -v ss >/dev/null && ss -ltn "sport = :$port" | grep -q LISTEN); then
      die "Port $port is already in use on this server. Free it, or set HTTP_PORT/HTTPS_PORT in .env."
    fi
  done
fi

# ── 2. Settings ──────────────────────────────────────────────────────────────
if [ ! -f .env ]; then
  cp .env.example .env
  chmod 600 .env
  say "Generating secrets"
  env_set POSTGRES_PASSWORD "$(random_secret 40)"
  env_set JWT_AUDIENCE "kynexone-$(random_secret 12 | tr 'A-Z' 'a-z')"
  env_set JWT_SIGNING_KEY "$(random_secret 96)"
  env_set PROXY_CLIENT_IP_SECRET "$(random_secret 48)"
fi
for key in KYNEXONE_VERSION KYNEXONE_REGISTRY CADDY_TLS; do
  value="$(printenv "$key" || true)"
  [ -z "$value" ] || env_set "$key" "$value"
done
[ -n "$DOMAIN" ] || DOMAIN="$(ask "Address people will use to reach KynexOne (e.g. hr.example.local)" "$(env_get KYNEXONE_DOMAIN)")"
[ -n "$DOMAIN" ] && [ "$DOMAIN" != "hr.example.local" ] || die "Give the real address with --domain."
env_set KYNEXONE_DOMAIN "$DOMAIN"
if [ -n "$INTERACTIVE" ]; then
  echo "Certificates: 'internal' (Caddy's own CA, for a LAN), an email address (Let's Encrypt, server reachable"
  echo "from the internet), or '/certs/cert.pem /certs/key.pem' (your own certificate, files placed in ./certs)."
  env_set CADDY_TLS "$(ask "TLS" "$(env_get CADDY_TLS)")"
fi
for key in POSTGRES_PASSWORD JWT_AUDIENCE JWT_SIGNING_KEY PROXY_CLIENT_IP_SECRET; do
  [ "$(env_get "$key")" != "GENERATED" ] || die "$key in .env is still GENERATED. Delete .env and run install.sh again."
done

# ── 3. Images ────────────────────────────────────────────────────────────────
BUNDLE_DIR="$(cd "$KIT_DIR/../.." && pwd)"
if [ -f "$BUNDLE_DIR/images/kynexone-images.tar" ]; then
  say "Verifying the offline bundle"
  (cd "$BUNDLE_DIR" && sha256sum -c --quiet CHECKSUMS.sha256) || die "The bundle's checksums do not match. Download it again."
  env_set KYNEXONE_VERSION "$(cat "$BUNDLE_DIR/VERSION")"
  say "Loading images"
  docker load -i "$BUNDLE_DIR/images/kynexone-images.tar"
else
  API_IMAGE="$(env_get KYNEXONE_REGISTRY)/kynexone-api:$(env_get KYNEXONE_VERSION)"
  WEB_IMAGE="$(env_get KYNEXONE_REGISTRY)/kynexone-web:$(env_get KYNEXONE_VERSION)"
  if docker image inspect "$API_IMAGE" >/dev/null 2>&1 && docker image inspect "$WEB_IMAGE" >/dev/null 2>&1; then
    say "Using the KynexOne images already on this server ($(env_get KYNEXONE_VERSION))"
    compose pull postgres redis proxy
  else
    say "Pulling images for version $(env_get KYNEXONE_VERSION)"
    compose pull postgres redis migrate backend web proxy
  fi
fi

# ── 4. Database and storage ──────────────────────────────────────────────────
say "Starting the database and cache"
compose up -d --wait postgres redis
say "Applying the database migrations"
compose up migrate --exit-code-from migrate

# ── 5. Organisation ──────────────────────────────────────────────────────────
ORGANISATIONS="$(compose exec -T postgres psql -U kynexone -d kynexone -tAc 'SELECT count(*) FROM tenants')"
SETUP_ARGS=()
for var in OrganisationName LegalName HomeCountry AdminEmail AdminFullName AdminPassword; do
  value="$(printenv "OrganisationSetup__$var" || true)"
  [ -z "$value" ] || SETUP_ARGS+=(-e "OrganisationSetup__$var=$value")
done
if [ "${ORGANISATIONS:-0}" != "0" ]; then
  say "The organisation is already set up; keeping it"
elif [ -n "$INTERACTIVE" ]; then
  say "Creating the organisation and its first administrator"
  compose run --rm --no-deps "${SETUP_ARGS[@]}" backend --setup-organisation
else
  say "Creating the organisation and its first administrator"
  compose run --rm --no-deps -T "${SETUP_ARGS[@]}" backend --setup-organisation
fi

# ── 6. Licence ───────────────────────────────────────────────────────────────
if [ -n "$LICENCE" ]; then
  [ -f "$LICENCE" ] || die "Licence file not found: $LICENCE"
  say "Importing the licence"
  LICENCE_ABS="$(cd "$(dirname "$LICENCE")" && pwd)/$(basename "$LICENCE")"
  compose run --rm --no-deps -T -v "$LICENCE_ABS:/tmp/kynexone.licence:ro" backend --import-licence /tmp/kynexone.licence
else
  INSTALLATION_ID="$(compose run --rm --no-deps -T backend --licence-status | sed -n 's/^Installation id *: *//p')"
  warn "No licence imported. Send this installation id to your KynexOne supplier: $INSTALLATION_ID"
  warn "Then upload the licence in Organisation Admin -> Licence. Until then the API answers 402 licence_missing."
fi

# ── 7. Start ─────────────────────────────────────────────────────────────────
say "Starting KynexOne"
compose up -d
wait_ready 60 || die "KynexOne did not become ready. See: docker compose logs backend"
smoke_check || warn "Smoke checks reported a problem (above)."

echo
echo "KynexOne is installed: https://$(env_get KYNEXONE_DOMAIN)"
echo "  - Sign in as the administrator you just created."
echo "  - Schedule scripts/backup.sh (cron) and copy the backups off this server."
echo "  - Keep .env safe: it holds the database password and the token signing key."
