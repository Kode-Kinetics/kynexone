#!/usr/bin/env bash
# KynexOne Neon point-in-time restore drill. See docs/runbooks/disaster-recovery.md.
#
# Creates a throwaway Neon branch from the primary at a past timestamp, runs scripts/dr/verify.sql
# on it (read-only), writes a markdown evidence report, and deletes the branch.
#
# Required env (supplied by the owner at run time, never stored in the repo):
#   NEON_API_KEY        Neon API key
#   NEON_PROJECT_ID     Neon project id
#   TARGET_TIMESTAMP    RFC 3339 UTC instant to restore to, e.g. 2026-10-26T09:00:00Z (must be in the past)
# Optional env:
#   NEON_DATABASE_NAME  default neondb       NEON_ROLE_NAME  default: first non-protected role
#   DR_OPERATOR         name written into the report sign-off block
#   DR_OUT              report path (default ./dr-evidence-<UTC stamp>.md)
#   NEON_API_BASE       default https://console.neon.tech/api/v2
#   DR_POLL_SECONDS     default 5        DR_POLL_MAX  default 60 polls
# Flags: --dry-run      print what would happen, make no API call, touch nothing
#        --yes          skip the interactive confirmation (CI)
#        --keep-branch  do not delete the drill branch (you must delete it yourself)
set -euo pipefail

DRY=0; YES=0; KEEP=0
for a in "$@"; do
  case "$a" in
    --dry-run) DRY=1 ;; --yes) YES=1 ;; --keep-branch) KEEP=1 ;;
    -h|--help) sed -n 2,19p "$0"; exit 0 ;;
    *) echo "unknown argument: $a" >&2; exit 2 ;;
  esac
done

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API="${NEON_API_BASE:-https://console.neon.tech/api/v2}"
DB="${NEON_DATABASE_NAME:-neondb}"
POLL="${DR_POLL_SECONDS:-5}"; POLL_MAX="${DR_POLL_MAX:-60}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="${DR_OUT:-./dr-evidence-$STAMP.md}"
BRANCH_NAME="dr-drill-$STAMP"
BRANCH_ID=""; PRIMARY_ID=""

die() { echo "ERROR: $*" >&2; exit 1; }
need() { [ -n "${!1:-}" ] || die "$1 is required"; }

need NEON_PROJECT_ID; need TARGET_TIMESTAMP
[ "$DRY" = 1 ] || need NEON_API_KEY
[[ "$NEON_PROJECT_ID" =~ ^[A-Za-z0-9_-]+$ ]] || die "NEON_PROJECT_ID has unexpected characters"
[[ "$TARGET_TIMESTAMP" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]] \
  || die "TARGET_TIMESTAMP must be RFC 3339 UTC like 2026-10-26T09:00:00Z"
command -v jq >/dev/null || die "jq is required"
command -v curl >/dev/null || die "curl is required"
[ "$DRY" = 1 ] || command -v psql >/dev/null || die "psql is required"

# Past-only check (GNU and BSD date).
ts_epoch() { date -u -d "$1" +%s 2>/dev/null || date -u -j -f "%Y-%m-%dT%H:%M:%SZ" "$1" +%s; }
T_EPOCH="$(ts_epoch "$TARGET_TIMESTAMP")"
[ "$T_EPOCH" -lt "$(date -u +%s)" ] || die "TARGET_TIMESTAMP must be in the past"

neon() { # method path [json-body]
  local m="$1" p="$2" b="${3:-}"
  if [ -n "$b" ]; then
    curl -sS --fail-with-body -X "$m" "$API$p" -H "Authorization: Bearer $NEON_API_KEY" \
      -H 'Content-Type: application/json' -H 'Accept: application/json' -d "$b"
  else
    curl -sS --fail-with-body -X "$m" "$API$p" -H "Authorization: Bearer $NEON_API_KEY" -H 'Accept: application/json'
  fi
}

cat <<MSG
KynexOne restore drill
  project          : $NEON_PROJECT_ID
  restore point    : $TARGET_TIMESTAMP
  drill branch     : $BRANCH_NAME  (new, throwaway, deleted at the end)
  database         : $DB
  report           : $OUT
  mode             : $([ "$DRY" = 1 ] && echo DRY-RUN || echo LIVE)
The primary branch is never modified, connected to, or deleted by this script.
MSG

if [ "$DRY" = 1 ]; then
  cat <<MSG
Dry run. Would:
  1. GET    /projects/$NEON_PROJECT_ID/branches     (find the primary branch id)
  2. POST   /projects/$NEON_PROJECT_ID/branches     (branch at $TARGET_TIMESTAMP + read_only endpoint)
  3. poll the endpoint until idle, fetch a connection URI for the drill branch only
  4. psql -f $HERE/verify.sql                       (READ ONLY transaction)
  5. DELETE the drill branch (refused if its id equals the primary id or its name lacks the dr-drill- prefix)
No API call was made.
MSG
  exit 0
fi

if [ "$YES" != 1 ]; then
  [ -t 0 ] || die "not a terminal; pass --yes to confirm non-interactively"
  read -r -p "Create branch $BRANCH_NAME from project $NEON_PROJECT_ID at $TARGET_TIMESTAMP? Type 'restore' to continue: " ans
  [ "$ans" = "restore" ] || die "aborted by operator"
fi

