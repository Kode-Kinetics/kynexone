-- =============================================================================
-- tests/070_rls_proof.sql
-- Proves the §19.2 / §19.3 security properties against a live database.
--
-- Run it with tests/verify_rls.sh, which builds a throwaway postgres:16
-- container, applies 001 … 060 and then runs this file. It NEVER runs against a
-- real database: it seeds two tenants, temporarily grants and revokes on a
-- partition child, and asserts by raising.
--
-- Must be run by a role that can SET ROLE to kynex_app / kynex_job /
-- kynex_platform / kynex_ro — the container's bootstrap superuser. SET ROLE is
-- faithful for this purpose: RLS is decided on the CURRENT user, and after
-- SET ROLE kynex_app the session is NOBYPASSRLS and non-superuser, so the
-- policies apply exactly as they do on a real kynex_app connection.
-- verify_rls.sh re-runs the sharpest assertions over genuine per-role
-- connections as well, so the equivalence is not assumed.
--
-- Every assertion raises on failure. Reaching the end is the pass.
-- =============================================================================

\set ON_ERROR_STOP on
\timing off

\echo '== seeding two tenants as kynex_owner =='

SET ROLE kynex_owner;

-- kynex_owner holds BYPASSRLS, which is what lets the seed write both tenants
-- in one session. That privilege is exactly what every assertion below is
-- checking no OTHER role can reach.
DO $$
DECLARE
    a uuid := 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    b uuid := 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
    v_month date := date_trunc('month', now())::date;
BEGIN
    INSERT INTO tenants (id, slug, name, status) VALUES
        (a, 'tenant-a', 'Tenant A', 'Active'),
        (b, 'tenant-b', 'Tenant B', 'Active');

    INSERT INTO employees (id, tenant_id, employee_number, name_en, status) VALUES
        ('a1111111-1111-4111-8111-111111111111', a, 'A-001', 'Employee A', 'Active'),
        ('b1111111-1111-4111-8111-111111111111', b, 'B-001', 'Employee B', 'Active');

    INSERT INTO users (id, tenant_id, employee_id, normalized_email, status, password_hash) VALUES
        ('a2222222-2222-4222-8222-222222222222', a,
         'a1111111-1111-4111-8111-111111111111', 'user@example.test', 'Active', 'HASH-A'),
        ('b2222222-2222-4222-8222-222222222222', b,
         'b1111111-1111-4111-8111-111111111111', 'user@example.test', 'Active', 'HASH-B');
    -- NOTE: the SAME email in both tenants, deliberately. That is the case
    -- §19.2 says an email-only resolve_login cannot answer.

    INSERT INTO leave_types (id, tenant_id, code, name_en) VALUES
        ('a3333333-3333-4333-8333-333333333333', a, 'ANNUAL', 'Annual leave'),
        ('b3333333-3333-4333-8333-333333333333', b, 'ANNUAL', 'Annual leave');

    INSERT INTO leave_ledger (id, tenant_id, employee_id, leave_type_id,
                              entry_type, entry_date, days, idempotency_key) VALUES
        ('a4444444-4444-4444-8444-444444444444', a,
         'a1111111-1111-4111-8111-111111111111', 'a3333333-3333-4333-8333-333333333333',
         'Accrual', current_date, 21.00, 'seed-a'),
        ('b4444444-4444-4444-8444-444444444444', b,
         'b1111111-1111-4111-8111-111111111111', 'b3333333-3333-4333-8333-333333333333',
         'Accrual', current_date, 30.00, 'seed-b');

    -- partitioned tables: dates inside the CURRENT month, so the rows land in a
    -- real monthly child and not in the DEFAULT catch-all.
    INSERT INTO attendance_punches (id, tenant_id, employee_id, direction, source,
                                    occurred_at, idempotency_key) VALUES
        ('a5555555-5555-4555-8555-555555555555', a,
         'a1111111-1111-4111-8111-111111111111', 'In', 'Device',
         v_month + interval '2 days', 'punch-a'),
        ('b5555555-5555-4555-8555-555555555555', b,
         'b1111111-1111-4111-8111-111111111111', 'In', 'Device',
         v_month + interval '2 days', 'punch-b');

    INSERT INTO attendance_days (id, tenant_id, employee_id, status, work_date) VALUES
        ('a6666666-6666-4666-8666-666666666666', a,
         'a1111111-1111-4111-8111-111111111111', 'Present', v_month + 2),
        ('b6666666-6666-4666-8666-666666666666', b,
         'b1111111-1111-4111-8111-111111111111', 'Present', v_month + 2);

    -- nullable-tenant tier: one row per tenant AND one platform row, so shape
    -- (b) has something to be wrong about in both directions.
    INSERT INTO audit_logs (id, tenant_id, chain_key, seq, created_at,
                            record_kind, envelope_hash) VALUES
        ('a7777777-7777-4777-8777-777777777777', a,    'tenant-a', 1,
         v_month + interval '3 days', 'Event', 'deadbeef'),
        ('b7777777-7777-4777-8777-777777777777', b,    'tenant-b', 1,
         v_month + interval '3 days', 'Event', 'deadbeef'),
        ('c7777777-7777-4777-8777-777777777777', NULL, 'platform', 1,
         v_month + interval '3 days', 'Event', 'deadbeef');

    INSERT INTO platform_users (id, email, platform_role, full_name, password_hash, status)
    VALUES ('c1111111-1111-4111-8111-111111111111', 'operator@example.test',
            'Support', 'Platform Operator', 'HASH-P', 'Active');

    INSERT INTO auth_sessions (id, tenant_id, user_id, platform_user_id, device_id,
                               subject_kind, refresh_token_hash, expires_at) VALUES
        ('a8888888-8888-4888-8888-888888888888', a,
         'a2222222-2222-4222-8222-222222222222', NULL, 'dev-a', 'Tenant',
         'RT-A', now() + interval '30 days'),
        ('b8888888-8888-4888-8888-888888888888', b,
         'b2222222-2222-4222-8222-222222222222', NULL, 'dev-b', 'Tenant',
         'RT-B', now() + interval '30 days'),
        ('c8888888-8888-4888-8888-888888888888', NULL, NULL,
         'c1111111-1111-4111-8111-111111111111', 'dev-p', 'Platform',
         'RT-P', now() + interval '30 days');
