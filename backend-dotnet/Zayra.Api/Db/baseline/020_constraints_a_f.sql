-- =============================================================================
-- 020_constraints_a_f.sql
-- Foreign keys, CHECK constraints and exclusion constraints for domains A–F.
-- TARGET_SCHEMA.md revision 6 §8 (FK register), §9 (enumerations), §11 (invariants),
-- §13.3 (bounded text / format checks), §16 (polymorphic pointers);
-- CONVENTIONS.md §3 (composite keys), §5 (effective dating), §7 (enum governance).
--
-- WHAT IS AND IS NOT HERE
--   * 77 of the 83 §8 register rows whose CHILD is an A–F table. The remaining six
--     point at G–R parents and are listed, by register number, at the foot of this
--     file for whoever writes 021.
--   * No triggers. Everything §11.2 calls a "deferred constraint trigger", every
--     frozen-row and append-only guard, the payroll_runs transition guard, and the
--     retention_policies "a tenant row may only lengthen a platform period" rule are
--     050_triggers.sql. Where a rule could not be a CHECK, it says so below.
--   * No policies, no partitions, no indexes.
--
-- RULES APPLIED UNIFORMLY (§8.1)
--   * ON UPDATE RESTRICT on every FK, without exception: keys are uuid and never
--     updated.
--   * Every FK between two tenant-tier tables is COMPOSITE on (tenant_id, x_id).
--     CONVENTIONS.md §3 states this as universal; §8.2 spells only some rows that way
--     (e.g. rows 14, 34, 35), which is presentation, not a different rule. The
--     universal form is used here so no row can point across tenants.
--     Single-column FKs are used only where the parent has no tenant_id: tenants,
--     platform_users, permissions, statutory_rules, statutory_rule_bands.
--   * A composite ON DELETE SET NULL must name its column — plain SET NULL would null
--     tenant_id too, which is NOT NULL, and the delete would fail at runtime rather
--     than at DDL time. Every such FK below uses `SET NULL (<column>)` (PG15+).
--   * "CASCADE + guard" in §8.2 is written as CASCADE here; the BEFORE DELETE guard
--     that restricts it to a draft parent is 050_triggers.sql.
--
-- CONSTRAINT NAMING — a reported contradiction, resolved.
--   CONVENTIONS.md §1 specifies `ck_<table>__<what it asserts>` and marks it [NEW];
--   §16 lists constraint naming as decision 4, still open. TARGET_SCHEMA.md §9 gives
--   36 CHECKs explicit `chk_<table>_<column>` names and marks them [TS], "mirrored by
--   a C# constants class of the same name". §9's names win where §9 gives one,
--   because code is asserted against them; everything else uses the §1 pattern.
-- =============================================================================


-- =============================================================================
-- PART 1 — FOREIGN KEYS
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Domain A — platform and tenancy (§8 rows 1–6)
-- -----------------------------------------------------------------------------
-- 1
ALTER TABLE tenant_settings ADD CONSTRAINT fk_tenant_settings__tenant_id
    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE RESTRICT ON DELETE CASCADE;
-- 2
ALTER TABLE number_sequences ADD CONSTRAINT fk_number_sequences__tenant_id
    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE RESTRICT ON DELETE CASCADE;
-- 3
ALTER TABLE number_sequences ADD CONSTRAINT fk_number_sequences__company_id
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 4
ALTER TABLE files ADD CONSTRAINT fk_files__tenant_id
    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 5 — the file outlives an anonymised uploader
