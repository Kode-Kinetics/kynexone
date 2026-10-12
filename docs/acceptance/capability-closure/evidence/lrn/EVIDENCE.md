# LRN evidence — real-backend headed Chrome journey + automated suites

Local acceptance only (disposable stack). Nothing here was run against a deployed environment.

| Item | Value |
|---|---|
| Code under test | `bd9bc40ab15c5589884fbc2b3b2da06b48732469` (branch `feat/learning`, fix round 2); API `/health/live` reported the same commit |
| Run (UTC) | 2026-10-11 20:15–20:17 (journey run id `10112015`), one run on a fresh database |
| Browser | Google Chrome, Playwright `channel: 'chrome'`, headed; desktop 1440×950, phone 390×844 (isMobile) |
| Stack | Postgres 16 container `lrn-pg` :55511 (fresh, migrated by the API on start, `pendingMigrations 0`); API `dotnet run --no-launch-profile -- --contentRoot <scratch dir>` :5211 (document storage written outside the repo; the repo tree stayed clean); `next dev -p 5311` (`NEXT_PUBLIC_API_BASE_URL=http://localhost:5211`); SAMPLE cmi5 unit served by the journey config's `webServer` from `frontend/e2e/fixtures/cmi5-fire-safety` on `http://127.0.0.1:5411` (a different origin; not part of the app build) |
| World | `e2e/bootstrap` (provision.ts; preflight target 18/18, world 171/171) + `e2e/bootstrap/learning-seed.ts` |
| Tenant A | intelliflow `fc48268f-3c20-46f0-9987-73e57c5212d9`, company `9c8696ce-d89e-40e6-8d12-7daef7dc9d6d`; departments Operations `700bdd6f-…`, Quality `e4579ff5-…` |
| Tenant B (denial) | rasalmanar `e3916dbd-5f6f-4c12-a23c-5c9ca4225181` |
| Personas | HR Manager `hrmanager@intelliflow.com`; Manager `lrn.manager@intelliflow.com` (linked to INTELLIFLOW-KSA-E1, head of Operations); employees `employee1@` (Liu Wei, Operations), `employee2@` (Carlos Mendez, Operations); Quality: LRN-QA-E1..E3; tenant B admin `admin@rasalmanar.com` |
| Raw log | `journey-log.json` (steps, timestamps, viewports, ids) |

Command: `PLAYWRIGHT_BASE_URL=http://localhost:5311 E2E_API_BASE_URL=http://localhost:5211 PLATFORM_ADMIN_EMAIL=… PLATFORM_ADMIN_PASSWORD=… npx playwright test -c e2e/playwright.learning-journey.config.ts` → **6 passed (2.4 m)**.

## Journey steps