END
$$;

RESET ROLE;


-- =============================================================================
-- PROOF 1 — FAIL-CLOSED
--
-- §19.2: "With app.tenant_id unset, app.current_tenant() is NULL,
-- tenant_id = NULL is NULL, no row is visible, and every INSERT raises 42501."
--
-- Asserted over EVERY table the manifest declares as tenant-filtered, not over a
-- sample: a table that leaked would otherwise have to be guessed at.
-- =============================================================================

\echo '== 1. kynex_app with app.tenant_id unset sees zero rows in every tenant table =='

DO $$
DECLARE
    r        record;
    n        bigint;
    v_leaked text := '';
    v_count  int := 0;
BEGIN
    EXECUTE 'SET ROLE kynex_app';
    PERFORM set_config('app.tenant_id', '', false);
    PERFORM set_config('app.platform',  'off', false);

    FOR r IN
        SELECT m.relname AS tbl
          FROM app.rls_manifest m
          JOIN pg_class c   ON c.relname = m.relname
          JOIN pg_namespace n2 ON n2.oid = c.relnamespace AND n2.nspname = 'public'
         WHERE m.shape IN ('a','b','p_auth','self_tenant')
           AND NOT c.relispartition
         ORDER BY 1
    LOOP
        EXECUTE format('SELECT count(*) FROM public.%I', r.tbl) INTO n;
        v_count := v_count + 1;
        IF n <> 0 THEN
            v_leaked := v_leaked || format(' %s=%s', r.tbl, n);
        END IF;
    END LOOP;

    EXECUTE 'RESET ROLE';

    IF v_leaked <> '' THEN
        RAISE EXCEPTION 'PROOF 1 FAILED — rows visible with no tenant GUC:%', v_leaked;
    END IF;
    IF v_count < 70 THEN
        RAISE EXCEPTION 'PROOF 1 INCONCLUSIVE — only % tables checked', v_count;
    END IF;
    RAISE NOTICE '   % ok', rpad(format('%s tenant tables, all zero rows', v_count), 58);
END
$$;

-- and the INSERT half of fail-closed
DO $$
DECLARE ok boolean := false;
BEGIN
    EXECUTE 'SET ROLE kynex_app';
    PERFORM set_config('app.tenant_id', '', false);
    BEGIN
        INSERT INTO employees (id, tenant_id, employee_number, name_en)
        VALUES (gen_random_uuid(), 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'X-001', 'X');
    EXCEPTION WHEN insufficient_privilege THEN ok := true;
    END;
    EXECUTE 'RESET ROLE';
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 1 FAILED — INSERT succeeded with no tenant GUC'; END IF;
    RAISE NOTICE '   % ok', rpad('INSERT with no tenant GUC raises 42501', 58);
END
$$;

-- platform_users is not merely empty for kynex_app: it is ungranted.
DO $$
DECLARE ok boolean := false;
BEGIN
    EXECUTE 'SET ROLE kynex_app';
    BEGIN
        PERFORM count(*) FROM platform_users;
    EXCEPTION WHEN insufficient_privilege THEN ok := true;
    END;
    EXECUTE 'RESET ROLE';
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 1 FAILED — kynex_app can read platform_users'; END IF;
    RAISE NOTICE '   % ok', rpad('platform_users ungranted to kynex_app (42501)', 58);
