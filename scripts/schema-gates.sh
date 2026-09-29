#!/usr/bin/env bash
# schema-gates.sh — ONE command that runs every §19.1 schema gate locally.
#
#   ./scripts/schema-gates.sh              everything, including the self-test (~4 min)
#   ./scripts/schema-gates.sh --fast       skip the self-test (~1 min)
#   ./scripts/schema-gates.sh --write      regenerate schema.sql, then run everything
#
# This is the command HOW_TO_CHANGE_THE_SCHEMA.md tells an engineer to run before opening a
# schema PR, and it is the same set of gates CI runs — deliberately the same scripts, not a
# local approximation of them, so "it passed locally" and "it passed in CI" cannot diverge.
#
# It needs Docker and nothing else: no psql, no local cluster, no .NET. One throwaway
# postgres:16 container is started, shared by every gate (so the ~9 rebuilds below cost one
# container rather than nine) and destroyed on exit, whether the run passes, fails or is
# interrupted. No real database is ever reachable from here.
#
# The gates, in the order they run and the order they matter:
#
#   0  schema-normalise.sh --self-test    assertion 18 — the drift gate's blind spot is only
#                                         the four declared line forms. Runs FIRST: if the
#                                         normaliser is wrong, gate 2's green means nothing.
#   1  schema-apply-order-gate.sh         the baseline is repeatable from empty, its filename
#                                         order is load-bearing, and the failure is legible
#   2  schema-drift-gate.sh               the canonical schema.sql byte-diff — the gate that
#                                         catches every trigger, policy, partition, EXCLUDE,
#                                         grant and COMMENT that EF cannot see
#   3  schema-ratchet-gate.sh             070_rls_proof.sql (reused) + 080_schema_ratchets.sql
#   4  schema-gate-selftest.sh            ten deliberate defects; each gate must catch its own
#
# Gate 4 is the one people will be tempted to skip. It is the only one that proves the other
# three can still fail.
set -euo pipefail
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/schema-gate-common.sh"

FAST=0
WRITE=0
for a in "$@"; do
  case "$a" in
    --fast)  FAST=1 ;;
    --write) WRITE=1 ;;
    -h|--help) sed -n '2,30p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "schema-gates.sh: unknown argument '$a'" >&2; exit 2 ;;
  esac
done

started=$(date +%s)
schema_gate_up
trap 'schema_gate_down' EXIT
export SCHEMA_GATE_REUSE=1

step() { echo ""; echo "══════════════════════════════════════════════════════════════"; echo " $*"; echo "══════════════════════════════════════════════════════════════"; }

step "0/4  normaliser self-test (assertion 18)"
"$REPO_ROOT/scripts/schema-normalise.sh" --self-test

if [[ $WRITE -eq 1 ]]; then
  step "--write  regenerating schema.sql"
  SCHEMA_GATE_DB=kynex_regen "$REPO_ROOT/scripts/schema-drift-gate.sh" --write
fi

step "1/4  apply-order gate (repeatability + order is load-bearing)"
SCHEMA_GATE_DB=kynex_order "$REPO_ROOT/scripts/schema-apply-order-gate.sh"

step "2/4  schema-drift gate (canonical schema.sql byte-diff)"
SCHEMA_GATE_DB=kynex_drift "$REPO_ROOT/scripts/schema-drift-gate.sh"

step "3/4  policy / partition / comment ratchets"
SCHEMA_GATE_DB=kynex_ratchet "$REPO_ROOT/scripts/schema-ratchet-gate.sh"

if [[ $FAST -eq 1 ]]; then
  step "4/4  gate self-test — SKIPPED (--fast)"
  echo "   The self-test is what proves the three gates above can still fail. CI always runs"
  echo "   it. Run it before you open the PR:  ./scripts/schema-gates.sh"
else
  step "4/4  gate self-test (ten deliberate defects)"
  SCHEMA_GATE_DB=kynex_selftest "$REPO_ROOT/scripts/schema-gate-selftest.sh"
fi

echo ""
echo "══════════════════════════════════════════════════════════════"
echo " ALL SCHEMA GATES PASSED  ($(( $(date +%s) - started ))s)"
echo "══════════════════════════════════════════════════════════════"
