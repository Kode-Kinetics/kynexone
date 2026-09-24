-- =============================================================================
-- 060_policies.sql
-- KynexOne baseline — ENABLE + FORCE ROW LEVEL SECURITY on every table, the
-- policy shapes of §19.2, the two security_invoker views, and every table and
-- column grant.
--
-- Source of truth: TARGET_SCHEMA.md revision 6 §19.2; CONVENTIONS.md §12.
-- Applied by kynex_migrator with SET ROLE kynex_owner, after 030_partitions.
--
-- -----------------------------------------------------------------------------
-- WHAT §19.2 SAYS, AND WHERE THE 76 TABLES ACTUALLY LAND
--
-- §19.2 declares "three policy shapes, and only three" over 66 + 6 + 4 tables,
-- with platform_users "outside all three". That is 66 + 6 + 4 + 1 = 77 for a
-- 76-table design, so the arithmetic does not close, and the reason is that
-- three tables have NO tenant_id column at all and therefore nothing for shape
-- (a) to filter on. Counted against the DDL that 010–018 actually wrote:
--
--   shape (a) tenant/company tier      62   tenant_id NOT NULL
--   shape (b) nullable-tenant tier      5   audit_logs, background_jobs,
--                                           background_job_items,
--                                           public_holidays, retention_policies
--   p_auth (its own hand-written pair)  2   auth_sessions, auth_tokens
--   shape (c) reference tier            4   permissions, statutory_rules,
--                                           statutory_rule_bands, nitaqat_grid
--   platform-tier, grant-policed        2   platform_users, data_protection_keys
--   self-tenant                         1   tenants
--                                     ----
--                                       76
--
-- Three corrections to §19.2, each of which is a hole if taken literally:
--
--  1. `background_job_items.tenant_id` IS NULLABLE (018_workflow_audit.sql), and
--     §19.2's nullable-tenant list does not contain it. Under shape (a) the
--     platform rows of a platform job would be invisible to the very worker that
--     must drain them — the same silent failure §19.2 diagnosed for
--     retention_policies — so it gets shape (b) and the kynex_job queue policy,
--     exactly as its parent background_jobs does.
--
--  2. `tenants` has no tenant_id; its `id` IS the tenant id. Shape (a) is not
--     expressible on it. It gets `id = app.current_tenant() OR app.is_platform()`
--     for reads and platform-only writes, which is the same guarantee in the only
--     form the column list allows.
--
--  3. `data_protection_keys` has no tenant_id and appears in no shape in §19.2 —
--     it is named only in the secret-column list. It is the ASP.NET Data
--     Protection key ring: one ring for the whole deployment, written by the API
--     itself, not tenant data. It is policed by grant (and by the column revoke
--     on `xml`), with RLS enabled and forced so the ratchet does not need an
--     exception.
--
-- Everything else follows §19.2 as written.
-- =============================================================================

SET search_path = public, pg_catalog;


-- =============================================================================
-- 1. THE SHAPE MANIFEST
--
-- §19.2: "The generator emits a manifest of table → shape … and CI compares it
-- against pg_policies + role_table_grants, failing when a table's live policy
-- text does not match its declared shape, when a table has no entry, or when an
-- entry has no table." The manifest is emitted here, into the database, so CI
-- reads one source rather than parsing this file.
-- =============================================================================

CREATE TABLE IF NOT EXISTS app.rls_manifest (
    relname   text PRIMARY KEY,
    shape     text NOT NULL
        CHECK (shape IN ('a','b','c','p_auth','platform','self_tenant','keyring','view')),
    note      text
);

COMMENT ON TABLE app.rls_manifest IS
  'table → declared RLS shape, for the §19.2 ratchet. A table with no entry, or an entry with no table, fails CI by absence.';

ALTER TABLE app.rls_manifest ENABLE ROW LEVEL SECURITY;
ALTER TABLE app.rls_manifest FORCE ROW LEVEL SECURITY;
CREATE POLICY p_manifest_read ON app.rls_manifest FOR SELECT USING (true);

TRUNCATE app.rls_manifest;


