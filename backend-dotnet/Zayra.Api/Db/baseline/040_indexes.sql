-- =============================================================================
-- 040_indexes.sql
-- KynexOne baseline schema — TARGET_SCHEMA.md revision 6 §19.4 (index inventory).
--
-- Three parts:
--   PART 1  one index per hot query H1-H12, plus the workflow-and-lifecycle row
--           of the §19.4 table. Every index carries a COMMENT ON INDEX naming
--           the query it serves (§19.4 rule 3, restated as a DDL obligation).
--   PART 2  the FK-covering indexes of §19.4 rule 2 — one per foreign key whose
--           referencing columns are not already the leading prefix of an index,
--           minus the classes §19.4 deliberately leaves unindexed.
--   PART 3  the deliberately unindexed foreign keys, written out with the reason,
--           so a future reader does not "fix" them.
--
-- §19.4 rule 1 — every index on a tenant table leads with tenant_id (company-tier:
-- tenant_id, company_id), so the RLS predicate tenant_id = app.current_tenant() is
-- an index condition rather than a filter over a sequential scan. Four indexes
-- deliberately do NOT lead with tenant_id, each marked THE QUEUE EXCEPTION below:
-- they serve cross-tenant pollers that run as kynex_job and are the exact shape
-- §19.4 itself prints for them.
--
-- §19.4 rule 5 — no index ships without an EXPLAIN (ANALYZE, BUFFERS) against a
-- seeded 5,000-employee tenant, recorded in the PR. That seed does not exist yet;
-- this file is the inventory the seed will be measured against, and the two
-- measured decisions §19.4 defers (H12's trigger-maintained fallback, H1's
-- trgm-vs-keyset split) stay open until it does.
--
-- Ordering note: this file may be applied before or after 030_partitions.sql.
-- Nothing here is UNIQUE, so no index depends on carrying the partition key, and
-- a plain CREATE INDEX on a partitioned parent propagates to every partition.
-- =============================================================================


-- =============================================================================
-- PART 1 — THE HOT QUERIES
-- =============================================================================

-- -----------------------------------------------------------------------------
-- H1  employee list and search (EmployeesController.cs:86) — five OR'd
--     leading-wildcard LIKEs, which no btree can serve.
-- -----------------------------------------------------------------------------

CREATE INDEX ix_employees__search_trgm ON employees
    USING gin ((
        coalesce(employee_number, '')::text || ' ' ||
        coalesce(name_en, '')::text        || ' ' ||
        coalesce(name_ar, '')::text        || ' ' ||
        coalesce(work_email, '')::text
    ) gin_trgm_ops)
    WHERE status <> 'Archived';
COMMENT ON INDEX ix_employees__search_trgm IS
    'H1 employee search. pg_trgm GIN over the four searched columns concatenated, so the five '
    'OR''d leading-wildcard LIKEs of EmployeesController.cs:86 become one index scan. Partial on '
    'status <> ''Archived'' because an archived employee is never a search hit. tenant_id is NOT '
    'the leading key here (a GIN index has no leading key); RLS still filters, and the trigram '
    'candidate set is small.';

CREATE INDEX ix_employees__tenant_status_name_en ON employees (tenant_id, status, name_en);
COMMENT ON INDEX ix_employees__tenant_status_name_en IS
    'H1 employee list, unsearched default page, and the keyset paging that replaces OFFSET in the port.';

-- -----------------------------------------------------------------------------
-- H2  dashboard summary (DashboardController.cs:250). The FK cover on
--     attendance_days (tenant_id, employee_id) that §19.4 also names is already
--     the leading prefix of uq_attendance_days__employee_work_date, so it is not
--     created twice. Keep the 60 s cache.
-- -----------------------------------------------------------------------------

CREATE INDEX ix_attendance_days__tenant_work_date_status ON attendance_days (tenant_id, work_date, status);
COMMENT ON INDEX ix_attendance_days__tenant_work_date_status IS
    'H2 dashboard summary (DashboardController.cs:250): today''s present/absent/leave counts for a tenant.';

-- -----------------------------------------------------------------------------
-- H3  dashboard KPIs (DashboardController.cs:1104, :1150).
--     The correlated per-employee COUNT(DISTINCT lower(doc_type)) is rewritten as
--     one GROUP BY in the port; no index fixes that query as written.
-- -----------------------------------------------------------------------------

CREATE INDEX ix_employee_documents__employee_doc_type ON employee_documents (tenant_id, employee_id, doc_type);
COMMENT ON INDEX ix_employee_documents__employee_doc_type IS
    'H3 dashboard KPIs (:1104): document completeness per employee, after the correlated subquery becomes a GROUP BY.';

CREATE INDEX ix_employee_documents__expiry_date ON employee_documents (tenant_id, expiry_date);
COMMENT ON INDEX ix_employee_documents__expiry_date IS
    'H3 dashboard KPIs (:1150): documents expiring inside the window, all statuses.';

CREATE INDEX ix_leave_requests__pending ON leave_requests (tenant_id, status)
    WHERE status = 'PendingApproval';
COMMENT ON INDEX ix_leave_requests__pending IS
    'H3 dashboard KPIs: the pending-leave count. §19.4 rule 4 partial — the pending subset is tiny '
    'next to the table. H10''s LIKE ''%Pending%'' becomes this status set in the port.';

-- -----------------------------------------------------------------------------
-- H4  approvals inbox (ApprovalWorkflowService.cs:94). Sort is
--     due_at ASC NULLS LAST — the COALESCE the live code uses is not sargable.
-- -----------------------------------------------------------------------------

CREATE INDEX ix_approval_requests__inbox_user ON approval_requests
    (tenant_id, current_approver_user_id, due_at ASC NULLS LAST, created_at DESC)
    WHERE status = 'Pending';
COMMENT ON INDEX ix_approval_requests__inbox_user IS
    'H4 approvals inbox by approver user (ApprovalWorkflowService.cs:94). Partial on status=''Pending''; '
    'due_at ASC NULLS LAST matches the port''s sort exactly.';

CREATE INDEX ix_approval_requests__inbox_employee ON approval_requests
    (tenant_id, current_approver_employee_id, due_at ASC NULLS LAST, created_at DESC)
    WHERE status = 'Pending';
COMMENT ON INDEX ix_approval_requests__inbox_employee IS
    'H4 approvals inbox, the current_approver_employee_id twin: an approver identified as an employee '
    'rather than a user account.';

CREATE INDEX ix_approval_requests__overdue ON approval_requests (tenant_id, due_at)
    WHERE status = 'Pending' AND due_at IS NOT NULL;