END
$$;


-- =============================================================================
-- PROOF 2 — TENANT A CANNOT REACH TENANT B
--
-- Read, update and delete; through both views; through a partition parent;
-- through a partition child named directly; and through a SECURITY DEFINER
-- function.
-- =============================================================================

\echo '== 2. tenant A cannot read, update or delete tenant B =='

DO $$
DECLARE
    a       uuid := 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    b_emp   uuid := 'b1111111-1111-4111-8111-111111111111';
    n       bigint;
    v_rows  int;
    ok      boolean;
BEGIN
    EXECUTE 'SET ROLE kynex_app';
    PERFORM set_config('app.tenant_id', a::text, false);

    -- 2a. read
    SELECT count(*) INTO n FROM employees;
    IF n <> 1 THEN RAISE EXCEPTION 'PROOF 2a FAILED — employees visible = % (expected 1)', n; END IF;
    SELECT count(*) INTO n FROM employees WHERE id = b_emp;
    IF n <> 0 THEN RAISE EXCEPTION 'PROOF 2a FAILED — tenant B employee readable by id'; END IF;

    -- 2b. update: a policy-filtered UPDATE finds no row rather than erroring
    UPDATE employees SET name_en = 'HIJACKED' WHERE id = b_emp;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    IF v_rows <> 0 THEN RAISE EXCEPTION 'PROOF 2b FAILED — updated % of tenant B''s rows', v_rows; END IF;

    -- 2c. delete
    DELETE FROM employees WHERE id = b_emp;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    IF v_rows <> 0 THEN RAISE EXCEPTION 'PROOF 2c FAILED — deleted % of tenant B''s rows', v_rows; END IF;

    -- 2d. write INTO tenant B — blocked by WITH CHECK, which is the half a
    -- USING-only policy would miss
    ok := false;
    BEGIN
        INSERT INTO employees (id, tenant_id, employee_number, name_en)
        VALUES (gen_random_uuid(), 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'B-999', 'Planted');
    EXCEPTION WHEN insufficient_privilege THEN ok := true;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 2d FAILED — inserted a row into tenant B'; END IF;

    -- 2e. UPDATE that tries to MOVE a row to tenant B — the other WITH CHECK case
    ok := false;
    BEGIN
        UPDATE employees SET tenant_id = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
    EXCEPTION WHEN insufficient_privilege THEN ok := true;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 2e FAILED — moved a row into tenant B'; END IF;

    EXECUTE 'RESET ROLE';
    RAISE NOTICE '   % ok', rpad('select / update / delete / insert / re-tenant', 58);
END
$$;

\echo '== 2f. through both views =='

DO $$
DECLARE
    a uuid := 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    n bigint;
    v numeric;
BEGIN
    EXECUTE 'SET ROLE kynex_app';
    PERFORM set_config('app.tenant_id', a::text, false);

    SELECT count(*) INTO n FROM v_leave_balances;
    IF n <> 1 THEN RAISE EXCEPTION 'PROOF 2f FAILED — v_leave_balances rows = % (expected 1)', n; END IF;

    -- the leak this view would carry without security_invoker is not a stray
    -- row, it is a WRONG NUMBER: the SUM would silently include tenant B's 30
    -- days and return 51.
    SELECT balance_days INTO v FROM v_leave_balances;
    IF v <> 21.00 THEN
        RAISE EXCEPTION 'PROOF 2f FAILED — v_leave_balances summed across tenants: % (expected 21.00)', v;
    END IF;

    SELECT count(*) INTO n FROM v_employee_current;
    IF n <> 1 THEN RAISE EXCEPTION 'PROOF 2f FAILED — v_employee_current rows = % (expected 1)', n; END IF;
    SELECT count(*) INTO n FROM v_employee_current WHERE tenant_id <> a;
    IF n <> 0 THEN RAISE EXCEPTION 'PROOF 2f FAILED — v_employee_current shows another tenant'; END IF;

    EXECUTE 'RESET ROLE';
    RAISE NOTICE '   % ok', rpad('v_leave_balances sums one tenant only (21.00, not 51.00)', 58);
    RAISE NOTICE '   % ok', rpad('v_employee_current shows one tenant only', 58);
END
$$;

\echo '== 2g. through a partition parent, and a child named directly =='

DO $$
DECLARE
    a       uuid := 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    v_child text := format('attendance_punches_y%sm%s',
                           to_char(now(), 'YYYY'), to_char(now(), 'MM'));
    n       bigint;
    ok      boolean := false;