-- =============================================================================
-- 2. ENABLE + FORCE ROW LEVEL SECURITY ON EVERYTHING
--
-- Every ordinary table, every partitioned parent AND every partition child
-- (relkind r and p, relispartition true or false). §19.3 consequence 3 is the
-- reason children are not exempted: a policy on the parent governs access
-- THROUGH the parent only, so a child that is granted and has RLS off is an
-- unfiltered copy of millions of rows that any session can read by naming it.
--
-- FORCE as well as ENABLE, because every object here is owned by kynex_owner and
-- a table owner is exempt from its own policies unless FORCE is set. FORCE does
-- NOT stop kynex_owner reading everything — BYPASSRLS outranks FORCE, measured —
-- which is precisely why the views below must be security_invoker.
--
-- This runs before any policy is created, so between this statement and section
-- 3 the database is fully closed. That is the intended direction of failure.
-- =============================================================================

DO $$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT n.nspname AS sch, c.relname AS tbl
          FROM pg_class c
          JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE n.nspname = 'public'
           AND c.relkind IN ('r','p')
           AND NOT EXISTS (SELECT 1 FROM pg_depend d
                            WHERE d.classid = 'pg_class'::regclass
                              AND d.objid = c.oid AND d.deptype = 'e')
         ORDER BY 1, 2
    LOOP
        EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY', r.sch, r.tbl);
        EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY',  r.sch, r.tbl);
    END LOOP;
END
$$;


-- =============================================================================
-- 3. SHAPE (a) — TENANT AND COMPANY TIER, 62 TABLES
--
-- USING (tenant_id = app.current_tenant()) and the same WITH CHECK.
-- Company-tier tables need nothing extra: company_id is already bound to the
-- tenant by its composite FK, so a row whose tenant_id passes cannot carry
-- another tenant's company.
--
-- FOR ALL, so the predicate is the visibility rule for SELECT/UPDATE/DELETE and
-- the admission rule for INSERT/UPDATE. With the GUC unset app.current_tenant()
-- is NULL, `tenant_id = NULL` is NULL, no row is visible and every INSERT raises
-- 42501 — the fail-closed property of §19.2.
--
-- The list is generated from the catalog condition it encodes (tenant_id NOT
-- NULL, minus the tables that carry a hand-written policy) rather than typed, so
-- a table added later without a policy cannot be missed by a copy-paste.
-- =============================================================================