COMMENT ON INDEX ix_approval_requests__overdue IS
    'H4 overdue sweep and the §10.2 Pending -> Expired transition at due_at.';

-- -----------------------------------------------------------------------------
-- H5  approvals N+1 (ApprovalWorkflowService.cs:174, :476, :483). Not an index
--     problem: the fix is to memoise the caller''s employee id per request and
--     replace the in-memory manager walk with a recursive CTE. This is the index
--     that CTE needs. It also covers the employee_assignments.manager_employee_id
--     foreign key, so PART 2 does not repeat it.
-- -----------------------------------------------------------------------------

CREATE INDEX ix_employee_assignments__manager_employee_id ON employee_assignments (tenant_id, manager_employee_id)
    WHERE effective_to IS NULL;
COMMENT ON INDEX ix_employee_assignments__manager_employee_id IS
    'H5 recursive manager walk over employee_assignments.manager_employee_id, partial on the current '
    'assignment (effective_to IS NULL) per §19.4 rule 4. Also the FK cover for that column.';

-- -----------------------------------------------------------------------------
-- H6  payroll run (PayrollController.cs:1379). The 48 serial preloads are
--     batched in the port; these are the indexes the batched reads need.
-- -----------------------------------------------------------------------------

CREATE INDEX ix_employee_salaries__as_of ON employee_salaries (tenant_id, employee_id, effective_from DESC);
COMMENT ON INDEX ix_employee_salaries__as_of IS
    'H6 payroll run: the as-of salary read. Sits beside ex_employee_salaries__employee_no_overlap — the '
    'gist EXCLUDE guarantees there is exactly one row for a date, this btree finds it.';

CREATE INDEX ix_employee_assignments__as_of ON employee_assignments (tenant_id, employee_id, effective_from DESC);
COMMENT ON INDEX ix_employee_assignments__as_of IS
    'H6 payroll run: the as-of assignment read, which §11.3 makes authoritative for "was this person '
    'employed on date D". Same pairing with the gist EXCLUDE.';

CREATE INDEX ix_payroll_inputs__claimable ON payroll_inputs (tenant_id, company_id, run_year, run_month, employee_id)
    WHERE status = 'Pending';
COMMENT ON INDEX ix_payroll_inputs__claimable IS
    'H6 payroll run: the single-statement UPDATE ... RETURNING claim of §10.10, which reads exactly the '
    'Pending rows for a company and period. §19.4 rule 4 partial.';

CREATE INDEX ix_loans__active_recoverable ON loans (tenant_id, employee_id)
    WHERE status = 'Active' AND outstanding > 0;
COMMENT ON INDEX ix_loans__active_recoverable IS
    'H6 payroll run: loans still being recovered. Partial, so a tenant''s settled loan history does not '
    'sit in the index the run scans every month.';

CREATE INDEX ix_overtime_requests__work_date_status ON overtime_requests (tenant_id, work_date, status);
COMMENT ON INDEX ix_overtime_requests__work_date_status IS
    'H6 payroll run: approved overtime for the period being paid.';

-- -----------------------------------------------------------------------------
-- H7  payroll YTD (PayrollController.cs:1811). The design''s ytd_* columns on
--     payroll_slips make this a single-row read from the prior slip, so the
--     60,000-row scan disappears; payroll_slips (tenant_id, run_id, employee_id)
--     is already uq_payroll_slips__run_id_employee_id and is not created twice.
-- -----------------------------------------------------------------------------

CREATE INDEX ix_payroll_runs__period ON payroll_runs (tenant_id, company_id, year, month, status);
COMMENT ON INDEX ix_payroll_runs__period IS
    'H7 payroll YTD and every period lookup: the prior run for a company, year and month.';

-- -----------------------------------------------------------------------------
-- H8  attendance sweep (AttendanceService.cs:947), rewritten set-based in the
--     port. attendance_days'' own unique serves its side of the sweep, and
--     public_holidays (calendar_code, date) is loaded once per sweep.
-- -----------------------------------------------------------------------------

CREATE INDEX ix_attendance_punches__employee_occurred_at ON attendance_punches (tenant_id, employee_id, occurred_at);
COMMENT ON INDEX ix_attendance_punches__employee_occurred_at IS
    'H8 attendance sweep: a day''s punches per employee. Propagates per partition once 030 range-partitions '
    'attendance_punches on occurred_at. Also the FK cover for attendance_punches.employee_id.';

CREATE INDEX ix_leave_requests__approved_span ON leave_requests
    USING gist (tenant_id, employee_id, daterange(start_date, end_date, '[]'))
    WHERE status = 'Approved';
COMMENT ON INDEX ix_leave_requests__approved_span IS
    'H8 attendance sweep: "was this employee on approved leave on date D". gist over the inclusive-inclusive '
    'daterange of the §1 effective-dating convention; needs btree_gist for the two scalar keys.';

CREATE INDEX ix_public_holidays__calendar_date ON public_holidays (calendar_code, holiday_date);
COMMENT ON INDEX ix_public_holidays__calendar_date IS
    'H8 attendance sweep: the holiday calendar, loaded once per sweep. THE QUEUE EXCEPTION to §19.4 rule 1 — '
    'public_holidays.tenant_id is nullable because a NULL row is the shared national calendar, so a '
    'tenant-leading index would not find it.';

-- -----------------------------------------------------------------------------
-- H9  device ingest (AttendanceService.cs:484). Both indexes already exist:
--     uq_attendance_punches__device_external_id is the ON CONFLICT target and
--     uq_attendance_devices__serial is the device lookup. Nothing to add.
-- -----------------------------------------------------------------------------

-- -----------------------------------------------------------------------------
-- H10 ESS dashboard (EmployeeSelfServiceController.cs:66), batched to <=4 round
--     trips in the port.
-- -----------------------------------------------------------------------------

CREATE INDEX ix_payroll_slips__employee_run ON payroll_slips (tenant_id, employee_id, run_id DESC);
COMMENT ON INDEX ix_payroll_slips__employee_run IS
    'H10 ESS dashboard: my latest payslips, newest first. Also the FK cover for payroll_slips.employee_id.';

CREATE INDEX ix_notifications__unread ON notifications (tenant_id, user_id, created_at DESC)
    WHERE read_at IS NULL;
COMMENT ON INDEX ix_notifications__unread IS
    'H10 ESS dashboard: the unread bell count and list. §19.4 rule 4 partial on read_at IS NULL — the read '
    'tail is the overwhelming majority of the table and is never in this query.';

