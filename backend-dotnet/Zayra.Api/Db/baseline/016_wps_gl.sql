-- =============================================================================
-- 016_wps_gl.sql
-- KynexOne baseline schema — TARGET_SCHEMA.md revision 6.
--
-- The filed-money and statutory-filing half of the second scope:
--
-- Domain G  WPS ....................... wps_batches, wps_lines
-- Domain H  GL export ................. gl_mappings, gl_journals,
--                                       gl_journal_lines, gl_period_closes
-- Domain I  Loans and advances ........ loans, loan_installments
-- Domain M  Nitaqat ................... nitaqat_grid, nitaqat_snapshots
-- Domain N  GOSI registration/filing .. employee_gosi_registrations,
--                                       gosi_filings
--
-- (M and N sit here rather than in 017 because they are statutory filings on
--  the same evidence footing as WPS and GL, not leave/attendance operations.)
--
-- Tables only. Foreign keys, CHECK sets (§9) and invariant constraints (§11)
-- live in 021_constraints_g_r.sql. Indexes beyond PRIMARY KEY / UNIQUE, RLS
-- policies, partitions and triggers are added by later passes (§19.1).
--
-- Conventions applied here (schema-docs/CONVENTIONS.md, TARGET_SCHEMA.md §1):
--   * uuid primary keys (UUIDv7, allocated app-side)
--   * tenant tier carries tenant_id NOT NULL plus UNIQUE (tenant_id, id)
--   * money numeric(18,2), rates numeric(9,6), instants timestamptz, dates date
--   * column order follows CONVENTIONS §10
--   * mutable tables carry created_at/created_by/updated_at/updated_by;
--     frozen and append-only tables carry created_at plus their own actor only
-- =============================================================================

-- -----------------------------------------------------------------------------
-- G. WPS
-- -----------------------------------------------------------------------------

CREATE TABLE wps_batches (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    company_id              uuid           NOT NULL,
    run_id                  uuid           NOT NULL,
    batch_number            varchar(40)    NOT NULL,
    status                  varchar(40)    NOT NULL DEFAULT 'Generated',
    format_version          varchar(40)    NOT NULL,
    employee_count          integer        NOT NULL DEFAULT 0,
    total_amount            numeric(18,2)  NOT NULL DEFAULT 0,
    submission_reference    varchar(64),
    file_id                 uuid,
    file_sha256             text,
    resubmission_of_id      uuid,
    generated_by            uuid,
    submitted_at            timestamptz,
    acknowledged_at         timestamptz,
    rejected_at             timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_wps_batches            PRIMARY KEY (id),
    CONSTRAINT uq_wps_batches__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_wps_batches__batch_number UNIQUE (tenant_id, batch_number)
);

COMMENT ON TABLE wps_batches IS
    'Holds one Wage Protection System SIF file per payroll run as generated, submitted and acknowledged by the bank, including its resubmission chain. @tier:C @owner:Finance @retention:84m-keep';

CREATE TABLE wps_lines (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    batch_id                uuid           NOT NULL,
    slip_id                 uuid           NOT NULL,
    employee_id             uuid           NOT NULL,
    -- SIF snapshot, frozen when the batch is submitted (§15)
    id_number               varchar(20),
    employee_number         varchar(32)    NOT NULL,
    iban                    varchar(34)    NOT NULL,
    bank_code               varchar(16),
    mol_id                  varchar(20),
    basic                   numeric(18,2)  NOT NULL DEFAULT 0,
    housing                 numeric(18,2)  NOT NULL DEFAULT 0,
    other_earnings          numeric(18,2)  NOT NULL DEFAULT 0,
    deductions              numeric(18,2)  NOT NULL DEFAULT 0,
    net                     numeric(18,2)  NOT NULL DEFAULT 0,
    -- bank confirmation, the only part writable after freeze (§10.5)
    bank_status             varchar(40)    NOT NULL DEFAULT 'Pending',
    bank_reference          text,
    confirmed_amount        numeric(18,2),
    reason_code             varchar(40),
    value_date              date,
    confirmation_job_id     uuid,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    CONSTRAINT pk_wps_lines            PRIMARY KEY (id),
    CONSTRAINT uq_wps_lines__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_wps_lines__batch_id_slip_id UNIQUE (tenant_id, batch_id, slip_id)
);

