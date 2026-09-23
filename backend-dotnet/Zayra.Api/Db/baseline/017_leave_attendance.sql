-- =============================================================================
-- 017_leave_attendance.sql
-- KynexOne baseline schema — TARGET_SCHEMA.md revision 6.
--
-- Domain J  Leave ..................... leave_types, leave_requests,
--                                       leave_ledger
-- Domain K  Attendance, overtime,
--           timesheets ................ shifts, shift_assignments,
--                                       attendance_devices, attendance_punches,
--                                       attendance_days, overtime_requests,
--                                       timesheets, timesheet_entries,
--                                       timesheet_day_reconciliations
-- Domain L  End of service and
--           final settlement .......... eos_calculations, final_settlements,
--                                       final_settlement_lines
--
-- Tables only. Foreign keys, CHECK sets (§9), EXCLUDE constraints and the §11
-- invariants live in 021_constraints_g_r.sql.
--
-- UNIT RULE (§K, corrected design). Every attendance and timesheet duration in
-- this file is stored in WHOLE MINUTES as an integer. There is no `hours`
-- column anywhere: worked_minutes, late_minutes, early_out_minutes,
-- overtime_minutes, timesheet_entries.minutes, timesheets.total_minutes and
-- both sides of timesheet_day_reconciliations are minutes, so the variance is
-- a subtraction rather than a unit conversion. Leave is the one duration that
-- is NOT minutes: leave is granted and consumed in days, including half days,
-- so leave_requests.days and leave_ledger.days are numeric(9,2).
-- =============================================================================

-- -----------------------------------------------------------------------------
-- J. Leave
-- -----------------------------------------------------------------------------

CREATE TABLE leave_types (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    code                    varchar(32)    NOT NULL,
    name_en                 text           NOT NULL,
    name_ar                 text,
    is_statutory            boolean        NOT NULL DEFAULT false,
    is_paid                 boolean        NOT NULL DEFAULT true,
    -- resolves to statutory_rules + statutory_rule_bands for tiered sick pay
    pay_rule_key            varchar(64),
    -- the tenant's contractual ladder; the statutory floor stays in
    -- statutory_rule_bands and the engine takes max(statutory, contractual)
    policy                  jsonb          NOT NULL DEFAULT '{}'::jsonb,
    is_active               boolean        NOT NULL DEFAULT true,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_leave_types            PRIMARY KEY (id),
    CONSTRAINT uq_leave_types__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_leave_types__code      UNIQUE (tenant_id, code)
);

COMMENT ON TABLE leave_types IS
    'Defines each leave type a tenant offers together with its contractual entitlement, accrual, carry-forward and eligibility policy above the statutory floor. @tier:T @owner:HR @retention:tenant-lifecycle';

