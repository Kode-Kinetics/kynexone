-- =============================================================================
-- 021_constraints_g_r.sql
-- KynexOne baseline schema — TARGET_SCHEMA.md revision 6.
--
-- Constraints for domains G-R (the tables created by 016, 017 and 018):
--   1. Foreign keys, with the exact ON DELETE of the §8.2 register, including
--      the ones pointing at domains A-F.
--   2. The §9 status CHECKs, under the constraint names §9 itself gives, plus
--      the remaining closed sets §9's closing paragraph defers to the same
--      rule.
--   3. The §11 invariants that are expressible as constraints, and the
--      structural CHECKs that keep a period, a range or a duration sane.
--   4. The §5 daterange EXCLUDE for the effective-dated tables in this scope
--      and the §E numrange EXCLUDE for the Nitaqat band grid.
--
-- Requires btree_gist (001_extensions.sql) for every EXCLUDE below.
--
-- ON UPDATE is RESTRICT on every foreign key without exception: keys are uuid
-- and are never updated (§8.1).
--
-- COMPOSITE KEYS. Every foreign key between two tenant-tier tables is written
-- (tenant_id, x_id) REFERENCES x (tenant_id, id), so a row can never point
-- across tenants even through an application bug (§Conventions, §8.1). Four
-- parents are reached by a single-column key instead, each for a stated
-- reason:
--   * statutory_rules and nitaqat_grid are reference tier and carry no
--     tenant_id at all.
--   * background_jobs carries a NULLABLE tenant_id (platform jobs), so a
--     composite reference from a NULL-tenant child would silently not be
--     enforced under MATCH SIMPLE. §8 rows 87, 154 and 155 write these
--     single-column and this file follows them.
--
-- ON DELETE SET NULL ON A COMPOSITE KEY. A bare SET NULL would null tenant_id
-- too, which is NOT NULL on every tenant table, so the delete would fail at
-- runtime rather than release the optional pointer. PostgreSQL 15+ column-list
-- SET NULL is used instead: ON DELETE SET NULL (<the optional column only>).
-- =============================================================================


-- =============================================================================
-- 1. FOREIGN KEYS
-- =============================================================================

-- -----------------------------------------------------------------------------
-- G. WPS  (§8 rows 79-87)
-- -----------------------------------------------------------------------------