COMMENT ON TABLE wps_lines IS
    'Records each employee payment line exactly as filed in a WPS SIF batch, frozen at submission, alongside the bank confirmation result returned for it. @tier:C @owner:Finance @retention:84m-keep';

-- -----------------------------------------------------------------------------
-- H. GL export
-- -----------------------------------------------------------------------------

CREATE TABLE gl_mappings (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    company_id              uuid,
    cost_center_id          uuid,
    gl_driver               varchar(40)    NOT NULL,
    debit_account           varchar(64)    NOT NULL,
    credit_account          varchar(64)    NOT NULL,
    is_active               boolean        NOT NULL DEFAULT true,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_gl_mappings            PRIMARY KEY (id),
    CONSTRAINT uq_gl_mappings__tenant_id UNIQUE (tenant_id, id),
    -- NULLS NOT DISTINCT so the tenant-wide default (company_id IS NULL) and the
    -- un-dimensioned mapping (cost_center_id IS NULL) can each exist only once.
    CONSTRAINT uq_gl_mappings__driver_scope
        UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, gl_driver, cost_center_id)
);

COMMENT ON TABLE gl_mappings IS
    'Maps each payroll GL driver, optionally narrowed by company and cost centre, onto the debit and credit account codes owned by the customer ERP chart of accounts. @tier:T @owner:Finance @retention:tenant-lifecycle';

CREATE TABLE gl_journals (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    company_id              uuid           NOT NULL,
    source_type             varchar(40)    NOT NULL,
    source_id               uuid,
    status                  varchar(40)    NOT NULL DEFAULT 'Draft',
    -- the accounting period, as two columns rather than a named concept (§C)
    year             smallint       NOT NULL,
    month            smallint       NOT NULL,
    reversal_of_id          uuid,
    erp_reference           text,
    file_id                 uuid,
    export_file_sha256      text,
    idempotency_key         text,
    posted_at               timestamptz,
    exported_at             timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_gl_journals            PRIMARY KEY (id),
    CONSTRAINT uq_gl_journals__tenant_id UNIQUE (tenant_id, id),
    -- §H: one journal per source event, with the reversal as a distinct row.
    CONSTRAINT uq_gl_journals__source
        UNIQUE NULLS NOT DISTINCT (tenant_id, source_type, source_id, reversal_of_id),
    CONSTRAINT uq_gl_journals__idempotency_key
        UNIQUE (tenant_id, idempotency_key)
);

COMMENT ON TABLE gl_journals IS
    'Represents one general-ledger journal per source event, carrying the exported file, its hash, the ERP acknowledgement and the reversal chain. @tier:C @owner:Finance @retention:84m-keep';

