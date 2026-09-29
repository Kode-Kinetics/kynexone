#!/usr/bin/env bash
# schema-ratchet-gate.sh — the policy, partition, comment and retention ratchets.
#
#   ./scripts/schema-ratchet-gate.sh
#
# Builds a database from EMPTY out of the baseline files and runs the two proof suites
# against it, in this order:
#
#   tests/070_rls_proof.sql        the RLS and partition proofs that already existed —
#                                  fail-closed, cross-tenant reads, self-promotion, the
#                                  credential surface, transitive BYPASSRLS reachability,
#                                  forced RLS everywhere, ungranted partition children,
#                                  security_invoker views AND the counter-proof that the
#                                  flag is what holds the door.
#   tests/080_schema_ratchets.sql  the assertions 070 does not make — declared policy SHAPE
#                                  vs live policy text, the manifest closing in both
#                                  directions and arithmetically, table comments, owners,
#                                  retention declarations, index justification, and the heap
#                                  check that closes the drift normaliser's blind spot.
#
# 070 is REUSED, not re-implemented. It seeds two tenants and briefly grants and revokes on a
# partition child, which is why it only ever runs against a container this script created and
# will destroy. 080 runs after it, on the same database, so the ratchets see a schema that has
# had real rows through it rather than an empty one.
set -euo pipefail
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/schema-gate-common.sh"

DB="${SCHEMA_GATE_DB:-kynex_ratchet}"
TESTS="$BASELINE_DIR/tests"

if [[ "${SCHEMA_GATE_REUSE:-0}" != "1" ]]; then
  schema_gate_up
  trap 'schema_gate_down' EXIT
fi

echo "== policy / partition / comment ratchets =="
"$REPO_ROOT/scripts/schema-build.sh" --db "$DB" --quiet

# psql exits non-zero on a RAISE EXCEPTION with ON_ERROR_STOP, which is how both suites fail.
# No `|| true` anywhere in this file: a gate that cannot fail is not a gate, and this
# repository has already shipped a lint that passed vacuously.
echo ""
echo "-- tests/070_rls_proof.sql (reused) ------------------------------------------"
pg_psql postgres "$DB" -v ON_ERROR_STOP=1 -q < "$TESTS/070_rls_proof.sql"

echo ""
echo "-- tests/080_schema_ratchets.sql ---------------------------------------------"
pg_psql postgres "$DB" -v ON_ERROR_STOP=1 -q < "$TESTS/080_schema_ratchets.sql"

echo ""
echo "RATCHET GATE PASSED"