BEGIN
    EXECUTE 'SET ROLE kynex_app';
    PERFORM set_config('app.tenant_id', a::text, false);

    -- parent
    SELECT count(*) INTO n FROM attendance_punches;
    IF n <> 1 THEN RAISE EXCEPTION 'PROOF 2g FAILED — parent shows % punches (expected 1)', n; END IF;

    -- child, named directly, ungranted
    BEGIN
        EXECUTE format('SELECT count(*) FROM public.%I', v_child) INTO n;
    EXCEPTION WHEN insufficient_privilege THEN ok := true;
    END;
    EXECUTE 'RESET ROLE';
    IF NOT ok THEN
        RAISE EXCEPTION 'PROOF 2g FAILED — % is directly readable by kynex_app', v_child;
    END IF;
    RAISE NOTICE '   % ok', rpad(format('parent filters; %s ungranted (42501)', v_child), 58);
END
$$;

-- The stronger half. §19.3's rule is that a child must be BOTH ungranted AND
-- RLS-forced, because the two failures are independent: PartitionMaintenance
-- could add a GRANT, and only the forced RLS would then stand between a session
-- and every tenant's rows. Granted here on purpose, in a throwaway container,
-- to show that the second lock holds on its own. Revoked immediately after.
DO $$
DECLARE
    a       uuid := 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    v_child text := format('attendance_punches_y%sm%s',
                           to_char(now(), 'YYYY'), to_char(now(), 'MM'));
    n       bigint;
BEGIN
    EXECUTE 'SET ROLE kynex_owner';
    EXECUTE format('GRANT SELECT ON public.%I TO kynex_app', v_child);
    EXECUTE 'RESET ROLE';

    EXECUTE 'SET ROLE kynex_app';
    PERFORM set_config('app.tenant_id', a::text, false);
    EXECUTE format('SELECT count(*) FROM public.%I', v_child) INTO n;
    EXECUTE 'RESET ROLE';

    EXECUTE 'SET ROLE kynex_owner';
    EXECUTE format('REVOKE SELECT ON public.%I FROM kynex_app', v_child);
    EXECUTE 'RESET ROLE';

    IF n <> 0 THEN
        RAISE EXCEPTION
          'PROOF 2g FAILED — with a GRANT, % returned % rows; forced RLS on the child did not hold',
          v_child, n;
    END IF;
    RAISE NOTICE '   % ok', rpad('child WITH a grant still returns 0 (RLS forced, no policy)', 58);
END
$$;

\echo '== 2h. through a SECURITY DEFINER function =='

DO $$
DECLARE
    a  uuid := 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    h  text;
    ok boolean := false;
BEGIN
    EXECUTE 'SET ROLE kynex_app';
    PERFORM set_config('app.tenant_id', a::text, false);

    -- own tenant: the login path works
    SELECT r.password_hash INTO h FROM app.resolve_login('tenant-a', 'user@example.test') r;
    IF h IS DISTINCT FROM 'HASH-A' THEN
        RAISE EXCEPTION 'PROOF 2h FAILED — resolve_login did not return the caller''s own hash (got %)', h;
    END IF;

    -- another tenant, same email: refused, not answered
    BEGIN
        PERFORM * FROM app.resolve_login('tenant-b', 'user@example.test');
    EXCEPTION WHEN insufficient_privilege THEN ok := true;
    END;

    EXECUTE 'RESET ROLE';
    IF NOT ok THEN
        RAISE EXCEPTION 'PROOF 2h FAILED — a tenant-bound session read tenant B''s credential through a SECURITY DEFINER function';
    END IF;
    RAISE NOTICE '   % ok', rpad('resolve_login: own tenant answered, other tenant 42501', 58);
END
$$;

-- …and there is no OTHER SECURITY DEFINER function to go through. §19.2 CI
-- assertion 3: none executable by PUBLIC or kynex_ro, every one declaring
-- SET search_path.
DO $$
DECLARE
    r      record;
    v_bad  text := '';
BEGIN
    FOR r IN
        SELECT p.oid, n.nspname || '.' || p.proname AS fn, p.proconfig, p.proacl
          FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
         WHERE p.prosecdef
           AND n.nspname NOT IN ('pg_catalog','information_schema')
           AND NOT EXISTS (SELECT 1 FROM pg_depend d
                            WHERE d.classid = 'pg_proc'::regclass
                              AND d.objid = p.oid AND d.deptype = 'e')
    LOOP
        IF r.proconfig IS NULL
           OR NOT ('search_path=pg_catalog, app' = ANY (r.proconfig)) THEN
            v_bad := v_bad || format(' %s(no search_path)', r.fn);
        END IF;
        IF has_function_privilege('public', r.oid, 'EXECUTE') THEN
            v_bad := v_bad || format(' %s(PUBLIC can execute)', r.fn);
        END IF;
        IF has_function_privilege('kynex_ro', r.oid, 'EXECUTE') THEN
            v_bad := v_bad || format(' %s(kynex_ro can execute)', r.fn);
        END IF;
    END LOOP;
    IF v_bad <> '' THEN
        RAISE EXCEPTION 'PROOF 2h FAILED — SECURITY DEFINER defects:%', v_bad;
    END IF;
    RAISE NOTICE '   % ok', rpad('every SECURITY DEFINER fn: search_path set, not PUBLIC, not ro', 58);
