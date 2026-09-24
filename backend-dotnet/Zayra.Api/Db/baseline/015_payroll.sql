-- =============================================================================
-- 015_payroll.sql
-- Domain F — Payroll (6 tables).
-- TARGET_SCHEMA.md revision 6 §2.F; CONVENTIONS.md §1–§10.
--
-- CREATE TABLE only. FKs/CHECKs/EXCLUDEs live in 020_constraints_a_f.sql.
-- No table in domain F is partitioned (§19.3).
--
-- TIER DISCREPANCY (reported, resolved in favour of §8).
--   §2.F marks payroll_slips, payroll_slip_lines and payroll_issues as tier C, but
--   neither their key-column lists nor the §8 FK register gives any of them a
--   company_id or a (tenant_id, company_id) FK to companies. They reach the legal
--   entity through payroll_runs.company_id. This baseline follows §8 — the
--   authoritative register — and does NOT invent a company_id. If the tier letter is
--   the intended truth, three columns and three composite FKs are missing from §8.
--
-- Stored totals on payroll_slips and payroll_runs are CACHES. Lines are always
-- authoritative, and each total has a deferred constraint trigger in the 050 pass
-- (§11.2). Nothing here enforces them.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- pay_components — tier T. The component catalogue.
-- `is_active` is legal here: one of the seven catalogues of §12.1.
-- FK target for the composite (tenant_id, pay_component_code) references below.
-- -----------------------------------------------------------------------------
CREATE TABLE pay_components (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    code                    varchar(32)     NOT NULL,
    kind                    varchar(40)     NOT NULL,
    name_en                 text            NOT NULL,
    name_ar                 text,
    gosi_contributory       boolean         NOT NULL DEFAULT false,
    eos_eligible            boolean         NOT NULL DEFAULT false,
    prorate                 boolean         NOT NULL DEFAULT false,
    gl_driver               varchar(64),
    is_system               boolean         NOT NULL DEFAULT false,
    is_active               boolean         NOT NULL DEFAULT true,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_pay_components PRIMARY KEY (id),
    CONSTRAINT uq_pay_components__tenant_id_id UNIQUE (tenant_id, id),
    -- Required as the target of the composite FKs from payroll_slip_lines,
    -- payroll_inputs and company_pay_policies (§8 rows 22, 64, 72).
    CONSTRAINT uq_pay_components__tenant_id_code UNIQUE (tenant_id, code)
);
COMMENT ON TABLE pay_components IS
  'The catalogue of everything that can appear as a line on a payslip, each declaring whether it is GOSI-contributory, EOS-eligible, prorated and which GL driver it posts through. @tier:T @owner:Finance';
COMMENT ON COLUMN pay_components.is_system IS
  'Seeded per tenant at provisioning: BASIC, HOUSING, TRANSPORT, OT, GOSI_ANN_EE/ER, SANED_EE/ER, OH_ER, LOAN, ADVANCE, UNPAID_LEAVE, ABSENCE. A tenant may add components but not remove these.';

-- -----------------------------------------------------------------------------
-- payroll_runs — tier C. Run header.
-- Totals are four explicit columns (revision 6 expanded the named "totals" group).
-- State machine §10.1, enforced by the DB trigger trg_payroll_run_transition (050).
-- -----------------------------------------------------------------------------
CREATE TABLE payroll_runs (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    company_id              uuid            NOT NULL,
    parent_run_id           uuid,
    source_import_job_id    uuid,
    approval_request_id     uuid,
    run_type                varchar(40)     NOT NULL,
    status                  varchar(40)     NOT NULL DEFAULT 'Draft',
    year                    smallint        NOT NULL,
    month                   smallint        NOT NULL,
    attendance_locked_range daterange,
    employee_count          integer         NOT NULL DEFAULT 0,
    selected_employee_count integer         NOT NULL DEFAULT 0,
    total_gross             numeric(18,2)   NOT NULL DEFAULT 0,
    total_deductions        numeric(18,2)   NOT NULL DEFAULT 0,
    total_net               numeric(18,2)   NOT NULL DEFAULT 0,
    total_employer_statutory numeric(18,2)  NOT NULL DEFAULT 0,
    rules_version           varchar(40),
    source_system           varchar(40),
    idempotency_key         text,
    selection               jsonb,
    calculated_at           timestamptz,
    approved_at             timestamptz,
    locked_at               timestamptz,
    void_reason             text,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_payroll_runs PRIMARY KEY (id),
    CONSTRAINT uq_payroll_runs__tenant_id_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_payroll_runs__idempotency_key UNIQUE (tenant_id, idempotency_key)
);
-- §2.F: UNIQUE (tenant, company, year, month, run_type)
--        WHERE run_type IN ('Regular','Opening') AND status <> 'Voided'.
-- A partial unique cannot be a table constraint, so it is a unique INDEX. This is a
-- design uniqueness rule, not an access path, so it belongs with the table.
CREATE UNIQUE INDEX uq_payroll_runs__period_regular_opening
    ON payroll_runs (tenant_id, company_id, year, month, run_type)
    WHERE run_type IN ('Regular', 'Opening') AND status <> 'Voided';
