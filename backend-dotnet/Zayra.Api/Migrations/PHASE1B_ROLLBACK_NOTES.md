# Phase 1B Migration — `20260705053606_Phase1BCompanyScopeFoundation`

**Generate-only policy:** this migration was generated locally and MUST NOT be executed
against production by hand. Production applies it through the standard deploy path
(`MigrateAsync` on boot / `--migrate` one-off job), same as every prior migration.

## What Up() does (verified purely additive — 0 drops, 0 renames, 0 rebuilds)

- **21 × AddColumn** — nullable `company_id uuid` on operational tables
  (attendance_records, attendance_regularization_requests, leave_requests,
  leave_balance_transactions, employee_loans, salary_advances, employee_bonuses,
  payroll_deductions, payslips, payroll_slips, shift_assignments, employee_documents,
  employee_contracts, visa_records, passport_records, work_permit_records,
  overtime_requests, wps_file_batches, audit_logs, admin_audit_logs) and
  `account_type text NOT NULL DEFAULT 'SingleCompany'` on tenants.
- **2 × CreateTable** — `company_tax_policies`, `company_compliance_profiles`.
- **32 × CreateIndex** — convention `(tenant_id, company_id)` on every company-scoped
  table plus hot-path composites: attendance `(tenant_id, company_id, work_date)`,
  leave_requests `(tenant_id, company_id, status)`, payroll_runs
  `(tenant_id, company_id, status)`, public_holiday_calendars `(tenant_id, company_id)`, and governance-table resolution indexes
  `(tenant_id, company_id, status, effective_from)`.

## Backfill

Data backfill is deliberately **not** in the migration. It runs as the idempotent
`CompanyScopeBackfill` service on startup (after `MigrateAsync`), guarded by config
`CompanyScope:Backfill` (`CompanyScope__Backfill=false` to disable). It only ever
touches rows whose `company_id IS NULL` and tenants without a company, so re-running
is a no-op. Order per tenant: ensure default company → employees → employee-accurate
pass per company → default sweep → AccountType promotion (>1 active company ⇒ Group).

## Why CompanyId stays nullable (for now)

`NOT NULL` promotion is deferred to Phase 2 **by design**: write-side CompanyId
stamping does not exist yet, so a NOT NULL constraint would fail inserts from any code
path that doesn't set the column. Isolation does not depend on it — the
`ICompanyScopedOperational` query filter already treats `company_id IS NULL` rows as
invisible to scoped users (fail closed). Promote to NOT NULL per-table in Phase 2 once
stamping ships and `SELECT count(*) WHERE company_id IS NULL` stays 0 across a release.

## Rollback

1. **Code:** revert the deploy (previous image ignores the new columns/tables — all
   changes are additive, so the old binary runs unchanged against the new schema).
2. **Schema (optional, only if required):** `dotnet ef database update <previous>`
   (previous = `20260704220616_AddPlatformLockoutAndMfaAttempts`) executes Down():
   drops the two governance tables, the 21 columns, and the new indexes.
   **Data loss on schema rollback is limited to**: company assignments written by the
   backfill (recomputable — the backfill is deterministic), any tax policies /
   compliance profiles entered after deploy, and tenants' `account_type` values.
   No pre-existing column or row is touched by Up() or Down().
3. **Backfill-only rollback:** set `CompanyScope__Backfill=false` — assignments already
   written are harmless (nullable column, invisible to legacy code paths).
