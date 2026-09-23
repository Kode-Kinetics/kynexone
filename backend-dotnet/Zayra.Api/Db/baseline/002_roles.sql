-- =============================================================================
-- 002_roles.sql
-- KynexOne baseline — roles, the `app` schema and the GUC accessors.
--
-- Source of truth: TARGET_SCHEMA.md revision 6 §19.2; CONVENTIONS.md §12.
--
-- BOOTSTRAP FILE. This is the one file in the baseline that is NOT applied by
-- kynex_migrator, because it is the file that creates kynex_migrator. It is run
-- once, by the cluster's bootstrap superuser-class role (locally `postgres`, on
-- Neon `neondb_owner`), which must itself hold CREATEROLE and BYPASSRLS —
-- PostgreSQL will not let a role grant an attribute it does not hold, so a
-- bootstrap role without BYPASSRLS cannot create kynex_owner. Every later file
-- (010 … 070) is applied by kynex_migrator with `SET ROLE kynex_owner`, which is
-- what makes kynex_owner the owner of every object.
--
-- Idempotent: safe to re-run. CREATE ROLE has no IF NOT EXISTS, so each role is
-- created inside a DO block that checks pg_roles first.
--
-- Scope of this file: roles, grants that are not table grants (those live in
-- 060_policies.sql), schema `app`, and the four functions §19.2 names. It
-- deliberately creates NO table, index or trigger.
-- =============================================================================


-- -----------------------------------------------------------------------------
-- 1. THE SIX ROLES
--
-- §19.2 names five and then describes a sixth, kynex_migrator, in the same
-- table. The count of "five roles" in §19.1's file-layout row is the pre-
-- migrator count and is stale; six roles are created here.
--
-- Only kynex_migrator, kynex_app, kynex_job, kynex_platform and kynex_ro may
-- log in. kynex_owner deliberately cannot: it is reached only by membership, so
-- revoking one GRANT disarms the only path to BYPASSRLS.
--
-- Passwords are NOT set here. The deploy pipeline sets them out of band
-- (`ALTER ROLE … PASSWORD`) from the secret store, so no credential is ever
-- committed and re-running this file cannot reset a live password.
-- -----------------------------------------------------------------------------

DO $$
BEGIN
    -- kynex_owner — owns every object; the only role with DDL; BYPASSRLS; NOLOGIN.
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kynex_owner') THEN
        CREATE ROLE kynex_owner NOLOGIN NOINHERIT BYPASSRLS;
    END IF;

    -- kynex_migrator — applies the baseline and every migration. No privilege of
    -- its own; its entire authority is the membership granted below. LOGIN.
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kynex_migrator') THEN
        CREATE ROLE kynex_migrator LOGIN NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;

    -- kynex_app — the API.
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kynex_app') THEN
        CREATE ROLE kynex_app LOGIN NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;

    -- kynex_job — the seven background services.
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kynex_job') THEN
        CREATE ROLE kynex_job LOGIN NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;

    -- kynex_platform — the platform/tenant-admin surface. Membership in this
    -- role is the ONLY thing that makes app.is_platform() capable of returning
    -- true; the GUC alone can never grant it (§19.2).
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kynex_platform') THEN
        CREATE ROLE kynex_platform LOGIN NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;

    -- kynex_ro — support and analytics. SELECT only, and not on the secret
    -- columns of §19.2 (the column-level grants are issued in 060_policies.sql).
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kynex_ro') THEN
        CREATE ROLE kynex_ro LOGIN NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END
$$;

-- The one membership in the design. WITH INHERIT TRUE so `dotnet ef database
-- update` needs no SET ROLE to hold DDL privilege, and WITH SET TRUE so an
-- operator can still SET ROLE kynex_owner explicitly.
--
-- CI assertion 1 (§19.2) walks pg_auth_members recursively from every
-- rolcanlogin role and fails if any closure but kynex_migrator's reaches a
-- BYPASSRLS role. This is the only GRANT in the baseline that puts a login role
-- in such a closure; tests/070_rls_proof.sql asserts there is no other.
GRANT kynex_owner TO kynex_migrator;

