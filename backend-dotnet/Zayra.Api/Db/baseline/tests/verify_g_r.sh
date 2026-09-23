#!/usr/bin/env bash
# Verifies the domain G-R baseline DDL (016, 017, 018, 021) against a throwaway
# PostgreSQL 16 container. Nothing here touches a real database.
#
#   ./backend-dotnet/Zayra.Api/Db/baseline/tests/verify_g_r.sh
#
# Until 010-015 land, the domain A-F tables are supplied by tests/stub_a_f.sql,
# which carries only the keys the G-R foreign keys target. Once the real files
# exist, replace the stub in FILES below with them and the same script proves
# the two halves fit.
set -euo pipefail

CONTAINER=${CONTAINER:-kynex-schema-verify}
IMAGE=${IMAGE:-postgres:16}
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE="$(dirname "$HERE")"

FILES=(
  "$HERE/stub_a_f.sql"
  "$BASE/016_wps_gl.sql"
  "$BASE/017_leave_attendance.sql"
  "$BASE/018_workflow_audit.sql"
  "$BASE/021_constraints_g_r.sql"
)

cleanup() { docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT

cleanup
docker run -d --name "$CONTAINER" -e POSTGRES_PASSWORD=verify -e POSTGRES_DB=kynex_baseline "$IMAGE" >/dev/null
for _ in $(seq 1 60); do
  docker exec "$CONTAINER" pg_isready -U postgres -d kynex_baseline >/dev/null 2>&1 && break
  sleep 1
done

psql() { docker exec -i "$CONTAINER" psql -U postgres -d kynex_baseline "$@"; }

echo "== applying =="
for f in "${FILES[@]}"; do
  printf '   %-28s' "$(basename "$f")"
  psql -v ON_ERROR_STOP=1 -q < "$f"
  echo "ok"
done

echo "== assertions =="
psql -v ON_ERROR_STOP=1 -qtA <<'SQL'
\set stubs '''tenants'',''users'',''companies'',''employees'',''files'',''branches'',''cost_centers'',''payroll_runs'',''payroll_slips'',''statutory_rules'''

-- 38 tables in scope
DO $$
DECLARE n int;
BEGIN
  SELECT count(*) INTO n FROM pg_class c JOIN pg_namespace s ON s.oid=c.relnamespace
   WHERE s.nspname='public' AND c.relkind='r'
     AND c.relname NOT IN ('tenants','users','companies','employees','files','branches',
                           'cost_centers','payroll_runs','payroll_slips','statutory_rules');
  IF n <> 38 THEN RAISE EXCEPTION 'expected 38 tables in domains G-R, found %', n; END IF;
  RAISE NOTICE '   38 tables                     ok';
END $$;

-- every table carries COMMENT ON TABLE (the CI comment-coverage gate)
DO $$
DECLARE missing text;
BEGIN
  SELECT string_agg(c.relname, ', ') INTO missing
    FROM pg_class c JOIN pg_namespace s ON s.oid=c.relnamespace
   WHERE s.nspname='public' AND c.relkind='r'
     AND obj_description(c.oid,'pg_class') IS NULL
     AND c.relname NOT IN ('tenants','users','companies','employees','files','branches',
                           'cost_centers','payroll_runs','payroll_slips','statutory_rules');
  IF missing IS NOT NULL THEN RAISE EXCEPTION 'tables without a COMMENT: %', missing; END IF;
  RAISE NOTICE '   comment coverage              ok';
END $$;

-- ON UPDATE is RESTRICT on every foreign key, without exception (§8.1)
DO $$
DECLARE bad text;
BEGIN
  SELECT string_agg(conname, ', ') INTO bad FROM pg_constraint
   WHERE contype='f' AND conname LIKE 'fk%' AND confupdtype <> 'r';
  IF bad IS NOT NULL THEN RAISE EXCEPTION 'ON UPDATE is not RESTRICT on: %', bad; END IF;
  RAISE NOTICE '   ON UPDATE RESTRICT everywhere ok';
END $$;

-- the row-stamping exempt list (§1, KEEPING_DOCS_HONEST assertion 9)
DO $$
DECLARE bad text;
BEGIN
  WITH mine AS (
    SELECT c.relname FROM pg_class c JOIN pg_namespace s ON s.oid=c.relnamespace
     WHERE s.nspname='public' AND c.relkind='r'
       AND c.relname NOT IN ('tenants','users','companies','employees','files','branches',
                             'cost_centers','payroll_runs','payroll_slips','statutory_rules')),
  exempt(relname) AS (VALUES ('leave_ledger'),('audit_logs'),('payroll_audit_logs'),
    ('retention_purge_audits'),('attendance_punches'),('wps_lines'),('final_settlement_lines'),
    ('gl_journal_lines'),('nitaqat_snapshots'),('background_job_items'),('nitaqat_grid')),
  cols AS (SELECT table_name, array_agg(column_name::text) c
             FROM information_schema.columns WHERE table_schema='public' GROUP BY 1)
  SELECT string_agg(m.relname, ', ') INTO bad
    FROM mine m LEFT JOIN exempt e ON e.relname=m.relname JOIN cols x ON x.table_name=m.relname
   WHERE NOT ('created_at' = ANY(x.c))
      OR (e.relname IS NULL     AND NOT ('updated_by' = ANY(x.c)))
      OR (e.relname IS NOT NULL AND      ('updated_by' = ANY(x.c)));
  IF bad IS NOT NULL THEN RAISE EXCEPTION 'row-stamping rule broken on: %', bad; END IF;
  RAISE NOTICE '   row stamping / exempt list    ok';
END $$;
SQL

echo "== behaviour =="
psql -v ON_ERROR_STOP=1 -qtA <<'SQL'
INSERT INTO tenants (id) VALUES ('11111111-1111-1111-1111-111111111111'),
                                ('22222222-2222-2222-2222-222222222222');
INSERT INTO employees (id,tenant_id) VALUES
 ('bbbbbbbb-0000-0000-0000-000000000001','11111111-1111-1111-1111-111111111111'),
 ('bbbbbbbb-0000-0000-0000-000000000002','22222222-2222-2222-2222-222222222222');
INSERT INTO companies (id,tenant_id,gosi_registration_no) VALUES
 ('aaaaaaaa-0000-0000-0000-000000000001','11111111-1111-1111-1111-111111111111','GOSI-A');
INSERT INTO shifts (id,tenant_id,code,name,start_time,end_time) VALUES
 ('dddddddd-0000-0000-0000-000000000001','11111111-1111-1111-1111-111111111111','DAY','Day','08:00','17:00');

-- a row may never point across tenants, even through a hand-written INSERT
DO $$
BEGIN
  INSERT INTO shift_assignments (id,tenant_id,employee_id,shift_id,effective_from)
  VALUES (gen_random_uuid(),'11111111-1111-1111-1111-111111111111',
          'bbbbbbbb-0000-0000-0000-000000000002','dddddddd-0000-0000-0000-000000000001','2026-01-01');
  RAISE EXCEPTION 'cross-tenant FK was accepted';
EXCEPTION WHEN foreign_key_violation THEN RAISE NOTICE '   cross-tenant FK rejected      ok';
END $$;

-- effective-dated no-overlap, and the inclusive-inclusive boundary
INSERT INTO shift_assignments (id,tenant_id,employee_id,shift_id,effective_from,effective_to)
VALUES ('e0000000-0000-0000-0000-000000000001','11111111-1111-1111-1111-111111111111',
        'bbbbbbbb-0000-0000-0000-000000000001','dddddddd-0000-0000-0000-000000000001','2026-01-01','2026-03-30');
DO $$
BEGIN
  INSERT INTO shift_assignments (id,tenant_id,employee_id,shift_id,effective_from)
  VALUES (gen_random_uuid(),'11111111-1111-1111-1111-111111111111',
          'bbbbbbbb-0000-0000-0000-000000000001','dddddddd-0000-0000-0000-000000000001','2026-03-30');
  RAISE EXCEPTION 'an overlapping effective-dated row was accepted';
EXCEPTION WHEN exclusion_violation THEN RAISE NOTICE '   dated overlap rejected        ok';
END $$;
INSERT INTO shift_assignments (id,tenant_id,employee_id,shift_id,effective_from)
VALUES ('e0000000-0000-0000-0000-000000000002','11111111-1111-1111-1111-111111111111',
        'bbbbbbbb-0000-0000-0000-000000000001','dddddddd-0000-0000-0000-000000000001','2026-03-31');
DO $$ BEGIN RAISE NOTICE '   30th/31st boundary            ok'; END $$;

-- Nitaqat bands: adjacent is legal, overlapping is not
INSERT INTO nitaqat_grid (id,activity_code,size_tier,band,grid_version,effective_from,
                          headcount_min,headcount_max,min_saudization_pct,max_saudization_pct) VALUES
 (gen_random_uuid(),'4711','Small','Red','NITAQAT-2025','2025-01-01',6,49,0,30),
 (gen_random_uuid(),'4711','Small','LowGreen','NITAQAT-2025','2025-01-01',6,49,30,40);
DO $$
BEGIN
  INSERT INTO nitaqat_grid (id,activity_code,size_tier,band,grid_version,effective_from,
                            headcount_min,headcount_max,min_saudization_pct,max_saudization_pct)
  VALUES (gen_random_uuid(),'4711','Small','MidGreen','NITAQAT-2025','2025-01-01',6,49,35,45);
  RAISE EXCEPTION 'an overlapping Nitaqat band was accepted';
EXCEPTION WHEN exclusion_violation THEN RAISE NOTICE '   nitaqat band overlap rejected ok';
END $$;

-- the timesheet reconciliation, with both sides in minutes
INSERT INTO timesheets (id,tenant_id,company_id,employee_id,period_start,period_end)
VALUES ('10000000-0000-0000-0000-000000000001','11111111-1111-1111-1111-111111111111',
        'aaaaaaaa-0000-0000-0000-000000000001','bbbbbbbb-0000-0000-0000-000000000001','2026-01-01','2026-01-31');
INSERT INTO attendance_days (id,tenant_id,employee_id,status,work_date,worked_minutes)
VALUES ('20000000-0000-0000-0000-000000000001','11111111-1111-1111-1111-111111111111',
        'bbbbbbbb-0000-0000-0000-000000000001','Present','2026-01-05',480);
DO $$
BEGIN
  INSERT INTO timesheet_day_reconciliations (id,tenant_id,timesheet_id,attendance_day_id,work_date,
                                             timesheet_minutes,attendance_minutes,variance_minutes)
  VALUES (gen_random_uuid(),'11111111-1111-1111-1111-111111111111','10000000-0000-0000-0000-000000000001',
          '20000000-0000-0000-0000-000000000001','2026-01-05',500,480,999);
  RAISE EXCEPTION 'a wrong variance was accepted';
EXCEPTION WHEN check_violation THEN RAISE NOTICE '   variance invariant            ok';
END $$;
INSERT INTO timesheet_day_reconciliations (id,tenant_id,timesheet_id,attendance_day_id,work_date,
                                           timesheet_minutes,attendance_minutes,variance_minutes)
VALUES (gen_random_uuid(),'11111111-1111-1111-1111-111111111111','10000000-0000-0000-0000-000000000001',
        '20000000-0000-0000-0000-000000000001','2026-01-05',500,480,20);
DO $$ BEGIN RAISE NOTICE '   FK into partitioned day       ok'; END $$;

-- PDPL erasure: null the payload, keep the digests, never touch envelope_hash
INSERT INTO audit_logs (id,tenant_id,record_kind,category,chain_key,seq,action,entity,
                        envelope_hash,before,after,personal_data,personal_data_hash,before_hash,after_hash)
VALUES ('70000000-0000-0000-0000-000000000001','11111111-1111-1111-1111-111111111111','Event','Leave',
        't1',1,'Approve','leave_requests','h1','{"a":1}','{"a":2}','{"ip":"1.2.3.4"}','ph','bh','ah');
UPDATE audit_logs SET personal_data=NULL, before=NULL, after=NULL, personal_data_erased_at=now()
 WHERE id='70000000-0000-0000-0000-000000000001';
DO $$
DECLARE r record;
BEGIN
  SELECT envelope_hash, personal_data_hash, before_hash, after_hash INTO r
    FROM audit_logs WHERE id='70000000-0000-0000-0000-000000000001';
  IF r.envelope_hash IS NULL OR r.personal_data_hash IS NULL
     OR r.before_hash IS NULL OR r.after_hash IS NULL THEN
    RAISE EXCEPTION 'erasure destroyed a digest the verifier needs';
  END IF;
  RAISE NOTICE '   audit erasure recomputable    ok';
END $$;
DO $$
BEGIN
  INSERT INTO audit_logs (id,record_kind,chain_key,seq,covers_seq_from,covers_seq_to,root_hash)
  VALUES (gen_random_uuid(),'Checkpoint','t1',2,1,1,'r1');
  RAISE EXCEPTION 'a checkpoint without a time bound was accepted';
EXCEPTION WHEN check_violation THEN RAISE NOTICE '   checkpoint shape              ok';
END $$;
SQL

echo "== partition rehearsal =="
# Every unique constraint on the five partitioned tables must already contain
# the partition key, or 030_partitions.sql cannot create the parents at all.
docker exec "$CONTAINER" psql -U postgres -qc "CREATE DATABASE parttest" >/dev/null
python3 - "$BASE" <<'PY' | docker exec -i "$CONTAINER" psql -U postgres -d parttest -v ON_ERROR_STOP=1 -q
import re, sys
base = sys.argv[1]
keys = {'attendance_punches':'occurred_at','attendance_days':'work_date',
        'timesheet_entries':'work_date','audit_logs':'created_at',
        'background_job_items':'created_at'}
src = open(base+'/017_leave_attendance.sql').read() + open(base+'/018_workflow_audit.sql').read()
for t,k in keys.items():
    m = re.search(r'CREATE TABLE %s \((.*?)\n\);' % t, src, re.S)
    assert m, t
    print("CREATE TABLE %s (%s\n) PARTITION BY RANGE (%s);" % (t, m.group(1), k))
    print("CREATE TABLE %s_2026_01 PARTITION OF %s FOR VALUES FROM ('2026-01-01') TO ('2026-02-01');" % (t,t))
    print("CREATE TABLE %s_default PARTITION OF %s DEFAULT;" % (t,t))
PY
echo "   five parents + monthly + DEFAULT children  ok"

echo
echo "PASS"