CREATE TABLE leave_requests (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    employee_id             uuid           NOT NULL,
    leave_type_id           uuid           NOT NULL,
    approval_request_id     uuid,
    request_kind            varchar(40)    NOT NULL DEFAULT 'Leave',
    status                  varchar(40)    NOT NULL DEFAULT 'Draft',
    start_date              date           NOT NULL,
    end_date                date           NOT NULL,
    return_date             date,
    days                    numeric(9,2)   NOT NULL,
    reason                  text,
    contact_during_leave    text,
    day_breakdown           jsonb          NOT NULL DEFAULT '[]'::jsonb,
    submitted_at            timestamptz,
    decided_at              timestamptz,
    cancelled_at            timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_leave_requests            PRIMARY KEY (id),
    CONSTRAINT uq_leave_requests__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE leave_requests IS
    'Captures an employee request to take leave or to encash it, with the per-day breakdown and the approval decision that turns it into a ledger movement. @tier:T @owner:HR @retention:84m-keep';

-- Append-only (§6): BEFORE UPDATE/DELETE triggers raise; a correction is a
-- Reversal row. Carries created_at and its own actor only.
CREATE TABLE leave_ledger (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    employee_id             uuid           NOT NULL,
    leave_type_id           uuid           NOT NULL,
    entry_type              varchar(40)    NOT NULL,
    entry_date              date           NOT NULL,
    days                    numeric(9,2)   NOT NULL,
    reason                  text,
    source_type             varchar(40),
    source_id               uuid,
    idempotency_key         text           NOT NULL,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    CONSTRAINT pk_leave_ledger            PRIMARY KEY (id),
    CONSTRAINT uq_leave_ledger__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_leave_ledger__idempotency_key
        UNIQUE (tenant_id, idempotency_key)
);

COMMENT ON TABLE leave_ledger IS
    'Is the append-only record of every movement in an employee leave balance, where a correction is a reversing row and the balance itself is only ever read through v_leave_balances. @tier:T @owner:HR @retention:84m-keep';

-- -----------------------------------------------------------------------------
-- K. Attendance, overtime and timesheets
-- -----------------------------------------------------------------------------

CREATE TABLE shifts (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    code                    varchar(32)    NOT NULL,
    name                    text           NOT NULL,
    start_time              time           NOT NULL,
    end_time                time           NOT NULL,
    break_minutes           integer        NOT NULL DEFAULT 0,
    crosses_midnight        boolean        NOT NULL DEFAULT false,
    weekly_off_days         text[]         NOT NULL DEFAULT '{}'::text[],
    -- grace, late/early tolerances and the annually refreshed Ramadan window
    rules                   jsonb          NOT NULL DEFAULT '{}'::jsonb,
    is_active               boolean        NOT NULL DEFAULT true,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_shifts            PRIMARY KEY (id),
    CONSTRAINT uq_shifts__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_shifts__code      UNIQUE (tenant_id, code)
);

COMMENT ON TABLE shifts IS
    'Defines a working shift with its start and end times, unpaid break, weekly off days and the grace, lateness and Ramadan rules the attendance engine applies to it. @tier:T @owner:HR @retention:tenant-lifecycle';

-- Effective-dated (§5): inclusive bounds, no-overlap per employee enforced by
-- the gist EXCLUDE in 021.
CREATE TABLE shift_assignments (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    employee_id             uuid           NOT NULL,
    shift_id                uuid           NOT NULL,
    effective_from          date           NOT NULL,
    effective_to            date,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_shift_assignments            PRIMARY KEY (id),
    CONSTRAINT uq_shift_assignments__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE shift_assignments IS
    'Rosters an employee onto a shift for an inclusive effective-dated period, with the database rejecting any overlapping assignment. @tier:T @owner:HR @retention:24m-purge';

CREATE TABLE attendance_devices (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    branch_id               uuid           NOT NULL,
    serial                  varchar(64)    NOT NULL,
    name                    text,
    model                   varchar(64),
    api_key_hash            text           NOT NULL,
    is_active               boolean        NOT NULL DEFAULT true,
    last_seen_at            timestamptz,
    -- the highest occurred_at accepted from this terminal, so a resumed sync is
    -- bounded and a gap is detectable rather than silent (§18)
    sync_watermark          timestamptz,
    -- the bounded replay window for the device punch API (§18)
    recent_nonces           jsonb          NOT NULL DEFAULT '[]'::jsonb,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_attendance_devices            PRIMARY KEY (id),
    CONSTRAINT uq_attendance_devices__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_attendance_devices__serial    UNIQUE (tenant_id, serial)
);

COMMENT ON TABLE attendance_devices IS
    'Registers a biometric or terminal device at a branch with its API credential hash, sync watermark and replay-nonce window. @tier:C @owner:HR @retention:tenant-lifecycle';

-- PARTITIONED: RANGE (occurred_at), monthly, 24-month online window (§19.3).
-- Written here as a plain table; 030_partitions.sql converts the parent and
-- creates the monthly children plus the DEFAULT catch-all.
-- Because the partition key must appear in every unique constraint on a
-- partitioned table, the primary key is (id, occurred_at), the tenant key is
-- (tenant_id, id, occurred_at), and the idempotency and device-record keys
-- both carry occurred_at as well. That is safe in practice: the idempotency
-- key is derived from (device serial, employee, occurred_at), so a duplicate
-- always arrives with the same occurred_at.
-- Immutable raw (§6): written once by ingestion; a correction is a new row
-- with source='Correction'. Exempt from row stamping.
CREATE TABLE attendance_punches (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    employee_id             uuid           NOT NULL,
    device_id               uuid,
    approval_request_id     uuid,
    direction               varchar(40)    NOT NULL,
    source                  varchar(40)    NOT NULL,
    occurred_at             timestamptz    NOT NULL,
    latitude                numeric(9,6),
    longitude               numeric(9,6),
    -- the device's own record id, so one physical punch cannot land twice
    -- under two different idempotency keys (§18)
    external_id             varchar(64),
    idempotency_key         text           NOT NULL,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    CONSTRAINT pk_attendance_punches PRIMARY KEY (id, occurred_at),
    CONSTRAINT uq_attendance_punches__tenant_id
        UNIQUE (tenant_id, id, occurred_at),
    CONSTRAINT uq_attendance_punches__idempotency_key
        UNIQUE (tenant_id, idempotency_key, occurred_at),
    CONSTRAINT uq_attendance_punches__device_external_id
        UNIQUE (tenant_id, device_id, external_id, occurred_at)
);

COMMENT ON TABLE attendance_punches IS
    'Stores every raw clock event exactly as received from a device, mobile app, import or correction, written once and never edited. @tier:T @owner:HR @retention:24m-purge';

-- PARTITIONED: RANGE (work_date), monthly, 24-month online window (§19.3).
-- Unique constraints all carry work_date; (tenant_id, employee_id, work_date)
-- already contained the partition key and is unchanged.
-- This is the one partitioned table that is an FK target: see
-- timesheet_day_reconciliations below and §8 row 126.
CREATE TABLE attendance_days (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    employee_id             uuid           NOT NULL,
    shift_id                uuid,
    locked_run_id           uuid,
    status                  varchar(40)    NOT NULL,
    -- a LOCAL date in the company timezone, computed once at write and never
    -- re-derived from a UTC instant at read time (§13.4)
    work_date               date           NOT NULL,
    first_in                timestamptz,
    last_out                timestamptz,
    scheduled_minutes       integer        NOT NULL DEFAULT 0,
    worked_minutes          integer        NOT NULL DEFAULT 0,
    break_minutes           integer        NOT NULL DEFAULT 0,
    late_minutes            integer        NOT NULL DEFAULT 0,
    early_out_minutes       integer        NOT NULL DEFAULT 0,
    overtime_minutes        integer        NOT NULL DEFAULT 0,
    absent_minutes          integer        NOT NULL DEFAULT 0,
    exceptions              jsonb          NOT NULL DEFAULT '[]'::jsonb,
    computed_at             timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_attendance_days PRIMARY KEY (id, work_date),
    CONSTRAINT uq_attendance_days__tenant_id
        UNIQUE (tenant_id, id, work_date),
    CONSTRAINT uq_attendance_days__employee_work_date
        UNIQUE (tenant_id, employee_id, work_date)
);

COMMENT ON TABLE attendance_days IS
    'Holds the computed attendance result for one employee on one local working day in whole minutes, with its exceptions and the payroll run that locked it. @tier:T @owner:HR @retention:24m-purge';

CREATE TABLE overtime_requests (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    employee_id             uuid           NOT NULL,
    statutory_rule_id       uuid,
    approval_request_id     uuid,
    ot_type                 varchar(40)    NOT NULL,
    payout                  varchar(40)    NOT NULL DEFAULT 'Pay',
    status                  varchar(40)    NOT NULL DEFAULT 'Draft',
    work_date               date           NOT NULL,
    overtime_minutes        integer        NOT NULL,
    reason                  text,
    -- frozen calculation: the hourly rate and the Art. 107 multiplier in force
    -- when the claim was raised, so the amount can be reconstructed
    basic_hourly_rate       numeric(18,2)  NOT NULL,
    multiplier              numeric(9,6)   NOT NULL,
    amount                  numeric(18,2)  NOT NULL,
    rules_version           varchar(40),
    decided_at              timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_overtime_requests            PRIMARY KEY (id),
    CONSTRAINT uq_overtime_requests__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE overtime_requests IS
    'Records an overtime claim in minutes for one local working day with the hourly rate, statutory multiplier and amount frozen at the moment it was calculated. @tier:T @owner:HR @retention:84m-keep';

CREATE TABLE timesheets (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    company_id              uuid           NOT NULL,
    employee_id             uuid           NOT NULL,
    approval_request_id     uuid,
    locked_run_id           uuid,
    -- allocated from number_sequences scope_key 'timesheet_no' (§A)
    timesheet_number        varchar(40),
    status                  varchar(40)    NOT NULL DEFAULT 'Draft',
    period_start            date           NOT NULL,
    period_end              date           NOT NULL,
    total_minutes           integer        NOT NULL DEFAULT 0,
    submitted_at            timestamptz,
    decided_at              timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_timesheets            PRIMARY KEY (id),
    CONSTRAINT uq_timesheets__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_timesheets__timesheet_number
        UNIQUE (tenant_id, timesheet_number),
    CONSTRAINT uq_timesheets__employee_period
        UNIQUE (tenant_id, employee_id, period_start)
);

COMMENT ON TABLE timesheets IS
    'Groups one employee logged working minutes for a period into a single submittable, approvable and lockable record. @tier:C @owner:HR @retention:24m-purge';

-- PARTITIONED: RANGE (work_date), monthly, 24-month online window (§19.3).
-- Primary key (id, work_date); tenant key (tenant_id, id, work_date).
CREATE TABLE timesheet_entries (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    timesheet_id            uuid           NOT NULL,
    cost_center_id          uuid,
    work_date               date           NOT NULL,
    minutes                 integer        NOT NULL,
    project_code            varchar(64),
    task                    text,
    billable                boolean        NOT NULL DEFAULT false,
    rate_source             varchar(40),
    notes                   text,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_timesheet_entries PRIMARY KEY (id, work_date),
    CONSTRAINT uq_timesheet_entries__tenant_id
        UNIQUE (tenant_id, id, work_date)
);

COMMENT ON TABLE timesheet_entries IS
    'Logs minutes worked on one local day against a cost centre, project and task, and is the only place the project and client dimension of time is captured. @tier:C @owner:HR @retention:24m-purge';

CREATE TABLE timesheet_day_reconciliations (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    timesheet_id            uuid           NOT NULL,
    attendance_day_id       uuid,
    status                  varchar(40)    NOT NULL DEFAULT 'Open',
    work_date               date           NOT NULL,
    -- both sides in the SAME unit so the variance is a subtraction, not a
    -- conversion; the invariant is a CHECK in 021 (§11.2)
    timesheet_minutes       integer        NOT NULL DEFAULT 0,
    attendance_minutes      integer        NOT NULL DEFAULT 0,
    variance_minutes        integer        NOT NULL DEFAULT 0,
    explanation             text,
    -- plain uuid: §8 registers no FK for this column, and the reviewer may be
    -- anonymised without the exception losing its meaning
    resolved_by             uuid,
    resolved_at             timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_timesheet_day_reconciliations            PRIMARY KEY (id),
    CONSTRAINT uq_timesheet_day_reconciliations__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_timesheet_day_reconciliations__day
        UNIQUE (tenant_id, timesheet_id, work_date)
);

COMMENT ON TABLE timesheet_day_reconciliations IS
    'Compares the minutes an employee booked on a timesheet day against the minutes attendance computed for the same day and holds the variance until it is explained or accepted. @tier:C @owner:HR @retention:24m-purge';

-- -----------------------------------------------------------------------------
-- L. End of service and final settlement
-- -----------------------------------------------------------------------------

CREATE TABLE eos_calculations (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    employee_id             uuid           NOT NULL,
    settlement_id           uuid,
    status                  varchar(40)    NOT NULL DEFAULT 'Estimate',
    separation_reason       varchar(40)    NOT NULL,
    calculation_date        date           NOT NULL,
    service_start_date      date           NOT NULL,
    service_end_date        date           NOT NULL,
    service_days            integer        NOT NULL,
    excluded_unpaid_days    integer        NOT NULL DEFAULT 0,
    last_wage_basis         varchar(40),
    eligible_wage           numeric(18,2)  NOT NULL DEFAULT 0,
    amount                  numeric(18,2)  NOT NULL DEFAULT 0,
    prior_paid_deducted     numeric(18,2)  NOT NULL DEFAULT 0,
    rules_version           varchar(40),
    -- the resolved Art. 84/85 bands copied in, so a settlement can be
    -- reconstructed years later without re-resolving reference data
    rules_snapshot          jsonb          NOT NULL DEFAULT '{}'::jsonb,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_eos_calculations            PRIMARY KEY (id),
    CONSTRAINT uq_eos_calculations__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE eos_calculations IS
    'Computes an end-of-service award under the Labour Law articles that apply to the separation, freezing the resolved statutory bands so the figure can be reconstructed years later. @tier:T @owner:Finance @retention:84m-keep';

CREATE TABLE final_settlements (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    company_id              uuid           NOT NULL,
    employee_id             uuid           NOT NULL,
    paid_via_run_id         uuid,
    approval_request_id     uuid,
    settlement_number       varchar(40),
    separation_type         varchar(40)    NOT NULL,
    status                  varchar(40)    NOT NULL DEFAULT 'Draft',
    last_working_day        date           NOT NULL,
    notice_given_on         date,
    notice_served_days      integer,
    gross                   numeric(18,2)  NOT NULL DEFAULT 0,
    deductions              numeric(18,2)  NOT NULL DEFAULT 0,
    net                     numeric(18,2)  NOT NULL DEFAULT 0,
    -- a schema-validated array of {key, required, done_by, done_at}; an undone
    -- required item blocks Draft -> PendingApproval (§10.10)
    clearance               jsonb          NOT NULL DEFAULT '[]'::jsonb,
    approved_at             timestamptz,
    paid_at                 timestamptz,
    cancelled_at            timestamptz,
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    updated_at              timestamptz,
    updated_by              uuid,
    CONSTRAINT pk_final_settlements            PRIMARY KEY (id),
    CONSTRAINT uq_final_settlements__tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT uq_final_settlements__settlement_number
        UNIQUE (tenant_id, settlement_number)
);

COMMENT ON TABLE final_settlements IS
    'Is the separation case and settlement header for one employee, carrying the clearance checklist, the approved totals and the payroll run the settlement was paid through. @tier:C @owner:Finance @retention:84m-keep';

CREATE TABLE final_settlement_lines (
    id                      uuid           NOT NULL,
    tenant_id               uuid           NOT NULL,
    settlement_id           uuid           NOT NULL,
    loan_installment_id     uuid,
    kind                    varchar(40)    NOT NULL,
    description             text,
    amount                  numeric(18,2)  NOT NULL,
    source_type             varchar(40),
    source_id               uuid,
    rules_version           varchar(40),
    created_at              timestamptz    NOT NULL DEFAULT now(),
    created_by              uuid,
    CONSTRAINT pk_final_settlement_lines            PRIMARY KEY (id),
    CONSTRAINT uq_final_settlement_lines__tenant_id UNIQUE (tenant_id, id)
);

COMMENT ON TABLE final_settlement_lines IS
    'Itemises a final settlement into its end-of-service, encashment, notice-pay and recovery components, frozen once the settlement is approved. @tier:C @owner:Finance @retention:84m-keep';