COMMENT ON ROLE kynex_owner     IS 'Owns every object; BYPASSRLS; the only role with DDL; NOLOGIN so it is reachable only by membership (§19.2).';
COMMENT ON ROLE kynex_migrator  IS 'The only LOGIN role that reaches BYPASSRLS, and only by its membership in kynex_owner. Credentials live in the CI/deploy secret store, never in an application environment (§19.2).';
COMMENT ON ROLE kynex_app       IS 'The API. NOBYPASSRLS; DML on tenant tables; SELECT only on reference tables; no grant at all on platform_users (§19.2).';
COMMENT ON ROLE kynex_job       IS 'The background services. As kynex_app, plus its own policy on background_jobs/background_job_items for the leased queue (§19.2).';
COMMENT ON ROLE kynex_platform  IS 'The platform/tenant-admin surface. The only role for which app.is_platform() can return true (§19.2).';
COMMENT ON ROLE kynex_ro        IS 'Support and analytics. SELECT only, withheld on the secret columns of §19.2 by column-level grant.';


-- -----------------------------------------------------------------------------
-- 2. SCHEMA PRIVILEGE BASELINE
--
-- PUBLIC loses everything on `public`. In PostgreSQL 15+ PUBLIC already has no
-- CREATE there, but it still has USAGE, and USAGE on the schema is what lets a
-- role name a table at all. Revoking it means a future role starts with nothing
-- and is granted forward, rather than starting with ambient reach.
-- -----------------------------------------------------------------------------

REVOKE ALL ON SCHEMA public FROM PUBLIC;

-- kynex_owner needs CREATE on `public` to apply 010 … 070. Granting CREATE is
-- preferred over ALTER SCHEMA public OWNER TO kynex_owner because the latter
-- needs ownership of the schema, which the Neon bootstrap role does not reliably
-- hold, and because leaving pg_database_owner as the owner keeps `DROP SCHEMA
-- public` out of kynex_owner's reach.
GRANT USAGE, CREATE ON SCHEMA public TO kynex_owner;
GRANT USAGE ON SCHEMA public TO kynex_app, kynex_job, kynex_platform, kynex_ro, kynex_migrator;

-- `app` holds the GUC accessors, the login bypass surface and the partition
-- maintenance template. Owned by kynex_owner; no CREATE for anyone else.
CREATE SCHEMA IF NOT EXISTS app AUTHORIZATION kynex_owner;
REVOKE ALL ON SCHEMA app FROM PUBLIC;
GRANT USAGE ON SCHEMA app TO kynex_app, kynex_job, kynex_platform, kynex_ro, kynex_migrator;

COMMENT ON SCHEMA app IS
  'Security surface: the GUC accessors every RLS policy calls, the four named bypass surfaces of §19.2, and the partition-maintenance template of §19.3. Contains no business table.';

-- Every object 010 … 070 creates is owned by kynex_owner because kynex_migrator
-- applies them with SET ROLE kynex_owner. These default ACLs cover anything a
-- later migration creates without an explicit grant: nothing is granted by
-- default, so a new table is invisible until 060's generator gives it a policy
-- and a grant. Fail-closed by absence, which is the same property the
-- policy-coverage ratchet asserts (§19.1 CI gate 2).
ALTER DEFAULT PRIVILEGES FOR ROLE kynex_owner IN SCHEMA public
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE kynex_owner IN SCHEMA public
    REVOKE ALL ON SEQUENCES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE kynex_owner IN SCHEMA app
    REVOKE ALL ON FUNCTIONS FROM PUBLIC;


-- -----------------------------------------------------------------------------
-- 3. GUC ACCESSORS (§19.2)
--
-- STABLE PARALLEL SAFE, never SECURITY DEFINER. STABLE is load-bearing, not
-- decoration: the planner folds the call to a constant once per statement, so
-- the policy predicate becomes `tenant_id = <const>` and still chooses the
-- tenant-leading index of §19.4. A VOLATILE accessor would be re-evaluated per
-- row and would cost a sequential scan on every RLS-filtered table.
--
-- current_setting(…, true) returns NULL for an unset GUC instead of raising,
-- which is what makes the unset case fail CLOSED rather than fail LOUD in a
-- place the application cannot catch.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION app.current_tenant() RETURNS uuid
    LANGUAGE sql
    STABLE
    PARALLEL SAFE
