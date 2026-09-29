-- =============================================================================
-- 010_platform.sql
-- Domain A — Platform, tenancy and shared infrastructure (7 tables).
-- TARGET_SCHEMA.md revision 6 §2.A; CONVENTIONS.md §1–§10.
--
-- Scope of this file: CREATE TABLE only — columns, PRIMARY KEY, UNIQUE.
--   * FOREIGN KEY, CHECK and EXCLUDE constraints are in 020_constraints_a_f.sql
--     (TARGET_SCHEMA.md §19.1 puts them there).
--   * No policies, no partitions, no indexes beyond PK/UNIQUE — later passes.
--   * No table in domain A is partitioned (§19.3 partitions only attendance_punches,
--     audit_logs, background_job_items, attendance_days, timesheet_entries — all G–R).
--
-- Column order follows CONVENTIONS.md §10:
--   identity · tenancy · parent FKs · natural key · discriminators · effective dating ·
--   payload · money · snapshot · provenance · json · lifecycle timestamps ·
--   soft delete/retention · audit.
-- Bounded text lengths are from TARGET_SCHEMA.md §13.3 (status/kind columns = 40).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- tenants — tier P (platform: no tenant_id). Soft delete per §12.1.
-- -----------------------------------------------------------------------------
CREATE TABLE tenants (
    id                      uuid            NOT NULL,
    slug                    varchar(63)     NOT NULL,
    status                  varchar(40)     NOT NULL DEFAULT 'Active',
    name                    text            NOT NULL,
    timezone_id             varchar(64)     NOT NULL DEFAULT 'Asia/Riyadh',
    plan_code               varchar(40),
    plan_expires_at         timestamptz,
    enabled_modules         text[]          NOT NULL DEFAULT '{}',
    plan_limits             jsonb           NOT NULL DEFAULT '{}'::jsonb,
    soft_deleted_at         timestamptz,
    purged_at               timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_tenants PRIMARY KEY (id),
    CONSTRAINT uq_tenants__slug UNIQUE (slug)
);
COMMENT ON TABLE tenants IS
  'Customer account root: identity, plan gating, seat and module limits, timezone anchor and the tenancy soft-delete/purge lifecycle. @tier:P @owner:Platform @retention:3-months-after-SoftDelete-then-Purge';
COMMENT ON COLUMN tenants.plan_limits IS
  'Authoritative seat/module limits (max_employees, max_users, max_companies). Nothing caches a seat count (§11.6).';
COMMENT ON COLUMN tenants.timezone_id IS
  'IANA id, validated against the runtime tz database on write. Anchors every business day (§13.4).';

-- -----------------------------------------------------------------------------
-- tenant_settings — tier T. One row per tenant, written per section.
--
-- DESIGN CONTRADICTION (resolved, reported): §2.A lists the key as "tenant_id (PK)",
-- but CONVENTIONS.md §2 forbids a composite/natural PK and requires `id uuid` on every
-- table, and §3 requires `UNIQUE (tenant_id, id)` on every tenant-tier table.
-- Resolved by keeping `id uuid` as the PK and enforcing the 1:1 with a separate
-- UNIQUE (tenant_id), which satisfies both rules and the §8 row 1 cardinality.
-- -----------------------------------------------------------------------------
CREATE TABLE tenant_settings (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    sections                jsonb           NOT NULL DEFAULT '{}'::jsonb,
    section_versions        jsonb           NOT NULL DEFAULT '{}'::jsonb,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_tenant_settings PRIMARY KEY (id),
    CONSTRAINT uq_tenant_settings__tenant_id_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_tenant_settings__tenant_id UNIQUE (tenant_id)
);
COMMENT ON TABLE tenant_settings IS
  'The single settings row per tenant, holding every configuration section as versioned JSON so two admins editing different sections never clobber each other. @tier:T @owner:Platform';
COMMENT ON COLUMN tenant_settings.sections IS
  'Keys: general, hr, payroll, localization, branding, security, lookups, leave, loans, overtime, notification_templates, document_requirements, help_texts. Written with jsonb_set on one key, guarded by that key''s section_versions entry (§A).';

-- -----------------------------------------------------------------------------
-- number_sequences — tier T (company_id NULL = tenant-wide).
-- The only place a human-facing counter may live (CONVENTIONS.md §2, §8).
-- -----------------------------------------------------------------------------
CREATE TABLE number_sequences (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    company_id              uuid,
    scope_key               varchar(40)     NOT NULL,
    period_key              varchar(16),
    reset_period            varchar(40)     NOT NULL DEFAULT 'None',
    prefix                  varchar(16),
    pattern                 varchar(64),
    next_value              bigint          NOT NULL DEFAULT 1,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_number_sequences PRIMARY KEY (id),
    CONSTRAINT uq_number_sequences__tenant_id_id UNIQUE (tenant_id, id),
    -- NULLS NOT DISTINCT so a tenant-wide row (company_id IS NULL) and a NULL period_key
    -- collide with themselves rather than silently duplicating the counter.
    CONSTRAINT uq_number_sequences__scope UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, scope_key, period_key)
);
COMMENT ON TABLE number_sequences IS
  'Allocates every human-facing number (employee, letter, run, WPS batch, settlement, GOSI filing, timesheet) with one UPDATE ... RETURNING, so no counter ever lives in a settings blob. @tier:T @owner:Platform';