| # | Test ID / LRN | Step | Expected | Actual | Screenshot | UTC |
|---|---|---|---|---|---|---|
| 1 | LRN-01 | HR creates and publishes 3 courses in the UI: lesson + acknowledgement (with a `<script>`), lesson + 2-question assessment (pass 70%, 3 attempts, valid 12 months), cmi5 unit (moveOn CompletedAndPassed, unit pass mark 70%, "HR checks the completion" on by default) | 3 rows "Version 1 · Published"; published row offers "New version", not "Edit draft" | As expected | 01-LRN-01-catalogue.png | 20:15:39 |
| 2 | LRN-02 | Path with the 3 courses, course 2 "Only after the course above", activated | Active; "After course 1" | As expected | 02-LRN-02-path.png | 20:15:44 |
| 3 | LRN-03 | Assign to 2 Operations employees with a due date | "2 assigned · 0 already had it · 0 not assigned" | Same | 03-LRN-03-assign-employees.png | 20:15:47 |
| 4 | LRN-03 | Assign to the Quality department | "3 assigned" | Same | 04-LRN-03-assign-department.png | 20:15:48 |
| 5 | LRN-03 | Assign Liu Wei again | 409 duplicate_assignment, shown as a sentence | Refused with the sentence | 05-LRN-03-duplicate.png | 20:15:49 |
| 6 | LRN-04 | Liu Wei opens My learning | Path, due date, 0 of 3, course 2 waits for course 1 | As expected | 06-LRN-04-my-learning.png | 20:16:07 |
| 7 | LRN-05 | Open the lesson, acknowledge | `<script>` gone; Completed · Acknowledged | As expected | 07-LRN-05-acknowledged.png | 20:16:09 |
| 8 | LRN-07 | Course 2: wrong answers | Not passed, 2 attempts left | "Not passed: 0%. The pass mark is 70%. 0 of 2 answers correct. You have 2 attempts left." | 08-LRN-07-failed.png | 20:16:12 |
| 9 | LRN-06 | Retake, right answers (server-scored) | Completed · Passed assessment · 100% | As expected | 09-LRN-06-passed.png | 20:16:12 |
| 10 | LRN-08 | Certificate | Number + code + valid until +12 months | LRN-2026-PGMKTGX6, valid until 2027-10-11 | 10-LRN-08-certificate.png | 20:16:13 |
| 11 | LRN-13 | SAMPLE unit in its own window on 127.0.0.1:5411 | fetch → LaunchData → initialized → progressed×3 → passed → completed → terminated, all 200 | `fetch → 200 \| LMS.LaunchData → moveOn CompletedAndPassed, session 28c80a4d-… \| initialized → 200 \| progressed → 200 ×3 \| passed → 200 \| completed → 200 \| terminated → 200` | 11-LRN-13-cmi5-unit.png | 20:16:26 |
| 12 | LRN-05/13 | Course page updates by itself | "Waiting for HR to verify" — the unit's report alone issues no certificate | As expected | 12-LRN-05-cmi5-reported.png | 20:16:30 |
| 13 | LRN-05/11 | HR: Overview → KPI "Waiting for HR to verify" → records | Liu Wei listed | As expected | 13-LRN-05-hr-pending.png | 20:16:39 |
| 14 | LRN-05/15 | HR opens Liu Wei's assignment in Records | cmi5 item "Reported complete · Completed — reported by course content", Verify / Reject | As expected | 14-LRN-05-hr-verify.png | 20:16:41 |
| 15 | LRN-05/15 | HR verifies | Item Completed, certificate issued | As expected | 15-LRN-05-hr-verified.png | 20:16:42 |
| 16 | LRN-04 | Liu Wei reloads My learning | Path Completed, 3 of 3; transcript: Acknowledged / Passed assessment / Completed — reported by course content | As expected | 16-LRN-04-persisted.png | 20:16:50 |
| 17 | LRN-11 | Manager → Overview → "Assigned" records | Only their reports; no Quality employee; no Courses tab | As expected | 17-LRN-11-manager-team.png | 20:16:57 |
| 18 | LRN-11 | HR completion by department; KPI vs rows | Operations and Quality; KPI "Assigned" = rows of `/reports/status` | 5 = 5 | 18-LRN-11-hr-departments.png | 20:17:04 |
| 19 | LRN-12 | Carlos acts on Liu Wei's records and HR endpoints; self-acknowledges an assessment-gated course | 404 / 404 / 403 / 403 / 409 | `{"otherEmployeesEnrollment":404,"ackOtherEmployees":404,"assignAsEmployee":403,"revokeAsEmployee":403,"selfCompleteAssessment":409}` | 19-LRN-12-denied.png | 20:17:13 |
| 20 | LRN-12 | Tenant B admin reads tenant A assignment | 404 | 404 | (API) | 20:17:18 |
| 21 | LRN-14 | My learning in Arabic | `dir=rtl`, Arabic headings and statuses | As expected | 21-LRN-14-arabic-my-learning.png | 20:17:23 |
| 22 | LRN-14 | Certificate in Arabic | "شهادة إتمام"; number/code LTR | As expected | 22-LRN-14-arabic-certificate.png | 20:17:24 |
| 23 | LRN-14 | My learning at 390px | No horizontal scroll | overflow 0px | 23-LRN-14-mobile-my-learning.png | 20:17:30 |
| 24 | LRN-14 | Course page at 390px | Readable, controls reachable | As expected | 24-LRN-14-mobile-course.png | 20:17:31 |

## Records written (queried from `lrn-pg` after the run)