AS $$
    SELECT nullif(current_setting('app.tenant_id', true), '')::uuid
$$;

COMMENT ON FUNCTION app.current_tenant() IS
  'The tenant this session acts for, or NULL when unset. Self-assertable BY DESIGN (§19.2): a session may choose which tenant it acts for, and it is the JWT-bound middleware and the DbConnectionInterceptor that bind the choice to the authenticated principal. A malformed GUC raises on the cast, which is a refusal, not a leak.';


-- app.is_platform() ANDs the GUC with real role membership.
--
-- WHY THE AND IS NOT OPTIONAL: PostgreSQL does not restrict `SET` on a *custom*
-- GUC by role. Any session, including kynex_app, can run `SET app.platform =
-- 'on'`. A GUC-only is_platform() would therefore hand every authenticated API
-- connection the platform tier — the nullable-tenant rows of shape (b), the
-- operator list in platform_users, and every platform auth_session. ANDing with
-- pg_has_role means the GUC can only ever NARROW an authority the role already
-- holds, never confer one.
--
-- 'MEMBER' rather than 'USAGE' on purpose: USAGE is false for a NOINHERIT
-- membership, so a future operator role granted kynex_platform WITH INHERIT
-- FALSE would silently lose the platform tier after a SET ROLE. MEMBER is true
-- in both cases and is the question actually being asked — "is this principal
-- permitted to be kynex_platform" — while the GUC supplies "is it acting as
-- kynex_platform right now".
--
-- NOT SECURITY DEFINER, deliberately: current_user inside a SECURITY DEFINER
-- function is the DEFINER (kynex_owner), so pg_has_role would be answered about
-- the wrong principal and, kynex_owner being superuser-adjacent in the
-- membership graph, could answer true for everyone.
CREATE OR REPLACE FUNCTION app.is_platform() RETURNS boolean
    LANGUAGE sql
    STABLE
    PARALLEL SAFE
AS $$
    SELECT coalesce(current_setting('app.platform', true), 'off') = 'on'
       AND pg_has_role(current_user, 'kynex_platform', 'MEMBER')
$$;

COMMENT ON FUNCTION app.is_platform() IS
  'True only when the session has BOTH set app.platform=''on'' AND holds membership in kynex_platform. The GUC can only narrow an authority the role already has; it can never confer one, which is what stops a kynex_app session from promoting itself (§19.2).';

-- Both accessors keep EXECUTE for PUBLIC. They must: a policy predicate is
-- evaluated with the *caller's* privileges, so revoking EXECUTE would make every
-- policy raise 42501 for every role. Neither is SECURITY DEFINER, neither reads
-- a table, and neither can return more than the caller's own session state, so
-- PUBLIC EXECUTE grants nothing. §19.2's CI assertion 3 is scoped to SECURITY
-- DEFINER functions for exactly this reason.


-- -----------------------------------------------------------------------------
-- 4. BYPASS SURFACE 1 — LOGIN AND CREDENTIAL RECOVERY (§19.2)
--
-- The only path that must read `users` / `platform_users` before a tenant is
-- known. One SECURITY DEFINER function per subject kind, and nothing else.
--
-- Three properties §19.2 requires, all present below:
--  (i)   keyed on (tenant_slug, email), not email alone — email is unique PER
--        TENANT (uq_users__tenant_id_normalized_email), so an email-only key is
--        ambiguous the moment two tenants share an address;
--  (ii)  REVOKE EXECUTE FROM PUBLIC, GRANT to kynex_app only — a SECURITY
--        DEFINER function owned by a BYPASSRLS role is EXECUTE-to-PUBLIC by
--        default, so kynex_ro could otherwise call it and receive password_hash,
--        defeating the column revoke entirely;
--  (iii) SET search_path = pg_catalog, app so a shadowing object in a
--        caller-controlled schema cannot hijack the body.
--
-- ADDED BEYOND §19.2, and reported: the tenant-confinement guard. §19.2 as
-- written leaves resolve_login callable by an ALREADY-authenticated kynex_app
-- session against ANY tenant slug, which is a cross-tenant credential read
-- through a SECURITY DEFINER function — the one hole the RLS work would
-- otherwise leave open. The guard permits the call only when the session is not
-- yet bound to a tenant (the real login path, where app.tenant_id is unset) or
-- when the requested tenant IS the session's tenant (self-service re-auth). It
-- costs the login path nothing, because the login path has no tenant yet.
--
-- SET search_path = pg_catalog, app: `users`, `tenants` and `platform_users`
-- live in `public`, which is NOT on that path, so every reference to them is
-- schema-qualified below. That is the point of the restricted path.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION app.resolve_login(
        p_tenant_slug text,
        p_email       text)
    RETURNS TABLE (
        user_id       uuid,
        tenant_id     uuid,
        password_hash text,
        status        varchar(40),
        lockout_end   timestamptz)
    LANGUAGE plpgsql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, app
