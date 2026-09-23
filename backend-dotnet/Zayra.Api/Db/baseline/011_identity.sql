-- =============================================================================
-- 011_identity.sql
-- Domain B — Identity and access (8 tables).
-- TARGET_SCHEMA.md revision 6 §2.B; CONVENTIONS.md §1–§10.
--
-- CREATE TABLE only. FKs/CHECKs/EXCLUDEs live in 020_constraints_a_f.sql.
-- No table in domain B is partitioned (§19.3).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- users — tier T. Every TENANT login (staff and ESS employees).
-- `tenant_id` is NOT NULL: operators live in platform_users (§5 decision 2).
-- -----------------------------------------------------------------------------
CREATE TABLE users (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    employee_id             uuid,
    normalized_email        text            NOT NULL,
    status                  varchar(40)     NOT NULL DEFAULT 'Invited',
    password_hash           text,
    mfa_enabled             boolean         NOT NULL DEFAULT false,
    mfa_secret_encrypted    text,
    mfa_recovery_hashes     jsonb,
    failed_login_count      integer         NOT NULL DEFAULT 0,
    lockout_end             timestamptz,
    notification_prefs      jsonb           NOT NULL DEFAULT '{}'::jsonb,
    last_login_at           timestamptz,
    deleted_at              timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_users PRIMARY KEY (id),
    CONSTRAINT uq_users__tenant_id_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_users__tenant_id_normalized_email UNIQUE (tenant_id, normalized_email),
    -- §2.B: employee_id is "nullable, unique". Tenant-scoped, NULLs distinct, so many
    -- staff users without an employee record coexist but one employee has one login.
    CONSTRAINT uq_users__tenant_id_employee_id UNIQUE (tenant_id, employee_id)
);
COMMENT ON TABLE users IS
  'Every tenant-side login — staff and employee self-service — with its credential, MFA and lockout state, optionally bound one-to-one to an employee record. @tier:T @owner:Platform @retention:soft-delete-only';
COMMENT ON COLUMN users.normalized_email IS
  'Unique per tenant, NOT globally: two tenants may legitimately share an address, which is why app.resolve_login() is keyed on (tenant, email) (§19.2 bypass surface 1).';
COMMENT ON COLUMN users.password_hash IS
  'Secret column: REVOKE from kynex_ro by column privilege (§19.2). NULL until an invitation is consumed.';

-- -----------------------------------------------------------------------------
-- roles — tier T. Seeded per tenant at provisioning.
-- -----------------------------------------------------------------------------
CREATE TABLE roles (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    code                    varchar(64)     NOT NULL,
    name                    text            NOT NULL,
    is_system               boolean         NOT NULL DEFAULT false,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_roles PRIMARY KEY (id),
    CONSTRAINT uq_roles__tenant_id_id UNIQUE (tenant_id, id),
    -- [DESIGN GAP] §2.B names `code` as a key column but states no uniqueness.
    -- Added: a role code that repeats inside a tenant cannot be resolved by code.
    CONSTRAINT uq_roles__tenant_id_code UNIQUE (tenant_id, code)
);
COMMENT ON TABLE roles IS
  'Tenant-defined and system-seeded roles; a per-user permission override is modelled as a custom role rather than its own table (§5 decision 5). @tier:T @owner:Platform';

-- -----------------------------------------------------------------------------
-- permissions — tier R. Platform reference catalogue, read-only to tenants.
-- Reference tables take the row-stamp exemption (§Conventions): created_at only.
-- -----------------------------------------------------------------------------
CREATE TABLE permissions (
    id                      uuid            NOT NULL,
    code                    varchar(64)     NOT NULL,
    module                  varchar(40)     NOT NULL,
    description             text,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    CONSTRAINT pk_permissions PRIMARY KEY (id),
    -- FK target for role_permissions.permission_code (§8 row 11).
    CONSTRAINT uq_permissions__code UNIQUE (code)
);
COMMENT ON TABLE permissions IS
  'The platform permission catalogue, including the access.grant.* keys that govern delegated granting authority; retiring a permission is a migration, never a delete. @tier:R @owner:Platform @retention:Keep';

-- -----------------------------------------------------------------------------
-- role_permissions — tier T.
-- -----------------------------------------------------------------------------
CREATE TABLE role_permissions (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    role_id                 uuid            NOT NULL,
    permission_code         varchar(64)     NOT NULL,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_role_permissions PRIMARY KEY (id),
    CONSTRAINT uq_role_permissions__tenant_id_id UNIQUE (tenant_id, id),
    -- [DESIGN GAP] Not stated in §2.B. Added: the join row has no meaning twice.
    CONSTRAINT uq_role_permissions__role_id_permission_code UNIQUE (tenant_id, role_id, permission_code)
);
COMMENT ON TABLE role_permissions IS
  'Grants one catalogue permission to one tenant role; the join that turns a role into an authorisation decision. @tier:T @owner:Platform';

-- -----------------------------------------------------------------------------
-- user_roles — tier T. A role grant with an optional data scope and an expiry.
-- -----------------------------------------------------------------------------
CREATE TABLE user_roles (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    user_id                 uuid            NOT NULL,
    role_id                 uuid            NOT NULL,
    scope_company_id        uuid,
    scope_branch_id         uuid,
    scope_department_id     uuid,
    granted_by              uuid,
    granted_at              timestamptz     NOT NULL DEFAULT now(),
    expires_at              timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_user_roles PRIMARY KEY (id),
    CONSTRAINT uq_user_roles__tenant_id_id UNIQUE (tenant_id, id)
    -- No natural UNIQUE: §2.B allows the same role granted twice at different scopes,
    -- and the design states none. Deliberately left unconstrained.
);
COMMENT ON TABLE user_roles IS
  'Grants a role to a user, optionally narrowed to one company, branch or department and optionally time-boxed; NULL scope columns mean the whole tenant. @tier:T @owner:Platform';
