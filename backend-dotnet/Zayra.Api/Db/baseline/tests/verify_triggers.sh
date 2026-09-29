#!/usr/bin/env bash
# Verifies 022_constraints_cross.sql, 040_indexes.sql and 050_triggers.sql by
# applying the WHOLE baseline 001 -> 060 to an empty throwaway PostgreSQL 16
# container and then proving the behaviour, not just the DDL. Nothing here
# touches a real database.
#
#   ./backend-dotnet/Zayra.Api/Db/baseline/tests/verify_triggers.sh
#
# Production is Neon PG 17.11; every construct used below (composite ON DELETE
# SET NULL (col), NULLS NOT DISTINCT, constraint triggers on partitioned tables)
# is PG15+ and behaves identically on 16 and 17.
set -euo pipefail

CONTAINER=${CONTAINER:-kynex-trigger-verify}
IMAGE=${IMAGE:-postgres:16}
DB=kynex_baseline
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE="$(dirname "$HERE")"

FILES=(001_extensions 002_roles 010_platform 011_identity 012_org 013_employees
       014_statutory 015_payroll 016_wps_gl 017_leave_attendance 018_workflow_audit
       020_constraints_a_f 021_constraints_g_r 022_constraints_cross
       030_partitions 040_indexes 050_triggers 060_policies)

