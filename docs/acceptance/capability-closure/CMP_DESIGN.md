# CMP — Merit planning and pay benchmarking: design

Branch `feat/merit-planning` (base `76985c55`). Migration `AddMeritPlanning`. Opt-in feature `compensation` (off unless the platform team enables it).
Binding rules: REGISTER.md §3.3. Evidence: `evidence/cmp/EVIDENCE.md`.

## 1. Payroll integration decision (the money path)

> **Owner-overridable decision (CTO call, approved by the Integration Owner 2026-10-11): award authority.** The two
> approvals already given — each worksheet in Approvals, then the whole cycle by a second person — are treated as the
> salary-change approval. Applied awards are therefore created `ApprovedPendingEffectiveDate` with
> `ApprovalRequestId = null` and are **not** re-routed through the employee-change workflow one by one. If the owner
> wants a per-award approval instead, the change is local to `MeritPlanningService.ApplyAsync`: create the change as
> `PendingApproval` and call `CreateRequestAsync` for `EmployeeChangeRequest`; nothing else in CMP depends on it.

**Decision.** An approved award never writes `employee_salary_structures`. At apply, each award becomes **one**
`EmployeeChangeRequest` with a `salaryBreakdown` patch:

- **Status:** `ApprovedPendingEffectiveDate`.
- **Approver:** `ApprovedByUserId` = the cycle approver; `ApprovedAtUtc` = the apply time.
- **`ApprovalRequestId` = null, deliberately.** It would otherwise point at a `CompensationWorksheet` approval request. A
  re-review raised by the effective-date job must then route on the `EmployeeChangeRequest` workflow; pointing it at the
  worksheet workflow makes `CreateRequestAsync` throw, and the job item retries for ever.
- **Baseline:** an `EmployeeHistory` row of type `SensitiveChangeScheduled` carries the sealed baseline
  (`EmployeeChangeBaseline.Capture/Protect`). It is the same "what the approver saw" record the Approval Center writes
  for a future-dated change.

The existing `EffectiveChangeJobHandler` applies the change on the effective date through
`EmployeeChangeApplier.ApplyApprovedChangeAsync` → `EmployeeSalaryBreakdownChanges.ApplyAsync` →
`EmployeeSalaryBreakdownWriter`. Payroll reads the latest active row dated on or before the period end
(`PayrollController.cs:1603,2213`), so the award is paid from its effective month.

**Why this path.**

- It reuses the only governed salary writer, along with its row locks, once-only marker, drift check (the job returns the
  change for review if salary moved since apply), audit and notifications.
- The alternative — routing each award through a second approval as a `PendingApproval` change — would ask someone to
  re-approve 300 awards one by one. That second approval adds no control the worksheet approval and the cycle approval
  have not already exercised.

**Once-only, proven by tests.**

1. **Apply cannot double-create.** Apply takes the cycle row `FOR UPDATE` in an execution-strategy transaction and skips
   any line that already has a change. The database also allows only one award per (tenant, cycle, employee): the
   partial unique index `ux_comp_worksheet_lines__one_award`.
   - `Apply_IsIdempotent_…` runs apply three times and gets 10 changes, then 11 once the overlap is resolved — never more.
   - `OneAppliedAwardPerCycleAndEmployee_IsADatabaseRule` proves the database rule directly.
2. **The job applies a change exactly once.** It locks the change row and re-checks the status. In the test, the day
   before the effective date gives `AlreadyHandled`, the effective date gives `Applied`, and a re-run gives
   `AlreadyHandled`.
3. **Payroll does not double-pay.**
   - The effective-month run pays basic 6,360.00.
   - The next month pays 6,360.00 again, with arrears = 0 for the employee.
   - Cancelling before the effective date leaves payroll on the old basic.
   - A reversal is a new dated row; nothing is deleted.

**Blocks at apply.** A line is refused, with the reason recorded on the line, when any of the following holds. The award
is never re-based on new pay.