DO $$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT c.relname AS tbl
          FROM pg_class c
          JOIN pg_namespace n ON n.oid = c.relnamespace
          JOIN pg_attribute a ON a.attrelid = c.oid
         WHERE n.nspname = 'public'
           AND c.relkind IN ('r','p')
           AND NOT c.relispartition
           AND a.attname = 'tenant_id'
           AND a.attnum > 0 AND NOT a.attisdropped
           AND a.attnotnull
         ORDER BY 1
    LOOP
        EXECUTE format(
            'CREATE POLICY p_tenant ON public.%I
                 FOR ALL
                 USING (tenant_id = app.current_tenant())
                 WITH CHECK (tenant_id = app.current_tenant())', r.tbl);
        INSERT INTO app.rls_manifest(relname, shape, note)
        VALUES (r.tbl, 'a', 'tenant/company tier: tenant_id = app.current_tenant()');
    END LOOP;
END
$$;


-- =============================================================================
-- 4. SHAPE (b) — NULLABLE-TENANT TIER, 5 TABLES
--
-- USING (tenant_id = app.current_tenant()
--        OR (tenant_id IS NULL AND app.is_platform()))
--
-- retention_policies belongs here and not in shape (a), and the reason is a
-- silent failure rather than a leak: its platform DEFAULT rows carry tenant_id
-- IS NULL, so under shape (a) a tenant session would see only its own override
-- rows and the retention engine would run with overrides and no defaults at all,
-- under-retaining or skipping entities whose only policy is the platform row.
--
-- public_holidays is the same story with the opposite consequence: the KSA
-- national calendar is seeded with tenant_id IS NULL, and a tenant that could
-- not see it would compute every Eid as a working day.
-- =============================================================================

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'audit_logs',
        'background_jobs',
        'background_job_items',
        'public_holidays',
        'retention_policies'
    ]
    LOOP
        EXECUTE format(
            'CREATE POLICY p_tenant_or_platform ON public.%I
                 FOR ALL
                 USING (tenant_id = app.current_tenant()
                        OR (tenant_id IS NULL AND app.is_platform()))
                 WITH CHECK (tenant_id = app.current_tenant()
                        OR (tenant_id IS NULL AND app.is_platform()))', t);
        INSERT INTO app.rls_manifest(relname, shape, note)
        VALUES (t, 'b', 'nullable-tenant tier: own rows, plus platform rows when is_platform()');
    END LOOP;
END
$$;

-- The leased queue. §19.2: background_jobs "additionally carries a kynex_job
-- policy so the leased-queue claim (FOR UPDATE SKIP LOCKED) can see queued
-- platform work without is_platform()". Scoped to tenant_id IS NULL — platform
-- work and nothing else — because a policy of USING (true) would hand the worker
-- every tenant's queue and make the per-tenant GUC loop of bypass surface 3
-- decorative. Policies are OR-ed, so this widens kynex_job only.
--
-- background_job_items gets the identical policy: an item of a platform job is
-- as unreachable without it as the job itself.
CREATE POLICY p_job_platform_queue ON background_jobs
    FOR ALL TO kynex_job
    USING (tenant_id IS NULL)
    WITH CHECK (tenant_id IS NULL);

CREATE POLICY p_job_platform_queue ON background_job_items
    FOR ALL TO kynex_job
    USING (tenant_id IS NULL)
    WITH CHECK (tenant_id IS NULL);


-- =============================================================================
-- 5. p_auth — auth_sessions AND auth_tokens (§19.2, hand-written)
--
-- These are the only tables carrying both subject kinds, so they get an
-- explicitly stated policy rather than shape (b). The subject_kind XOR is
-- already a table CHECK (ck_auth_sessions__subject_xor,
-- ck_auth_tokens__subject_xor in 020_constraints_a_f.sql), which is what makes
-- the policy total: every row satisfies exactly one branch, so there is no row
-- the policy fails to classify.
--
-- The consequence that matters: a tenant session can never create a session
-- without a tenant, and a platform session can never be READ by kynex_app,
-- because kynex_app cannot make app.is_platform() true (002_roles.sql).
--
-- Login must read these before a tenant is known; it does so through
-- app.resolve_login / app.resolve_platform_login, not through an unpoliced table.
-- =============================================================================

CREATE POLICY p_auth ON auth_sessions
    FOR ALL
    USING (
           (subject_kind = 'Tenant'   AND tenant_id = app.current_tenant())
        OR (subject_kind = 'Platform' AND tenant_id IS NULL AND app.is_platform())
    )
    WITH CHECK (
           (subject_kind = 'Tenant'   AND tenant_id = app.current_tenant())
        OR (subject_kind = 'Platform' AND tenant_id IS NULL AND app.is_platform())
    );

CREATE POLICY p_auth ON auth_tokens
    FOR ALL
    USING (
           (subject_kind = 'Tenant'   AND tenant_id = app.current_tenant())
        OR (subject_kind = 'Platform' AND tenant_id IS NULL AND app.is_platform())
    )
    WITH CHECK (
           (subject_kind = 'Tenant'   AND tenant_id = app.current_tenant())
        OR (subject_kind = 'Platform' AND tenant_id IS NULL AND app.is_platform())
    );

INSERT INTO app.rls_manifest(relname, shape, note) VALUES
    ('auth_sessions', 'p_auth', 'subject_kind XOR: Tenant rows by tenant, Platform rows only when is_platform()'),
    ('auth_tokens',   'p_auth', 'subject_kind XOR: Tenant rows by tenant, Platform rows only when is_platform()');


-- =============================================================================
-- 6. SHAPE (c) — REFERENCE TIER, 4 TABLES
--
-- FOR SELECT USING (true), and protected by WITHHOLDING THE WRITE GRANT rather
-- than by a policy. §19.2: this is what removes the largest bypass category
-- outright — reading statutory data stops needing a bypass because it stops
-- being filtered.
--
-- FOR SELECT and not FOR ALL on purpose. A FOR ALL USING (true) policy would
-- also supply a permissive WITH CHECK, so the moment anyone grants INSERT by
-- accident the table is writable. With a SELECT-only policy the grant alone is
-- not enough: an INSERT would still find no permissive policy and raise 42501.
-- Two independent things have to go wrong instead of one.
-- =============================================================================

DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'permissions',
        'statutory_rules',
        'statutory_rule_bands',
        'nitaqat_grid'
    ]
    LOOP
        EXECUTE format(
            'CREATE POLICY p_reference_read ON public.%I FOR SELECT USING (true)', t);
        INSERT INTO app.rls_manifest(relname, shape, note)
        VALUES (t, 'c', 'reference tier: readable by all, written only by kynex_owner (no write grant exists)');
    END LOOP;