ALTER TABLE files ADD CONSTRAINT fk_files__uploaded_by
    FOREIGN KEY (tenant_id, uploaded_by) REFERENCES users (tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (uploaded_by);
-- 6 — a tenant override dies with the tenant; the platform row has tenant_id NULL
ALTER TABLE retention_policies ADD CONSTRAINT fk_retention_policies__tenant_id
    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE RESTRICT ON DELETE CASCADE;

-- -----------------------------------------------------------------------------
-- Domain B — identity and access (§8 rows 7–19b, 156–158)
-- -----------------------------------------------------------------------------
-- 7 — RESTRICT: tenant purge deletes users explicitly, in the §8.3 order
ALTER TABLE users ADD CONSTRAINT fk_users__tenant_id
    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 8 — erasure anonymises the employee and disables the login; it never deletes the row
ALTER TABLE users ADD CONSTRAINT fk_users__employee_id
    FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 9
ALTER TABLE roles ADD CONSTRAINT fk_roles__tenant_id
    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 10 — grants are part of the role
ALTER TABLE role_permissions ADD CONSTRAINT fk_role_permissions__role_id
    FOREIGN KEY (tenant_id, role_id) REFERENCES roles (tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;
-- 11 — single-column: permissions is reference tier and has no tenant_id
ALTER TABLE role_permissions ADD CONSTRAINT fk_role_permissions__permission_code
    FOREIGN KEY (permission_code) REFERENCES permissions (code) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 12 — a grant is part of the user
ALTER TABLE user_roles ADD CONSTRAINT fk_user_roles__user_id
    FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;
-- 13 — a role in use cannot vanish; deactivate it
ALTER TABLE user_roles ADD CONSTRAINT fk_user_roles__role_id
    FOREIGN KEY (tenant_id, role_id) REFERENCES roles (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 14, 15, 16 — scope must never silently widen to the whole tenant
ALTER TABLE user_roles ADD CONSTRAINT fk_user_roles__scope_company_id
    FOREIGN KEY (tenant_id, scope_company_id) REFERENCES companies (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
ALTER TABLE user_roles ADD CONSTRAINT fk_user_roles__scope_branch_id
    FOREIGN KEY (tenant_id, scope_branch_id) REFERENCES branches (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
ALTER TABLE user_roles ADD CONSTRAINT fk_user_roles__scope_department_id
    FOREIGN KEY (tenant_id, scope_department_id) REFERENCES departments (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 17 — who granted it is also in audit_logs
ALTER TABLE user_roles ADD CONSTRAINT fk_user_roles__granted_by
    FOREIGN KEY (tenant_id, granted_by) REFERENCES users (tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (granted_by);
-- 18 / 18b — exactly one subject column is set (XOR CHECK in part 2).
-- The tenant half is composite; MATCH SIMPLE means a platform row, whose tenant_id and
-- user_id are both NULL, is simply not constrained by it.
ALTER TABLE auth_sessions ADD CONSTRAINT fk_auth_sessions__user_id
    FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;
ALTER TABLE auth_sessions ADD CONSTRAINT fk_auth_sessions__platform_user_id
    FOREIGN KEY (platform_user_id) REFERENCES platform_users (id) ON UPDATE RESTRICT ON DELETE CASCADE;
-- 19 / 19b
ALTER TABLE auth_tokens ADD CONSTRAINT fk_auth_tokens__user_id
    FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;
ALTER TABLE auth_tokens ADD CONSTRAINT fk_auth_tokens__platform_user_id
    FOREIGN KEY (platform_user_id) REFERENCES platform_users (id) ON UPDATE RESTRICT ON DELETE CASCADE;
-- 156 — the authority is part of the user it is granted to
ALTER TABLE permission_grantor_records ADD CONSTRAINT fk_permission_grantor_records__grantor_user_id
    FOREIGN KEY (tenant_id, grantor_user_id) REFERENCES users (tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;
-- 157 / 158 — who conferred or revoked the authority is an authorization fact
ALTER TABLE permission_grantor_records ADD CONSTRAINT fk_permission_grantor_records__granted_by_user_id
    FOREIGN KEY (tenant_id, granted_by_user_id) REFERENCES users (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
ALTER TABLE permission_grantor_records ADD CONSTRAINT fk_permission_grantor_records__revoked_by
    FOREIGN KEY (tenant_id, revoked_by) REFERENCES users (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;

-- -----------------------------------------------------------------------------
-- Domain C — organisation (§8 rows 20–31)
-- -----------------------------------------------------------------------------
-- 20 — payroll history must never disappear with a mis-clicked tenant delete
ALTER TABLE companies ADD CONSTRAINT fk_companies__tenant_id
    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 21
ALTER TABLE company_pay_policies ADD CONSTRAINT fk_company_pay_policies__company_id
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 22 — a component named in a policy cannot be deleted
ALTER TABLE company_pay_policies ADD CONSTRAINT fk_company_pay_policies__pay_component_code
    FOREIGN KEY (tenant_id, pay_component_code) REFERENCES pay_components (tenant_id, code) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 23
ALTER TABLE branches ADD CONSTRAINT fk_branches__company_id
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 24
ALTER TABLE departments ADD CONSTRAINT fk_departments__company_id
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 25 — a parent with children stays
ALTER TABLE departments ADD CONSTRAINT fk_departments__parent_id
    FOREIGN KEY (tenant_id, parent_id) REFERENCES departments (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 26 — the department survives a retired cost centre
ALTER TABLE departments ADD CONSTRAINT fk_departments__cost_center_id
    FOREIGN KEY (tenant_id, cost_center_id) REFERENCES cost_centers (tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (cost_center_id);
-- 27
ALTER TABLE cost_centers ADD CONSTRAINT fk_cost_centers__company_id
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 28
ALTER TABLE cost_centers ADD CONSTRAINT fk_cost_centers__parent_id
    FOREIGN KEY (tenant_id, parent_id) REFERENCES cost_centers (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 29
ALTER TABLE designations ADD CONSTRAINT fk_designations__tenant_id
    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 30
ALTER TABLE grades ADD CONSTRAINT fk_grades__tenant_id
    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 31 — a tenant calendar is part of the tenant; NULL is the platform calendar
ALTER TABLE public_holidays ADD CONSTRAINT fk_public_holidays__tenant_id
    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE RESTRICT ON DELETE CASCADE;

-- -----------------------------------------------------------------------------
-- Domain D — employees, history, documents and letters (§8 rows 32–52)
-- -----------------------------------------------------------------------------
-- 32 — purge deletes explicitly, in order, with evidence
ALTER TABLE employees ADD CONSTRAINT fk_employees__tenant_id
    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 33 — history outlives edits; employees are anonymised, not deleted
ALTER TABLE employee_assignments ADD CONSTRAINT fk_employee_assignments__employee_id
    FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 34
ALTER TABLE employee_assignments ADD CONSTRAINT fk_employee_assignments__company_id
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 35 — required: attendance and WPS both need it
ALTER TABLE employee_assignments ADD CONSTRAINT fk_employee_assignments__branch_id
    FOREIGN KEY (tenant_id, branch_id) REFERENCES branches (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 36 — required for GL and reporting
ALTER TABLE employee_assignments ADD CONSTRAINT fk_employee_assignments__department_id
    FOREIGN KEY (tenant_id, department_id) REFERENCES departments (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 37 — required for the Nitaqat occupation
ALTER TABLE employee_assignments ADD CONSTRAINT fk_employee_assignments__designation_id
    FOREIGN KEY (tenant_id, designation_id) REFERENCES designations (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 38 — optional by product
ALTER TABLE employee_assignments ADD CONSTRAINT fk_employee_assignments__grade_id
    FOREIGN KEY (tenant_id, grade_id) REFERENCES grades (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 39 — the org chart must not silently break
ALTER TABLE employee_assignments ADD CONSTRAINT fk_employee_assignments__manager_employee_id
    FOREIGN KEY (tenant_id, manager_employee_id) REFERENCES employees (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 40 — NOT NULL in effect via a payroll_issues Block, not via the column
ALTER TABLE employee_assignments ADD CONSTRAINT fk_employee_assignments__cost_center_id
    FOREIGN KEY (tenant_id, cost_center_id) REFERENCES cost_centers (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 42 — money history
ALTER TABLE employee_salaries ADD CONSTRAINT fk_employee_salaries__employee_id
    FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 44 — legal document
ALTER TABLE employee_contracts ADD CONSTRAINT fk_employee_contracts__employee_id
    FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 45 — the terms survive the scan
ALTER TABLE employee_contracts ADD CONSTRAINT fk_employee_contracts__document_id
    FOREIGN KEY (tenant_id, document_id) REFERENCES employee_documents (tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (document_id);
-- 46 — payment history
ALTER TABLE employee_bank_accounts ADD CONSTRAINT fk_employee_bank_accounts__employee_id
    FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 47 — documents are part of the person; only the purge job can reach this
ALTER TABLE employee_documents ADD CONSTRAINT fk_employee_documents__employee_id
    FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;
-- 48 — the files row owns the purge state
ALTER TABLE employee_documents ADD CONSTRAINT fk_employee_documents__file_id
    FOREIGN KEY (tenant_id, file_id) REFERENCES files (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 49 — an issued letter must keep its template
ALTER TABLE employee_documents ADD CONSTRAINT fk_employee_documents__template_id
    FOREIGN KEY (tenant_id, template_id) REFERENCES document_templates (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 50 — a renewal chain survives a deleted predecessor
ALTER TABLE employee_documents ADD CONSTRAINT fk_employee_documents__supersedes_id
    FOREIGN KEY (tenant_id, supersedes_id) REFERENCES employee_documents (tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (supersedes_id);
-- 52 — NULL = tenant-wide
ALTER TABLE document_templates ADD CONSTRAINT fk_document_templates__company_id
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;

-- -----------------------------------------------------------------------------
-- Domain E — statutory reference (§8 row 53)
-- -----------------------------------------------------------------------------
-- 53 — bands are parts of the rule. Single-column: both tables are reference tier.
ALTER TABLE statutory_rule_bands ADD CONSTRAINT fk_statutory_rule_bands__statutory_rule_id
    FOREIGN KEY (statutory_rule_id) REFERENCES statutory_rules (id) ON UPDATE RESTRICT ON DELETE CASCADE;

-- -----------------------------------------------------------------------------
-- Domain F — payroll (§8 rows 54–78)
-- -----------------------------------------------------------------------------
-- 54 — referenced by every line
ALTER TABLE pay_components ADD CONSTRAINT fk_pay_components__tenant_id
    FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 55 — filed money
ALTER TABLE payroll_runs ADD CONSTRAINT fk_payroll_runs__company_id
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 56 — correction chains stay intact
ALTER TABLE payroll_runs ADD CONSTRAINT fk_payroll_runs__parent_run_id
    FOREIGN KEY (tenant_id, parent_run_id) REFERENCES payroll_runs (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 59 — slips are parts of a run; the 050 guard raises unless status='Draft'
ALTER TABLE payroll_slips ADD CONSTRAINT fk_payroll_slips__run_id
    FOREIGN KEY (tenant_id, run_id) REFERENCES payroll_runs (tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;
-- 60 — paid money
ALTER TABLE payroll_slips ADD CONSTRAINT fk_payroll_slips__employee_id
    FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 61 — the issued document
ALTER TABLE payroll_slips ADD CONSTRAINT fk_payroll_slips__payslip_file_id
    FOREIGN KEY (tenant_id, payslip_file_id) REFERENCES files (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 62 — reprint fidelity
ALTER TABLE payroll_slips ADD CONSTRAINT fk_payroll_slips__template_id
    FOREIGN KEY (tenant_id, template_id) REFERENCES document_templates (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 63 — lines are parts of a slip; the 050 guard raises unless the run is Draft
ALTER TABLE payroll_slip_lines ADD CONSTRAINT fk_payroll_slip_lines__slip_id
    FOREIGN KEY (tenant_id, slip_id) REFERENCES payroll_slips (tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;
-- 64 — component meaning must survive
ALTER TABLE payroll_slip_lines ADD CONSTRAINT fk_payroll_slip_lines__pay_component_code
    FOREIGN KEY (tenant_id, pay_component_code) REFERENCES pay_components (tenant_id, code) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 65 / 66 — the frozen rule and band. Single-column: reference tier.
ALTER TABLE payroll_slip_lines ADD CONSTRAINT fk_payroll_slip_lines__statutory_rule_id
    FOREIGN KEY (statutory_rule_id) REFERENCES statutory_rules (id) ON UPDATE RESTRICT ON DELETE RESTRICT;
ALTER TABLE payroll_slip_lines ADD CONSTRAINT fk_payroll_slip_lines__statutory_rule_band_id
    FOREIGN KEY (statutory_rule_band_id) REFERENCES statutory_rule_bands (id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 67 — provenance of variable pay; the surviving direction of the broken cycle (§8.4)
ALTER TABLE payroll_slip_lines ADD CONSTRAINT fk_payroll_slip_lines__payroll_input_id
    FOREIGN KEY (tenant_id, payroll_input_id) REFERENCES payroll_inputs (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 69 — GL dimension
ALTER TABLE payroll_slip_lines ADD CONSTRAINT fk_payroll_slip_lines__cost_center_id
    FOREIGN KEY (tenant_id, cost_center_id) REFERENCES cost_centers (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 70 — money owed
ALTER TABLE payroll_inputs ADD CONSTRAINT fk_payroll_inputs__employee_id
    FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 71 — paying entity
ALTER TABLE payroll_inputs ADD CONSTRAINT fk_payroll_inputs__company_id
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 72 — component meaning
ALTER TABLE payroll_inputs ADD CONSTRAINT fk_payroll_inputs__pay_component_code
    FOREIGN KEY (tenant_id, pay_component_code) REFERENCES pay_components (tenant_id, code) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 73 — GL dimension
ALTER TABLE payroll_inputs ADD CONSTRAINT fk_payroll_inputs__cost_center_id
    FOREIGN KEY (tenant_id, cost_center_id) REFERENCES cost_centers (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 74 — deleting a draft run releases the claim
ALTER TABLE payroll_inputs ADD CONSTRAINT fk_payroll_inputs__claimed_by_run_id
    FOREIGN KEY (tenant_id, claimed_by_run_id) REFERENCES payroll_runs (tenant_id, id) ON UPDATE RESTRICT ON DELETE SET NULL (claimed_by_run_id);
-- 75 — a consumed input pins its run
ALTER TABLE payroll_inputs ADD CONSTRAINT fk_payroll_inputs__consumed_run_id
    FOREIGN KEY (tenant_id, consumed_run_id) REFERENCES payroll_runs (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 76 — run findings die with a draft run; NULL = a standing gap
ALTER TABLE payroll_issues ADD CONSTRAINT fk_payroll_issues__run_id
    FOREIGN KEY (tenant_id, run_id) REFERENCES payroll_runs (tenant_id, id) ON UPDATE RESTRICT ON DELETE CASCADE;
-- 77 — standing gaps belong to the person
ALTER TABLE payroll_issues ADD CONSTRAINT fk_payroll_issues__employee_id
    FOREIGN KEY (tenant_id, employee_id) REFERENCES employees (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;
-- 78 — who waived a warning is evidence
ALTER TABLE payroll_issues ADD CONSTRAINT fk_payroll_issues__override_by
    FOREIGN KEY (tenant_id, override_by) REFERENCES users (tenant_id, id) ON UPDATE RESTRICT ON DELETE RESTRICT;


-- =============================================================================
-- PART 2 — CHECK CONSTRAINTS
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 2a. §9 enumerated status domains. Names are §9's own, verbatim, because a C#
--     constants class of the same name is asserted against them (§7).
-- -----------------------------------------------------------------------------
-- §9 row 1
ALTER TABLE tenants ADD CONSTRAINT chk_tenants_status
    CHECK (status IN ('Active', 'Suspended', 'SoftDeleted', 'Purged'));
-- §9 row 2
ALTER TABLE users ADD CONSTRAINT chk_users_status
    CHECK (status IN ('Invited', 'Active', 'Suspended', 'Locked', 'Disabled'));
-- §9 row 3
ALTER TABLE platform_users ADD CONSTRAINT chk_platform_users_status
    CHECK (status IN ('Active', 'Suspended', 'Disabled'));
-- §9 row 3b. §9 gives ONE name, chk_auth_subject_kind, for a constraint on TWO tables;
-- PostgreSQL scopes constraint names per table, so each gets the name suffixed by its
-- table. The mirrored C# class is still one. [Reported: §9 names it once for two tables.]
ALTER TABLE auth_sessions ADD CONSTRAINT chk_auth_subject_kind_auth_sessions
    CHECK (subject_kind IN ('Tenant', 'Platform'));
ALTER TABLE auth_tokens ADD CONSTRAINT chk_auth_subject_kind_auth_tokens
    CHECK (subject_kind IN ('Tenant', 'Platform'));
-- §9 row 4
ALTER TABLE auth_tokens ADD CONSTRAINT chk_auth_tokens_purpose
    CHECK (purpose IN ('PasswordReset', 'MfaChallenge', 'Invitation', 'EmailConfirm'));
-- §9 row 5 — Suspended and Invited are live values revision 2 lost (§10.7)
ALTER TABLE employees ADD CONSTRAINT chk_employees_status
    CHECK (status IN ('Draft', 'Invited', 'Active', 'Suspended', 'Offboarded', 'Archived'));
-- §9 row 6
ALTER TABLE employees ADD CONSTRAINT chk_employees_privacy
    CHECK (privacy_status IN ('Normal', 'PendingErasure', 'Anonymised', 'MergedDuplicate'));
-- §9 row 7
ALTER TABLE employee_documents ADD CONSTRAINT chk_employee_documents_status
    CHECK (status IN ('Active', 'Expired', 'Superseded', 'Revoked'));
-- §9 row 8
ALTER TABLE employee_documents ADD CONSTRAINT chk_employee_documents_type
    CHECK (doc_type IN ('Iqama', 'Passport', 'Visa', 'WorkPermit', 'Contract', 'Letter', 'Medical', 'Other'));
-- §9 row 9 — the nine already in the live CHECK; revision 3 must not regress them
ALTER TABLE payroll_runs ADD CONSTRAINT chk_payroll_runs_status
    CHECK (status IN ('Draft', 'Processing', 'Processed', 'PendingFinanceReview', 'Approved', 'Completed', 'Locked', 'Paid', 'Voided'));
-- §9 row 10
ALTER TABLE payroll_runs ADD CONSTRAINT chk_payroll_runs_type
    CHECK (run_type IN ('Regular', 'OffCycle', 'FinalSettlement', 'Correction', 'Opening'));
-- §9 row 11
ALTER TABLE payroll_slips ADD CONSTRAINT chk_payroll_slips_inclusion
    CHECK (inclusion_status IN ('Included', 'ExcludedByFilter', 'ExcludedByHold', 'ExcludedByBlock'));
-- §9 row 12
ALTER TABLE payroll_slip_lines ADD CONSTRAINT chk_payroll_slip_lines_kind
    CHECK (kind IN ('Earning', 'Deduction', 'EmployerContribution', 'Info'));
-- §9 row 13
ALTER TABLE payroll_inputs ADD CONSTRAINT chk_payroll_inputs_status
    CHECK (status IN ('Pending', 'Claimed', 'Consumed', 'Cancelled'));
-- §9 row 14
ALTER TABLE payroll_issues ADD CONSTRAINT chk_payroll_issues_severity
    CHECK (severity IN ('Block', 'Warn'));
-- §9 row 35
ALTER TABLE files ADD CONSTRAINT chk_files_purge_state
    CHECK (purge_state IN ('Active', 'PendingPurge', 'Purged'));
-- §9 row 36
ALTER TABLE retention_policies ADD CONSTRAINT chk_retention_policies_disposition
    CHECK (disposition IN ('Anonymise', 'Purge', 'Keep'));

-- -----------------------------------------------------------------------------
-- 2b. The "remaining closed sets" of §9's closing paragraph, and the value sets §2
--     states inline. Same mechanism, §1 naming pattern (§9 gives these no name).
-- -----------------------------------------------------------------------------
ALTER TABLE pay_components ADD CONSTRAINT ck_pay_components__kind
    CHECK (kind IN ('Earning', 'Deduction', 'EmployerContribution', 'Info'));
ALTER TABLE payroll_inputs ADD CONSTRAINT ck_payroll_inputs__kind
    CHECK (kind IN ('Adjustment', 'Arrears', 'Receivable', 'Overtime', 'UnpaidLeave', 'Absence', 'LeaveEncashment', 'Bonus'));
ALTER TABLE document_templates ADD CONSTRAINT ck_document_templates__kind
    CHECK (kind IN ('Letter', 'Payslip', 'Contract'));
-- §9 closing paragraph: family was free text in revision 2 and is now constrained.
ALTER TABLE statutory_rules ADD CONSTRAINT ck_statutory_rules__family
    CHECK (family IN ('GOSI', 'EOS', 'Overtime', 'Leave', 'Nitaqat', 'WPS'));
ALTER TABLE statutory_rule_bands ADD CONSTRAINT ck_statutory_rule_bands__unit
    CHECK (unit IN ('ServiceYears', 'SickDays', 'ContributoryWage'));
-- §2.E dimension values
ALTER TABLE statutory_rules ADD CONSTRAINT ck_statutory_rules__nationality_class
    CHECK (nationality_class IN ('Saudi', 'GCC', 'NonSaudi', 'Any'));
ALTER TABLE statutory_rules ADD CONSTRAINT ck_statutory_rules__cohort
    CHECK (cohort IN ('Legacy', 'Entrant2024', 'Any'));
-- §2.A value sets
ALTER TABLE number_sequences ADD CONSTRAINT ck_number_sequences__scope_key
    CHECK (scope_key IN ('employee_no', 'letter_no', 'run_no', 'wps_batch_no', 'settlement_no', 'gosi_filing_no', 'timesheet_no'));
ALTER TABLE number_sequences ADD CONSTRAINT ck_number_sequences__reset_period
    CHECK (reset_period IN ('None', 'Year', 'Month'));
ALTER TABLE files ADD CONSTRAINT ck_files__purpose
    CHECK (purpose IN ('EmployeeDocument', 'Payslip', 'WpsSif', 'GlExport', 'BankConfirmation', 'Import', 'LetterPdf'));
ALTER TABLE retention_policies ADD CONSTRAINT ck_retention_policies__trigger_event
    CHECK (trigger_event IN ('SoftDelete', 'Separation', 'RecordDate', 'Expiry'));
-- §16 pointer 2: the allowed source_type values, which are also what makes consumption
-- idempotent, since the uniqueness key includes both source_type and source_id.
ALTER TABLE payroll_inputs ADD CONSTRAINT ck_payroll_inputs__source_type
    CHECK (source_type IN ('Overtime', 'Leave', 'Attendance', 'Timesheet', 'Manual', 'Opening', 'Bonus'));
-- The §16 mapping also fixes which values carry a subject id and which do not.
ALTER TABLE payroll_inputs ADD CONSTRAINT ck_payroll_inputs__source_id_presence
    CHECK (
        (source_type IN ('Manual', 'Bonus') AND source_id IS NULL)
     OR (source_type NOT IN ('Manual', 'Bonus') AND source_id IS NOT NULL)
    );
-- target_run_type, when set, must name a real run type (§2.F claim statement)
ALTER TABLE payroll_inputs ADD CONSTRAINT ck_payroll_inputs__target_run_type
    CHECK (target_run_type IS NULL OR target_run_type IN ('Regular', 'OffCycle', 'FinalSettlement', 'Correction', 'Opening'));

-- -----------------------------------------------------------------------------
-- 2c. Subject-kind integrity on the two nullable-tenant infrastructure tables.
--     §19.2 states this CHECK verbatim: it is what makes a tenant session
--     impossible to create without a tenant and a platform session unreadable by
--     kynex_app. The XOR of §2.B is subsumed by it.
-- -----------------------------------------------------------------------------
ALTER TABLE auth_sessions ADD CONSTRAINT ck_auth_sessions__subject_xor
    CHECK (
        (subject_kind = 'Tenant'   AND user_id IS NOT NULL AND platform_user_id IS NULL     AND tenant_id IS NOT NULL)
     OR (subject_kind = 'Platform' AND user_id IS NULL     AND platform_user_id IS NOT NULL AND tenant_id IS NULL)
    );
ALTER TABLE auth_tokens ADD CONSTRAINT ck_auth_tokens__subject_xor
    CHECK (
        (subject_kind = 'Tenant'   AND user_id IS NOT NULL AND platform_user_id IS NULL     AND tenant_id IS NOT NULL)
     OR (subject_kind = 'Platform' AND user_id IS NULL     AND platform_user_id IS NOT NULL AND tenant_id IS NULL)
    );

-- -----------------------------------------------------------------------------
-- 2d. §13.3 format-bearing columns. Bounded length is in the column type; these are
--     the stated pattern checks.
-- -----------------------------------------------------------------------------
-- §13.3: national_id / iqama_no are 10 digits beginning 1 or 2
ALTER TABLE employees ADD CONSTRAINT ck_employees__national_id_format
    CHECK (national_id IS NULL OR national_id ~ '^[12][0-9]{9}$');
ALTER TABLE employees ADD CONSTRAINT ck_employees__iqama_no_format
    CHECK (iqama_no IS NULL OR iqama_no ~ '^[12][0-9]{9}$');
-- §13.3: the SA IBAN pattern. The mod-97 checksum is enforced in the service, not here.
-- NOTE: this forbids a non-SA IBAN outright, which is what §13.3 says and what WPS
-- requires. payroll_slips.iban is a FROZEN witness and is deliberately NOT constrained:
-- a snapshot must record what was true, including a value a later rule would reject.
ALTER TABLE employee_bank_accounts ADD CONSTRAINT ck_employee_bank_accounts__iban_format
    CHECK (iban ~ '^SA[0-9]{22}$');
-- §13.3: ISO-3166-1 alpha-2
ALTER TABLE employees ADD CONSTRAINT ck_employees__nationality_code_format
    CHECK (nationality_code IS NULL OR nationality_code ~ '^[A-Z]{2}$');
ALTER TABLE employee_documents ADD CONSTRAINT ck_employee_documents__issuing_country_format
    CHECK (issuing_country IS NULL OR issuing_country ~ '^[A-Z]{2}$');
ALTER TABLE statutory_rules ADD CONSTRAINT ck_statutory_rules__country_code_format
    CHECK (country_code ~ '^[A-Z]{2}$');
-- §13.2: ISO-4217, and the currency of record
ALTER TABLE companies ADD CONSTRAINT ck_companies__currency_code_format
    CHECK (currency_code ~ '^[A-Z]{3}$');
-- §2.A: slug is lowercase, bounded 63 by its type
ALTER TABLE tenants ADD CONSTRAINT ck_tenants__slug_format
    CHECK (slug ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$');
-- NOT ENFORCED HERE: tenants.timezone_id / companies.timezone_id are "IANA, validated"
-- (§13.4). Validation needs the runtime tz database, so it is a write-time service
-- check, not a CHECK constraint. Recorded rather than silently dropped.

-- -----------------------------------------------------------------------------
-- 2e. §11 invariants that are constraints rather than triggers.
--     Everything §11.2 attributes to a trg_* trigger is 050_triggers.sql and is
--     deliberately absent here.
-- -----------------------------------------------------------------------------
-- §11.2, last-but-one row: payroll_inputs.amount = entitled − previously settled.
-- §11.2 names this a CHECK, not a trigger.
ALTER TABLE payroll_inputs ADD CONSTRAINT ck_payroll_inputs__amount_is_net_entitlement
    CHECK (amount = entitled_amount - previously_settled_amount);
-- §2.F: attendance_locked_range must lie inside the run period.
ALTER TABLE payroll_runs ADD CONSTRAINT ck_payroll_runs__lock_inside_period
    CHECK (
        attendance_locked_range IS NULL
     OR attendance_locked_range <@ daterange(
            make_date(year::int, month::int, 1),
            (make_date(year::int, month::int, 1) + interval '1 month')::date,
            '[)')
    );
-- §2.F / §10.1: an Opening run is non-payable. The full Draft -> Locked machine is the
-- 050 transition trigger; this is the half that is expressible as a CHECK.
ALTER TABLE payroll_runs ADD CONSTRAINT ck_payroll_runs__opening_never_paid
    CHECK (run_type <> 'Opening' OR status <> 'Paid');
-- §2.F: Block is never overridable. Enforced here so it cannot be a UI-only rule.
ALTER TABLE payroll_issues ADD CONSTRAINT ck_payroll_issues__block_never_overridden
    CHECK (
        severity <> 'Block'
     OR (override_by IS NULL AND override_at IS NULL AND override_reason IS NULL)
    );
-- §7 / §16 (revision 6 correction): value_json is bounded, with a number.
ALTER TABLE company_pay_policies ADD CONSTRAINT ck_company_pay_policies__value_json_bounded
    CHECK (value_json IS NULL OR pg_column_size(value_json) <= 8192);
-- NOT ENFORCED HERE: §12.4's "a tenant row may only LENGTHEN a platform period".
-- The rule compares a row against the platform row of the same entity, which needs a
-- subquery; it is a trigger in 050. Recorded rather than silently dropped.

-- -----------------------------------------------------------------------------
-- 2f. Effective dating: the bounds must be orderable. The no-overlap half is the
--     EXCLUDE in part 3. Both bounds are INCLUSIVE, so effective_to = effective_from
--     is a legal one-day row (CONVENTIONS.md §5).
-- -----------------------------------------------------------------------------
ALTER TABLE retention_policies ADD CONSTRAINT ck_retention_policies__effective_range
    CHECK (effective_to IS NULL OR effective_to >= effective_from);
ALTER TABLE company_pay_policies ADD CONSTRAINT ck_company_pay_policies__effective_range
    CHECK (effective_to IS NULL OR effective_to >= effective_from);
ALTER TABLE employee_assignments ADD CONSTRAINT ck_employee_assignments__effective_range
    CHECK (effective_to IS NULL OR effective_to >= effective_from);
ALTER TABLE employee_salaries ADD CONSTRAINT ck_employee_salaries__effective_range
    CHECK (effective_to IS NULL OR effective_to >= effective_from);
ALTER TABLE employee_contracts ADD CONSTRAINT ck_employee_contracts__effective_range
    CHECK (effective_to IS NULL OR effective_to >= effective_from);
ALTER TABLE employee_bank_accounts ADD CONSTRAINT ck_employee_bank_accounts__effective_range
    CHECK (effective_to IS NULL OR effective_to >= effective_from);
ALTER TABLE statutory_rules ADD CONSTRAINT ck_statutory_rules__effective_range
    CHECK (effective_to IS NULL OR effective_to >= effective_from);
-- Bands use the other convention: inclusive lower, EXCLUSIVE upper, so a zero-width
-- band is meaningless and the bound must be strict (§5, §2.E).
ALTER TABLE statutory_rule_bands ADD CONSTRAINT ck_statutory_rule_bands__bounds
    CHECK (upper_bound IS NULL OR upper_bound > lower_bound);

-- -----------------------------------------------------------------------------
-- 2g. Domain sanity on periods, counters and quantities.
-- -----------------------------------------------------------------------------
ALTER TABLE payroll_runs ADD CONSTRAINT ck_payroll_runs__month_range
    CHECK (month BETWEEN 1 AND 12);
ALTER TABLE payroll_inputs ADD CONSTRAINT ck_payroll_inputs__run_month_range
    CHECK (run_month BETWEEN 1 AND 12);
ALTER TABLE payroll_inputs ADD CONSTRAINT ck_payroll_inputs__covered_month_range
    CHECK (covered_month BETWEEN 1 AND 12);
ALTER TABLE companies ADD CONSTRAINT ck_companies__go_live_month_range
    CHECK (go_live_month IS NULL OR go_live_month BETWEEN 1 AND 12);
-- The go-live period is two columns, so either both are set or neither is (§2.C).
ALTER TABLE companies ADD CONSTRAINT ck_companies__go_live_period_complete
    CHECK ((go_live_year IS NULL) = (go_live_month IS NULL));
ALTER TABLE payroll_inputs ADD CONSTRAINT ck_payroll_inputs__revision_positive
    CHECK (revision >= 1);
ALTER TABLE document_templates ADD CONSTRAINT ck_document_templates__version_positive
    CHECK (version >= 1);
ALTER TABLE employee_documents ADD CONSTRAINT ck_employee_documents__version_positive
    CHECK (version >= 1);
ALTER TABLE number_sequences ADD CONSTRAINT ck_number_sequences__next_value_positive
    CHECK (next_value >= 1);
ALTER TABLE files ADD CONSTRAINT ck_files__size_bytes_nonnegative
    CHECK (size_bytes >= 0);
ALTER TABLE retention_policies ADD CONSTRAINT ck_retention_policies__minimum_nonnegative
    CHECK (minimum_retention_months >= 0);
ALTER TABLE users ADD CONSTRAINT ck_users__failed_login_count_nonnegative
    CHECK (failed_login_count >= 0);
ALTER TABLE platform_users ADD CONSTRAINT ck_platform_users__failed_login_count_nonnegative
    CHECK (failed_login_count >= 0);
ALTER TABLE auth_tokens ADD CONSTRAINT ck_auth_tokens__attempts_nonnegative
    CHECK (attempts >= 0);
ALTER TABLE payroll_runs ADD CONSTRAINT ck_payroll_runs__counts_nonnegative
    CHECK (employee_count >= 0 AND selected_employee_count >= 0);
ALTER TABLE grades ADD CONSTRAINT ck_grades__basic_band_ordered
    CHECK (min_basic IS NULL OR max_basic IS NULL OR max_basic >= min_basic);
-- A self-parent is not a hierarchy (§8 rows 25, 28, 50, 56).
ALTER TABLE departments ADD CONSTRAINT ck_departments__parent_not_self
    CHECK (parent_id IS NULL OR parent_id <> id);
ALTER TABLE cost_centers ADD CONSTRAINT ck_cost_centers__parent_not_self
    CHECK (parent_id IS NULL OR parent_id <> id);
ALTER TABLE employee_documents ADD CONSTRAINT ck_employee_documents__supersedes_not_self
    CHECK (supersedes_id IS NULL OR supersedes_id <> id);
ALTER TABLE payroll_runs ADD CONSTRAINT ck_payroll_runs__parent_not_self
    CHECK (parent_run_id IS NULL OR parent_run_id <> id);
ALTER TABLE employee_assignments ADD CONSTRAINT ck_employee_assignments__manager_not_self
    CHECK (manager_employee_id IS NULL OR manager_employee_id <> employee_id);


-- =============================================================================
-- PART 3 — EXCLUSION CONSTRAINTS (btree_gist)
--
-- Dates are INCLUSIVE-INCLUSIVE, always spelled
--   daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]')
-- Numeric bands are inclusive-lower / EXCLUSIVE-upper, '[)'. That is the whole rule
-- (CONVENTIONS.md §5).
-- =============================================================================

-- Seven of the ten effective-dated tables named in CONVENTIONS.md §5 are in A–F. The
-- other three — shift_assignments, employee_gosi_registrations, nitaqat_grid — are G–R.

ALTER TABLE company_pay_policies ADD CONSTRAINT ex_company_pay_policies__policy_no_overlap
    EXCLUDE USING gist (
        tenant_id WITH =,
        company_id WITH =,
        policy_key WITH =,
        -- A NULL pay_component_code would make the row invisible to the exclusion
        -- (a NULL operand yields NULL, and the constraint skips it), so the
        -- component-wide policy would silently be allowed to overlap itself.
        COALESCE(pay_component_code, '') WITH =,
        daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&
    );

ALTER TABLE employee_assignments ADD CONSTRAINT ex_employee_assignments__employee_no_overlap
    EXCLUDE USING gist (
        tenant_id WITH =,
        employee_id WITH =,
        daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&
    );

ALTER TABLE employee_salaries ADD CONSTRAINT ex_employee_salaries__employee_no_overlap
    EXCLUDE USING gist (
        tenant_id WITH =,
        employee_id WITH =,
        daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&
    );

ALTER TABLE employee_contracts ADD CONSTRAINT ex_employee_contracts__employee_no_overlap
    EXCLUDE USING gist (
        tenant_id WITH =,
        employee_id WITH =,
        daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&
    );

ALTER TABLE employee_bank_accounts ADD CONSTRAINT ex_employee_bank_accounts__employee_no_overlap
    EXCLUDE USING gist (
        tenant_id WITH =,
        employee_id WITH =,
        daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&
    );

-- retention_policies: tenant_id is NULLABLE (NULL = the platform default row), and a
-- NULL operand makes the exclusion skip the row entirely — so without the COALESCE the
-- platform rows, which are exactly the rows the retention engine depends on, would be
-- the ONLY ones free to overlap. Reported: the design states the EXCLUDE convention
-- without addressing its interaction with a nullable subject column.
ALTER TABLE retention_policies ADD CONSTRAINT ex_retention_policies__entity_no_overlap
    EXCLUDE USING gist (
        COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid) WITH =,
        entity_name WITH =,
        daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&
    );

-- statutory_rules: reference tier, so the subject is the full dimension set rather than
-- a tenant. Same COALESCE reasoning for the two nullable dimensions.
ALTER TABLE statutory_rules ADD CONSTRAINT ex_statutory_rules__rule_no_overlap
    EXCLUDE USING gist (
        country_code WITH =,
        family WITH =,
        rule_key WITH =,
        nationality_class WITH =,
        cohort WITH =,
        COALESCE(gosi_branch, '') WITH =,
        COALESCE(payer, '') WITH =,
        daterange(effective_from, COALESCE(effective_to, 'infinity'::date), '[]') WITH &&
    );

-- statutory_rule_bands: the numeric convention, '[)' — stated verbatim in §2.E.
ALTER TABLE statutory_rule_bands ADD CONSTRAINT ex_statutory_rule_bands__band_no_overlap
    EXCLUDE USING gist (
        statutory_rule_id WITH =,
        numrange(lower_bound, COALESCE(upper_bound, 'infinity'::numeric), '[)') WITH &&
    );


-- =============================================================================
-- PART 4 — THE SIX FOREIGN KEYS DELIBERATELY NOT DECLARED HERE
--
-- Their child is an A–F table but their parent is not, so they cannot be created until
-- G–R exists. Whoever writes the G–R constraints file must add exactly these, with the
-- ON DELETE shown, and make each composite on (tenant_id, x_id) per CONVENTIONS.md §3.
-- Listed by §8.2 register number so nothing is lost between the two halves.
--
--   41  employee_assignments.approval_request_id -> approval_requests   O  RESTRICT
--   43  employee_salaries.approval_request_id    -> approval_requests   O  RESTRICT
--   51  employee_documents.leave_request_id      -> leave_requests      O  SET NULL (leave_request_id)
--   57  payroll_runs.source_import_job_id        -> background_jobs     O  SET NULL (source_import_job_id)
--                                                   NOTE: background_jobs has a NULLABLE
--                                                   tenant_id, so this one cannot be
--                                                   composite on (tenant_id, id) unless
--                                                   background_jobs carries
--                                                   UNIQUE NULLS NOT DISTINCT (tenant_id, id).
--                                                   Flagged for the G–R author.
--   58  payroll_runs.approval_request_id         -> approval_requests   O  RESTRICT
--   68  payroll_slip_lines.loan_installment_id   -> loan_installments   O  RESTRICT
--
-- All six columns already exist with the nullability §8 requires; only the constraints
-- are missing.
-- =============================================================================
