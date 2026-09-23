-- =============================================================================
-- 013_employees.sql
-- Domain D — Employees, history, documents and letters (7 tables).
-- TARGET_SCHEMA.md revision 6 §2.D; CONVENTIONS.md §1–§10.
--
-- CREATE TABLE only. FKs/CHECKs/EXCLUDEs live in 020_constraints_a_f.sql.
-- No table in domain D is partitioned (§19.3).
--
-- Four tables here are effective-dated and take the standard
--   EXCLUDE USING gist (tenant_id =, <subject> =, daterange(from, to, '[]') &&)
-- in 020: employee_assignments, employee_salaries, employee_contracts,
-- employee_bank_accounts (CONVENTIONS.md §5).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- employees — tier T. Person and CURRENT identity.
-- Deliberately not effective-dated as a whole (§11.3): placement, pay, contract and
-- bank are the dated tables; every slip freezes its own identity snapshot.
-- Soft delete + PDPL lifecycle per §12.1/§12.3.
-- -----------------------------------------------------------------------------
CREATE TABLE employees (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    employee_number         varchar(32)     NOT NULL,
    status                  varchar(40)     NOT NULL DEFAULT 'Draft',
    privacy_status          varchar(40)     NOT NULL DEFAULT 'Normal',
    name_en                 text            NOT NULL,
    name_ar                 text,
    work_email              text,
    gender                  varchar(40),
    dob                     date,
    nationality_code        char(2),
    national_id             varchar(10),
    iqama_no                varchar(10),
    border_no               varchar(12),
    joining_date            date,
    gosi_first_registered_on date,
    wps_eligible            boolean         NOT NULL DEFAULT true,
    nitaqat_weight_override numeric(9,6),
    nitaqat_weight_override_reason text,
    eos_service_start_date  date,
    eos_prior_paid_amount   numeric(18,2),
    separation_date         date,
    deleted_at              timestamptz,
    retention_until         date,
    redacted_at             timestamptz,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_employees PRIMARY KEY (id),
    CONSTRAINT uq_employees__tenant_id_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_employees__tenant_id_employee_number UNIQUE (tenant_id, employee_number)
);
COMMENT ON TABLE employees IS
  'The person: current identity, statutory identifiers, employment anchors and the PDPL lifecycle that lets an erasure anonymise the record in place while every dependent payroll row keeps its foreign key. @tier:T @owner:HR @retention:84-months-from-Separation-then-Anonymise';
COMMENT ON COLUMN employees.status IS
  'Current-state PROJECTION maintained by EmployeeLifecycleService (§10.7). employee_assignments is authoritative for "was this person employed on date D" (§11.3).';
COMMENT ON COLUMN employees.gosi_first_registered_on IS
  'Drives the GOSI cohort (Legacy vs Entrant2024). NULL raises a BLOCKING payroll_issues row; the code never defaults to a cohort (§2.E).';
COMMENT ON COLUMN employees.separation_date IS
  'Projection of final_settlements.last_working_day, written when the settlement is approved; the settlement is authoritative (§11.3).';
COMMENT ON COLUMN employees.employee_number IS
  'Allocated from number_sequences. Retained through anonymisation so statutory payroll rows stay traceable (§12.3).';
-- [DESIGN DEFECT — reported] §19.4 H1 specifies a pg_trgm GIN index over
-- "(employee_number ‖ name_en ‖ name_ar ‖ work_email)", but §2.D's column list for
-- `employees` never defines work_email. The column is added here so the stated index
-- can be built by the 040 pass; if the intent was that ESS email lives only on
-- `users.normalized_email`, then §19.4 H1 must be corrected and this column dropped.
COMMENT ON COLUMN employees.work_email IS
  'Added to satisfy §19.4 H1, which indexes it; §2.D does not list it. See the note above this comment.';

