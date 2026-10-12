# ENG — evidence (local, privacy fix round 3)

| Item | Value |
|---|---|
| Production code under test | `6eb3d0c8` (fix round 3). The commit after it changes only this evidence and the design record. |
| Stack | Disposable. Postgres 16 `eng-pg` :55512 (container rebuilt from scratch for round 3, so the patched CHECKs exist), a fresh database migrated on boot through `20261011124500_AddEngagementSurveys`. API `dotnet run --no-launch-profile` :5212 (Development, email capture). `next dev -p 5312`. |
| Browser | Google Chrome (Playwright `channel: chrome`), **headed**, 1440×1000. The phone step runs at 390×844. |
| Tenants | `eng-acme`: one KSA company, 40 employees (36 in the audience and 4 managers), each with a real invited login. `eng-other`: isolation. Both have the `engagement` flag on. |
| Roles | `hr.director@eng-acme.test`: tenant Admin. Employees: Employee role in ESSOnly mode. Mona (`mgr-eng@…`): Manager role in ManagerPortal mode. `hr@eng-other.test`: Admin of the other tenant. |
| Journey run | 2026-10-11 21:27Z → 21:29:48Z UTC, fresh database. All 10 steps passed (an earlier attempt at load average ≈ 250 lost a typed title in J1 before hydration; the re-run on a reset database passed). Ids and per-step UTC times are in `journey.json`. |
| Survey / action | Survey `95eb54dc-3922-44e0-91d5-abbc176324db`. Action `d6fd0841-2ab7-49cd-bfa2-84ba93a31c1e`. |

## Journey steps (`frontend/e2e/engagement-journey.spec.ts`)

| Step | ENG | Expected | Actual | Screenshots |
|---|---|---|---|---|
| J0 Seed through the API only | 02 | World built, no SQL | 40 employees, 2 tenants | — |
| J1 Template built and published; anonymous survey created, previewed and launched | 01, 02, 03 | 36 eligible; the notice is verbatim; the survey opens with 36 invited | As expected | `J1-01…05` |
| J2 ACM-001 saves a draft, comes back, then submits; the API then reopens and resubmits | 04, 11 | Draft restored; thanks shown; both retries return 409 `survey_already_submitted` | As expected | `J2-01…04` |
| J3 33 more submit through the API | 04 | 34 of 36 submitted | 34 | — |
| J3b ACM-030 responds on a phone, in Arabic | 11 | Arabic notice; 0 px overflow; submitted | As expected, 0 px | `J3b-01…03` |
| J3c Results viewed while open; HR then closes the survey | 06, 07 | Only "Results appear when the survey closes on …" and the response rate (35 of 36); comments 409; then closed | `resultsAvailable=false`, comments 409, closed | `J3c-01…02` |
| J4 Results after close: breakdown, the Operations × Engineer filter, CSV export | 06, 07 | 97.2 %. Legal and Finance hidden (API reason `suppressed`), Operations 23. The intersection is hidden, explained in one plain sentence. CSV shows the same suppression and no comments | As expected; favourable 64.3, eNPS 17.1; `J4-export.csv` | `J4-01…03` |
| J5 HR creates an action; Mona records InProgress, then Done; the page is reloaded | 08 | Done, 3 progress entries, still there after reload | As expected | `J5-01…04` |
| J6 Results page in Arabic | 11 | `dir=rtl`; Arabic suppression sentence | As expected | `J6-01` |
| J8 Other tenant's HR, and an employee, ask for the results | 05, 06 | 404 and 403 | 404 and 403 | — |

## Automated tests (exact commands and results)

| Command | Result |
|---|---|
| `dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj` at 6eb3d0c8 | **Passed 6970, Failed 3** (24 m 7 s, host load average 60–100 from other agents): `ShutdownDrainTests.OnKestrel_…` (connection reset), `EmployeeWelcomeCodeMigrationPostgresTests` (lock-timeout bound) and `ALargeSurvey_IsDecidedQuickly` (11 s wall clock). Re-run alone: **16 passed, 0 failed** (the timing test: 0.58 s). |
| `dotnet test … --filter "FullyQualifiedName~Engagement\|FullyQualifiedName~CohortSuppression"` | Passed 77, Failed 1: the same timing test under load (8.7 s); alone it passes |
| Platform gates (`TableBudget`, `Orphan`, `ModuleCatalog`, `NavigationPageGate`, `LegacyRoleGate`, `RawSql`, `CompanyScope`, `PersonalDataLogging`, `ScopedBypass`, `ReportAccessPolicy`, `PostgresDateTime`) | Passed 179, Failed 0 |
| `dotnet ef migrations has-pending-model-changes` | No changes since the last migration |
| `cd frontend && npx tsc --noEmit` | 0 errors |
| `npm run test:unit` | 236 passed |
| `npx playwright test -c e2e/playwright.browserless.config.ts` | 199 passed, 1 skipped |
| `npx playwright test -c e2e/playwright.engagement.config.ts` (mocked) | 14 passed |
| `npx playwright test -c e2e/playwright.engagement-journey.config.ts` (headed Chrome, real backend, fresh container) | 10 passed |

## Fix round 3 (third privacy review) → tests

