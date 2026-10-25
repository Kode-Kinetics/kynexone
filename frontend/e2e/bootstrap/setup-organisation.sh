#!/usr/bin/env bash
# Creates the e2e fixture organisation in the docker-compose stack and imports a licence — the step before
# the bootstrap (e2e/bootstrap/provision.ts) provisions the rest of the world through the organisation's API.
#
#   frontend/e2e/bootstrap/setup-organisation.sh [licence-file]      (default .ci-licence/ci.licence)
#
# The organisation is created by `--setup-organisation`, the only door (docs/DATA_ENTRY_PATHS.md), with the
# values e2e/world.ts declares: ORGANISATION and ORGANISATION_ADMIN (= ALMARAI_OWNER, password
# E2E_GROUP_PASSWORD). e2e/identity/identity-contract.spec.ts fails when the two drift apart.
#
# The licence must be signed with a key the API trusts. In CI and locally:
#   scripts/ci/ci-licence.sh                         # per-run key pair + .ci-licence/ci.licence
#   CI_LICENCE_KEY=true docker compose up -d --build  # an API image that trusts that key
#   frontend/e2e/bootstrap/setup-organisation.sh
# Re-running is safe: an organisation that exists is kept, and an already-active licence is a no-op.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
cd "$ROOT"
LICENCE="${1:-$ROOT/.ci-licence/ci.licence}"
API="${E2E_API_BASE_URL:-http://localhost:5117}"
API="${API%/}"

# ── e2e/world.ts: ORGANISATION and ORGANISATION_ADMIN ─────────────────────────────────────────
ORGANISATION_NAME='KynexOne E2E Organisation'
FIRST_COMPANY_CODE='ALM-DAIRY-KSA'
HOME_COUNTRY='SA'
ADMIN_EMAIL='owner@almarai-test.local'
ADMIN_FULL_NAME='Almarai Group Owner'
if [ -n "${CI:-}" ] && [ -z "${E2E_GROUP_PASSWORD:-}" ]; then
  echo "E2E_GROUP_PASSWORD must be set in CI (generated per run)." >&2
  exit 1
fi
ADMIN_PASSWORD="${E2E_GROUP_PASSWORD:-GroupDemo123!x}"

installation() { curl -fsS "$API/api/public/installation"; }

if installation | grep -q '"setupComplete":true'; then
  echo "[setup-organisation] The organisation already exists; keeping it."
else
  echo "[setup-organisation] Creating '$ORGANISATION_NAME' with administrator $ADMIN_EMAIL"
  docker compose exec -T \
    -e "OrganisationSetup__OrganisationName=$ORGANISATION_NAME" \
    -e "OrganisationSetup__LegalName=$FIRST_COMPANY_CODE" \
    -e "OrganisationSetup__HomeCountry=$HOME_COUNTRY" \
    -e "OrganisationSetup__AdminEmail=$ADMIN_EMAIL" \
    -e "OrganisationSetup__AdminFullName=$ADMIN_FULL_NAME" \
    -e "OrganisationSetup__AdminPassword=$ADMIN_PASSWORD" \
    backend dotnet Zayra.Api.dll --setup-organisation
fi

[ -f "$LICENCE" ] || { echo "Licence not found: $LICENCE (run scripts/ci/ci-licence.sh first)." >&2; exit 1; }
echo "[setup-organisation] Importing $LICENCE"
docker compose cp "$LICENCE" backend:/tmp/e2e.licence
docker compose exec -T backend dotnet Zayra.Api.dll --import-licence /tmp/e2e.licence

installation | grep -q '"state":"Valid"' || { echo "The licence did not become valid: $(installation)" >&2; exit 1; }
echo "[setup-organisation] Ready: $(installation)"
