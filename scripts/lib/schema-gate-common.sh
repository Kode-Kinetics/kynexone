#!/usr/bin/env bash
# schema-gate-common.sh — the one connection layer every schema gate shares.
#
# Sourced, never executed. It exists so the gates run identically in two places that reach
# Postgres differently:
#
#   SCHEMA_GATE_MODE=docker   (default, a developer's machine)
#       A throwaway `postgres:16` container this library starts and stops. Nothing on the
#       machine needs psql installed, and no local cluster is touched.
#
#   SCHEMA_GATE_MODE=direct   (CI)
#       An already-running server on PGHOST:PGPORT — the `services: postgres` container the
#       existing `schema-gates` job already declares, reached with the runner's
#       postgresql-client. This is the style the repository's other database jobs use, so the
#       new job does not introduce a second one.
#
# postgres:16 and not 17, deliberately: production is Neon PG 17.11, so the baseline must not
# depend on anything newer than 16. A gate proved on 17 would pass on syntax 16 rejects.
set -euo pipefail

SCHEMA_GATE_MODE="${SCHEMA_GATE_MODE:-docker}"
SCHEMA_GATE_CONTAINER="${SCHEMA_GATE_CONTAINER:-kynex-schema-gate}"
SCHEMA_GATE_IMAGE="${SCHEMA_GATE_IMAGE:-postgres:16}"
SCHEMA_GATE_SUPERPASS="${SCHEMA_GATE_SUPERPASS:-gate}"
SCHEMA_GATE_ROLEPASS="${SCHEMA_GATE_ROLEPASS:-gate}"
PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BASELINE_DIR="$REPO_ROOT/backend-dotnet/Zayra.Api/Db/baseline"
CANONICAL_SCHEMA="$BASELINE_DIR/schema.sql"

# ── connection ───────────────────────────────────────────────────────────────────────────
pg_psql() {           # pg_psql <role> <db> [psql args…]
  local role="$1" db="$2"; shift 2
  if [[ "$SCHEMA_GATE_MODE" == "docker" ]]; then
    docker exec -i "$SCHEMA_GATE_CONTAINER" psql -U "$role" -d "$db" "$@"
  else
    local pass="$SCHEMA_GATE_ROLEPASS"
    [[ "$role" == "postgres" ]] && pass="$SCHEMA_GATE_SUPERPASS"
    PGPASSWORD="$pass" psql -h "$PGHOST" -p "$PGPORT" -U "$role" -d "$db" "$@"
  fi
}

pg_dump_schema() {    # pg_dump_schema <db>  → the raw --schema-only --no-owner dump on stdout
  local db="$1"
  if [[ "$SCHEMA_GATE_MODE" == "docker" ]]; then
    docker exec "$SCHEMA_GATE_CONTAINER" pg_dump -U postgres -d "$db" --schema-only --no-owner
  else
    PGPASSWORD="$SCHEMA_GATE_SUPERPASS" pg_dump -h "$PGHOST" -p "$PGPORT" -U postgres -d "$db" --schema-only --no-owner
  fi
}

# ── lifecycle ────────────────────────────────────────────────────────────────────────────
schema_gate_up() {
  if [[ "$SCHEMA_GATE_MODE" != "docker" ]]; then
    for _ in $(seq 1 60); do
      PGPASSWORD="$SCHEMA_GATE_SUPERPASS" pg_isready -h "$PGHOST" -p "$PGPORT" -U postgres >/dev/null 2>&1 && return 0
      sleep 1
    done
    echo "schema gates: no Postgres on $PGHOST:$PGPORT" >&2; return 1
  fi
  command -v docker >/dev/null || { echo "schema gates: docker is not on PATH" >&2; return 1; }
  docker rm -f "$SCHEMA_GATE_CONTAINER" >/dev/null 2>&1 || true
  docker run -d --name "$SCHEMA_GATE_CONTAINER" \
    -e POSTGRES_PASSWORD="$SCHEMA_GATE_SUPERPASS" -e POSTGRES_DB=postgres \
    "$SCHEMA_GATE_IMAGE" >/dev/null
  for _ in $(seq 1 90); do
    docker exec "$SCHEMA_GATE_CONTAINER" pg_isready -U postgres -d postgres >/dev/null 2>&1 && return 0
    sleep 1
  done
  echo "schema gates: $SCHEMA_GATE_CONTAINER never became ready" >&2; return 1
}

schema_gate_down() {
  [[ "$SCHEMA_GATE_MODE" == "docker" ]] || return 0
  [[ "${SCHEMA_GATE_KEEP:-0}" == "1" ]] && { echo "   (container $SCHEMA_GATE_CONTAINER kept: SCHEMA_GATE_KEEP=1)"; return 0; }
  docker rm -f "$SCHEMA_GATE_CONTAINER" >/dev/null 2>&1 || true
}

# ── the apply set, discovered rather than listed ─────────────────────────────────────────
# A new numbered baseline file is picked up by every gate the moment it lands. Nothing to
# forget to update — which is the whole difference between a ratchet and a checklist.
baseline_files() {
  find "$BASELINE_DIR" -maxdepth 1 -name '[0-9][0-9][0-9]_*.sql' | sort
}

gate_fail() { echo ""; echo "GATE FAILED — $*" >&2; exit 1; }
gate_ok()   { echo "   ok   $*"; }