COMMENT ON INDEX uq_payroll_runs__period_regular_opening IS
  'Enforces §2.F: at most one live Regular run and one live Opening run per company and period. Partial on status <> ''Voided'' so a voided run can be re-run for the same month. Also serves the payroll dashboard''s lookup of the current run for a company and period.';
COMMENT ON TABLE payroll_runs IS
  'One payroll execution for a company and period — regular, off-cycle, correction, final settlement or the mid-year Opening import — carrying its selection, its cached totals, the rules version it applied and the attendance range it locked. @tier:C @owner:Finance @retention:Keep';
COMMENT ON COLUMN payroll_runs.attendance_locked_range IS
  'Authoritative for the attendance lock; attendance_days.locked_run_id is the per-row projection written in the same transaction (§11.6). CHECK-constrained to lie inside the run period. Set on Approved, cleared on Void.';
COMMENT ON COLUMN payroll_runs.selected_employee_count IS
  'The run''s exit condition: it leaves Processing only when slips + explicitly excluded = this count (§10.1, §19.5).';
COMMENT ON COLUMN payroll_runs.total_gross IS
  'CACHE of SUM over included slips, reconciled by trg_run_totals only in the transaction that moves Processing -> Processed. While Processing, every total_* column is UNDEFINED and must not be displayed as authoritative (§11.2).';
COMMENT ON COLUMN payroll_runs.source_import_job_id IS
  'Opening-run provenance. FK deferred to the cross-domain constraints pass: background_jobs is domain R.';

-- -----------------------------------------------------------------------------
-- payroll_slips — tier C per §2.F (see the tier note at the top of this file).
-- FROZEN from Approved onward, with the full set of reconstruction witnesses so a
-- slip never has to join a live row (§15).
-- -----------------------------------------------------------------------------
CREATE TABLE payroll_slips (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    run_id                  uuid            NOT NULL,
    employee_id             uuid            NOT NULL,
    payslip_file_id         uuid,
    template_id             uuid,
    payslip_number          varchar(40),
    inclusion_status        varchar(40)     NOT NULL DEFAULT 'Included',
    paid_from               date,
    paid_to                 date,
    paid_days               numeric(9,2),
    period_days             numeric(9,2),
    proration_denominator_days numeric(9,2),
    proration_basis         varchar(40),
    proration_factor        numeric(9,6),
    gross                   numeric(18,2)   NOT NULL DEFAULT 0,
    deductions              numeric(18,2)   NOT NULL DEFAULT 0,
    net                     numeric(18,2)   NOT NULL DEFAULT 0,
    employee_statutory_total numeric(18,2)  NOT NULL DEFAULT 0,
    employer_statutory_total numeric(18,2)  NOT NULL DEFAULT 0,
    loan_deductions         numeric(18,2)   NOT NULL DEFAULT 0,
    arrears_amount          numeric(18,2)   NOT NULL DEFAULT 0,
    is_final_wage_month     boolean         NOT NULL DEFAULT false,
    ytd_gross               numeric(18,2)   NOT NULL DEFAULT 0,
    ytd_deductions          numeric(18,2)   NOT NULL DEFAULT 0,
    ytd_net                 numeric(18,2)   NOT NULL DEFAULT 0,
    ytd_employee_statutory  numeric(18,2)   NOT NULL DEFAULT 0,
    ytd_employer_statutory  numeric(18,2)   NOT NULL DEFAULT 0,
    ytd_contributory_wage   numeric(18,2)   NOT NULL DEFAULT 0,
    employee_number         varchar(32),
    employee_name           text,
    department_name         text,
    designation_name        text,
    nationality_class       varchar(40),
    gosi_cohort             varchar(40),
    employer_gosi_registration_no varchar(20),
    iban                    varchar(34),
    bank_code               varchar(16),
    gosi_base_policy        varchar(40),
    full_basic              numeric(18,2),
    full_housing            numeric(18,2),
    full_transport          numeric(18,2),
    contributory_wage       numeric(18,2),
    payslip_sha256          text,
    template_version        integer,
    language                varchar(40),
    published_at            timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_payroll_slips PRIMARY KEY (id),
    CONSTRAINT uq_payroll_slips__tenant_id_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_payroll_slips__run_id_employee_id UNIQUE (tenant_id, run_id, employee_id)
);
COMMENT ON TABLE payroll_slips IS
  'One frozen payslip per employee per run, holding its own identity, proration, GOSI and year-to-date witnesses so it can be reprinted and reconciled years later without joining a single live row. @tier:C @owner:Finance @retention:84-months-from-RecordDate-then-Keep';
