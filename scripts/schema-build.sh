#!/usr/bin/env bash
# schema-build.sh — build one database from EMPTY using the baseline SQL files.
#
#   ./scripts/schema-build.sh --db kynex_fresh
#   ./scripts/schema-build.sh --db kynex_wrong --swap 020_constraints_a_f.sql,010_platform.sql
#   ./scripts/schema-build.sh --list            # print the discovered apply order and exit
#
# This is the single apply path every schema gate shares, so "the database the ratchets
# checked" and "the database schema.sql was dumped from" cannot drift apart by having been
# built two different ways.
#
# ── The two roles the apply path uses, and why ───────────────────────────────────────────
# 001 and 002 are bootstrap: 002 CREATEs the roles, so nothing can yet connect as one of
# them. They run as the superuser. Everything after runs as `kynex_migrator` with
# `SET ROLE kynex_owner`, exactly as the deploy applies it (§19.2) — that is what makes every
# object OWNED by kynex_owner, and therefore what makes the grants in 060 and the
# `security_invoker` requirement of assertion 13 mean what they say. Applying the whole thing
# as the superuser would build a database that passes RLS checks it should fail.
#
# ── Apply order is DISCOVERED, never listed ──────────────────────────────────────────────
# The files are globbed and sorted. `tests/` is excluded — it holds proofs, not schema — and
# so is `schema.sql`, which is the dump of the result and must never be an input.
set -euo pipefail
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/schema-gate-common.sh"

DB=""; SWAP=""; LIST_ONLY=0; QUIET=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --db)     DB="$2"; shift 2 ;;
    --swap)   SWAP="$2"; shift 2 ;;   # "A,B" — move A to sit before B, to prove order matters
    --list)   LIST_ONLY=1; shift ;;
    --quiet)  QUIET=1; shift ;;
    *) echo "schema-build.sh: unknown argument '$1'" >&2; exit 2 ;;
  esac
done
[[ -n "$DB" || $LIST_ONLY -eq 1 ]] || { echo "schema-build.sh: --db is required" >&2; exit 2; }

# (while-read rather than `mapfile`: macOS ships bash 3.2, and the local entry point must not
# need a newer shell than the machine has.)
ALL=()
while IFS= read -r f; do ALL+=("$f"); done < <(baseline_files)
[[ ${#ALL[@]} -ge 10 ]] || { echo "schema-build.sh: only ${#ALL[@]} baseline files under $BASELINE_DIR" >&2; exit 1; }

BOOTSTRAP=(); MIGRATED=()
for f in "${ALL[@]}"; do
  case "$(basename "$f")" in
    001_*|002_*) BOOTSTRAP+=("$f") ;;
    *)           MIGRATED+=("$f") ;;
  esac
done

# ── --swap: deliberately apply one file before another ───────────────────────────────────
# Used only by the apply-order gate's negative case: `--swap 020_constraints_a_f.sql,\
# 010_platform.sql` puts the constraints ahead of the tables they constrain. The build must
# then FAIL; a build that survives its own files being reordered is a build whose order was
# never load-bearing, and the gate would be theatre.
if [[ -n "$SWAP" ]]; then
  a="${SWAP%%,*}"; b="${SWAP##*,}"
  rest=(); moved=""
  for f in "${MIGRATED[@]}"; do
    if [[ "$(basename "$f")" == "$a" ]]; then moved="$f"; else rest+=("$f"); fi
  done
  [[ -n "$moved" ]] || { echo "schema-build.sh: --swap source '$a' is not a baseline file" >&2; exit 2; }
  final=(); placed=0
  for f in "${rest[@]}"; do
    if [[ "$(basename "$f")" == "$b" ]]; then final+=("$moved"); placed=1; fi
    final+=("$f")
  done
  [[ $placed -eq 1 ]] || { echo "schema-build.sh: --swap target '$b' is not a baseline file" >&2; exit 2; }
  MIGRATED=("${final[@]}")
fi

if [[ $LIST_ONLY -eq 1 ]]; then
  for f in "${BOOTSTRAP[@]}"; do echo "bootstrap  $(basename "$f")"; done
  for f in "${MIGRATED[@]}";  do echo "migrator   $(basename "$f")"; done
  exit 0
fi

say() { [[ $QUIET -eq 1 ]] || echo "$@"; }

# ── empty database, every time ───────────────────────────────────────────────────────────
pg_psql postgres postgres -v ON_ERROR_STOP=1 -q <<SQL
DROP DATABASE IF EXISTS "$DB" WITH (FORCE);
CREATE DATABASE "$DB";
SQL

say "== building $DB from empty (${#BOOTSTRAP[@]} bootstrap + ${#MIGRATED[@]} migrated files) =="
for f in "${BOOTSTRAP[@]}"; do
  say "   $(basename "$f")  (superuser)"
  pg_psql postgres "$DB" -v ON_ERROR_STOP=1 -q < "$f"
done

# Every login role gets a password so a gate can open a GENUINE per-role connection rather
# than relying on SET ROLE. Throwaway database only; it is dropped by the next build.
pg_psql postgres "$DB" -v ON_ERROR_STOP=1 -q <<SQL
ALTER ROLE kynex_migrator PASSWORD '$SCHEMA_GATE_ROLEPASS';
ALTER ROLE kynex_app      PASSWORD '$SCHEMA_GATE_ROLEPASS';
ALTER ROLE kynex_job      PASSWORD '$SCHEMA_GATE_ROLEPASS';
ALTER ROLE kynex_platform PASSWORD '$SCHEMA_GATE_ROLEPASS';
ALTER ROLE kynex_ro       PASSWORD '$SCHEMA_GATE_ROLEPASS';
SQL

for f in "${MIGRATED[@]}"; do
  say "   $(basename "$f")  (kynex_migrator → kynex_owner)"
  { echo "SET ROLE kynex_owner;"; cat "$f"; } | pg_psql kynex_migrator "$DB" -v ON_ERROR_STOP=1 -q
done

say "== $DB built =="
