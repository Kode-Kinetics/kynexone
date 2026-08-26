# KynexOne HRM browser pilot audit

Audit date: 2026-08-15 (America/New_York)

Environment: isolated Docker Compose project `zayra-browser-audit`; frontend `http://127.0.0.1:55173`; API `http://127.0.0.1:55117`; fresh isolated Postgres volume; documented `SEED_DEMO_DATA=true` seeding.

Historical verdict at discovery: **NO-GO**. This report preserves the original browser findings and before-state evidence. The blockers were subsequently fixed and clean-stack revalidated; see [FINAL-REVALIDATION.md](FINAL-REVALIDATION.md) for the current verdict and final evidence.

## Prioritized findings

### Resolved during audit — Dashboard aggregate Redis failure

- Before: after valid IntelliFlow login, key metrics, action queue, activity, and analytics remained skeletons indefinitely; `GET /api/dashboard/full?months=6` returned HTTP 500 twice.
- Runtime cause: `StackExchange.Redis.RedisConnectionException`; the connection target became `redis://redis:6379:6379` because a URI was passed directly as a StackExchange.Redis configuration string.
- After the backend Redis parsing fix and frontend error-state repair, both images were rebuilt in the exact Compose topology. A fresh browser login produced HTTP 200 in 178 ms with no Redis exception and rendered SAR 196.7K net payroll, 12 active employees, 12 present, payroll/departments, and workforce mix.
- Regression evidence: [before — permanent skeleton](02-dashboard.png), [after — populated dashboard](12-dashboard-after-redis-fix.png).

### P0 — A locked payroll produces an unbalanced double-entry GL journal

- July 2026 payroll is `Locked`, with gross SAR 205,200.00 and net SAR 196,717.50.
- `Payroll > GL Journal > Jul 2026 — Locked > Generate Journal` shows debits 216,722.50 and credits 0.00, explicitly reporting `Journal is out of balance`.
- This prevents finance posting and proves the Payroll → GL handoff is not operational.
- Currency is also mislabeled USD throughout the GL journal although the tenant, salary structure, and payroll row are SAR.
- Evidence: [unbalanced GL journal](05-gl-journal-unbalanced.png).

### P0 — Leave submission is not connected to either approval queue

- Created a real Annual Leave request for Yaser Al-Ghamdi. UI confirmed: `Leave Request Submitted` and `Your request is pending manager approval`.
- `Leave > All Requests` persisted the request as `Submitted`.
- `Leave > Approvals` showed `0 pending approvals`; global `Approval Center` also showed `0 requests` in every queue.
- Code path explains the result: `LeaveService.SubmitRequestAsync` creates a `LeaveApproval` only when a resolved policy exists; otherwise it stores `Submitted`. It does not create a unified `ApprovalRequest`. The seeded tenant has no resolvable policy, so the UI promise and operational queues diverge.
- Evidence: [submitted leave request](09-leave-dates-shift-one-day.png), [empty centralized approval queue](10-leave-missing-from-approvals.png).

### P0 — Payroll state is internally contradictory across screens

- Locked July run has 12 generated salary slips and correct gross/net totals.
- Payroll dashboard simultaneously reports `Salary Coverage 0% — 12 missing`.
- Each locked slip reports YTD Gross = 0.00 and YTD Net = 0.00 even though July is part of the same year.
- Saudi Compliance reports all 12 employees blocked from GOSI with `MISSING_BASIC_SALARY`, despite the locked slips containing non-zero basic salary for every employee.
- Dashboard group KPI and totals are labeled USD while the company row is SAR, with the same unconverted numeric values.
- Evidence: [payroll contradiction](04-payroll-july-inconsistent.png), [regulatory contradiction](11-saudi-compliance-blockers.png).

### P1 — Date-only values shift one calendar day in the UI

- Entered leave dates 2026-08-24 through 2026-08-25; the persisted request renders Aug 23 through Aug 24.
- Root cause is visible in `frontend/src/views/LeavePage.tsx:62-65`: `new Date('YYYY-MM-DD')` treats the value as UTC, then `toLocaleDateString` shifts it to the prior date in America/New_York.
- The same class of error appears in attendance: raw punch timestamp displays Aug 15, while the processed daily attendance record is dated Aug 16.
- Date-only HR records must be parsed/rendered as calendar dates, not UTC instants.
- Evidence: [shifted leave dates](09-leave-dates-shift-one-day.png), [raw punch on Aug 15](08-raw-punch-local-date.png), [processed attendance on Aug 16](07-attendance-timezone-mismatch.png).

### P1 — Seeded KSA tenant is not client-demo ready

- Saudi Compliance score is 25/100 (`At Risk`) with six urgent actions.
- All 12 active employees are blocked from QIWA sync and GOSI calculation.
- QIWA credentials, GOSI employer ID, occupation codes, work-location IDs, and contract references are missing. WPS has a missing/invalid IBAN and no SIF export.
- The demo seed logs a locked payroll and GL total as if it were finance-complete, while the UI correctly exposes that the regulatory chain is incomplete.
- Evidence: [Saudi compliance blockers](11-saudi-compliance-blockers.png).