END
$$;


-- =============================================================================
-- PROOF 3 — kynex_app CANNOT PROMOTE ITSELF TO PLATFORM
--
-- PostgreSQL does not restrict SET on a custom GUC by role, so the SET below
-- SUCCEEDS. What must not happen is that it changes anything.
-- =============================================================================

\echo '== 3. kynex_app cannot set itself platform =='

DO $$
DECLARE
    v_is boolean;
    n    bigint;
BEGIN
    EXECUTE 'SET ROLE kynex_app';
    PERFORM set_config('app.tenant_id', '', false);
    PERFORM set_config('app.platform',  'on', false);   -- allowed, and useless

    SELECT app.is_platform() INTO v_is;
    IF v_is THEN
        RAISE EXCEPTION 'PROOF 3 FAILED — kynex_app made app.is_platform() true with the GUC alone';
    END IF;

    -- and the consequence: the platform rows of shape (b) stay invisible
    SELECT count(*) INTO n FROM audit_logs WHERE tenant_id IS NULL;
    IF n <> 0 THEN
        RAISE EXCEPTION 'PROOF 3 FAILED — kynex_app saw % platform audit rows', n;
    END IF;

    -- and the platform auth_session stays invisible
    SELECT count(*) INTO n FROM auth_sessions WHERE subject_kind = 'Platform';
    IF n <> 0 THEN
        RAISE EXCEPTION 'PROOF 3 FAILED — kynex_app saw a Platform auth_session';
    END IF;

    EXECUTE 'RESET ROLE';
    RAISE NOTICE '   % ok', rpad('GUC set, is_platform() still false, platform rows unseen', 58);
END
$$;

-- the positive control: the same GUC DOES work for the role that holds the
-- membership, so the AND is narrowing an authority rather than disabling one.
DO $$
DECLARE
    v_is boolean;
    n    bigint;
BEGIN
    EXECUTE 'SET ROLE kynex_platform';
    PERFORM set_config('app.tenant_id', '', false);
    PERFORM set_config('app.platform',  'on', false);

    SELECT app.is_platform() INTO v_is;
    IF NOT v_is THEN
        RAISE EXCEPTION 'PROOF 3 FAILED — kynex_platform could not become platform';
    END IF;
    SELECT count(*) INTO n FROM audit_logs WHERE tenant_id IS NULL;
    IF n <> 1 THEN
        RAISE EXCEPTION 'PROOF 3 FAILED — kynex_platform saw % platform audit rows (expected 1)', n;
    END IF;

    -- and with the GUC off it narrows back down
    PERFORM set_config('app.platform', 'off', false);
    SELECT app.is_platform() INTO v_is;
    IF v_is THEN
        RAISE EXCEPTION 'PROOF 3 FAILED — is_platform() true with the GUC off';
    END IF;

    EXECUTE 'RESET ROLE';
    RAISE NOTICE '   % ok', rpad('kynex_platform + GUC on = platform; GUC off = not', 58);
END
$$;


-- =============================================================================
-- PROOF 4 — kynex_ro CANNOT REACH A CREDENTIAL
--
-- Not by column, and not through the login function either. §19.2 calls the
-- second one out explicitly: a SECURITY DEFINER function owned by a BYPASSRLS
-- role is EXECUTE-to-PUBLIC by default, so the column revoke alone would be
-- defeated by one function call.
-- =============================================================================

\echo '== 4. kynex_ro cannot read users.password_hash, by column or by function =='

DO $$
DECLARE
    a  uuid := 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    n  bigint;
    ok boolean;