- Pay of record changed since approval (a different salary row, basic, gross or currency).
- Another salary change is pending.
- A later-dated salary row exists.
- The pay is bound to a contract (`RenewalCaseId`, a non-Amount allowance basis, or Qiwa-confirmed). These rows stay
  **blocked**. The writer's message now points to the governed salary-change flow (a dated change approved in Approvals)
  or contract renewal, not to the Compensation page. **Owner decision pending:** whether CMP should recompute
  percent-of-basic allowances on the new basic instead of blocking.
- Eligibility is re-evaluated at apply with the cycle's own rules: an employee now on notice, offboarding, terminated or
  otherwise ineligible is refused, as is one whose grade or department changed since the award was approved.
- `EmployeeSalaryBreakdownChanges.ValidateAsync` refuses the package (grade range, salary-structure rules).

Apply is re-runnable: a line is picked up again once its block is resolved.

**On the effective date (leavers after apply).** `EffectiveChangeJobHandler` asks every registered
`IEffectiveChangeGate` before it writes anything, inside the change's row lock. The job knows nothing about CMP; CMP
registers `MeritEffectiveChangeGate` (`AddCompensation`). For a change raised by a merit line, the gate re-runs the
cycle's eligibility rule (`MeritBasisLoader.Eligibility`, the same function used at planning and apply) and checks grade
and department against the line. If the employee went on notice, started offboarding, left the eligible statuses, or
moved grade or department after apply, the change is **Cancelled** with the reason (history row, audit
`employee.change_effective_cancelled`), the line records it, and HR (who applied the cycle) is notified. Separated
statuses keep the job's existing return-for-review path.

**Withdrawing one award.** HR (`compensation.manage`) can withdraw a single award that has not taken effect, with a
reason (`POST awards/{lineId}/withdraw`). The change is cancelled under the same row lock the job takes, any re-review is
closed, the line records it, and `compensation.award.withdrawn` is audited. The rest of the cycle stays scheduled. An
award in pay is reversed instead.

## 2. Tables (8 — one over the CMP cap of 7; exception APPROVED by the Integration Owner 2026-10-11)

Every table is `ITenantOwned` + `ICompanyScopedOperational` (company required on the cycle by `ck_comp_cycles__company_required`). Every FK states ON DELETE. Audit: `IAuditService` after commit unless noted "staged".

**Exception.** The proposed list had no worksheet header. The approval engine needs one subject per submission, carrying:

- status and revision;
- the approval request it raised;
- the manager it is about;
- the overspend justification;
- the scenario name and whether it is the primary scenario.

Carrying those on every line would duplicate them per employee. **Approved.**

### `comp_cycles` — planning round
- **Lifecycle:** its own state machine (§4). `PerformanceCycle` rates people and `IncrementRecommendation` is ad hoc, so
  neither holds a budgeted, currency-fixed, dated round.
- **Keys:**
  - `company_id` → companies, RESTRICT.
- **CHECKs:**
  - `status`
  - `period_order`
  - `currency` (3 upper)
  - `pay_component_basic`
  - `proration_rule`
  - `min_days`
  - `company_required`
  - `effective_first_of_month` (raw SQL, PostgreSQL `EXTRACT`)
- **Index:** `(tenant_id, company_id, effective_date)` → cycle list.
- **Concurrency:** `version` token.
- **Retention:** kept while the salary rows its awards produced exist (employment + statutory minimum). No delete path.
- **Audit:**
  - `compensation.cycle.created|updated|planning_opened|review_started|reopened|approved|applied|cancelled`
  - `compensation.workflow.installed` (staged)

### `comp_cycle_budgets` — department pool
- **Keys:**
  - `cycle_id` → CASCADE.
  - `department_id` → RESTRICT.
