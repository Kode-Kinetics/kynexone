# Leave and attendance — four wrong figures

Branch `fix/leave-attendance-figures`, base `develop @ d730313`.
Production Postgres consulted read-only throughout. The live Evostel tenant is the one with five
employees, 226 legacy attendance rows and 31 daily records (created 2026-07-18); the other fifteen
`Evostel LLC` rows are stale provisioning attempts with no daily records and no payroll impacts, and
are excluded from every figure below.

**Summary of the four**

| # | Defect | Live for Evostel today? | Direction |
|---|---|---|---|
| 1 | Leave encashment subtracts `Expired` twice | **No** — latent, zero rows armed | Under-pays a leaver |
| 2 | Live balance adds `Entitled` + `Accrued` | **Yes** — 100% of rows | Over-states leave, over-states liability |
| 3 | Approved leave leaves the absence charge standing | **No** — armed, zero overlap | Would under-pay |
| 4 | Dashboard attendance rate counts rest days and leave as absence | **Yes** | Understates the client |

Two further findings are reported without a code change because they need a product decision
(§5), and one clear defect found alongside §4 is fixed (attendance overtime consumed unpaid).

---

## 1. Leave encashment subtracts expired days twice — under-pays a leaver

### Root cause

`backend-dotnet/Zayra.Api/Infrastructure/Payroll/LeaveEncashmentCalculator.cs:117` (pre-fix):

```csharp
var available = Math.Round(b.Available - b.Expired, 2);
```

`backend-dotnet/Zayra.Api/Models/Leave.cs:79` already subtracts it:

```csharp
public decimal Available =>
    Entitled + Accrued + CarriedForward + ManualAdjustment - Used - Pending - Encashed - Expired;
```

`git log -L 78,79:.../Models/Leave.cs` shows `- Expired` was appended by `16ad1b3`
("Sunday demo: harden HRM workflows…", 2026-09-19). The calculator was written against the older
definition and was not revisited.

**The doc comment is the trap.** `LeaveEncashmentCalculator.cs:42-46` said, in bold, that this was
"THE ONE DELIBERATE DIVERGENCE FROM `Available`", that `Available` "does not subtract `Expired`", and
that it was stated prominently "so the two definitions cannot drift for any other reason". Read at
speed it reads as a live justification for line 117. It had become a false statement about
`Models/Leave.cs:79`, and it is what made the duplicate look intentional. The paragraph written to
prevent drift is what concealed it.

Note the direction: the class remarks argue the divergence is safe because it is "in the conservative
direction (it can never over-pay relative to what ESS displays)". Conservative here means **under-pay
the departing employee**, which is the worse direction, and after `16ad1b3` it was not even
conservative — it was simply wrong.

### The money

Leaver on 9,000.00/month. Annual-leave balance: 30 entitled, 10 used, 4 expired. Day rate is
`gross/30` = 300.00 (`DayRateBasis`, unchanged by this fix).

| | Days | Amount |
|---|---|---|
| `Available` (the figure ESS shows) | 30 − 10 − 4 = **16.00** | |
| Paid before | `16 − 4` = **12.00** | 3,600.00 |
| Paid after | **16.00** | 4,800.00 |
| **Shortfall** | 4.00 days | **1,200.00 SAR** |

Source of truth for the right figure: `EmployeeLeaveBalance.Available`, which is what the employee's
own ESS screen, the leave-balance screen and `EncashmentController`'s sufficiency check all read, and
which `EncashmentControllerTests` pins directly. KSA Art. 111 grants payment for accrued untaken leave
on termination; lapsed days are correctly excluded, and after the fix they still are — once.

### Is it live for Evostel?

**No, and not for anyone.** Production has **zero** `employee_leave_balances` rows with `expired > 0`,
across every tenant:

```
rows_with_expired | total_expired_days
                0 |                  0
```

The defect is real and reachable but unarmed. It arms the moment either (a) a leave-expiry sweep
writes the column, or (b) a legacy-balance CSV is imported with a non-zero `Expired` — and `Expired`
is a column in the shipped `leaveBalances` import template
(`MigrationImportController.cs:77`), which is the onboarding path. A migrating customer with lapsed
balances in their old system is the exact victim profile, and they would be under-paid on their first
settlement.

### Fix

`LeaveEncashmentCalculator.cs:117` now reads `Math.Round(b.Available, 2)`, and the class remarks are
rewritten to state that there is **no** divergence, to record why the old paragraph was dangerous, and
to require any future divergence to be expressed as a test in both files rather than as prose.

### Fail before / pass after

New file `backend-dotnet/Zayra.Api.Tests/LeaveEncashmentCalculatorTests.cs` (7 tests: the worked
example above, a 4-case theory asserting the calculator's output equals `Available` by identity for
every component shape, a fully-lapsed balance still encashing nothing, and the per-type cap still
binding).

Before (calculator restored from `develop`, tests unchanged):