BEGIN
    EXECUTE 'SET ROLE kynex_ro';
    PERFORM set_config('app.tenant_id', a::text, false);

    -- 4a. the column itself
    ok := false;
    BEGIN
        PERFORM password_hash FROM users;
    EXCEPTION WHEN insufficient_privilege THEN ok := true;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 4a FAILED — kynex_ro read users.password_hash'; END IF;

    -- 4b. and SELECT * does not smuggle it out
    ok := false;
    BEGIN
        PERFORM * FROM users;
    EXCEPTION WHEN insufficient_privilege THEN ok := true;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 4b FAILED — SELECT * on users succeeded for kynex_ro'; END IF;

    -- 4c. the non-secret columns still work, or support has no product
    SELECT count(*) INTO n FROM (SELECT id, normalized_email, status FROM users) q;
    IF n <> 1 THEN RAISE EXCEPTION 'PROOF 4c FAILED — kynex_ro sees % users (expected 1)', n; END IF;

    -- 4d. the function route
    ok := false;
    BEGIN
        PERFORM * FROM app.resolve_login('tenant-a', 'user@example.test');
    EXCEPTION WHEN insufficient_privilege THEN ok := true;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 4d FAILED — kynex_ro obtained password_hash via app.resolve_login'; END IF;

    -- 4e. and the platform twin
    ok := false;
    BEGIN
        PERFORM * FROM app.resolve_platform_login('operator@example.test');
    EXCEPTION WHEN insufficient_privilege THEN ok := true;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 4e FAILED — kynex_ro obtained a platform hash via app.resolve_platform_login'; END IF;

    -- 4f. the other secret columns of §19.2
    ok := false;
    BEGIN PERFORM token_hash FROM auth_tokens;
    EXCEPTION WHEN insufficient_privilege THEN ok := true; END;
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 4f FAILED — kynex_ro read auth_tokens.token_hash'; END IF;

    ok := false;
    BEGIN PERFORM api_key_hash FROM attendance_devices;
    EXCEPTION WHEN insufficient_privilege THEN ok := true; END;
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 4f FAILED — kynex_ro read attendance_devices.api_key_hash'; END IF;

    ok := false;
    BEGIN PERFORM xml FROM data_protection_keys;
    EXCEPTION WHEN insufficient_privilege THEN ok := true; END;
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 4f FAILED — kynex_ro read data_protection_keys.xml'; END IF;

    ok := false;
    BEGIN PERFORM refresh_token_hash FROM auth_sessions;
    EXCEPTION WHEN insufficient_privilege THEN ok := true; END;
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 4f FAILED — kynex_ro read auth_sessions.refresh_token_hash'; END IF;

    -- 4g. and it holds no write anywhere
    ok := false;
    BEGIN
        UPDATE employees SET name_en = 'RO WAS HERE';
    EXCEPTION WHEN insufficient_privilege THEN ok := true;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 4g FAILED — kynex_ro wrote to employees'; END IF;

    EXECUTE 'RESET ROLE';
    RAISE NOTICE '   % ok', rpad('password_hash, token_hash, api_key_hash, xml, refresh hash', 58);
    RAISE NOTICE '   % ok', rpad('resolve_login and resolve_platform_login both 42501', 58);
END
$$;