cleanup() {
  local rc=$?
  trap - EXIT
  if [ -n "$BRANCH_ID" ] && [ "$KEEP" != 1 ]; then
    if [ -z "$PRIMARY_ID" ] || [ "$BRANCH_ID" = "$PRIMARY_ID" ]; then
      echo "REFUSING to delete branch $BRANCH_ID: it is (or cannot be proven not to be) the primary." >&2
    elif [[ "$BRANCH_NAME" != dr-drill-* ]]; then
      echo "REFUSING to delete branch with unexpected name." >&2
    else
      echo "Deleting drill branch $BRANCH_ID ..."
      neon DELETE "/projects/$NEON_PROJECT_ID/branches/$BRANCH_ID" >/dev/null \
        || echo "WARNING: could not delete drill branch $BRANCH_ID ($BRANCH_NAME); delete it by hand in the Neon console." >&2
    fi
  elif [ -n "$BRANCH_ID" ]; then
    echo "Drill branch $BRANCH_ID ($BRANCH_NAME) KEPT (--keep-branch). Delete it when done." >&2
  fi
  exit "$rc"
}
trap cleanup EXIT

START_EPOCH="$(date -u +%s)"
PRIMARY_ID="$(neon GET "/projects/$NEON_PROJECT_ID/branches" | jq -r '[.branches[] | select(.primary == true or .default == true)][0].id // empty')"
[ -n "$PRIMARY_ID" ] || die "could not identify the primary branch; refusing to continue"

BODY="$(jq -n --arg n "$BRANCH_NAME" --arg p "$PRIMARY_ID" --arg t "$TARGET_TIMESTAMP" \
  '{branch:{name:$n,parent_id:$p,parent_timestamp:$t},endpoints:[{type:"read_only"}]}')"
RESP="$(neon POST "/projects/$NEON_PROJECT_ID/branches" "$BODY")"
BRANCH_ID="$(jq -r '.branch.id // empty' <<<"$RESP")"
[ -n "$BRANCH_ID" ] || die "branch creation returned no branch id"
[ "$BRANCH_ID" != "$PRIMARY_ID" ] || { BRANCH_ID=""; die "new branch id equals primary id; aborting"; }
EP_ID="$(jq -r '.endpoints[0].id // empty' <<<"$RESP")"
[ -n "$EP_ID" ] || die "branch creation returned no endpoint"
echo "Created branch $BRANCH_ID endpoint $EP_ID; waiting for it to become ready ..."

n=0
while :; do
  st="$(neon GET "/projects/$NEON_PROJECT_ID/endpoints/$EP_ID" | jq -r '.endpoint.current_state // empty')"
  case "$st" in idle|active) break ;; esac
  n=$((n+1)); [ "$n" -le "$POLL_MAX" ] || die "endpoint not ready after $POLL_MAX polls (state=$st)"
  sleep "$POLL"
done
READY_EPOCH="$(date -u +%s)"
echo "Restore branch ready in $((READY_EPOCH-START_EPOCH))s."

ROLE="${NEON_ROLE_NAME:-}"
if [ -z "$ROLE" ]; then
  ROLE="$(neon GET "/projects/$NEON_PROJECT_ID/branches/$BRANCH_ID/roles" | jq -r '[.roles[] | select(.protected != true)][0].name // empty')"
  [ -n "$ROLE" ] || die "no usable role found; set NEON_ROLE_NAME"
fi
URI="$(neon GET "/projects/$NEON_PROJECT_ID/connection_uri?branch_id=$BRANCH_ID&endpoint_id=$EP_ID&database_name=$DB&role_name=$ROLE" | jq -r '.uri // empty')"
[ -n "$URI" ] || die "no connection uri returned"
# The URI is a credential: it is never echoed or written to the report.

# Read-only at session level too, in addition to the READ ONLY transaction inside verify.sql.
VERIFY_OUT="$(PGOPTIONS='-c default_transaction_read_only=on -c statement_timeout=120000' \
  psql "$URI" -X -q -v ON_ERROR_STOP=1 -f "$HERE/verify.sql")" || die "verification SQL failed"
END_EPOCH="$(date -u +%s)"

FAILS="$(grep -c '|FAIL$' <<<"$VERIFY_OUT" || true)"
RESULT="PASS"; [ "$FAILS" = 0 ] || RESULT="FAIL"
NOW_ISO="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
LAG_MIN=$(( ( $(date -u +%s) - T_EPOCH ) / 60 ))

{
  echo "# KynexOne restore drill evidence"
  echo
  echo "| Field | Value |"
  echo "|---|---|"
  echo "| Run (UTC) | $NOW_ISO |"
  echo "| Neon project | $NEON_PROJECT_ID |"
  echo "| Restore point | $TARGET_TIMESTAMP ($LAG_MIN min before this run) |"
  echo "| Drill branch | $BRANCH_NAME ($BRANCH_ID), deleted after run: $([ "$KEEP" = 1 ] && echo no || echo yes) |"
  echo "| Time to a queryable restore | $((READY_EPOCH-START_EPOCH)) s (DB-step RTO evidence) |"
  echo "| Verification SQL runtime | $((END_EPOCH-READY_EPOCH)) s |"
  echo "| Automated result | $RESULT (audit hash-chain section) |"
  echo "| Operator | ${DR_OPERATOR:-<unset>} |"
  echo
  echo "## Verification output"
  echo
  echo '```text'
  echo "$VERIFY_OUT"
  echo '```'
  echo
  echo "## Sign-off (complete by hand)"
  echo
  echo "- [ ] Migration id above equals the id on the primary at the restore point"
  echo "- [ ] Row counts are plausible against the primary (small drift for later writes is expected)"
  echo "- [ ] Payroll checksum matches the value recorded at period close"
  echo "- [ ] Drill branch deleted (Neon console lists no dr-drill-* branch)"
  echo "- [ ] Measured RPO / RTO entered in the drill log in docs/runbooks/disaster-recovery.md"
  echo
  echo "Signed: ______________________  Role: ______________  Date: ____________"
} > "$OUT"

echo "Evidence written to $OUT (result: $RESULT)"
[ "$RESULT" = PASS ] || exit 3
