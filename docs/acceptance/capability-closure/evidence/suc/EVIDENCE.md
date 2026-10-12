# SUC evidence — real-backend headed Chrome journey + automated suites (final round)

Local acceptance only (disposable stack). Nothing here was run against a deployed environment.

| Item | Value |
|---|---|
| Code under test | `ab1db637c79683829de37484881f9653b4df9aed` (branch `feat/succession`, includes `feat/learning` through `7b6ca6f5`); API `/health/live` and frontend `/build-info` both reported this commit |
| Run (UTC) | 2026-10-11 22:02:47 – 22:08:39 |
| Browser | Google Chrome, Playwright `channel: 'chrome'`, headed; desktop 1440×950, phone 390×844 (isMobile) |
| Stack | Postgres 16 `suc-pg` :55517 — database dropped and recreated before the run, migrated by the API on start; API `dotnet run --no-launch-profile` :5217 (env from a file used only by this run); `next dev -p 5317` with `BUILD_COMMIT` |
| World | `e2e/bootstrap` provision.ts (preflight target 19/19, world 172/172) + `e2e/bootstrap/succession-seed.ts` (runs learning-seed.ts first) |
| Tenant A | intelliflow `a0da8172-9ae3-4b1c-961c-c80285b08bbf`, company `ab8b359d-053a-4b76-babc-4d29cf98586e` |
| Tenant B (denial) | rasalmanar `a58a4a5b-32b4-4f4f-a114-702fdb2c83c9` |
| Seeded inputs | Published appraisal cycle through the product path: Liu Wei 86, Carlos Mendez 68, everyone else 72. Positions SUC-CFO, SUC-HOQ. Learning path SUC-LEAD. Goal for Liu Wei |
| Personas | HR Manager `suc.hrmanager@` and HR Director `suc.hrdirector@` — logins linked to Quality employees (LRN-QA-E2/E3, outside the candidates' lines; candidate-level reads need a linked employee), created by the seed through work email → invitation → acceptance; line manager `lrn.manager@` (Liu and Carlos report to her), candidates `employee1@` (Liu Wei) / `employee2@` (Carlos Mendez), Admin `admin@intelliflow.com`, no-access `supervisor@`, tenant B `admin@rasalmanar.com` |
| Raw log | `journey-log.json` |

Command: `E2E_API_BASE_URL=http://127.0.0.1:5217 PLAYWRIGHT_BASE_URL=http://localhost:5317 PLATFORM_ADMIN_EMAIL=… PLATFORM_ADMIN_PASSWORD=… npx playwright test -c e2e/playwright.succession-journey.config.ts` → **7 passed (5.9 m)**.

## Journey steps

| # | SUC | Step | Expected | Actual | Screenshot | UTC |
|---|---|---|---|---|---|---|
| 1 | 01 | HR Manager designates SUC-CFO (High/Critical, rationale) | Gaps-first row: vacant, high risk, no approved slate; headline figures re-read (Active critical positions 1) | As expected | 01-SUC-01-designated.png | 22:03:21 |
| 2 | 02 | Nominate Liu and Carlos (evidence), into review, assess both (2026-H2); repeat nomination via API | Two competing rows; repeat → 409 `duplicate_nomination` | As expected | 02-SUC-02-competing-candidates.png | 22:03:29 |
| 3 | 03 | HR Director moves Carlos's potential with a 9-character reason | Refused (≥ 20 characters) | As expected | 03-SUC-03-calibration-reason-required.png | 22:03:35 |
| 4 | 03 | Calibrate Liu (kept), Carlos (moved, reason); confirm readiness | Both Calibrated | As expected | 04-SUC-03-calibrated.png | 22:03:36 |
| 5 | 03/08 | 9-box; open Liu's placement | Liu P3-High (86), Carlos P1-Moderate (68); inputs incl. band rule `default-75-60-v1` | As expected | 05-SUC-03-ninebox-inputs.png | 22:03:38 |
| 6 | 06 | Build v1 (order + rationale), submit | v1 Submitted, hash prefix | As expected | 06-SUC-06-submitted.png | 22:03:48 |
| 7 | 06/07 | HR Director's Approval Center | "Succession slate v1 · 82b9f36c8147", no name; all-requests API payload name-free; `requestedForEmployeeId` null | As expected | 07-SUC-06-approval-center.png | 22:03:57 |
| 8 | 06 | Return v1 with a reason | ReturnedForRevision, reason kept | As expected | 08-SUC-06-returned.png | 22:04:02 |
| 9 | 06 | Revise → v2, submit; HR Director approves | v2 Approved, v1 Returned; hashes verified | As expected | 09-SUC-06-approved-history.png | 22:04:15 |
| 10 | 04 | Liu's employee record → Development: plan; Learning (SUC-LEAD), Goal and Experience actions; activate | Plan Active, 3 actions | As expected | 10-SUC-04-plan-active.png | 22:04:35 |
| 11 | 04 | Liu completes the lesson; opens My development plan; proposes the Experience action done; API tries to cancel / complete it outright | Learning action Completed (`LinkedLearningCompleted`); Experience "You said this is done" (still In progress); cancel 403, complete-outright 403; no memberships (visibility off) | As expected | 11-SUC-04-linked-completion.png | 22:04:46 |
| 12 | 04 | HR confirms the proposal on the Development tab | Experience Completed (Manual) — counted only now | As expected | 12-SUC-04-owner-confirms.png | 22:05:00 |
| 13 | 06/09 | Carlos 1–2 years → Ready now (reason); history | Changed; history entry listed | As expected | 13-SUC-06-readiness-history.png | 22:05:21 |
| 14 | 06 | Open approved v2 | Snapshot still 1–2 years for Carlos; hash verified | As expected | 14-SUC-06-snapshot-kept.png | 22:05:25 |
| 15 | 07 | Carlos (candidate, Employee) opens v2 and calls the API | No content; slate 404, nominations 403, others' plans 403 | As expected | 15-SUC-07-candidate-denied.png | 22:05:42 |
| 16 | 07 | The candidates' line manager | Own nominations + team plans only; slate 404, nominations 403, 9-box 403, plan without nomination link, no slate in team queue; her own nomination of Liu to a pool comes back **masked** (stage only) | As expected | 16-SUC-07-manager-view.png | 22:06:06 |
| 17 | 07 | Admin (configuration role) opens Succession; calls candidate endpoints; reads the audit-log viewer | Positions + Pools tabs only, no candidate names; nominations 403, 9-box 403, reports 403, slate edit 403, slate read 404; 29 succession audit rows, metadata ids only | As expected | 17-SUC-07-admin-config-only.png | 22:06:37 |
| 18 | 07 | Supervisor; tenant B Admin (API) | 403; position 404, slate 404 | As expected | (API) | 22:06:5x |
| 19 | 05 | Every count metric vs its drill rows; click Coverage | All equal (positions 1, coverage 1, extended 1, bench Ready-now 1, 1–2y 1, 3+ 0, gaps 0) | As expected | 18-SUC-05-kpi-drill.png | 22:07:09 |
| 20 | 08 | Position page in Arabic | `dir=rtl`; 9-box mirrored (asserted by bounding boxes) | As expected | 19-SUC-08-arabic-ninebox.png | 22:07:37 |
| 21 | 04 | My development plan in Arabic | RTL | As expected | 20-SUC-04-arabic-my-plan.png | 22:07:55 |
| 22 | 04 | My development plan at 390px | No horizontal scroll | overflow 0px | 21-SUC-04-mobile-my-plan.png | 22:08:12 |
| 23 | 01 | Position page at 390px (HR) | No horizontal scroll | overflow 0px | 22-SUC-01-mobile-position.png | 22:08:22 |

## Records written (queried from `suc-pg` after the run)
- Critical position `196e4387-7b24-49b5-bbc3-18214e45c1fe`. Nominations: Liu `851440e1-…` OnSlate ReadyNow; Carlos `14cd4d0f-…` OnSlate ReadyNow (was OneToTwoYears); Liu pool nomination `67871bcb-…` Proposed (by the line manager).
- Slates: v1 `5bd97163-a27a-4b11-8d5a-162ea2e4d7ff` ReturnedForRevision (82b9f36c8147…); v2 `2f4c58d0-8f18-4b7c-8c39-f41d6489b5df` Approved (dcb329b21456…).
- Plan actions: Learning Completed `LinkedLearningCompleted`; Experience Completed `Manual` with `completion_proposed_at_utc` set (proposed by the employee, confirmed by HR); Goal NotStarted.
- Audit (`succession.*`): 29 rows — designated 1, nomination.created 3, in_review 2, assessment.created 2, assessment.calibrated 2, nomination.calibrated 2, slate.drafted 1, submitted 2, returned 1, revised 1, edited 1, approved 1, devplan.created 1, action_added 3, activated 1, action_synced 1, completion_proposed 1, action_updated 1, readiness_changed 1, pool.created 1. Metadata shapes (all rows): `{nominationId}`, `{assessmentId[,supersedes]}`, `{planId[,actionId]}`, `{slateId[,VersionNo|revisedFrom|approvalRequestId,snapshotSha256]}`, `{criticalPositionId}`, `{poolId}` — the row's own ids and hashes only.

## Automated suites (backend at `bcdad8d6`; types, unit, browserless, mocked UI and the journey at `ab1db637`, which adds only the workspace figure refresh)

| Suite | Command | Result |
|---|---|---|
| SUC Postgres + architecture + review regressions + HTTP pipeline | `--filter "FullyQualifiedName~Succession"` | 82 passed (incl. 8 F-round and 8 final-round regressions, 30 HTTP) |
| SUC + platform gates | `--filter "…~Zayra.Api.Tests.Succession\|…~TableBudget\|…~Orphan\|…~ModuleCatalog\|…~NavigationPageGate\|…~LegacyRoleGate\|…~RawSql\|…~CompanyScope\|…~PrivilegedMfa\|…~ApprovalProducer\|…~AuthSeeder\|…~NoMigrationReferences\|…~SelfieReview3\|…~ConfigurationConsumer\|…~QueryFilterBypass"` | 388 passed |
| Full backend suite | `dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj` | 7123 passed, 1 failed of 7124 (19 m 34 s, machine load avg > 40). The failure, `AdvisoryLockTransactionPoolingPostgresTests.MigrationImportLease_WhoseKeepaliveStalls_LosesTheLock_AndSaysSo`, is timing-based and outside SUC; re-run alone (`--filter "…~AdvisoryLockTransactionPoolingPostgresTests"`): 16/16 passed |
| Migration | `dotnet ef migrations has-pending-model-changes --context ZayraDbContext` | "No changes have been made to the model since the last migration." (AddLearning `20261011194628` then AddSuccession `20261011210200`) |
| Mocked UI (EN, AR/RTL, 390px, axe) | `npx playwright test -c e2e/playwright.succession.config.ts` (next dev :5317) | 16 passed |
| Frontend unit | `npx playwright test -c playwright.unit.config.ts` | 248 passed |
| Browserless lints | `npx playwright test -c e2e/playwright.browserless.config.ts` | 200 passed, 1 skipped (pre-existing) |
| Types | `npx tsc --noEmit -p .` | clean |

Final-round regressions (`SuccessionFinalRoundRegressionTests`): `E1_DottedLineSupervisorOfACandidate_IsBarredFromReadingAndDeciding`, `E2_StoredSkipLevelManagerIsACandidate_IsBarredFromReadingAndDeciding`, `E3_CandidateLoginWithoutEmployeeClaim_IsResolvedThroughTheLoginLink_AndBarred`, `N2_SlateReaderWithNoResolvableEmployee_IsRefusedCandidateReads_AndDecisions`, `E5_ManagerAndHr_CannotCreateOrManageTheirOwnPlan`, `N4_AuditMetadataHasNoTargetsOrReasons_AndCountsAreWithheldWithoutSlateAccess`, `N6_SnapshotRewrite_MustBeTheDeterministicPseudonym`, `N6_RetentionJob_WritesExactlyThePseudonym`.

Review regressions (`SuccessionReviewRegressionTests`): `F1_AManagerNominatingAReport_GetsTheMaskedView_NoNineBoxNoSlateCount`, `F2_AdminAndSlatesManageWithoutRead_CannotReachACandidate_ThroughAnyWriteEndpoint`, `F3_SuccessionAuditRows_CarryIdsOnly`, `F4_ACandidateWithHrKeys_NeverSeesOrActsOnTheirOwnPositions_Slates_Nominations_Or_Assessments`, `F5_TheEmployeeStartsNotesAndProposes_TheOwnerConfirms_CancelAndDueDatesAreRefused`, `F6_ALeaver_IsRefusedOnASlate_AndStopsCountingAsCoverage_BeforeTheSweep`, `F7_Anonymisation_CanRewriteNamesOnly_AndTheHashStatesTellAnonymisedFromTampered`, `F8_ACompletedAssignmentCannotBeLinked_AndARevokedCertificateReopensThePlanAction`. HTTP (`SuccessionHttpTests`): permission matrix (any-of; the HR principal is linked to an employee), opt-in 403 `feature_not_enabled`, error mapping, masked manager nomination + audit-log viewer as Admin.