CREATE INDEX ix_employee_documents__employee_expiry ON employee_documents (tenant_id, employee_id, expiry_date);
COMMENT ON INDEX ix_employee_documents__employee_expiry IS
    'H10 ESS dashboard: my documents and what expires next.';

-- -----------------------------------------------------------------------------
-- H11 unpaged lists — deliberately no index. pageSize is clamped server-side on
--     every list endpoint, and the ESS payslip and attendance histories are paged,
--     so there is no unbounded query left for an index to rescue.
-- -----------------------------------------------------------------------------

-- -----------------------------------------------------------------------------
-- H12 v_leave_balances.
-- -----------------------------------------------------------------------------

CREATE INDEX ix_leave_ledger__balance ON leave_ledger (tenant_id, employee_id, leave_type_id) INCLUDE (days);
COMMENT ON INDEX ix_leave_ledger__balance IS
    'H12 v_leave_balances: INCLUDE (days) makes the SUM index-only, since §11.6 stores no balance anywhere. '
    'Also the FK cover for leave_ledger.employee_id. If p95 exceeds 50 ms at 270k rows per tenant the '
    'fallback is a trigger-maintained balance row — §19.4 leaves that to measurement, so it is not here.';

-- -----------------------------------------------------------------------------
-- Workflow and lifecycle (the last row of the §19.4 table).
-- -----------------------------------------------------------------------------

CREATE INDEX ix_payroll_issues__run_blocks ON payroll_issues (tenant_id, run_id)
    WHERE severity = 'Block';
COMMENT ON INDEX ix_payroll_issues__run_blocks IS
    'Workflow: "can this run be approved" — the Block issues of a run. §19.4 rule 4 partial.';

CREATE INDEX ix_payroll_issues__employee_standing ON payroll_issues (tenant_id, employee_id)
    WHERE run_id IS NULL;
COMMENT ON INDEX ix_payroll_issues__employee_standing IS
    'Workflow: standing issues against an employee that belong to no run. Also the FK cover for '
    'payroll_issues.employee_id on the subset that query reads.';

CREATE INDEX ix_background_jobs__queue ON background_jobs (status, scheduled_at)
    WHERE status IN ('Queued', 'Leased', 'Running');
COMMENT ON INDEX ix_background_jobs__queue IS
    'Workflow: the job poller. THE QUEUE EXCEPTION to §19.4 rule 1 — the poller runs as kynex_job across '
    'every tenant and background_jobs.tenant_id is nullable for platform jobs, so a tenant-leading index '
    'would be useless to it. §19.4 prints this index as (status, next_attempt_at); background_jobs has no '
    'next_attempt_at column — the due-time column the design actually built is scheduled_at, used here.';

CREATE INDEX ix_notification_deliveries__retry ON notification_deliveries (status, next_attempt_at)
    WHERE status IN ('Queued', 'Failed');
COMMENT ON INDEX ix_notification_deliveries__retry IS
    'Workflow: the delivery retry poller. THE QUEUE EXCEPTION to §19.4 rule 1, same reason as the job queue.';

CREATE INDEX ix_files__pending_purge ON files (purge_state)
    WHERE purge_state = 'PendingPurge';
COMMENT ON INDEX ix_files__pending_purge IS
    'Lifecycle: the §12.3 blob purge worklist. THE QUEUE EXCEPTION to §19.4 rule 1 — the retention job '
    'walks tenants in bounded batches (§19.5) and needs the global worklist first.';

CREATE INDEX ix_employee_documents__active_expiry ON employee_documents (tenant_id, expiry_date)
    WHERE status = 'Active';
COMMENT ON INDEX ix_employee_documents__active_expiry IS
    'Lifecycle: the Iqama/passport/work-permit expiry alerting sweep, which only ever looks at Active documents.';

CREATE INDEX ix_wps_lines__batch_bank_status ON wps_lines (tenant_id, batch_id, bank_status);
COMMENT ON INDEX ix_wps_lines__batch_bank_status IS
    'Workflow: the bank confirmation importer, and the §10.1 Paid -> Completed test that every '
    'wps_lines.bank_status in a run is terminal. Also the FK cover for wps_lines.batch_id.';

CREATE INDEX ix_gosi_filings__period ON gosi_filings (tenant_id, company_id, year, month);
COMMENT ON INDEX ix_gosi_filings__period IS
    'Workflow: the filing for a company and period across revisions. uq_gosi_filings__period leads with '
    'gosi_registration_no before year and month, so it cannot serve this lookup.';

CREATE INDEX ix_audit_logs__correlation_id ON audit_logs (tenant_id, correlation_id);
COMMENT ON INDEX ix_audit_logs__correlation_id IS
    '§14 audit: every row written by one request or job. The (chain_key, seq) verification order §19.4 also '
    'names is already uq_audit_logs__chain_seq, which leads with exactly those two columns.';


-- =============================================================================
-- PART 2 — FK-COVERING INDEXES (§19.4 rule 2)
--
-- One index per foreign key whose referencing columns are not already the leading
-- prefix of an existing index, minus the three exclusion classes of PART 3. Every
-- one is composite and tenant-leading, because every one of these foreign keys is
-- composite and tenant-leading.
--
-- Why this matters beyond joins: §8.3 makes a tenant purge an ordered hard delete,
-- and every RESTRICT parent delete and every SET NULL cascade has to find its
-- children. Without the index that is one sequential scan of the child table per
-- deleted parent row.
-- =============================================================================