- **Unique:** `(cycle_id, department_id)`.
- **CHECK:** `pool_type`; `pool_value` pairs the type with exactly one of percent (0–100) / fixed (≥ 0).
- **Index:** `department_id` (restrict probe).
- **Concurrency:** `version`.
- **Audit:** `compensation.budgets.saved`.

### `comp_guidelines` — rating × compa band → min/target/max %
- **Keys:**
  - `cycle_id` → CASCADE.
- **Unique:** `(cycle_id, rating, compa_ratio_min)`.
- **CHECKs:** `percent_order`, `percent_range`, `band`, `rating_present`. The service also refuses overlapping bands.
- **Lifecycle:** editable in Draft only.
- **Audit:** `compensation.guidelines.saved`.

### `comp_fx_rates` — the product's first FX table
- **Keys:**
  - `cycle_id` → CASCADE.
- **Unique:** `(cycle_id, from_currency)`.
- **CHECKs:** `rate_positive`, `currency`, `source_present`.
- **Columns:** rate (18,8), source, rate date, entered by (id + name), entered at.
- **Lifecycle:** editable in Draft and Planning.
- **Audit:** `compensation.fx.saved`.

### `comp_worksheets` — manager × scenario (the approval subject)
- **Keys:**
  - `cycle_id` → CASCADE.
  - `manager_employee_id` → employees, RESTRICT.
- **Uniques:**
  - `(cycle_id, manager_employee_id, scenario_name)`
  - `ux_comp_worksheets__one_primary` `(cycle_id, manager_employee_id) WHERE is_primary`
