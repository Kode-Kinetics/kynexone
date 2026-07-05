# Group → Company Migration Runbook

**Summary.** The Group→Company program ships as three **purely additive** EF Core migrations plus an idempotent startup backfill service. No migration drops, renames, or rebuilds anything; the previous binary runs unchanged against the new schema, so code rollback never requires schema rollback. `CompanyId` columns are deliberately nullable until a gated NOT NULL promotion validated against production null counts. Source runbooks: `backend-dotnet/Zayra.Api/Migrations/PHASE1B_ROLLBACK_NOTES.md` and `PHASE2_CUTOVER_RUNBOOK.md`.

---

## 1. Generate-only policy

Migrations are generated locally and **MUST NOT be executed against production by hand**. Production applies them through the standard deploy path only:

1. Startup `MigrateAsync()` (`Program.cs` boot sequence), or
2. the `--migrate`-and-exit one-off job,

same as every prior migration. Never `dotnet ef database update` against production.

## 2. The three migrations

### 2.1 `20260705053606_Phase1BCompanyScopeFoundation`
(`Migrations/PHASE1B_ROLLBACK_NOTES.md` — verified purely additive: 0 drops, 0 renames, 0 rebuilds.)

- **21 × AddColumn**: nullable `company_id uuid` on the operational tables (attendance_records, attendance_regularization_requests, leave_requests, leave_balance_transactions, employee_loans, salary_advances, employee_bonuses, payroll_deductions, payslips, payroll_slips, shift_assignments, employee_documents, employee_contracts, visa_records, passport_records, work_permit_records, overtime_requests, wps_file_batches, audit_logs, admin_audit_logs) plus `account_type text NOT NULL DEFAULT 'SingleCompany'` on `tenants`.
- **2 × CreateTable**: `company_tax_policies`, `company_compliance_profiles` (`Models/CompanyGovernance.cs`).
- **32 × CreateIndex**: convention `(tenant_id, company_id)` everywhere + hot-path composites — attendance `(tenant_id, company_id, work_date)`, leave_requests / payroll_runs `(tenant_id, company_id, status)`, public_holiday_calendars `(tenant_id, company_id)`, governance tables `(tenant_id, company_id, status, effective_from)`.

### 2.2 `20260705114353_Phase2GrantModesAndInsightCompany`
(`Migrations/PHASE2_CUTOVER_RUNBOOK.md`.)

- `user_entity_accesses.grant_mode` (text, default `SelectedCompanies`) with an **in-migration least-privilege backfill**: rows with `company_id IS NULL` → `AllCurrentAndFutureCompanies` (behavior-preserving — a null-company grant always meant dynamic group access); rows with a company → `SelectedCompanies`.
- `ai_insights.company_id` (nullable) + `(tenant_id, company_id)` index.

### 2.3 Phase 3 final product migration — *name placeholder, fill in on generation*
The final batch (company governance completion, seeder support tables if any, remaining index work) lands as a third additive migration under the same rules: additive-only, generate-only, rollback notes committed alongside. **Update this section with the exact `2026xxxxxxxxxx_<Name>` once generated.**

## 3. Startup flow

Order on every boot:

1. `MigrateAsync()` applies pending migrations.
2. **`CompanyScopeBackfill.RunAsync`** (`Infrastructure/Boot/CompanyScopeBackfill.cs`) — data backfill is deliberately *not* inside the migrations.
3. `CompanyScopeBootAssertion` (and `TenantOwnershipBootAssertion`) validate the model.
4. Seeders (auth, statutory rules, pricing; enterprise test seeder only if `SEED_ENTERPRISE_TEST_DATA=true`).

## 4. `CompanyScopeBackfill` behavior

Idempotent — every step touches only rows still missing a company assignment (`company_id IS NULL`) and tenants without a company, so re-running is a no-op. Non-null values are never modified; no rows are deleted or renamed. Per active tenant, in order:

1. **Ensure default company** — oldest active company; if none exists, create one named after the tenant. No Branch records are created.
2. **Employees**: null `CompanyId` → default company.
3. **Employee-accurate pass** (matters for multi-company tenants): employee-linked operational rows follow their **owning employee's** company (via int `Employee.Id` / `EmployeeIntId` linkage), not the tenant default.
4. **Default sweep**: remaining null operational rows with no resolvable employee link (visa/passport/permit registers, contracts, WPS batches, stragglers) → default company.
5. **AccountType promotion**: tenants already operating >1 active company → `Group`.

**Disable flag:** config `CompanyScope:Backfill` (env `CompanyScope__Backfill=false`). Default is enabled; leave it on — it no-ops once data is clean. The backfill intentionally uses `IgnoreQueryFilters()` (startup has no HttpContext) with an explicit `TenantId` predicate on every statement, so no cross-tenant write is possible (`CompanyScopeBackfill.cs:168-178`).

## 5. Rollback strategy

### Per migration
| Migration | Code rollback | Schema rollback (optional, only if required) | Data lost on schema rollback |
|---|---|---|---|
| Phase1B `…053606` | Revert deploy — previous image ignores the new columns/tables | `dotnet ef database update 20260704220616_AddPlatformLockoutAndMfaAttempts` (drops 2 governance tables, 21 columns, new indexes) | Backfill-written company assignments (recomputable — backfill is deterministic), tax policies / compliance profiles entered after deploy, tenants' `account_type`. **No pre-existing column or row is touched by Up() or Down().** |
| Phase2 `…114353` | Revert deploy | `dotnet ef database update 20260705053606_Phase1BCompanyScopeFoundation` (drops `grant_mode`, `ai_insights.company_id`, one index) | Grant modes (recomputable from `company_id`), AI-insight company stamps. |
| Phase 3 final (TBD) | Revert deploy | `dotnet ef database update 20260705114353_Phase2GrantModesAndInsightCompany` | Fill in with the migration's rollback notes. |

### Backfill-only rollback
Set `CompanyScope__Backfill=false`. Assignments already written are harmless: the columns are nullable and invisible to legacy code paths.

## 6. NOT NULL promotion gate (CompanyId on operational tables) — GATED, not executed

Write-side stamping (`ZayraDbContext.EnforceCompanyScopeOnWritesAsync`) now guarantees new operational rows carry `CompanyId` or fail closed — but a NOT NULL constraint is safe only when **production** null counts are zero, which cannot be verified from a development branch. Run after the deploy has been live through **one full payroll cycle**:

```sql
-- Validation (run against prod read replica); repeat per table:
SELECT 'attendance_records' t, count(*) FROM attendance_records WHERE company_id IS NULL
UNION ALL SELECT 'leave_requests',   count(*) FROM leave_requests   WHERE company_id IS NULL
UNION ALL SELECT 'employee_loans',   count(*) FROM employee_loans   WHERE company_id IS NULL
UNION ALL SELECT 'salary_advances',  count(*) FROM salary_advances  WHERE company_id IS NULL
UNION ALL SELECT 'employee_bonuses', count(*) FROM employee_bonuses WHERE company_id IS NULL
UNION ALL SELECT 'payroll_runs',     count(*) FROM payroll_runs     WHERE company_id IS NULL
UNION ALL SELECT 'payslips',         count(*) FROM payslips         WHERE company_id IS NULL
UNION ALL SELECT 'employees',        count(*) FROM employees        WHERE company_id IS NULL;
```

Decision rules:
- **All zeros for N consecutive days** → generate per-table `AlterColumn` NOT NULL migrations (additive-safety rules still apply: one table per review, rollback note each).
- **Non-zero** → the backfill repairs it on next boot (idempotent), but investigate the writing code path first — by design only seeders/workers (system contexts) can skip stamping.
- **Keep nullable forever** on: audit tables, `LeavePolicy` / holiday calendars / config templates — null is their legitimate tenant-wide semantic (`ICompanyScoped` base tier).

Until promotion, isolation does **not** depend on NOT NULL: the `ICompanyScopedOperational` filter already treats `company_id IS NULL` rows as invisible to scoped users (fail closed) — `Domain/Entities/ICompanyScoped.cs`.
