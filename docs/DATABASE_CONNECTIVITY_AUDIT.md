# DATABASE CONNECTIVITY AUDIT — KynexOne

_Audit date: 2026-06-08 · Scope: live MySQL schema (`zayra` database, 233 tables), introspected directly._
_Spec reference: §5 (Database and Storage Audit)._

## Method

Introspected the **running MySQL 8 schema** (not just the C# models) via
`information_schema` for: table inventory, `tenant_id` coverage, `created_at`/`updated_at`
timestamps, `is_deleted` soft-delete columns, and foreign-key constraints.

## Headline results

| Metric | Value | Assessment |
|--------|-------|------------|
| Total tables | **233** | Large, mature schema |
| Tables with `tenant_id` | **227 / 233 (97%)** | ✅ Excellent — the 6 without are correctly tenant-agnostic |
| Tables with soft delete (`is_deleted`) | **40** | ✅ Core entities covered |
| Tables **without** `created_at`/`created_at_utc` | **64** | ⚠️ Add to critical transactional tables |
| **Foreign-key constraints** | **12** | ⚠️ **Key finding** — integrity is app-level, not DB-enforced |

## 1. Tenant isolation (✅ strong)

**227 of 233 tables carry `tenant_id`.** The only 6 without it are correctly
tenant-agnostic: `tenants` (the tenant table itself), `permissions` (global catalogue),
and the identity join/token tables `role_permissions`, `user_roles`, `refresh_tokens`,
`password_reset_tokens` (scoped via their parent `user`/`role`).

This is now backed at runtime by the **global EF query filter** added this engagement
(see RBAC_TENANT_ISOLATION_AUDIT.md), so the schema-level coverage is actually enforced on
every query.

## 2. Core required tables (spec §5 checklist)

| Spec table | Actual table(s) | Present |
|------------|-----------------|---------|
| tenants, users, roles, permissions, user_roles | same | ✅ |
| employees, departments, job_titles | employees, departments, **designations** (= job titles) | ✅ |
| attendance_records | attendance_records | ✅ |
| leave_types, leave_requests | same (+ leave_policies, employee_leave_balances) | ✅ |
| payroll_runs, payroll_items | payroll_runs, **payroll_slips** (= items) | ✅ |
| recruitment_jobs, candidates | **job_openings** + **manpower_requisitions**, candidates, job_applications | ✅ |
| onboarding_tasks | onboarding_tasks | ✅ |
| performance_reviews | **appraisal_reviews** (+ performance_cycles) | ✅ |
| kpi_definitions, kpi_scores | embedded in appraisal_reviews (KpiScore) + employee_goals + competencies | ⚠️ no standalone KPI tables |
| documents | employee_documents, employee_document_versions, candidate_documents | ✅ |
| approval_requests, notifications, audit_logs, settings | approval_requests, notifications, audit_logs, system_settings | ✅ |
| analytics_events | — (analytics computed live from source tables) | ⚠️ by design, not stored |
| **training_programs, training_assignments** | — | ⛔ **Training module not built** |
| **assets** | — | ⛔ **Assets module not built** |

## 3. Findings

### 3.1 Foreign-key constraints — **only 12 across 233 tables** ⚠️ (highest priority)
DB-level referential integrity exists **only** in the auth/approval area:
```
users.tenant_id→tenants            roles.tenant_id→tenants
user_roles.user_id→users           user_roles.role_id→roles
role_permissions.role_id→roles     role_permissions.permission_id→permissions
employee_user_accounts.user_id→users   user_permission_overrides.user_id→users
refresh_tokens.user_id→users       password_reset_tokens.user_id→users
approval_decisions.approval_request_id→approval_requests
approval_workflow_steps.workflow_id→approval_workflows
```
**Every business module** (employees, leave, payroll, attendance, recruitment, performance,
finance) relates rows by **scalar id columns with no FK constraint** — because the EF
entities use scalar keys (`int EmployeeId`, `Guid RunId`) without navigation properties, so
EF Core never generated FKs.

**Implications:** no DB-level cascade, no DB-level orphan prevention. Integrity depends
entirely on application code (which the controller audit found to be consistent) plus the
soft-delete strategy.

**Recommendation:** for production, add FK constraints (at least on the highest-value
relationships: `*.employee_id→employees.id`, `payroll_slips.run_id→payroll_runs.id`,
`leave_requests.leave_type_id→leave_types.id`, etc.), or formally accept app-level
integrity and add orphan-cleanup jobs. This is the single biggest schema gap vs. spec §5
("foreign keys must be logical and consistent", "no orphan records").

### 3.2 Missing audit timestamps on 64 tables ⚠️
64 tables have neither `created_at` nor `created_at_utc`. Several are **critical
transactional/child tables** that should be timestamped: `attendance_records`,
`payroll_slips`, `payroll_earnings/deductions/allowances`, `leave_balances`,
`approval_decisions`, `employee_documents`, `system_settings`, `security_settings`.
Many others are legitimate line/join tables where it's less critical.

**Recommendation:** add `created_at_utc` (and `updated_at_utc` where mutable) to the
critical transactional tables above for traceability.

### 3.3 Soft delete (✅ adequate)
40 tables expose `is_deleted` — covering the main mutable entities (employees, documents,
loans, advances, bonuses, leave types/policies, etc.). Combined with app-level checks this
mitigates the orphan risk from §3.1.

### 3.4 Schema management: `EnsureCreatedAsync`, not migrations ⚠️
The app builds the schema with `Database.EnsureCreatedAsync()` plus an idempotent
"add column if missing" bootstrapper (which logs benign `ALTER TABLE … ADD COLUMN`
failures for existing columns at startup). EF **migration files exist** in `Migrations/`
but are **not applied** via `Database.Migrate()` at runtime.

**Implications:** no migration history table is authoritative; schema evolution relies on
EnsureCreated (which never alters existing tables) + the manual bootstrapper. Risk of
drift between models and DB on future changes.

**Recommendation (production):** consolidate to a clean migration history and call
`Database.Migrate()` on startup; retire EnsureCreated + the ad-hoc bootstrapper.

## 4. Module → table connectivity (representative)

| Frontend module | API endpoint | Primary tables | Tenant isolation | Status |
|-----------------|--------------|----------------|------------------|--------|
| People | `/api/employees` | employees, employee_documents, employee_history | tenant_id ✅ | working |
| Attendance | `/api/attendance` | attendance_records, attendance_raw_events, attendance_devices | tenant_id ✅ | working (⚠️ no created_at on attendance_records) |
| Leave | `/api/leave/*` | leave_types, leave_policies, employee_leave_balances, leave_requests | tenant_id ✅ | working |
| Payroll | `/api/payroll` | payroll_runs, payroll_slips, payroll_earnings/deductions | tenant_id ✅ | working (⚠️ no FK run_id, no created_at on slips) |
| Recruitment | `/api/recruitment/*` | job_openings, manpower_requisitions, candidates, job_applications | tenant_id ✅ | working |
| Performance | `/api/performance/*` | appraisal_reviews, performance_cycles, employee_goals, competencies | tenant_id ✅ | working |
| Finance | `/api/finance/*` | employee_loans, salary_advances, bonus_batches, employee_bonuses | tenant_id ✅ | working |
| Approvals | `/api/approval-requests` | approval_requests, approval_decisions, approval_workflows | tenant_id ✅ | working (FKs present ✅) |
| Reports | `/api/reports` | saved_reports, report_schedules, report_execution_logs | tenant_id ✅ | working |
| Users/Roles | `/api/access` | users, roles, permissions, user_roles, audit_logs | FK-enforced ✅ | working |

## 5. Summary of recommendations (priority order)

1. **Add FK constraints** to the highest-value business relationships (or formally accept
   app-level integrity + add orphan checks). — _highest impact_
2. **Add `created_at_utc`/`updated_at_utc`** to critical transactional tables
   (`attendance_records`, `payroll_slips`, `leave_balances`, …).
3. **Move to `Database.Migrate()`** with a clean migration history for production.
4. **Decide Training & Assets** — build the modules or formally mark out of scope (today
   they are correctly absent, not stubbed).

## Verification status
- Schema introspected live ✅ · 233 tables · tenant_id 97% · 12 FKs · 40 soft-delete · 64 missing created_at.
- No migration/DDL changes were made by this audit (read-only introspection).
