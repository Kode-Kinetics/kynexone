# Performance module — making four recorded decisions real

Branch `fix/performance-decisions`, base `develop @ d730313`.

All four defects share one shape: **a decision that records a word and does nothing**. The bar applied
throughout is the codebase's own: *a decision recorded must change something a user can observe, or be
refused* — and where it must be refused, refused with a machine-readable code, never answered 200.

**No migration.** Every fix routes through fields and tables that already exist and are already read.
That was deliberate: Evostel is live, and the additive-migration rule is easier to satisfy by not needing
one. `dotnet-ef migrations has-pending-model-changes` confirms the model is unchanged.

---

## Evidence method

The fix changes controller signatures (a new constructor argument, two new endpoints, two new optional
request fields), so the final test file cannot compile against `develop`. Fail-before was therefore
captured with a **probe written against the develop-era surface only** — same seeds, same assertions,
no new API — run with the four Performance controllers reverted to `d730313`
(`git checkout develop -- backend-dotnet/Zayra.Api/Controllers/Performance/`). Ten probes, ten failures.
The probe was then deleted and the branch restored; the committed suite
(`backend-dotnet/Zayra.Api.Tests/PerformanceDecisionsPostgresTests.cs`, 14 tests) asserts the same facts
against the fixed code.

Both suites run on **real Postgres** (`[Collection("Integration")]`), because the scope fix depends on
the company query filter and on `DataScopeService` materialising an org-wide caller's employee universe —
neither behaves correctly on the in-memory provider.

---

## Defect 4 — `ImplementationQueue` leaked every company's salary increments and bonus amounts

**FIXED FIRST: this was a live data leak.**

### Before
`RecommendationsController.ImplementationQueue` (develop lines 23–41) filtered on `TenantId` only. It had
no `IDataScopeService` call at all, while its sibling `ListIncrements` (lines 45–54) did. None of
`IncrementRecommendation`, `PromotionRecommendation` or `BonusRecommendation` is company-scoped — they are
`ITenantOwned` only — so the EF company query filter never touched them either. A Payroll Manager scoped
to one company read every sibling company's approved new salaries and bonus amounts off this one endpoint.

### Now
The resolved scope is applied to all three queries, materialised once so the three lists cannot disagree
about who the caller may see. For a company-scoped caller `DataScopeService.ApplyCompanyBoundaryAsync`
turns "Organization" into an explicit in-company employee id set, which is the only containment these
rows have.

### Observable consequence asserted
`ImplementationQueue_DoesNotLeakAnotherCompanysSalaryIncrementsAndBonusAmounts` — two companies in one
tenant, each with an increment, a bonus and a promotion carrying **different** values. Company A's Payroll
Manager (v2 `companies` claim for A) must see A's 11,111 and 2,222 and must not see B's 99,999 or 88,888.
**Non-vacuous by construction**: the test fails if A sees nothing, because it asserts A's own three rows
are present and `total == 3` before asserting B's are absent.

### Fail before
```
Did not expect raw "{"increments":[{"id":"8affd90f-…","type":"SalaryIncrement","employeeId":8,
"employeeName":"Probe Beta","effectiveDate":"2026-10-01","amount":99999.00,…},
{"id":"b0265703-…","employeeId":7,"employeeName":"Probe Alpha","amount":11111.00,…}],
"promotions":[],"bonuses":[{"employeeId":7,"employeeName":"Probe Alpha","amount":2222.00,…},
{"employeeId":8,"employeeName":"Probe Beta","amount":88888.00,…}],"total":4}"
to contain "99999" because Company B's new salary must not be readable here.
```
That is the leak itself, printed: Company A's payroll manager holding Company B's salary and bonus figures.

### Pass after
```
Passed!  - Failed: 0, Passed: 2600, Skipped: 0, Total: 2600, Duration: 1 m 35 s - Zayra.Api.Tests.dll
```

---

