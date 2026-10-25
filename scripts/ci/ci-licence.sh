#!/usr/bin/env bash
# CI only: a licence for the browser suites and the client-install job.
#
#   scripts/ci/ci-licence.sh [out-dir]          (default .ci-licence/)
#
# 1. generates a key pair for this run (the private half stays in <out-dir>, never in the repository);
# 2. puts the public half in backend-dotnet/Zayra.Api/Infrastructure/Licensing/Keys/ci/, which a build embeds only
#    with -p:KynexOneCiLicenceKey=true (Docker: --build-arg CI_LICENCE_KEY=true). Release builds never set it, so a
#    licence signed with a CI key is refused by every released image;
# 3. issues <out-dir>/ci.licence: unbound to any installation, every module and opt-in feature, limits generous
#    enough for the fixture world, valid for two days.
# Both the key folder and <out-dir> are git-ignored.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="${1:-$ROOT/.ci-licence}"
KEYS="$ROOT/backend-dotnet/Zayra.Api/Infrastructure/Licensing/Keys/ci"
KID="ci-$(date -u +%Y%m%d%H%M%S)"

mkdir -p "$OUT" "$KEYS"
rm -f "$KEYS"/*.pub.pem
openssl ecparam -name prime256v1 -genkey -noout -out "$OUT/ci.key" 2>/dev/null
chmod 600 "$OUT/ci.key"
openssl ec -in "$OUT/ci.key" -pubout -out "$KEYS/$KID.pub.pem" 2>/dev/null

EXPIRES="$(date -u -d '+2 days' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -v+2d +%Y-%m-%dT%H:%M:%SZ)"
cat > "$OUT/request.json" <<JSON
{
  "licenceId": "CI-$KID",
  "licensee": "KynexOne CI",
  "installationId": null,
  "edition": "CI",
  "expiresAtUtc": "$EXPIRES",
  "graceDays": 0,
  "supportExpiresAtUtc": "$EXPIRES",
  "accountType": "Group",
  "maxEmployees": 0,
  "maxUsers": 0,
  "maxCompanies": 0,
  "maxAdminUsers": 0,
  "aiMonthlyTokenLimit": 10000000,
  "modules": [],
  "optInFeatures": [],
  "countries": null
}
JSON

dotnet run --project "$ROOT/tools/LicenceIssuer" -c Release -- issue \
  --key "$OUT/ci.key" --kid "$KID" --in "$OUT/request.json" --out "$OUT/ci.licence" --all-modules yes

echo "CI licence: $OUT/ci.licence (kid $KID). Build the API with CI_LICENCE_KEY=true to trust it."
