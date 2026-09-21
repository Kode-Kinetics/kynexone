# Configuration consumers — make stored configuration take effect, or refuse it

Branch `fix/config-consumers`, based on `integration/wave6` (`6d7905f`). Committed, not pushed, not
merged.

---

## 1. The verified inventory

Re-verified against the code on `integration/wave6`, not taken from the register. The prior register
(`scratchpad/s3-dead-configuration-register.md`) listed 20 items; **one has since been fixed by
another stream and is removed from the list.** Nothing new was found dead, and the 29 orphan
entities are unchanged in both count and membership.

| # | Setting / field | Tenant write path | Runtime read | Outcome |
|---|---|---|---|---|
| 1 | `Employee.AttendancePolicyCode` | employee form, DTO, CSV import + export | none | **WIRED** |
| 2 | `LeavePolicy.ApprovalWorkflowId` | `LeavePoliciesController` create + update | none | **WIRED** |
| 3 | `ContractTemplate.Variables` (+ the whole merge step) | `ContractsController:82` | none | **WIRED** |
| 4 | `ApprovalDelegation` (whole entity) | `AccessController` POST / PATCH-cancel | none | **REFUSED 501** |
| 5 | `ApprovalAuthority.AmountLimit` / `CanFinalApprove` | `AccessController` POST / PUT | none | **REFUSED 501** |
| 6 | Approval workflow `EntityName` = `OvertimeRequest` | `ApprovalWorkflowsController` + seeder | none | **REFUSED 400** + seed removed |
| 7 | …`PayrollRun` | as above | none | **REFUSED 400** + seed removed |
| 8 | …`EmployeeDraft` | `AuthSeeder` | none | **REFUSED 400** + seed removed |
| 9 | …`EmployeeTransferRequest` | `AuthSeeder` | none | **REFUSED 400** + seed removed |
| 10 | `LeavePolicy.CarryForwardMax` / `CarryForwardExpiry` | `LeavePoliciesController` | none | **REFUSED 400** + UI input removed |
| 11 | `PublicHoliday.IsRecurring` | `HolidayCalendarController` create + update | none | **REFUSED 400** + UI checkbox & badge removed |
| 12 | `CountryPayrollSetting.WpsMolCode` | `SetupSettingsController:200,215` | none | **NOT DONE — see §6** |
| 13 | `CountryPayrollSetting.SifEnabled` | `SetupSettingsController:200,215` | none | **NOT DONE — see §6** |
| 14 | `RamadanHoursEnabled` / `RamadanReducedHoursPerDay` | `SetupSettingsController:207,222` | none | **NOT DONE — see §6** |
| 15 | `Location.GeofenceRadiusMeters` | `SetupSettingsController:296,315` | none | **NOT DONE — see §6** |
| 16 | `EmployeeOffboarding.RehireEligible` | `OffboardingController:206` | none | **NOT DONE — see §6** |
| 17 | `AttendancePolicy.LateThresholdMinutes`, `AbsentThresholdMinutes`, `RequiresOvertimeApproval`, `AllowAbsenceToLeaveConversion` | seeders only — **no tenant write path exists** | none | **NOT DONE — see §6** |
| 18 | `LoanPolicy.MaxMultiplierOfSalary`, `AllowEarlySettlement`, `AllowRescheduling` | **no write path exists at all** | none | **NOT DONE — latent** |
| 19 | `LeaveRequest.ReturnDate` | none | none | **NOT DONE — pure schema debt** |
| 20 | `OvertimePolicy.RamadanReducedHoursPlaceholder` | none | none | **NOT DONE — pure schema debt** |
| 21 | 29 entities with a `DbSet`, a table and no code | n/a | none | **PINNED by ratchet** |
| 22 | `AttendanceLockPeriod` | **no application code inserts one** | enforcement IS live | **NOT DONE — inverse defect** |

### Removed from the register — now fixed elsewhere

**`CountryPayrollSetting.EosbMinYears` / `EosbYears1To5Rate` / `EosbYearsAbove5Rate`.** The register
recorded these as the highest-severity money item. They are **now read**: `PayrollController.cs:6881`
builds an `EosbPolicyOverride(gcc.EosbYears1To5Rate, gcc.EosbYearsAbove5Rate, gcc.EosbMinYears)` and
`KsaCalculators.cs:249` applies it through `ApplyPolicyRates`. Partially, honestly: the UAE and Qatar
packs read `EosbMinYears` only as a notice and still hard-code 21/30 days
(`UaeCalculators.cs:148-149`, `QatarCalculators.cs:150`). I did not touch this.