-- -----------------------------------------------------------------------------
-- employee_assignments — tier C. EFFECTIVE-DATED placement.
-- Authoritative for employment state on a date (§11.3).
-- -----------------------------------------------------------------------------
CREATE TABLE employee_assignments (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    company_id              uuid            NOT NULL,
    employee_id             uuid            NOT NULL,
    branch_id               uuid            NOT NULL,
    department_id           uuid            NOT NULL,
    designation_id          uuid            NOT NULL,
    grade_id                uuid,
    manager_employee_id     uuid,
    cost_center_id          uuid,
    approval_request_id     uuid,
    employment_status       varchar(40),
    pay_group               varchar(40),
    effective_from          date            NOT NULL,
    effective_to            date,
    change_reason           text,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_employee_assignments PRIMARY KEY (id),
    CONSTRAINT uq_employee_assignments__tenant_id_id UNIQUE (tenant_id, id)
);
COMMENT ON TABLE employee_assignments IS
  'The effective-dated record of where a person sat — company, branch, department, designation, grade, manager and cost centre — and the single authority for whether they were employed on any given date. @tier:C @owner:HR @retention:Keep';
COMMENT ON COLUMN employee_assignments.cost_center_id IS
  'Optional in the schema on purpose: a company that posts GL by cost centre gets a payroll_issues BLOCK at calculation instead of a NOT NULL that stops HR saving an employee (§8 row 40).';
COMMENT ON COLUMN employee_assignments.approval_request_id IS
  'The decision behind the change. The row has no status of its own: the approval is its only state (§11.1).';

-- -----------------------------------------------------------------------------
-- employee_salaries — tier T. EFFECTIVE-DATED pay.
-- -----------------------------------------------------------------------------
CREATE TABLE employee_salaries (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    employee_id             uuid            NOT NULL,
    approval_request_id     uuid,
    effective_from          date            NOT NULL,
    effective_to            date,
    basic                   numeric(18,2)   NOT NULL,
    housing                 numeric(18,2)   NOT NULL DEFAULT 0,
    transport               numeric(18,2)   NOT NULL DEFAULT 0,
    housing_in_kind         boolean         NOT NULL DEFAULT false,
    components              jsonb,
    change_reason           text,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_employee_salaries PRIMARY KEY (id),
    CONSTRAINT uq_employee_salaries__tenant_id_id UNIQUE (tenant_id, id)
);
COMMENT ON TABLE employee_salaries IS
  'The effective-dated salary structure — basic, housing, transport and any further components — that a payroll run resolves as of the period, in the employing company''s currency. @tier:T @owner:Finance @retention:Keep';
COMMENT ON COLUMN employee_salaries.components IS
  'Amounts only. There is no per-row currency column anywhere; the currency is companies.currency_code (§13.2).';
COMMENT ON COLUMN employee_salaries.housing_in_kind IS
  'When true, housing is deemed at the statutory percentage of basic for the contributory wage rather than paid in cash (§2.E).';

-- -----------------------------------------------------------------------------
-- employee_contracts — tier T. EFFECTIVE-DATED contract.
-- -----------------------------------------------------------------------------
CREATE TABLE employee_contracts (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    employee_id             uuid            NOT NULL,
    document_id             uuid,
    contract_type           varchar(40),
    effective_from          date            NOT NULL,
    effective_to            date,
    start_date              date,
    end_date                date,
    probation_end           date,
    weekly_hours            numeric(6,2),
    notice_days             integer,
    qiwa_contract_no        text,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_employee_contracts PRIMARY KEY (id),
    CONSTRAINT uq_employee_contracts__tenant_id_id UNIQUE (tenant_id, id)
);
COMMENT ON TABLE employee_contracts IS
  'The effective-dated employment contract — type, term, probation, contracted hours and notice period — with an optional pointer to the signed scan. @tier:T @owner:HR @retention:Keep';
-- [DESIGN CONTRADICTION — reported] §18's external-id convention says
-- `external_system` / `external_id` / `external_synced_at` REPLACE today's
-- `qiwa_contract_no` spelling, but §2.D still lists `qiwa_contract_no` as a column of
-- this table and names no external_* columns anywhere in A–F. §2's table spec is
-- followed here; adding both would be the duplication §7 forbids.
COMMENT ON COLUMN employee_contracts.qiwa_contract_no IS
  'Kept per §2.D. §18 says the external_system/external_id/external_synced_at trio should replace it — unreconciled in revision 6.';
COMMENT ON COLUMN employee_contracts.end_date IS
  'Named end_date, not `end`: `end` is a reserved word (CONVENTIONS.md §1).';