END
$$;


-- =============================================================================
-- 7. platform_users — OUTSIDE ALL THREE SHAPES (§19.2)
--
-- No tenant_id at all, so there is nothing to filter on. Policed by grant:
-- kynex_platform only, with kynex_app, kynex_job and kynex_ro holding no grant
-- whatsoever. RLS is still enabled and forced with a USING (app.is_platform())
-- policy, so a stolen kynex_app connection — or a future accidental GRANT — sees
-- an empty table rather than the operator list.
--
-- kynex_ro is withheld too, not just kynex_app: §19.2's secret-column list
-- includes "platform_users.* equivalents", and the cheapest way to satisfy that
-- is to grant support no access to the operator table at all.
-- =============================================================================

CREATE POLICY p_platform ON platform_users
    FOR ALL
    USING (app.is_platform())
    WITH CHECK (app.is_platform());

INSERT INTO app.rls_manifest(relname, shape, note) VALUES
    ('platform_users', 'platform',
     'grant-policed: kynex_platform only. Policy is the second lock, not the first.');


-- =============================================================================
-- 8. tenants — SELF-TENANT (correction 2 above)
--
-- `id` is the tenant id, so the shape (a) predicate is written against `id`.
-- Reads: your own tenant row, or every row when is_platform(). Writes: platform
-- only — tenants.plan_limits is authoritative for plan gating (§11.6) and a
-- tenant that could raise its own seat limit would make the limit decorative.
-- The WITH CHECK is belt to the grant's braces: kynex_app holds SELECT only.
-- =============================================================================

CREATE POLICY p_self_tenant ON tenants
    FOR ALL
    USING (id = app.current_tenant() OR app.is_platform())
    WITH CHECK (app.is_platform());

INSERT INTO app.rls_manifest(relname, shape, note) VALUES
    ('tenants', 'self_tenant',
     'no tenant_id column; id IS the tenant. Read own row or all rows when is_platform(); write platform-only.');


-- =============================================================================
-- 9. data_protection_keys — THE KEY RING (correction 3 above)
--
-- One ring for the deployment, written by the API itself, not tenant data: every
-- tenant's payload is protected by the same keys, so partitioning the ring by
-- tenant would break decryption rather than isolate anything. Policed by grant
-- and by the column revoke on `xml`. RLS enabled and forced with an explicit
-- permissive policy so the coverage ratchet needs no exception for it, and so
-- the "why is this readable" question is answered in the schema rather than in a
-- reviewer's memory.
-- =============================================================================

CREATE POLICY p_keyring ON data_protection_keys
    FOR ALL
    USING (true)
    WITH CHECK (true);

INSERT INTO app.rls_manifest(relname, shape, note) VALUES
    ('data_protection_keys', 'keyring',
     'deployment-wide ASP.NET Data Protection ring; not tenant data. Grant-policed; xml revoked from kynex_ro.');


