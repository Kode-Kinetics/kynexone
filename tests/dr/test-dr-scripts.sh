#!/usr/bin/env bash
# shellcheck disable=SC2015
# Mocked tests for scripts/dr/*.sh. No network, no database: curl, psql, rclone are shims on PATH.
# Run: bash tests/dr/test-dr-scripts.sh
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DRILL="$ROOT/scripts/dr/restore-drill.sh"
B2="$ROOT/scripts/dr/b2-backup.sh"
PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "ok   - $1"; }
bad()  { FAIL=$((FAIL+1)); echo "FAIL - $1"; }
check() { # name, expected-rc, actual-rc
  if [ "$2" = "$3" ]; then ok "$1"; else bad "$1 (expected rc $2, got $3)"; fi
}

new_env() {
  T="$(mktemp -d)"; mkdir "$T/bin"; : > "$T/calls.log"
  # curl shim: logs "METHOD PATH", answers like the Neon API. PRIMARY_ID_MOCK / NEW_ID_MOCK tunable.
  cat > "$T/bin/curl" <<'SH'
#!/usr/bin/env bash
m=GET; url=""
while [ $# -gt 0 ]; do case "$1" in -X) m="$2"; shift 2 ;; http*) url="$1"; shift ;; *) shift ;; esac; done
p="${url#*api/v2}"; echo "$m $p" >> "$CALLS"
case "$m $p" in
  "GET /projects/proj-1/branches") echo "{\"branches\":[{\"id\":\"${PRIMARY_ID_MOCK:-br-primary}\",\"primary\":true},{\"id\":\"br-other\"}]}" ;;
  "POST /projects/proj-1/branches") echo "{\"branch\":{\"id\":\"${NEW_ID_MOCK:-br-drill}\"},\"endpoints\":[{\"id\":\"ep-1\"}]}" ;;
  "GET /projects/proj-1/endpoints/ep-1") echo '{"endpoint":{"current_state":"idle"}}' ;;
  "GET /projects/proj-1/branches/"*"/roles") echo '{"roles":[{"name":"owner","protected":false}]}' ;;
  "GET /projects/proj-1/connection_uri"*) echo '{"uri":"postgres://mock:mock@mock.invalid/neondb"}' ;;
  "DELETE /projects/proj-1/branches/"*) echo '{}' ;;
  *) echo "unexpected curl: $m $p" >&2; exit 22 ;;
esac
SH
  cat > "$T/bin/psql" <<'SH'
#!/usr/bin/env bash
echo "psql $*" >> "$CALLS"
[ "${PSQL_FAIL:-0}" = 1 ] && exit 1
echo "## 3. audit hash-chain spot check"
echo "entries_checked|missing_entry_hash|chain_breaks|result"
echo "200|0|${CHAIN_BREAKS:-0}|${CHAIN_RESULT:-PASS}"
SH
  cat > "$T/bin/rclone" <<'SH'
#!/usr/bin/env bash
echo "rclone $*" >> "$CALLS"
SH
  chmod +x "$T/bin/"*
  export CALLS="$T/calls.log" PATH="$T/bin:$PATH"
  export NEON_API_KEY=k NEON_PROJECT_ID=proj-1 TARGET_TIMESTAMP=2020-01-01T00:00:00Z
  export DR_OUT="$T/report.md" DR_POLL_SECONDS=0 DR_OPERATOR=tester
  unset PRIMARY_ID_MOCK NEW_ID_MOCK PSQL_FAIL CHAIN_RESULT CHAIN_BREAKS
}

# 1. dry run: no API calls at all, no key needed
new_env; unset NEON_API_KEY
bash "$DRILL" --dry-run >/dev/null 2>&1; check "dry-run exits 0" 0 $?
[ ! -s "$CALLS" ] && ok "dry-run makes no curl/psql call" || bad "dry-run made calls: $(cat "$CALLS")"

# 2. missing env / bad timestamp / future timestamp
new_env; unset NEON_PROJECT_ID; bash "$DRILL" --yes >/dev/null 2>&1; check "missing project id refused" 1 $?
new_env; TARGET_TIMESTAMP=yesterday bash "$DRILL" --yes >/dev/null 2>&1; check "bad timestamp refused" 1 $?
new_env; TARGET_TIMESTAMP=2999-01-01T00:00:00Z bash "$DRILL" --yes >/dev/null 2>&1; check "future timestamp refused" 1 $?
[ ! -s "$CALLS" ] && ok "validation failures make no calls" || bad "validation made calls"