-- -----------------------------------------------------------------------------
-- employee_bank_accounts — tier T. EFFECTIVE-DATED IBAN.
-- -----------------------------------------------------------------------------
CREATE TABLE employee_bank_accounts (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    employee_id             uuid            NOT NULL,
    effective_from          date            NOT NULL,
    effective_to            date,
    iban                    varchar(34)     NOT NULL,
    bank_code               varchar(16),
    account_holder_name     text,
    payment_method          varchar(40),
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_employee_bank_accounts PRIMARY KEY (id),
    CONSTRAINT uq_employee_bank_accounts__tenant_id_id UNIQUE (tenant_id, id)
);
COMMENT ON TABLE employee_bank_accounts IS
  'The effective-dated payment instruction an employee is paid to, so a WPS file filed last March can still be explained by the IBAN that was current then. @tier:T @owner:Finance @retention:Keep';
COMMENT ON COLUMN employee_bank_accounts.iban IS
  'Bounded to 34 and pattern-checked for SA IBANs by CHECK; the mod-97 checksum is enforced in the service (§13.3).';

-- -----------------------------------------------------------------------------
-- employee_documents — tier T. Every employee file, expiring ID document and
-- issued letter, in one versioned table (superseded via supersedes_id).
-- -----------------------------------------------------------------------------
CREATE TABLE employee_documents (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    employee_id             uuid            NOT NULL,
    file_id                 uuid,
    template_id             uuid,
    supersedes_id           uuid,
    leave_request_id        uuid,
    document_number         varchar(64),
    letter_number           varchar(40),
    verification_code       varchar(64),
    doc_type                varchar(40)     NOT NULL,
    status                  varchar(40)     NOT NULL DEFAULT 'Active',
    issue_date              date,
    expiry_date             date,
    issuing_country         char(2),
    version                 integer         NOT NULL DEFAULT 1,
    template_version        integer,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_employee_documents PRIMARY KEY (id),
    CONSTRAINT uq_employee_documents__tenant_id_id UNIQUE (tenant_id, id)
);
COMMENT ON TABLE employee_documents IS
  'Every document attached to a person — iqama, passport, visa, work permit, contract scan, issued letter, sick note — with its expiry, its version chain and the file that holds the blob. @tier:T @owner:HR @retention:84-months-from-Expiry-then-Purge';
COMMENT ON COLUMN employee_documents.expiry_date IS
  'Read by the expiry-reminder job straight into notifications. There is deliberately no reminder table (§2.D).';
COMMENT ON COLUMN employee_documents.supersedes_id IS
  'Renewal chain. SET NULL on delete so a chain survives a removed predecessor (§8 row 50); indexed parent-side in revision 6 (§19.4).';
COMMENT ON COLUMN employee_documents.leave_request_id IS
  'Present only for sick notes. FK deferred to the cross-domain constraints pass: leave_requests is domain J.';

-- -----------------------------------------------------------------------------
-- document_templates — tier T (company_id NULL = tenant-wide).
-- `version` is immutable once used; a change is a new version (§6 Versioned class).
-- -----------------------------------------------------------------------------
CREATE TABLE document_templates (
    id                      uuid            NOT NULL,
    tenant_id               uuid            NOT NULL,
    company_id              uuid,
    code                    varchar(64)     NOT NULL,
    kind                    varchar(40)     NOT NULL,
    version                 integer         NOT NULL DEFAULT 1,
    body_en                 text,
    body_ar                 text,
    merge_fields            jsonb,
    created_at              timestamptz     NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_document_templates PRIMARY KEY (id),
    CONSTRAINT uq_document_templates__tenant_id_id UNIQUE (tenant_id, id),
    -- [DESIGN GAP] §2.D states `version int (immutable once used)` but no uniqueness.
    -- Added: without it "version 3 of the OFFER_LETTER template" is not a single row,
    -- and payroll_slips.template_version / employee_documents.template_version — both
    -- snapshot columns — would not resolve to one body.
    CONSTRAINT uq_document_templates__code_version UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, kind, code, version)
);
COMMENT ON TABLE document_templates IS
  'Versioned letter, payslip and contract bodies in English and Arabic with their merge-field contract, scoped to one company or to the whole tenant. @tier:T @owner:HR';
COMMENT ON COLUMN document_templates.version IS
  'Immutable once a document or slip references it; a change is a NEW row with the next version (§6).';
