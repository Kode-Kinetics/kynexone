-- =============================================================================
-- 012_org.sql
-- Domain C — Organisation (8 tables).
-- TARGET_SCHEMA.md revision 6 §2.C; CONVENTIONS.md §1–§10.
--
-- CREATE TABLE only. FKs/CHECKs/EXCLUDEs live in 020_constraints_a_f.sql.
-- No table in domain C is partitioned (§19.3).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- companies — tier T (the legal entity itself; company tier hangs off it).
-- Soft delete per §12.1. Single owner of gosi_registration_no per §11.5.
-- -----------------------------------------------------------------------------
CREATE TABLE companies (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    cr_number               varchar(15),
    mol_establishment_no    varchar(20),
    gosi_registration_no    varchar(20),
    name_en                 text            NOT NULL,
    name_ar                 text,
    nitaqat_activity_code   varchar(16),
    wps_bank_code           varchar(16),
    wps_mol_id              varchar(20),
    currency_code           char(3)         NOT NULL DEFAULT 'SAR',
    timezone_id             varchar(64),
    go_live_year            smallint,
    go_live_month           smallint,
    settings                jsonb           NOT NULL DEFAULT '{}'::jsonb,
    soft_deleted_at         timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_companies PRIMARY KEY (id),
    CONSTRAINT uq_companies__tenant_id_id UNIQUE (tenant_id, id),
    -- §11.5: the ONE writable copy of the filing identifier, and the FK target for
    -- employee_gosi_registrations and gosi_filings (§8 rows 138, 140).
    CONSTRAINT uq_companies__tenant_id_gosi_registration_no UNIQUE (tenant_id, gosi_registration_no)
);
COMMENT ON TABLE companies IS
  'The legal entity and its MOL establishment: the registration identifiers every statutory filing is made under, the currency of record, an optional timezone override and the mid-year go-live period. @tier:T @owner:Finance @retention:Keep';
COMMENT ON COLUMN companies.gosi_registration_no IS
  'The single writable copy of the GOSI establishment number (§11.5). A typo can exist in exactly one place and cannot propagate into a filing.';
COMMENT ON COLUMN companies.currency_code IS
  'SAR is the currency of record. Every money column in the design is denominated in THIS company''s currency; there is no per-row currency column anywhere (§13.2).';
COMMENT ON COLUMN companies.settings IS
  'Non-money company overrides only. Contractual pay parameters moved OUT to company_pay_policies in revision 3 precisely so they get the dated EXCLUDE discipline (§13.5).';
COMMENT ON COLUMN companies.go_live_year IS
  'The go-live period as two typed columns, not a named concept (§2.C, revision 6 correction).';

-- -----------------------------------------------------------------------------
-- company_pay_policies — tier C. Effective-dated contractual pay parameters.
-- -----------------------------------------------------------------------------
CREATE TABLE company_pay_policies (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    company_id              uuid            NOT NULL,
    policy_key              varchar(64)     NOT NULL,
    pay_component_code      varchar(32),
    effective_from          date            NOT NULL,
    effective_to            date,
    rate                    numeric(9,6),
    amount                  numeric(18,2),
    value_json              jsonb,
    approved_by             uuid,
    source_reference        text,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_company_pay_policies PRIMARY KEY (id),
    CONSTRAINT uq_company_pay_policies__tenant_id_id UNIQUE (tenant_id, id)
);
COMMENT ON TABLE company_pay_policies IS
  'Contractual, above-statutory-floor pay parameters per legal entity, effective-dated so the rate in force on any day is a single row rather than a JSON lookup. @tier:C @owner:Finance @retention:Keep';
COMMENT ON COLUMN company_pay_policies.approved_by IS
  'Plain uuid, not an FK: §8.2 registers no FK for this column and a purged approver must not block a contractual row (same rule as created_by).';
COMMENT ON COLUMN company_pay_policies.value_json IS
  'Bounded to 8 KiB by CHECK and schema-validated on write. May only EXCEED a statutory floor, checked against the rule in force (§11.6).';

-- -----------------------------------------------------------------------------
-- branches — tier C. Physical site.
-- -----------------------------------------------------------------------------
CREATE TABLE branches (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    company_id              uuid            NOT NULL,
    name                    text            NOT NULL,
    city                    text,
    address                 text,
    lat                     numeric(9,6),
    lng                     numeric(9,6),
    geofence_radius_m       integer,
    holiday_calendar_code   varchar(40),
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_branches PRIMARY KEY (id),
    CONSTRAINT uq_branches__tenant_id_id UNIQUE (tenant_id, id)
);
COMMENT ON TABLE branches IS
  'A physical site of a legal entity, carrying the geofence that validates a mobile punch and the holiday calendar that shapes its working days. @tier:C @owner:HR';