-- -----------------------------------------------------------------------------
-- files — tier T. Every stored blob plus its PDPL purge state.
-- -----------------------------------------------------------------------------
CREATE TABLE files (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    uploaded_by             uuid,
    storage_key             text            NOT NULL,
    purpose                 varchar(40)     NOT NULL,
    purge_state             varchar(40)     NOT NULL DEFAULT 'Active',
    bucket                  varchar(63)     NOT NULL,
    mime                    varchar(255)    NOT NULL,
    size_bytes              bigint          NOT NULL,
    sha256                  text            NOT NULL,
    retention_until         date,
    purged_at               timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_files PRIMARY KEY (id),
    CONSTRAINT uq_files__tenant_id_id UNIQUE (tenant_id, id),
    -- §2.A states "storage_key (unique)" without a tenant prefix: an object-store key is
    -- globally unique by construction, so the constraint is global by design.
    CONSTRAINT uq_files__storage_key UNIQUE (storage_key)
);
COMMENT ON TABLE files IS
  'Every stored blob with its hash and purge state, so PDPL erasure can delete the object while the referencing row keeps the sha256 as evidence the document existed. @tier:T @owner:HR @retention:84-months-from-Expiry-then-Purge';
COMMENT ON COLUMN files.sha256 IS
  'Lowercase hex digest. Survives a purge as evidence (§12.3).';

-- -----------------------------------------------------------------------------
-- retention_policies — tier R/T, tenant_id NULL = the platform default row.
-- Effective-dated (CONVENTIONS.md §5). RLS policy shape (b), not (a) (§19.2).
--
-- Carries the full audit quartet rather than the reference-table exemption, because
-- a tenant genuinely writes override rows here; the pure reference tables
-- (permissions, statutory_rules, statutory_rule_bands) take the exemption.
-- -----------------------------------------------------------------------------
CREATE TABLE retention_policies (
    id                      uuid            NOT NULL,
    tenant_id               uuid,
    entity_name             varchar(63)     NOT NULL,
    rule_key                varchar(64)     NOT NULL,
    trigger_event           varchar(40)     NOT NULL,
    disposition             varchar(40)     NOT NULL,
    effective_from          date            NOT NULL,
    effective_to            date,
    legal_basis             text            NOT NULL,
    minimum_retention_months integer        NOT NULL,
    owner_role              varchar(40)     NOT NULL,
    source_reference        text,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_retention_policies PRIMARY KEY (id),
    CONSTRAINT uq_retention_policies__tenant_id_id UNIQUE NULLS NOT DISTINCT (tenant_id, id)
);
COMMENT ON TABLE retention_policies IS
  'The PDPL retention matrix as data — one row per entity giving the legal basis, minimum period, trigger event and disposition the retention job reads instead of appsettings. @tier:R/T @owner:Compliance @retention:Keep';
COMMENT ON COLUMN retention_policies.tenant_id IS
  'NULL = the platform default row, visible to every session under RLS shape (b). A tenant override row may only LENGTHEN the platform period (§12.4) — enforced by trigger, not by a table CHECK, because the rule needs a subquery.';
COMMENT ON COLUMN retention_policies.rule_key IS
  'Matches the C# RetentionRuleKeys constants and retention_purge_audits.rule_key (FK-free match, §Q).';

-- -----------------------------------------------------------------------------
-- platform_users — tier P. Operators in their own table (§5 decision 2), so no
-- nullable-tenant row ever sits in a client-data table. Outside every tenant purge.
-- -----------------------------------------------------------------------------
CREATE TABLE platform_users (
    id                      uuid            NOT NULL,
    email                   text            NOT NULL,
    status                  varchar(40)     NOT NULL DEFAULT 'Active',
    platform_role           varchar(40)     NOT NULL,
    full_name               text            NOT NULL,
    password_hash           text,
    mfa_enabled             boolean         NOT NULL DEFAULT false,
    mfa_secret_encrypted    text,
    mfa_recovery_hashes     jsonb,
    failed_login_count      integer         NOT NULL DEFAULT 0,
    lockout_end             timestamptz,
    last_login_at           timestamptz,
    deleted_at              timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_platform_users PRIMARY KEY (id),
    CONSTRAINT uq_platform_users__email UNIQUE (email)
);
COMMENT ON TABLE platform_users IS
  'Platform operators and their credentials, kept outside tenancy entirely so the operator surface is policed by grant rather than by a tenant filter. @tier:P @owner:Platform @retention:12-months-after-SoftDelete-then-Anonymise';
COMMENT ON COLUMN platform_users.email IS
  'Globally unique (no tenant to scope it by). Stored normalised by the application; see 001_extensions.sql on the citext question.';
COMMENT ON COLUMN platform_users.password_hash IS
  'Secret column: REVOKE from kynex_ro by column privilege (§19.2).';

-- -----------------------------------------------------------------------------
-- data_protection_keys — tier P. The ASP.NET Data Protection key ring that
-- encrypts MFA secrets and tokens. Framework-owned; no outbound FK by design (§8.2).
-- -----------------------------------------------------------------------------
CREATE TABLE data_protection_keys (
    id                      uuid            NOT NULL,
    friendly_name           text            NOT NULL,
    xml                     text            NOT NULL,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_data_protection_keys PRIMARY KEY (id)
);
COMMENT ON TABLE data_protection_keys IS
  'The ASP.NET Data Protection key ring whose keys encrypt MFA secrets and single-use tokens at rest. @tier:P @owner:Platform @retention:Keep';
COMMENT ON COLUMN data_protection_keys.xml IS
  'Secret column: REVOKE from kynex_ro by column privilege (§19.2).';
