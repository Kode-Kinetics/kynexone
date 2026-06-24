-- ─────────────────────────────────────────────────────────────────────────────
-- intelliflow_neon_ops.sql
-- IntelliFlow fragment cleanup + integrity verification for Neon PostgreSQL
--
-- RUN ORDER (paste each section into the Neon SQL editor in sequence):
--   SECTION 1 — Pre-flight: FK dependency counts per fragment tenant
--   SECTION 2 — Cross-tenant FK safety check (ensure no cross-tenant references)
--   SECTION 3 — Rasalmanar guard (confirm it is NOT in the fragment set)
--   SECTION 4 — Tenant integrity snapshot (before)
--   [CONFIRM NEON BRANCH SNAPSHOT EXISTS BEFORE PROCEEDING PAST THIS POINT]
--   [THEN DEPLOY via Render one-off job — the C# seeders handle deletion + creation]
--   SECTION 5 — Tenant integrity snapshot (after — run post-deploy to verify)
-- ─────────────────────────────────────────────────────────────────────────────

-- ════════════════════════════════════════════════════════════════════════════
-- SECTION 1 — Pre-flight: FK child-row counts per fragment tenant
-- Expected: rasalmanar (15b0c4c2) does NOT appear in these results.
-- ════════════════════════════════════════════════════════════════════════════

SELECT
    t.id::text,
    LEFT(t.id::text, 8)                                                AS id_prefix,
    t.slug,
    t.is_active,
    (SELECT COUNT(*) FROM users         WHERE tenant_id = t.id)        AS users_count,
    (SELECT COUNT(*) FROM companies     WHERE tenant_id = t.id)        AS companies_count,
    (SELECT COUNT(*) FROM employees     WHERE tenant_id = t.id)        AS employees_count,
    (SELECT COUNT(*) FROM payroll_runs  WHERE tenant_id = t.id)        AS payroll_runs_count,
    (SELECT COUNT(*) FROM payroll_slips WHERE tenant_id = t.id)        AS payroll_slips_count,
    (SELECT COUNT(*) FROM payroll_deductions WHERE tenant_id = t.id)   AS payroll_deductions_count,
    (SELECT COUNT(*) FROM user_entity_accesses WHERE tenant_id = t.id) AS user_entity_accesses_count,
    (SELECT COUNT(*) FROM refresh_tokens rt
       JOIN users u ON u.id = rt.user_id WHERE u.tenant_id = t.id)    AS active_refresh_tokens
FROM tenants t
WHERE t.slug IN (
    'intelliflow-system',
    'intelliflow',
    'intelliflow__deleted_67802831',
    'intelliflow__deleted_dd7c2ff9',
    'intelliflow__deleted_004e58eb'
)
ORDER BY t.slug;

-- ════════════════════════════════════════════════════════════════════════════
-- SECTION 2 — Cross-tenant FK safety check
-- Confirms none of the 5 fragment tenant_ids is referenced by rows belonging
-- to a DIFFERENT tenant.  Expected: 0 rows for each query.
-- ════════════════════════════════════════════════════════════════════════════

-- 2a. Employees from a different tenant pointing to a fragment company
SELECT 'cross_tenant_employees_vs_companies' AS check_name, COUNT(*) AS violations
FROM employees e
JOIN companies c ON c.id = e.company_id
WHERE c.tenant_id IN (
    SELECT id FROM tenants WHERE slug IN (
        'intelliflow-system','intelliflow',
        'intelliflow__deleted_67802831','intelliflow__deleted_dd7c2ff9','intelliflow__deleted_004e58eb'
    )
)
AND e.tenant_id NOT IN (
    SELECT id FROM tenants WHERE slug IN (
        'intelliflow-system','intelliflow',
        'intelliflow__deleted_67802831','intelliflow__deleted_dd7c2ff9','intelliflow__deleted_004e58eb'
    )
);

-- 2b. Payroll runs from a different tenant pointing to a fragment company
SELECT 'cross_tenant_payroll_runs_vs_companies' AS check_name, COUNT(*) AS violations
FROM payroll_runs pr
JOIN companies c ON c.id = pr.company_id
WHERE c.tenant_id IN (
    SELECT id FROM tenants WHERE slug IN (
        'intelliflow-system','intelliflow',
        'intelliflow__deleted_67802831','intelliflow__deleted_dd7c2ff9','intelliflow__deleted_004e58eb'
    )
)
AND pr.tenant_id NOT IN (
    SELECT id FROM tenants WHERE slug IN (
        'intelliflow-system','intelliflow',
        'intelliflow__deleted_67802831','intelliflow__deleted_dd7c2ff9','intelliflow__deleted_004e58eb'
    )
);

-- ════════════════════════════════════════════════════════════════════════════
-- SECTION 3 — Rasalmanar guard
-- MUST return 1 row with id_prefix='15b0c4c2' and is_active=true.
-- If this returns 0 rows or is_active=false — STOP IMMEDIATELY.
-- ════════════════════════════════════════════════════════════════════════════

SELECT
    LEFT(id::text, 8) AS id_prefix,
    slug,
    is_active,
    (SELECT COUNT(*) FROM employees WHERE tenant_id = t.id) AS employees,
    (SELECT COUNT(*) FROM users     WHERE tenant_id = t.id) AS users,
    (SELECT COUNT(*) FROM companies WHERE tenant_id = t.id) AS companies,
    (SELECT COUNT(*) FROM payroll_runs WHERE tenant_id = t.id AND status = 'Locked') AS locked_runs
FROM tenants t
WHERE LEFT(id::text, 8) = '15b0c4c2';
-- Expected: id_prefix=15b0c4c2, slug=rasalmanar, is_active=true,
--           employees=15, users≥7, companies=1, locked_runs=1

-- ════════════════════════════════════════════════════════════════════════════
-- SECTION 4 — Full tenant integrity snapshot (run BEFORE the fix)
-- ════════════════════════════════════════════════════════════════════════════

SELECT
    LEFT(t.id::text, 8)                                                  AS id_prefix,
    t.slug,
    t.is_active,
    (SELECT COUNT(*) FROM users u    WHERE u.tenant_id = t.id
                                       AND u.is_active = true
                                       AND u.is_deleted = false)         AS active_users,
    (SELECT COUNT(*) FROM users u    WHERE u.tenant_id = t.id
                                       AND u.is_group_scope = true
                                       AND u.is_deleted = false)         AS users_group_scope,
    (SELECT COUNT(*) FROM companies  WHERE tenant_id = t.id
                                       AND is_deleted = false)           AS companies,
    (SELECT COUNT(*) FROM employees  WHERE tenant_id = t.id
                                       AND is_deleted = false)           AS employees,
    (SELECT COUNT(*) FROM payroll_runs WHERE tenant_id = t.id)          AS payroll_runs,
    (SELECT COUNT(*) FROM payroll_runs WHERE tenant_id = t.id
                                         AND status = 'Locked')          AS locked_runs
FROM tenants t
WHERE t.slug NOT LIKE '%__deleted_%'
   OR t.slug IN ('intelliflow-system','intelliflow')
ORDER BY t.slug;

-- ════════════════════════════════════════════════════════════════════════════
-- NOTE: After running Sections 1–4 and confirming the snapshot exists,
-- trigger the fix by running:
--
--   dotnet Zayra.Api.dll --migrate
--
-- on Render (one-off job with the production CONNECTION STRING).
-- The startup seeders will:
--   1. IntelliFlowFragmentCleanup  → soft-deletes the 5 fragments
--   2. IntelliFlowDemoSeeder       → creates one clean 'intelliflow' tenant
-- ════════════════════════════════════════════════════════════════════════════

-- ════════════════════════════════════════════════════════════════════════════
-- SECTION 5 — Post-fix verification (run AFTER the --migrate job completes)
-- ════════════════════════════════════════════════════════════════════════════

-- 5a. IntelliFlow tenant integrity check
-- Expected: exactly 1 row with users_missing_group_scope=0,
--           employees>0, companies=1, is_active=true
SELECT
    LEFT(t.id::text, 8)                                               AS id_prefix,
    t.slug,
    t.is_active,
    (SELECT COUNT(*) FROM users u    WHERE u.tenant_id = t.id
                                       AND u.is_deleted = false)      AS users_total,
    (SELECT COUNT(*) FROM users u    WHERE u.tenant_id = t.id
                                       AND u.is_group_scope = true
                                       AND u.is_deleted = false)      AS users_group_scope,
    (SELECT COUNT(*) FROM users u    WHERE u.tenant_id = t.id
                                       AND u.is_group_scope = false
                                       AND u.is_active = true
                                       AND u.is_deleted = false
                                       AND u.access_mode = 'FullPortal'
                                       AND NOT EXISTS (
                                             SELECT 1 FROM user_entity_accesses uea
                                             WHERE uea.user_id = u.id AND uea.is_active = true
                                           ))                         AS users_missing_group_scope,
    (SELECT COUNT(*) FROM companies  WHERE tenant_id = t.id
                                       AND is_deleted = false)        AS companies,
    (SELECT COUNT(*) FROM employees  WHERE tenant_id = t.id
                                       AND is_deleted = false)        AS employees,
    (SELECT COUNT(*) FROM payroll_runs WHERE tenant_id = t.id
                                         AND status = 'Locked')       AS locked_runs
FROM tenants t
WHERE t.slug = 'intelliflow' AND t.is_active = true;

-- 5b. Confirm the 5 old fragments are all inactive (soft-deleted)
SELECT
    LEFT(id::text, 8) AS id_prefix,
    slug,
    is_active
FROM tenants
WHERE slug LIKE '%intelliflow%'
ORDER BY slug;
-- Expected: 'intelliflow' → is_active=true
--           all 'intelliflow*__deleted_*' rows → is_active=false
--           'intelliflow-system__deleted_*' → is_active=false

-- 5c. Rasalmanar untouched confirmation
SELECT
    LEFT(id::text, 8) AS id_prefix,
    slug,
    is_active,
    (SELECT COUNT(*) FROM employees WHERE tenant_id = t.id) AS employees,
    (SELECT COUNT(*) FROM payroll_runs WHERE tenant_id = t.id AND status = 'Locked') AS locked_runs
FROM tenants t
WHERE LEFT(id::text, 8) = '15b0c4c2';
-- Expected: slug=rasalmanar, is_active=true, employees=15, locked_runs=1
