# CMP evidence (local, disposable stack)

The code is `feat/merit-planning`. Evidence was refreshed after the final round at `321693b7` (code); only this
document, `CMP_DESIGN.md` and the screenshots changed after that. No real customer data was used, and every figure is synthetic.

**Stack**

| Component | Detail |
|---|---|
| PostgreSQL | `cmp-pg` (16-alpine) on 127.0.0.1:55513, database `kynex_cmp`, recreated before each run |
| API | `Zayra.Api.dll` on :5213. Migrations ran on start; `/health/ready` reports `pendingMigrations: 0`. `EmployeeEffectiveChanges:ScheduleInterval` is 15 s |
| Web | `next dev -p 5313` |
| Browser | Google Chrome (channel `chrome`), headed, 1440×1000; the last step resizes to 390×844 |

**Tenants and roles**

| Tenant | Tenant ID | Users |
|---|---|---|
| `cmp-alpha` | `475eef12-5a7e-46ac-a61b-c8125f029c37` | Hana (HR Manager), Farah (Finance Approver), Mona (Manager; employee login made through the invitation flow) |
| `cmp-beta` | `aabf9276-537d-4da3-b44e-7df29efa0d8e` | Used only to prove isolation |

**Records**

| Record | ID |
|---|---|
| Cycle | `911d1c29-b6b8-401b-a6c5-3cb400bc1833` (Applied) |
| Worksheet | `f0e00030-46c2-40ce-bdc5-9bd6b39e2498` (revision 2, Approved) |
| Approval requests | `e745e792-…` (Rejected), `5e21cbe9-…` (Approved) |
| Change requests | `8364c8ea-…`, `d0bfeca9-…`, `9848f1b4-…`, `4e83e67d-…` (all `ApprovedApplied`, effective 2026-10-01) |
| Benchmark set | `13fccc0b-…` (`is_synthetic`, 2 rows) |

## A. Headed real-backend journey

**Run:** 2026-10-11, from 21:58:25Z to 22:01:16Z (UTC), on the final-round code, recorded in `journey-steps.jsonl`. The
spec is `frontend/e2e/compensation-journey.spec.ts` and the seed is `frontend/e2e/bootstrap/compensation-seed.ts`. It
passed (seed + journey, 3.4 min); it had passed 4 times before. The awards were applied by the production scheduler
with the app's own DI, so the merit effective-date gate ran and let all 4 eligible awards through. The journey's effective date is the 1st of
the current month and no payroll run exists before approval, so no retro acknowledgement is asked for.

| ID | CMP | Step | Expected | Actual | Screenshot |
|---|---|---|---|---|---|
| J-01 | 01, 04 | HR creates a draft cycle | Draft in SAR; planning is blocked until there are guidelines, budgets and an AED rate | As expected; the blockers are listed | j01-cycle-draft-blockers.png |
| J-02 | 01, 04 | Guideline, budget and FX saved | Pool = 2 % of 27,120.00 = 542.40. Before the AED rate exists the pool counts only 21,000.00 (= 420.00) | 420.00, then 542.40; the rate shows who entered it | j02-cycle-configured.png |
| J-03 | 06, 07 | Import the SYNTHETIC CSV and map Engineering L1 | Provenance stored; synthetic banner shown; stale label shown | As expected; 1 of 2 rows mapped | j03-benchmark-synthetic.png |
| J-04 | 01 | Open planning | One worksheet for Mona; the late joiner is ineligible; Mona herself is uncovered | As expected | j04-planning-open.png |
| J-05 | 02, 05 | Manager proposes 3 / 3 / 6 / 3 % | Total 1,053.60; Omar is outside the guideline; the AED line is paid in AED; the synthetic chip shows | As expected | j05-worksheet-built.png |
| J-06 | 02, 09 | Submit without justifications | Refused, with the out-of-guideline and overspend blockers | Refused | j06-submit-refused.png |
| J-07 | 03 | Submit with justifications | The worksheet locks and the approval is flagged over budget | Locked | j07-submitted.png |
| J-08 | 03, 09 | HR rejects in Approvals, with a comment | The worksheet is Returned at revision 2 | 200 from `/decisions` | j08-approvals-returned.png |
| J-09 | 03 | Manager sees the comment, sets Omar to 5 % and resubmits | Revision 2 is submitted | As expected | j09-revised-resubmitted.png |
| J-10 | 03, 09 | Manager tries to approve her own worksheet (API) | Refused | HTTP 400 maker-checker | — |
| J-11 | 03 | HR Manager approves, then Finance Approver approves | Two steps decided by two people; Approved | As expected | j11-finance-approved.png |
| J-12 | 03 | HR sends the cycle to review; Finance approves it | The checker is a different person | Approved | j12-cycle-approved.png |
| J-13 | 08 | HR schedules the awards | 4 approved, effective-dated changes | 4 | j13-awards-scheduled.png |
| J-14 | 08 | Apply again (API) | Still exactly 4 | 4 | — |
| J-15 | 08 | The effective-date job runs | All 4 are `ApprovedApplied`, exactly once; the pool stays frozen | 4 in pay of record | j15-applied-in-pay-of-record.png |
| J-16 | 08 | Employee history | Scheduled, then Applied | As expected | — |
| J-17 | 08 | Payroll for 2026-10 (`PayrollController.Process`) | Ali's basic = 6,180.00 on one slip | run 201, process 200, basic 6180 | — |
| J-18 | 08 | Payroll screen | The run is listed | Screenshot | j18-payroll.png |
| J-19 | 09 | Tenant B asks for the cycle (flag on for both tenants) | 404 | 404 | — |
| J-20 | all | Arabic | RTL, translated | `dir=rtl` | j20-arabic-rtl.png |
| J-21 | 02 | 390 px phone | No horizontal page scroll | overflow 0 px | j21-mobile-390.png |