-- =============================================================================
-- 10. PARTITION CHILDREN — NO POLICY, BY DESIGN
--
-- Every child already has RLS enabled and forced (section 2). None gets a policy
-- and none gets a grant, so:
--     direct SELECT on a child, granted   → 0 rows   (RLS on, no policy = deny)
--     direct SELECT on a child, ungranted → ERROR 42501
-- and all real access goes through the parent, which carries the grants and the
-- policies. §19.3 consequence 3 rule 2: the ratchet asserts, for every child, RLS
-- enabled and forced and ZERO direct grants to any login role.
--
-- Children are recorded in the manifest under their parent's shape so an entry
-- exists for every relation and "no entry" stays a CI failure.
-- =============================================================================

INSERT INTO app.rls_manifest(relname, shape, note)
SELECT ch.relname,
       m.shape,
       format('partition child of %s: RLS forced, no policy, no grant', pa.relname)
  FROM pg_inherits i
  JOIN pg_class ch ON ch.oid = i.inhrelid
  JOIN pg_class pa ON pa.oid = i.inhparent
  JOIN pg_namespace n ON n.oid = ch.relnamespace
  JOIN app.rls_manifest m ON m.relname = pa.relname
 WHERE n.nspname = 'public';


-- =============================================================================
-- 11. THE TWO VIEWS — security_invoker, AND THIS IS A P0
--
-- A view without WITH (security_invoker = true) evaluates its base tables with
-- the privileges and policies of the VIEW OWNER. Every object here is owned by
-- kynex_owner, which holds BYPASSRLS, so an ordinary view would read EVERY
-- tenant's rows and hand them to the caller — and FORCE ROW LEVEL SECURITY would
-- not help, because BYPASSRLS outranks FORCE. Measured in postgres:16 against
-- this very schema: a FORCE-d table read by a BYPASSRLS role returns all rows.
--
-- That makes an ordinary view a cross-tenant leak on the design's own mandated
-- read path, because §11.6 makes v_leave_balances the ONLY sanctioned way to
-- read a leave balance.
--
-- With security_invoker = true the base tables are read with the CALLER's
-- privileges and the CALLER's policies, so the views are exactly as filtered as
-- the tables underneath them, and a kynex_ro caller still cannot see a column it
-- has no grant on.
-- =============================================================================

CREATE OR REPLACE VIEW v_leave_balances WITH (security_invoker = true) AS
SELECT l.tenant_id,
       l.employee_id,
       l.leave_type_id,
       sum(l.days)                                     AS balance_days,
       sum(l.days) FILTER (WHERE l.entry_type = 'Accrual')  AS accrued_days,
       sum(l.days) FILTER (WHERE l.entry_type = 'Debit')    AS debited_days,
       count(*)                                        AS movement_count,
       max(l.entry_date)                               AS last_movement_on
  FROM leave_ledger l
 GROUP BY l.tenant_id, l.employee_id, l.leave_type_id;

COMMENT ON VIEW v_leave_balances IS
  'The only sanctioned way to read a leave balance (§11.6): a SUM over the append-only leave_ledger, which is authoritative. No table stores a balance. security_invoker = true, so the SUM is over the caller''s tenant only — without it the view would sum every tenant''s ledger, because its owner kynex_owner holds BYPASSRLS.';