cleanup() { docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT
cleanup

docker run -d --name "$CONTAINER" -e POSTGRES_PASSWORD=verify -e POSTGRES_DB="$DB" "$IMAGE" >/dev/null
for _ in $(seq 1 60); do
  docker exec "$CONTAINER" pg_isready -U postgres -d "$DB" >/dev/null 2>&1 && break
  sleep 1
done

q()  { docker exec -i "$CONTAINER" psql -U postgres -d "$DB" -v ON_ERROR_STOP=1 -qtA "$@"; }

echo "== applying 001 -> 060 from empty =="
for f in "${FILES[@]}"; do
  if [[ ! -f "$BASE/$f.sql" ]]; then
    printf '   %-26s SKIPPED (not written yet)\n' "$f"
    continue
  fi
  printf '   %-26s' "$f"
  q -q < "$BASE/$f.sql"
  echo ok
done

# ---------------------------------------------------------------------------
# Assertion helpers. A "must fail" case is run as its own transaction, so a
# DEFERRABLE INITIALLY DEFERRED constraint trigger is genuinely exercised at
# COMMIT rather than smuggled past by a savepoint.
# ---------------------------------------------------------------------------
pass() { printf '   %-52s ok\n' "$1"; }
fail() { printf '   %-52s FAILED\n' "$1"; exit 1; }

ok_sql() {   # ok_sql "label" <<SQL ... SQL
  local label="$1"; local sql; sql="$(cat)"
  if printf '%s' "$sql" | q -q >/dev/null 2>&1; then pass "$label"; else
    printf '   %-52s FAILED (expected to succeed)\n' "$label"
    printf '%s' "$sql" | q -q 2>&1 | tail -3; exit 1
  fi
}
no_sql() {   # no_sql "label" <<SQL ... SQL
  local label="$1"; local sql; sql="$(cat)"
  if printf '%s' "$sql" | q -q >/dev/null 2>&1; then
    printf '   %-52s FAILED (expected rejection)\n' "$label"; exit 1
  else pass "$label"; fi
}

echo "== seed =="
q -q <<'SQL'
INSERT INTO tenants (id, slug, name) VALUES ('11111111-0000-0000-0000-000000000001', 'acme', 'Acme');
INSERT INTO companies (id, tenant_id, name_en)
     VALUES ('c0000000-0000-0000-0000-000000000001', '11111111-0000-0000-0000-000000000001', 'Acme KSA');
INSERT INTO employees (id, tenant_id, employee_number, name_en) VALUES
 ('e0000000-0000-0000-0000-000000000001', '11111111-0000-0000-0000-000000000001', 'E-001', 'Sara'),
 ('e0000000-0000-0000-0000-000000000002', '11111111-0000-0000-0000-000000000001', 'E-002', 'Omar');
INSERT INTO pay_components (id, tenant_id, code, kind, name_en) VALUES
 (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', 'BASIC', 'Earning',   'Basic'),
 (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', 'GOSIE', 'Deduction', 'GOSI employee');
SQL
echo "   tenant, company, 2 employees, 2 pay components       ok"

echo
echo "== (a) trg_row_stamp =="
ok_sql "created_at is forced to now(), not to what was supplied" <<'SQL'
INSERT INTO shifts (id, tenant_id, code, name, start_time, end_time, created_at)
VALUES ('5f000000-0000-0000-0000-000000000001', '11111111-0000-0000-0000-000000000001',
        'DAY', 'Day', '08:00', '17:00', '1999-01-01');
DO $$ BEGIN
  IF (SELECT created_at FROM shifts WHERE id='5f000000-0000-0000-0000-000000000001') < now() - interval '1 min'
  THEN RAISE EXCEPTION 'created_at was not stamped'; END IF;
  IF (SELECT updated_at FROM shifts WHERE id='5f000000-0000-0000-0000-000000000001') IS NOT NULL
  THEN RAISE EXCEPTION 'updated_at was set on INSERT'; END IF;
END $$;
SQL
ok_sql "UPDATE stamps updated_at and cannot rewrite created_at" <<'SQL'
SELECT set_config('app.user_id', '99999999-0000-0000-0000-000000000009', false);
UPDATE shifts SET name = 'Day shift', created_at = '1999-01-01'
 WHERE id = '5f000000-0000-0000-0000-000000000001';
DO $$ DECLARE r record; BEGIN
  SELECT * INTO r FROM shifts WHERE id='5f000000-0000-0000-0000-000000000001';
  IF r.created_at < now() - interval '1 min' THEN RAISE EXCEPTION 'created_at was rewritten'; END IF;
  IF r.updated_at IS NULL THEN RAISE EXCEPTION 'updated_at was not stamped'; END IF;
  IF r.updated_by <> '99999999-0000-0000-0000-000000000009' THEN RAISE EXCEPTION 'updated_by ignored the GUC'; END IF;
END $$;
SQL
ok_sql "the exempt list is exactly the §1 list" <<'SQL'
DO $$
DECLARE got text; want text;
BEGIN
  SELECT string_agg(c.relname, ',' ORDER BY c.relname) INTO got
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
   WHERE n.nspname = 'public' AND c.relkind IN ('r','p') AND NOT c.relispartition
     AND NOT EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid = c.oid AND t.tgname = 'trg_row_stamp');
  want := 'attendance_punches,audit_logs,background_job_items,final_settlement_lines,gl_journal_lines,'
       || 'leave_ledger,nitaqat_grid,nitaqat_snapshots,payroll_audit_logs,payroll_slip_lines,permissions,'
       || 'retention_purge_audits,statutory_rule_bands,statutory_rules,wps_lines';
  IF got <> want THEN RAISE EXCEPTION 'exempt list drifted: %', got; END IF;
END $$;
SQL

echo
echo "== (b) trg_payroll_run_transition (§10.1) =="
q -q <<'SQL'
INSERT INTO payroll_runs (id, tenant_id, company_id, run_type, year, month, selected_employee_count)
VALUES ('4a000000-0000-0000-0000-000000000001', '11111111-0000-0000-0000-000000000001',
        'c0000000-0000-0000-0000-000000000001', 'Regular', 2026, 1, 1);
SQL
no_sql "a run cannot jump Draft -> Processed" <<'SQL'
UPDATE payroll_runs SET status='Processed' WHERE id='4a000000-0000-0000-0000-000000000001';
SQL
no_sql "a run cannot jump Draft -> Approved" <<'SQL'
UPDATE payroll_runs SET status='Approved' WHERE id='4a000000-0000-0000-0000-000000000001';
SQL
no_sql "run_type is immutable" <<'SQL'
UPDATE payroll_runs SET run_type='Correction' WHERE id='4a000000-0000-0000-0000-000000000001';
SQL
ok_sql "Draft -> Processing" <<'SQL'
UPDATE payroll_runs SET status='Processing' WHERE id='4a000000-0000-0000-0000-000000000001';
SQL

# One transaction, because trg_slip_totals is DEFERRABLE INITIALLY DEFERRED and
# psql without an explicit BEGIN commits each statement on its own: a slip written
# in its own transaction is a slip whose totals do not match its (absent) lines.
q -q <<'SQL'
BEGIN;
INSERT INTO payroll_slips (id, tenant_id, run_id, employee_id, inclusion_status, gross, deductions, net)
VALUES ('51000000-0000-0000-0000-000000000001', '11111111-0000-0000-0000-000000000001',
        '4a000000-0000-0000-0000-000000000001', 'e0000000-0000-0000-0000-000000000001',
        'Included', 1000, 100, 900);
INSERT INTO payroll_slip_lines (id, tenant_id, slip_id, pay_component_code, kind, amount) VALUES
 (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', '51000000-0000-0000-0000-000000000001', 'BASIC', 'Earning',   1000),
 (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', '51000000-0000-0000-0000-000000000001', 'GOSIE', 'Deduction',  100);
COMMIT;
SQL
ok_sql "run totals are NOT enforced while Processing" <<'SQL'
UPDATE payroll_runs SET total_gross = 999999, total_net = -5, employee_count = 42
 WHERE id = '4a000000-0000-0000-0000-000000000001';
SQL
no_sql "Processing -> Processed with wrong totals is refused" <<'SQL'
UPDATE payroll_runs SET status='Processed' WHERE id='4a000000-0000-0000-0000-000000000001';
SQL
no_sql "Processing -> Processed with a slip short of the selection" <<'SQL'
UPDATE payroll_runs
   SET total_gross=1000, total_deductions=100, total_net=900, total_employer_statutory=0,
       employee_count=1, selected_employee_count=2, status='Processed'
 WHERE id='4a000000-0000-0000-0000-000000000001';
SQL
ok_sql "Processing -> Processed once the totals reconcile" <<'SQL'
UPDATE payroll_runs
   SET total_gross=1000, total_deductions=100, total_net=900, total_employer_statutory=0,
       employee_count=1, status='Processed'
 WHERE id='4a000000-0000-0000-0000-000000000001';
SQL
ok_sql "Processed -> Approved sets attendance_locked_range" <<'SQL'
UPDATE payroll_runs SET status='Approved' WHERE id='4a000000-0000-0000-0000-000000000001';
DO $$ BEGIN
  IF (SELECT attendance_locked_range FROM payroll_runs WHERE id='4a000000-0000-0000-0000-000000000001') IS NULL
  THEN RAISE EXCEPTION 'attendance_locked_range was not set on Approved'; END IF;
END $$;
SQL

echo
echo "== (c) slips are immutable from Approved (§10.1, §1 frozen rows) =="
no_sql "UPDATE payroll_slips under an Approved run" <<'SQL'
UPDATE payroll_slips SET gross = 2000 WHERE id='51000000-0000-0000-0000-000000000001';
SQL
no_sql "INSERT payroll_slip_lines under an Approved run" <<'SQL'
INSERT INTO payroll_slip_lines (id, tenant_id, slip_id, pay_component_code, kind, amount)
VALUES (gen_random_uuid(), '11111111-0000-0000-0000-000000000001',
        '51000000-0000-0000-0000-000000000001', 'BASIC', 'Earning', 1);
SQL
no_sql "DELETE payroll_slip_lines under an Approved run" <<'SQL'
DELETE FROM payroll_slip_lines WHERE slip_id='51000000-0000-0000-0000-000000000001';
SQL

echo
echo "== (d) the CASCADE parent guard (§8.1) =="
no_sql "an Approved run with frozen children cannot be deleted" <<'SQL'
DELETE FROM payroll_runs WHERE id='4a000000-0000-0000-0000-000000000001';
SQL
ok_sql "a Draft run and its slips and lines cascade away" <<'SQL'
INSERT INTO payroll_runs (id, tenant_id, company_id, run_type, year, month)
VALUES ('4a000000-0000-0000-0000-0000000000d1', '11111111-0000-0000-0000-000000000001',
        'c0000000-0000-0000-0000-000000000001', 'OffCycle', 2026, 2);
INSERT INTO payroll_slips (id, tenant_id, run_id, employee_id)
VALUES ('51000000-0000-0000-0000-0000000000d1', '11111111-0000-0000-0000-000000000001',
        '4a000000-0000-0000-0000-0000000000d1', 'e0000000-0000-0000-0000-000000000002');
DELETE FROM payroll_runs WHERE id='4a000000-0000-0000-0000-0000000000d1';
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM payroll_slips WHERE id='51000000-0000-0000-0000-0000000000d1')
  THEN RAISE EXCEPTION 'the slip did not cascade'; END IF;
END $$;
SQL
no_sql "a Submitted WPS batch cannot be deleted" <<'SQL'
INSERT INTO wps_batches (id, tenant_id, company_id, run_id, batch_number, format_version, status)
VALUES ('b0000000-0000-0000-0000-000000000001', '11111111-0000-0000-0000-000000000001',
        'c0000000-0000-0000-0000-000000000001', '4a000000-0000-0000-0000-000000000001',
        'WPS-1', 'SARIE-1', 'Submitted');
DELETE FROM wps_batches WHERE id='b0000000-0000-0000-0000-000000000001';
SQL
no_sql "an Active loan cannot be deleted" <<'SQL'
INSERT INTO loans (id, tenant_id, employee_id, kind, status, start_year, start_month,
                   installment_count, principal, outstanding)
VALUES ('10a00000-0000-0000-0000-000000000001', '11111111-0000-0000-0000-000000000001',
        'e0000000-0000-0000-0000-000000000001', 'Loan', 'Active', 2026, 1, 2, 1000, 1000);
DELETE FROM loans WHERE id='10a00000-0000-0000-0000-000000000001';
SQL

echo
echo "== (e) the Opening run (§10.1) =="
q -q <<'SQL'
INSERT INTO payroll_runs (id, tenant_id, company_id, run_type, year, month)
VALUES ('40000000-0000-0000-0000-00000000000f', '11111111-0000-0000-0000-000000000001',
        'c0000000-0000-0000-0000-000000000001', 'Opening', 2025, 12);
SQL
no_sql "an Opening run cannot be Processed" <<'SQL'
UPDATE payroll_runs SET status='Processing' WHERE id='40000000-0000-0000-0000-00000000000f';
SQL
ok_sql "an Opening run may go Draft -> Locked" <<'SQL'
UPDATE payroll_runs SET status='Locked' WHERE id='40000000-0000-0000-0000-00000000000f';
SQL
no_sql "an Opening run cannot be Paid" <<'SQL'
UPDATE payroll_runs SET status='Paid' WHERE id='40000000-0000-0000-0000-00000000000f';
SQL

echo
echo "== (f) the §11.2 deferred totals =="
ok_sql "a total may be written BEFORE its lines inside one transaction" <<'SQL'
BEGIN;
INSERT INTO timesheets (id, tenant_id, company_id, employee_id, period_start, period_end, total_minutes)
VALUES ('75000000-0000-0000-0000-000000000001', '11111111-0000-0000-0000-000000000001',
        'c0000000-0000-0000-0000-000000000001', 'e0000000-0000-0000-0000-000000000001',
        '2026-01-01', '2026-01-31', 960);
INSERT INTO timesheet_entries (id, tenant_id, timesheet_id, work_date, minutes) VALUES
 (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', '75000000-0000-0000-0000-000000000001', '2026-01-05', 480),
 (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', '75000000-0000-0000-0000-000000000001', '2026-01-06', 480);
COMMIT;
SQL
no_sql "timesheets.total_minutes must equal its entries" <<'SQL'
UPDATE timesheets SET total_minutes = 999 WHERE id='75000000-0000-0000-0000-000000000001';
SQL
no_sql "the entries-side trigger fires on the PARTITIONED child too" <<'SQL'
DELETE FROM timesheet_entries WHERE timesheet_id='75000000-0000-0000-0000-000000000001'
   AND work_date='2026-01-06';
SQL
no_sql "loans.outstanding must equal principal - recovered" <<'SQL'
BEGIN;
INSERT INTO loan_installments (id, tenant_id, loan_id, installment_number, kind, status,
                               due_year, due_month, amount)
VALUES (gen_random_uuid(), '11111111-0000-0000-0000-000000000001',
        '10a00000-0000-0000-0000-000000000001', 1, 'Scheduled', 'Recovered', 2026, 1, 500);
COMMIT;
SQL
ok_sql "loans.outstanding follows the recovery in the same transaction" <<'SQL'
BEGIN;
INSERT INTO loan_installments (id, tenant_id, loan_id, installment_number, kind, status,
                               due_year, due_month, amount)
VALUES (gen_random_uuid(), '11111111-0000-0000-0000-000000000001',
        '10a00000-0000-0000-0000-000000000001', 1, 'Scheduled', 'Recovered', 2026, 1, 500);
UPDATE loans SET outstanding = 500 WHERE id='10a00000-0000-0000-0000-000000000001';
COMMIT;
SQL
no_sql "a GL journal must balance" <<'SQL'
BEGIN;
INSERT INTO gl_journals (id, tenant_id, company_id, source_type, year, month, status)
VALUES ('91000000-0000-0000-0000-000000000001', '11111111-0000-0000-0000-000000000001',
        'c0000000-0000-0000-0000-000000000001', 'PayrollRun', 2026, 1, 'Exported');
INSERT INTO gl_journal_lines (id, tenant_id, journal_id, line_order, account, debit, credit) VALUES
 (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', '91000000-0000-0000-0000-000000000001', 1, '5000', 1000, 0),
 (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', '91000000-0000-0000-0000-000000000001', 2, '2100', 0, 900);
COMMIT;
SQL
ok_sql "a balanced GL journal commits" <<'SQL'
BEGIN;
INSERT INTO gl_journals (id, tenant_id, company_id, source_type, year, month, status)
VALUES ('91000000-0000-0000-0000-000000000002', '11111111-0000-0000-0000-000000000001',
        'c0000000-0000-0000-0000-000000000001', 'PayrollRun', 2026, 2, 'Exported');
INSERT INTO gl_journal_lines (id, tenant_id, journal_id, line_order, account, debit, credit) VALUES
 (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', '91000000-0000-0000-0000-000000000002', 1, '5000', 1000, 0),
 (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', '91000000-0000-0000-0000-000000000002', 2, '2100', 0, 1000);
COMMIT;
SQL
no_sql "final_settlements totals must equal the signed line sum" <<'SQL'
BEGIN;
INSERT INTO final_settlements (id, tenant_id, company_id, employee_id, separation_type,
                               last_working_day, gross, deductions, net)
VALUES ('f5000000-0000-0000-0000-000000000001', '11111111-0000-0000-0000-000000000001',
        'c0000000-0000-0000-0000-000000000001', 'e0000000-0000-0000-0000-000000000002',
        'Resignation', '2026-01-31', 5000, 0, 5000);
INSERT INTO final_settlement_lines (id, tenant_id, settlement_id, kind, amount) VALUES
 (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', 'f5000000-0000-0000-0000-000000000001', 'EOS', 5000),
 (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', 'f5000000-0000-0000-0000-000000000001', 'LoanRecovery', 500);
COMMIT;
SQL
no_sql "wps_batches totals must equal its lines" <<'SQL'
BEGIN;
INSERT INTO wps_lines (id, tenant_id, batch_id, slip_id, employee_id, employee_number, iban, net)
VALUES (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001',
        '51000000-0000-0000-0000-000000000001', 'e0000000-0000-0000-0000-000000000001', 'E-001', 'SA03', 900);
COMMIT;
SQL

echo
echo "== (g) audit_logs: exactly one permitted UPDATE shape (§12.3) =="
q -q <<'SQL'
INSERT INTO audit_logs (id, tenant_id, record_kind, category, chain_key, seq, action, entity,
                        envelope_hash, before, after, personal_data, personal_data_hash, before_hash, after_hash)
VALUES ('a0d17000-0000-0000-0000-000000000001', '11111111-0000-0000-0000-000000000001', 'Event', 'Leave',
        'chain-1', 1, 'Approve', 'leave_requests', 'env-h', '{"a":1}', '{"a":2}',
        '{"ip":"1.2.3.4"}', 'p-h', 'b-h', 'a-h');
SQL
no_sql "an ordinary column edit is refused" <<'SQL'
UPDATE audit_logs SET action='Reject' WHERE id='a0d17000-0000-0000-0000-000000000001';
SQL
no_sql "nulling the payload WITHOUT stamping is refused" <<'SQL'
UPDATE audit_logs SET personal_data=NULL, before=NULL, after=NULL
 WHERE id='a0d17000-0000-0000-0000-000000000001';
SQL
no_sql "stamping WITHOUT nulling the payload is refused" <<'SQL'
UPDATE audit_logs SET personal_data_erased_at=now() WHERE id='a0d17000-0000-0000-0000-000000000001';
SQL
no_sql "the erasure shape plus one extra column change is refused" <<'SQL'
UPDATE audit_logs SET personal_data=NULL, before=NULL, after=NULL,
       personal_data_erased_at=now(), envelope_hash='forged'
 WHERE id='a0d17000-0000-0000-0000-000000000001';
SQL
no_sql "DELETE is refused" <<'SQL'
DELETE FROM audit_logs WHERE id='a0d17000-0000-0000-0000-000000000001';
SQL
ok_sql "the §12.3 erasure succeeds and keeps every digest" <<'SQL'
UPDATE audit_logs SET personal_data=NULL, before=NULL, after=NULL, personal_data_erased_at=now()
 WHERE id='a0d17000-0000-0000-0000-000000000001';
DO $$ DECLARE r record; BEGIN
  SELECT * INTO r FROM audit_logs WHERE id='a0d17000-0000-0000-0000-000000000001';
  IF r.envelope_hash IS NULL OR r.personal_data_hash IS NULL
     OR r.before_hash IS NULL OR r.after_hash IS NULL
  THEN RAISE EXCEPTION 'erasure destroyed a digest the verifier needs'; END IF;
END $$;
SQL
no_sql "a second erasure of the same row is refused" <<'SQL'
UPDATE audit_logs SET personal_data=NULL, before=NULL, after=NULL, personal_data_erased_at=now()
 WHERE id='a0d17000-0000-0000-0000-000000000001';
SQL
no_sql "leave_ledger is append-only" <<'SQL'
INSERT INTO leave_types (id, tenant_id, code, name_en)
VALUES ('17000000-0000-0000-0000-000000000001','11111111-0000-0000-0000-000000000001','ANNUAL','Annual');
INSERT INTO leave_ledger (id, tenant_id, employee_id, leave_type_id, entry_type, entry_date, days)
VALUES ('1e000000-0000-0000-0000-000000000001','11111111-0000-0000-0000-000000000001',
        'e0000000-0000-0000-0000-000000000001','17000000-0000-0000-0000-000000000001','Accrual','2026-01-01',2.5);
UPDATE leave_ledger SET days = 99 WHERE id='1e000000-0000-0000-0000-000000000001';
SQL

echo
echo "== (h) 022: the six cross-half foreign keys =="
ok_sql "all six §8.2 rows exist, composite, ON UPDATE RESTRICT" <<'SQL'
DO $$
DECLARE n int;
BEGIN
  SELECT count(*) INTO n FROM pg_constraint
   WHERE contype='f' AND confupdtype='r' AND cardinality(conkey)=2 AND conname IN (
     'fk_employee_assignments__approval_request_id','fk_employee_salaries__approval_request_id',
     'fk_employee_documents__leave_request_id','fk_payroll_runs__source_import_job_id',
     'fk_payroll_runs__approval_request_id','fk_payroll_slip_lines__loan_installment_id');
  IF n <> 6 THEN RAISE EXCEPTION 'expected 6 composite cross-half FKs, found %', n; END IF;
END $$;
SQL
no_sql "row 57 refuses a platform job (tenant_id NULL) as a run's source" <<'SQL'
INSERT INTO background_jobs (id, kind, status) VALUES ('7b000000-0000-0000-0000-000000000001','import','Queued');
UPDATE payroll_runs SET source_import_job_id='7b000000-0000-0000-0000-000000000001'
 WHERE id='4a000000-0000-0000-0000-000000000001';
SQL
ok_sql "row 57 accepts the run's own tenant's job, and SET NULLs on delete" <<'SQL'
INSERT INTO background_jobs (id, tenant_id, kind, status)
VALUES ('7b000000-0000-0000-0000-000000000002', '11111111-0000-0000-0000-000000000001', 'import', 'Queued');
UPDATE payroll_runs SET source_import_job_id='7b000000-0000-0000-0000-000000000002'
 WHERE id='4a000000-0000-0000-0000-000000000001';
DELETE FROM background_jobs WHERE id='7b000000-0000-0000-0000-000000000002';
DO $$ BEGIN
  IF (SELECT source_import_job_id FROM payroll_runs WHERE id='4a000000-0000-0000-0000-000000000001') IS NOT NULL
  THEN RAISE EXCEPTION 'ON DELETE SET NULL (source_import_job_id) did not fire'; END IF;
END $$;
SQL

echo
echo "== (i) 040: FK coverage matches the §19.4 exclusion list =="
ok_sql "exactly 13 FKs are uncovered, and they are the named ones" <<'SQL'
DO $$
DECLARE got text; want text;
BEGIN
  WITH fk AS (
    SELECT c.oid, cl.relname child, c.conkey, c.conrelid,
      (SELECT string_agg(a.attname,',' ORDER BY x.ord)
         FROM unnest(c.conkey) WITH ORDINALITY x(att,ord)
         JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=x.att) cols
      FROM pg_constraint c JOIN pg_class cl ON cl.oid=c.conrelid WHERE c.contype='f'
  ), cov AS (
    SELECT fk.oid FROM fk JOIN pg_index i ON i.indrelid=fk.conrelid
     WHERE (SELECT array_agg(k ORDER BY k) FROM unnest((i.indkey::int2[])[0:array_length(fk.conkey,1)-1]) k)
         = (SELECT array_agg(k ORDER BY k) FROM unnest(fk.conkey) k))
  SELECT string_agg(child||'.'||cols, E'\n' ORDER BY child||'.'||cols) INTO got
    FROM fk WHERE oid NOT IN (SELECT oid FROM cov);
  want := 'cost_centers.tenant_id,parent_id
departments.tenant_id,parent_id
employee_documents.tenant_id,template_id
gl_period_closes.tenant_id,closed_by
gl_period_closes.tenant_id,reopened_by
gosi_filings.tenant_id,filed_by
overtime_requests.statutory_rule_id
payroll_issues.tenant_id,override_by
payroll_slip_lines.statutory_rule_band_id
payroll_slip_lines.statutory_rule_id
payroll_slips.tenant_id,template_id
permission_grantor_records.tenant_id,granted_by_user_id
role_permissions.permission_code';
  IF got <> want THEN RAISE EXCEPTION 'FK coverage drifted. Uncovered now:%s%', E'\n', got; END IF;
END $$;
SQL
ok_sql "every index in 040 carries a COMMENT naming its query" <<'SQL'
DO $$
DECLARE missing text;
BEGIN
  SELECT string_agg(c.relname, ', ') INTO missing
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
   WHERE n.nspname='public' AND c.relkind='i' AND c.relname LIKE 'ix\_%'
     AND NOT c.relispartition
     AND obj_description(c.oid, 'pg_class') IS NULL;
  IF missing IS NOT NULL THEN RAISE EXCEPTION 'indexes without a COMMENT: %', missing; END IF;
END $$;
SQL

echo
echo "PASS"