### P1 — Existing broad browser gate is stale against the current demo seed

- `frontend/e2e/helpers.ts` uses `Demo@1234` and expects both IntelliFlow and Evostel.
- Current `IntelliFlowDemoSeeder.cs:17-25` uses `IntelliFlow@2026!`; the current clean seed does not provide the expected Evostel state.
- Command: `PLAYWRIGHT_BASE_URL=http://127.0.0.1:55173 PLATFORM_ADMIN_EMAIL=platform@kynexone.com PLATFORM_ADMIN_PASSWORD=... npx playwright test e2e/tenant-auth.spec.ts --project=chromium --workers=1 --reporter=line`.
- Result before stopping the redundant failure cascade: setup passed; IntelliFlow happy-path failed twice at `helpers.ts:57` waiting for dashboard because credentials were rejected; Evostel test then entered the same timeout; 10 tests had not run. This suite cannot currently serve as a release gate.

### P2 — New tenant-pilot browser lane has a loading-state race

- The independent `e2e/pilot-critical.spec.ts` lane was run against ports 55173/55117 with the current IntelliFlow credentials and 12-employee expectation.
- It authenticated and observed `/api/dashboard/full` HTTP 200, then failed twice at `/people` because it reads `body.innerText()` immediately after `domcontentloaded`; both screenshots show the application loading spinner and zero text.
- Real client-side browser navigation with a 1.8-second settle rendered `/people` with a 10,431-character accessible DOM and no fatal error. The test must wait for the app loader to disappear or for a module landmark before asserting content.

### P2 — Dashboard chart containers emit runtime layout warnings

- Chrome console reported Recharts warnings twice: chart width and height were `-1`.
- This is consistent with charts mounting into unsized skeleton/layout containers and should be eliminated after the dashboard data failure is fixed.

## Verified working paths

- Invalid login shows a correct credential error; the current seeded admin can log in with the current seeder credential.
- Major modules load without React crashes: People, Attendance, Leave, Shifts, Overtime, Payroll, Recruitment, Performance, Compliance, Reports, Approvals, Saudi Compliance, and Setup.
- Post-fix authenticated client-side navigation rechecked People, Attendance, Leave, Loans & Advances, Offboarding, Reports & Analytics, Approvals, Saudi Compliance, and Setup; each rendered substantive accessible content without a fatal error.
- People CRUD works: created Layla Al-Sabah, auto-generated `EMP-SA-HR-2026-0001`, persisted department/designation/branch/payroll method, and enforced a KSA activation checklist.
- Attendance event ingestion and processing persist: a web punch appeared in Raw Punch Logs and was transformed into a daily record (subject to the date-shift defect above).
- Reports reconcile the last payroll period/status/net total with the locked July payroll.
- Employee activation is correctly blocked while mandatory GOSI readiness data is missing.

## Evidence index

- [01-login-page.png](01-login-page.png)
- [02-dashboard.png](02-dashboard.png)
- [03-employee-created.png](03-employee-created.png)
- [04-payroll-july-inconsistent.png](04-payroll-july-inconsistent.png)
- [05-gl-journal-unbalanced.png](05-gl-journal-unbalanced.png)
- [07-attendance-timezone-mismatch.png](07-attendance-timezone-mismatch.png)
- [08-raw-punch-local-date.png](08-raw-punch-local-date.png)
- [09-leave-dates-shift-one-day.png](09-leave-dates-shift-one-day.png)
- [10-leave-missing-from-approvals.png](10-leave-missing-from-approvals.png)
- [11-saudi-compliance-blockers.png](11-saudi-compliance-blockers.png)
- [12-dashboard-after-redis-fix.png](12-dashboard-after-redis-fix.png)

## Required release gates

1. Keep `/api/dashboard/full` HTTP 200 and non-skeleton rendering as a mandatory regression gate under the exact documented Compose topology.
2. Reject payroll lock unless salary coverage is complete and generated GL debits equal credits in the company currency.
3. Wire Leave submission into one canonical approval engine and seed a real manager/HR approval policy.
4. Replace UTC parsing of `YYYY-MM-DD` date-only values across Leave, Attendance, Payroll, Compliance, Performance, and Offboarding.
5. Reconcile salary assignments, payslip YTD, payroll dashboard coverage, and Saudi GOSI readiness from one effective-dated salary source.
6. Make the pilot seed itself pass QIWA/GOSI/WPS readiness or explicitly use a non-regulatory demo tenant; update E2E credentials and fixtures to the same seed contract.
7. Add browser gates for: dashboard non-skeleton, leave-to-approval visibility, locked-payroll balanced GL, date round-trip in a non-UTC timezone, and cross-screen currency consistency; make route checks wait for the application loader to finish.