CREATE OR REPLACE VIEW v_employee_current WITH (security_invoker = true) AS
SELECT e.tenant_id,
       e.id                        AS employee_id,
       e.employee_number,
       e.status,
       e.name_en,
       e.name_ar,
       e.work_email,
       e.joining_date,
       e.separation_date,
       asg.id                      AS assignment_id,
       asg.company_id,
       asg.branch_id,
       asg.department_id,
       asg.designation_id,
       asg.grade_id,
       asg.manager_employee_id,
       asg.cost_center_id,
       asg.employment_status,
       asg.pay_group,
       asg.effective_from          AS assignment_effective_from,
       sal.id                      AS salary_id,
       sal.basic,
       sal.housing,
       sal.transport,
       sal.housing_in_kind,
       sal.effective_from          AS salary_effective_from,
       bank.id                     AS bank_account_id,
       bank.iban,
       bank.bank_code,
       bank.payment_method
  FROM employees e
  -- the business day in the TENANT's timezone, not the server's (§13.4). The
  -- join is inner on purpose: a session that cannot see its tenant row cannot
  -- see an employee either, which is the fail-closed direction.
  JOIN tenants t
    ON t.id = e.tenant_id
  CROSS JOIN LATERAL (SELECT (now() AT TIME ZONE t.timezone_id)::date AS d) today
  LEFT JOIN LATERAL (
        SELECT a.*
          FROM employee_assignments a
         WHERE a.tenant_id = e.tenant_id
           AND a.employee_id = e.id
           AND a.effective_from <= today.d
           AND (a.effective_to IS NULL OR a.effective_to >= today.d)
         ORDER BY a.effective_from DESC
         LIMIT 1) asg ON true
  LEFT JOIN LATERAL (
        SELECT s.*
          FROM employee_salaries s
         WHERE s.tenant_id = e.tenant_id
           AND s.employee_id = e.id
           AND s.effective_from <= today.d
           AND (s.effective_to IS NULL OR s.effective_to >= today.d)
         ORDER BY s.effective_from DESC
         LIMIT 1) sal ON true
  LEFT JOIN LATERAL (
        SELECT b.*
          FROM employee_bank_accounts b
         WHERE b.tenant_id = e.tenant_id
           AND b.employee_id = e.id
           AND b.effective_from <= today.d
           AND (b.effective_to IS NULL OR b.effective_to >= today.d)
         ORDER BY b.effective_from DESC
         LIMIT 1) bank ON true
 WHERE e.deleted_at IS NULL;

COMMENT ON VIEW v_employee_current IS
  'One row per live employee joined to the assignment, salary and bank row in force TODAY in the tenant''s own timezone, so no screen has to choose between employees.status and the effective-dated truth (§11.3). security_invoker = true: without it the view would read every tenant, its owner holding BYPASSRLS.';

INSERT INTO app.rls_manifest(relname, shape, note) VALUES
    ('v_leave_balances',  'view', 'security_invoker = true; filtered by the base tables'' own policies'),
    ('v_employee_current','view', 'security_invoker = true; filtered by the base tables'' own policies');


-- =============================================================================
-- 12. GRANTS
--
-- The order that matters: revoke first, grant forward. Nothing below relies on a
-- default, and a table that this section forgets ends up reachable by nobody.
-- =============================================================================

-- Belt and braces on top of 002_roles.sql's REVOKE ALL ON SCHEMA public.
-- Scoped past extension-owned relations: `REVOKE … ON ALL TABLES IN SCHEMA
-- public` would also hit pg_stat_statements' views and emit a column warning per
-- column. pg_stat_statements IS revoked, but deliberately and on its own line —
-- it is world-readable by default and its query texts carry literals from every
-- tenant, which makes it a cross-tenant read that no policy covers.
DO $$
DECLARE
    r record;
BEGIN
    SET LOCAL client_min_messages = error;   -- extension views warn per column
    FOR r IN
        SELECT c.relname AS tbl
          FROM pg_class c
          JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE n.nspname = 'public'
           AND c.relkind IN ('r','p','v','S')
           AND NOT EXISTS (SELECT 1 FROM pg_depend d
                            WHERE d.classid = 'pg_class'::regclass
                              AND d.objid = c.oid AND d.deptype = 'e')
    LOOP
        EXECUTE format('REVOKE ALL ON public.%I FROM PUBLIC', r.tbl);
    END LOOP;
END
$$;

DO $$
BEGIN
    SET LOCAL client_min_messages = error;
    IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = 'pg_stat_statements') THEN
        EXECUTE 'REVOKE ALL ON public.pg_stat_statements FROM PUBLIC';
        EXECUTE 'GRANT SELECT ON public.pg_stat_statements TO kynex_platform';
    END IF;
END
$$;

DO $$
DECLARE
    r        record;
    v_dml    constant text := 'SELECT, INSERT, UPDATE, DELETE';
    v_shape  text;