```
Error Message:
 Expected result.TotalDays to be 16M because Available already nets off Expired (30 - 10 - 4);
 subtracting it again pays for 12 days and under-pays the leaver by the 4 lapsed days a second
 time, but found 12M (difference of -4).
Failed!  - Failed:     4, Passed:     3, Skipped:     0, Total:     7
```

After (with `EncashmentControllerTests` alongside):

```
Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9
```

---

## 2. The live leave balance adds `Entitled` and `Accrued` together

### Root cause

`backend-dotnet/Zayra.Api/Models/Leave.cs:78-79`. `Entitled` and `Accrued` are two representations of
**one** grant, and `Available` added them.

Which is authoritative depends on the policy, and the code already says so:

- A front-loaded policy (`LeavePolicy.AccrualMethod == "Yearly"`, the default) puts the whole year's
  allocation in `Entitled` and never touches `Accrued`.
- A monthly-accrual policy grows `Accrued` — `LeaveService.cs:294`, `balance.Accrued += employeeMonthlyAccrual`,
  which is **the only writer of either field in production** — and leaves `Entitled` at zero.
  `LeaveService.GetOrCreateBalanceAsync` (`:141-161`) creates rows with neither set, which is the
  coherent accrual shape.
- The other writer of `Entitled` is `LeaveService.cs:420` (`case "Allocation"`), which has **no
  callers**, and `LeaveController.cs:106` (`Entitled = 30`), whose method also has no callers and is
  already on the deletion list at `docs/CONFIGURABILITY_PROGRAM.md:73`.

The rows that carry both come from the five seeders (`AuthSeeder.cs:1109`, `DemoDataSeeder.cs:820`,
`CleanDemoKsaSeeder.cs:608`, `IntelliFlowDemoSeeder.cs:599`, `KsaDemoTenantSeeder.cs:525`, all
`Entitled = entitled, Accrued = Math.Round(entitled * today.Month / 12m, 1)`) and from the CSV
legacy-balance import, where **both columns are `DecRequired`** — mandatory
(`MigrationImportController.cs:534`). The shipped template row is `…,ANNUAL,2026,30,30,0,…`
(`:77`), which alone yielded `Available = 60` for a 30-day entitlement. The double-count was the
documented happy path.

### The chosen reading, and why not simply "pick one"

`Available` now reads a new computed property `Granted => Math.Max(Entitled, Accrued)`
(`Models/Leave.cs`), ignored in EF (`ZayraDbContext.cs`, beside the existing `Ignore(x => x.Available)`).
`dotnet-ef migrations has-pending-model-changes` → **"No changes have been made to the model since the
last migration."** No migration, no backfill, no touch of live data.

Picking a single field would have been a silent data migration in production:

- an accrued-only reading zeroes every CSV-imported tenant whose figure sits in `Entitled` — the exact
  failure already documented at `LeaveEncashmentCalculator.cs:33-36`;
- an entitled-only reading zeroes every tenant on the accrual engine.

`MAX` is a **no-op for every row that populates exactly one of them**, so it cannot move a coherent
tenant at all, and for a row carrying both it recognises the larger of the two representations —
which is by construction ≥ either field alone, i.e. the employee-favourable non-double-counting
answer. That is the tie-break the brief asks for where the reading is genuinely open.

### Art. 109 interaction

`KsaAnnualLeaveScale` (`KsaLeaveAndHoursCalculators.cs:132-163`) tiers the annual quantum 21 → 30 days
at five years of continuous service, seeded as effective-dated rules
(`StatutoryRuleSeeder.cs:273-281`: `leave.annual_base_days`, `leave.annual_tiered_days`,
`leave.annual_tier_threshold_years`). `LeaveService.cs:281-294` applies it as a **floor** over
`LeavePolicy.AnnualEntitlementDays` and writes the result into `Accrued` only, at 21/12 = 1.75 or
30/12 = 2.5 days a month. No statutory value was moved into or out of a calculator by this change.

- On the coherent accrual shape (`Entitled = 0`) `Granted == Accrued`, so the tier is fully visible and
  the 1.75 → 2.5 step shows in the balance the month it happens. Unchanged.
- On a row that also carries `Entitled`, `MAX` front-loads: the balance shows the full annual figure
  and the monthly tier is not visible until `Accrued` overtakes it. That is a **product decision, not
  an arithmetic one** — see the counsel question in §6. It is the employee-favourable side.

The three accrual tests that pin the tiering (`KsaStatutoryLeaveAndHoursTests.cs:239-246, 272, 298`)
assert `balance.Accrued` directly and are untouched by this change; all pass.

### The money

Evostel employee 4697, annual leave 2026 — **the 37.5 on the screen**:

| Component | Value |
|---|---|
| `entitled` | 30.00 |
| `accrued` | 17.50 |
| `used` | 10.00 |
| everything else | 0.00 |