# 3. non-interactive without --yes is refused
new_env; bash "$DRILL" </dev/null >/dev/null 2>&1; check "no tty and no --yes refused" 1 $?

# 4. happy path: creates branch from primary at timestamp, verifies, deletes drill branch only, report written
new_env; bash "$DRILL" --yes >"$T/out" 2>&1; check "happy path exits 0" 0 $?
grep -q '^POST /projects/proj-1/branches$' "$CALLS" && ok "branch created" || bad "no branch POST"
grep -q '^DELETE /projects/proj-1/branches/br-drill$' "$CALLS" && ok "drill branch deleted" || bad "drill branch not deleted"
! grep -q 'DELETE .*br-primary' "$CALLS" && ok "primary never deleted" || bad "primary delete attempted"
grep -q 'default_transaction_read_only=on' "$CALLS" || grep -q 'psql ' "$CALLS" && ok "psql invoked" || bad "psql not invoked"
grep -q 'Automated result | PASS' "$T/report.md" && ok "report says PASS" || bad "report missing PASS"
grep -q 'Signed:' "$T/report.md" && ok "report has sign-off block" || bad "report missing sign-off"
! grep -q 'mock.invalid' "$T/report.md" "$T/out" && ok "connection uri never printed" || bad "connection uri leaked"

# 5. safety rail: new branch id collides with primary -> abort, never delete
new_env; NEW_ID_MOCK=br-primary bash "$DRILL" --yes >/dev/null 2>&1; rc=$?
[ "$rc" != 0 ] && ok "id collision aborts" || bad "id collision did not abort"
! grep -q '^DELETE' "$CALLS" && ok "no delete when id equals primary" || bad "delete issued against primary id"

# 6. psql failure still deletes the drill branch and exits non-zero
new_env; PSQL_FAIL=1 bash "$DRILL" --yes >/dev/null 2>&1; rc=$?
[ "$rc" != 0 ] && ok "verification failure exits non-zero" || bad "verification failure exited 0"
grep -q '^DELETE /projects/proj-1/branches/br-drill$' "$CALLS" && ok "cleanup deletes branch after failure" || bad "no cleanup after failure"

# 7. hash-chain FAIL -> exit 3, report says FAIL
new_env; CHAIN_RESULT=FAIL CHAIN_BREAKS=2 bash "$DRILL" --yes >/dev/null 2>&1; check "chain break exits 3" 3 $?
grep -q 'Automated result | FAIL' "$T/report.md" && ok "report says FAIL" || bad "report missing FAIL"

# 8. --keep-branch does not delete
new_env; bash "$DRILL" --yes --keep-branch >/dev/null 2>&1
! grep -q '^DELETE' "$CALLS" && ok "--keep-branch keeps branch" || bad "--keep-branch deleted"

# 9. b2-backup
new_env
export SRC_ENDPOINT=https://s3.src.invalid SRC_BUCKET=docs SRC_ACCESS_KEY=a SRC_SECRET_KEY=b
export DST_ENDPOINT=https://s3.dst.invalid DST_BUCKET=docs-bak DST_ACCESS_KEY=c DST_SECRET_KEY=d
bash "$B2" --dry-run >/dev/null 2>&1; check "b2 dry-run exits 0" 0 $?
grep -q -- '--dry-run' "$CALLS" && ok "b2 dry-run passes --dry-run" || bad "b2 dry-run not dry"
: > "$CALLS"; bash "$B2" >/dev/null 2>&1; check "b2 live exits 0" 0 $?
grep -q '^rclone copy ' "$CALLS" && ok "b2 uses copy" || bad "b2 no copy"
! grep -q 'rclone sync' "$CALLS" && ok "b2 never syncs (no delete propagation)" || bad "b2 used sync"
grep -q '^rclone check .*--one-way' "$CALLS" && ok "b2 verifies" || bad "b2 no verify"
DST_BUCKET=docs DST_ENDPOINT=$SRC_ENDPOINT bash "$B2" >/dev/null 2>&1; check "b2 same bucket refused" 1 $?
( unset DST_SECRET_KEY; bash "$B2" >/dev/null 2>&1 ); check "b2 missing secret refused" 1 $?

echo; echo "passed=$PASS failed=$FAIL"; [ "$FAIL" = 0 ]