## Defect 1 — an appeal permanently froze an employee out of pay

### Before
`ReviewsController.SubmitAppeal` set `review.Status = "Appealed"` and **nothing ever moved it out**.
`RecommendationsController.ResolveSubjectAsync` (line 254) refuses every increment, promotion and bonus
unless the review is `Published` or `Acknowledged`. `RespondToAppeal` wrote `appeal.Status` and stopped —
so `Upheld` and `Rejected` were behaviourally identical, and **an appeal HR rejected still locked the
employee out of compensation permanently**. `CyclesController.Advance` (line 222) explicitly lets a cycle
publish with reviews left in `Appealed`, so the cycle moved on and the review never could.
`respondToAppeal` existed in `frontend/src/api/performance.ts` with **zero call sites**, so the resolution
path was unreachable from the product even in principle.

### Now — the resolution path
| Decision | Review lands in | Compensation | Meaning |
|---|---|---|---|
| **Rejected** | the pre-appeal status — `Acknowledged` if `AcknowledgedAt` is set, else `Published` | **permitted again** | the published result stands; the employee returns to exactly where they were |
| **Upheld** | `FinalApproval` | still blocked, with a guaranteed exit | the published outcome is **withdrawn for revision** — an increment must not be raised against a score the employer has just accepted was wrong |

The pre-appeal status needs no new column: an appeal can only be submitted from `Published` or
`Acknowledged`, and `AcknowledgedAt` already records which. Deriving it means the appeals Evostel already
has open resolve correctly with no backfill.

`PublishedAt` / `AcknowledgedAt` are left intact on an upheld appeal — they are the history of the issue
being appealed, not a claim about the current state. `Publish` reads `PublishedAt` to recognise a
**re-issue** and waives the cycle gate for it. Without that, an upheld appeal would park the review in
`FinalApproval` with no reachable exit once the cycle closed — the same permanent freeze, one status
along. The waiver is exactly scoped: the only path to `FinalApproval` with `PublishedAt != null` is an
upheld appeal, because `CyclesController.Advance` only promotes reviews sitting at `ManagerReviewComplete`.

Also added: `GET /api/performance/reviews/appeals` (scoped, HR-only), a required written response on the
decision, a scope check on the appeal's employee, an audit line, and `reviewStatus` /
`compensationPermitted` / `nextStep` in the response. Frontend: an **Open appeals** panel at the top of
Team Reviews with Uphold / Reject, each explaining its consequence before it is taken.

### Observable consequences asserted
- `RejectedAppeal_ReturnsTheEmployeeToCompensationEligibility` — `CreateIncrement` is `Conflict` while the
  appeal is open, then `Created` after the rejection, and an `IncrementRecommendation` row exists.
- `UpheldAppeal_WithdrawsThePublishedOutcome_AndTheReIssueIsReachableAfterTheCycleMovedOn` — review →
  `FinalApproval`, compensation still refused, then **with the cycle forced to `Closed`** `override-score`
  + `publish` succeed and compensation is permitted again.
- `AppealDecision_WithoutRecordedReasoning_IsRefused` — and the review stays `Appealed` (no half-apply).
- `OpenAppeals_AreReachable_SoTheResolutionPathHasAnEntryPoint`.

### Fail before
```
Expected type to be Microsoft.AspNetCore.Mvc.CreatedResult because a REJECTED appeal must not lock the
employee out of pay, but found Microsoft.AspNetCore.Mvc.ConflictObjectResult.

Expected (db.AppraisalReviews…SingleAsync(r => r.Id == review.Id)).Status to be "FinalApproval" … because
an upheld appeal must move the review, not just the appeal row, but "Appealed" has a length of 8.

Expected type to be Microsoft.AspNetCore.Mvc.BadRequestObjectResult, but found OkObjectResult.
```

---

## Defect 2 — "terminate probation" terminated nothing