**About the time shift.** The real clock cannot be moved, so the effective date is set to the 1st of the current month
(2026-10-01). The award is therefore already due, and the production `EffectiveChangeScheduler` and job apply it. The
future-date path (not due → due → applied once) is proven in `CompensationLifecycleTests` using the job's own
`ProcessAsync` with the tenant-local date.

## B. Mocked Browser Pilot

The spec is `frontend/e2e/compensation.spec.ts` with `playwright.compensation.config.ts`; all 9 tests pass.

| Area | Tests | Screenshots |
|---|---|---|
| Manager view | The manager sees only their own worksheet. Saving sends every line and the justification. A server refusal is shown verbatim. | cmp-manager-* |
| HR view | One next action, budgets with definitions, and FX provenance. | cmp-hr-cycle |
| Benchmarks and equity | The synthetic banner; equity hides small groups; the formulas are shown. | cmp-benchmarks-synthetic, cmp-equity |
| Arabic and mobile | Arabic RTL; no horizontal page scroll at 390 px. | cmp-manager-ar-rtl, cmp-manager-390 |
| Accessibility | axe (WCAG 2 A/AA) finds no serious or critical violation. | — |
| Withdraw one award (final round) | Only a scheduled award offers Withdraw (an award in pay offers Reverse); a reason is required and sent. | — |
| Coverage, retro, reopen (fix round) | HR assigns an uncovered employee (only editable primary worksheets are offered), opens a worksheet for a manager who has none, sees the retro cost of a past date, and reopens planning only with a reason. | cmp-coverage-retro |

## C. Backend on real PostgreSQL (`Zayra.Api.Tests/Compensation`, 45 tests, plus 4 salary-import tests)

