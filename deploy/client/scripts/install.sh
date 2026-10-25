#!/usr/bin/env bash
# First installation of KynexOne on this server.
#
#   scripts/install.sh --domain hr.example.local [--images kynexone-1.0.0.tar] [--licence kynexone.licence]
#
# 1. writes .env with fresh secrets (an existing .env is kept: re-running install.sh is safe);
# 2. loads the images (from --images for an offline server, otherwise from the registry);
# 3. creates the database schema (--migrate);
# 4. creates the organisation and its first administrator (--setup-organisation, asks for the details);
# 5. imports the licence when --licence is given (it can also be uploaded later in Tenant Admin -> Licence);
# 6. starts everything and waits until it reports ready.
source "$(dirname "$0")/_common.sh"

DOMAIN=""; IMAGES=""; LICENCE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --domain)  DOMAIN="${2:?--domain needs a value}"; shift 2 ;;
    --images)  IMAGES="${2:?--images needs a file}"; shift 2 ;;
    --licence) LICENCE="${2:?--licence needs a file}"; shift 2 ;;
    -h|--help) sed -n '2,11p' "$0"; exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
done

require_docker

if [ -f .env ]; then
  say "Keeping the existing .env"
else
  [ -n "$DOMAIN" ] || read -r -p "Address people will use to reach KynexOne (e.g. hr.example.local): " DOMAIN
  [ -n "$DOMAIN" ] || die "An address is required."
  say "Writing .env with new secrets"
  cp .env.example .env
  chmod 600 .env
  env_set KYNEXONE_DOMAIN "$DOMAIN"
  env_set POSTGRES_PASSWORD "$(random_secret 40)"
  env_set JWT_AUDIENCE "kynexone-$(random_secret 12 | tr 'A-Z' 'a-z')"
  env_set JWT_SIGNING_KEY "$(random_secret 96)"
fi

for key in POSTGRES_PASSWORD JWT_AUDIENCE JWT_SIGNING_KEY; do
  [ "$(env_get "$key")" != "GENERATED" ] || die "$key in .env is still GENERATED. Delete .env and run install.sh again."
done

if [ -n "$IMAGES" ]; then
  say "Loading images from $IMAGES"
  docker load -i "$IMAGES"
else
  say "Pulling images for version $(env_get KYNEXONE_VERSION)"
  compose pull
fi

say "Starting the database"
compose up -d --wait postgres

say "Creating the database schema"
api_command --migrate

say "Creating the organisation and its first administrator"
api_command --setup-organisation

if [ -n "$LICENCE" ]; then
  [ -f "$LICENCE" ] || die "Licence file not found: $LICENCE"
  say "Importing the licence"
  LICENCE_ABS="$(cd "$(dirname "$LICENCE")" && pwd)/$(basename "$LICENCE")"
  compose run --rm --no-deps -v "$LICENCE_ABS:/tmp/kynexone.licence:ro" api --import-licence /tmp/kynexone.licence
fi

say "Starting KynexOne"
compose up -d
wait_ready 60 || die "KynexOne did not become ready. See: docker compose logs api"

echo
echo "KynexOne is installed: https://$(env_get KYNEXONE_DOMAIN)"
echo
echo "Next:"
echo "  - Sign in as the administrator you just created."
echo "  - No licence yet? Upload it in Tenant Admin -> Licence. Until then the API answers 402 licence_missing."
echo "  - Schedule scripts/backup.sh (cron) and copy the backups off this server."
echo "  - Keep .env safe: it holds the database password and the token signing key."