### Two items where the defect runs the other way

Recorded because they are the same failure in mirror image, and both are cheap.

- **`AttendanceLockPeriod`** — the payroll-period lock is *enforced* in four places
  (`AttendanceService.cs:632` refuses processing, `:1225` `IsLocked`, consulted at `:538`, `:794`)
  and **no application code ever inserts a lock row**; the only inserts are three test files. So the
  period lock is permanently open in production while the test suite shows it covered.
- **`LoanPolicy`** — `MaxConcurrentLoans` and `CooldownMonthsAfterRepayment` *are* enforced
  (`LoansController.cs:139,142`) but no code can ever create a `LoanPolicy` row, so the live
  consumer's `policy != null` is always false.

---

## 2. Wired — with the consumer, and why the test is not a round-trip

Each of these is proved by a test that **changes the configured value and observes different
behaviour**. A save-and-read-back test is exactly the test that let twenty of these ship, so none is
used here.

### 2.1 `Employee.AttendancePolicyCode`
**Consumer:** `backend-dotnet/Zayra.Api/Infrastructure/Attendance/AttendanceService.cs:1246-1256`
(`ResolveAttendancePolicy`).

A registered employee field labelled "Attendance policy", on the employee form, in the DTO, in the
CSV import template and in the export — read by nothing. Every employee was governed by their
branch/department/grade match or, failing that, by `policies.OrderBy(p => p.Code).First()`. A client
migrating from another HRIS mapped the column because the template offered it, and their grace
periods, late thresholds and standard hours were then wrong for everyone.

The assignment now wins. A code matching no active policy falls through to the existing tiering
rather than failing the day's processing — import data is dirty, and refusing to process attendance
is a worse answer than the previous behaviour. Matching is case-insensitive.

