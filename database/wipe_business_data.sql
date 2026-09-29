-- ─────────────────────────────────────────────────────────────────────────────
-- wipe_business_data.sql — FULL clean-slate wipe of ALL business/tenant data
-- ─────────────────────────────────────────────────────────────────────────────
-- REFUSE-GUARD. Read this first. The script changes NOTHING unless both are true:
--
--   1. The connected database is not a production database. Production is
--      `kynexone_clean` (live since the 2026-09-23 cutover) and `neondb` (kept
--      as its rollback). A Neon branch of production keeps those names, so a
--      branch is refused too.
--   2. The session names the database it intends to wipe, exactly:
--        psql "$URL" -c "SET kynexone.wipe_confirm_database = 'zayra_dev'" \
--                    -f database/wipe_business_data.sql
--      or
--        PGOPTIONS='-c kynexone.wipe_confirm_database=zayra_dev' \
--          psql "$URL" -f database/wipe_business_data.sql
--      A confirmation that names a different database is refused, so a setting
--      left over from another target cannot carry over.
--
-- The same check runs again inside the wipe transaction. A client that carries
-- on after an error (psql without ON_ERROR_STOP, an editor that runs every
-- statement) still truncates nothing, and the COMMIT becomes a ROLLBACK.
--
-- There is no flag that permits a production wipe. Doing that needs a reviewed
-- change to this file, and that is deliberate.
--
-- Outcome (per owner decision 2026-07-26):
--   • Schema untouched (TRUNCATE, not DROP)
--   • ALL tenant/business data across the ENTIRE database is removed
--   • Reference config (country/GOSI/statutory rules, permission catalog,
--     pricing config) is re-seeded automatically by the always-on seeders on
--     the next backend boot
--   • ONE clean bootstrap tenant + admin is re-created on next boot from env
--     (SeedAdmin__TenantName / __TenantSlug / __Email / __Password)
--
-- The table list is not hard-coded. The script finds every table in `public`
-- EXCEPT the preserve list below, so tables added later are always covered.
--
-- PRESERVED (not truncated):
--   __EFMigrationsHistory  — REQUIRED: wiping it desyncs EF migrations and the
--                            next `--migrate` job would try to re-apply
--                            everything against existing tables and fail.
--   platform_users and all platform_* tables
--                          — platform-admin logins/config. Platform-owner
--                            seeding is demo-gated and REFUSED in production,
--                            so if these were wiped the platform login would
--                            NOT come back automatically. Remove them from the
--                            preserve list only if you accept that.
--
-- RUN ORDER (non-production only):
--   1. Take a snapshot or backup of the target database
--   2. Run this script with the confirmation shown above
--   3. Restart the backend that uses this database, so the boot seeders
--      re-create reference config + the bootstrap tenant/admin
--   4. Log in with the SeedAdmin env credentials and verify a clean slate
-- ─────────────────────────────────────────────────────────────────────────────

-- Fail fast, before anything else runs.
DO $guard$
DECLARE
  db         text   := current_database();
  confirmed  text   := nullif(current_setting('kynexone.wipe_confirm_database', true), '');
  production text[] := ARRAY['kynexone_clean', 'neondb'];
BEGIN
  IF db = ANY (production) THEN
    RAISE EXCEPTION 'wipe_business_data.sql refused: "%" is a production database. Nothing was changed.', db;
  END IF;
  IF confirmed IS DISTINCT FROM db THEN
    RAISE EXCEPTION 'wipe_business_data.sql refused: connected to "%", but kynexone.wipe_confirm_database is "%". Run SET kynexone.wipe_confirm_database = ''%'' in this session first. Nothing was changed.',
      db, coalesce(confirmed, '(not set)'), db;
  END IF;
END
$guard$;

BEGIN;

DO $$
DECLARE
  t record;
  preserved text[] := ARRAY['__EFMigrationsHistory'];
  db         text   := current_database();
  confirmed  text   := nullif(current_setting('kynexone.wipe_confirm_database', true), '');
  production text[] := ARRAY['kynexone_clean', 'neondb'];
BEGIN
  -- The same guard again, in the wipe's own transaction. If the block above was
  -- skipped or its error ignored, this still stops before the first TRUNCATE.
  IF db = ANY (production) OR confirmed IS DISTINCT FROM db THEN
    RAISE EXCEPTION 'wipe_business_data.sql refused inside the wipe transaction (database "%"). Nothing was changed.', db;
  END IF;

  FOR t IN
    SELECT tablename
    FROM pg_tables
    WHERE schemaname = 'public'
      AND NOT (tablename = ANY (preserved))
      AND tablename NOT LIKE 'platform\_%'
      AND tablename <> 'platform_users'
  LOOP
    EXECUTE format('TRUNCATE TABLE public.%I RESTART IDENTITY CASCADE', t.tablename);
  END LOOP;
END $$;

COMMIT;

-- ── Verification (run after COMMIT) ─────────────────────────────────────────
-- Expect 0 for every business table; platform/__EF tables keep their rows.
SELECT 'tenants'    AS tbl, count(*) FROM tenants
UNION ALL SELECT 'users',                count(*) FROM users
UNION ALL SELECT 'employees',            count(*) FROM employees
UNION ALL SELECT 'employee_salary_structures', count(*) FROM employee_salary_structures
UNION ALL SELECT 'payroll_runs',         count(*) FROM payroll_runs
UNION ALL SELECT 'approval_requests',    count(*) FROM approval_requests
UNION ALL SELECT 'companies',            count(*) FROM companies
UNION ALL SELECT 'gl_accounts',          count(*) FROM gl_accounts
UNION ALL SELECT 'audit_logs',           count(*) FROM audit_logs
UNION ALL SELECT '__EFMigrationsHistory (preserved, >0)', count(*) FROM "__EFMigrationsHistory";
