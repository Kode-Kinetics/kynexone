#!/usr/bin/env bash
# schema-gate-selftest.sh — break the schema on purpose, and require each gate to notice.
#
#   ./scripts/schema-gate-selftest.sh
#
# A gate you have not seen fail is not a gate. This repository has already shipped a lint that
# passed vacuously and a health check that always returned zero, and `deploy-preflight` in
# ci.yml exists for the same reason: to prove the env-var gate still CATCHES the input that
# once got through.
#
# So every gate added for §19.1 gets the same treatment. Each case below damages a freshly
# built throwaway database — or a baseline file, restored afterwards — in one specific way,
# runs the gate that is supposed to catch it, and FAILS THIS SCRIPT IF THE GATE PASSES.
#
# The seven cases, and why each one:
#
#   1  a COMMENT ON TABLE is dropped        R3a — the table stops naming a capability, and
#                                           DATA_DICTIONARY.md is generated from that comment
#   2  a policy is dropped                  R1 — the table looks EMPTY rather than WRONG,
#                                           which is how this defect shipped twice
#   3  a view loses security_invoker        070 proof 6d — the mandated read path starts
#                                           returning every tenant's rows
#   4  a partition child is granted          070 proof 6b — the revision-5 hole: millions of
#                                           unfiltered rows readable by naming the child
#   5  an unjustified index is added        R4 — write amplification nobody can ever retire
#   6  a baseline file is edited            the drift gate — the change reached the database
#      without regenerating schema.sql      and not the committed schema
#   7  files applied out of order           the apply-order gate (its own step 2)
#
# Cases 1, 2, 3 and 5 are ALSO byte-visible to the drift gate, and the script proves that
# second catch for case 5 — two independent gates on one defect is the design, not redundancy.
set -euo pipefail
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/schema-gate-common.sh"

DB="${SCHEMA_GATE_DB:-kynex_selftest}"
TESTS="$BASELINE_DIR/tests"
BUILD="$REPO_ROOT/scripts/schema-build.sh"

if [[ "${SCHEMA_GATE_REUSE:-0}" != "1" ]]; then
  schema_gate_up
  trap 'schema_gate_down' EXIT
fi

PASS=0
FAILED=0

# run_expect_fail <label> <gate description> <command…>
# The whole point: a ZERO exit from the gate is a FAILURE of this script.
run_expect_fail() {
  local label="$1" gate="$2"; shift 2
  local out rc
  set +e
  out="$("$@" 2>&1)"
  rc=$?
  set -e
  if [[ $rc -eq 0 ]]; then
    echo "   MISSED   $label"
    echo "            $gate exited 0 on a database that is broken. The gate is not a gate."
    FAILED=$((FAILED + 1))
    return
  fi
  echo "   caught   $label"
  echo "            by $gate: $(printf '%s' "$out" | grep -m1 -E 'FAILED|ERROR|GATE' | sed 's/^[[:space:]]*//' | cut -c1-110)"
  PASS=$((PASS + 1))
}

rebuild() { "$BUILD" --db "$DB" --quiet >/dev/null 2>&1; }
tamper()  { pg_psql kynex_migrator "$DB" -v ON_ERROR_STOP=1 -q -c "SET ROLE kynex_owner; $1"; }
# stdin, never -f: in docker mode psql runs INSIDE the container, where the host's path to
# the proof file does not exist. An -f here failed as "No such file or directory" — which,
# being a non-zero exit, would have scored as the gate CATCHING every tamper.
run_070() { pg_psql postgres "$DB" -v ON_ERROR_STOP=1 -q < "$TESTS/070_rls_proof.sql"; }
run_080() { pg_psql postgres "$DB" -v ON_ERROR_STOP=1 -q < "$TESTS/080_schema_ratchets.sql"; }

echo "=============================================================="
echo " schema gate self-test — each gate must FAIL on a broken tree"
echo "=============================================================="

# ── 0. the control. On a clean build every gate passes, so a later FAIL means the tamper
#       and not the harness. Without this, a script in which every gate always failed would
#       score a perfect 7/7.
echo ""
echo "-- 0. control: the clean tree ------------------------------------------------"
rebuild
if run_080 >/dev/null 2>&1 && run_070 >/dev/null 2>&1; then
  echo "   ok       both suites pass on an untampered build"
else
  echo "   ABORT    the suites do not pass on a CLEAN build; fix that before reading anything below" >&2
  exit 1
fi

# ── 1. delete a COMMENT ──────────────────────────────────────────────────────────────────
echo ""
echo "-- 1. a table loses its COMMENT ----------------------------------------------"
rebuild
tamper "COMMENT ON TABLE employees IS NULL;" >/dev/null
run_expect_fail "COMMENT ON TABLE employees removed" "ratchet R3a" run_080

# ── 2. drop a policy ─────────────────────────────────────────────────────────────────────
# The table keeps RLS enabled and forced, so it still looks perfectly locked down to any
# check that asks "is RLS on?". With no policy and FORCE set it is simply invisible — which
# is why the two real occurrences of this defect were mistaken for an empty table.
echo ""
echo "-- 2. a policy is dropped (RLS stays on, so it looks fine) --------------------"
rebuild
tamper "DROP POLICY p_tenant ON employees;" >/dev/null
run_expect_fail "policy p_tenant dropped from employees" "ratchet R1 (declared shape vs live policy)" run_080

# and the subtler half: the policy is still THERE, but it is the wrong shape.
echo ""
echo "-- 2b. a policy is REPLACED with the wrong shape ------------------------------"
rebuild
tamper "DROP POLICY p_tenant ON employees;
        CREATE POLICY p_tenant ON employees USING (tenant_id = app.current_tenant() OR app.is_platform());" >/dev/null
