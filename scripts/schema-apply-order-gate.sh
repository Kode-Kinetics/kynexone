#!/usr/bin/env bash
# schema-apply-order-gate.sh — the baseline is repeatable, and its order is load-bearing.
#
#   ./scripts/schema-apply-order-gate.sh
#
# Three assertions, in this order:
#
#   1  REPEATABILITY. Build a database from EMPTY using the numbered baseline files in
#      filename order. Do it TWICE, into two separate databases, and prove the two normalised
#      dumps are byte-identical. A baseline that embeds `now()`, a random name, a serial that
#      starts wherever the last run left it, or an `IF NOT EXISTS` that silently skips on the
#      second pass produces two different schemas from one input — and the drift gate would
#      then be red or green depending on which build happened to run first.
#
#   2  THE ORDER IS LOAD-BEARING. Build again with two files deliberately swapped
#      (constraints before the tables they constrain) and require the build to FAIL. If an
#      out-of-order apply succeeds, then "applied in filename order" was never a property of
#      this baseline, and assertion 1 was proving nothing about order at all. This is the
#      half that stops the gate being theatre.
#
#   3  THE FAILURE IS LEGIBLE. The out-of-order build must fail with a Postgres error naming
#      the missing object, not with a shell error, a timeout or a silent zero-row result. An
#      engineer who hits this at 2am has to be able to read it.
#
# Nothing here touches a real database: every build DROPs and CREATEs its own throwaway.
set -euo pipefail
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/schema-gate-common.sh"

NORMALISE="$REPO_ROOT/scripts/schema-normalise.sh"
BUILD="$REPO_ROOT/scripts/schema-build.sh"

if [[ "${SCHEMA_GATE_REUSE:-0}" != "1" ]]; then
  schema_gate_up
  trap 'schema_gate_down' EXIT
fi
TMP="$(mktemp -d)"
cleanup() { [[ "${SCHEMA_GATE_REUSE:-0}" == "1" ]] || schema_gate_down; rm -rf "$TMP"; }
trap cleanup EXIT

echo "== apply-order gate =="

# ── 0. the order every build uses, printed once so a reviewer can see it ─────────────────
echo "   discovered apply order:"
"$BUILD" --list | sed 's/^/      /'

# ── 1. repeatability ─────────────────────────────────────────────────────────────────────
echo ""
echo "   [1/3] building twice from empty, in filename order…"
"$BUILD" --db kynex_order_a --quiet
"$BUILD" --db kynex_order_b --quiet
pg_dump_schema kynex_order_a | "$NORMALISE" > "$TMP/a.sql"
pg_dump_schema kynex_order_b | "$NORMALISE" > "$TMP/b.sql"

if ! cmp -s "$TMP/a.sql" "$TMP/b.sql"; then
  echo "" >&2
  echo "GATE FAILED — two builds of the SAME baseline produced DIFFERENT schemas." >&2
  echo "" >&2
  echo "Something in the baseline is not deterministic. The usual causes, in order of how" >&2
  echo "often they are the cause: a DEFAULT or partition bound computed from now(); a name" >&2
  echo "or bound derived from the current date; an IF NOT EXISTS that skipped on one pass;" >&2
  echo "an ordering-dependent generated identifier." >&2
  echo "" >&2
  diff "$TMP/a.sql" "$TMP/b.sql" | head -80 >&2
  exit 1
fi
gate_ok "two independent builds are byte-identical ($(wc -l < "$TMP/a.sql" | tr -d ' ') lines)"

# and the repeatable result is the one that is committed — otherwise "repeatable" and
# "canonical" could both be true of two different schemas.
if [[ -f "$CANONICAL_SCHEMA" ]]; then
  cmp -s "$TMP/a.sql" "$CANONICAL_SCHEMA" \
    || gate_fail "the repeatable build does not match the committed schema.sql.
   Run ./scripts/schema-drift-gate.sh for the full diff, then --write to regenerate."
  gate_ok "and it is the schema committed as schema.sql"
fi

# ── 2. the order is load-bearing ─────────────────────────────────────────────────────────
# 020_constraints_a_f.sql ALTERs tables that 010_platform.sql creates. Put it first and
# Postgres must refuse. If it does not, filename order is decorative.
echo ""
echo "   [2/3] building with 020_constraints_a_f.sql moved BEFORE 010_platform.sql…"
set +e
"$BUILD" --db kynex_order_bad --quiet --swap 020_constraints_a_f.sql,010_platform.sql \
  > "$TMP/bad.out" 2> "$TMP/bad.err"
rc=$?
set -e

if [[ $rc -eq 0 ]]; then
  echo "" >&2
  echo "GATE FAILED — the baseline built successfully with its files OUT OF ORDER." >&2
  echo "" >&2
  echo "Filename order is then not a property of this baseline, and every other gate that" >&2
  echo "says 'applied in order' is describing a coincidence. Either the files have grown" >&2
  echo "IF NOT EXISTS / DO-block guards that make them order-independent — in which case the" >&2
  echo "guards are hiding real dependency errors — or 020 no longer constrains 010's tables" >&2
  echo "and this gate must be re-pointed at a pair that does still depend on each other." >&2
  exit 1
fi
gate_ok "an out-of-order apply fails (exit $rc), so filename order is load-bearing"

# ── 3. the failure is legible ────────────────────────────────────────────────────────────
echo ""
echo "   [3/3] checking the out-of-order failure names the missing object…"
if ! grep -qE 'ERROR:.*(does not exist|relation)' "$TMP/bad.err"; then
  echo "" >&2
  echo "GATE FAILED — the out-of-order build failed, but not with a legible Postgres error." >&2
  echo "A gate whose failure message does not say what is wrong gets disabled the first time" >&2
  echo "it is hit. What it actually printed:" >&2
  echo "" >&2
  head -20 "$TMP/bad.err" >&2
  exit 1
fi
gate_ok "it fails with: $(grep -m1 -E 'ERROR:' "$TMP/bad.err" | sed 's/^[[:space:]]*//' | cut -c1-90)"

echo ""
echo "APPLY-ORDER GATE PASSED"