### 2.2 `LeavePolicy.ApprovalWorkflowId`
**Consumers:** `Infrastructure/Leave/LeaveService.cs:628-640` (reads the resolved policy's pin) and
`Infrastructure/Approvals/ApprovalRouter.cs:96-119` (`ResolvePinnedAsync`, new).

Settable per leave policy since before F1 and read by nothing, so "sick leave is approved by HR
only, annual goes line manager → HR, unpaid needs the MD" saved, read back, and then routed every
type through the one department-level workflow — meaning the line manager saw the sick notes.

A pin is honoured only when the workflow is **active** and belongs to **LeaveRequest**; otherwise the
router returns null and the caller falls back to the tier match, so a stale pin degrades to the old
behaviour rather than blocking a submission. Department/grade scoping is deliberately not re-checked
on a pinned route: an explicit pin is a narrower statement than the tier rule, and overriding it is
the point.

### 2.3 `ContractTemplate.Variables` and the merge step
**Consumers:** `Infrastructure/Compliance/ContractMergeFields.cs` (new) and
`Controllers/Compliance/ContractsController.cs:168-199`.

The template body was copied **verbatim** — there was no substitution step anywhere in the product.
A template written with `{{employee_name}}` produced a contract containing those literal braces, so
an employment contract could be issued reading *"This agreement is between the Company and
{{employee_name}}"*. The declared merge-field list was written on create and read by nothing.

Both halves are live now:
- a declared field the product cannot supply **refuses at the template**
  (`contract_merge_field_unsupported`), catching a typo once instead of on every contract;
- an unresolved `{{…}}` surviving substitution **refuses the contract**
  (`contract_unresolved_merge_field`) — a legal document is not issued with a hole in it, and
  silently deleting the token would be a worse document.

Only the double-brace form is a merge token: `NotificationBodyPolicy.Interpolate` also substitutes
single `{token}`, which is right for an SMS body and wrong for contract HTML carrying inline CSS.
Values are HTML-encoded. The field set is an allow-list of eleven facts — bank details, national IDs
and passport numbers are absent on purpose.

---

## 3. Refused — 501 / 400 with a code and a pointer

Following the rule this codebase already wrote down in `ApprovalPoliciesController` and applied once:
*a configuration endpoint that no runtime path reads must answer 410 (retired) or 501 (never built),
with a machine-readable code and a replacement pointer — never 200.*

### 3.1 `ApprovalDelegation` — writes answer 501
`Controllers/AccessController.cs:409-415`, code `approval_delegation_not_implemented`.

Refused rather than wired, deliberately. The register recommends wiring it, and I judged against it:
the entity's `Scope` is free text documented only as *"e.g. All, Leave, Payroll"*, with no vocabulary
anywhere in the code. Matching those strings onto approval entity names would be **inventing** the
semantics, and a wrong consumer silently does something the client did not ask for — worse than no
consumer. Two further facts make the stored rows unusable as they stand: the whole controller is
`[HasPermission("security.manage")]`, so an approver cannot record their own delegation before going
on leave, and there is no escalation timer, so a delegated-away queue would stall regardless.

`GET` and the cancel endpoint stay live: a tenant holding rows must still see them and be able to
clear them. Hiding the data would be a second deception on top of the first.

### 3.2 `ApprovalAuthority` — writes answer 501
`Controllers/AccessController.cs:426-432` and `:447-453`, code `approval_authority_not_implemented`.

The structural reason is decisive: **`ApprovalRequest` carries no monetary amount.** There is nothing
for an `AmountLimit` to be compared against, so "department managers may approve up to SAR 50,000"
could never have been applied, and storing it produces the client's own evidence of a control that
does not exist — the failure mode that ends in an external audit finding. Wiring it is a 3–5 day
feature (amount + currency on `ApprovalRequest`, a migration, every producer taught to populate it,
currency normalisation, a decision rule), not a wiring job.

### 3.3 Leave carry-forward — 400
`Controllers/Leave/LeavePoliciesController.cs:137-150`, applied at `:79` and `:162`, code `leave_carry_forward_not_implemented`.
Applied on both create and update.

**There is no leave year-end process of any kind.** No accrual job, no roll-over job, no expiry job —
the only background job type the product registers is `attendance.process`. On 1 January nothing
happens. A cap was stored, read back on screen, and consulted by nothing. Zero — the documented
"none" value — is still accepted, because it is the only value that is currently true.

The `Carry-Forward Max` input is **removed** from `LeavePage.tsx`, and the form no longer echoes a
stored non-zero value back (it sends 0), so a legacy policy stays editable instead of failing every
save. The guard comes out in the same change that adds the year-end job — which is why
`LeaveAccrualRule`, its data model, must not be deleted.

### 3.4 Recurring holidays — 400
`Controllers/Leave/HolidayCalendarController.cs:91-105`, applied at `:116` and `:183`, code
`holiday_recurrence_not_implemented`. Applied on both add and update.

Nothing has ever expanded a recurring holiday into the next year: calendars are per `CalendarYear`
and no scheduled job exists that could roll one forward. The checkbox **and the blue "Recurring"
badge** are removed from `LeavePage.tsx` — the badge was the visible half of the promise — and
`openEditHoliday` now always sends `false` so a legacy row stays editable. Hijri-dated holidays are
the reason "just copy the calendar forward" is not a safe silent default: they move about eleven days
a year and must be confirmed by a person.

---

## 4. The four dead approval chains, and the reasoning for each

`ManpowerRequisition` was converged onto `IApprovalWorkflowService` on `feat/demo-reachability`
(`3914011`). I read that commit and `Application/Approvals/ApprovalDecisionGuard.cs` first. The two
patterns in this codebase are explicitly distinguished by the guard's own doc comment:
`IApprovalWorkflowService` owns entities whose approval lives on the **shared `ApprovalRequest`
aggregate**; `ApprovalDecisionGuard` serves modules that own **their own aggregate and step table**.

**All four are refused, none is converged.** Here is why each, individually.

### `OvertimeRequest` — refused
The register described this as a bare status flip at `OvertimeController.cs:230`. **That is no longer
true.** Overtime today owns a genuine two-stage chain — `PendingManager → PendingHR → Approved`
(`OvertimeController.cs:214-280`) — with its own step table (`OvertimeApprovals`), its own
maker-checker (`:221`), a `DecisionVersion` concurrency guard, and a decision that produces an
`OvertimeCalculation` and an `OvertimePayrollImpact` at final approval. Its approvers are already
resolved from live configuration: `IsCurrentUserResolvedApproverAsync(…, "Overtime", …)` at `:626`
reads an **HRM workflow** of type `Overtime`.

So overtime approvals *are* configurable — through a different, working surface. The seeded
`OVERTIME-DEFAULT` `ApprovalWorkflow` was a second, inert configuration point for the same decision.
Converging it would put the shared engine's step configuration and the module's own status machine in
charge of one decision at once — precisely the "two records of the same fact, permanently
disagreeing" defect the requisition commit had to fix. It would also change who may approve overtime
in a live pilot. Refusing the duplicate and leaving the working chain alone is the correct outcome.

### `PayrollRun` — refused
`git grep ApprovalRequest -- PayrollController.cs` returns nothing: no code path has ever created a
shared approval row for a payroll run, and there is no `PayrollRun` approval aggregate to sync back
to. Wiring it would mean designing payroll approval from scratch — a feature, inside S1's file, on
the money path. Out of scope for a wiring change, and inventing it would be worse than refusing it.

### `EmployeeTransferRequest` — refused
Transfers have a bespoke inline flow in `EmployeesController` that never touches the workflow engine.
The seeded workflow was the most misleading of the four: a two-step **"Current Manager → HR
Approval"** chain, visible in the Approvals configuration and citable in a security questionnaire,
routing nothing. Converging it is a real piece of work (a sync hook plus a decision surface on the
transfer aggregate); asserting it works is not.

### `EmployeeDraft` — refused
Same shape: drafts are reviewed and activated on the employee record. The seeded
"Employee Onboarding Approval" chain routed nothing.

**What "refused" concretely means:** `ApprovalWorkflowsController.Create` and `.Update` reject an
`EntityName` with no producer (§5.2), the four seeds are gone from `TenantProvisioningBundle` and
`AuthSeeder`, and the two demo `ApprovalRequest` rows for `PayrollRun` and
`EmployeeTransferRequest` are gone from `AuthSeeder` — the transfer one pointed at a
`Guid.NewGuid()` EntityId, so it was a queue item that opened onto no record at all. Existing rows in
a deployed tenant are untouched: nothing is dropped, and the tenant can still list them.

---

## 5. The two guards

### 5.1 `OrphanEntityRatchetTests` — pinned at **29**
`backend-dotnet/Zayra.Api.Tests/Security/OrphanEntityRatchetTests.cs`, shaped after
`QueryFilterBypassRatchetTests`: a pinned set that may only shrink, an explicit per-entity allow-list
with a written justification for each group, and a **throw** (never a skip) when the source root
cannot be resolved.

Measured today: **322 `DbSet<T>` declarations, 29 with no reference outside `Models/`, `Data/` and
`Migrations/`** — the same 29 names the prior audit found, over a week in which 16 new entities
landed without adding one. Two anti-vacuous-pass guards (`declared > 200`, `blob > 1 MB`) stop a
wrong source root reporting green. A second test fails if a pinned name *gains* a consumer, so a
stale pin cannot quietly stop guarding.

**Two corrections I had to make to my own guard, both found by its own self-tests:**

1. **A mention in a comment counted as a consumer.** Writing the refusal message in §3.3 — which
   names `LeaveAccrualRule` while explaining that it is the year-end engine's data model — made the
   scan treat that entity as consumed and silently drop it out of the pinned set. The blob now has
   comments stripped before matching. This matters more than a scan detail: a guard that exists
   *because writing something down is cheaper than wiring it* must not be satisfiable by writing
   something down.
2. **The scan could invent an orphan.** An entity ending in `y` pluralises to `ies`, so
   `RoleCompetency` is **not** a substring of `db.RoleCompetencies`. A name-only scan would accuse
   any such entity reached solely through its accessor — a false accusation, the one direction this
   guard must never fail in. The `y → ies` form now counts as a reference. Re-measured with the rule
   in place the orphan set is unchanged at 29, so it costs nothing today and protects the next `…y`
   entity somebody adds.

`DataProtectionKey` is pinned but explicitly annotated as **not debt**: it is the ASP.NET Core
data-protection entity, reached by the framework through `IDataProtectionKeyContext` and never named
in application code. It is pinned rather than allow-listed away so the number reconciles by name with
the audit that produced it. Deleting it would invalidate every issued auth cookie.

### 5.2 `EntityName` validation — the registry and the 400
`Application/Approvals/ApprovalEntities.cs` (new) and `Controllers/ApprovalWorkflowsController.cs:45-56`, applied at `:60` and `:76`.

Before this, the entire processing on the write path was `Clean()` — a trim. Any string was accepted.
`ApprovalEntities.ProducersWithEvidence` names the **four** entities that have a producer, each with
the producing call site written beside it:

| Entity | Producer |
|---|---|
| `LeaveRequest` | `LeaveService.cs:1484` |
| `EmployeeChangeRequest` | `EmployeesController.cs:2464` |
| `ManpowerRequisition` | `RecruitmentService.cs:69-80` via `RequisitionsController.cs:136` |
| `Timesheet` | `TimesheetService.cs:240` |

`Create` and `Update` both refuse anything else with a 400 carrying `code`,
`entityName`, `validEntities` and, for the four known-dead ones, a message saying **what actually
governs that decision** instead — so a client who had a transfer chain configured is told where
transfer approval really lives rather than only that they were wrong.

Two tests keep the registry honest in both directions: one asserts a producer entity is *not*
refused (without it the theory would pass on a controller that rejected everything), and
`EverySeededDefaultApprovalWorkflow_NamesAnEntityWithAProducer` reads the seeders' own declarations
by reflection so seed-versus-producer drift fails on the change that introduces it.

---

## 6. What I did not do, and why

Reported by name so nothing here is mistaken for covered.

**`WpsMolCode` and `SifEnabled` — deliberately not wired, and not yet refused.** I was ready to map
`WpsMolCode` onto the WPS/SIF header's `EstablishmentId` and stopped: the contract at
`CountryPackContracts.cs:192` documents that field as *"KSA: Mudad employer ID; UAE: MOHRE
establishment; QA: CR number"*. The UI hint calls `WpsMolCode` a *"7-character Ministry of Labour
establishment code"*, which is the **UAE** meaning; for KSA the field would be the Mudad employer ID,
which is plausibly the `WpsAgentId` already used. The intended behaviour is genuinely ambiguous
between two jurisdictions, and this is the money/statutory path — a wrong consumer here puts the
wrong establishment on a wage file. I refused to invent it. I also did not add a 400: these two
fields share one payload with eighteen live ones in `SetupSettingsController.UpsertGCCSetting`, and
the Saudi Compliance screen posts the whole record, so refusing a stored value would brick the GCC
settings save for any pilot tenant that already has a MOL code. Doing this properly means removing
the inputs from `SaudiComplianceConfig.tsx` in the same change as the refusal.

**Ramadan reduced hours — not wired, not refused, and the register is out of date here.** Ramadan
reduced hours *are* implemented, from the KSA statutory rules service
(`AttendanceService.cs:1025-1028`, `_ksaWorkingHours.ResolveDailyAsync`). What is dead is the GCC
*toggle*: `RamadanHoursEnabled` and `RamadanReducedHoursPerDay` cannot enable, disable or resize it.
The honest fix is to refuse the pair and say so on the screen, blocked by the same shared-payload
problem as above.

**Geofence (`Location.GeofenceRadiusMeters`) — not wired, on risk grounds.** The wire is clear
(haversine against the employee's location in the punch intake, and the mobile rejection string
already exists in both languages at `mobile/src/config/i18n.ts:108`). What is not safe mid-pilot is
the other half of the rule: missing coordinates must **not** auto-pass or the control is bypassable
by turning GPS off — and failing them closed would stop web clock-ins that carry no coordinates at
all. That needs a product decision about exception handling, not a wiring change.

**`RehireEligible`, `AttendanceLockPeriod`, the four `AttendancePolicy` fields, `LoanPolicy`,
`LeaveRequest.ReturnDate`, `OvertimePolicy.RamadanReducedHoursPlaceholder`** — verified dead,
untouched. The first two need new endpoints/UI, the `AttendancePolicy` fields need a CRUD screen that
does not exist, and the last three have no write path at all, so they cannot currently lie to anyone.

**The 29 orphan entities** are pinned, not removed. Deleting them touches every module at once and at
least four (`EmployeeDependent`, `CandidateDocument`, `PerformanceRatingScale`/`Option`) need a
product decision first.

**Frontend forms for the two 501'd endpoints.** `UserManagementPage.tsx` still renders a "New
Delegation" form and an "Approval Authorities" tab whose writes now return 501 with an explanatory
message. Removing them is correct and I did not do it: that file is the most likely collision point
with the agent building the tenant admin surface. **Flagged for whoever owns that file.**

---

## 7. Evidence

### 7.1 Fail before / pass after — both ways

A "does not compile" is weak evidence, so I wrote a `BeforeProbe` whose every assertion asserts the
**defect**, and ran it on both trees. It is not committed to the branch.

**On `integration/wave6` (`6d7905f`), unmodified — all 12 defect assertions PASS, confirming every
defect is present:**

```
Passed!  - Failed: 0, Passed: 12, Skipped: 0, Total: 12 - Zayra.Api.Tests.dll (net8.0)
```

**On `fix/config-consumers`, the same probe — all 12 FAIL, confirming every defect is gone:**

```
Failed Zayra.Api.Tests.BeforeProbe.BEFORE_AttendancePolicyCode_IsIgnored_AlphabeticalPolicyWins
   Expected resolved.Code to be "ATT-STD" ... because DEFECT: the employee's assigned policy code
   is never read, but "ZONE-NIGHT" ... differs near "ZON" (index 0).

Failed Zayra.Api.Tests.BeforeProbe.BEFORE_LeavePolicyApprovalWorkflowId_IsIgnored_TenantDefaultRoutes
   Expected approval.WorkflowId to be {a8da9668-…} because DEFECT: the policy's pinned workflow is
   never read, but found {0b658a56-…}.

Failed Zayra.Api.Tests.BeforeProbe.BEFORE_ContractTemplateMergeFields_AreEmittedLiterally
   Expected contract.ContentHtmlEn "<p>This agreement is between the Company and Aisha Rahman.</p>"
   to contain "{{employee_name}}" because DEFECT: the template body is copied verbatim.

Failed BEFORE_AnyEntityNameIsAccepted_NothingIsRefused(entityName: "OvertimeRequest")
Failed BEFORE_AnyEntityNameIsAccepted_NothingIsRefused(entityName: "PayrollRun")
Failed BEFORE_AnyEntityNameIsAccepted_NothingIsRefused(entityName: "EmployeeDraft")
Failed BEFORE_AnyEntityNameIsAccepted_NothingIsRefused(entityName: "EmployeeTransferRequest")
Failed BEFORE_AnyEntityNameIsAccepted_NothingIsRefused(entityName: "CompletelyMadeUpEntity")
   Expected a <System.ArgumentNullException> to be thrown, but no exception was thrown.
   [it is now refused before tenant resolution is reached]

Failed BEFORE_SeededDefaultWorkflowsIncludeTwoEntitiesWithNoProducer
   Expected ... to be a collection with 2 item(s) because DEFECT: every provisioned tenant gets two
   approval workflows that route nothing, but found an empty collection.

Failed BEFORE_CarryForwardCapIsStored_AndNothingEverReadsIt
   Expected type to be CreatedResult because DEFECT: a carry-forward cap is accepted with a 201,
   but found BadRequestObjectResult.

Failed BEFORE_RecurringHolidayIsStored_AndNothingEverExpandsIt
   Expected type to be CreatedResult because DEFECT: 'recurring' is accepted with a 201,
   but found BadRequestObjectResult.

Failed BEFORE_ApprovalDelegationAndAuthorityWrites_AttemptToStore_RatherThanRefuse
   Expected a <System.Exception> to be thrown because DEFECT: the write is attempted, not refused,
   but no exception was thrown.

Failed!  - Failed: 12, Passed: 0, Skipped: 0, Total: 12
```

The contract line is the clearest of them: the same template that produced
`"between the Company and {{employee_name}}"` on the base now produces
`"between the Company and Aisha Rahman."`.

### 7.2 Full suite, reconciled by name

| Run | Tree | Load at start | Test procs at start | Result |
|---|---|---|---|---|
| Baseline | `integration/wave6` `6d7905f`, pristine | 13.79 | 0 | **2446 passed / 0 failed**, 2m43s |
| Candidate | `fix/config-consumers` | 17.57 | 0 | **2477 passed / 0 failed**, 1m28s |

Both runs met the contention bar: load under 18 and zero `testhost`/`vstest.console` processes at
start, confirmed by `uptime` and `ps ax -o command= | grep -E 'testhost|vstest.console'` read row by
row in the same shell invocation that launched the run. Load at end: 73.05 (baseline) and 14.81
(candidate) — other agents were active throughout, which is why each run was gated on a polled
window rather than launched on demand.

**Reconciliation by name** (parsed from the two `.trx` files, not from the totals):

```
baseline: 2446   candidate: 2477   delta: +31
baseline failures:  []
candidate failures: []
REMOVED   (in baseline, not in candidate): 0
REGRESSED (passed before, not now):        0
ADDED:                                     31
```

The 31 added tests are exactly the two new files — 27 in `ConfigurationConsumerTests` and 4 in
`OrphanEntityRatchetTests`. No pre-existing test was removed, renamed or regressed.
`TenantProvisioningTests.TenantProvisioning_InstallsDefaults_Idempotently` is in both runs and passes
in both: its assertion was edited, not replaced, so the name reconciles.

### 7.3 Gate output

**Migration gate** (the two-step form):

```
$ dotnet tool restore --tool-manifest dotnet-tools.json
Tool 'dotnet-ef' (version '8.0.11') was restored.
Restore was successful.

$ dotnet tool run dotnet-ef migrations has-pending-model-changes
Build succeeded.
No changes have been made to the model since the last migration.
```

This change adds **no migration and alters no schema**, so the additive requirement (no `DropTable`,
no `DropColumn`, no NOT NULL column without a default on an existing table) is met trivially. The
`DataProtectionKey`, `LeaveAccrualRule`, `ApprovalPolicyStep` and the other 26 orphan tables are left
in place on purpose.

**Frontend gates** (`LeavePage.tsx` was touched, so both were required):

```
$ npx tsc --noEmit
TSC_EXIT=0

$ npx next build
   ...
   ○  (Static)   prerendered as static content
   ƒ  (Dynamic)  server-rendered on demand
NEXT_EXIT=0
```

`node_modules` was absent in the worktree and was installed with `npm install` before running these.
`npm run lint` is not runnable in this repo, as documented.

**No ratchet was suppressed, weakened or re-pinned.** `QueryFilterBypassRatchetTests`,
`ExecutionStrategyLintTests`, `BypassLintTests`, `EntityScopeResolutionRatchetTests`,
`PiiLoggingLintTests`, `StartupContractLintTests` and `DemoLeaveSeedLintTests` all pass unchanged in
the candidate run.

---

## 8. Files touched — for the agent building client-facing configurability

**Backend, new**
- `backend-dotnet/Zayra.Api/Application/Approvals/ApprovalEntities.cs`
- `backend-dotnet/Zayra.Api/Infrastructure/Compliance/ContractMergeFields.cs`
- `backend-dotnet/Zayra.Api.Tests/Security/OrphanEntityRatchetTests.cs`
- `backend-dotnet/Zayra.Api.Tests/ConfigurationConsumerTests.cs`

**Backend, modified**
- `Application/Approvals/IApprovalRouter.cs` — added `ResolvePinnedAsync`
- `Infrastructure/Approvals/ApprovalRouter.cs` — implemented it
- `Infrastructure/Leave/LeaveService.cs` — one call site in `SubmitRequestAsync`
- `Infrastructure/Attendance/AttendanceService.cs` — `ResolveAttendancePolicy` + new `ResolveByOrgTier`
- `Controllers/ApprovalWorkflowsController.cs` — the entity guard
- `Controllers/AccessController.cs` — four write methods now refuse
- `Controllers/Compliance/ContractsController.cs` — the merge step in `Create`
- `Controllers/Leave/LeavePoliciesController.cs` — carry-forward refusal
- `Controllers/Leave/HolidayCalendarController.cs` — recurrence refusal
- `Infrastructure/Seed/TenantProvisioningBundle.cs` — two seeds removed
- `Infrastructure/Seed/AuthSeeder.cs` — two workflow seeds and two demo approval rows removed
- `backend-dotnet/Zayra.Api.Tests/TenantProvisioningTests.cs` — the seeded-defaults assertion

**Frontend, modified**
- `frontend/src/views/LeavePage.tsx` — carry-forward input removed; recurring checkbox and badge
  removed; both form initialisers pinned to the neutral value

**Not touched, but flagged:** `frontend/src/views/UserManagementPage.tsx` (delegation + authority
forms) and `frontend/src/views/SaudiComplianceConfig.tsx` (WPS MOL code, SIF, Ramadan).

**No migration was added** — this change alters no schema, so the additive requirement is met
trivially. `dotnet-ef migrations has-pending-model-changes` reports *"No changes have been made to
the model since the last migration."*

**No file was deleted:** `git diff --diff-filter=D --name-only integration/wave6 fix/config-consumers`
is empty.

**No ratchet was suppressed, weakened or re-pinned.** No `ScopedBypass` was added and
`QueryFilterBypassRatchetTests`' pinned counts are untouched.
