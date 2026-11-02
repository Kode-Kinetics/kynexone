-- KynexOne restore-drill verification. READ-ONLY by construction: runs inside a READ ONLY
-- transaction and contains only SELECTs. Run by scripts/dr/restore-drill.sh against a Neon
-- point-in-time branch, never against the primary.
--
-- Output is psql unaligned (pipe separated). Every section starts with a "## " title line.
-- Tables that do not exist in the restored schema are skipped, not errors, so the same file
-- works across schema versions.

\set ON_ERROR_STOP on
\pset format unaligned
\pset fieldsep '|'
\pset footer off
BEGIN READ ONLY;

\echo '## 1. latest applied migration'
SELECT "MigrationId" AS latest_migration_id, count(*) OVER () AS applied_count
FROM "__EFMigrationsHistory"
ORDER BY "MigrationId" DESC
LIMIT 1;

\echo '## 2. row counts, key tables (soft-deleted rows included)'
SELECT t AS table_name,
       (xpath('/row/c/text()',
              query_to_xml(format('select count(*) as c from %I', t), false, true, '')))[1]::text::bigint AS row_count
FROM unnest(ARRAY['tenants','companies','users','employees','employee_documents',
                  'payroll_runs','payroll_run_items','leave_requests','employee_loans',
                  'audit_logs']) AS t
WHERE to_regclass(format('public.%I', t)) IS NOT NULL
ORDER BY t;

\echo '## 3. audit hash-chain spot check (latest 200 entries per tenant, up to 25 tenants)'
-- Each entry's previous_hash must equal the preceding entry's entry_hash for the same tenant
-- (same ordering the application uses: created_at_utc, id). A break is counted and the result
-- column reads FAIL, which the drill script turns into a non-zero exit.
WITH tenants_sample AS (
    SELECT tenant_id FROM audit_logs WHERE tenant_id IS NOT NULL
    GROUP BY tenant_id ORDER BY max(created_at_utc) DESC LIMIT 25
), recent AS (
    SELECT a.tenant_id, a.id, a.previous_hash, a.entry_hash,
           lag(a.entry_hash) OVER (PARTITION BY a.tenant_id ORDER BY a.created_at_utc, a.id) AS expected_prev,
           row_number() OVER (PARTITION BY a.tenant_id ORDER BY a.created_at_utc DESC, a.id DESC) AS rn
    FROM audit_logs a JOIN tenants_sample s ON s.tenant_id = a.tenant_id
)
SELECT count(*) AS entries_checked,
       count(*) FILTER (WHERE entry_hash IS NULL OR entry_hash = '') AS missing_entry_hash,
       count(*) FILTER (WHERE expected_prev IS NOT NULL
                          AND coalesce(previous_hash,'') <> expected_prev) AS chain_breaks,
       CASE WHEN count(*) FILTER (WHERE expected_prev IS NOT NULL
                                   AND coalesce(previous_hash,'') <> expected_prev) = 0
             AND count(*) FILTER (WHERE entry_hash IS NULL OR entry_hash = '') = 0
            THEN 'PASS' ELSE 'FAIL' END AS result
FROM recent WHERE rn <= 200;

\echo '## 4. payroll totals checksum, last closed period'
-- "Closed" = locked_at_utc is set. The checksum covers run id and the four money totals, so a
-- restore that lost or altered any run in the period changes it. Compare it to the value
-- recorded when that period was closed, or to the same query on the primary at the same instant.
WITH last_period AS (
    SELECT year, month FROM payroll_runs
    WHERE locked_at_utc IS NOT NULL
    ORDER BY year DESC, month DESC LIMIT 1
)
SELECT p.year, p.month, count(*) AS runs,
       sum(r.total_gross_salary) AS gross, sum(r.total_deductions) AS deductions,
       sum(r.total_net_salary) AS net, sum(r.total_employer_statutory_cost) AS employer_cost,
       md5(string_agg(r.id::text || ':' || r.total_gross_salary::text || ':' || r.total_deductions::text
                      || ':' || r.total_net_salary::text || ':' || r.total_employer_statutory_cost::text,
                      ',' ORDER BY r.id)) AS checksum_md5
FROM payroll_runs r JOIN last_period p ON p.year = r.year AND p.month = r.month
WHERE r.locked_at_utc IS NOT NULL
GROUP BY p.year, p.month;

\echo '## 5. newest write visible in the restore (proves the restore point)'
SELECT max(created_at_utc) AS newest_audit_entry_utc FROM audit_logs;

ROLLBACK;