BEGIN
    FOR r IN
        SELECT c.relname AS tbl, m.shape
          FROM pg_class c
          JOIN pg_namespace n ON n.oid = c.relnamespace
          JOIN app.rls_manifest m ON m.relname = c.relname
         WHERE n.nspname = 'public'
           AND c.relkind IN ('r','p')
           AND NOT c.relispartition          -- children get NOTHING (§19.3)
         ORDER BY 1
    LOOP
        v_shape := r.shape;

        IF v_shape IN ('a','b','p_auth') THEN
            -- the working set: the API and the workers read and write it,
            -- platform administration reaches it through the same policies,
            -- support reads it.
            EXECUTE format('GRANT %s ON public.%I TO kynex_app, kynex_job, kynex_platform', v_dml, r.tbl);
            EXECUTE format('GRANT SELECT ON public.%I TO kynex_ro', r.tbl);

        ELSIF v_shape = 'c' THEN
            -- reference tier: SELECT only, for everyone. NO write grant to any
            -- login role — this is the protection, not the policy (§19.2 CI
            -- assertion 2). Only kynex_owner, reached by kynex_migrator, writes
            -- these, which is what 070_seed_reference.sql does.
            EXECUTE format('GRANT SELECT ON public.%I TO kynex_app, kynex_job, kynex_platform, kynex_ro', r.tbl);

        ELSIF v_shape = 'platform' THEN
            -- platform_users: kynex_platform and nobody else. §19.2 CI assertion
            -- 2 asserts kynex_app holds NO grant at all here.
            EXECUTE format('GRANT %s ON public.%I TO kynex_platform', v_dml, r.tbl);

        ELSIF v_shape = 'self_tenant' THEN
            -- tenants: read for everyone (the policy narrows it to your own row),
            -- write for platform only.
            EXECUTE format('GRANT SELECT ON public.%I TO kynex_app, kynex_job, kynex_ro', r.tbl);
            EXECUTE format('GRANT %s ON public.%I TO kynex_platform', v_dml, r.tbl);

        ELSIF v_shape = 'keyring' THEN
            -- data_protection_keys: the API rotates the ring, so it needs write.
            -- No DELETE: a Data Protection key is revoked by writing a new
            -- element, never by removing one, and a deleted key is unreadable
            -- ciphertext for every tenant at once.
            EXECUTE format('GRANT SELECT, INSERT, UPDATE ON public.%I TO kynex_app, kynex_job', r.tbl);
            EXECUTE format('GRANT SELECT ON public.%I TO kynex_platform', r.tbl);

        ELSE
            RAISE EXCEPTION '060: table % has manifest shape % with no grant rule', r.tbl, v_shape;
        END IF;
    END LOOP;
END
$$;

-- No sequences exist in this baseline (every key is an app-side UUIDv7,
-- CONVENTIONS.md §2), but audit_logs.seq is allocated from a per-tenant sequence
-- created at provisioning time (§19.3). This makes that grant the default so
-- provisioning does not have to remember it.
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO kynex_app, kynex_job, kynex_platform;
ALTER DEFAULT PRIVILEGES FOR ROLE kynex_owner IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO kynex_app, kynex_job, kynex_platform;

-- The views. security_invoker means the caller still needs its own grants on the
-- base tables, which every role above already has.
GRANT SELECT ON v_leave_balances, v_employee_current
    TO kynex_app, kynex_job, kynex_platform, kynex_ro;

GRANT SELECT ON app.rls_manifest TO kynex_app, kynex_job, kynex_platform, kynex_ro;


-- =============================================================================
-- 13. SECRET COLUMNS (§19.2, audit P1-17)
--
-- kynex_ro is support and analytics. It must not be able to read a credential.
-- Section 12 gave it table-level SELECT, which covers every column including
-- future ones; here that table-level grant is REVOKED on the tables holding
-- secrets and replaced by a column-level SELECT over everything else.
--
-- Generated from the secret list rather than typed out, so adding a column to
-- `users` extends kynex_ro's grant automatically while password_hash stays out.
-- A column added to the SECRET list, however, requires this file to re-run —
-- which the baseline does on every fresh deploy, and which an ordinary migration
-- must do explicitly.
--
-- Two additions to §19.2's list, and they are additions, reported as such:
-- auth_sessions.refresh_token_hash and auth_sessions.previous_token_hash. §19.2
-- lists auth_tokens.token_hash but not these, and they are the same class of
-- secret — a refresh token hash is a durable credential for a live device
-- session. push_token is included with them because it is a third-party push
-- credential, not an analytics attribute.
-- =============================================================================