COMMENT ON COLUMN user_roles.granted_by IS
  'Who made THIS grant. Who MAY grant is a different fact and lives in permission_grantor_records (§2.B).';

-- -----------------------------------------------------------------------------
-- permission_grantor_records — tier T. Delegated granting authority (§F5 decision).
--
-- DESIGN INCONSISTENCY (reported): §2.B models the lifecycle as `is_active` +
-- revoked_at/revoked_by, while §10.11 describes a three-state machine
-- (Active -> Revoked / Expired) and §12.1 restricts `is_active` to seven named
-- catalogues, of which this is not one. §2's explicit column list is followed here;
-- Expired is derivable from expires_at, so no information is lost, but a reader
-- expecting a `status` column will not find one.
-- -----------------------------------------------------------------------------
CREATE TABLE permission_grantor_records (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    grantor_user_id         uuid            NOT NULL,
    granted_by_user_id      uuid,
    revoked_by              uuid,
    permission_scope        text            NOT NULL,
    can_sub_delegate        boolean         NOT NULL DEFAULT false,
    is_active               boolean         NOT NULL DEFAULT true,
    reason                  text,
    expires_at              timestamptz,
    revoked_at              timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_permission_grantor_records PRIMARY KEY (id),
    CONSTRAINT uq_permission_grantor_records__tenant_id_id UNIQUE (tenant_id, id)
);
COMMENT ON TABLE permission_grantor_records IS
  'Records that a named user MAY grant permissions over a stated scope, with or without sub-delegation, until a date and for a reason — the authority behind a grant, not the grant itself. @tier:T @owner:Platform';
COMMENT ON COLUMN permission_grantor_records.permission_scope IS
  '''all'', a module prefix, or an explicit key list. Sub-delegation may only narrow the parent scope (§10.11), checked in AccessManagementService.';

-- -----------------------------------------------------------------------------
-- auth_sessions — tier T/P. ONE ROW PER DEVICE, not per login.
-- One of only two tables with a nullable tenant_id (§3, §19.2 policy p_auth).
-- -----------------------------------------------------------------------------
CREATE TABLE auth_sessions (
    id                      uuid            NOT NULL,
    tenant_id               uuid,
    user_id                 uuid,
    platform_user_id        uuid,
    device_id               varchar(128)    NOT NULL,
    subject_kind            varchar(40)     NOT NULL,
    refresh_token_hash      text            NOT NULL,
    previous_token_hash     text,
    push_token              text,
    push_platform           varchar(40),
    ip                      text,
    user_agent              text,
    last_seen_at            timestamptz,
    expires_at              timestamptz     NOT NULL,
    revoked_at              timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_auth_sessions PRIMARY KEY (id),
    CONSTRAINT uq_auth_sessions__tenant_id_id UNIQUE NULLS NOT DISTINCT (tenant_id, id)
);
-- §2.B: UNIQUE (subject_kind, COALESCE(user_id, platform_user_id), device_id).
-- An expression key cannot be a table constraint, so it is a unique INDEX. This is a
-- uniqueness rule from the design, not an access-path index, so it belongs here rather
-- than in the 040 index pass.
CREATE UNIQUE INDEX uq_auth_sessions__subject_device
    ON auth_sessions (subject_kind, COALESCE(user_id, platform_user_id), device_id);
COMMENT ON TABLE auth_sessions IS
  'One row per signed-in device for either subject kind, carrying the rotated refresh token, the previous hash for reuse detection and the push registration, so re-login never loses a device. @tier:T/P @owner:Platform @retention:1-month-after-Expiry-then-Purge';
COMMENT ON COLUMN auth_sessions.tenant_id IS
  'NULL only for subject_kind=''Platform''. One of exactly two nullable-tenant client-adjacent tables; policed by the hand-written p_auth policy (§19.2).';
COMMENT ON COLUMN auth_sessions.previous_token_hash IS
  'Presented-again detection: a refresh with this hash means the token was replayed, and the whole session is revoked.';

-- -----------------------------------------------------------------------------
-- auth_tokens — tier T/P. Single-use tokens for both subject kinds.
-- -----------------------------------------------------------------------------
CREATE TABLE auth_tokens (
    id                      uuid            NOT NULL,
    tenant_id               uuid,
    user_id                 uuid,
    platform_user_id        uuid,
    token_hash              text            NOT NULL,
    subject_kind            varchar(40)     NOT NULL,
    purpose                 varchar(40)     NOT NULL,
    attempts                integer         NOT NULL DEFAULT 0,
    expires_at              timestamptz     NOT NULL,
    consumed_at             timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_auth_tokens PRIMARY KEY (id),
    CONSTRAINT uq_auth_tokens__tenant_id_id UNIQUE NULLS NOT DISTINCT (tenant_id, id),
    CONSTRAINT uq_auth_tokens__token_hash UNIQUE (token_hash)
);
COMMENT ON TABLE auth_tokens IS
  'Single-use, expiring tokens for password reset, MFA challenge, invitation and email confirmation, for either subject kind, with an attempt counter that supports lockout. @tier:T/P @owner:Platform @retention:1-month-after-Expiry-then-Purge';
COMMENT ON COLUMN auth_tokens.token_hash IS
  'Secret column: REVOKE from kynex_ro by column privilege (§19.2). Lowercase hex; the plaintext token is never stored.';