| Test | Ruling |
|---|---|
| `EngagementViewFamilyPropertyTests.RecomputingTheFamilyAtAnotherK_WouldLeak_SoTheAuditIsNotVacuous` (the reviewer's engine search) | P1 |
| `EngagementViewFamilyPropertyTests.RaisingKAfterClose_OnlyHidesViewsOfTheFrozenFamily_NumbersAndComments` (300 populations; union at k … k+3 passes the span and atom audits) | P1 |
| `EngagementReview3RegressionTests.ChangingTheTenantsK_AfterClose_OnlyEverHidesViewsOfTheFrozenFamily` (lazy freeze; tenant k 6, 7, 8, 5, 7, 3) | P1 |
| `EngagementViewFamilyPropertyTests.ScopeNestingAndBreakdownCells_DependOnInviteesNotRespondents` | P2 |
| `EngagementReview3RegressionTests.ARefusalOrAListedCell_DoesNotChange_WhenOnePersonResponds` (also: every hidden reason is `suppressed`) | P2 |

## Fix round 2 (second privacy review) → tests

| Test | Ruling |
|---|---|
| `EngagementReview2RegressionTests.InclusionExclusion_CannotRecoverTheOneFinanceEngineer` (review attack NewA through the API) | P0-2, (b) |
| `EngagementReview2RegressionTests.ScopeSwitching_GroupVsSelectedCompany_…` (NewB: numbers and comment lists; every pair of visible lists differs and overlaps by 0 or ≥ k) | P0-1, (a) |
| `EngagementViewFamilyPropertyTests.ReviewAttackA_…` / `ReviewAttackB_…` (the pure-engine proofs) | P0-2, P0-1 |
| `…AScopedReader_WhoseFilterIsNestedInTheScope_KeepsTwoDimensions`, `…UsesTheScopeAsOneOfTwoDimensions`, `…AScopedView_IsShownOnlyIfEveryComponentIs` | (a) |
| `…TheAuditItself_FindsTheReviewersInclusionExclusion` (the oracle is not vacuous), `…TheGoldenPopulation_PassesTheAudit`, `…RandomisedPopulations_PassTheLinearSpanAudit` (400 populations, k 3/5, one or several companies and teams, plus every reader's scoped views; R1–R5 asserted as properties too), `…RandomisedCommentLists_LeaveNoSmallAtom` (200) | (c) |
| `…ALargeSurvey_IsDecidedQuickly` (3,000 people: one aggregate family < 5 s — ≈ 0.3 s unloaded — plus the comment family < 10 s) | (c) |
| `EngagementReview2RegressionTests.AHiddenItem_HidesTheDriverAndTheOverallScoreThatContainIt` | (d) P2-4 |
| `EngagementReview2RegressionTests.TheAnonymousLedger_DoesNotMarkWhoOpened` (no session hash, no dates, version 0/1; an opener with a draft and a non-opener differ only in `draft_cipher`; five forging UPDATEs refused by `ck_survey_invitations__anonymous_ledger_minimal`) | (e) P2-5 |
| `EngagementReview2RegressionTests.OpenSurveyParticipation_IsTheCallersOwnScope_WithK` | (f) Low-6 |
| `EngagementReview2RegressionTests.AScopedReader_GetsOneFilterOrOneBreakdown_AndIsToldWhy`; mocked `a team-scoped reader: …` | (a) UX |
| `EngagementReview2RegressionTests.AClosedSurvey_CannotBeRelaunchedReopenedCancelledOrEdited` | review ledger file |

## Fix round 1 (first privacy review) → tests

All are in `Zayra.Api.Tests/Engagement`.

| Test | Review item / ENG |
|---|---|
| `ReviewAttack1_SiblingSum_CannotRecoverLegal` (the reviewer's attack 1, now expecting suppression) | P0-1 / 06 |
| `EverySecondarilyHiddenBreakdownCell_IsHiddenInItsFilterFormToo` (department, designation, tenure, company) | P0-1, P2-7 / 06 |
| `AtMostTwoDimensionsCombine_ABreakdownCountingAsOne` (results and export) | P0-1 / 06 |
| `EnumeratingEveryPermittedView_IsolatesNoGroupSmallerThanK`: every single filter, pair and the root through the API; nested differences and parent-minus-visible-children | P0-1, P1-4 / 06 |
| `EngagementViewFamilyPropertyTests` (golden + 400 randomised populations × scoped/unscoped, k 3/5; coincidental nesting; 3,000-person timing) | P0-1, P1-4 / 06 |
| `ReviewAttack2_LiveDifferencing_IsImpossibleWhileOpen`: the reviewer's attack 2. Results, comments, export and trend refused while open; one snapshot after close | P0-2 / 05, 07 |
| `SurveyList_ParticipationHonoursK` | P2-5 / 06 |
| `Comments_OnlyWholeSurveyOrTheDeclaredDimension_NeverIntersected` | P1-3 / 09 |
| `Anonymous_ResponseRows_HaveNoIdentityColumn…`: no `submitted_on` on responses; anonymous ledger without opened or dates; CHECK `ck_survey_invitations__anonymous_ledger_minimal` | P2-6 / 03, 05 |
| `Reminders_GoOnce_AndAnAnonymousSurveyRemindsEveryone` | P2-6 / 04 |
| `ATeamScopedReport_AndTheDashboard_CountTheSamePopulation` | Low-9 / 10 |
| Mocked spec `results view has no serious accessibility violations (en / ar)` | P2-8 / 11 |

The earlier mapping still holds: golden reconciliation, export and scheduled delivery, audit, double and concurrent
submit, drafts, modes, recurrence, worker isolation, actions, authorization negatives, retention, immutability,
settings floor and the privacy ratchets.