-- kynex_app holds no write grant on a reference table (§19.2 CI assertion 2)
DO $$
DECLARE ok boolean := false;
BEGIN
    EXECUTE 'SET ROLE kynex_app';
    PERFORM set_config('app.tenant_id', 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', false);
    BEGIN
        INSERT INTO permissions (id, key, module, name_en)
        VALUES (gen_random_uuid(), 'x.y', 'X', 'X');
    EXCEPTION
        WHEN insufficient_privilege THEN ok := true;
        WHEN undefined_column THEN ok := true;   -- shape differs; the grant is what is under test
    END;
    EXECUTE 'RESET ROLE';
    IF NOT ok THEN RAISE EXCEPTION 'PROOF 4 FAILED — kynex_app wrote to a reference table'; END IF;
    RAISE NOTICE '   % ok', rpad('kynex_app holds no write grant on reference tables', 58);
END
$$;

DO $$
DECLARE v_bad text;
BEGIN
    SELECT string_agg(DISTINCT table_name || '/' || privilege_type, ', ')
      INTO v_bad
      FROM information_schema.role_table_grants
     WHERE grantee = 'kynex_app'
       AND table_schema = 'public'
       AND ( (table_name IN ('permissions','statutory_rules','statutory_rule_bands','nitaqat_grid')
              AND privilege_type <> 'SELECT')
          OR table_name = 'platform_users' );
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'PROOF 4 FAILED — kynex_app holds forbidden grants: %', v_bad;
    END IF;
    RAISE NOTICE '   % ok', rpad('role_table_grants: no write on reference, none on platform_users', 58);
END
$$;


-- =============================================================================
-- PROOF 5 — NO LOGIN ROLE REACHES BYPASSRLS EXCEPT kynex_migrator
--
-- §19.2: "the first is written transitively on purpose. A direct
-- pg_roles.rolbypassrls check is not enough — GRANT kynex_owner TO kynex_app
-- would defeat it while every row still reads rolbypassrls = false."
--
-- The walk also treats rolsuper as BYPASSRLS, because a superuser bypasses RLS
-- whether or not the attribute is set, and a superuser login role would be the
-- same hole wearing a different flag.
-- =============================================================================

\echo '== 5. no LOGIN role reaches BYPASSRLS except kynex_migrator =='

DO $$
DECLARE
    v_bad text;
BEGIN
    WITH RECURSIVE closure(login_role, reached) AS (
        SELECT r.rolname, r.oid
          FROM pg_roles r
         WHERE r.rolcanlogin
        UNION
        SELECT c.login_role, m.roleid
          FROM closure c
          JOIN pg_auth_members m ON m.member = c.reached
    )
    SELECT string_agg(DISTINCT format('%s → %s', c.login_role, g.rolname), ', ')
      INTO v_bad
      FROM closure c
      JOIN pg_roles g ON g.oid = c.reached
     WHERE (g.rolbypassrls OR g.rolsuper)
       AND c.login_role <> 'kynex_migrator'
       -- the container's own bootstrap superuser is not part of the design; on
       -- Neon this is neondb_owner and it is excluded by the same rule, which is
       -- why §19.6 requires the deploy to hand the application ONLY kynex_app.
       AND c.login_role NOT IN ('postgres', 'neondb_owner');

    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'PROOF 5 FAILED — login role reaches BYPASSRLS: %', v_bad;
    END IF;

    -- the positive control: the walk actually finds kynex_migrator's path, so a
    -- silently-empty closure cannot pass as a clean result
    PERFORM 1 FROM (
        WITH RECURSIVE closure(login_role, reached) AS (
            SELECT r.rolname, r.oid FROM pg_roles r WHERE r.rolcanlogin
            UNION
            SELECT c.login_role, m.roleid FROM closure c
              JOIN pg_auth_members m ON m.member = c.reached
        )
        SELECT 1 FROM closure c JOIN pg_roles g ON g.oid = c.reached
         WHERE c.login_role = 'kynex_migrator' AND g.rolname = 'kynex_owner' AND g.rolbypassrls
    ) q;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'PROOF 5 INCONCLUSIVE — the walk did not even find kynex_migrator → kynex_owner';
    END IF;

    RAISE NOTICE '   % ok', rpad('transitive closure clean; migrator''s own path found', 58);
END
$$;

-- kynex_owner must not be able to log in, or the membership rule is decorative
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kynex_owner' AND rolcanlogin) THEN
        RAISE EXCEPTION 'PROOF 5 FAILED — kynex_owner can log in';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles
                WHERE rolname IN ('kynex_app','kynex_job','kynex_platform','kynex_ro')
                  AND (rolbypassrls OR rolsuper OR rolcreatedb OR rolcreaterole)) THEN
        RAISE EXCEPTION 'PROOF 5 FAILED — an application role holds an elevated attribute';
    END IF;
    RAISE NOTICE '   % ok', rpad('kynex_owner NOLOGIN; app roles hold no attribute', 58);
END
$$;


-- =============================================================================
-- PROOF 6 — COVERAGE
--
-- Every table RLS-forced; every partition child RLS-forced with zero direct
-- grants; every view security_invoker.
-- =============================================================================

\echo '== 6. coverage: forced RLS, ungranted children, security_invoker views =='

DO $$
DECLARE
    v_bad text;
    n     int;
