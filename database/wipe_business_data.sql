-- ─────────────────────────────────────────────────────────────────────────────
-- wipe_business_data.sql — FULL clean-slate wipe of ALL business/tenant data
-- ─────────────────────────────────────────────────────────────────────────────
-- Outcome (per owner decision 2026-07-26):
--   • Schema untouched (TRUNCATE, not DROP)
--   • ALL tenant/business data across the ENTIRE database is removed
--   • Reference config (country/GOSI/statutory rules, permission catalog,
--     pricing config) is re-seeded automatically by the always-on seeders on
--     the next backend boot
--   • ONE clean bootstrap tenant + admin is re-created on next boot from env
--     (SeedAdmin__TenantName / __TenantSlug / __Email / __Password)
--
-- Unlike neon_truncate.sql (a static table list that goes stale as the schema
-- grows), this script discovers tables dynamically — it truncates EVERY table
-- in `public` EXCEPT the preserve list below, so new tables are always covered.
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
-- RUN ORDER:
--   1. Neon Dashboard → Branches → create a branch snapshot (instant rollback)
--   2. Run this script in the Neon SQL editor against the production branch
--   3. Render → Manual Deploy → "Deploy latest commit" (or Restart) so the
--      boot seeders re-create reference config + the bootstrap tenant/admin
--   4. Log in with the SeedAdmin env credentials and verify a clean slate
-- ─────────────────────────────────────────────────────────────────────────────

BEGIN;

DO $$
DECLARE
  t record;
  preserved text[] := ARRAY['__EFMigrationsHistory'];
BEGIN
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