-- -----------------------------------------------------------------------------
-- departments — tier C. Self-referencing org unit.
-- -----------------------------------------------------------------------------
CREATE TABLE departments (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    company_id              uuid            NOT NULL,
    parent_id               uuid,
    cost_center_id          uuid,
    name                    text            NOT NULL,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_departments PRIMARY KEY (id),
    CONSTRAINT uq_departments__tenant_id_id UNIQUE (tenant_id, id)
);
COMMENT ON TABLE departments IS
  'The org-unit hierarchy within a legal entity, optionally pointing at the cost centre its salary cost posts to. @tier:C @owner:HR';

-- -----------------------------------------------------------------------------
-- cost_centers — tier C. A real table because GL, timesheets and project costing
-- all key on it; a text code on departments could not carry a GL segment (§2.C).
-- `is_active` is legal here: one of the seven catalogues of §12.1.
-- -----------------------------------------------------------------------------
CREATE TABLE cost_centers (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    company_id              uuid            NOT NULL,
    parent_id               uuid,
    code                    varchar(40)     NOT NULL,
    name                    text            NOT NULL,
    gl_segment              varchar(64),
    is_active               boolean         NOT NULL DEFAULT true,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_cost_centers PRIMARY KEY (id),
    CONSTRAINT uq_cost_centers__tenant_id_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_cost_centers__company_id_code UNIQUE (tenant_id, company_id, code)
);
COMMENT ON TABLE cost_centers IS
  'The costing dimension shared by GL export, timesheets, payroll inputs and slip lines, carrying the segment the ERP''s chart of accounts expects. @tier:C @owner:Finance';

-- -----------------------------------------------------------------------------
-- designations — tier T. Job title and its statutory occupation code.
-- -----------------------------------------------------------------------------
CREATE TABLE designations (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    occupation_code         varchar(16),
    title_en                text            NOT NULL,
    title_ar                text,
    is_active               boolean         NOT NULL DEFAULT true,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_designations PRIMARY KEY (id),
    CONSTRAINT uq_designations__tenant_id_id UNIQUE (tenant_id, id)
);
COMMENT ON TABLE designations IS
  'Job titles in English and Arabic with the MHRSD/GOSI occupation code that Saudization-restricted jobs and GOSI registration require. @tier:T @owner:HR';

-- -----------------------------------------------------------------------------
-- grades — tier T. Grade and pay band.
-- -----------------------------------------------------------------------------
CREATE TABLE grades (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    code                    varchar(40)     NOT NULL,
    name                    text            NOT NULL,
    min_basic               numeric(18,2),
    max_basic               numeric(18,2),
    pay_scale               jsonb,
    is_active               boolean         NOT NULL DEFAULT true,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_grades PRIMARY KEY (id),
    CONSTRAINT uq_grades__tenant_id_id UNIQUE (tenant_id, id),
    -- [DESIGN GAP] §2.C names `code` as a key column but states no uniqueness. Added.
    CONSTRAINT uq_grades__tenant_id_code UNIQUE (tenant_id, code)
);
COMMENT ON TABLE grades IS
  'Salary grades with their basic-pay band and an optional component pay scale, used to validate and default an employee''s salary structure. @tier:T @owner:HR';

-- -----------------------------------------------------------------------------
-- public_holidays — tier R/T, tenant_id NULL = the platform KSA calendar.
-- RLS policy shape (b) (§19.2).
--
-- NOTE: §2.C names the column `date`. CONVENTIONS.md §1's reserved-word rule and the
-- `<noun>_date` naming rule both forbid it, so it is `holiday_date` here.
-- -----------------------------------------------------------------------------
CREATE TABLE public_holidays (
    id                      uuid            NOT NULL,
    tenant_id               uuid,
    calendar_code           varchar(40)     NOT NULL,
    holiday_date            date            NOT NULL,
    name                    text            NOT NULL,
    is_paid                 boolean         NOT NULL DEFAULT true,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_public_holidays PRIMARY KEY (id),
    CONSTRAINT uq_public_holidays__tenant_id_id UNIQUE NULLS NOT DISTINCT (tenant_id, id),
    -- [DESIGN GAP] Not stated in §2.C. Added: §19.4 H8 loads (calendar_code, date)
    -- once per attendance sweep and a duplicated holiday would double-count a day.
    CONSTRAINT uq_public_holidays__calendar_date UNIQUE NULLS NOT DISTINCT (tenant_id, calendar_code, holiday_date)
);
COMMENT ON TABLE public_holidays IS
  'Named non-working days per calendar code, with the platform KSA calendar carried as the tenant_id IS NULL rows that every tenant session can read. @tier:R/T @owner:HR';
COMMENT ON COLUMN public_holidays.holiday_date IS
  'A local Gregorian date. Hijri occasions are named in `name` but never stored as Hijri — Hijri is always derived (§13.4).';