### Before
`ProbationController.HrDecision` (line 107) wrote `HrDecision` and nothing else. `grep -c "Employees"` on
the file was 0; there is no reader of `HrDecision` anywhere in the repository; the value was not validated,
so `"Confirmed"`, `"Terminated"` and `"Yes please"` were all accepted and all produced the identical
screen. A terminated probationer stayed `Active`, kept their login, kept their WPS payroll footprint, and
had no `EmployeeOffboarding` — so `/final-settlement` would refuse them (`not_a_leaver`) and
`POST /api/offboarding` would refuse them too once they were terminated by any other route.

### Now — routed into the offboarding domain that already works
`IEmployeeManagementService` is injected (optional-with-refusal, matching the repo's pattern) and each
decision does something a reader consumes:

- **Terminated** → `TerminateAsync(..., SeparationType: "ProbationFailure")`. One call, one transaction:
  employee → `Terminated`, `EmployeeStatusHistory` + lifecycle history written, the authoritative
  `EmployeeOffboarding` minted with `Status = "InProgress"` and `LastWorkingDay` = the probation end date
  (overridable), and the WPS payroll footprint deactivated. `"ProbationFailure"` was **already** in the
  closed separation vocabulary (`EmployeeManagementService.AllowedSeparationTypes`) and is what
  `PayrollController.ResolveTerminationReasonAsync` → `KsaEndOfServiceCalculator` reads.
  Gated on `employees.approve` — the same privilege boundary `EmployeesController` draws at
  `PATCH /employees/{id}/status`, because the separation type decides the gratuity.
- **Confirmed** → `Employee.ConfirmationDate = effectiveDate` and `ProbationEndDate` brought forward to
  the day before. `ProbationEndDate` is the field anything actually **reads**: `LeaveService.cs:608`
  refuses leave under a policy with `AppliesOnProbation = false` while the request falls on or before it,
  and the probation headcount (`EmployeesController.cs:3496`) is `ProbationEndDate >= today`. Both are
  inclusive, which is why the last probationary day is the day *before* confirmation takes effect.
  `ProbationEndDate` is only ever brought forward here, never extended.
- **Extended** → requires the new end date (400 otherwise) and writes it to the same field, so the leave
  gate and the headcount extend with it. The review returns to `Pending`: a further manager review is due.
- **Anything else** → 400 `invalid_decision` listing the allowed values, and nothing is written.
- **No separation service on the request** → 501 `probation_termination_unavailable`, not a 200.

`ManagerReview` now validates `Confirm/Extend/Terminate` too, and both actions gained the data-scope check
the other Performance controllers have.

Frontend: the three buttons now open a decision modal that states the consequence, takes the date each
decision needs, and reports back what actually happened (including "a 'ProbationFailure' separation was
raised — complete the offboarding checklist and final settlement in Offboarding").

### Observable consequences asserted
- `TerminateProbation_RaisesAnAuthoritativeProbationFailureSeparation_AndDeactivatesTheWpsFootprint` —
  `Employee.Status == Terminated`, exactly one `EmployeeOffboarding` with
  `SeparationType == "ProbationFailure"` / `Status == "InProgress"` / `LastWorkingDay ==` the probation end
  date, `EmployeePayrollProfile.WpsEligible == false`, an `EmployeeStatusHistory` row, review `Closed`.
- `TerminateProbation_WithoutTheSeparationPrivilege_IsRefused_AndRecordsNothing` — `Forbid`, zero
  offboardings, `HrDecision` still empty.
- `TerminateProbation_WithNoSeparationService_Refuses501_RatherThanRecordingAWord`.
- `ConfirmProbation_EndsTheProbationWindowTheLeaveRulesRead` — the probation-population count goes 1 → 0.
- `ExtendProbation_WithoutANewEndDate_IsRefused_AndWithOneExtendsTheWindow`.
- `ProbationHrDecision_RefusesAWordItCannotHonour`.

### Fail before
```
Expected (db.Employees…SingleAsync(e => e.Id == employee.Id)).Status to be "Terminated" … because Confirm
and Terminate must not produce the same screen, but "Active" has a length of 6.

Expected emp.ConfirmationDate to be <2026-09-21> because confirmation must record that the employee is
now permanent, but found <null>.

Expected type to be BadRequestObjectResult because any string is currently accepted as an employment
decision, but found OkObjectResult.

Expected type to be BadRequestObjectResult because an extension with no new date changes nothing
observable, but found OkObjectResult.
```

---

## Defect 3 — a failed PIP did nothing

### Before
`PIPController.UpdateStatus` (line 139) demanded a written reason for `TerminationRecommended` and then
routed it nowhere: the word went into a column, the PIP dropped off the "Active" list, and no screen,
queue or notification ever raised it with anyone. `PIPCheckIn.Outcome` had **no read site in the
repository at all** — the monitoring record a PIP exists to produce was write-only.

### Now
The product's own position, printed on the screen, is that this is a recommendation and "HR and leadership
must make the final employment decision". So **nobody is terminated automatically**. Instead:

- `GET /api/performance/pip/termination-queue` (scoped) returns the PIPs whose recommendation is still
  outstanding, rendered as a banner on the PIP tab. The queue is **derived, not stored**: an entry leaves
  it as soon as that employee has a non-cancelled `EmployeeOffboarding` or is no longer employed. Nothing
  to backfill, nothing to keep in sync, and no second termination path invented alongside the working one.
- `PIPCheckIn.Outcome` gains three readers: the detail payload, the list (`latestCheckInOutcome`,
  `checkInCount`, rendered on each row), and the guard below.
- Closing a PIP as `TerminationRecommended` or `Failed` with **zero recorded check-ins** is refused,
  409 `no_monitoring_evidence`. This is the conservative half of the rule: it does not second-guess the
  outcome — a deteriorating employee who then improves is real and common — only the absence of any
  monitoring at all.

### Observable consequences asserted
- `PipTerminationRecommendation_LandsOnTheHrQueue_AndClearsWhenTheSeparationIsRaised` — queue total 1,
  naming the employee; then a real `TerminateAsync` is run and the queue empties itself.
- `AdversePipClose_WithNoMonitoringEvidence_IsRefused` — and the PIP stays `Active`.
- `PipCheckInOutcome_IsReadBackOnTheList`.

### Fail before
```
Expected raw "[{"id":"…","employeeName":"Probe PIP",…,"status":"Active","hrNotes":"",…}]"
to contain "latestCheckInOutcome" because PIPCheckIn.Outcome has no read site anywhere — the monitoring
record is write-only.

Expected type to be ConflictObjectResult because an adverse close with zero check-ins has no monitoring
behind it, but found OkObjectResult.
```

---

## Product questions I had to answer myself

These are genuine product decisions. I took the conservative reading in each case and made it explicit in
code; each needs the product owner's confirmation.

**Q1 — What does an upheld appeal do to the review's scores?**
*Reading taken:* nothing automatically. The review is withdrawn to `FinalApproval` and HR must state the
correction through `override-score`, which already requires a written reason. The controller deliberately
does not adjust any number.
*Why:* auto-adjusting would invent a scoring rule the business has not specified, and an appeal can be
upheld on process grounds without the score changing at all.
*Alternative if the PO disagrees:* an upheld appeal could carry a revised score on the decision itself,
making it one act instead of two.

**Q2 — Should an upheld appeal keep compensation blocked?**
*Reading taken:* yes, until the corrected review is re-issued. An increment must not be raised against a
score the employer has just accepted was wrong. This is the one case where the block is correct — but it
is now bounded, because the re-issue is always reachable.
*Risk accepted:* if HR uphold an appeal and never re-publish, the employee is still blocked. That is now
visible (the review sits at `FinalApproval` in the Team Reviews list) rather than invisible, but a
follow-up SLA or reminder may be wanted.

**Q3 — Does terminating probation complete the separation, or only start it?**
*Reading taken:* it terminates — `Employee.Status = "Terminated"` with an `InProgress`
`EmployeeOffboarding`, identical to the canonical `/employees/{id}/terminate` command. HR then works the
existing offboarding checklist (assets, access revocation, final settlement) as they would for any leaver.
*Alternative:* route to `OffboardingController.Initiate` semantics instead (status `Offboarded`, notice
served, backfill requisition raised). I did not, for three reasons: probation termination in KSA is
immediate rather than notice-served; `Initiate` is inline in a controller with no injectable service, so
reusing it means extracting it (a refactor outside this module); and `TerminateAsync` is the transactional,
idempotent path with the partial-unique-index protection already tested.
**This is the single decision most worth confirming.**

**Q4 — Does terminating probation revoke the employee's login?**
*Reading taken:* **no, and this is a known gap I did not close.** Access revocation lives entirely in
`OffboardingController`'s private `RevokeEmployeeAccessAsync` and fires from the checklist tick, the
explicit revoke endpoint, or offboarding completion. A probation termination therefore leaves the login
alive until HR ticks "Access revoked" — **exactly the same as any direct `/terminate` today**. I chose
consistency with the existing command over duplicating revocation logic into the Performance module, but
the product owner should decide whether *any* termination should revoke immediately. If yes, the fix
belongs in `EmployeeManagementService.ChangeStatusAsync`, not here.

**Q5 — Which separation type for a probation dismissal *for cause*?**
*Reading taken:* this endpoint always writes `"ProbationFailure"`, which pays the full Art. 84 award. A
dismissal for cause is `"Article80"` (which forfeits the award entirely) and must be raised through the
offboarding screen, where the Art. 80 reason gate already applies. The termination modal says so.
*Why:* offering Article 80 from a probation screen without that gate is how an unevidenced forfeiture of a
statutory entitlement gets keyed.

**Q6 — Is confirmation effective today or at the scheduled probation end date?**
*Reading taken:* the effective date the caller states, defaulting to **today**. Confirming early ends
probation early — that is what "confirm" means — and because both readers of `ProbationEndDate` are
inclusive, the last probationary day is set to the day before. If the business intends confirmation to
always take effect at the scheduled end date, the default should flip.

**Q7 — Should an adverse PIP close require at least one check-in?**
*Reading taken:* yes, refused with 409 `no_monitoring_evidence`. A termination recommendation with no
recorded monitoring is an unevidenced decision, and it is the one that ends up in front of a labour court.
*Risk accepted:* this can block an HR user closing a legacy PIP that predates check-ins being used. The
remedy is to record one check-in describing what actually happened, which is the right outcome — but the
PO should know it is a new gate on an existing screen.

**Q8 — Should `TerminationRecommended` be able to raise the separation directly from the PIP screen?**
*Reading taken:* no. The screen already tells the user "this is a recommendation only — not an automatic
termination", so the queue routes the recommendation to a human and stops. Adding a one-click terminate
there would contradict the product's stated position.

---

## What I did not do

- **No new columns, no migration.** Everything derives from data that already exists.
- **No change to `Employee.Status` semantics**, `OffboardingController`, `EmployeeManagementService`, or
  anything outside `Controllers/Performance/` and its two frontend files. In particular I did not touch
  `Infrastructure/Leave`, `Infrastructure/Attendance`, `AuthSeeder` or `PlatformController`, which other
  agents own this run.
- **No ratchet touched.** No `IgnoreQueryFilters()` added, no `DbSet` added, no bare
  `BeginTransactionAsync`, no pinned count changed.


---

## Gates

| Gate | Result |
|---|---|
| Baseline full suite (`d730313`, before any change) | `Passed! - Failed: 0, Passed: 2586, Skipped: 0, Total: 2586, Duration: 1 m 15 s` |
| Fail-before probe (controllers reverted to `d730313`) | `Failed! - Failed: 10, Passed: 0, Skipped: 0, Total: 10` |
| Full suite after | `Passed! - Failed: 0, Passed: 2600, Skipped: 0, Total: 2600, Duration: 1 m 35 s` |
| `npx tsc --noEmit` | clean, no output |
| `npx next build` | exit 0 |
| `npx playwright test -c e2e/playwright.browserless.config.ts` | `12 passed` — includes the RTL logical-property ratchet and its pinned exception count |
| `dotnet tool run dotnet-ef migrations has-pending-model-changes` | `No changes have been made to the model since the last migration.` |
| `git diff --diff-filter=D --name-only d730313 fix/performance-decisions` | *(empty)* |

Neither `dotnet test` run used `--no-build`.

### By-name reconciliation, 2586 → 2600

**Removed: none.**

**Added: 14, all in the one new class `Zayra.Api.Tests.PerformanceDecisionsPostgresTests`:**

```
AdversePipClose_WithNoMonitoringEvidence_IsRefused
AppealDecision_WithoutRecordedReasoning_IsRefused
ConfirmProbation_EndsTheProbationWindowTheLeaveRulesRead
ExtendProbation_WithoutANewEndDate_IsRefused_AndWithOneExtendsTheWindow
ImplementationQueue_DoesNotLeakAnotherCompanysSalaryIncrementsAndBonusAmounts
OpenAppeals_AreReachable_SoTheResolutionPathHasAnEntryPoint
PipCheckInOutcome_IsReadBackOnTheList
PipTerminationRecommendation_LandsOnTheHrQueue_AndClearsWhenTheSeparationIsRaised
ProbationHrDecision_RefusesAWordItCannotHonour
RejectedAppeal_ReturnsTheEmployeeToCompensationEligibility
TerminateProbation_RaisesAnAuthoritativeProbationFailureSeparation_AndDeactivatesTheWpsFootprint
TerminateProbation_WithNoSeparationService_Refuses501_RatherThanRecordingAWord
TerminateProbation_WithoutTheSeparationPrivilege_IsRefused_AndRecordsNothing
UpheldAppeal_WithdrawsThePublishedOutcome_AndTheReIssueIsReachableAfterTheCycleMovedOn
```

### One existing test was edited, and why

`Security/SecurityAuditBatch2Tests.Pip_List_ScopedEmployeeSeesOnlyOwn` failed on the first post-change
run (`ArgumentNullException`) because `PIPController.List` now returns a row carrying the latest check-in
outcome, so the payload is no longer `List<PerformanceImprovementPlan>`. I introduced a **named**
`PipListItem` record rather than an anonymous type — precisely so the payload stays bindable — and
updated the test's cast. The scoping assertion it exists for is byte-for-byte unchanged, and I
**strengthened** it with `items.Should().NotBeEmpty(...)`: as written it would have passed vacuously on
an empty list.

### A note on the machine discipline

The first gated runner never started: its poll matched the literal string `dotnet test`, which appeared
in its own invoking shell's argv because the script was written with a heredoc in the same command. The
gate therefore counted itself forever. Rewritten to poll for live `testhost`/`vstest` processes, which
cannot appear in a runner's own argv, and invoked by path with no heredoc. Both full-suite runs then
started only with load < 18 and zero competing test processes.

### `develop` moved during this work

The base is `d730313`, as specified. `develop` has since advanced to `cb78f97` (another agent merged),
so `git diff --diff-filter=D --name-only develop fix/performance-decisions` today lists five files —
all of them files that *exist on the newer develop and not on this branch*, i.e. other people's
additions after my base, not deletions by me. Against the stated base the deletion list is empty. This
branch has not been rebased: the 2586 baseline was measured at `d730313` and rebasing would pull in
in-flight work from three other agents and invalidate that comparison.