run_expect_fail "employees' policy widened with OR app.is_platform()" "ratchet R1" run_080

# ── 3. a view loses security_invoker ─────────────────────────────────────────────────────
echo ""
echo "-- 3. a view loses security_invoker -------------------------------------------"
rebuild
tamper "ALTER VIEW v_employee_current RESET (security_invoker);" >/dev/null
run_expect_fail "security_invoker removed from v_employee_current" "070 (reused) — proof 2f caught the LEAK before 6d even checked the flag" run_070

# ── 4. a partition child is granted ──────────────────────────────────────────────────────
# Exactly what PartitionMaintenance did in revision 5. The parent's policy governs access
# THROUGH the parent, so a granted child is an unfiltered copy that any session can read by
# naming it. A child that 070 does not itself touch, so the catch is this tamper's.
echo ""
echo "-- 4. a partition child is granted to kynex_app -------------------------------"
rebuild
CHILD="$(pg_psql postgres "$DB" -tA -c "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relispartition AND c.relname LIKE 'audit_logs_%' ORDER BY 1 LIMIT 1;" | tr -d '[:space:]')"
[[ -n "$CHILD" ]] || { echo "   ABORT    no partition child found to grant" >&2; exit 1; }
tamper "GRANT SELECT ON $CHILD TO kynex_app;" >/dev/null
run_expect_fail "GRANT SELECT ON $CHILD TO kynex_app" "070 proof 6b (reused)" run_070

# ── 5. an unjustified index ──────────────────────────────────────────────────────────────
echo ""
echo "-- 5. an index is added with no COMMENT ON INDEX ------------------------------"
rebuild
tamper "CREATE INDEX ix_employees__selftest_unjustified ON employees (created_at);" >/dev/null
run_expect_fail "index ix_employees__selftest_unjustified added, no comment" "ratchet R4" run_080

# the same defect, caught a second time by a completely different mechanism
echo ""
echo "-- 5b. …and the same index is visible to the byte-diff gate -------------------"
TMP="$(mktemp -d)"
pg_dump_schema "$DB" | "$REPO_ROOT/scripts/schema-normalise.sh" > "$TMP/tampered.sql"
if cmp -s "$TMP/tampered.sql" "$CANONICAL_SCHEMA"; then
  echo "   MISSED   an extra index left schema.sql byte-identical. The drift gate is blind to indexes."
  FAILED=$((FAILED + 1))
else
  echo "   caught   by the drift gate: $(diff "$CANONICAL_SCHEMA" "$TMP/tampered.sql" | grep -m1 -E '^> .+' | cut -c1-100)"
  PASS=$((PASS + 1))
fi
rm -rf "$TMP"

# ── 6. a baseline file edited without regenerating schema.sql ────────────────────────────
# The everyday failure this gate exists for: the DDL changed, the database changed, and the
# committed canonical schema did not. The file is restored whatever happens.
echo ""
echo "-- 6. a baseline file is edited without regenerating schema.sql ---------------"
VICTIM="$BASELINE_DIR/010_platform.sql"
BACKUP="$(mktemp)"
cp "$VICTIM" "$BACKUP"
restore_victim() { cp "$BACKUP" "$VICTIM"; rm -f "$BACKUP"; }
trap 'restore_victim; [[ "${SCHEMA_GATE_REUSE:-0}" == "1" ]] || schema_gate_down' EXIT
printf '\nCOMMENT ON COLUMN tenants.id IS %s;\n' "'self-test: an edit that never reached schema.sql'" >> "$VICTIM"
run_expect_fail "010_platform.sql edited, schema.sql not regenerated" "the drift gate" \
  env SCHEMA_GATE_REUSE=1 SCHEMA_GATE_DB=kynex_selftest_drift "$REPO_ROOT/scripts/schema-drift-gate.sh"
restore_victim
trap '[[ "${SCHEMA_GATE_REUSE:-0}" == "1" ]] || schema_gate_down' EXIT

# ── 7. out of order ──────────────────────────────────────────────────────────────────────
# The apply-order gate's step 2 IS this case, so it is not re-staged here; it is asserted
# directly, so a regression that made an out-of-order apply succeed shows up in both places.
echo ""
echo "-- 7. the baseline applied out of order ---------------------------------------"
run_expect_fail "020_constraints_a_f.sql applied before 010_platform.sql" "schema-build.sh (and the apply-order gate's step 2)" \
  "$BUILD" --db kynex_selftest_order --quiet --swap 020_constraints_a_f.sql,010_platform.sql

# ── 8. the normaliser widened ────────────────────────────────────────────────────────────
# Assertion 18's own failure mode: the gate goes red, and somebody makes the normaliser
# delete a little more until it goes green. The self-test refuses.
echo ""
echo "-- 8. the normaliser widened to swallow statement text ------------------------"
WIDE="$(mktemp -d)/schema-normalise.sh"
sed -e "s|^  sed -E .|  sed -E -e '/security_invoker/d' \\\\|" "$REPO_ROOT/scripts/schema-normalise.sh" > "$WIDE"
chmod +x "$WIDE"
run_expect_fail "schema-normalise.sh widened with an extra delete rule" "its own --self-test (assertion 18)" \
  "$WIDE" --self-test
rm -rf "$(dirname "$WIDE")"

# ── verdict ──────────────────────────────────────────────────────────────────────────────
echo ""
echo "=============================================================="
if [[ $FAILED -gt 0 ]]; then
  echo " SELF-TEST FAILED — $FAILED of $((PASS + FAILED)) defects were NOT caught"
  echo "=============================================================="
  exit 1
fi
echo " SELF-TEST PASSED — all $PASS deliberate defects were caught"
echo "=============================================================="