- Assignments: Liu Wei `621b8de4-6cd8-4fec-a43b-8a98c8bc38d3` Completed; Carlos `160c0276-…` InProgress; LRN-QA-E1..E3 Assigned (source Department).
- Credentials: LRN-2026-PY8FAZBS (Acknowledgement), LRN-2026-PGMKTGX6 (Assessment, 100, expires 2027-10-11), LRN-2026-WTYT72FC (Cmi5, 100 — issued at verification), LRN-2026-KP753754 (Carlos, Acknowledgement).
- cmi5 enrollment `46e82578-…`: Completed; evidence `5fe2c50a-ccf6-4ab0-96e4-416bb4a42283` (the AU's `completed`); `cmi5_registration` `31a2afc7-…` = the session's registration; reported 20:16:26.41, verified 20:16:42.10 by `hrmanager@intelliflow.com`.
- Session `28c80a4d-0625-40f5-bff2-a535232eb66a`: Terminated, origin `http://127.0.0.1:5411`, fetch used once.
- Audit (`learning.*`): assignment.created 5, assignment.completed 1, attempt.started 2, attempt.failed 1, attempt.passed 1, cmi5.launched 1, content.opened 4, course.created 3, course.published 3, credential.issued 4, enrollment.completion_reported 1, enrollment.verified 1, enrollment.completed 4, path.created 1, path.activated 1.
- Repo tree after bootstrap + journey: no untracked files outside this evidence folder (`backend-dotnet/Zayra.Api/storage/` absent; 88 stub documents went to the scratch content root).

## Automated suites

| Suite | Command | Result |
|---|---|---|
| Backend LRN integration + unit + HTTP (real Postgres via Testcontainers; HTTP through the real Program.cs pipeline) | `dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter "FullyQualifiedName~Zayra.Api.Tests.Learning"` | 110 passed, 0 failed (at `bd9bc40a`) |
| Platform gates | `--filter "…~TableBudget\|…~Orphan\|…~ModuleCatalog\|…~NavigationPageGate\|…~LegacyRoleGate\|…~RawSql\|…~CompanyScope\|…~BypassLint\|…~QueryFilterBypass\|…~ExecutionStrategyLint\|…~BootAssertion\|…~PrivilegedMfa\|…~PersonalDataLogging"` | 257 passed, 0 failed (at `bd9bc40a`) |
| Full backend suite | `dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj` | 7003 passed, 2 failed of 7005 (38 m 21 s, at `bd9bc40a`, machine heavily loaded by parallel agents). The 2 — `PayComponentMigrationBackfillTests.Migration_BackfillsEveryUnseededTenant…` (Npgsql timeout after 14 m 29 s) and `SelfieReview2Tests.Item3_OneImageSlotByDefault_AndABusyUploadWaitsBrieflyForIt` (timing) — are outside learning and passed when re-run alone: 2 passed, 0 failed |
| Mocked UI | `LEARNING_BASE_URL=http://127.0.0.1:5311 npx playwright test -c e2e/playwright.learning.config.ts` | 14 passed (at `bd9bc40a`) |
| Frontend unit | `npx playwright test -c playwright.unit.config.ts` | 237 passed (at `bd9bc40a`) |
| Browserless lints (i18n coverage, RTL, identity contract …) | `npx playwright test -c e2e/playwright.browserless.config.ts` | 199 passed, 1 skipped (pre-existing) |
| Types | `npx tsc --noEmit -p .` | clean |

Fix-round tests (backend): `LearningCmi5Tests` — `ConcurrentStatements_FourteenParallelWithDuplicates_AllStored_CompletionExactlyOnce`,
`ConcurrentFetch_OneWins_TheOtherIsTold_AlreadyUsed`, `ContentReportedCertificate_WaitsForHr_SelfVerificationRefused_RejectReopens`,
`Publish_RefusesSatisfiedOnLaunchWithACertificate_AUnitWithoutMastery_AndAZeroPassMark`,
`PassedBelowMastery_FailedAtOrAboveIt_AndCallsWithoutTheAuOrigin_AreRefused`, `Limits_OversizedStatement413_SessionQuota429`;
`LearningLifecycleTests` — `RenewingAPathWithTwoCertificateCourses_SupersedesBoth_AndNothingExpires`,
`Sweep_PagesThroughMoreRowsThanAPage_AndNeverWritesATenantWideNotice`,
`HrCannotDecideOnTheirOwnRecord_AndACorrectionCannotLeaveAValidCertificateBelowThePassMark`,
`Revocation_ReopensTheLearningWithHrsDueDate_AndReCompletionIssuesANewCertificate`,
`Leavers_OpenAssignmentsAreClosedByTheSweep_AndLeaveOverdueCountsAndNotices`, `APathOutsideItsEffectiveDates_CannotBeAssigned`,
`AnArchivedPath_StillGetsItsExpiringNotice_ButNoRenewalCycle_AndIsNotRetried`, `ALinkBlockedByItsPrerequisite_DoesNotRevealItsAddress`,
`ASubmissionThatDoesNotFitTheAssessment_IsRefused_AndUsesNoAttempt`, `DueDatesUseTheTenantsOwnDate_NotUtc`,
`Overdue_CountsSuspendedPeopleLabelled_AndInvitedOnlyOnceTheyHaveStarted` (round 2); round 2 in `LearningCmi5Tests` —
`ARejectedCompletion_DoesNotComeBackOnRelaunch_UntilTheUnitReportsAgain`, `ARevokedCmi5Certificate_DoesNotComeBackOnRelaunch_UntilTheUnitReportsAgain`
(reviewer probes C and D), `AnAssignmentOnlyWaitingForHr_IsNotOverdue_ButListedAsWaitingForVerification`; the daily no-recipient retry is asserted in
`Sweep_PagesThroughMoreRowsThanAPage_AndNeverWritesATenantWideNotice`;
`LearningHttpTests` — permission matrix (23 cases), `ATenantWithoutTheOptIn_IsRefusedEverywhere`,
`Ess_ResolvesTheEmployeeFromTheLogin_AndALoginWithoutALinkIsToldWhy`,
`Cmi5OverHttp_CorsOnlyForAnOpenSessionsOrigin_OriginRequired_SizeCapped_ReturnUrlFiltered`, `SafeReturnUrl_KeepsOnlyHttpUrlsOnTheAppsOwnOrigins` (7 cases).