COMMENT ON COLUMN payroll_slips.contributory_wage IS
  'The PERIOD''s computed contributory wage. Deliberately a different fact from payroll_slip_lines.applied_contributory_wage (per GOSI branch) and from employee_gosi_registrations.registered_contributory_wage (what GOSI holds) — §11.4.';
COMMENT ON COLUMN payroll_slips.employer_gosi_registration_no IS
  'Frozen snapshot, and the only copy of the registration number not bound by FK; companies.gosi_registration_no is the single owner (§11.5). It is what traces a slip to the return that carried it.';
COMMENT ON COLUMN payroll_slips.ytd_gross IS
  'Year-to-date set. §19.4 H7 turns the YTD query into a single-row read of the prior slip; the Opening run seeds these as the go-live carry-forward (§2.F).';
COMMENT ON COLUMN payroll_slips.employee_name IS
  'Identity snapshot. §2.F names these columns "name, department, designation"; spelled _name here so they cannot be mistaken for live joins.';
COMMENT ON COLUMN payroll_slips.gross IS
  'CACHE of the signed SUM of payroll_slip_lines of the matching kinds, reconciled by the deferred trg_slip_totals (§11.2). Lines are authoritative.';

-- -----------------------------------------------------------------------------
-- payroll_slip_lines — tier C per §2.F (see the tier note at the top of this file).
-- The ONLY side of both former FK cycles: payroll_inputs.consumed_line_id and
-- loan_installments.recovered_slip_line_id were deleted (§8.4).
-- Row-stamp EXEMPT (§Conventions): created_at and its own actor column only.
-- -----------------------------------------------------------------------------
CREATE TABLE payroll_slip_lines (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    slip_id                 uuid            NOT NULL,
    pay_component_code      varchar(32)     NOT NULL,
    statutory_rule_id       uuid,
    statutory_rule_band_id  uuid,
    payroll_input_id        uuid,
    loan_installment_id     uuid,
    cost_center_id          uuid,
    kind                    varchar(40)     NOT NULL,
    amount                  numeric(18,2)   NOT NULL,
    quantity                numeric(18,6),
    rate                    numeric(9,6),
    gosi_branch             varchar(40),
    gosi_payer              varchar(40),
    applied_contributory_wage numeric(18,2),
    rules_version           varchar(40),
    gl_driver               varchar(64),
    source_type             varchar(40),
    source_system           varchar(40),
    source_record_id        text,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    CONSTRAINT pk_payroll_slip_lines PRIMARY KEY (id),
    CONSTRAINT uq_payroll_slip_lines__tenant_id_id UNIQUE (tenant_id, id)
);
COMMENT ON TABLE payroll_slip_lines IS
  'The single line table behind every payslip, freezing the component, the GOSI branch, payer, applied wage, rule and band that produced each amount, plus where the amount came from. @tier:C @owner:Finance @retention:84-months-from-RecordDate-then-Keep';
COMMENT ON COLUMN payroll_slip_lines.applied_contributory_wage IS
  'The wage actually applied to THIS line''s GOSI branch, which differs whenever a branch has its own floor or cap. Renamed in revision 3 to end the ambiguity with the slip-level figure (§11.4).';
COMMENT ON COLUMN payroll_slip_lines.loan_installment_id IS
  'Recovery evidence, and the surviving half of the broken payroll_slip_lines <-> loan_installments cycle (§8.4). FK deferred to the cross-domain pass: loan_installments is domain I.';
COMMENT ON COLUMN payroll_slip_lines.payroll_input_id IS
  'The line points at the input; the input does NOT point back. That is how the second cycle was broken (§8.4).';
COMMENT ON COLUMN payroll_slip_lines.source_record_id IS
  'Opening-run provenance alongside source_system: which row of which legacy system this opening amount came from (§2.F).';

