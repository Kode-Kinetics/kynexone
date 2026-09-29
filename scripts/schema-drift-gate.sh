#!/usr/bin/env bash
# schema-drift-gate.sh — §19.1 gate 1: the canonical schema.sql byte-diff.
#
#   ./scripts/schema-drift-gate.sh            # verify  (what CI runs)
#   ./scripts/schema-drift-gate.sh --write    # regenerate schema.sql, then commit the diff
#
# Builds a database from EMPTY out of backend-dotnet/Zayra.Api/Db/baseline/[0-9][0-9][0-9]_*.sql,
# dumps it with `pg_dump --schema-only --no-owner`, normalises it with schema-normalise.sh —
# and nothing else — and byte-diffs the result against the committed
# backend-dotnet/Zayra.Api/Db/baseline/schema.sql.
#
# ── Why this is the gate that matters ────────────────────────────────────────────────────
# The six gates in the existing `schema-gates` job are all EF-based, so they share EF's blind
# spot: a trigger, an RLS policy, a partition bound, an `EXCLUDE USING gist`, a `COMMENT`, a
# grant and a `security_invoker` reloption are all invisible to `dotnet ef migrations` and to
# the model snapshot. Fresh-vs-upgrade convergence compares two EQUALLY BLIND schemas and
# passes. `pg_catalog` is not blind, and a byte-diff of its dump cannot be argued with.
#
# ── Why a byte-diff and not a semantic comparison ────────────────────────────────────────
# Because a semantic comparison is a program with its own bugs and its own blind spots, and
# the first time it reports green on something real nobody will know. `diff` has neither.
# The cost is that the file must be regenerated on every schema change; `--write` is one
# command and the failure message prints it.
set -euo pipefail
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/schema-gate-common.sh"

WRITE=0
[[ "${1:-}" == "--write" ]] && WRITE=1

NORMALISE="$REPO_ROOT/scripts/schema-normalise.sh"
DB="${SCHEMA_GATE_DB:-kynex_drift}"
OWN_CONTAINER=0

if [[ "${SCHEMA_GATE_REUSE:-0}" != "1" ]]; then
  schema_gate_up
  OWN_CONTAINER=1
  trap 'schema_gate_down' EXIT
fi

echo "== §19.1 gate 1: canonical schema.sql byte-diff =="

# The normaliser's own proof runs first. It is the gate's blind spot by construction — what
# it deletes, the diff cannot see — so assertion 18 is checked before the diff relies on it.
"$NORMALISE" --self-test

"$REPO_ROOT/scripts/schema-build.sh" --db "$DB" --quiet

TMP="$(mktemp -d)"
trap '[[ $OWN_CONTAINER -eq 1 ]] && schema_gate_down; rm -rf "$TMP"' EXIT

pg_dump_schema "$DB" > "$TMP/raw.sql"
"$NORMALISE" < "$TMP/raw.sql" > "$TMP/fresh.sql"

# The nonce guard is load-bearing, and this proves it on THIS server rather than assuming it.
# pg_dump 16.10+/17.6+ regenerates the \restrict token every invocation, so two dumps of one
# unchanged database differ. If a future pg_dump stops emitting them the gate still works;
# what must never happen is the normaliser silently removing something that was stable.
pg_dump_schema "$DB" > "$TMP/raw2.sql"
if cmp -s "$TMP/raw.sql" "$TMP/raw2.sql"; then
  echo "   note: this pg_dump is already byte-stable across invocations (no nonce)"
else
  echo "   note: raw dumps differ across invocations (the \\restrict nonce) — normalisation is load-bearing"
fi
"$NORMALISE" < "$TMP/raw2.sql" > "$TMP/fresh2.sql"
cmp -s "$TMP/fresh.sql" "$TMP/fresh2.sql" \
  || gate_fail "two dumps of ONE database differ after normalisation. Something in the dump is
   non-deterministic beyond the four normalised forms, and no byte-diff gate can stand on it.
$(diff "$TMP/fresh.sql" "$TMP/fresh2.sql" | head -40)"
gate_ok "the normalised dump is deterministic"

if [[ $WRITE -eq 1 ]]; then
  cp "$TMP/fresh.sql" "$CANONICAL_SCHEMA"
  echo ""
  echo "WROTE $CANONICAL_SCHEMA  ($(wc -l < "$CANONICAL_SCHEMA" | tr -d ' ') lines)"
  echo "Review the diff and commit it in the SAME commit as the DDL change."
  exit 0
fi

[[ -f "$CANONICAL_SCHEMA" ]] || gate_fail "$CANONICAL_SCHEMA does not exist.
   Generate it:  ./scripts/schema-drift-gate.sh --write"

if cmp -s "$TMP/fresh.sql" "$CANONICAL_SCHEMA"; then
  gate_ok "schema.sql matches a fresh build of the baseline, byte for byte ($(wc -l < "$CANONICAL_SCHEMA" | tr -d ' ') lines)"
  echo ""
  echo "SCHEMA DRIFT GATE PASSED"
  exit 0
fi

echo ""
echo "GATE FAILED — the committed schema.sql is not what the baseline files build." >&2
echo "" >&2
echo "  - lines are in the COMMITTED schema.sql and not in a fresh build" >&2
echo "  + lines are in a FRESH BUILD and not in the committed schema.sql" >&2
echo "" >&2
diff "$CANONICAL_SCHEMA" "$TMP/fresh.sql" | head -120 >&2
echo "" >&2
echo "  (first 120 diff lines; $(diff "$CANONICAL_SCHEMA" "$TMP/fresh.sql" | wc -l | tr -d ' ') in total)" >&2
echo "" >&2
echo "Fix:  ./scripts/schema-drift-gate.sh --write     (then commit schema.sql with the DDL)" >&2
echo "" >&2
echo "If you did not intend a schema change, this diff is the change you did not mean to make." >&2
exit 1