CREATE TABLE gl_journal_lines (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    journal_id              uuid           NOT NULL,
    cost_center_id          uuid,
    line_order              integer        NOT NULL DEFAULT 0,
    account                 varchar(64)    NOT NULL,
    project_code            varchar(64),
    description             text,
    debit                   numeric(18,2)  NOT NULL DEFAULT 0,
    credit                  numeric(18,2)  NOT NULL DEFAULT 0,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    CONSTRAINT pk_gl_journal_lines            PRIMARY KEY (id),
    CONSTRAINT uq_gl_journal_lines__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE gl_journal_lines IS
    'Carries the balanced debit and credit lines of a GL journal together with their cost-centre and project segments. @tier:C @owner:Finance @retention:84m-keep';

CREATE TABLE gl_period_closes (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    company_id              uuid           NOT NULL,
    status                  varchar(40)    NOT NULL DEFAULT 'Open',
    year             smallint       NOT NULL,
    month            smallint       NOT NULL,
    reopen_reason           text,
    closed_at               timestamptz,
    closed_by               uuid,
    reopened_at             timestamptz,
    reopened_by             uuid,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_gl_period_closes            PRIMARY KEY (id),
    CONSTRAINT uq_gl_period_closes__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_gl_period_closes__period
        UNIQUE (tenant_id, company_id, year, month)
);

COMMENT ON TABLE gl_period_closes IS
    'Records the finance lock on one accounting period per company, including who closed it and the reason any reopening was granted. @tier:C @owner:Finance @retention:84m-keep';

-- -----------------------------------------------------------------------------
-- I. Loans and advances
-- -----------------------------------------------------------------------------

CREATE TABLE loans (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    employee_id             uuid           NOT NULL,
    approval_request_id     uuid,
    kind                    varchar(40)    NOT NULL,
    type_code               varchar(40),
    status                  varchar(40)    NOT NULL DEFAULT 'PendingApproval',
    -- the first recovery period, as two columns (§C)
    start_year       smallint       NOT NULL,
    start_month      smallint       NOT NULL,
    installment_count       integer        NOT NULL,
    reason                  text,
    principal               numeric(18,2)  NOT NULL,
    opening_outstanding     numeric(18,2)  NOT NULL DEFAULT 0,
    outstanding             numeric(18,2)  NOT NULL DEFAULT 0,
    disbursed_at            timestamptz,
    settled_at              timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_loans            PRIMARY KEY (id),
    CONSTRAINT uq_loans__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE loans IS
    'Holds an employee loan or salary advance with its principal, opening balance carried in at go-live, approval and the outstanding amount reconciled against its recoveries. @tier:T @owner:Finance @retention:84m-keep';

CREATE TABLE loan_installments (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    loan_id                 uuid           NOT NULL,
    installment_number      integer        NOT NULL,
    kind                    varchar(40)    NOT NULL DEFAULT 'Scheduled',
    status                  varchar(40)    NOT NULL DEFAULT 'Due',
    -- the period this installment falls due in, as two columns (§C)
    due_year         smallint       NOT NULL,
    due_month        smallint       NOT NULL,
    amount                  numeric(18,2)  NOT NULL,
    recovered_at            timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_loan_installments            PRIMARY KEY (id),
    CONSTRAINT uq_loan_installments__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_loan_installments__loan_id_number
        UNIQUE (tenant_id, loan_id, installment_number)
);

COMMENT ON TABLE loan_installments IS
    'Schedules each recovery of a loan or advance by due period and records whether it was recovered, waived or cancelled; the recovering payroll or settlement line points back at this row. @tier:T @owner:Finance @retention:84m-keep';

-- -----------------------------------------------------------------------------
-- M. Nitaqat
-- -----------------------------------------------------------------------------

-- Reference tier: seeded by the baseline, read-only to tenants, no tenant_id,
-- and carrying effective_from instead of the mutable-row audit quartet (§1).
CREATE TABLE nitaqat_grid (
    id                      uuid           NOT NULL,
    activity_code           varchar(32)    NOT NULL,
    activity_name_en        text,
    activity_name_ar        text,
    size_tier               varchar(40)    NOT NULL,
    band                    varchar(40)    NOT NULL,
    grid_version            varchar(40)    NOT NULL,
    effective_from          date           NOT NULL,
    effective_to            date,
    headcount_min           integer        NOT NULL,
    headcount_max           integer,
    min_saudization_pct     numeric(9,6)   NOT NULL,
    max_saudization_pct     numeric(9,6),
    source_reference        text,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    CONSTRAINT pk_nitaqat_grid PRIMARY KEY (id),
    CONSTRAINT uq_nitaqat_grid__band
        UNIQUE (activity_code, size_tier, grid_version, band, effective_from)
);

COMMENT ON TABLE nitaqat_grid IS
    'Seeds the MHRSD Nitaqat colour bands as Saudization percentage ranges per economic activity, establishment size tier and published grid version. @tier:R @owner:Compliance @retention:indefinite-keep';

CREATE TABLE nitaqat_snapshots (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    company_id              uuid           NOT NULL,
    as_of_date              date           NOT NULL,
    activity_code           varchar(32)    NOT NULL,
    size_tier               varchar(40)    NOT NULL,
    band                    varchar(40)    NOT NULL,
    total_headcount         integer        NOT NULL DEFAULT 0,
    saudi_weighted          numeric(18,2)  NOT NULL DEFAULT 0,
    total_weighted          numeric(18,2)  NOT NULL DEFAULT 0,
    achieved_pct            numeric(9,6)   NOT NULL DEFAULT 0,
    grid_version            varchar(40),
    rules_version           varchar(40),
    employee_breakdown      jsonb          NOT NULL DEFAULT '[]'::jsonb,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    CONSTRAINT pk_nitaqat_snapshots            PRIMARY KEY (id),
    CONSTRAINT uq_nitaqat_snapshots__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_nitaqat_snapshots__as_of
        UNIQUE (tenant_id, company_id, as_of_date)
);

COMMENT ON TABLE nitaqat_snapshots IS
    'Freezes an establishment Saudization standing on one date, with the weighted headcounts, achieved percentage, awarded band and the per-employee breakdown that the figure drills down to. @tier:C @owner:Compliance @retention:84m-keep';

-- -----------------------------------------------------------------------------
-- N. GOSI registration and filing
-- -----------------------------------------------------------------------------

CREATE TABLE employee_gosi_registrations (
    id                          uuid           NOT NULL,
    tenant_id                   uuid           NOT NULL,
    company_id                  uuid           NOT NULL,
    employee_id                 uuid           NOT NULL,
    gosi_registration_no        varchar(20)    NOT NULL,
    gosi_employee_no            varchar(20),
    status                      varchar(40)    NOT NULL DEFAULT 'Registered',
    effective_from              date           NOT NULL,
    effective_to                date,
    occupation_code             varchar(16),
    registered_on               date,
    deregistered_on             date,
    registered_contributory_wage numeric(18,2),
    created_at                  timestamptz    NOT NULL DEFAULT now(),
    created_by                  uuid,
    updated_at                  timestamptz,
    updated_by                  uuid,
    CONSTRAINT pk_employee_gosi_registrations            PRIMARY KEY (id),
    CONSTRAINT uq_employee_gosi_registrations__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE employee_gosi_registrations IS
    'Tracks the effective-dated GOSI registration of an employee against an establishment, including the contributory wage GOSI itself holds on file, which is what a filing variance is explained against. @tier:C @owner:Finance @retention:84m-keep';

CREATE TABLE gosi_filings (
    id                              uuid           NOT NULL,
    tenant_id                       uuid           NOT NULL,
    company_id                      uuid           NOT NULL,
    gosi_registration_no            varchar(20)    NOT NULL,
    status                          varchar(40)    NOT NULL DEFAULT 'Draft',
    year                            smallint       NOT NULL,
    month                           smallint       NOT NULL,
    revision                        integer        NOT NULL DEFAULT 1,
    employee_count                  integer        NOT NULL DEFAULT 0,
    -- branch x payer totals as seven explicit columns (§N), never a JSON blob,
    -- because finance reconciles them against the GOSI invoice.
    annuities_employee              numeric(18,2)  NOT NULL DEFAULT 0,
    annuities_employer              numeric(18,2)  NOT NULL DEFAULT 0,
    saned_employee                  numeric(18,2)  NOT NULL DEFAULT 0,
    saned_employer                  numeric(18,2)  NOT NULL DEFAULT 0,
    occupational_hazards_employer   numeric(18,2)  NOT NULL DEFAULT 0,
    total_contributory_wage         numeric(18,2)  NOT NULL DEFAULT 0,
    total_amount                    numeric(18,2)  NOT NULL DEFAULT 0,
    gosi_invoice_amount             numeric(18,2),
    variance_amount                 numeric(18,2),
    variance_reason                 text,
    rules_version                   varchar(40),
    file_id                         uuid,
    file_sha256                     text,
    filed_at                        timestamptz,
    filed_by                        uuid,
    reconciled_at                   timestamptz,
    created_at                      timestamptz    NOT NULL DEFAULT now(),
    created_by                      uuid,
    updated_at                      timestamptz,
    updated_by                      uuid,
    CONSTRAINT pk_gosi_filings            PRIMARY KEY (id),
    CONSTRAINT uq_gosi_filings__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_gosi_filings__period
        UNIQUE (tenant_id, company_id, gosi_registration_no, year, month, revision)
);

COMMENT ON TABLE gosi_filings IS
    'Holds the monthly GOSI return per establishment exactly as filed, with its seven branch-by-payer totals, the invoice it is reconciled against and the revision that supersedes a correction. @tier:C @owner:Finance @retention:84m-keep';