CREATE INDEX ix_approval_actions__actor_user_id ON approval_actions (tenant_id, actor_user_id);
COMMENT ON INDEX ix_approval_actions__actor_user_id IS '§19.4 rule 2: FK cover for approval_actions(tenant_id, actor_user_id) -> users, ON DELETE RESTRICT.';
CREATE INDEX ix_approval_actions__on_behalf_of_user_id ON approval_actions (tenant_id, on_behalf_of_user_id);
COMMENT ON INDEX ix_approval_actions__on_behalf_of_user_id IS '§19.4 rule 2: FK cover for approval_actions(tenant_id, on_behalf_of_user_id) -> users, ON DELETE SET NULL.';
CREATE INDEX ix_approval_actions__request_id ON approval_actions (tenant_id, request_id);
COMMENT ON INDEX ix_approval_actions__request_id IS '§19.4 rule 2: FK cover for approval_actions(tenant_id, request_id) -> approval_requests, ON DELETE RESTRICT.';
CREATE INDEX ix_approval_delegations__delegate_user_id ON approval_delegations (tenant_id, delegate_user_id);
COMMENT ON INDEX ix_approval_delegations__delegate_user_id IS '§19.4 rule 2: FK cover for approval_delegations(tenant_id, delegate_user_id) -> users, ON DELETE CASCADE.';
CREATE INDEX ix_approval_delegations__delegator_user_id ON approval_delegations (tenant_id, delegator_user_id);
COMMENT ON INDEX ix_approval_delegations__delegator_user_id IS '§19.4 rule 2: FK cover for approval_delegations(tenant_id, delegator_user_id) -> users, ON DELETE CASCADE.';
CREATE INDEX ix_approval_requests__employee_id ON approval_requests (tenant_id, employee_id);
COMMENT ON INDEX ix_approval_requests__employee_id IS '§19.4 rule 2: FK cover for approval_requests(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';
CREATE INDEX ix_approval_requests__requester_user_id ON approval_requests (tenant_id, requester_user_id);
COMMENT ON INDEX ix_approval_requests__requester_user_id IS '§19.4 rule 2: FK cover for approval_requests(tenant_id, requester_user_id) -> users, ON DELETE RESTRICT.';
CREATE INDEX ix_approval_requests__workflow_id ON approval_requests (tenant_id, workflow_id);
COMMENT ON INDEX ix_approval_requests__workflow_id IS '§19.4 rule 2: FK cover for approval_requests(tenant_id, workflow_id) -> approval_workflows, ON DELETE RESTRICT.';
CREATE INDEX ix_attendance_days__locked_run_id ON attendance_days (tenant_id, locked_run_id);
COMMENT ON INDEX ix_attendance_days__locked_run_id IS '§19.4 rule 2: FK cover for attendance_days(tenant_id, locked_run_id) -> payroll_runs, ON DELETE SET NULL.';
CREATE INDEX ix_attendance_days__shift_id ON attendance_days (tenant_id, shift_id);
COMMENT ON INDEX ix_attendance_days__shift_id IS '§19.4 rule 2: FK cover for attendance_days(tenant_id, shift_id) -> shifts, ON DELETE SET NULL.';
CREATE INDEX ix_attendance_devices__branch_id ON attendance_devices (tenant_id, branch_id);
COMMENT ON INDEX ix_attendance_devices__branch_id IS '§19.4 rule 2: FK cover for attendance_devices(tenant_id, branch_id) -> branches, ON DELETE RESTRICT.';
CREATE INDEX ix_attendance_punches__approval_request_id ON attendance_punches (tenant_id, approval_request_id);
COMMENT ON INDEX ix_attendance_punches__approval_request_id IS '§19.4 rule 2: FK cover for attendance_punches(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';
CREATE INDEX ix_attendance_punches__employee_id ON attendance_punches (tenant_id, employee_id);
COMMENT ON INDEX ix_attendance_punches__employee_id IS '§19.4 rule 2: FK cover for attendance_punches(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';
CREATE INDEX ix_auth_sessions__platform_user_id ON auth_sessions (platform_user_id);
COMMENT ON INDEX ix_auth_sessions__platform_user_id IS '§19.4 rule 2: FK cover for auth_sessions(platform_user_id) -> platform_users, ON DELETE CASCADE.';
CREATE INDEX ix_auth_sessions__user_id ON auth_sessions (tenant_id, user_id);
COMMENT ON INDEX ix_auth_sessions__user_id IS '§19.4 rule 2: FK cover for auth_sessions(tenant_id, user_id) -> users, ON DELETE CASCADE.';
CREATE INDEX ix_auth_tokens__platform_user_id ON auth_tokens (platform_user_id);
COMMENT ON INDEX ix_auth_tokens__platform_user_id IS '§19.4 rule 2: FK cover for auth_tokens(platform_user_id) -> platform_users, ON DELETE CASCADE.';
CREATE INDEX ix_auth_tokens__user_id ON auth_tokens (tenant_id, user_id);
COMMENT ON INDEX ix_auth_tokens__user_id IS '§19.4 rule 2: FK cover for auth_tokens(tenant_id, user_id) -> users, ON DELETE CASCADE.';
CREATE INDEX ix_background_job_items__job_id ON background_job_items (job_id);
COMMENT ON INDEX ix_background_job_items__job_id IS '§19.4 rule 2: FK cover for background_job_items(job_id) -> background_jobs, ON DELETE CASCADE.';
CREATE INDEX ix_background_jobs__source_file_id ON background_jobs (source_file_id);
COMMENT ON INDEX ix_background_jobs__source_file_id IS '§19.4 rule 2: FK cover for background_jobs(source_file_id) -> files, ON DELETE SET NULL.';
CREATE INDEX ix_branches__company_id ON branches (tenant_id, company_id);
COMMENT ON INDEX ix_branches__company_id IS '§19.4 rule 2: FK cover for branches(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';
CREATE INDEX ix_company_pay_policies__pay_component_code ON company_pay_policies (tenant_id, pay_component_code);
COMMENT ON INDEX ix_company_pay_policies__pay_component_code IS '§19.4 rule 2: FK cover for company_pay_policies(tenant_id, pay_component_code) -> pay_components, ON DELETE RESTRICT.';
CREATE INDEX ix_departments__company_id ON departments (tenant_id, company_id);
COMMENT ON INDEX ix_departments__company_id IS '§19.4 rule 2: FK cover for departments(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';
CREATE INDEX ix_departments__cost_center_id ON departments (tenant_id, cost_center_id);
COMMENT ON INDEX ix_departments__cost_center_id IS '§19.4 rule 2: FK cover for departments(tenant_id, cost_center_id) -> cost_centers, ON DELETE SET NULL.';
CREATE INDEX ix_employee_assignments__approval_request_id ON employee_assignments (tenant_id, approval_request_id);
COMMENT ON INDEX ix_employee_assignments__approval_request_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';
CREATE INDEX ix_employee_assignments__branch_id ON employee_assignments (tenant_id, branch_id);
COMMENT ON INDEX ix_employee_assignments__branch_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, branch_id) -> branches, ON DELETE RESTRICT.';
CREATE INDEX ix_employee_assignments__company_id ON employee_assignments (tenant_id, company_id);
COMMENT ON INDEX ix_employee_assignments__company_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';
CREATE INDEX ix_employee_assignments__cost_center_id ON employee_assignments (tenant_id, cost_center_id);
COMMENT ON INDEX ix_employee_assignments__cost_center_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, cost_center_id) -> cost_centers, ON DELETE RESTRICT.';
CREATE INDEX ix_employee_assignments__department_id ON employee_assignments (tenant_id, department_id);
COMMENT ON INDEX ix_employee_assignments__department_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, department_id) -> departments, ON DELETE RESTRICT.';
CREATE INDEX ix_employee_assignments__designation_id ON employee_assignments (tenant_id, designation_id);
COMMENT ON INDEX ix_employee_assignments__designation_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, designation_id) -> designations, ON DELETE RESTRICT.';
CREATE INDEX ix_employee_assignments__grade_id ON employee_assignments (tenant_id, grade_id);
COMMENT ON INDEX ix_employee_assignments__grade_id IS '§19.4 rule 2: FK cover for employee_assignments(tenant_id, grade_id) -> grades, ON DELETE RESTRICT.';
CREATE INDEX ix_employee_contracts__document_id ON employee_contracts (tenant_id, document_id);
COMMENT ON INDEX ix_employee_contracts__document_id IS '§19.4 rule 2: FK cover for employee_contracts(tenant_id, document_id) -> employee_documents, ON DELETE SET NULL.';
CREATE INDEX ix_employee_documents__employee_id ON employee_documents (tenant_id, employee_id);
COMMENT ON INDEX ix_employee_documents__employee_id IS '§19.4 rule 2: FK cover for employee_documents(tenant_id, employee_id) -> employees, ON DELETE CASCADE.';
CREATE INDEX ix_employee_documents__file_id ON employee_documents (tenant_id, file_id);
COMMENT ON INDEX ix_employee_documents__file_id IS '§19.4 rule 2: FK cover for employee_documents(tenant_id, file_id) -> files, ON DELETE RESTRICT.';
CREATE INDEX ix_employee_documents__leave_request_id ON employee_documents (tenant_id, leave_request_id);
COMMENT ON INDEX ix_employee_documents__leave_request_id IS '§19.4 rule 2: FK cover for employee_documents(tenant_id, leave_request_id) -> leave_requests, ON DELETE SET NULL.';
CREATE INDEX ix_employee_documents__supersedes_id ON employee_documents (tenant_id, supersedes_id);
COMMENT ON INDEX ix_employee_documents__supersedes_id IS '§19.4 rule 2: FK cover for employee_documents(tenant_id, supersedes_id) -> employee_documents, ON DELETE SET NULL.';
CREATE INDEX ix_employee_gosi_registrations__company_id ON employee_gosi_registrations (tenant_id, company_id);
COMMENT ON INDEX ix_employee_gosi_registrations__company_id IS '§19.4 rule 2: FK cover for employee_gosi_registrations(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';
CREATE INDEX ix_employee_gosi_registrations__gosi_registration_no ON employee_gosi_registrations (tenant_id, gosi_registration_no);
COMMENT ON INDEX ix_employee_gosi_registrations__gosi_registration_no IS '§19.4 rule 2: FK cover for employee_gosi_registrations(tenant_id, gosi_registration_no) -> companies, ON DELETE RESTRICT.';
CREATE INDEX ix_employee_salaries__approval_request_id ON employee_salaries (tenant_id, approval_request_id);
COMMENT ON INDEX ix_employee_salaries__approval_request_id IS '§19.4 rule 2: FK cover for employee_salaries(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';
CREATE INDEX ix_eos_calculations__employee_id ON eos_calculations (tenant_id, employee_id);
COMMENT ON INDEX ix_eos_calculations__employee_id IS '§19.4 rule 2: FK cover for eos_calculations(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';
CREATE INDEX ix_eos_calculations__settlement_id ON eos_calculations (tenant_id, settlement_id);
COMMENT ON INDEX ix_eos_calculations__settlement_id IS '§19.4 rule 2: FK cover for eos_calculations(tenant_id, settlement_id) -> final_settlements, ON DELETE SET NULL.';
CREATE INDEX ix_files__uploaded_by ON files (tenant_id, uploaded_by);
COMMENT ON INDEX ix_files__uploaded_by IS '§19.4 rule 2: FK cover for files(tenant_id, uploaded_by) -> users, ON DELETE SET NULL.';
CREATE INDEX ix_final_settlement_lines__loan_installment_id ON final_settlement_lines (tenant_id, loan_installment_id);
COMMENT ON INDEX ix_final_settlement_lines__loan_installment_id IS '§19.4 rule 2: FK cover for final_settlement_lines(tenant_id, loan_installment_id) -> loan_installments, ON DELETE RESTRICT.';
CREATE INDEX ix_final_settlement_lines__settlement_id ON final_settlement_lines (tenant_id, settlement_id);
COMMENT ON INDEX ix_final_settlement_lines__settlement_id IS '§19.4 rule 2: FK cover for final_settlement_lines(tenant_id, settlement_id) -> final_settlements, ON DELETE CASCADE.';
CREATE INDEX ix_final_settlements__approval_request_id ON final_settlements (tenant_id, approval_request_id);
COMMENT ON INDEX ix_final_settlements__approval_request_id IS '§19.4 rule 2: FK cover for final_settlements(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';
CREATE INDEX ix_final_settlements__company_id ON final_settlements (tenant_id, company_id);
COMMENT ON INDEX ix_final_settlements__company_id IS '§19.4 rule 2: FK cover for final_settlements(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';
CREATE INDEX ix_final_settlements__employee_id ON final_settlements (tenant_id, employee_id);
COMMENT ON INDEX ix_final_settlements__employee_id IS '§19.4 rule 2: FK cover for final_settlements(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';
CREATE INDEX ix_final_settlements__paid_via_run_id ON final_settlements (tenant_id, paid_via_run_id);
COMMENT ON INDEX ix_final_settlements__paid_via_run_id IS '§19.4 rule 2: FK cover for final_settlements(tenant_id, paid_via_run_id) -> payroll_runs, ON DELETE RESTRICT.';
CREATE INDEX ix_gl_journal_lines__cost_center_id ON gl_journal_lines (tenant_id, cost_center_id);
COMMENT ON INDEX ix_gl_journal_lines__cost_center_id IS '§19.4 rule 2: FK cover for gl_journal_lines(tenant_id, cost_center_id) -> cost_centers, ON DELETE RESTRICT.';
CREATE INDEX ix_gl_journal_lines__journal_id ON gl_journal_lines (tenant_id, journal_id);
COMMENT ON INDEX ix_gl_journal_lines__journal_id IS '§19.4 rule 2: FK cover for gl_journal_lines(tenant_id, journal_id) -> gl_journals, ON DELETE CASCADE.';
CREATE INDEX ix_gl_journals__company_id ON gl_journals (tenant_id, company_id);
COMMENT ON INDEX ix_gl_journals__company_id IS '§19.4 rule 2: FK cover for gl_journals(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';
CREATE INDEX ix_gl_journals__file_id ON gl_journals (tenant_id, file_id);
COMMENT ON INDEX ix_gl_journals__file_id IS '§19.4 rule 2: FK cover for gl_journals(tenant_id, file_id) -> files, ON DELETE RESTRICT.';
CREATE INDEX ix_gl_journals__reversal_of_id ON gl_journals (tenant_id, reversal_of_id);
COMMENT ON INDEX ix_gl_journals__reversal_of_id IS '§19.4 rule 2: FK cover for gl_journals(tenant_id, reversal_of_id) -> gl_journals, ON DELETE RESTRICT.';
CREATE INDEX ix_gl_mappings__cost_center_id ON gl_mappings (tenant_id, cost_center_id);
COMMENT ON INDEX ix_gl_mappings__cost_center_id IS '§19.4 rule 2: FK cover for gl_mappings(tenant_id, cost_center_id) -> cost_centers, ON DELETE RESTRICT.';
CREATE INDEX ix_gosi_filings__file_id ON gosi_filings (tenant_id, file_id);
COMMENT ON INDEX ix_gosi_filings__file_id IS '§19.4 rule 2: FK cover for gosi_filings(tenant_id, file_id) -> files, ON DELETE RESTRICT.';
CREATE INDEX ix_gosi_filings__gosi_registration_no ON gosi_filings (tenant_id, gosi_registration_no);
COMMENT ON INDEX ix_gosi_filings__gosi_registration_no IS '§19.4 rule 2: FK cover for gosi_filings(tenant_id, gosi_registration_no) -> companies, ON DELETE RESTRICT.';
CREATE INDEX ix_leave_ledger__employee_id ON leave_ledger (tenant_id, employee_id);
COMMENT ON INDEX ix_leave_ledger__employee_id IS '§19.4 rule 2: FK cover for leave_ledger(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';
CREATE INDEX ix_leave_ledger__leave_type_id ON leave_ledger (tenant_id, leave_type_id);
COMMENT ON INDEX ix_leave_ledger__leave_type_id IS '§19.4 rule 2: FK cover for leave_ledger(tenant_id, leave_type_id) -> leave_types, ON DELETE RESTRICT.';
CREATE INDEX ix_leave_requests__approval_request_id ON leave_requests (tenant_id, approval_request_id);
COMMENT ON INDEX ix_leave_requests__approval_request_id IS '§19.4 rule 2: FK cover for leave_requests(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';
CREATE INDEX ix_leave_requests__employee_id ON leave_requests (tenant_id, employee_id);
COMMENT ON INDEX ix_leave_requests__employee_id IS '§19.4 rule 2: FK cover for leave_requests(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';
CREATE INDEX ix_leave_requests__leave_type_id ON leave_requests (tenant_id, leave_type_id);
COMMENT ON INDEX ix_leave_requests__leave_type_id IS '§19.4 rule 2: FK cover for leave_requests(tenant_id, leave_type_id) -> leave_types, ON DELETE RESTRICT.';
CREATE INDEX ix_loans__approval_request_id ON loans (tenant_id, approval_request_id);
COMMENT ON INDEX ix_loans__approval_request_id IS '§19.4 rule 2: FK cover for loans(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';
CREATE INDEX ix_loans__employee_id ON loans (tenant_id, employee_id);
COMMENT ON INDEX ix_loans__employee_id IS '§19.4 rule 2: FK cover for loans(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';
CREATE INDEX ix_notification_deliveries__notification_id ON notification_deliveries (tenant_id, notification_id);
COMMENT ON INDEX ix_notification_deliveries__notification_id IS '§19.4 rule 2: FK cover for notification_deliveries(tenant_id, notification_id) -> notifications, ON DELETE CASCADE.';
CREATE INDEX ix_notifications__user_id ON notifications (tenant_id, user_id);
COMMENT ON INDEX ix_notifications__user_id IS '§19.4 rule 2: FK cover for notifications(tenant_id, user_id) -> users, ON DELETE CASCADE.';
CREATE INDEX ix_overtime_requests__approval_request_id ON overtime_requests (tenant_id, approval_request_id);
COMMENT ON INDEX ix_overtime_requests__approval_request_id IS '§19.4 rule 2: FK cover for overtime_requests(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';
CREATE INDEX ix_overtime_requests__employee_id ON overtime_requests (tenant_id, employee_id);
COMMENT ON INDEX ix_overtime_requests__employee_id IS '§19.4 rule 2: FK cover for overtime_requests(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_inputs__claimed_by_run_id ON payroll_inputs (tenant_id, claimed_by_run_id);
COMMENT ON INDEX ix_payroll_inputs__claimed_by_run_id IS '§19.4 rule 2: FK cover for payroll_inputs(tenant_id, claimed_by_run_id) -> payroll_runs, ON DELETE SET NULL.';
CREATE INDEX ix_payroll_inputs__company_id ON payroll_inputs (tenant_id, company_id);
COMMENT ON INDEX ix_payroll_inputs__company_id IS '§19.4 rule 2: FK cover for payroll_inputs(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_inputs__consumed_run_id ON payroll_inputs (tenant_id, consumed_run_id);
COMMENT ON INDEX ix_payroll_inputs__consumed_run_id IS '§19.4 rule 2: FK cover for payroll_inputs(tenant_id, consumed_run_id) -> payroll_runs, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_inputs__cost_center_id ON payroll_inputs (tenant_id, cost_center_id);
COMMENT ON INDEX ix_payroll_inputs__cost_center_id IS '§19.4 rule 2: FK cover for payroll_inputs(tenant_id, cost_center_id) -> cost_centers, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_inputs__employee_id ON payroll_inputs (tenant_id, employee_id);
COMMENT ON INDEX ix_payroll_inputs__employee_id IS '§19.4 rule 2: FK cover for payroll_inputs(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_inputs__pay_component_code ON payroll_inputs (tenant_id, pay_component_code);
COMMENT ON INDEX ix_payroll_inputs__pay_component_code IS '§19.4 rule 2: FK cover for payroll_inputs(tenant_id, pay_component_code) -> pay_components, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_issues__employee_id ON payroll_issues (tenant_id, employee_id);
COMMENT ON INDEX ix_payroll_issues__employee_id IS '§19.4 rule 2: FK cover for payroll_issues(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_issues__run_id ON payroll_issues (tenant_id, run_id);
COMMENT ON INDEX ix_payroll_issues__run_id IS '§19.4 rule 2: FK cover for payroll_issues(tenant_id, run_id) -> payroll_runs, ON DELETE CASCADE.';
CREATE INDEX ix_payroll_runs__approval_request_id ON payroll_runs (tenant_id, approval_request_id);
COMMENT ON INDEX ix_payroll_runs__approval_request_id IS '§19.4 rule 2: FK cover for payroll_runs(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_runs__parent_run_id ON payroll_runs (tenant_id, parent_run_id);
COMMENT ON INDEX ix_payroll_runs__parent_run_id IS '§19.4 rule 2: FK cover for payroll_runs(tenant_id, parent_run_id) -> payroll_runs, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_runs__source_import_job_id ON payroll_runs (tenant_id, source_import_job_id);
COMMENT ON INDEX ix_payroll_runs__source_import_job_id IS '§19.4 rule 2: FK cover for payroll_runs(tenant_id, source_import_job_id) -> background_jobs, ON DELETE SET NULL.';
CREATE INDEX ix_payroll_slip_lines__cost_center_id ON payroll_slip_lines (tenant_id, cost_center_id);
COMMENT ON INDEX ix_payroll_slip_lines__cost_center_id IS '§19.4 rule 2: FK cover for payroll_slip_lines(tenant_id, cost_center_id) -> cost_centers, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_slip_lines__loan_installment_id ON payroll_slip_lines (tenant_id, loan_installment_id);
COMMENT ON INDEX ix_payroll_slip_lines__loan_installment_id IS '§19.4 rule 2: FK cover for payroll_slip_lines(tenant_id, loan_installment_id) -> loan_installments, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_slip_lines__pay_component_code ON payroll_slip_lines (tenant_id, pay_component_code);
COMMENT ON INDEX ix_payroll_slip_lines__pay_component_code IS '§19.4 rule 2: FK cover for payroll_slip_lines(tenant_id, pay_component_code) -> pay_components, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_slip_lines__payroll_input_id ON payroll_slip_lines (tenant_id, payroll_input_id);
COMMENT ON INDEX ix_payroll_slip_lines__payroll_input_id IS '§19.4 rule 2: FK cover for payroll_slip_lines(tenant_id, payroll_input_id) -> payroll_inputs, ON DELETE RESTRICT.';
CREATE INDEX ix_payroll_slip_lines__slip_id ON payroll_slip_lines (tenant_id, slip_id);
COMMENT ON INDEX ix_payroll_slip_lines__slip_id IS '§19.4 rule 2: FK cover for payroll_slip_lines(tenant_id, slip_id) -> payroll_slips, ON DELETE CASCADE.';
CREATE INDEX ix_payroll_slips__payslip_file_id ON payroll_slips (tenant_id, payslip_file_id);
COMMENT ON INDEX ix_payroll_slips__payslip_file_id IS '§19.4 rule 2: FK cover for payroll_slips(tenant_id, payslip_file_id) -> files, ON DELETE RESTRICT.';
CREATE INDEX ix_permission_grantor_records__grantor_user_id ON permission_grantor_records (tenant_id, grantor_user_id);
COMMENT ON INDEX ix_permission_grantor_records__grantor_user_id IS '§19.4 rule 2: FK cover for permission_grantor_records(tenant_id, grantor_user_id) -> users, ON DELETE CASCADE.';
CREATE INDEX ix_permission_grantor_records__revoked_by ON permission_grantor_records (tenant_id, revoked_by);
COMMENT ON INDEX ix_permission_grantor_records__revoked_by IS '§19.4 rule 2: FK cover for permission_grantor_records(tenant_id, revoked_by) -> users, ON DELETE RESTRICT.';
CREATE INDEX ix_shift_assignments__shift_id ON shift_assignments (tenant_id, shift_id);
COMMENT ON INDEX ix_shift_assignments__shift_id IS '§19.4 rule 2: FK cover for shift_assignments(tenant_id, shift_id) -> shifts, ON DELETE RESTRICT.';
CREATE INDEX ix_timesheet_day_reconciliations__attendance_day_id_work_date ON timesheet_day_reconciliations (tenant_id, attendance_day_id, work_date);
COMMENT ON INDEX ix_timesheet_day_reconciliations__attendance_day_id_work_date IS '§19.4 rule 2: FK cover for timesheet_day_reconciliations(tenant_id, attendance_day_id, work_date) -> attendance_days, ON DELETE RESTRICT.';
CREATE INDEX ix_timesheet_entries__cost_center_id ON timesheet_entries (tenant_id, cost_center_id);
COMMENT ON INDEX ix_timesheet_entries__cost_center_id IS '§19.4 rule 2: FK cover for timesheet_entries(tenant_id, cost_center_id) -> cost_centers, ON DELETE RESTRICT.';
CREATE INDEX ix_timesheet_entries__timesheet_id ON timesheet_entries (tenant_id, timesheet_id);
COMMENT ON INDEX ix_timesheet_entries__timesheet_id IS '§19.4 rule 2: FK cover for timesheet_entries(tenant_id, timesheet_id) -> timesheets, ON DELETE CASCADE.';
CREATE INDEX ix_timesheets__approval_request_id ON timesheets (tenant_id, approval_request_id);
COMMENT ON INDEX ix_timesheets__approval_request_id IS '§19.4 rule 2: FK cover for timesheets(tenant_id, approval_request_id) -> approval_requests, ON DELETE RESTRICT.';
CREATE INDEX ix_timesheets__company_id ON timesheets (tenant_id, company_id);
COMMENT ON INDEX ix_timesheets__company_id IS '§19.4 rule 2: FK cover for timesheets(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';
CREATE INDEX ix_timesheets__locked_run_id ON timesheets (tenant_id, locked_run_id);
COMMENT ON INDEX ix_timesheets__locked_run_id IS '§19.4 rule 2: FK cover for timesheets(tenant_id, locked_run_id) -> payroll_runs, ON DELETE SET NULL.';
CREATE INDEX ix_user_roles__granted_by ON user_roles (tenant_id, granted_by);
COMMENT ON INDEX ix_user_roles__granted_by IS '§19.4 rule 2: FK cover for user_roles(tenant_id, granted_by) -> users, ON DELETE SET NULL.';
CREATE INDEX ix_user_roles__role_id ON user_roles (tenant_id, role_id);
COMMENT ON INDEX ix_user_roles__role_id IS '§19.4 rule 2: FK cover for user_roles(tenant_id, role_id) -> roles, ON DELETE RESTRICT.';
CREATE INDEX ix_user_roles__scope_branch_id ON user_roles (tenant_id, scope_branch_id);
COMMENT ON INDEX ix_user_roles__scope_branch_id IS '§19.4 rule 2: FK cover for user_roles(tenant_id, scope_branch_id) -> branches, ON DELETE RESTRICT.';
CREATE INDEX ix_user_roles__scope_company_id ON user_roles (tenant_id, scope_company_id);
COMMENT ON INDEX ix_user_roles__scope_company_id IS '§19.4 rule 2: FK cover for user_roles(tenant_id, scope_company_id) -> companies, ON DELETE RESTRICT.';
CREATE INDEX ix_user_roles__scope_department_id ON user_roles (tenant_id, scope_department_id);
COMMENT ON INDEX ix_user_roles__scope_department_id IS '§19.4 rule 2: FK cover for user_roles(tenant_id, scope_department_id) -> departments, ON DELETE RESTRICT.';
CREATE INDEX ix_user_roles__user_id ON user_roles (tenant_id, user_id);
COMMENT ON INDEX ix_user_roles__user_id IS '§19.4 rule 2: FK cover for user_roles(tenant_id, user_id) -> users, ON DELETE CASCADE.';
CREATE INDEX ix_wps_batches__company_id ON wps_batches (tenant_id, company_id);
COMMENT ON INDEX ix_wps_batches__company_id IS '§19.4 rule 2: FK cover for wps_batches(tenant_id, company_id) -> companies, ON DELETE RESTRICT.';
CREATE INDEX ix_wps_batches__file_id ON wps_batches (tenant_id, file_id);
COMMENT ON INDEX ix_wps_batches__file_id IS '§19.4 rule 2: FK cover for wps_batches(tenant_id, file_id) -> files, ON DELETE RESTRICT.';
CREATE INDEX ix_wps_batches__generated_by ON wps_batches (tenant_id, generated_by);
COMMENT ON INDEX ix_wps_batches__generated_by IS '§19.4 rule 2: FK cover for wps_batches(tenant_id, generated_by) -> users, ON DELETE SET NULL.';
CREATE INDEX ix_wps_batches__resubmission_of_id ON wps_batches (tenant_id, resubmission_of_id);
COMMENT ON INDEX ix_wps_batches__resubmission_of_id IS '§19.4 rule 2: FK cover for wps_batches(tenant_id, resubmission_of_id) -> wps_batches, ON DELETE RESTRICT.';
CREATE INDEX ix_wps_batches__run_id ON wps_batches (tenant_id, run_id);
COMMENT ON INDEX ix_wps_batches__run_id IS '§19.4 rule 2: FK cover for wps_batches(tenant_id, run_id) -> payroll_runs, ON DELETE RESTRICT.';
CREATE INDEX ix_wps_lines__confirmation_job_id ON wps_lines (confirmation_job_id);
COMMENT ON INDEX ix_wps_lines__confirmation_job_id IS '§19.4 rule 2: FK cover for wps_lines(confirmation_job_id) -> background_jobs, ON DELETE SET NULL.';
CREATE INDEX ix_wps_lines__employee_id ON wps_lines (tenant_id, employee_id);
COMMENT ON INDEX ix_wps_lines__employee_id IS '§19.4 rule 2: FK cover for wps_lines(tenant_id, employee_id) -> employees, ON DELETE RESTRICT.';
CREATE INDEX ix_wps_lines__slip_id ON wps_lines (tenant_id, slip_id);
COMMENT ON INDEX ix_wps_lines__slip_id IS '§19.4 rule 2: FK cover for wps_lines(tenant_id, slip_id) -> payroll_slips, ON DELETE RESTRICT.';