DO $$
DECLARE
    r        record;
    v_cols   text;
BEGIN
    FOR r IN
        SELECT * FROM (VALUES
            ('users',                  ARRAY['password_hash','mfa_secret_encrypted','mfa_recovery_hashes']),
            ('auth_tokens',            ARRAY['token_hash']),
            ('auth_sessions',          ARRAY['refresh_token_hash','previous_token_hash','push_token']),
            ('attendance_devices',     ARRAY['api_key_hash']),
            ('data_protection_keys',   ARRAY['xml'])
        ) AS v(tbl, secrets)
    LOOP
        -- every secret column must actually exist, or the revoke is a no-op that
        -- silently leaves a credential readable.
        PERFORM 1
           FROM unnest(r.secrets) s
          WHERE NOT EXISTS (
                SELECT 1 FROM information_schema.columns c
                 WHERE c.table_schema = 'public'
                   AND c.table_name = r.tbl
                   AND c.column_name = s);
        IF FOUND THEN
            RAISE EXCEPTION '060: secret column list for % names a column that does not exist', r.tbl;
        END IF;

        SELECT string_agg(quote_ident(c.column_name), ', ' ORDER BY c.ordinal_position)
          INTO v_cols
          FROM information_schema.columns c
         WHERE c.table_schema = 'public'
           AND c.table_name = r.tbl
           AND NOT (c.column_name = ANY (r.secrets));

        EXECUTE format('REVOKE SELECT ON public.%I FROM kynex_ro', r.tbl);
        EXECUTE format('GRANT SELECT (%s) ON public.%I TO kynex_ro', v_cols, r.tbl);
    END LOOP;
END
$$;

-- platform_users needs no column surgery: kynex_ro holds no grant on it at all
-- (section 12), which covers "platform_users.* equivalents" in one statement.


-- =============================================================================
-- 14. COVERAGE SELF-CHECK
--
-- The ratchet of §19.1 gate 2 and §19.2 lives in CI, but a baseline that shipped
-- an uncovered table would not be caught until CI ran. These four assertions run
-- inside the migration transaction, so an uncovered table fails the DEPLOY.
-- =============================================================================

DO $$
DECLARE
    v_bad text;
BEGIN
    -- (1) every table has RLS enabled AND forced
    SELECT string_agg(c.relname, ', ' ORDER BY c.relname) INTO v_bad
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = 'public' AND c.relkind IN ('r','p')
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass
                          AND d.objid = c.oid AND d.deptype = 'e')
       AND NOT (c.relrowsecurity AND c.relforcerowsecurity);
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION '060: RLS not enabled+forced on: %', v_bad;
    END IF;

    -- (2) every table and view has a manifest entry
    SELECT string_agg(c.relname, ', ' ORDER BY c.relname) INTO v_bad
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = 'public' AND c.relkind IN ('r','p','v')
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass
                          AND d.objid = c.oid AND d.deptype = 'e')
       AND NOT EXISTS (SELECT 1 FROM app.rls_manifest m WHERE m.relname = c.relname);
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION '060: no manifest entry for: %', v_bad;
    END IF;

    -- (3) no manifest entry without a relation
    SELECT string_agg(m.relname, ', ' ORDER BY m.relname) INTO v_bad
      FROM app.rls_manifest m
     WHERE NOT EXISTS (
           SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = m.relname
              AND c.relkind IN ('r','p','v'));
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION '060: manifest names a relation that does not exist: %', v_bad;
    END IF;

    -- (4) every view carries security_invoker
    SELECT string_agg(c.relname, ', ' ORDER BY c.relname) INTO v_bad
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = 'public' AND c.relkind = 'v'
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass
                          AND d.objid = c.oid AND d.deptype = 'e')
       AND NOT coalesce(array_to_string(c.reloptions, ',') LIKE '%security_invoker=true%', false);
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION '060: view without security_invoker=true: %', v_bad;
    END IF;
END
$$;