- Before: `30.00 + 17.50 − 10.00` = **37.50 days available against a 30-day entitlement**
- After: `max(30.00, 17.50) − 10.00` = **20.00 days**

All nine Evostel balance rows, from production:

| Employee | Type | Was | Now | Overstated |
|---|---|---|---|---|
| 4695 | Annual | 46.50 | 29.00 | 17.50 |
| 4695 | Casual | 1.90 | **−1.00** | 2.90 |
| 4695 | Sick | 20.80 | 12.00 | 8.80 |
| 4696 | Annual | 40.50 | 23.00 | 17.50 |
| 4696 | Casual | 5.90 | 3.00 | 2.90 |
| 4696 | Sick | 19.80 | 11.00 | 8.80 |
| 4697 | Annual | 37.50 | 20.00 | 17.50 |
| 4697 | Casual | 7.90 | 5.00 | 2.90 |
| 4697 | Sick | 16.80 | 8.00 | 8.80 |

Cash: the leave-liability report multiplies `Available` by a day rate
(`LeaveReportsController.cs:270`). For employees whose salary is populated, e.g. employee 4187 on
22,000.00 (day rate 733.33): provision falls from **55,146.67 to 33,733.33 SAR** — the balance sheet
carried a 39% overstatement. The same figure is what `LeaveEncashmentCalculator` would pay on
termination.

### Is it live for Evostel?

**Yes, and universally.** Every leave balance row in production carries both fields:

```
both_populated | total
          1659 |  1659
```

Total overstatement across production: **16,042.60 leave-days** over 1,659 rows (mean 9.67 days each).

### ⚠️ Figures Evostel has already been shown will move

Flagging prominently, as instructed:

1. **Every leave balance on every screen drops.** Evostel's annual-leave figures go 46.50 → 29.00,
   40.50 → 23.00, 37.50 → 20.00. This is a correction, but it will look like leave being taken away
   and needs to be communicated before deploy, not discovered.
2. **18 balance rows in production become negative** (21 go from positive to ≤ 0; none were negative
   before). Evostel employee 4695's casual leave goes 1.90 → −1.00. That is the true reading — they
   have 4 used plus 2 pending against a 5-day entitlement — and it is *caused by* the defect: the
   sufficiency check `LeaveService.cs:388` (`balance.Available >= requestedDays`) let them book
   against the inflated figure. The negative is the evidence, not a new bug, but it will be visible.
3. **Employees can book less leave than yesterday.** Same sufficiency check. An employee who could
   request 37.5 days can now request 20.

None of these should be suppressed; all three are the inflated number going away.

### The seven divergent copies, collapsed

The formula was re-spelt in seven places and had drifted into four different answers for the same
employee. All are now aligned to `Available` (or, where the expression must translate to SQL, to the
same arithmetic with `Math.Max` and `− Expired`):

