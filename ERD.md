# KynexOne Entity Relationship Diagrams (ERD)

This document contains the complete Entity Relationship Diagrams (ERDs) for the **KynexOne** enterprise workforce operating system (PostgreSQL schema). All diagrams are written in GitHub-compliant **Mermaid** syntax (`erDiagram`).

---

## Table of Contents
1. [Core System Overview](#1-core-system-overview)
2. [Domain 1: Tenancy, Identity, Platform & RBAC](#2-domain-1-tenancy-identity-platform--rbac)
3. [Domain 2: Organization Hierarchy & Legal Entities](#3-domain-2-organization-hierarchy--legal-entities)
4. [Domain 3: Workforce, Assignments, Salaries & Contracts](#4-domain-3-workforce-assignments-salaries--contracts)
5. [Domain 4: Payroll Engine & Statutory Rules](#5-domain-4-payroll-engine--statutory-rules)
6. [Domain 5: Wages Protection System (WPS) & GL Accounting](#6-domain-5-wages-protection-system-wps--gl-accounting)
7. [Domain 6: Time, Attendance, Overtime & Timesheets](#7-domain-6-time-attendance-overtime--timesheets)
8. [Domain 7: Leave Management & Ledger Transactions](#8-domain-7-leave-management--ledger-transactions)
9. [Domain 8: Loans, Advances & End of Service (EOS)](#9-domain-8-loans-advances--end-of-service-eos)
10. [Domain 9: Statutory Compliance (GOSI & Nitaqat)](#10-domain-9-statutory-compliance-gosi--nitaqat)
11. [Domain 10: Approvals, Notifications, Audit & Background Jobs](#11-domain-10-approvals-notifications-audit--background-jobs)
12. [Architectural Conventions](#12-architectural-conventions)

---

## 1. Core System Overview

The high-level relationship between the primary functional domains: Multi-Tenancy, Organization Structure, Employee Profile, Payroll Execution, Time & Attendance, and Approvals.

```mermaid
erDiagram
    TENANTS ||--o{ USERS : "provisions"
    TENANTS ||--o{ COMPANIES : "owns"
    TENANTS ||--o{ ROLES : "defines"
    TENANTS ||--o{ EMPLOYEES : "employs"
    TENANTS ||--o{ PAY_COMPONENTS : "catalogs"
    TENANTS ||--o{ APPROVAL_WORKFLOWS : "configures"

    COMPANIES ||--o{ BRANCHES : "locates"
    COMPANIES ||--o{ DEPARTMENTS : "organizes"
    COMPANIES ||--o{ COST_CENTERS : "segments"
    COMPANIES ||--o{ PAYROLL_RUNS : "runs"
    COMPANIES ||--o{ EMPLOYEE_ASSIGNMENTS : "places"

    USERS |o--|| EMPLOYEES : "links_to"
    USERS ||--o{ USER_ROLES : "assigned"
    ROLES ||--o{ USER_ROLES : "granted_to"
    ROLES ||--o{ ROLE_PERMISSIONS : "contains"

    EMPLOYEES ||--o{ EMPLOYEE_ASSIGNMENTS : "history"
    EMPLOYEES ||--o{ EMPLOYEE_SALARIES : "compensation"
    EMPLOYEES ||--o{ EMPLOYEE_CONTRACTS : "contracts"
    EMPLOYEES ||--o{ EMPLOYEE_BANK_ACCOUNTS : "banking"
    EMPLOYEES ||--o{ EMPLOYEE_DOCUMENTS : "records"

    EMPLOYEES ||--o{ ATTENDANCE_DAYS : "records_daily"
    EMPLOYEES ||--o{ LEAVE_REQUESTS : "requests"
    EMPLOYEES ||--o{ LEAVE_LEDGER : "balances"
    EMPLOYEES ||--o{ LOANS : "borrows"
    EMPLOYEES ||--o{ PAYROLL_SLIPS : "receives"

    PAYROLL_RUNS ||--o{ PAYROLL_SLIPS : "generates"
    PAYROLL_SLIPS ||--o{ PAYROLL_SLIP_LINES : "details"
    PAYROLL_RUNS ||--o{ WPS_BATCHES : "files"
    PAYROLL_RUNS ||--o{ GL_JOURNALS : "posts"

    APPROVAL_WORKFLOWS ||--o{ APPROVAL_REQUESTS : "governs"
    APPROVAL_REQUESTS ||--o{ APPROVAL_ACTIONS : "decides"
```

---

## 2. Domain 1: Tenancy, Identity, Platform & RBAC

Handles multi-tenant account isolation, platform operators, user authentication, sessions, single-use security tokens, and fine-grained scoped RBAC.

```mermaid
erDiagram
    TENANTS ||--o{ TENANT_SETTINGS : "configures"
    TENANTS ||--o{ USERS : "owns"
    TENANTS ||--o{ ROLES : "defines"
    TENANTS ||--o{ NUMBER_SEQUENCES : "allocates"
    TENANTS ||--o{ FILES : "stores"
    TENANTS ||--o{ RETENTION_POLICIES : "overrides"

    PLATFORM_USERS ||--o{ AUTH_SESSIONS : "operator_session"
    PLATFORM_USERS ||--o{ AUTH_TOKENS : "operator_token"

    USERS ||--o{ USER_ROLES : "assigned"
    USERS ||--o{ AUTH_SESSIONS : "user_session"
    USERS ||--o{ AUTH_TOKENS : "user_token"
    USERS ||--o{ PERMISSION_GRANTOR_RECORDS : "delegates"

    ROLES ||--o{ USER_ROLES : "granted"
    ROLES ||--o{ ROLE_PERMISSIONS : "includes"
    PERMISSIONS ||--o{ ROLE_PERMISSIONS : "categorized"

    TENANTS {
        uuid id PK
        string slug UK
        string name
        string timezone_id
        string status
        jsonb plan_limits
        timestamptz soft_deleted_at
    }

    PLATFORM_USERS {
        uuid id PK
        string email UK
        string full_name
        string password_hash
        string platform_role
        string status
    }

    USERS {
        uuid id PK
        uuid tenant_id FK
        string normalized_email UK
        string password_hash
        uuid employee_id FK
        boolean mfa_enabled
        string status
    }

    ROLES {
        uuid id PK
        uuid tenant_id FK
        string code UK
        string name
        boolean is_system
    }

    PERMISSIONS {
        string code PK
        string module
        string description
    }

    USER_ROLES {
        uuid id PK
        uuid tenant_id FK
        uuid user_id FK
        uuid role_id FK
        uuid scope_company_id FK
        uuid scope_branch_id FK
        uuid scope_department_id FK
        timestamptz expires_at
    }

    PERMISSION_GRANTOR_RECORDS {
        uuid id PK
        uuid tenant_id FK
        uuid grantor_user_id FK
        string permission_scope
        boolean can_sub_delegate
        uuid granted_by_user_id FK
        timestamptz expires_at
        boolean is_active
    }

    AUTH_SESSIONS {
        uuid id PK
        string subject_kind
        uuid user_id FK
        uuid platform_user_id FK
        string device_id
        string refresh_token_hash
        timestamptz expires_at
        timestamptz revoked_at
    }

    AUTH_TOKENS {
        uuid id PK
        string subject_kind
        uuid user_id FK
        uuid platform_user_id FK
        string purpose
        string token_hash UK
        timestamptz expires_at
        timestamptz consumed_at
    }
```

---

## 3. Domain 2: Organization Hierarchy & Legal Entities

Covers legal entities (MOL establishments), branches, hierarchical departments, cost centers, designations, grades, and company-level pay policies.

```mermaid
erDiagram
    TENANTS ||--o{ COMPANIES : "incorporates"
    TENANTS ||--o{ DESIGNATIONS : "defines"
    TENANTS ||--o{ GRADES : "scales"
    TENANTS ||--o{ PUBLIC_HOLIDAYS : "calendars"

    COMPANIES ||--o{ BRANCHES : "establishes"
    COMPANIES ||--o{ DEPARTMENTS : "structures"
    COMPANIES ||--o{ COST_CENTERS : "allocates"
    COMPANIES ||--o{ COMPANY_PAY_POLICIES : "sets"

    DEPARTMENTS ||--o{ DEPARTMENTS : "parent_unit"
    DEPARTMENTS |o--o{ COST_CENTERS : "assigned_cost"
    COST_CENTERS ||--o{ COST_CENTERS : "parent_cost_center"

    COMPANIES {
        uuid id PK
        uuid tenant_id FK
        string name_en
        string name_ar
        string cr_number
        string mol_establishment_no
        string gosi_registration_no UK
        string currency_code
        int go_live_year
        int go_live_month
    }

    COMPANY_PAY_POLICIES {
        uuid id PK
        uuid tenant_id FK
        uuid company_id FK
        string policy_key
        string pay_component_code FK
        numeric rate
        numeric amount
        date effective_from
        date effective_to
    }

    BRANCHES {
        uuid id PK
        uuid tenant_id FK
        uuid company_id FK
        string name
        string city
        numeric lat
        numeric lng
        int geofence_radius_m
    }

    DEPARTMENTS {
        uuid id PK
        uuid tenant_id FK
        uuid company_id FK
        uuid parent_id FK
        uuid cost_center_id FK
        string name
    }

    COST_CENTERS {
        uuid id PK
        uuid tenant_id FK
        uuid company_id FK
        uuid parent_id FK
        string code UK
        string name
        string gl_segment
        boolean is_active
    }

    DESIGNATIONS {
        uuid id PK
        uuid tenant_id FK
        string title_en
        string title_ar
        string occupation_code
    }

    GRADES {
        uuid id PK
        uuid tenant_id FK
        string code UK
        string name
        numeric min_basic
        numeric max_basic
    }

    PUBLIC_HOLIDAYS {
        uuid id PK
        uuid tenant_id FK
        string calendar_code
        date holiday_date
        string name
        boolean is_paid
    }
```

---

## 4. Domain 3: Workforce, Assignments, Salaries & Contracts

Models employee profile facts, effective-dated placements, compensation structures, labour contracts, bank accounts (IBAN), and document attachments.

```mermaid
erDiagram
    TENANTS ||--o{ EMPLOYEES : "employs"
    EMPLOYEES ||--o{ EMPLOYEE_ASSIGNMENTS : "positions"
    EMPLOYEES ||--o{ EMPLOYEE_SALARIES : "compensates"
    EMPLOYEES ||--o{ EMPLOYEE_CONTRACTS : "binds"
    EMPLOYEES ||--o{ EMPLOYEE_BANK_ACCOUNTS : "pays_to"
    EMPLOYEES ||--o{ EMPLOYEE_DOCUMENTS : "holds"

    EMPLOYEE_DOCUMENTS ||--o{ FILES : "attaches"
    DOCUMENT_TEMPLATES ||--o{ EMPLOYEE_DOCUMENTS : "generates_from"

    EMPLOYEE_ASSIGNMENTS }o--|| COMPANIES : "placed_in"
    EMPLOYEE_ASSIGNMENTS }o--|| BRANCHES : "assigned_branch"
    EMPLOYEE_ASSIGNMENTS }o--|| DEPARTMENTS : "assigned_dept"
    EMPLOYEE_ASSIGNMENTS }o--|| DESIGNATIONS : "assigned_title"
    EMPLOYEE_ASSIGNMENTS }o--o| GRADES : "assigned_grade"
    EMPLOYEE_ASSIGNMENTS }o--o| COST_CENTERS : "assigned_cost"
    EMPLOYEE_ASSIGNMENTS }o--o| EMPLOYEES : "reports_to_manager"

    EMPLOYEES {
        uuid id PK
        uuid tenant_id FK
        string employee_number UK
        string work_email
        string first_name_en
        string last_name_en
        string national_id
        string iqama_no
        string nationality_code
        date joining_date
        date gosi_first_registered_on
        string status
        boolean wps_eligible
        date eos_service_start_date
    }

    EMPLOYEE_ASSIGNMENTS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        date effective_from
        date effective_to
        uuid company_id FK
        uuid branch_id FK
        uuid department_id FK
        uuid designation_id FK
        uuid grade_id FK
        uuid manager_employee_id FK
        uuid cost_center_id FK
    }

    EMPLOYEE_SALARIES {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        date effective_from
        date effective_to
        numeric basic
        numeric housing
        numeric transport
        jsonb components
    }

    EMPLOYEE_CONTRACTS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        date effective_from
        date effective_to
        string contract_type
        date start_date
        date end_date
        numeric weekly_hours
        string qiwa_contract_no
    }

    EMPLOYEE_BANK_ACCOUNTS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        date effective_from
        date effective_to
        string iban
        string bank_code
        string payment_method
    }

    EMPLOYEE_DOCUMENTS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        string doc_type
        string document_number
        date issue_date
        date expiry_date
        uuid file_id FK
        uuid supersedes_id FK
    }
```

---

## 5. Domain 4: Payroll Engine & Statutory Rules

Governs monthly payroll calculation, wage snapshots, component itemization, variable inputs (arrears/overtime/deductions), statutory GOSI rules, and validation blockers.

```mermaid
erDiagram
    COMPANIES ||--o{ PAYROLL_RUNS : "computes"
    PAYROLL_RUNS ||--o{ PAYROLL_SLIPS : "produces"
    PAYROLL_SLIPS ||--o{ PAYROLL_SLIP_LINES : "details"

    EMPLOYEES ||--o{ PAYROLL_SLIPS : "issued_to"
    EMPLOYEES ||--o{ PAYROLL_INPUTS : "receives_variable"

    PAY_COMPONENTS ||--o{ PAYROLL_SLIP_LINES : "classifies"
    PAY_COMPONENTS ||--o{ PAYROLL_INPUTS : "types"

    STATUTORY_RULES ||--o{ STATUTORY_RULE_BANDS : "bands"
    STATUTORY_RULES ||--o{ PAYROLL_SLIP_LINES : "applies_law"
    STATUTORY_RULE_BANDS ||--o{ PAYROLL_SLIP_LINES : "applies_band"

    PAYROLL_INPUTS |o--o| PAYROLL_SLIP_LINES : "consumed_by"
    PAYROLL_RUNS ||--o{ PAYROLL_ISSUES : "validates"

    PAYROLL_RUNS {
        uuid id PK
        uuid tenant_id FK
        uuid company_id FK
        int year
        int month
        string run_type
        string status
        numeric total_gross
        numeric total_deductions
        numeric total_net
        numeric total_employer_statutory
        int employee_count
        daterange attendance_locked_range
    }

    PAYROLL_SLIPS {
        uuid id PK
        uuid tenant_id FK
        uuid run_id FK
        uuid employee_id FK
        string employee_number
        string gosi_cohort
        numeric gross
        numeric deductions
        numeric net
        numeric contributory_wage
        numeric employee_statutory_total
        numeric employer_statutory_total
        numeric ytd_gross
        numeric ytd_net
    }

    PAYROLL_SLIP_LINES {
        uuid id PK
        uuid tenant_id FK
        uuid slip_id FK
        string pay_component_code FK
        string kind
        numeric amount
        numeric rate
        string gosi_branch
        string gosi_payer
        uuid statutory_rule_id FK
        uuid payroll_input_id FK
        uuid loan_installment_id FK
        uuid cost_center_id FK
    }

    PAYROLL_INPUTS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        uuid company_id FK
        int run_year
        int run_month
        int covered_year
        int covered_month
        string kind
        string pay_component_code FK
        numeric amount
        numeric gosi_basis_delta
        string status
        uuid claimed_by_run_id FK
        uuid consumed_run_id FK
    }

    STATUTORY_RULES {
        uuid id PK
        string country_code
        string family
        string rule_key
        string nationality_class
        string cohort
        string gosi_branch
        string gosi_payer
        numeric rate
        numeric wage_floor
        numeric wage_cap
        date effective_from
        date effective_to
    }

    STATUTORY_RULE_BANDS {
        uuid id PK
        uuid statutory_rule_id FK
        string band_key
        string unit
        numeric lower_bound
        numeric upper_bound
        numeric rate
        numeric amount
    }

    PAYROLL_ISSUES {
        uuid id PK
        uuid tenant_id FK
        uuid run_id FK
        uuid employee_id FK
        string severity
        string code
        string message
        timestamptz resolved_at
    }
```

---

## 6. Domain 5: Wages Protection System (WPS) & GL Accounting

Handles KSA central bank / MHRSD compliant SIF file generation, bank acknowledgements, balanced double-entry ERP journal posting, and accounting period locks.

```mermaid
erDiagram
    PAYROLL_RUNS ||--o{ WPS_BATCHES : "generates_sif"
    WPS_BATCHES ||--o{ WPS_LINES : "contains"
    PAYROLL_SLIPS ||--o{ WPS_LINES : "paid_by_sif"

    COMPANIES ||--o{ GL_JOURNALS : "creates_journal"
    GL_JOURNALS ||--o{ GL_JOURNAL_LINES : "balanced_lines"
    COMPANIES ||--o{ GL_PERIOD_CLOSES : "locks_period"
    COMPANIES ||--o{ GL_MAPPINGS : "rules"

    COST_CENTERS ||--o{ GL_JOURNAL_LINES : "segments"
    COST_CENTERS ||--o{ GL_MAPPINGS : "maps"

    WPS_BATCHES {
        uuid id PK
        uuid tenant_id FK
        uuid run_id FK
        uuid company_id FK
        string batch_number
        string status
        int employee_count
        numeric total_amount
        string submission_reference
        uuid file_id FK
    }

    WPS_LINES {
        uuid id PK
        uuid tenant_id FK
        uuid batch_id FK
        uuid slip_id FK
        uuid employee_id FK
        string id_number
        string iban
        numeric basic
        numeric housing
        numeric deductions
        numeric net
        string bank_status
    }

    GL_MAPPINGS {
        uuid id PK
        uuid tenant_id FK
        uuid company_id FK
        string gl_driver
        uuid cost_center_id FK
        string debit_account
        string credit_account
    }

    GL_JOURNALS {
        uuid id PK
        uuid tenant_id FK
        uuid company_id FK
        string source_type
        uuid source_id
        int year
        int month
        string status
        string erp_reference
        uuid file_id FK
    }

    GL_JOURNAL_LINES {
        uuid id PK
        uuid tenant_id FK
        uuid journal_id FK
        string account
        uuid cost_center_id FK
        numeric debit
        numeric credit
        string description
    }

    GL_PERIOD_CLOSES {
        uuid id PK
        uuid tenant_id FK
        uuid company_id FK
        int year
        int month
        string status
        uuid closed_by FK
        timestamptz closed_at
    }
```

---

## 7. Domain 6: Time, Attendance, Overtime & Timesheets

Tracks biometric device ingestion, raw punches, calculated daily attendance, overtime claims with statutory rates, employee timesheets, and reconciliation variance.

```mermaid
erDiagram
    TENANTS ||--o{ SHIFTS : "schedules"
    SHIFTS ||--o{ SHIFT_ASSIGNMENTS : "rostered"
    EMPLOYEES ||--o{ SHIFT_ASSIGNMENTS : "assigned_to"

    BRANCHES ||--o{ ATTENDANCE_DEVICES : "installs"
    ATTENDANCE_DEVICES ||--o{ ATTENDANCE_PUNCHES : "syncs"
    EMPLOYEES ||--o{ ATTENDANCE_PUNCHES : "punches"

    EMPLOYEES ||--o{ ATTENDANCE_DAYS : "computes_day"
    SHIFTS ||--o{ ATTENDANCE_DAYS : "evaluates_against"
    PAYROLL_RUNS |o--o{ ATTENDANCE_DAYS : "locks_range"

    EMPLOYEES ||--o{ OVERTIME_REQUESTS : "claims"
    STATUTORY_RULES ||--o{ OVERTIME_REQUESTS : "applies_rate"

    EMPLOYEES ||--o{ TIMESHEETS : "submits"
    TIMESHEETS ||--o{ TIMESHEET_ENTRIES : "entry_lines"
    COST_CENTERS ||--o{ TIMESHEET_ENTRIES : "charged_to"
    TIMESHEETS ||--o{ TIMESHEET_DAY_RECONCILIATIONS : "reconciles"
    ATTENDANCE_DAYS ||--o{ TIMESHEET_DAY_RECONCILIATIONS : "compares_against"

    SHIFTS {
        uuid id PK
        uuid tenant_id FK
        string code
        time start_time
        time end_time
        int break_minutes
        jsonb rules
    }

    ATTENDANCE_PUNCHES {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        uuid device_id FK
        timestamptz occurred_at
        string direction
        string source
        numeric lat
        numeric lng
    }

    ATTENDANCE_DAYS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        date work_date
        uuid shift_id FK
        int scheduled_minutes
        int worked_minutes
        int late_minutes
        int early_out_minutes
        int overtime_minutes
        int absent_minutes
        uuid locked_run_id FK
    }

    OVERTIME_REQUESTS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        date work_date
        int overtime_minutes
        string ot_type
        numeric multiplier
        numeric amount
        string status
        string payout
    }

    TIMESHEETS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        uuid company_id FK
        date period_start
        date period_end
        string status
        int total_minutes
        uuid locked_run_id FK
    }

    TIMESHEET_ENTRIES {
        uuid id PK
        uuid tenant_id FK
        uuid timesheet_id FK
        date work_date
        int minutes
        uuid cost_center_id FK
        string task
        boolean billable
    }

    TIMESHEET_DAY_RECONCILIATIONS {
        uuid id PK
        uuid tenant_id FK
        uuid timesheet_id FK
        uuid attendance_day_id FK
        date work_date
        int timesheet_minutes
        int attendance_minutes
        int variance_minutes
        string status
    }
```

---

## 8. Domain 7: Leave Management & Ledger Transactions

Manages statutory (Saudi Labour Law Art. 109–117) and company leave policies, request approval flows, and an append-only transaction ledger.

```mermaid
erDiagram
    TENANTS ||--o{ LEAVE_TYPES : "configures"
    LEAVE_TYPES ||--o{ LEAVE_REQUESTS : "categorizes"
    LEAVE_TYPES ||--o{ LEAVE_LEDGER : "audits_balance"

    EMPLOYEES ||--o{ LEAVE_REQUESTS : "submits"
    EMPLOYEES ||--o{ LEAVE_LEDGER : "holds_ledger"

    APPROVAL_REQUESTS |o--o| LEAVE_REQUESTS : "routes_decision"
    EMPLOYEE_DOCUMENTS |o--o| LEAVE_REQUESTS : "medical_certificate"

    LEAVE_TYPES {
        uuid id PK
        uuid tenant_id FK
        string code UK
        string name_en
        string name_ar
        boolean is_statutory
        string pay_rule_key
        jsonb policy
    }

    LEAVE_REQUESTS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        uuid leave_type_id FK
        string request_kind
        date start_date
        date end_date
        numeric days
        string status
        uuid approval_request_id FK
    }

    LEAVE_LEDGER {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        uuid leave_type_id FK
        date entry_date
        string entry_type
        numeric days
        string source_type
        uuid source_id
        string reason
    }
```

---

## 9. Domain 8: Loans, Advances & End of Service (EOS)

Governs employee loans, installment amortization, Article 84/85/87 End of Service gratuity computations, and final settlement payment lines.

```mermaid
erDiagram
    EMPLOYEES ||--o{ LOANS : "applies_loan"
    LOANS ||--o{ LOAN_INSTALLMENTS : "amortizes"

    EMPLOYEES ||--o{ EOS_CALCULATIONS : "estimates_eos"
    EMPLOYEES ||--o{ FINAL_SETTLEMENTS : "terminates"
    FINAL_SETTLEMENTS ||--o{ FINAL_SETTLEMENT_LINES : "settles"

    FINAL_SETTLEMENTS |o--o| EOS_CALCULATIONS : "incorporates"
    FINAL_SETTLEMENT_LINES }o--o| LOAN_INSTALLMENTS : "recovers_loan"
    FINAL_SETTLEMENTS }o--o| PAYROLL_RUNS : "paid_via_run"

    LOANS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        string kind
        numeric principal
        int installment_count
        int start_year
        int start_month
        numeric outstanding
        string status
    }

    LOAN_INSTALLMENTS {
        uuid id PK
        uuid tenant_id FK
        uuid loan_id FK
        int installment_number
        int due_year
        int due_month
        numeric amount
        string kind
        string status
        timestamptz recovered_at
    }

    EOS_CALCULATIONS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        date calculation_date
        string separation_reason
        date service_start
        int service_days
        numeric eligible_wage
        numeric amount
        jsonb rules_snapshot
        string status
    }

    FINAL_SETTLEMENTS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        uuid company_id FK
        string separation_type
        date last_working_day
        numeric gross
        numeric deductions
        numeric net
        string status
        uuid paid_via_run_id FK
    }

    FINAL_SETTLEMENT_LINES {
        uuid id PK
        uuid tenant_id FK
        uuid settlement_id FK
        string kind
        numeric amount
        uuid loan_installment_id FK
        string rules_version
    }
```

---

## 10. Domain 9: Statutory Compliance (GOSI & Nitaqat)

Models GOSI registration records at the establishment level, monthly return filing with branch-level reconciliation, and point-in-time Nitaqat Saudization quotas.

```mermaid
erDiagram
    COMPANIES ||--o{ EMPLOYEE_GOSI_REGISTRATIONS : "establishment_gosi"
    EMPLOYEES ||--o{ EMPLOYEE_GOSI_REGISTRATIONS : "registered_in"

    COMPANIES ||--o{ GOSI_FILINGS : "files_monthly_return"
    GOSI_FILINGS ||--o{ FILES : "attaches_form"

    NITAQAT_GRID ||--o{ NITAQAT_SNAPSHOTS : "evaluates"
    COMPANIES ||--o{ NITAQAT_SNAPSHOTS : "monthly_saudization"

    EMPLOYEE_GOSI_REGISTRATIONS {
        uuid id PK
        uuid tenant_id FK
        uuid employee_id FK
        uuid company_id FK
        string gosi_registration_no
        string gosi_employee_no
        date effective_from
        date effective_to
        numeric registered_contributory_wage
        string occupation_code
        string status
    }

    GOSI_FILINGS {
        uuid id PK
        uuid tenant_id FK
        uuid company_id FK
        string gosi_registration_no
        int year
        int month
        string status
        int employee_count
        numeric annuities_employee
        numeric annuities_employer
        numeric saned_employee
        numeric saned_employer
        numeric occupational_hazards_employer
        numeric total_amount
        uuid file_id FK
    }

    NITAQAT_GRID {
        uuid id PK
        string activity_code
        string size_tier
        string band
        numeric min_saudization_pct
        numeric max_saudization_pct
        date effective_from
        date effective_to
    }

    NITAQAT_SNAPSHOTS {
        uuid id PK
        uuid tenant_id FK
        uuid company_id FK
        date as_of_date
        string activity_code
        string size_tier
        numeric saudi_weighted
        numeric total_weighted
        numeric achieved_pct
        string band
        jsonb employee_breakdown
    }
```

---

## 11. Domain 10: Approvals, Notifications, Audit & Background Jobs

Covers generic approval workflows, user inboxes and multi-channel notifications, async background jobs, and PDPL-compliant tamper-evident audit logs.

```mermaid
erDiagram
    TENANTS ||--o{ APPROVAL_WORKFLOWS : "configures"
    APPROVAL_WORKFLOWS ||--o{ APPROVAL_REQUESTS : "initiates"
    APPROVAL_REQUESTS ||--o{ APPROVAL_ACTIONS : "records_action"
    USERS ||--o{ APPROVAL_DELEGATIONS : "delegates_authority"

    USERS ||--o{ NOTIFICATIONS : "receives"
    NOTIFICATIONS ||--o{ NOTIFICATION_DELIVERIES : "transmits"

    TENANTS ||--o{ BACKGROUND_JOBS : "executes"
    BACKGROUND_JOBS ||--o{ BACKGROUND_JOB_ITEMS : "processes_row"

    APPROVAL_WORKFLOWS {
        uuid id PK
        uuid tenant_id FK
        uuid company_id FK
        string request_type
        jsonb steps
        boolean is_active
    }

    APPROVAL_REQUESTS {
        uuid id PK
        uuid tenant_id FK
        uuid workflow_id FK
        string request_type
        string subject_type
        uuid subject_id
        uuid requester_user_id FK
        uuid employee_id FK
        jsonb payload
        int current_step
        string status
        uuid current_approver_user_id
    }

    APPROVAL_ACTIONS {
        uuid id PK
        uuid tenant_id FK
        uuid request_id FK
        int step
        uuid actor_user_id FK
        uuid on_behalf_of_user_id FK
        string action
        string comment
    }

    APPROVAL_DELEGATIONS {
        uuid id PK
        uuid tenant_id FK
        uuid delegator_user_id FK
        uuid delegate_user_id FK
        date from_date
        date to_date
        text_array request_types
    }

    NOTIFICATIONS {
        uuid id PK
        uuid tenant_id FK
        uuid user_id FK
        string category
        string title
        string body
        timestamptz read_at
    }

    NOTIFICATION_DELIVERIES {
        uuid id PK
        uuid tenant_id FK
        uuid notification_id FK
        string channel
        string destination
        string status
        int attempts
    }

    AUDIT_LOGS {
        uuid id PK
        uuid tenant_id
        uuid company_id
        string record_kind
        string category
        string action
        string entity
        uuid entity_id
        uuid actor_user_id
        jsonb before
        jsonb after
        string envelope_hash
        timestamptz created_at
    }

    PAYROLL_AUDIT_LOGS {
        uuid id PK
        uuid tenant_id
        uuid run_id
        string action
        jsonb before
        jsonb after
        int seq
        string prev_hash
        string entry_hash
        timestamptz created_at
    }

    BACKGROUND_JOBS {
        uuid id PK
        uuid tenant_id FK
        string kind
        string status
        int progress_current
        int progress_total
        uuid source_file_id FK
        timestamptz heartbeat_at
    }

    BACKGROUND_JOB_ITEMS {
        uuid id PK
        uuid job_id FK
        string row_ref
        string status
        string error_code
        string error_message
        timestamptz created_at
    }
```

---

## 12. Architectural Conventions

| Concept | Implementation Standard | Guarantees |
| :--- | :--- | :--- |
| **Primary Keys** | UUIDv7 across every table, client-side generated | Monotonic index locality, zero lock contention |
| **Tenant Isolation** | `tenant_id uuid NOT NULL` on every tenant table | Universal `UNIQUE(tenant_id, id)` and composite foreign keys |
| **Units** | Whole integer minutes (`_minutes`) for all time/attendance durations; Decimal days (`days`) for leaves | Precise subtraction without rounding or conversion errors |
| **Effective Dating** | `effective_from` & `effective_to` with PostgreSQL GIST EXCLUDE | Mathematical prevention of overlapping date ranges |
| **Tamper Evidence** | Unchained Merkle checkpoints (`audit_logs`) + Row-chained SHA-256 (`payroll_audit_logs`) | PDPL erasure compliant with verifiable history integrity |
| **Delete Actions** | `RESTRICT` on financial/statutory rows; `CASCADE` on compositional lines | Prevents accidental orphan records or financial erasure |