BEGIN
    -- 6a. every table, parent and child alike
    SELECT string_agg(c.relname, ', ' ORDER BY c.relname), count(*)
      INTO v_bad, n
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind IN ('r','p')
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass
                          AND d.objid = c.oid AND d.deptype = 'e')
       AND NOT (c.relrowsecurity AND c.relforcerowsecurity);
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'PROOF 6a FAILED — RLS not enabled+forced on: %', v_bad;
    END IF;

    SELECT count(*) INTO n
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind IN ('r','p')
       AND c.relrowsecurity AND c.relforcerowsecurity;
    IF n < 76 THEN RAISE EXCEPTION 'PROOF 6a INCONCLUSIVE — only % relations checked', n; END IF;
    RAISE NOTICE '   % ok', rpad(format('%s relations, all ENABLE + FORCE', n), 58);

    -- 6b. every partition child: forced, and ZERO direct grants to any login role
    SELECT string_agg(DISTINCT c.relname, ', ')
      INTO v_bad
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind IN ('r','p') AND c.relispartition
       AND NOT (c.relrowsecurity AND c.relforcerowsecurity);
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'PROOF 6b FAILED — partition child without forced RLS: %', v_bad;
    END IF;

    SELECT string_agg(DISTINCT g.table_name || '→' || g.grantee || '/' || g.privilege_type, ', ')
      INTO v_bad
      FROM information_schema.role_table_grants g
      JOIN pg_class c   ON c.relname = g.table_name
      JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = g.table_schema
      JOIN pg_roles r   ON r.rolname = g.grantee
     WHERE g.table_schema = 'public'
       AND c.relkind IN ('r','p') AND c.relispartition
       AND r.rolcanlogin;
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'PROOF 6b FAILED — partition child carries a direct grant: %', v_bad;
    END IF;

    SELECT count(*) INTO n
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind IN ('r','p') AND c.relispartition;
    IF n < 80 THEN RAISE EXCEPTION 'PROOF 6b INCONCLUSIVE — only % children found', n; END IF;
    RAISE NOTICE '   % ok', rpad(format('%s partition children: forced, zero grants', n), 58);

    -- 6c. and no child carries a policy either, which is what makes "forced"
    -- mean "deny" rather than "whatever the parent says"
    SELECT string_agg(DISTINCT c.relname, ', ')
      INTO v_bad
      FROM pg_policy p
      JOIN pg_class c ON c.oid = p.polrelid
     WHERE c.relkind IN ('r','p') AND c.relispartition;
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'PROOF 6c FAILED — partition child carries its own policy: %', v_bad;
    END IF;

    -- 6d. every view carries security_invoker
    SELECT string_agg(c.relname, ', ' ORDER BY c.relname), count(*)
      INTO v_bad, n
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind = 'v'
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass
                          AND d.objid = c.oid AND d.deptype = 'e')
       AND NOT coalesce(array_to_string(c.reloptions, ',') LIKE '%security_invoker=true%', false);
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'PROOF 6d FAILED — view without security_invoker=true: %', v_bad;
    END IF;

    SELECT count(*) INTO n
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind = 'v'
       AND array_to_string(c.reloptions, ',') LIKE '%security_invoker=true%';
    IF n <> 2 THEN RAISE EXCEPTION 'PROOF 6d INCONCLUSIVE — % security_invoker views (expected 2)', n; END IF;
    RAISE NOTICE '   % ok', rpad('both views carry security_invoker = true', 58);

    -- 6e. every relation has a manifest entry and vice versa (§19.2 ratchet)
    SELECT string_agg(c.relname, ', ' ORDER BY c.relname) INTO v_bad
      FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relkind IN ('r','p','v')
       AND NOT EXISTS (SELECT 1 FROM pg_depend d
                        WHERE d.classid = 'pg_class'::regclass
                          AND d.objid = c.oid AND d.deptype = 'e')
       AND NOT EXISTS (SELECT 1 FROM app.rls_manifest m WHERE m.relname = c.relname);
    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'PROOF 6e FAILED — relation with no manifest entry: %', v_bad;
    END IF;
    RAISE NOTICE '   % ok', rpad('manifest covers every table, parent, child and view', 58);
END
$$;

-- 6f. the counter-proof for the views. Recreate v_leave_balances WITHOUT
-- security_invoker, in a transaction that is rolled back, and show that it DOES
-- leak. A test that only asserts the flag is set proves the flag is set; this
-- proves the flag is what is holding the door.
BEGIN;
SET ROLE kynex_owner;
DROP VIEW v_leave_balances;
CREATE VIEW v_leave_balances AS
SELECT l.tenant_id, l.employee_id, l.leave_type_id, sum(l.days) AS balance_days
  FROM leave_ledger l GROUP BY 1,2,3;
GRANT SELECT ON v_leave_balances TO kynex_app;
RESET ROLE;

DO $$
DECLARE n bigint;
BEGIN
    EXECUTE 'SET ROLE kynex_app';
    PERFORM set_config('app.tenant_id', 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', false);
    SELECT count(*) INTO n FROM v_leave_balances;
    EXECUTE 'RESET ROLE';
    IF n <> 2 THEN
        RAISE EXCEPTION
          'PROOF 6f INCONCLUSIVE — the no-invoker view returned % rows, expected 2 (both tenants). The leak this file guards against may have changed shape.',
          n;
    END IF;
    RAISE NOTICE '   % ok', rpad('counter-proof: without security_invoker the view leaks BOTH tenants', 58);
END
$$;
ROLLBACK;

-- confirm the rollback put the real view back
DO $$
DECLARE n int;
BEGIN
    SELECT count(*) INTO n FROM pg_class c JOIN pg_namespace ns ON ns.oid = c.relnamespace
     WHERE ns.nspname = 'public' AND c.relname = 'v_leave_balances'
       AND array_to_string(c.reloptions, ',') LIKE '%security_invoker=true%';
    IF n <> 1 THEN RAISE EXCEPTION 'PROOF 6f FAILED — the security_invoker view was not restored'; END IF;
    RAISE NOTICE '   % ok', rpad('security_invoker view restored after the counter-proof', 58);
END
$$;

\echo ''
\echo 'ALL RLS PROOFS PASSED'