ALTER TABLE wps_batches
    ADD CONSTRAINT fk_wps_batches__run_id
        FOREIGN KEY (tenant_id, run_id) REFERENCES payroll_runs (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_wps_batches__company_id
        FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_wps_batches__file_id
        FOREIGN KEY (tenant_id, file_id) REFERENCES files (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_wps_batches__resubmission_of_id
        FOREIGN KEY (tenant_id, resubmission_of_id) REFERENCES wps_batches (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_wps_batches__generated_by
        FOREIGN KEY (tenant_id, generated_by) REFERENCES users (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE SET NULL (generated_by);

ALTER TABLE wps_lines
    -- CASCADE is guarded: 050_triggers.sql raises on DELETE of a wps_batches
    -- row that has left 'Generated', so the cascade can only fire on a draft.
    ADD CONSTRAINT fk_wps_lines__batch_id
        FOREIGN KEY (tenant_id, batch_id) REFERENCES wps_batches (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE CASCADE,
    ADD CONSTRAINT fk_wps_lines__slip_id
        FOREIGN KEY (tenant_id, slip_id) REFERENCES payroll_slips (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_wps_lines__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    -- single-column: background_jobs.tenant_id is nullable (§8 row 87)
    ADD CONSTRAINT fk_wps_lines__confirmation_job_id
        FOREIGN KEY (confirmation_job_id) REFERENCES background_jobs (id)
        ON UPDATE RESTRICT ON DELETE SET NULL;

-- -----------------------------------------------------------------------------
-- H. GL export  (§8 rows 88-96)
-- -----------------------------------------------------------------------------

ALTER TABLE gl_mappings
    ADD CONSTRAINT fk_gl_mappings__company_id
        FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_gl_mappings__cost_center_id
        FOREIGN KEY (tenant_id, cost_center_id) REFERENCES cost_centers (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE gl_journals
    ADD CONSTRAINT fk_gl_journals__company_id
        FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_gl_journals__file_id
        FOREIGN KEY (tenant_id, file_id) REFERENCES files (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_gl_journals__reversal_of_id
        FOREIGN KEY (tenant_id, reversal_of_id) REFERENCES gl_journals (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE gl_journal_lines
    -- CASCADE guarded: the guard raises unless the journal is 'Draft'.
    ADD CONSTRAINT fk_gl_journal_lines__journal_id
        FOREIGN KEY (tenant_id, journal_id) REFERENCES gl_journals (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE CASCADE,
    ADD CONSTRAINT fk_gl_journal_lines__cost_center_id
        FOREIGN KEY (tenant_id, cost_center_id) REFERENCES cost_centers (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE gl_period_closes
    ADD CONSTRAINT fk_gl_period_closes__company_id
        FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_gl_period_closes__closed_by
        FOREIGN KEY (tenant_id, closed_by) REFERENCES users (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_gl_period_closes__reopened_by
        FOREIGN KEY (tenant_id, reopened_by) REFERENCES users (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

-- -----------------------------------------------------------------------------
-- I. Loans and advances  (§8 rows 97-99)
-- -----------------------------------------------------------------------------

ALTER TABLE loans
    ADD CONSTRAINT fk_loans__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_loans__approval_request_id
        FOREIGN KEY (tenant_id, approval_request_id) REFERENCES approval_requests (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE loan_installments
    -- CASCADE guarded: the guard raises once any installment is 'Recovered'.
    ADD CONSTRAINT fk_loan_installments__loan_id
        FOREIGN KEY (tenant_id, loan_id) REFERENCES loans (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE CASCADE;

-- -----------------------------------------------------------------------------
-- M. Nitaqat  (§8 row 135)
-- -----------------------------------------------------------------------------

ALTER TABLE nitaqat_snapshots
    ADD CONSTRAINT fk_nitaqat_snapshots__company_id
        FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

-- -----------------------------------------------------------------------------
-- N. GOSI registration and filing  (§8 rows 136-142)
-- -----------------------------------------------------------------------------

ALTER TABLE employee_gosi_registrations
    ADD CONSTRAINT fk_employee_gosi_registrations__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_employee_gosi_registrations__company_id
        FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    -- §11.5: companies.gosi_registration_no is the only writable copy, so the
    -- filing identifier is reached by FK and a typo can exist in one place.
    -- Requires UNIQUE (tenant_id, gosi_registration_no) on companies.
    ADD CONSTRAINT fk_employee_gosi_registrations__gosi_registration_no
        FOREIGN KEY (tenant_id, gosi_registration_no)
        REFERENCES companies (tenant_id, gosi_registration_no)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE gosi_filings
    ADD CONSTRAINT fk_gosi_filings__company_id
        FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_gosi_filings__gosi_registration_no
        FOREIGN KEY (tenant_id, gosi_registration_no)
        REFERENCES companies (tenant_id, gosi_registration_no)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_gosi_filings__file_id
        FOREIGN KEY (tenant_id, file_id) REFERENCES files (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_gosi_filings__filed_by
        FOREIGN KEY (tenant_id, filed_by) REFERENCES users (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

-- -----------------------------------------------------------------------------
-- J. Leave  (§8 rows 100-105)
-- -----------------------------------------------------------------------------

ALTER TABLE leave_types
    ADD CONSTRAINT fk_leave_types__tenant_id
        FOREIGN KEY (tenant_id) REFERENCES tenants (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE leave_requests
    ADD CONSTRAINT fk_leave_requests__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_leave_requests__leave_type_id
        FOREIGN KEY (tenant_id, leave_type_id) REFERENCES leave_types (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_leave_requests__approval_request_id
        FOREIGN KEY (tenant_id, approval_request_id) REFERENCES approval_requests (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE leave_ledger
    ADD CONSTRAINT fk_leave_ledger__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_leave_ledger__leave_type_id
        FOREIGN KEY (tenant_id, leave_type_id) REFERENCES leave_types (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

-- -----------------------------------------------------------------------------
-- K. Attendance, overtime and timesheets  (§8 rows 106-126)
-- -----------------------------------------------------------------------------

ALTER TABLE shifts
    ADD CONSTRAINT fk_shifts__tenant_id
        FOREIGN KEY (tenant_id) REFERENCES tenants (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE shift_assignments
    ADD CONSTRAINT fk_shift_assignments__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_shift_assignments__shift_id
        FOREIGN KEY (tenant_id, shift_id) REFERENCES shifts (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE attendance_devices
    ADD CONSTRAINT fk_attendance_devices__branch_id
        FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE attendance_punches
    ADD CONSTRAINT fk_attendance_punches__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_attendance_punches__device_id
        FOREIGN KEY (tenant_id, device_id) REFERENCES attendance_devices (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE SET NULL (device_id),
    ADD CONSTRAINT fk_attendance_punches__approval_request_id
        FOREIGN KEY (tenant_id, approval_request_id) REFERENCES approval_requests (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE attendance_days
    ADD CONSTRAINT fk_attendance_days__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_attendance_days__shift_id
        FOREIGN KEY (tenant_id, shift_id) REFERENCES shifts (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE SET NULL (shift_id),
    -- voiding or deleting a draft run unlocks the days (§8 row 115)
    ADD CONSTRAINT fk_attendance_days__locked_run_id
        FOREIGN KEY (tenant_id, locked_run_id) REFERENCES payroll_runs (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE SET NULL (locked_run_id);

ALTER TABLE overtime_requests
    ADD CONSTRAINT fk_overtime_requests__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    -- single-column: statutory_rules is reference tier and has no tenant_id
    ADD CONSTRAINT fk_overtime_requests__statutory_rule_id
        FOREIGN KEY (statutory_rule_id) REFERENCES statutory_rules (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_overtime_requests__approval_request_id
        FOREIGN KEY (tenant_id, approval_request_id) REFERENCES approval_requests (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE timesheets
    ADD CONSTRAINT fk_timesheets__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_timesheets__company_id
        FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_timesheets__approval_request_id
        FOREIGN KEY (tenant_id, approval_request_id) REFERENCES approval_requests (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_timesheets__locked_run_id
        FOREIGN KEY (tenant_id, locked_run_id) REFERENCES payroll_runs (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE SET NULL (locked_run_id);

ALTER TABLE timesheet_entries
    -- CASCADE guarded: the guard raises unless the timesheet is Draft or
    -- Rejected, which are the only states in which entries are editable.
    ADD CONSTRAINT fk_timesheet_entries__timesheet_id
        FOREIGN KEY (tenant_id, timesheet_id) REFERENCES timesheets (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE CASCADE,
    ADD CONSTRAINT fk_timesheet_entries__cost_center_id
        FOREIGN KEY (tenant_id, cost_center_id) REFERENCES cost_centers (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE timesheet_day_reconciliations
    ADD CONSTRAINT fk_timesheet_day_reconciliations__timesheet_id
        FOREIGN KEY (tenant_id, timesheet_id) REFERENCES timesheets (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE CASCADE,
    -- §8 row 126, §19.3 consequence 2: the only FK in the baseline that
    -- targets a partitioned table. The partition key is part of the key
    -- because every unique constraint on attendance_days must contain it; the
    -- reconciliation row already carries work_date, so no new column is needed.
    ADD CONSTRAINT fk_timesheet_day_reconciliations__attendance_day_id
        FOREIGN KEY (tenant_id, attendance_day_id, work_date)
        REFERENCES attendance_days (tenant_id, id, work_date)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

-- -----------------------------------------------------------------------------
-- L. End of service and final settlement  (§8 rows 127-134)
-- -----------------------------------------------------------------------------

ALTER TABLE eos_calculations
    ADD CONSTRAINT fk_eos_calculations__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    -- an Estimate has no settlement yet, and losing one does not void the
    -- calculation (§8 row 128)
    ADD CONSTRAINT fk_eos_calculations__settlement_id
        FOREIGN KEY (tenant_id, settlement_id) REFERENCES final_settlements (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE SET NULL (settlement_id);

ALTER TABLE final_settlements
    ADD CONSTRAINT fk_final_settlements__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_final_settlements__company_id
        FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_final_settlements__paid_via_run_id
        FOREIGN KEY (tenant_id, paid_via_run_id) REFERENCES payroll_runs (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_final_settlements__approval_request_id
        FOREIGN KEY (tenant_id, approval_request_id) REFERENCES approval_requests (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE final_settlement_lines
    -- CASCADE guarded: the guard raises unless the settlement is 'Draft'.
    ADD CONSTRAINT fk_final_settlement_lines__settlement_id
        FOREIGN KEY (tenant_id, settlement_id) REFERENCES final_settlements (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE CASCADE,
    -- the typed FK that replaced the polymorphic pointer for loan recovery,
    -- and one half of how §8.4 broke the slip-line/installment cycle
    ADD CONSTRAINT fk_final_settlement_lines__loan_installment_id
        FOREIGN KEY (tenant_id, loan_installment_id) REFERENCES loan_installments (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

-- -----------------------------------------------------------------------------
-- O. Approvals  (§8 rows 143-150)
-- -----------------------------------------------------------------------------

ALTER TABLE approval_workflows
    ADD CONSTRAINT fk_approval_workflows__company_id
        FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE approval_requests
    ADD CONSTRAINT fk_approval_requests__workflow_id
        FOREIGN KEY (tenant_id, workflow_id) REFERENCES approval_workflows (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_approval_requests__requester_user_id
        FOREIGN KEY (tenant_id, requester_user_id) REFERENCES users (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_approval_requests__employee_id
        FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE approval_actions
    ADD CONSTRAINT fk_approval_actions__request_id
        FOREIGN KEY (tenant_id, request_id) REFERENCES approval_requests (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_approval_actions__actor_user_id
        FOREIGN KEY (tenant_id, actor_user_id) REFERENCES users (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    ADD CONSTRAINT fk_approval_actions__on_behalf_of_user_id
        FOREIGN KEY (tenant_id, on_behalf_of_user_id) REFERENCES users (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE SET NULL (on_behalf_of_user_id);

ALTER TABLE approval_delegations
    ADD CONSTRAINT fk_approval_delegations__delegator_user_id
        FOREIGN KEY (tenant_id, delegator_user_id) REFERENCES users (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE CASCADE,
    ADD CONSTRAINT fk_approval_delegations__delegate_user_id
        FOREIGN KEY (tenant_id, delegate_user_id) REFERENCES users (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE CASCADE;

-- -----------------------------------------------------------------------------
-- P. Notifications  (§8 rows 151-152)
-- -----------------------------------------------------------------------------

ALTER TABLE notifications
    ADD CONSTRAINT fk_notifications__user_id
        FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE CASCADE;

ALTER TABLE notification_deliveries
    ADD CONSTRAINT fk_notification_deliveries__notification_id
        FOREIGN KEY (tenant_id, notification_id) REFERENCES notifications (tenant_id, id)
        ON UPDATE RESTRICT ON DELETE CASCADE;

-- -----------------------------------------------------------------------------
-- Q. Audit  (§8, the unnumbered final row)
-- -----------------------------------------------------------------------------

-- audit_logs, payroll_audit_logs and retention_purge_audits carry NO foreign
-- key of any kind, deliberately, so the evidence outlives every purge and can
-- still describe a row that no longer exists. Orphan detection lives in §16.3.

-- -----------------------------------------------------------------------------
-- R. Jobs  (§8 rows 153-155)
-- -----------------------------------------------------------------------------

ALTER TABLE background_jobs
    -- NULL tenant_id = a platform job (§8 row 153)
    ADD CONSTRAINT fk_background_jobs__tenant_id
        FOREIGN KEY (tenant_id) REFERENCES tenants (id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    -- single-column: background_jobs.tenant_id may be NULL, so a composite
    -- reference would not be enforced for platform jobs (§8 row 154)
    ADD CONSTRAINT fk_background_jobs__source_file_id
        FOREIGN KEY (source_file_id) REFERENCES files (id)
        ON UPDATE RESTRICT ON DELETE SET NULL;

ALTER TABLE background_job_items
    -- single-column for the same reason (§8 row 155)
    ADD CONSTRAINT fk_background_job_items__job_id
        FOREIGN KEY (job_id) REFERENCES background_jobs (id)
        ON UPDATE RESTRICT ON DELETE CASCADE;


-- =============================================================================
-- 2. STATUS AND CLOSED-SET CHECKS
--
-- §9 registers twenty enumerated columns in this scope and names each
-- constraint; those names are used verbatim below, in preference to the
-- ck_<table>__<assertion> pattern of CONVENTIONS §1, because §9 is the
-- document CI compares the C# constants classes against.
--
-- §9's closing paragraph defers the remaining closed sets to the same rule
-- without naming them; those carry the ck_ pattern. Value sets marked
-- "PROPOSED" are ones no section enumerates: they are written here so the
-- column is governed rather than free text, and they need a §9 row before the
-- baseline is signed off.
-- =============================================================================

-- §9 row 15
ALTER TABLE wps_batches ADD CONSTRAINT chk_wps_batches_status
    CHECK (status IN ('Generated','Submitted','Accepted','PartiallyRejected','Rejected','Superseded'));

-- §9 row 16
ALTER TABLE wps_lines ADD CONSTRAINT chk_wps_lines_bank_status
    CHECK (bank_status IN ('Pending','Paid','Rejected','Returned','OnHold'));

-- §9 row 17
ALTER TABLE gl_journals ADD CONSTRAINT chk_gl_journals_status
    CHECK (status IN ('Draft','Exported','Posted','Rejected','Reversed'));

ALTER TABLE gl_journals ADD CONSTRAINT ck_gl_journals__source_type
    CHECK (source_type IN ('PayrollRun','FinalSettlement','LoanDisbursement','Reversal'));

-- §9 row 18
ALTER TABLE gl_period_closes ADD CONSTRAINT chk_gl_period_closes_status
    CHECK (status IN ('Open','Closed','Reopened'));

-- §9 row 19
ALTER TABLE loans ADD CONSTRAINT chk_loans_status
    CHECK (status IN ('PendingApproval','Active','Settled','Cancelled','Rejected'));

ALTER TABLE loans ADD CONSTRAINT ck_loans__kind
    CHECK (kind IN ('Loan','Advance'));

-- §9 row 20
ALTER TABLE loan_installments ADD CONSTRAINT chk_loan_installments_status
    CHECK (status IN ('Due','Recovered','Waived','Cancelled'));

ALTER TABLE loan_installments ADD CONSTRAINT ck_loan_installments__kind
    CHECK (kind IN ('Scheduled','EarlySettlement','FinalSettlement','Waiver'));

-- §9 row 21
ALTER TABLE leave_requests ADD CONSTRAINT chk_leave_requests_status
    CHECK (status IN ('Draft','PendingApproval','Approved','Rejected','Cancelled','Taken'));

ALTER TABLE leave_requests ADD CONSTRAINT ck_leave_requests__request_kind
    CHECK (request_kind IN ('Leave','Encashment'));

ALTER TABLE leave_ledger ADD CONSTRAINT ck_leave_ledger__entry_type
    CHECK (entry_type IN ('Opening','Accrual','Debit','Reversal','CarryForward','Expiry','Encashment','Adjustment'));

-- §9 row 22
ALTER TABLE overtime_requests ADD CONSTRAINT chk_overtime_requests_status
    CHECK (status IN ('Draft','PendingApproval','Approved','Rejected','Cancelled','Paid','ConvertedToCompOff'));

ALTER TABLE overtime_requests ADD CONSTRAINT ck_overtime_requests__payout
    CHECK (payout IN ('Pay','CompOff'));

-- PROPOSED: no section enumerates ot_type.
ALTER TABLE overtime_requests ADD CONSTRAINT ck_overtime_requests__ot_type
    CHECK (ot_type IN ('Normal','WeeklyOff','PublicHoliday','Ramadan'));

-- §9 row 23
ALTER TABLE attendance_days ADD CONSTRAINT chk_attendance_days_status
    CHECK (status IN ('Present','Absent','Leave','Holiday','WeeklyOff','Incomplete'));

ALTER TABLE attendance_punches ADD CONSTRAINT ck_attendance_punches__source
    CHECK (source IN ('Device','Mobile','Import','Correction'));

-- PROPOSED: no section enumerates direction.
ALTER TABLE attendance_punches ADD CONSTRAINT ck_attendance_punches__direction
    CHECK (direction IN ('In','Out'));

-- §9 row 24
ALTER TABLE timesheets ADD CONSTRAINT chk_timesheets_status
    CHECK (status IN ('Draft','Submitted','Approved','Rejected','Locked'));

-- §9 row 25
ALTER TABLE timesheet_day_reconciliations ADD CONSTRAINT chk_tsdr_status
    CHECK (status IN ('Open','Explained','Accepted','Rejected'));

-- §9 row 26
ALTER TABLE eos_calculations ADD CONSTRAINT chk_eos_calculations_status
    CHECK (status IN ('Estimate','Final','Superseded'));

-- The four Labour Law articles §L names for an end-of-service award.
ALTER TABLE eos_calculations ADD CONSTRAINT ck_eos_calculations__separation_reason
    CHECK (separation_reason IN ('Art84','Art85','Art87','Art77'));

-- §9 row 27
ALTER TABLE final_settlements ADD CONSTRAINT chk_final_settlements_status
    CHECK (status IN ('Draft','PendingApproval','Approved','Paid','Cancelled'));

-- PROPOSED: no section enumerates separation_type.
ALTER TABLE final_settlements ADD CONSTRAINT ck_final_settlements__separation_type
    CHECK (separation_type IN ('Resignation','Termination','EndOfContract','Retirement','Death','Abscond'));

ALTER TABLE final_settlement_lines ADD CONSTRAINT ck_final_settlement_lines__kind
    CHECK (kind IN ('EOS','LeaveEncashment','UnpaidSalary','NoticePay','Art77Compensation','LoanRecovery','OtherDeduction'));

-- §E: the five Nitaqat colour bands.
ALTER TABLE nitaqat_grid ADD CONSTRAINT ck_nitaqat_grid__band
    CHECK (band IN ('Platinum','HighGreen','MidGreen','LowGreen','Red'));

ALTER TABLE nitaqat_snapshots ADD CONSTRAINT ck_nitaqat_snapshots__band
    CHECK (band IN ('Platinum','HighGreen','MidGreen','LowGreen','Red'));

-- §9 row 28
ALTER TABLE gosi_filings ADD CONSTRAINT chk_gosi_filings_status
    CHECK (status IN ('Draft','Filed','Reconciled','Disputed','Superseded'));

-- §9 row 29
ALTER TABLE employee_gosi_registrations ADD CONSTRAINT chk_egr_status
    CHECK (status IN ('Registered','Suspended','Deregistered'));

-- §9 row 30
ALTER TABLE approval_requests ADD CONSTRAINT chk_approval_requests_status
    CHECK (status IN ('Draft','Pending','Approved','Rejected','Returned','Cancelled','Expired'));

-- §O: the thirteen registered request types, including 'Timesheet'. The same
-- set governs approval_requests.request_type and, per §16.1, subject_type.
ALTER TABLE approval_workflows ADD CONSTRAINT ck_approval_workflows__request_type
    CHECK (request_type IN ('Leave','LeaveCancel','Overtime','Loan','Advance','PayrollRun',
                            'FinalSettlement','ProfileChange','Transfer','SalaryChange',
                            'LetterRequest','AttendanceCorrection','Timesheet'));

ALTER TABLE approval_requests ADD CONSTRAINT ck_approval_requests__request_type
    CHECK (request_type IN ('Leave','LeaveCancel','Overtime','Loan','Advance','PayrollRun',
                            'FinalSettlement','ProfileChange','Transfer','SalaryChange',
                            'LetterRequest','AttendanceCorrection','Timesheet'));

ALTER TABLE approval_requests ADD CONSTRAINT ck_approval_requests__subject_type
    CHECK (subject_type IN ('Leave','LeaveCancel','Overtime','Loan','Advance','PayrollRun',
                            'FinalSettlement','ProfileChange','Transfer','SalaryChange',
                            'LetterRequest','AttendanceCorrection','Timesheet'));

-- §9 row 31
ALTER TABLE approval_actions ADD CONSTRAINT chk_approval_actions_action
    CHECK (action IN ('Approve','Reject','Return','Comment','Escalate'));

-- §9 row 32, including the DeadLettered terminal state §18 adds.
ALTER TABLE notification_deliveries ADD CONSTRAINT chk_notification_deliveries_status
    CHECK (status IN ('Queued','Sent','Delivered','Failed','Suppressed','DeadLettered'));

-- PROPOSED: §P names the three channels in prose but §9 has no row.
ALTER TABLE notification_deliveries ADD CONSTRAINT ck_notification_deliveries__channel
    CHECK (channel IN ('Email','Sms','Push'));

-- The audit categories §Q lists.
ALTER TABLE audit_logs ADD CONSTRAINT ck_audit_logs__category
    CHECK (category IS NULL OR category IN ('Auth','Admin','Employee','Leave','Attendance','Loan','Document','ESS'));

ALTER TABLE audit_logs ADD CONSTRAINT ck_audit_logs__record_kind
    CHECK (record_kind IN ('Event','Checkpoint'));

-- §9 row 36's disposition set, matched to retention_policies.disposition.
ALTER TABLE retention_purge_audits ADD CONSTRAINT ck_retention_purge_audits__disposition
    CHECK (disposition IN ('Anonymise','Purge','Keep'));

-- PROPOSED: no section enumerates outcome.
ALTER TABLE retention_purge_audits ADD CONSTRAINT ck_retention_purge_audits__outcome
    CHECK (outcome IN ('Applied','Skipped','Failed'));

-- §9 row 33
ALTER TABLE background_jobs ADD CONSTRAINT chk_background_jobs_status
    CHECK (status IN ('Queued','Leased','Running','Succeeded','Failed','Cancelled'));

-- §9 row 34
ALTER TABLE background_job_items ADD CONSTRAINT chk_background_job_items_status
    CHECK (status IN ('Pending','Succeeded','Failed','Skipped'));


-- =============================================================================
-- 3. INVARIANTS AND STRUCTURAL CHECKS
--
-- The §11.2 invariants that are constraints rather than triggers, plus the
-- period, range and duration checks that keep a row meaningful on its own.
-- The invariants that need to see sibling rows (slip totals, run totals, WPS
-- totals, loan outstanding, timesheet total minutes, settlement totals, GOSI
-- filing totals, GL journal balance) are deferred constraint triggers and
-- belong to 050_triggers.sql, not here.
-- =============================================================================

-- §11.2: the timesheet-day variance, with BOTH sides in minutes so this is a
-- subtraction and not a unit conversion.
ALTER TABLE timesheet_day_reconciliations
    ADD CONSTRAINT ck_timesheet_day_reconciliations__variance
        CHECK (variance_minutes = timesheet_minutes - attendance_minutes),
    ADD CONSTRAINT ck_timesheet_day_reconciliations__minutes_non_negative
        CHECK (timesheet_minutes >= 0 AND attendance_minutes >= 0);

-- §H: a GL line is a debit or a credit, never both, and never negative.
-- SUM(debit) = SUM(credit) per journal is a deferred trigger, not a CHECK.
ALTER TABLE gl_journal_lines
    ADD CONSTRAINT ck_gl_journal_lines__debit_xor_credit
        CHECK (debit * credit = 0),
    ADD CONSTRAINT ck_gl_journal_lines__amounts_non_negative
        CHECK (debit >= 0 AND credit >= 0);

-- Accounting and payroll periods are a year plus a month, as two columns (§C).
ALTER TABLE gl_journals
    ADD CONSTRAINT ck_gl_journals__period
        CHECK (period_month BETWEEN 1 AND 12 AND period_year BETWEEN 2000 AND 2200);

ALTER TABLE gl_period_closes
    ADD CONSTRAINT ck_gl_period_closes__period
        CHECK (period_month BETWEEN 1 AND 12 AND period_year BETWEEN 2000 AND 2200);

ALTER TABLE loans
    ADD CONSTRAINT ck_loans__start_period
        CHECK (start_period_month BETWEEN 1 AND 12 AND start_period_year BETWEEN 2000 AND 2200),
    ADD CONSTRAINT ck_loans__amounts
        CHECK (principal >= 0 AND opening_outstanding >= 0 AND outstanding >= 0),
    ADD CONSTRAINT ck_loans__installment_count
        CHECK (installment_count >= 1);

ALTER TABLE loan_installments
    ADD CONSTRAINT ck_loan_installments__due_period
        CHECK (due_period_month BETWEEN 1 AND 12 AND due_period_year BETWEEN 2000 AND 2200),
    ADD CONSTRAINT ck_loan_installments__installment_number
        CHECK (installment_number >= 1);

ALTER TABLE gosi_filings
    ADD CONSTRAINT ck_gosi_filings__period
        CHECK (month BETWEEN 1 AND 12 AND year BETWEEN 2000 AND 2200),
    ADD CONSTRAINT ck_gosi_filings__revision
        CHECK (revision >= 1);

-- Inclusive effective-dated bounds: effective_to, when set, is not before
-- effective_from (§5).
ALTER TABLE shift_assignments
    ADD CONSTRAINT ck_shift_assignments__effective_range
        CHECK (effective_to IS NULL OR effective_to >= effective_from);

ALTER TABLE employee_gosi_registrations
    ADD CONSTRAINT ck_employee_gosi_registrations__effective_range
        CHECK (effective_to IS NULL OR effective_to >= effective_from);

ALTER TABLE nitaqat_grid
    ADD CONSTRAINT ck_nitaqat_grid__effective_range
        CHECK (effective_to IS NULL OR effective_to >= effective_from),
    ADD CONSTRAINT ck_nitaqat_grid__headcount_range
        CHECK (headcount_min >= 0 AND (headcount_max IS NULL OR headcount_max >= headcount_min)),
    -- inclusive lower, exclusive upper: a continuous quantity has no last value (§5)
    ADD CONSTRAINT ck_nitaqat_grid__saudization_range
        CHECK (min_saudization_pct >= 0
               AND (max_saudization_pct IS NULL OR max_saudization_pct > min_saudization_pct));

ALTER TABLE approval_delegations
    ADD CONSTRAINT ck_approval_delegations__effective_range
        CHECK (effective_to IS NULL OR effective_to >= effective_from),
    ADD CONSTRAINT ck_approval_delegations__distinct_parties
        CHECK (delegator_user_id <> delegate_user_id);

ALTER TABLE leave_requests
    ADD CONSTRAINT ck_leave_requests__date_range
        CHECK (end_date >= start_date),
    ADD CONSTRAINT ck_leave_requests__days_positive
        CHECK (days > 0);

ALTER TABLE timesheets
    ADD CONSTRAINT ck_timesheets__period
        CHECK (period_end >= period_start),
    ADD CONSTRAINT ck_timesheets__total_minutes_non_negative
        CHECK (total_minutes >= 0);

ALTER TABLE timesheet_entries
    ADD CONSTRAINT ck_timesheet_entries__minutes_non_negative
        CHECK (minutes >= 0);

ALTER TABLE attendance_days
    ADD CONSTRAINT ck_attendance_days__minutes_non_negative
        CHECK (scheduled_minutes >= 0 AND worked_minutes >= 0 AND break_minutes >= 0
               AND late_minutes >= 0 AND early_out_minutes >= 0
               AND overtime_minutes >= 0 AND absent_minutes >= 0);

ALTER TABLE overtime_requests
    ADD CONSTRAINT ck_overtime_requests__minutes_positive
        CHECK (overtime_minutes > 0),
    ADD CONSTRAINT ck_overtime_requests__multiplier_positive
        CHECK (multiplier > 0);

ALTER TABLE eos_calculations
    ADD CONSTRAINT ck_eos_calculations__service
        CHECK (service_days >= 0 AND excluded_unpaid_days >= 0
               AND service_end_date >= service_start_date);

ALTER TABLE wps_batches
    ADD CONSTRAINT ck_wps_batches__counts_non_negative
        CHECK (employee_count >= 0 AND total_amount >= 0);

-- §12.3, §14: the PDPL redaction shape. Erasure nulls the personal payload and
-- the before/after pair together and stamps personal_data_erased_at; it never
-- touches envelope_hash, because the surviving digests are what let a verifier
-- RECOMPUTE that hash and bind the row to a Merkle root published before the
-- erasure. Comparing a stored hash with itself would prove nothing.
ALTER TABLE audit_logs
    ADD CONSTRAINT ck_audit_logs__erasure_is_complete
        CHECK (personal_data_erased_at IS NULL
               OR (personal_data IS NULL AND before IS NULL AND after IS NULL)),
    -- an Event carries an envelope hash and no checkpoint payload
    ADD CONSTRAINT ck_audit_logs__event_shape
        CHECK (record_kind <> 'Event'
               OR (envelope_hash IS NOT NULL
                   AND root_hash IS NULL
                   AND covers_seq_from IS NULL AND covers_seq_to IS NULL
                   AND covers_created_from IS NULL AND covers_created_to IS NULL)),
    -- a Checkpoint carries a Merkle root over a range bounded by BOTH seq and
    -- time: a seq-only walk prunes no partitions (§Q)
    ADD CONSTRAINT ck_audit_logs__checkpoint_shape
        CHECK (record_kind <> 'Checkpoint'
               OR (root_hash IS NOT NULL
                   AND covers_seq_from IS NOT NULL AND covers_seq_to IS NOT NULL
                   AND covers_created_from IS NOT NULL AND covers_created_to IS NOT NULL
                   AND covers_seq_to >= covers_seq_from
                   AND covers_created_to >= covers_created_from)),
    ADD CONSTRAINT ck_audit_logs__seq_positive
        CHECK (seq > 0);

ALTER TABLE payroll_audit_logs
    ADD CONSTRAINT ck_payroll_audit_logs__seq_positive
        CHECK (seq > 0);

ALTER TABLE background_jobs
    ADD CONSTRAINT ck_background_jobs__progress
        CHECK (progress_current >= 0
               AND (progress_total IS NULL
                    OR (progress_total >= 0 AND progress_current <= progress_total))),
    ADD CONSTRAINT ck_background_jobs__attempts_non_negative
        CHECK (attempts >= 0);

ALTER TABLE notification_deliveries
    ADD CONSTRAINT ck_notification_deliveries__attempts_non_negative
        CHECK (attempts >= 0);


-- =============================================================================
-- 4. EXCLUSION CONSTRAINTS
--
-- §5: dates are inclusive on both ends and every range expression in the
-- schema is exactly
--     daterange(effective_from, COALESCE(effective_to,'infinity'::date), '[]')
-- Numeric bands use the other convention deliberately: inclusive lower,
-- exclusive upper, because a continuous quantity has no last value.
--
-- Needs btree_gist for the equality operators on uuid and text.
-- =============================================================================

ALTER TABLE shift_assignments
    ADD CONSTRAINT ex_shift_assignments__employee_no_overlap
        EXCLUDE USING gist (
            tenant_id   WITH =,
            employee_id WITH =,
            daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&
        );

ALTER TABLE employee_gosi_registrations
    ADD CONSTRAINT ex_employee_gosi_registrations__employee_no_overlap
        EXCLUDE USING gist (
            tenant_id   WITH =,
            employee_id WITH =,
            daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&
        );

-- §E: the Nitaqat band grid is banded like statutory_rule_bands. Two bands of
-- the same activity, size tier and published grid version may not cover the
-- same Saudization percentage.
ALTER TABLE nitaqat_grid
    ADD CONSTRAINT ex_nitaqat_grid__saudization_band_no_overlap
        EXCLUDE USING gist (
            activity_code WITH =,
            size_tier     WITH =,
            grid_version  WITH =,
            numrange(min_saudization_pct,
                     COALESCE(max_saudization_pct, 'infinity'::numeric), '[)') WITH &&
        );