| Location | Was also wrong because |
|---|---|
| `Controllers/Reports/ReportsController.cs:453` | omitted `− Expired` |
| `Controllers/EmployeeSelfServiceController.cs:1334` | omitted `− Expired` (the ESS assistant quoted a different balance from the employee's own screen) |
| `Controllers/MigrationImportController.cs:539` | opening-balance origin stamp |
| `Controllers/MigrationImportController.cs:913` | import reconciliation control total |
| `frontend/src/views/LeavePage.tsx:401` | omitted `− Expired`; now reads the server's `available` |
| `frontend/src/views/LeavePage.tsx:502` | apply-leave form green-lit requests the API then rejected; now reads `available` |
| `frontend/src/api/leave.ts` | `granted` added to the type, documented |

`LeavePage.tsx`'s progress bar now divides by `granted` rather than `entitled`, so an accrual-only
tenant gets a bar instead of a permanent 0%. No CSS classes were added or changed: the physical-class
lint count is **0** in `frontend/src` and `frontend/app` and is unaffected by this branch.

### Fail before / pass after

`EncashmentControllerTests.cs` — the existing characterisation test
`AvailableBalance_SubtractsExpiredAndPendingWithoutDoubleSubtractingEncashmentTransfer` pinned the old
sum (20 + 2 + 3 + 1 − 4 − 2 − 3 − 5 = 12). **Its expected total is changed to 10 and the change is
documented in the test itself**, because `Granted` is now `max(20, 2) = 20`. The invariant it was
written for — Expired and Pending netted off once, a pending-to-encashed transfer not deducted twice —
is unchanged and still asserted. Two tests were added beside it: the Evostel shape
(`RowCarryingBothEntitledAndAccrued_CannotExceedTheEntitlement`) and a theory proving MAX is a no-op
for both single-field shapes (`SingleFieldTenantsAreUnaffected`).

Before (with `Granted` mutated back to `Entitled + Accrued`):

```
Failed Zayra.Api.Tests.EncashmentControllerTests.AvailableBalance_SubtractsExpiredAndPendingWithoutDoubleSubtractingEncashmentTransfer
Failed Zayra.Api.Tests.EncashmentControllerTests.RowCarryingBothEntitledAndAccrued_CannotExceedTheEntitlement
```

After: see the combined run in §4.

---

## 3. Approving leave over a day already marked absent leaves the loss-of-pay charge standing

### Root cause

`AttendanceService.ProcessEmployeeDay` reads approved leave exactly **once**, at the instant the day is
processed — `Infrastructure/Attendance/AttendanceService.cs:1006-1010`. If nothing covers the day then,
`:1051` writes `Status = "Absent"` and `:1138` (`UpsertImpacts`) writes
`AttendancePayrollImpact { ImpactType = "Absence deduction", Minutes = 480, Status = "PendingPayroll" }`.

Approval is the other order of events, and nothing reconciled it. `LeaveService.ApproveRequestCoreAsync`
(`Infrastructure/Leave/LeaveService.cs:691-814`) moves the balance, writes its own `LeavePayrollImpact`,
applies the Art. 117 sick scale and notifies the employee — and never looks at attendance.
`grep -rn "Attendance" Infrastructure/Leave/ Controllers/Leave/` returns **zero matches**.

The charge then survives: `PayrollController.cs:1636` loads impacts on `Status != "Processed"`, and
`:2216-2232` turns the 480 minutes into loss of pay. The `c87d8a3` fix at `:2211-2213` removed the
*duplicate* short-hours charge (because `"Absence deduction"` also contains the word `"deduction"`);
the LOP bucket below it is untouched and is the charge that survives approval. Nothing self-heals it:
there is no scheduled attendance worker, and a manual reprocess is refused once the period is locked
(`AttendanceService.cs:632-635`).

Backdated leave is the ordinary case, not an edge. `SubmitRequestCoreAsync` has **no past-date guard**,
and sick leave — reported after the fact by definition — is the archetype. A future-dated request hits
it too whenever attendance is processed before the last approval step lands.

### The money

Employee on 9,000.00 basic, KSA default LOP divisor 30, standard day 480 minutes. One backdated sick
day already processed as absent:

| | |
|---|---|
| Stale impact | 480 minutes, `Absence deduction`, `PendingPayroll` |
| LOP days | 480 ÷ 480 = 1.0000 |
| LOP day rate | 9,000.00 ÷ 30 = 300.00 |
| **Deducted for an approved paid sick day** | **300.00 SAR** |

Source of truth: the day is approved paid leave, so the employer owes the wage in full; there is no
absence to charge for. `AttendanceService.cs:1034` already agrees — it writes `"On leave"` and no
impact whenever the approval happens to land first.

### Is it live for Evostel?

**Armed but not fired.** In the live tenant there are **zero** days where an approved leave request
overlaps an `Absent` daily record:

```
tenant_id | employee_id | leave_type_name | start_date | end_date | work_date | status | impact_type | status
(0 rows)
```

All 23 `Absence deduction` rows in the live tenant (11,040 minutes, `PendingPayroll`) belong to
employee 4803, who has no leave requests at all. Two overlaps do exist in the *stale* Evostel
provisioning tenants (employees 4473 and 4372, July sick leave), but those tenants have no
`attendance_daily_records` and therefore no impact rows, so no money is at stake there either. Fixed
before it cost anyone.

### Fix

New `LeaveService.ReconcileAttendanceForApprovedLeaveAsync`, called from the **final-step branch only**
of `ApproveRequestCoreAsync` (the non-final early return at `:735-739` must not reprocess, and does
not). Both HTTP entry points — `LeaveRequestsController.cs:234` and
`ApprovalWorkflowService.cs:297` (Approval Center and mobile) — funnel through this one method, so one
hook covers all three clients.

It is deliberately **not** a call into `IAttendanceService`, for three reasons recorded in the method's
remarks:

- `ValidateProcessRangeAsync` **throws** on a payroll-locked period, which would turn a lock into a new
  way for a leave approval to fail. A correctness fix must not become a new failure mode.
- `UpsertImpacts` deletes every impact for the day **regardless of `Status`**, so a blind reprocess
  would silently un-consume rows a closed run had already processed, desynchronising them from the
  `PayrollRunConsumption` witnesses that `PayrollVoidService.cs:1026-1042` unwinds from.
- It would re-derive punches, shifts and policies that approving leave did not change.

What it does: for each `Absent` daily record inside the approved range, set `Status = "On leave"`, zero
late/early-exit/undertime and clear `MissingPunch` (mirroring `AttendanceService.cs:1034-1037` exactly,
so a later reprocess is a no-op rather than a second, different answer), remove the pending
`Absence deduction`, and mirror the status onto the legacy `attendance_records` projection that the
dashboard reads.

What it refuses to do:

- a **payroll-locked** day is skipped and flagged; the approval still succeeds;
- a day whose absence charge is already **`Processed`** is left alone and flagged `[FLAG-PAYROLL] … the
  employee has been under-paid for those days`, because money already paid out is recovered through a
  payroll adjustment or a void, never by deleting the row underneath it;
- days outside the range, and non-absence impacts (a `Late deduction` on the same day) are untouched.

Both flags land in the leave audit line, so the approver is told.

Idempotent under `NpgsqlRetryingExecutionStrategy`: it asserts a target state rather than applying a
delta, so a retried body reaches the same place. It stages only — no `BeginTransactionAsync`, no
`SaveChangesAsync` of its own beyond the audit write that already existed.

### Fail before / pass after

New file `backend-dotnet/Zayra.Api.Tests/LeaveApprovalAttendanceReconciliationTests.cs`, 5 tests. The
nearest existing coverage, `AttendanceBusinessInvariantTests.ApprovedLeave_WithNoPunches_DoesNotCreateAbsenceDeduction`,
seeds the leave **first** and then processes — it proves only the one ordering in which the defect
cannot occur. These are that test with the two steps swapped.

Before (reconcile call unhooked):

```
Failed Zayra.Api.Tests.LeaveApprovalAttendanceReconciliationTests.ApprovingLeaveOverADayAlreadyMarkedAbsent_ClearsTheLossOfPayCharge
Failed Zayra.Api.Tests.LeaveApprovalAttendanceReconciliationTests.APayrollLockedDay_IsSkippedAndFlagged_AndTheApprovalStillSucceeds
Failed Zayra.Api.Tests.LeaveApprovalAttendanceReconciliationTests.ADayAlreadyChargedByAProcessedRun_IsNotAltered_ButIsFlagged
```

(the two negative tests — nothing in range, only-in-range — pass before and after, by design.)

---

## 4. The dashboard attendance rate counts rest days and approved leave as absence

### Root cause

`backend-dotnet/Zayra.Api/Controllers/DashboardController.cs:337-353` (pre-fix):

```csharp
Total        = g.Count(),
PresentCount = g.Count(a => a.Status == "Present"),
...
var rate = row is { Total: > 0 } ? Math.Round(row.PresentCount * 100m / row.Total, 1) : 0m;
```

Wrong at both ends:

- **Denominator.** `g.Count()` is every attendance row, including `"Rest day"`, `"On leave"`,
  `"Public holiday"` and `"Half day"`. The calendar counted against the employee as a failure to turn
  up on days nobody rostered them for.
- **Numerator.** Only the literal `"Present"`. A `"Late"` day — on which they *did* turn up, and for
  which they are already docked through the short-hours deduction — counted against them a second
  time.

The status vocabulary the processor actually writes is at `AttendanceService.cs:1031-1055`.

### The money — Evostel's exact shape

From production, `attendance_records` for the live tenant, 226 rows:

| Status | Count |
|---|---|
| Present | 158 |
| Late | 24 |
| Absent | 27 |
| Leave | 9 |
| Rest day | 8 |

- **Before:** 158 ÷ 226 = 69.9115% → **69.9%**
- **After:** (158 + 24) ÷ (226 − 8 − 9) = 182 ÷ 209 = 87.0813% → **87.1%**

Both figures reproduce to the tenth of a percent. Source of truth for the denominator: a rest day, a
public holiday and an approved leave day are days on which no attendance was owed — the roster and the
approved leave request say so, and `AttendanceService.cs:1006-1046` has already encoded that judgement
into the status. Source of truth for the numerator: `AttendanceService.cs:888`, the attendance
module's own dashboard, which has always counted `"Present" or "Late" or "Half day"` as present. The
fix adopts that predicate, which **also closes the "two screens, two answers" divergence** for the
predicate (the table divergence remains — §5.1).

This figure is a client-facing KPI, not cash, but it is one Evostel is judged on.

### ⚠️ A figure Evostel has already been shown will move

The dashboard attendance trend goes from 69.9% to 87.1%. It is moving in their favour and is the
correct number, but it is a visible change.

### Also fixed here: the on-leave tile was structurally always zero

`DashboardController.cs:263-264` and `:311` counted `"Leave"` and `"On Leave"`, while the processor
writes `"On leave"` (lower-case L, `AttendanceService.cs:1034`). On any processed tenant the tile read
0. The one test covering it (`UnitTest1.cs:29`) seeds `"On Leave"` by hand, which is why it passed.
Both spellings are genuinely present in live data — Evostel's own legacy rows carry `"Leave"` — so all
three are now accepted rather than one being declared canonical without a backfill.

### Also fixed here: attendance-derived overtime was consumed and destroyed unpaid

`PayrollController.cs:1636` loaded `"Overtime payable"` impacts. They match **neither** money bucket
(`:2211-2213` wants `"deduction"`, `:2220-2222` wants `"Absence"`), there is no third reader of
`attendanceImpacts`, and `:2966-2971` then flipped the whole list to `"Processed"` — which the
`Status != "Processed"` predicate at `:1636` uses to hide them from every future run. The consumption
witness even records `Amount = 0m` for attendance impacts, so the audit trail could not show the loss.
Attendance overtime was permanently swallowed by the first run covering its period.

**A run must not consume what it does not pay.** The load query now excludes
`AttendanceImpactTypes.OvertimePayable`, so those rows stay `PendingPayroll` and remain claimable. This
does **not** start paying them — overtime money comes from `OvertimePayrollImpact`, raised only when an
overtime *request* is approved (`OvertimeController.cs:244`), reachable from attendance through the
explicit `POST /api/overtime/detect-from-attendance` (`:171-210`). Whether attendance overtime should
pay automatically is a product decision (§5.2); destroying it in the meantime is not.

**No payslip figure moves**: the excluded rows contributed to neither money bucket before. The only
behavioural change is that they are no longer marked `Processed`. Production has **zero**
`Overtime payable` rows in any tenant, so nothing is live.

### New shared vocabulary

`backend-dotnet/Zayra.Api/Application/Attendance/AttendanceStatuses.cs` (new) holds the status and
impact-type vocabulary and the two rate predicates (`IsAttended`, `IsScheduledWorkingDay`). All three
bugs in this section were spelling drift between inline string literals.

### Fail before / pass after

New file `backend-dotnet/Zayra.Api.Tests/DashboardAttendanceRateTests.cs`, 8 tests including Evostel's
exact 226-row shape. The existing rate test (`UnitTest1.cs:50-73`) seeds only `"Present"` and
`"Absent"`, so the defect was invisible to it; it still passes unchanged.

**Combined fail-before for defects 2, 3 and 4** (each behaviour reverted in place, API surface kept so
the tests compile):

```
  Failed Zayra.Api.Tests.EncashmentControllerTests.AvailableBalance_SubtractsExpiredAndPendingWithoutDoubleSubtractingEncashmentTransfer
  Failed Zayra.Api.Tests.DashboardAttendanceRateTests.OnLeaveTile_CountsTheSpellingTheProcessorActuallyWrites
  Failed Zayra.Api.Tests.DashboardAttendanceRateTests.ANonWorkingDayNeverDepressesTheRate(nonWorkingStatus: "Rest day")
  Failed Zayra.Api.Tests.DashboardAttendanceRateTests.ANonWorkingDayNeverDepressesTheRate(nonWorkingStatus: "Public holiday")
  Failed Zayra.Api.Tests.DashboardAttendanceRateTests.ANonWorkingDayNeverDepressesTheRate(nonWorkingStatus: "On leave")
  Failed Zayra.Api.Tests.DashboardAttendanceRateTests.ANonWorkingDayNeverDepressesTheRate(nonWorkingStatus: "On Leave")
  Failed Zayra.Api.Tests.DashboardAttendanceRateTests.ANonWorkingDayNeverDepressesTheRate(nonWorkingStatus: "Leave")
  Failed Zayra.Api.Tests.DashboardAttendanceRateTests.RestDaysAndApprovedLeaveAreNotAbsence_AndALateDayIsAttendance
  Failed Zayra.Api.Tests.EncashmentControllerTests.RowCarryingBothEntitledAndAccrued_CannotExceedTheEntitlement
  Failed Zayra.Api.Tests.LeaveApprovalAttendanceReconciliationTests.ApprovingLeaveOverADayAlreadyMarkedAbsent_ClearsTheLossOfPayCharge
  Failed Zayra.Api.Tests.LeaveApprovalAttendanceReconciliationTests.APayrollLockedDay_IsSkippedAndFlagged_AndTheApprovalStillSucceeds
  Failed Zayra.Api.Tests.LeaveApprovalAttendanceReconciliationTests.ADayAlreadyChargedByAProcessedRun_IsNotAltered_ButIsFlagged
Failed!  - Failed:    12, Passed:     7, Skipped:     0, Total:    19
```

After:

```
Passed!  - Failed:     0, Passed:    19, Skipped:     0, Total:    19
```

---

## 5. Reported, not fixed — these need a product decision

### 5.1 Two attendance tables give two different answers

`DashboardController` is the **only** consumer left reading the legacy `attendance_records`
(`:262-344`) for its headline numbers, while the attendance module, analytics, reports, ESS, mobile,
timesheets and payroll all read `attendance_daily_records`. `DashboardController` even mixes them:
`:552` reads the daily table for its exceptions KPI.

Two independent divergences:

1. **The predicate**, now closed by §4 — both sides count `"Present" or "Late" or "Half day"`.
2. **The population, still open.** `attendance_records` is a lossy projection maintained only by
   `AttendanceService.UpsertLegacyRecord`. Anything writing the daily table by another route —
   `MobileController.cs:216`, `MigrationImportController.cs:552`, `SundayKsaDemoFixtureSeeder.cs:377` —
   never updates it, so those days are invisible to the dashboard.

Evostel's data shows the split exactly. `attendance_records`: 226 rows, 2026-04-19 → 2026-08-31.
`attendance_daily_records`: 31 rows, **August only**. For August the two agree row for row (verified by
full outer join, all 31 rows matching on status). For April–July the daily table is empty.

**Why I did not switch the dashboard to the canonical table:** it would silently delete four months of
Evostel's attendance history from their dashboard. The right fix is a backfill of
`attendance_daily_records` from `attendance_records` for the pre-August period, then repointing the
dashboard, then deleting the legacy projection — a data migration that needs its own change and its own
sign-off, not a line in a defect fix. Recommend sequencing it next.

### 5.2 Should attendance-derived overtime pay automatically?

Now that it is no longer destroyed (§4), the rows accumulate as `PendingPayroll`. Three options:
(a) leave the manual `POST /api/overtime/detect-from-attendance` → approve → pay route as the only
path, which is the current design and the most defensible for KSA Art. 107 (overtime should be
authorised); (b) auto-raise an `OvertimeRequest` when attendance detects overtime; (c) pay the
attendance impact directly. **(a) is the status quo and is what the branch leaves in place.** Note
that if (b) or (c) is ever chosen, the accumulated `PendingPayroll` backlog will pay out in one run —
a deliberate decision to take, not a surprise to discover. Zero rows exist today.

### 5.3 A related gap left in place

`LeaveService.CancelRequestAsync` (`:1069-1112`) is the mirror image of defect 3 — cancelling an
approved leave removes the `LeavePayrollImpact` but does not reprocess attendance, so the day keeps
reading `"On leave"` with no absence charge. Same for
`AbsenceController.ApproveRegularization` (`Controllers/Leave/AbsenceController.cs:162-192`), which
sets `IsRegularized` and touches no impact. Both are in the same class of bug and both favour the
employee, so neither is urgent; flagged rather than fixed to keep this branch's blast radius on the
four figures it was scoped to.

---

## 6. Counsel question

**For the standing counsel list:**

> Where an employee's leave-balance row carries both a front-loaded annual entitlement (`Entitled`) and
> a month-by-month accrual (`Accrued`) — as every seeded and every CSV-imported tenant does — should
> the balance available to the employee mid-year be the **full annual entitlement** (front-loaded) or
> the **earned-to-date accrual**?
>
> KSA Art. 109 fixes the *quantum* (21 days, 30 from five years of continuous service) but is silent on
> whether it vests at the start of the leave year or accrues monthly. The distinction bites on
> termination under Art. 111: a leaver five months into the year is owed either 5/12 of the annual
> grant or the whole of it, less days taken.
>
> This branch takes the employee-favourable reading (`max` of the two, i.e. front-loaded where both are
> present) and records `LeavePolicy.AccrualMethod` as the field that already expresses the tenant's
> intent. Confirm whether that default is correct for KSA and whether it may lawfully be configured per
> tenant, or whether the accrued-to-date reading is mandatory.

A second, smaller one, found in passing: Evostel's three employees with leave balances joined
2022-01-01, 2023-03-16 and 2023-06-13 — all **under** five years of continuous service as at
2026-09-21, so their Art. 109 statutory entitlement is 21 days. Their `entitled` column is seeded at
30.00. Over-granting is lawful (Art. 109 is a floor) and may be contractual, but it is worth confirming
with the client that 30 is intended rather than a seeding artefact, because it is the figure their
encashment on termination will be computed from.

---

## 7. Verification

### Machine discipline

Full-suite runs were gated on load < 18 with zero competing test hosts, polled and launched in a single
shell invocation (`scratchpad/la-run.sh`, pattern built at runtime in a script file). Three other agents
were running concurrently; the gate waited for the machine rather than competing with them.

### Migration gate

```
$ dotnet tool restore --tool-manifest dotnet-tools.json
$ dotnet tool run dotnet-ef migrations has-pending-model-changes --project backend-dotnet/Zayra.Api/Zayra.Api.csproj
Build succeeded.
No changes have been made to the model since the last migration.
```

**No migration is added by this branch.** `Granted` is a computed property, ignored in EF beside the
existing `Ignore(x => x.Available)`. Evostel's data is untouched — every fix is a change to how stored
columns are *read*, not to the columns.

### Ratchets

No `ScopedBypass` added (`git diff develop | grep ScopedBypass` → empty), so
`QueryFilterBypassRatchetTests`' pinned count is untouched. No new entity types, so
`OrphanEntityRatchetTests`' 29 is untouched. No ratchet is suppressed, weakened or re-pinned.

### Frontend

`npx tsc --noEmit` → exit 0. Physical-class count in `frontend/src` and `frontend/app`: **0** — this
branch adds no CSS classes at all (the changes are JS expressions, a TS interface field and comments).

### Deletions

```
$ git diff --diff-filter=D --name-only develop fix/leave-attendance-figures
$
```

Empty. No file is deleted by this branch.

### Full suite — reconciled by name

Both runs on the same machine, gated, with the equivalence capture on.

| | Result |
|---|---|
| Baseline, `wt-leaveatt-base` @ `d730313` | `Passed! - Failed: 0, Passed: 2586, Skipped: 0, Total: 2586, Duration: 2 m 33 s` |
| Candidate, `fix/leave-attendance-figures` | `Passed! - Failed: 0, Passed: 2610, Skipped: 0, Total: 2610, Duration: 2 m 2 s` |

Baseline matches the stated 2586 / 0 exactly. Difference: **+24, all added, none removed.**

```
$ comm -23 baseline-names candidate-names | wc -l
0
$ comm -13 baseline-names candidate-names
   1 DashboardAttendanceRateTests.AMonthOfOnlyRestDaysReportsZeroRatherThanDividingByZero
   5 DashboardAttendanceRateTests.ANonWorkingDayNeverDepressesTheRate                     (theory, 5 cases)
   1 DashboardAttendanceRateTests.OnLeaveTile_CountsTheSpellingTheProcessorActuallyWrites
   1 DashboardAttendanceRateTests.RestDaysAndApprovedLeaveAreNotAbsence_AndALateDayIsAttendance
   1 EncashmentControllerTests.RowCarryingBothEntitledAndAccrued_CannotExceedTheEntitlement
   3 EncashmentControllerTests.SingleFieldTenantsAreUnaffected                            (theory, 3 cases)
   1 LeaveApprovalAttendanceReconciliationTests.ADayAlreadyChargedByAProcessedRun_IsNotAltered_ButIsFlagged
   1 LeaveApprovalAttendanceReconciliationTests.APayrollLockedDay_IsSkippedAndFlagged_AndTheApprovalStillSucceeds
   1 LeaveApprovalAttendanceReconciliationTests.ApprovingLeaveOverADayAlreadyMarkedAbsent_ClearsTheLossOfPayCharge
   1 LeaveApprovalAttendanceReconciliationTests.NoProcessedAttendanceInRange_ChangesNothingAndAddsNoNote
   1 LeaveApprovalAttendanceReconciliationTests.OnlyAbsentDaysInsideTheApprovedRangeAreTouched
   4 LeaveEncashmentCalculatorTests.EncashableDaysEqualAvailableExactly_WhateverTheComponents (theory, 4 cases)
   1 LeaveEncashmentCalculatorTests.ExpiredDaysAreNettedOffExactlyOnce_NotTwice
   1 LeaveEncashmentCalculatorTests.FullyLapsedBalanceStillEncashesNothing
   1 LeaveEncashmentCalculatorTests.PerTypeCapStillBindsAfterTheFix
                                                                                      total 24
```

`EncashmentControllerTests.AvailableBalance_SubtractsExpiredAndPendingWithoutDoubleSubtractingEncashmentTransfer`
appears in **both** lists' intersection — the name is unchanged and it passes in both runs. Only its
expected total moved (12 → 10), documented in §2 and in the test's own remarks.

### Payroll equivalence

Fresh baseline captured off `develop @ d730313` in a dedicated worktree (`wt-leaveatt-base`); the
older `.norm` files in this directory are stale and were not reused. Both dumps normalised with
`scratchpad/equiv-norm.sh`.

```
$ wc -l la-baseline.txt la-candidate.txt
8223 la-baseline.txt
8223 la-candidate.txt

$ diff la-baseline.norm la-candidate.norm | wc -l
0
$ cmp la-baseline.norm la-candidate.norm && echo IDENTICAL
IDENTICAL
$ md5 -q la-baseline.norm la-candidate.norm
2c6910dc40be1d5c0e43c38288ba10b5
2c6910dc40be1d5c0e43c38288ba10b5
```

**No payroll figure moves. Zero changed lines, so there is nothing to explain away.** The employee-code
field needed no stripping — the normaliser's GUID substitution already absorbs it, and the two dumps
are byte-identical including it.

This is a weaker result than it looks, and the reason matters more than the result:

- **Defect 1** cannot move an equivalence figure because **no suite fixture has `Expired > 0` on a
  balance that reaches a settlement**. The only `Expired = 5m` in the whole test project is
  `EncashmentControllerTests.cs:83`, a pure property assertion that never runs payroll. The suite has
  the same blind spot production has.
- **Defect 3** cannot move one because **no suite fixture approves leave over a day already processed
  as absent** — that ordering was untested, which is why the defect survived.
- **Defect 4's overtime exclusion** provably cannot move one: `"Overtime payable"` matched neither
  money bucket before, so removing it from the loaded list changes only which rows get stamped
  `Processed`.
- **Defect 2** does not reach a payslip line in any suite scenario; it moves balance, liability-report
  and encashment figures, which the capture does not record.

So equivalence proves the fixes did not disturb the 8,223 payroll facts the suite does exercise. It
does **not** prove defects 1 and 3 are fixed — the 24 targeted tests in §1 and §3 do that, and they
exist precisely because the equivalence harness is blind to those paths.