-- -----------------------------------------------------------------------------
-- payroll_inputs — tier C. Pending variable pay, WITH the covered period on the row.
-- Claimed by one single statement, never read-then-write (§F, §19.5).
-- -----------------------------------------------------------------------------
CREATE TABLE payroll_inputs (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    company_id              uuid            NOT NULL,
    employee_id             uuid            NOT NULL,
    pay_component_code      varchar(32)     NOT NULL,
    cost_center_id          uuid,
    claimed_by_run_id       uuid,
    consumed_run_id         uuid,
    kind                    varchar(40)     NOT NULL,
    status                  varchar(40)     NOT NULL DEFAULT 'Pending',
    target_run_type         varchar(40),
    source_type             varchar(40)     NOT NULL,
    source_id               uuid,
    revision                integer         NOT NULL DEFAULT 1,
    run_year                smallint        NOT NULL,
    run_month               smallint        NOT NULL,
    covered_year            smallint        NOT NULL,
    covered_month           smallint        NOT NULL,
    entitled_amount         numeric(18,2)   NOT NULL,
    previously_settled_amount numeric(18,2) NOT NULL DEFAULT 0,
    amount                  numeric(18,2)   NOT NULL,
    gosi_basis_delta        numeric(18,2)   NOT NULL DEFAULT 0,
    claimed_at              timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_payroll_inputs PRIMARY KEY (id),
    CONSTRAINT uq_payroll_inputs__tenant_id_id UNIQUE (tenant_id, id),
    -- §2.F, stated at length: deliberately NOT global on (source_type, source_id).
    -- Two successive backdated increments for the same covered period, settled in
    -- different payroll months, are legal and common. NULLS NOT DISTINCT because
    -- source_id is NULL for 'Manual' and 'Bonus' (§16).
    CONSTRAINT uq_payroll_inputs__source_period_revision UNIQUE NULLS NOT DISTINCT
        (tenant_id, source_type, source_id, covered_year, covered_month, pay_component_code, revision)
);
COMMENT ON TABLE payroll_inputs IS
  'Variable pay waiting to be paid — adjustments, arrears, overtime, absence, encashment — each carrying the period it BELONGS to as well as the period it is paid in, so a backdated amount recalculates GOSI as data rather than as arithmetic in a service. @tier:C @owner:Finance @retention:Keep';
COMMENT ON COLUMN payroll_inputs.gosi_basis_delta IS
  'The contributory-wage change this backdated amount causes in the COVERED period. Stored so the GOSI recalculation is data, not arithmetic in a service (§2.F).';
COMMENT ON COLUMN payroll_inputs.revision IS
  'Cancelling an input BUMPS this and inserts the replacement; it never reuses the key its replacement needs (§2.F, CONVENTIONS.md §11).';
COMMENT ON COLUMN payroll_inputs.claimed_by_run_id IS
  'SET NULL on delete so deleting a draft run releases the claim. A crashed run releases by predicate — status back to Pending where claimed_by_run_id points at a voided run — which is what makes a retry idempotent (§F).';
COMMENT ON COLUMN payroll_inputs.source_type IS
  'Polymorphic pointer with source_id (§16): Overtime -> overtime_requests, Leave -> leave_requests, Attendance -> attendance_days, Timesheet -> timesheets, Opening -> background_jobs, Manual/Bonus -> NULL. A nightly sweep reports unresolvable rows.';

-- -----------------------------------------------------------------------------
-- payroll_issues — tier C per §2.F (see the tier note at the top of this file).
-- run_id NULLABLE: NULL = a STANDING readiness gap that blocks any run.
-- -----------------------------------------------------------------------------
CREATE TABLE payroll_issues (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    run_id                  uuid,
    employee_id             uuid,
    override_by             uuid,
    code                    varchar(64)     NOT NULL,
    severity                varchar(40)     NOT NULL,
    gap_type                varchar(40),
    message                 text            NOT NULL,
    evidence                jsonb,
    detected_at             timestamptz     NOT NULL DEFAULT now(),
    resolved_at             timestamptz,
    override_reason         text,
    override_at             timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_payroll_issues PRIMARY KEY (id),
    CONSTRAINT uq_payroll_issues__tenant_id_id UNIQUE (tenant_id, id)
);
COMMENT ON TABLE payroll_issues IS
  'Every validation finding and standing readiness gap that blocks or warns a payroll run, with the evidence behind it and — for warnings only — who waived it and why. @tier:C @owner:Finance @retention:Keep';
COMMENT ON COLUMN payroll_issues.run_id IS
  'NULL = a standing gap that blocks ANY run for this employee (GOSI_COHORT_UNKNOWN, IBAN_MISSING, ORG_ESTABLISHMENT_MISSING, SALARY_HELD). Non-NULL = a finding of one run, which dies with a draft run.';
COMMENT ON COLUMN payroll_issues.severity IS
  'Block is NEVER overridable — enforced by CHECK, not by the UI (§2.F). Warn may be overridden with a recorded reason.';
