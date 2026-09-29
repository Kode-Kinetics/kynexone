# BACKEND CONNECTIVITY AUDIT — KynexOne

_Audit date: 2026-06-08 · Scope: all 69 controllers / **571 endpoints** in `backend-dotnet/Zayra.Api/Controllers`._
_Spec reference: §4 (Backend Functionality Audit)._

## Method

1. Enumerated every controller, its `[Route]`, and HTTP-verb endpoint count.
2. Extracted authentication (`[Authorize]` / `[AllowAnonymous]`) and authorization
   (`[Authorize(Roles=…)]` + claim-based `HasPermission`) per controller.
3. Confirmed real DB read/write through EF Core (`ZayraDbContext`) and identified the
   tables each group owns.
4. Cross-referenced with the live smoke test (see FRONTEND_CONNECTIVITY_AUDIT.md) — every
   group responds correctly against the running API.

## Headline results

- ✅ **All 69 controllers enforce `[Authorize]` at class level.** The only anonymous
  endpoints are the five pre-auth flows on `AuthController` (`login`, `refresh`,
  `forgot-password`, `reset-password`, `accept-invitation`) — correctly `[AllowAnonymous]`.
  `logout`, `me`, `change-password` require auth.
- ✅ **Role-based authorization is applied per-method** on sensitive actions across nearly
  every controller (e.g. payroll → `Payroll Manager/Officer/Finance Approver`, bonuses →
  `Finance`, AI → `HR Manager`, admin → `Admin`).
- ✅ **Tenant context enforced** — every business query filters `TenantId`, now backed by a
  **global EF query filter** as defence in depth (see RBAC_TENANT_ISOLATION_AUDIT.md).
- ✅ **Real persistence** — all groups read/write through EF Core to MySQL; no fake/stub
  success responses were found (the frontend audit confirmed forms persist).
- ✅ **Audit logging** present via `AuditService` / module audit logs (auth, employee,
  bonus, recruitment, payroll, admin actions).

## Coverage of required backend API groups (per spec §4)