- **CHECKs:** `status`, `revision >= 1`, `submitted_is_primary`, `submitted_has_approval`.
- **Index:** `approval_request_id` (the hook's lookup).
- **Concurrency:** `version`.
- **Audit:**
  - `compensation.worksheet.saved|submitted` (with a line-snapshot SHA-256)
  - `scenario_created|primary_changed`
  - `returned|approved` (staged in the decision's SaveChanges)

### `comp_worksheet_lines` — the award and its basis
- **Keys:**
  - `worksheet_id` → CASCADE.
  - `cycle_id` → CASCADE.
  - `employee_id` → RESTRICT.
  - `change_request_id` / `reversal_change_request_id` → employee_change_requests, RESTRICT.
- **Uniques:**
  - `(worksheet_id, employee_id)`
  - **`ux_comp_worksheet_lines__one_award` `(tenant_id, cycle_id, employee_id) WHERE change_request_id IS NOT NULL`**
- **CHECKs:**
  - `money_non_negative`
  - `proration_factor` (0–1)
  - `fx_rate_positive`
  - `out_of_guideline_justified` (no applied award without a justification)
  - `pay_currency`
- **Indexes:**
  - `(cycle_id, department_id)` → roll-ups and equity.
  - `employee_id`
  - the change-request ids
- **Audit:** `compensation.award.scheduled|apply_blocked|withdrawn|reversal_requested`.

### `pay_benchmark_sets` — customer-owned import with provenance
- **Keys:**
  - `company_id` → RESTRICT.
- **Unique:** `(tenant_id, company_id, source_file_sha256)` — the same file is not imported twice.
- **CHECKs:**
  - `provenance` (provider, licence, ISO-2 geography, currency)
  - `pay_basis` (MonthlyBasic / MonthlyGross)
  - `row_count > 0`
  - `file_hash`
- **Index:** `(tenant_id, company_id, is_active, survey_date)`.
- **Retention:** until HR retires the set. Retired sets are kept.
- **Audit:** `compensation.benchmark.imported|mapped|retired`.

### `pay_benchmark_rows` — percentiles + mapping
- **Keys:**
  - `set_id` → CASCADE.
  - `designation_id` / `grade_id` → SET NULL.
- **Unique:** `(set_id, job_family, level)`.
- **CHECKs:**
  - `percentiles_monotonic` (`0 < P25 ≤ P50 ≤ P75 ≤ P90`)
  - `sample_size > 0`
  - `keys_present`
- **Indexes:** `(tenant_id, designation_id)` and `(tenant_id, grade_id)`, partial — the worksheet lookup.

### Change to an existing table: `employee_salary_structures`
- **Constraint:** the deferred exclusion `ex_employee_salary_structures__one_active_per_date`:
  `EXCLUDE USING btree (tenant_id WITH =, employee_id WITH =, effective_date WITH =) WHERE (is_active) DEFERRABLE INITIALLY DEFERRED`.
- **Semantics:** the same rule as the requested unique partial index, checked at COMMIT.
- **Why deferred:** the single writer deactivates the replaced row and inserts the new one in one SaveChanges. A
  non-deferrable index would fail whenever EF ordered the INSERT first.
- **Proof:**
  - `TwoActiveSalaryRowsForOneDate_…` refuses a duplicate both sequentially and under a parallel race (exactly one
    commits).
  - `TheSingleWriter_ReplacingADate_StillWorks…`
  - The full suite (6,914 tests) passed with the constraint in the fixture.

### Migration
- `20261012042728_AddMeritPlanning` (regenerated after main's AddLearning and AddSuccession; `comp_worksheet_lines` gained `proposed_as_percent` and
  `assigned_by_user_id`, `comp_worksheets` `revision_editor_ids`, `comp_cycles` `retro_acknowledged_periods`;
  `has-pending-model-changes` reports none).
- **Expand-only:** 8 new tables, one added constraint, one added CHECK.
- **Raw SQL:** declared as constants in the migration class (`DestructiveMigrationGuardTests` reads them). The same
  constants run in `PostgresFixture`.
- **Rollback:** `Down` drops the constraint and the tables. No data elsewhere is touched.
- **Forward recovery:** switch the `compensation` flag off. Scheduled awards are ordinary change requests and still apply
  — cancel the cycle first if they must not.

## 3. API contract (`/api/compensation/merit`, all `[HasPermission]`, opt-in flag `compensation`)

Write routes may also return 422 `{ error, message, blockers }`, 409 conflict, 403 `{ error, message }`, or 404.

| Route | Permission | Notes |
|---|---|---|
| `GET cycles` | any compensation key | planners see only the cycles they have a worksheet in |
| `POST cycles` | `compensation.manage` | currency = company DefaultCurrency |
| `GET/PUT cycles/{id}` | read/approve/manage · manage | PUT allowed in Draft only |
| `PUT cycles/{id}/budgets` · `/guidelines` · `/fx` | manage | budgets/FX in Draft or Planning, and **locked** (`planning_locked`) once any worksheet is Submitted or Approved; guidelines in Draft only |
| `POST cycles/{id}/open-planning` · `start-review` · `reopen` · `cancel` | manage | `{ version, reason? }`; `reopen` works from Planning or Review and returns every Submitted/Approved worksheet (audited per worksheet) |
| `POST cycles/{id}/approve` | `compensation.approve` | reviewer ≠ approver; an approver whose own pay is in the cycle is refused; `acknowledgeRetroCost` required when the effective date falls in paid payroll months |
| `POST cycles/{id}/worksheets` `{ managerEmployeeId }` | manage | HR opens (or re-opens) a worksheet for any covered manager |
| `POST worksheets/{id}/assign` `{ employeeId }` | manage | HR assigns an uncovered employee to an editable primary worksheet; never to the employee or anyone they manage |
| `POST worksheets/{id}/close` `{ reason }` | manage | Draft/Returned only; its people show as uncovered |
| `POST cycles/{id}/apply` `{ acknowledgeRetroCost? }` | manage | idempotent; if a payroll month was run between approval and apply, refused (`retro_acknowledgement_required`) until someone with `compensation.approve` acknowledges the arrears (stored on the cycle, audited) |
| `POST awards/{lineId}/withdraw` `{ reason }` | manage | one award, before it takes effect |
| `GET cycles/{id}/equity` | `compensation.equity.read` | audited read |
| `POST cycles/{id}/scenarios` | plan/manage | at most 5 per manager |
| `GET my-worksheets` · `GET worksheets/{id}` | plan (own) / read | someone else's worksheet answers 404 |
| `PUT worksheets/{id}` · `POST …/submit` · `POST …/make-primary` | plan (own) / manage | `{ version, … }`; optimistic concurrency; a percent with more than 4 decimals is 400 `percent_precision` |
| `POST awards/{lineId}/reverse` | manage | correcting change on the employee-change approval route; into Approved/Locked/Paid payroll months needs `acknowledgeLockedPeriods` from someone with `compensation.approve` (audited) |
| `GET benchmarks` · `GET benchmarks/{id}` · `GET benchmarks/template.csv` | read or benchmarks.manage · benchmarks.manage for the template | a manager's worksheet shows the market *position* of their own lines only, never P50 or the set |
| `POST benchmarks/import` (multipart) · `PUT benchmarks/rows/{id}/mapping` · `POST benchmarks/{id}/retire` | `compensation.benchmarks.manage` | the import is all-or-nothing, with issues by row and field |

**Approval engine.**

- The entity `CompensationWorksheet` is registered in `ApprovalEntities`.
- `CompensationApprovalSync` adds three calls in `ApprovalWorkflowService.DecideCoreAsync`:
  - **Validate:**
    - no employee on the worksheet decides it;
    - nobody who changed this revision (saved, submitted, assigned an employee, made it primary — kept in
      `revision_editor_ids`, cleared when it is returned) decides it;
    - the decider needs `compensation.approve`;
    - a return needs a comment;
    - the cycle must be in Planning.
  - **Apply on reject/final:** Returned with revision +1, or Approved.
- The default workflow `COMP-WORKSHEET-DEFAULT` (HR Manager → Finance Approver) is installed insert-if-absent when
  planning opens.

**Permissions.**

| Role | Compensation permissions |
|---|---|
| HR Director | all `compensation.*` |
| HR Manager | read, manage, approve, benchmarks.manage, equity.read |
| Finance | read, approve |
| Finance Approver | read, approve |
| Manager | plan |

`compensation.plan` is non-privileged for MFA (own reporting line); every other compensation key is privileged.

## 4. State machines

- **Cycle:**
  - Draft → Planning: guards are ≥ 1 eligible employee, a guideline matrix, ≥ 1 budget, and an FX rate for every pay
    currency. Opening creates one primary "Main" worksheet per manager with eligible direct reports.
  - Planning → Review: every primary worksheet must be Approved.
  - Review → Planning, or Planning → Planning: reopen, with a reason. Submitted/Approved worksheets return to the
    manager (pending approvals close, revision +1, comment = the reason). This is the only way to change budgets or FX
    after a submission.
  - Send to review and approve both re-validate the cycle: each line's FX rate must still be the cycle's rate, and a
    worksheet that overspends must carry a justification (`cycle_inconsistent`).
  - Review → Approved: `compensation.approve`, by someone other than the reviewer and whose own pay is not on the cycle.
  - Approved → Applied: apply, which can be re-run while Applied.
  - Draft, Planning, Review, Approved → Cancelled: closes pending worksheet approvals.
  - Applied → Cancelled: only when no award has taken effect. It withdraws pending and returned-for-review changes under
    row lock; otherwise the cycle is refused and each award is reversed instead.
- **Worksheet:**
  - Draft or Returned → Submitted (primary only).
  - Submitted → Returned (approver rejects with a comment; revision +1).
  - Submitted → Approved (final step). The worksheet is then locked.
  - Draft or Returned → Cancelled (cycle cancelled, or HR closes it).
  - Submitted → Returned when its approval is closed without a decision — for example when the manager is
    soft-deleted or merged (`compensation.worksheet.approval_closed`).
  - Lines are recomputed from live pay of record while the worksheet is editable, and frozen once it is submitted.
- **Award (`EmployeeChangeRequest`, existing machine):**
  - ApprovedPendingEffectiveDate → ApprovedApplied (job) | PendingApproval (job drift → re-review) | Cancelled (cycle
    cancel, HR withdraws the award, or the merit gate finds the employee no longer eligible on the effective date).
  - A reversal is a new change: PendingApproval → Approved… → ApprovedApplied.

## 5. Formulas (decimal only; money half-away-from-zero 2 dp at line level; ratios 4 dp)

**Definitions.**

- **Gross** = basic + housing + transport + food + mobile + other. Fixed deduction is excluded.
- **Rate** = units of cycle currency per 1 unit of pay currency.
  - X_cycle = round(X_pay × rate, 2).
  - pay = round(cycle ÷ rate, 2).
- **Compa-ratio** = gross ÷ grade mid.
  - Computed in pay currency when the grade is in the pay currency.
  - Otherwise both sides are normalised to the cycle currency.
- **Range position** = (gross − min) ÷ (max − min).
- **Proration factor** = round(eligible days ÷ period days, 4). Eligible days = from max(period start, joining date) to
  the period end, inclusive.
- **Award:**
  - increase_cycle = the amount entered, or round(basic_cycle × % ÷ 100, 2).
  - proposed % = round(increase ÷ basic_cycle × 100, 4).
  - prorated = round(increase × factor, 2).
  - prorated_pay = round(prorated ÷ rate, 2).
  - new basic = basic + prorated_pay.
- **Pool:**
  - Percent pool = round(Σ eligible basic_cycle × % ÷ 100, 2).
  - Used = Σ prorated (primary worksheets).
  - % used = round(used ÷ pool × 100, 2).
  - From Review onward, eligible basic is read from the line snapshots (frozen).
- **Out of guideline:** an award outside the cell's [min, max], or no cell for an award, or a zero award where the
  minimum is > 0. A written justification is required.
- **Blocking at planning:** the new gross falls outside the grade's raw min/max (the writer refuses it), a missing FX
  rate, no salary row, contract-bound pay, or a later-dated salary row.

**Worked examples** (golden test, hand-computed; grade G1 8,000 / 10,000 / 12,000 SAR; period 365 days):

**A1** — basic 6,000, gross 8,000, Exceeds, 6 %.

- compa = 8,000 ÷ 10,000 = 0.8000
- range position = 0 ÷ 4,000 = 0.0000
- increase = 6,000 × 6 % = 360.00
- new basic 6,360.00; new compa 0.8360

**A5** — joined 1 May 2026, Unrated, 2 %.

- factor = 184 ÷ 365 = 0.50411 → 0.5041
- increase = 120.00
- prorated = 120 × 0.5041 = 60.492 → 60.49
- new basic 6,060.49

**A6** — paid in AED: basic 6,000, gross 8,000, rate 1.02.

- basic_cycle = 6,120.00; gross_cycle = 8,160.00
- compa = 8,160 ÷ 10,000 = 0.8160
- 3 % gives increase_cycle = 183.60
- pay amount = 183.60 ÷ 1.02 = 180.00 AED
- new basic 6,180.00 AED
- new compa = (8,180 × 1.02 = 8,343.60) ÷ 10,000 = 0.8344

**Engineering pool** — 4 % of (6,000 + 7,000 + 8,000 + 6,500 + 6,000 + 6,120 = 39,620.00).

- pool = 1,584.80
- used = 1,604.09
- remaining −19.29, 101.22 % → overspend; a justification is required

**Operations** — fixed pool 1,500.00.

- used = 1,319.50 (87.97 %)

**Totals.**

- pool = 3,084.80
- proposed = 2,923.59
- 94.77 %
- 11 awards

## 6. Pay equity and benchmarks

**Pay equity.**

- **Cohort:** company × grade × designation.
  - Active employees with a salary row and a grade mid.
- **Groups:**
  - gender: Male / Female / Not stated;
  - optionally National / Expatriate (KSA uses `SaudiNationality.IsSaudi`).
- **Per group:** count, mean and median compa-ratio, mean gross in cycle currency.
- **Projected** uses the primary worksheets' new compa-ratio.
- **Gap** = (mean men − mean women) ÷ mean men × 100, 2 dp.
- **Suppression:** k = 5 (local guard `CompCohortGuard`; unify with the ENG cohort-suppression service when it lands).
  - A cohort under k is hidden.
  - A group under k is hidden, and then the cohort total is shown only as "5 or more" (no difference attack).
  - The gap is shown only when both groups are visible.
- **Access:** reads are audited (`compensation.equity.viewed`). The UI states the formulas and an authorised-use notice.

**Benchmarks.**

- **Import:** CSV or XLSX, with the columns `job_family, level, sample_size, p25, p50, p75, p90` plus optional
  `currency` and `geography` cross-checks.
- **Required provenance:** provider, licence statement, survey date, ISO-2 geography, currency, pay basis.
- **Synthetic marker:** a `#` line containing `SYNTHETIC — NOT MARKET DATA` forces `is_synthetic`.
- **Mapping:**
  - The designation mapping is used first, then the grade mapping.
  - Mappings carry forward on the next import of the same family and level.
  - Among matches, in-scope sets win, then the newest survey.
- **States:**
  - `missing` — no mapped row.
  - `out_of_scope` — the geography differs from the company's country, or no FX rate exists for the set or pay currency.
    No figure is shown.
  - `stale` — older than 18 months at the effective date. Shown, and labelled.
  - `current`
- **Position** = basic or gross (per the pay basis) in cycle currency ÷ P50 in cycle currency.
- No market figures ship in code. The only fixture is `frontend/e2e/fixtures/compensation/synthetic-benchmarks.csv`
  (labelled).

## 6a. Coverage, guideline boundary and input limits (fix round)

- **Coverage.** A worksheet covers an employee by HR assignment if one exists, otherwise by `ManagerEmployeeId`.
  Uncovered lists: no manager, own manager, manager not active, and "manager has no worksheet in this cycle" (someone
  reassigned after planning opened). Each row says whether HR can open a worksheet for that owner.
- **Guideline boundary.** A percent is tested as entered (`proposed_as_percent`), so 3.00 % is inside a 3–5 % band
  whatever the cents of the basic. An amount is tested in money with a half-cent tolerance.
- **Retro cost.** A past effective date shows months in the past, the paid payroll periods
  (Processed…Paid), the monthly delta and the arrears (months × delta) on the cycle; approval needs an acknowledgement.
  The acknowledged periods are stored (`retro_acknowledged_periods`); a month paid after approval needs a fresh
  acknowledgement at apply.
- **XLSX benchmark import.** `MaxCharactersInPart` 2 M, at most 16 columns (a cell beyond P — e.g. `XFD1` — is
  refused by reference), rows streamed with `OpenXmlReader` and stopped after the row cap.
- **Payroll salary import** (`PayrollController.ImportEmployeeSalaries`). Duplicate (employee, date) rows in the file
  are refused in memory with "Row N: duplicates row M"; the write is one transaction; an overlap with a concurrent
  writer (23P01 on the salary-row constraint) returns 422 `salary_row_conflict` with row-level messages and nothing
  saved. There is one write path (no in-memory-provider branch); its validation tests run on PostgreSQL
  (`PayrollSalaryImportPostgresTests`).

## 7. Operator runbook

1. **Before deploying (read-only).** Run on the production database:
   ```sql
   SELECT tenant_id, employee_id, effective_date, count(*) AS active_rows, array_agg(id ORDER BY created_at_utc) AS row_ids
   FROM employee_salary_structures WHERE is_active
   GROUP BY tenant_id, employee_id, effective_date HAVING count(*) > 1
   ORDER BY tenant_id, employee_id, effective_date;
   ```
   - **Zero rows:** safe to deploy.
   - **Any rows:**
     - The migration will fail on purpose with `CMP pre-flight: N … group(s) …` and change nothing.
     - HR decides which row is pay of record.
     - Deactivate the others through a reviewed change.
     - Re-run the query, then deploy.
2. **Deploy.** Migrate as usual (approve the CI run). Check `/health/ready` reports `pendingMigrations: 0`.
3. **Enable for a tenant.** Run `PUT /api/platform/tenants/{id}/features/compensation { isEnabled: true }` as the platform
   owner. **Existing tenants' roles do not receive the keys on deploy.** The boot seeder (`AuthSeeder.SeedAsync`) backfills
   only the Admin role. `EnsureTenantRolesAsync`, which carries the role table in §3, runs only from PlatformController
   when a tenant is created (`POST /api/platform/tenants`, `POST /api/platform/leads/{id}/convert`) or when a requested
   role does not exist yet (`POST /api/platform/tenants/{id}/admins`, `POST /api/platform/tenants/{id}/users`). No
   platform endpoint re-applies the role table to a tenant whose roles already exist. So, as part of the rollout for
   each existing tenant, the tenant's security administrator (`security.manage`) grants the keys in **User Management →
   Roles**: HR Director all six, HR Manager read/manage/approve/benchmarks.manage/equity.read, Finance and Finance
   Approver read/approve, Manager plan. The screen calls `PUT /api/access/roles/{roleId}/permissions`, which REPLACES the
   role's whole permission set, so send the existing keys plus the new ones. Tenants created after the deploy get the
   keys automatically.
4. **Running a cycle:**
   1. Create the cycle.
   2. Set the guideline matrix, budgets and FX.
   3. Open planning. This installs `COMP-WORKSHEET-DEFAULT` if the tenant has no worksheet workflow.
   4. Managers submit their worksheets.
   5. Worksheets are approved in Approvals.
   6. Send the cycle to review.
   7. A different person approves the cycle.
   8. Apply.
   9. Awards take effect through the hourly `employee.effective-changes` job. Lines it could not schedule are listed with
      the reason; resolve them and apply again.
5. **Undo:**
   - **Before the effective date:** cancel the cycle, with a reason. This withdraws every pending award.
   - **After the effective date:** reverse per line. This raises a correcting change for normal approval; payroll and
     arrears consume it.

## 8. Rollback

- **Code:** revert the branch.
- **Schema:** `dotnet ef database update 20261011210200_AddSuccession --context ZayraDbContext`. This drops the eight
  tables and the salary-row constraint.
- **Effect on salaries:** awards already applied are ordinary salary rows and remain — they were approved decisions.
  Pending awards are ordinary approved change requests. Cancel them (cycle cancel) **before** rolling back, or they will
  still apply.
- **Faster mitigation:** switch the flag off.

## 9. Residuals (out of scope here, not made worse)

- **P1** Payroll uses gross `Employee.Salary` as basic when no salary row exists (`PayrollController.cs:2220`). CMP
  refuses awards for employees with no row.
- **P1** `POST /api/payroll/employee-salary-structures` and the payroll import write salary rows without maker-checker
  (`PayrollController.cs:452`, `:11344`). Both meet the duplicate guard; the import is now atomic with row-level 422s.
  Maker-checker is still absent.
- **Owner decisions:** recompute percent-of-basic allowances instead of blocking; per-award approval instead of the
  cycle approval (§1).
- **P1** Recommendation approval skips SoD (`RecommendationsController.cs:283`). Untouched.
- **P2** `EmployeeSalaryBreakdownChanges.ValidateAsync` compares gross with the grade min/max in raw numbers, ignoring
  the grade currency. CMP mirrors it so that a block is predicted rather than discovered.