| Test | CMP |
|---|---|
| `CompensationGoldenReconciliationTests.GoldenCycle_…` | 01, 02, 04, 05: every line, department, total and the FX block, against a hand-computed table |
| `…Overspend_IsRefusedWithoutJustification_…` | 02, 09 |
| `…OutOfGuideline_NeedsAJustification` | 02 |
| `…AnAwardTheSalaryRecordWouldRefuse_IsBlockedAtPlanning…` | 02, 08 |
| `CompensationLifecycleTests.RejectReturns_…` | 03: return, revision, two-step approval, lock, audit |
| `…SeparationOfDuties_…` | 03, 09: requester, own pay, missing approve permission, earlier step, HR editing own pay, cycle maker-checker |
| `…ManagersOwnLine_IsUncovered_…` | 03: self-award prevention |
| `…UnauthorisedCallers_AreRefused` | 09, plus the `[HasPermission]` reflection check |
| `…Apply_IsIdempotent_RefusesOverlaps_JobAppliesOnce_PayrollPaysTheNewBasicOnce_ScenarioNeverReachesPayroll` | 08, 09: overlap queue, apply ×3, sealed baseline, job once, frozen pool, payroll for 11/12/1, arrears 0, cancel refused after effect, reversal through approval, scenario never paid |
| `…CancellationBeforeTheEffectiveDate_…` | 08, 09 |
| `…AStaleWorksheetVersion_IsAConflict…` | 03: optimistic concurrency |
| `…TenantB_AndCompanyB_CannotReachTheCycle` | 09: tenant filter and company write guard |
| `CompensationDataIntegrityTests` (7) | Salary-row guard (sequential and a parallel race), the single-writer replace, pre-flight message and query, the one-award index, benchmark import (provenance, synthetic, validation, missing/stale/out-of-scope, carry-forward, retire), XLSX import, equity suppression and the hand-computed gap |
| `CompensationGuidelineBoundaryTests` (7, pure) | P1-1: entered percent at min/max with odd-cent and FX bases; amount half-cent tolerance; .xx5 tie; stored percent stays a percent |
| `CompensationFixRoundTests.ResignedTerminatedOrRegradedAfterApproval_TheAwardIsRefusedAtApply…` | P1-2 |
| `…ManagerRemovedMidReview_WorksheetReturns…` · `…ReassignedAfterPlanningOpened_IsUncovered…` | P1-3: approval closed → Returned with audit; HR open/assign/close; uncovered owner |
| `…AManagerSeesOnlyTheMarketPosition…` | P2-5 |
| `…HrWhoEditedAManagersWorksheet_CannotDecide…` | P2-6 |
| `…BudgetsAndFxLockOnceSubmitted_ReopenPlanning…_CancelDuringReview` | P2-7 |
| `…APastEffectiveDate_ShowsTheRetroCost…` · `…AReversalIntoALockedPayrollMonth…` | P2-9 |
| `…ParallelWorksheetSaves…_ParallelApply…` | P2-12 |
| `…APaddedXfdSheet_OrTooManyRows…` | P2-4 |
| `…SalaryImport_DeduplicatesInMemory_AndIsAtomic…` · `…TheSalaryRowConflictParser_ReadsPostgresDetail` | P2-8 |

| `CompensationFinalRoundTests.OnNoticeTerminatedOrRegradedAfterApply_TheAwardIsCancelledOnTheEffectiveDate_HrIsNotified_TheRestApply` | leaver after apply (job gate) |
| `…TheGate_IgnoresChangesThatDidNotComeFromAMeritCycle_AndIsRegisteredByAddCompensation` | gate scope and wiring |
| `…HrWithdrawsOneScheduledAward_TheRestStayScheduled_AnAppliedAwardMustBeReversedInstead` | single-award withdrawal |
| `…APayrollMonthRunBetweenApprovalAndApply_NeedsAnApproversAcknowledgementAtApply` | retro at apply |
| `…EveryoneWhoEditedTheRevision_IsBarredFromDecidingIt_NotOnlyTheLastEditor` | editor SoD |
| `…APercentWithMoreThanFourDecimals_IsA400_AndNothingIsSaved` | percent precision |
| `…AnXlsxPartOverTwoMillionCharacters_IsRefused` | XLSX part cap |
| `PayrollSalaryImportPostgresTests` (4) | import validation, moved from InMemory |

## D. Suites at the final-round SHA (`321693b7`)

| Command | Result |
|---|---|
| `dotnet test …Zayra.Api.Tests.csproj` (full) | 6,939 passed, 1 failed (20 m 49 s, machine shared with other suites) |
| the failure re-run alone: `SelfieHardeningRefusalTests.Item3_TwentyConcurrentPolicyReads…` | passed (a wall-clock timing test; unrelated to CMP) |
| `--filter Compensation\|PayrollModuleTests\|PayrollSalaryImport\|Approval\|EffectiveChange` | 551 passed |
| platform gates (TableBudget, Orphan, ModuleCatalog, NavigationPageGate, LegacyRoleGate, RawSql, CompanyScope, ApprovalProducer, DestructiveMigration, BypassLint, ExecutionStrategyLint, ConfigurationConsumer, PrivilegedMfa, EmployeeSnapshotMasking) | 293 passed |
| `dotnet ef migrations has-pending-model-changes --context ZayraDbContext` | no changes |
| `npx tsc --noEmit` · `npm run test:unit` · browserless | clean · 228 passed · 199 passed, 1 skipped |
| mocked spec · real headed journey | 9 passed · 2 passed |