| Required group | Controller(s) | Route | Endpoints | Auth | Authorization | Tables | Status |
|----------------|---------------|-------|-----------|------|---------------|--------|--------|
| **Auth** | AuthController | `/api/auth` | 8 | mixed | `[AllowAnonymous]` on login/refresh/forgot/reset/accept; rest auth | users, refresh_tokens, password_reset_tokens | ✅ working |
| **Tenants / Companies** | TenantAdminController, CompaniesController | `/api/tenant-admin`, `/api/companies` | 15 | yes | Admin / HR roles | tenant_subscriptions, tenant_feature_flags, tenant_branding, tenant_localization_settings, companies | ✅ working |
| **Employees** | EmployeesController | `/api/employees` | 30 | yes | Admin/HR/Manager/Payroll | employees, employee_documents, employee_history, employee_transfer_requests, employee_drafts | ✅ working |
| **Departments / Org** | DepartmentsController, BranchesController, CostCentersController, GradesController, OrganizationController | `/api/departments`, `/api/branches`, `/api/cost-centers`, `/api/grades`, `/api/organization` | 34 | yes | Admin/Auditor/HR/Manager | departments, branches, cost_centers, grades | ✅ working |
| **Job titles / Positions** | DesignationsController | `/api/designations` | 5 | yes | Admin/Auditor/HR | designations | ✅ working |
| **Attendance** | AttendanceController | `/api/attendance` | 34 | yes | Admin/Auditor/HR | attendance_records, attendance_raw_events, attendance_devices, attendance_policies, geofences | ✅ working |
| **Leave requests** | Leave* (13 controllers) | `/api/leave/*`, `/api/leave-requests` | 70 | yes | Admin/HR/Manager/Payroll/Auditor | leave_types, leave_policies, employee_leave_balances, leave_requests, holiday_calendars, encashment, compoff | ✅ working |
| **Payroll** | PayrollController | `/api/payroll` | 23 | yes | Payroll Manager/Officer/Finance Approver/HR/Admin | payroll_runs, payroll_slips, payroll_earnings/deductions/allowances, payment_batches | ✅ working |
| **Recruitment** | Recruitment* (10 controllers) | `/api/recruitment/*` | 72 | yes | Admin/HR/Recruiter/Manager | recruitment_jobs, candidates, applications, interviews, offers, requisitions, assessments | ✅ working |
| **Onboarding** | Recruitment/OnboardingController | `/api/recruitment/onboarding` | 7 | yes | Admin/HR/Recruiter | onboarding_tasks | ✅ working |
| **Performance / KPI** | Performance* (12 controllers) | `/api/performance/*` | 67 | yes | Admin/HR/Manager (+Employee on goals) | appraisal_reviews, performance_cycles, goals/kpi, competencies, feedback360, calibration, pip, probation | ✅ working |
| **Documents** | (within EmployeesController + Compliance) | `/api/employees/*`, `/api/compliance/*` | — | yes | Admin/HR | employee_documents, employee_document_versions | ⚠️ no standalone module |
| **Assets** | — | — | — | — | — | — | ⛔ not implemented |
| **Approvals** | ApprovalRequestsController, ApprovalWorkflowsController | `/api/approval-requests`, `/api/approval-workflows` | 11 | yes | Admin/HR/Manager/Auditor | approval_requests, approval_workflows, approval_decisions | ✅ working |
| **Reports / Analytics** | Reports/ReportsController, Reports/AnalyticsController | `/api/reports`, `/api/analytics` | 17 | yes | Admin/HR/Finance | saved_reports, report_schedules, report_execution_logs | ✅ working |
| **Users** | AccessController | `/api/access` | 38 | yes | Admin | users, user_roles, employee_user_accounts, security_settings | ✅ working |
| **Roles** | AccessController | `/api/access/roles` | (within 38) | yes | Admin | roles, role_permissions | ✅ working |
| **Permissions** | AccessController | `/api/access/permissions` | (within 38) | yes | Admin | permissions, user_permission_overrides | ✅ working |
| **Audit logs** | AuditLogsController (+ module audit logs) | `/api/audit-logs` | 1+ | yes | Admin | audit_logs, *_audit_logs | ✅ working |
| **Settings** | Admin/SetupSettingsController, Admin/MasterDataController, LocalizationController | `/api/admin/*`, `/api/localization` | 26 | yes | Admin/HR | system_settings, gcc_compliance_settings, master data, fiscal_years, locations | ✅ working |
| **Finance (loans/advances/bonuses)** | Finance/* (3 controllers) | `/api/finance/*` | 27 | yes | Admin/Finance/HR/Manager | employee_loans, salary_advances, bonus_batches, employee_bonuses | ✅ working |
| **Compliance** | Compliance/* (3 controllers) | `/api/compliance/*` | 25 | yes | Admin/HR | employee_compliance_records, contracts, visa_tracking | ✅ working |
| **Shifts** | ShiftsController | `/api/shifts` | 7 | yes | Admin/HR | shift_definitions, shift_rosters | ✅ working |
| **Overtime** | OvertimeController | `/api/overtime` | 15 | yes | Admin/HR/Manager/Payroll | overtime_policies, overtime_requests, overtime_compoff_conversions | ✅ working |
| **AI Assistant** | AIAssistantController, Leave/LeaveAIInsightsController | `/api/ai`, `/api/leave/ai-insights` | 11 | yes | Admin/HR/Payroll | ai_insights, ai_hr_query_logs, employee_risk_scores, payroll_ai_validation | ✅ working |
| **HR Request Center** | HRRequestCenterController | `/api/hr-requests` | 10 | yes | Admin/HR | hr_requests, hr_request_comments | ✅ working |
| **Employee Self-Service** | EmployeeSelfServiceController | `/api/ess` | 20 | yes | claim-based `ess.read`/`ess.write` + employee link | employees, leave_balances, attendance, employee_documents | ✅ working* |
| **Mobile** | MobileController | `/api/mobile` | 8 | yes | class-level auth | (lightweight reads over existing tables) | ✅ working |
| **Notifications** | NotificationsController | `/api/notifications` | 2 | yes | class-level auth | notifications | ✅ working |

\* ESS additionally requires the authenticated user to be linked to an employee record;
it returns a clear `400` guard message otherwise (verified — not a defect).

## Authorization model (observed)

- **Authentication:** JWT bearer; `[Authorize]` on every controller; `[AllowAnonymous]`
  only on the 5 pre-auth `AuthController` endpoints.
- **Authorization:** predominantly **role-based** (`[Authorize(Roles=…)]`) at method level.
  Roles in use: Admin, HR Manager, HR Officer, Payroll Manager, Payroll Officer, Finance,
  Finance Approver, Manager, Recruiter, Auditor, Employee.
- **Permission-claim checks:** `EmployeeSelfServiceController` reads `permission` claims
  directly (`ess.read`/`ess.write`). The frontend uses the same permission keys for route
  guards.

## Findings / inconsistencies (non-blocking)

| # | Finding | Severity | Recommendation |
|---|---------|----------|----------------|
| 1 | **No Assets module** (spec lists it as optional "if present"). | info | Implement only if assets management is in scope; otherwise it's correctly absent (not stubbed). |
| 2 | **No standalone Documents controller/module** — document CRUD lives inside Employees/Compliance; the `/documents` nav item was (correctly) removed as it had no backend. | low | Build a dedicated Documents module + page if document management is a first-class requirement. |
| 3 | **Mixed authorization styles** — most controllers use role-name checks; ESS uses permission-claim checks; the frontend uses permission keys throughout. | low | Standardise on permission-policy authorization for consistency and finer-grained control. |
| 4 | A few controllers are **class-level `[Authorize]` only** (Dashboard, Notifications, LeaveCalendar, LeaveDelegation, Mobile, Performance/Analytics, Performance/Feedback, Localization) — any authenticated user can call them. | low | Acceptable for read-only/self-service endpoints; add role/permission checks if any expose sensitive cross-employee data. |
| 5 | `EnsureCreatedAsync()` is used instead of applying EF **migrations** at runtime; the idempotent schema bootstrapper logs benign `ALTER TABLE … ADD COLUMN` failures for existing columns. | info | For production, switch to `Database.Migrate()` with a clean migration history (see DATABASE audit — pending). |

## Build / verification status
- Backend: `dotnet build` ✅ 0 errors.
- Live: all required API groups reachable and responding correctly (authenticated smoke test).