AS $$
DECLARE
    v_tenant_id uuid;
BEGIN
    IF p_tenant_slug IS NULL OR p_email IS NULL THEN
        RETURN;
    END IF;

    SELECT t.id INTO v_tenant_id
      FROM public.tenants t
     WHERE t.slug = lower(btrim(p_tenant_slug))
       AND t.status = 'Active'
       AND t.soft_deleted_at IS NULL
       AND t.purged_at IS NULL;

    IF v_tenant_id IS NULL THEN
        RETURN;
    END IF;

    -- Tenant confinement. A session already bound to tenant A may resolve a
    -- login only for tenant A. An unbound session (the login path) may resolve
    -- any tenant, which is unavoidable: the tenant is not known until this call
    -- returns. 42501 rather than an empty result, so the attempt is an error the
    -- application logs rather than a silent miss indistinguishable from a typo.
    IF app.current_tenant() IS NOT NULL AND app.current_tenant() <> v_tenant_id THEN
        RAISE EXCEPTION
            'app.resolve_login: session is bound to another tenant'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    RETURN QUERY
    SELECT u.id, u.tenant_id, u.password_hash, u.status, u.lockout_end
      FROM public.users u
     WHERE u.tenant_id = v_tenant_id
       AND u.normalized_email = lower(btrim(p_email))
       AND u.deleted_at IS NULL;
END
$$;

REVOKE ALL ON FUNCTION app.resolve_login(text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.resolve_login(text, text) TO kynex_app;

COMMENT ON FUNCTION app.resolve_login(text, text) IS
  'Named bypass surface 1 (§19.2): the ONLY way to read a tenant credential before a tenant is known. Keyed on (tenant_slug, email) because email is unique per tenant, not globally. EXECUTE revoked from PUBLIC and granted to kynex_app alone, so kynex_ro cannot obtain password_hash through it. Refuses with 42501 when the session is already bound to a different tenant.';


-- The platform twin, keyed on email alone — platform_users.email IS globally
-- unique (uq_platform_users__email), there being no tenant to scope it by.
-- Granted to kynex_app because the platform sign-in form is served by the same
-- API process before any role switch has happened; the row it returns is a
-- credential hash for an email the caller already supplied, and every other
-- read of platform_users is withheld by grant (060_policies.sql).
CREATE OR REPLACE FUNCTION app.resolve_platform_login(
        p_email text)
    RETURNS TABLE (
        platform_user_id uuid,
        password_hash    text,
        status           varchar(40),
        platform_role    varchar(40),
        lockout_end      timestamptz)
    LANGUAGE plpgsql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog, app
AS $$
BEGIN
    IF p_email IS NULL THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT p.id, p.password_hash, p.status, p.platform_role, p.lockout_end
      FROM public.platform_users p
     WHERE p.email = lower(btrim(p_email))
       AND p.deleted_at IS NULL;
END
$$;

REVOKE ALL ON FUNCTION app.resolve_platform_login(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.resolve_platform_login(text) TO kynex_app;

COMMENT ON FUNCTION app.resolve_platform_login(text) IS
  'Named bypass surface 1, platform twin (§19.2). Keyed on email alone because platform_users.email is globally unique. EXECUTE revoked from PUBLIC; granted to kynex_app only.';
