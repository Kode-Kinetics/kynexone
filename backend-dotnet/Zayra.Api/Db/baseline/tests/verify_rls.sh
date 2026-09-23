#!/usr/bin/env bash
# Verifies the KynexOne row-level-security and partitioning baseline (002, 030,
# 060) against a throwaway PostgreSQL 16 container. Nothing here touches a real
# database: the container is created and destroyed by this script.
#
#   ./backend-dotnet/Zayra.Api/Db/baseline/tests/verify_rls.sh
#
# postgres:16 and not 17, because production is Neon PG 17.11 and the baseline
# must not depend on anything newer than 16.
#
# What it proves (TARGET_SCHEMA.md §19.2, §19.3):
#   1  fail-closed: kynex_app with app.tenant_id unset sees zero rows everywhere
#   2  tenant A cannot read/update/delete tenant B — through a table, both views,
#      a partition parent, a child named directly, and a SECURITY DEFINER function
#   3  kynex_app cannot set itself platform
#   4  kynex_ro reaches no credential, by column or by function
#   5  no LOGIN role reaches a BYPASSRLS role except kynex_migrator (transitive)
#   6  RLS forced everywhere; children forced and ungranted; views security_invoker
#
# Most assertions live in 070_rls_proof.sql and use SET ROLE. Section 7 below
# re-runs the sharpest ones over GENUINE per-role connections, so the fidelity of
# SET ROLE is demonstrated rather than assumed.
set -euo pipefail

CONTAINER=${CONTAINER:-kynex-rls-verify}
IMAGE=${IMAGE:-postgres:16}
DB=${DB:-kynex_rls}
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE="$(dirname "$HERE")"

# 002 is the bootstrap file: it creates the roles, so it is applied by the
# superuser. Everything after it is applied the way the deploy applies it —
# as kynex_migrator with SET ROLE kynex_owner — which is what makes every object
# owned by kynex_owner and the grants in 060 mean what they say.
BOOTSTRAP=(
  "$BASE/001_extensions.sql"
  "$BASE/002_roles.sql"
)
MIGRATED=(
  "$BASE/010_platform.sql"
  "$BASE/011_identity.sql"
  "$BASE/012_org.sql"
  "$BASE/013_employees.sql"
  "$BASE/014_statutory.sql"
  "$BASE/015_payroll.sql"
  "$BASE/016_wps_gl.sql"
  "$BASE/017_leave_attendance.sql"
  "$BASE/018_workflow_audit.sql"
  "$BASE/020_constraints_a_f.sql"
  "$BASE/021_constraints_g_r.sql"
  "$BASE/030_partitions.sql"
  "$BASE/060_policies.sql"
)

cleanup() { docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT
cleanup

docker run -d --name "$CONTAINER" -e POSTGRES_PASSWORD=verify -e POSTGRES_DB="$DB" "$IMAGE" >/dev/null
for _ in $(seq 1 60); do
  docker exec "$CONTAINER" pg_isready -U postgres -d "$DB" >/dev/null 2>&1 && break
  sleep 1
done

su_psql()  { docker exec -i "$CONTAINER" psql -U postgres -d "$DB" "$@"; }
as_role()  { local r="$1"; shift; docker exec -i "$CONTAINER" psql -U "$r" -d "$DB" "$@"; }

echo "== applying =="
for f in "${BOOTSTRAP[@]}"; do
  printf '   %-28s' "$(basename "$f")"
  su_psql -v ON_ERROR_STOP=1 -q < "$f"
  echo "ok  (superuser: bootstrap)"
done

# every login role gets a password so the real-connection checks below can use
# one; the container's pg_hba trusts local connections anyway.
su_psql -v ON_ERROR_STOP=1 -q <<'SQL'
ALTER ROLE kynex_migrator PASSWORD 'verify';
ALTER ROLE kynex_app      PASSWORD 'verify';
ALTER ROLE kynex_job      PASSWORD 'verify';
ALTER ROLE kynex_platform PASSWORD 'verify';
ALTER ROLE kynex_ro       PASSWORD 'verify';
SQL

for f in "${MIGRATED[@]}"; do
  printf '   %-28s' "$(basename "$f")"
  { echo "SET ROLE kynex_owner;"; cat "$f"; } | as_role kynex_migrator -v ON_ERROR_STOP=1 -q
  echo "ok  (kynex_migrator → kynex_owner)"
done

echo "== proofs =="
su_psql -v ON_ERROR_STOP=1 -q < "$HERE/070_rls_proof.sql"

# ---------------------------------------------------------------------------
# 7. The same properties over genuine connections, not SET ROLE.
#
# SET ROLE is faithful for RLS — the policy is decided on the current user — but
# "faithful" is a claim, and this is the baseline's only security gate. Four
# assertions are therefore re-run on real kynex_app / kynex_ro connections.
# ---------------------------------------------------------------------------
echo "== 7. re-proved over genuine per-role connections =="

fail() { echo "   FAILED: $*"; exit 1; }

n=$(as_role kynex_app -tA -c "SELECT count(*) FROM employees;")
[ "$n" = "0" ] || fail "kynex_app connection with no tenant GUC saw $n employees"
echo "   fail-closed on a real kynex_app connection                 ok"

n=$(as_role kynex_app -tA \
      -c "SET app.tenant_id='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';" \
      -c "SELECT count(*) FROM employees;" | tail -1)
[ "$n" = "1" ] || fail "kynex_app as tenant A saw $n employees, expected 1"
echo "   tenant A sees exactly its own row                          ok"

n=$(as_role kynex_app -tA \
      -c "SET app.platform='on';" \
      -c "SELECT app.is_platform();" | tail -1)
[ "$n" = "f" ] || fail "kynex_app promoted itself to platform on a real connection"
echo "   kynex_app cannot self-promote on a real connection         ok"

n=$(as_role kynex_app -tA \
      -c "SET app.tenant_id='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';" \
      -c "SELECT balance_days FROM v_leave_balances;" | tail -1 | tr -d ' ')
[ "$n" = "21.00" ] || fail "v_leave_balances returned '$n' for tenant A, expected 21.00"
echo "   v_leave_balances sums one tenant on a real connection      ok"

if as_role kynex_ro -tA -c "SELECT password_hash FROM users;" >/dev/null 2>&1; then
  fail "kynex_ro read users.password_hash on a real connection"
fi
echo "   kynex_ro cannot read password_hash on a real connection    ok"

if as_role kynex_ro -tA -c "SELECT * FROM app.resolve_login('tenant-a','user@example.test');" >/dev/null 2>&1; then
  fail "kynex_ro executed app.resolve_login on a real connection"
fi
echo "   kynex_ro cannot execute app.resolve_login                  ok"

echo ""
echo "RLS BASELINE VERIFIED — 002_roles.sql, 030_partitions.sql, 060_policies.sql"