-- =============================================================================
-- PART 3 — THE FOREIGN KEYS DELIBERATELY LEFT UNINDEXED (§19.4)
--
-- Written out so nobody "completes" the inventory by adding them. §19.4 narrowed
-- revision 5's justification on purpose, because §8.3 does hard-delete users
-- during a tenant purge, so "users are never removed" was wrong. Three classes
-- survive that correction, and only these thirteen constraints are in them.
--
-- (a) FKs to immutable reference data. The parent is never deleted outside a
--     migration, and a migration can build the index first.
--        payroll_slip_lines.statutory_rule_id      -> statutory_rules
--        payroll_slip_lines.statutory_rule_band_id -> statutory_rule_bands
--        overtime_requests.statutory_rule_id       -> statutory_rules
--        role_permissions.permission_code          -> permissions
--        employee_documents.template_id            -> document_templates
--        payroll_slips.template_id                 -> document_templates
--
-- (b) RESTRICT actor columns whose parent delete is already blocked by the
--     restriction itself, and which no query filters on. A purge that must remove
--     such a user resolves the business row first, by the definition of RESTRICT.
--        payroll_issues.override_by                    -> users
--        gosi_filings.filed_by                         -> users
--        gl_period_closes.closed_by                    -> users
--        gl_period_closes.reopened_by                  -> users
--        permission_grantor_records.granted_by_user_id -> users
--
--     §19.4 names this class "override_by, filed_by, closed_by, reopened_by,
--     granted_by". Taken literally that would also exclude user_roles.granted_by —
--     but that column is ON DELETE SET NULL, and the same section's correction
--     says every SET NULL child of users IS now indexed, precisely because a
--     SET NULL cascade scans the whole child table per deleted user. The two
--     sentences collide on one column name. Resolved in favour of the correction:
--     user_roles.granted_by is indexed in PART 2, and "granted_by" is read as
--     permission_grantor_records.granted_by_user_id, which is RESTRICT and so
--     genuinely belongs to this class.
--
-- (c) Self-FKs on small hierarchies — hundreds of rows at most, where a sequential
--     scan is cheaper than maintaining the index.
--        departments.parent_id   -> departments
--        cost_centers.parent_id  -> cost_centers
--
-- Correcting revision 5, and therefore present in PART 2 rather than here:
--   files.uploaded_by, approval_actions.on_behalf_of_user_id, wps_batches.generated_by
--   and user_roles.granted_by, because a SET NULL cascade during a tenant purge
--   scans the whole child table per deleted user and files and approval_actions are
--   among the largest tables in the schema; employee_documents.supersedes_id and
--   payroll_runs.parent_run_id, which revision 5 filed as "read only from the child
--   side" and which are in fact read parent-side — renewal chains are listed from
--   the superseded document, and correction runs are listed under their parent run.
-- =============================================================================
