# prerequisites → run → validation → maker approval → finance approval → lock → payslips → bank file → reconciliation

Recorded by the KynexOne evidence harness (`frontend/e2e/evidence/`). Every step below is one linked record: who acted, when, what they did, what the API answered, what the database said when the record was read back, and a picture of the settled screen. Steps with no picture are listed as **NO CAPTURE** with the reason — never left blank.

- Run: `2026-09-30T17-00-09-937Z` · started 2026-09-30T17:00:09.937Z · finished 2026-09-30T17:00:45.230Z
- Frontend: http://127.0.0.1:5340 · API: http://127.0.0.1:5341
- Build under test: 64bab568c9ce72665c605ef39da7467d15ac2d82 · database: kynexone_evidence
- Personal data: **redacted** in this copy; the unredacted originals are kept outside the repository and are matched to it by `originalSha256`

**15 steps — 15 passed, 0 failed. 13 with a verified settled-state image, 2 explicitly NO CAPTURE. 15 re-read the record after the write.**

## Defects observed while recording this story

These were seen during the run. They did not necessarily fail a step — an evidence run sees more than it asserts — and they are recorded here so the bundle is not a highlight reel.

> **STATUS — both fixed since this run.** Both defects below were FIXED in PR #162 (the KSA activation floor is bypassed for an employee created without a country; the background AI-insights 403 raising an "Access Denied" toast). This bundle is the unaltered record of the run it describes, made on build 64bab568c9ce72665c605ef39da7467d15ac2d82, which predates that fix — the images, the step table and the observations below have deliberately NOT been re-recorded or edited, because a record that is rewritten after the fact stops being evidence. Read them as what was true on that build. The spec has since changed: the R1b tripwire step is gone and its case is folded into R1, which now makes both create calls and asserts the same 422 for each, so a fresh run records 14 steps and neither defect.

- **P1 — The KSA statutory activation floor is bypassed for any employee created without a country** — FIXED in PR #162; recorded here as it was on build 64bab568  
  POST /api/employees sets Employee.CountryCode ONLY from the first complianceRecords entry (EmployeeManagementService.CreateAsync) and never falls back to the country of the company the employee is attached to. GccReadinessFloor.Resolve("") returns an empty requirement list, so EnsureActivatable finds nothing to enforce and a non-GCC expat with no Iqama number and no GOSI reference is activated into a Saudi legal entity with HTTP 200. GET /api/employees/field-catalog states the intended rule — "Explicit countryCode wins, else the company's country" — so the derivation is specified and simply absent on this path. An employee activated this way occupies an active seat and is a candidate for a payroll population while being unpayable under KSA rules.
  - Step R1 (refusal-incomplete-employee): identical employee WITH a compliance record naming SA → HTTP 422 employee_not_activatable, blocking IqamaNumber, pay-blocking GosiReference.
  - Step R1b (this tripwire): identical employee WITHOUT it → countryCode "", HTTP 200, Active.
  - Zayra.Api.Tests/EmployeeActivationGateTests.cs sets CountryCode = "SA" directly on its fixture entities, so it covers the gate but never the derivation — the blind spot.
- **P2 — A background AI-insights fetch 403s on every payroll screen and raises a user-facing "Access Denied" error toast** — FIXED in PR #162; recorded here as it was on build 64bab568  
  GET /api/ai/insights is issued automatically by the payroll module on mount. For any persona without the AI insight permission — here the HR Manager and the Finance Approver, both of whom are doing exactly their job — it returns 403, and the frontend renders it as "Access Denied — You do not have permission to perform this action. Please contact your administrator." The payroll transaction underneath succeeded every time. The effect is that a correct, completed payroll action is presented to the operator as a permission failure. A background panel the user did not ask for should degrade silently (hide the panel), not raise a blocking error.
  - 5 such 403s across steps: prerequisites, validation, finance-approval, payslips, reconciliation
  - Visible in img/11-payslips.jpg — the toast sits over a screen that correctly shows 14 payslips, all published to ESS.
  - Affects the HR Manager and Finance Approver personas; the Admin persona holds the permission and sees no toast.

| # | Step | Actor | Role | Record | Outcome | Image |
|---|------|-------|------|--------|---------|-------|
| 1 | Read the payroll prerequisites for the tenant before opening a run | IntelliFlow HR Manager | HR Manager | Tenant payroll readiness `intelliflow` | pass | [`img/01-prerequisites.jpg`](img/01-prerequisites.jpg) |
| 2 | REFUSAL — try to activate a KSA expat who has no GOSI reference and no Iqama number | IntelliFlow Administrator | Admin | Employee (Draft, refused activation) `44` | pass | **NO CAPTURE** — Deliberately so: this refusal was driven through the API, so there is no screen of it to photograph; the 422 body recorded below is the whole of the evidence. A picture of the employee list here would show a screen that has nothing to do with the refusal, which is exactly the substitution this harness exists to prevent. The UI path for the same refusal is listed under Not run. |
| 3 | Create the payroll run for the first period that does not have one | IntelliFlow Administrator | Admin | PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa` | pass | [`img/03-run-created.jpg`](img/03-run-created.jpg) |
| 4 | Process the run so every employee on it gets a calculated salary slip | IntelliFlow Administrator | Admin | PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa` | pass | [`img/04-run-processed.jpg`](img/04-run-processed.jpg) |
| 5 | Run payroll validation on the processed run and read every finding | IntelliFlow HR Manager | HR Manager | PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa` | pass | [`img/05-validation.jpg`](img/05-validation.jpg) |
| 6 | The HR Manager (maker) approves the run and sends it to Finance | IntelliFlow HR Manager | HR Manager | PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa` | pass | [`img/06-maker-approval.jpg`](img/06-maker-approval.jpg) |
| 7 | REFUSAL — the maker tries to approve a second time and complete the run alone | IntelliFlow HR Manager | HR Manager | PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa` | pass | [`img/07-refusal-maker-cannot-finalise.jpg`](img/07-refusal-maker-cannot-finalise.jpg) |
| 8 | The Finance Approver — a different authorised person — gives the final approval | IntelliFlow Finance Approver | Finance Approver | PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa` | pass | [`img/08-finance-approval.jpg`](img/08-finance-approval.jpg) |
| 9 | REFUSAL — open the bank export while the run is Approved but not yet Locked | IntelliFlow Administrator | Admin | PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa` | pass | [`img/09-refusal-batch-before-lock.jpg`](img/09-refusal-batch-before-lock.jpg) |
| 10 | The Finance Approver locks the approved run | IntelliFlow Finance Approver | Finance Approver | PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa` | pass | [`img/10-run-locked.jpg`](img/10-run-locked.jpg) |
| 11 | Generate the payslips for the locked run | IntelliFlow HR Manager | HR Manager | PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa` | pass | [`img/11-payslips.jpg`](img/11-payslips.jpg) |
| 12 | Create the payment batch now that the run is locked | IntelliFlow Administrator | Admin | PayrollPaymentBatch `f7bc2787-93da-4ed3-af79-821f63346972` | pass | [`img/12-payment-batch.jpg`](img/12-payment-batch.jpg) |
| 13 | Generate the WPS/SIF bank file (generation only — this must not pay anybody) | IntelliFlow Administrator | Admin | PayrollPaymentBatch `f7bc2787-93da-4ed3-af79-821f63346972` | pass | [`img/13-bank-export-generation-only.jpg`](img/13-bank-export-generation-only.jpg) |
| 14 | Reconcile the displayed totals against the source records | IntelliFlow Finance Approver | Finance Approver | PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa` | pass | [`img/14-reconciliation.jpg`](img/14-reconciliation.jpg) |
| 15 | DEFECT TRIPWIRE — the same incomplete KSA expat, created without a country, activates anyway | IntelliFlow Administrator | Admin | Employee (Draft, wrongly activatable) `45` | pass | **NO CAPTURE** — Driven through the API, like R1, so there is no screen of it. The two API calls below, differing only in the absent complianceRecords, are the whole of the evidence. |

## 1. Read the payroll prerequisites for the tenant before opening a run

- **Actor** — IntelliFlow HR Manager (HR Manager), `hr*******@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:21.058Z (1461 ms)
- **Business record** — Tenant payroll readiness `intelliflow`
- **Expected** — The payroll dashboard states how many ACTIVE employees were checked against salary assignments, payroll profiles, IBANs and statutory readiness, and names every remaining gap.
- **Observed** — The prerequisites panel reports 14 active employees checked.
- **API**
  - `GET /api/help-texts` → **200** (0 ms)
  - `GET /api/auth/me` → **200** (0 ms)
  - `GET /api/features/disabled-keys` → **200** (0 ms)
  - `GET /api/features/modules` → **200** (0 ms)
  - `GET /api/tenant-admin/localization` → **200** (0 ms)
  - `GET /api/notifications` → **200** (0 ms)
  - `GET /api/ai/insights?acknowledged=false&pageSize=5` → **403** (0 ms)
  - `GET /api/payroll/companies` → **200** (0 ms)
  - `GET /api/payroll/reports/summary` → **200** (0 ms)
  - `GET /api/payroll/overview?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/readiness?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/readiness?year=2026&month=9` → **200** (33 ms)
- **Persisted (fresh GET)** — totalActiveEmployees=`14`, employeesWithSalary=`14`, salaryCoveragePercent=`100`, isReadyForProcessing=`false`
- **Screen assertion** — the settled screen showed /Checked for \d+ active employees/
- **Image** — `img/01-prerequisites.jpg`, sha256 `51432f6d3975cfa563a8fd06536f833581f8189d23c1253e0508eddfb742a48d` (original sha256 `7cf3b0f49d4e83d07d59d77cf99d23ac589216636ab081c9248ddf0c64bb0d7e`, 108594 bytes)
- **Settled** — after 404 ms; waited out 0 request(s) and 0 loading indicator(s); 1 node(s) redacted

![Read the payroll prerequisites for the tenant before opening a run](img/01-prerequisites.jpg)

## 2. REFUSAL — try to activate a KSA expat who has no GOSI reference and no Iqama number

- **Actor** — IntelliFlow Administrator (Admin), `ad***@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:22.519Z (135 ms)
- **Business record** — Employee (Draft, refused activation) `44`
- **Expected** — The activation is refused with a structured 422 naming the missing statutory data, and the employee stays Draft. An employee who cannot lawfully be paid must never join a payroll population.
- **Observed** — Draft employee #44 (SA, Indian national) created; activation refused with HTTP 422 employee_not_activatable. Activate-blocking: IqamaNumber. Pay-blocking: GosiReference, IqamaExpiry.
- **API**
  - `GET /api/companies?page=1&pageSize=10` → **200** (19 ms)
  - `POST /api/employees` → **201** (54 ms)
  - `POST /api/employees/44/activate` → **422** (33 ms) — {"error":"employee_not_activatable","employeeId":44,"message":"Cannot activate this employee — 1 required detail(s) missing.","policy":{"countryCode":"SA","tier":"certified","sources":["floor","tenant","company"]},"progr
  - `GET /api/employees/44` → **200** (26 ms)
- **Persisted (fresh GET of the employee — a refused activation must leave it Draft)** — employeeId=`44`, status=`"Draft"`, activated=`false`
- **Image** — **NO CAPTURE.** Deliberately so: this refusal was driven through the API, so there is no screen of it to photograph; the 422 body recorded below is the whole of the evidence. A picture of the employee list here would show a screen that has nothing to do with the refusal, which is exactly the substitution this harness exists to prevent. The UI path for the same refusal is listed under Not run.

## 3. Create the payroll run for the first period that does not have one

- **Actor** — IntelliFlow Administrator (Admin), `ad***@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:22.654Z (2203 ms)
- **Business record** — PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa`
- **Expected** — A new run exists in Draft for that period, and the list renders every run the API returned.
- **Observed** — Run for Sep 2026 created in Draft as a9e97248-45d6-41c4-ad47-2db6759327fa.
- **API**
  - `GET /api/help-texts` → **200** (0 ms)
  - `GET /api/auth/me` → **200** (0 ms)
  - `GET /api/tenant-admin/localization` → **200** (0 ms)
  - `GET /api/features/disabled-keys` → **200** (0 ms)
  - `GET /api/features/modules` → **200** (0 ms)
  - `GET /api/notifications` → **200** (0 ms)
  - `GET /api/payroll/companies` → **200** (0 ms)
  - `GET /api/payroll/reports/summary` → **200** (0 ms)
  - `GET /api/ai/insights?acknowledged=false&pageSize=5` → **200** (0 ms)
  - `GET /api/payroll/overview?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/readiness?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/companies` → **200** (0 ms)
  - …and 5 more, in `manifest.json`
- **Persisted (fresh GET)** — runId=`"a9e97248-45d6-41c4-ad47-2db6759327fa"`, status=`"Draft"`, employeeCount=`0`
- **Screen assertion** — the settled screen showed /Draft/
- **Image** — `img/03-run-created.jpg`, sha256 `d93d84b638e84753f5feb05f17bcb1e31e1a8d03e7037fa24a1337bab3f94634` (original sha256 `d93d84b638e84753f5feb05f17bcb1e31e1a8d03e7037fa24a1337bab3f94634`, 63245 bytes)
- **Settled** — after 437 ms; waited out 0 request(s) and 0 loading indicator(s); 0 node(s) redacted

![Create the payroll run for the first period that does not have one](img/03-run-created.jpg)

## 4. Process the run so every employee on it gets a calculated salary slip

- **Actor** — IntelliFlow Administrator (Admin), `ad***@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:24.857Z (2152 ms)
- **Business record** — PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa`
- **Expected** — The run leaves Draft for Processed and carries a non-zero employee count and net-pay total.
- **Observed** — 14 employees, gross 138250.00, net 119730.47.
- **API**
  - `POST /api/payroll/runs/a9e97248-45d6-41c4-ad47-2db6759327fa/process` → **200** (0 ms)
  - `GET /api/payroll/runs?pageSize=100` → **200** (17 ms)
- **Persisted (fresh GET)** — status=`"Processed"`, employeeCount=`14`, totalGrossSalary=`138250`, totalNetSalary=`119730.47`
- **Screen assertion** — the settled screen showed /Processed/
- **Image** — `img/04-run-processed.jpg`, sha256 `76697e56efdaa4aa2b80038b77cdf150995e3ab1d312789155e4041e2164e6fc` (original sha256 `76697e56efdaa4aa2b80038b77cdf150995e3ab1d312789155e4041e2164e6fc`, 63715 bytes)
- **Settled** — after 394 ms; waited out 0 request(s) and 0 loading indicator(s); 0 node(s) redacted

![Process the run so every employee on it gets a calculated salary slip](img/04-run-processed.jpg)

## 5. Run payroll validation on the processed run and read every finding

- **Actor** — IntelliFlow HR Manager (HR Manager), `hr*******@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:27.009Z (1554 ms)
- **Business record** — PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa`
- **Expected** — Validation completes with ZERO errors, and every warning it does return is one this story can explain by code — an unexplained warning fails the step rather than being counted and carried into approval.
- **Observed** — Validation returned 29 finding(s): 0 error(s), 29 warning(s); codes: GOSI_COHORT_NOT_RECORDED, WARN_ARREARS_LOOKBACK_TRUNCATED, WARN_GOSI_RATES_REQUIRE_SIGNOFF.
- **API**
  - `GET /api/help-texts` → **200** (0 ms)
  - `GET /api/auth/me` → **200** (0 ms)
  - `GET /api/tenant-admin/localization` → **200** (0 ms)
  - `GET /api/features/disabled-keys` → **200** (0 ms)
  - `GET /api/features/modules` → **200** (0 ms)
  - `GET /api/notifications` → **200** (0 ms)
  - `GET /api/ai/insights?acknowledged=false&pageSize=5` → **403** (0 ms)
  - `GET /api/payroll/companies` → **200** (0 ms)
  - `GET /api/payroll/reports/summary` → **200** (0 ms)
  - `GET /api/payroll/overview?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/readiness?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/runs?page=1&pageSize=100` → **200** (0 ms)
  - …and 5 more, in `manifest.json`
- **Persisted (fresh GET of the saved validation results)** — findings=`14`, errors=`0`, warnings=`14`, warningCodes=`["GOSI_COHORT_NOT_RECORDED"]`, warningsExplained=`{"GOSI_COHORT_NOT_RECORDED":"14× — The employee has no GOSI first-registration date, so the engine cannot tell which contribution schedule applies and computes on the pre-3-July-2024 one. It is a WARNING and not an error by design: the pre-2024 schedule is the correct answer for everyone hired before that date, and the date is recorded on the employee's Payroll tab once known. The fixture employees are provisioned without it, so this fires once per employee and is expected here. The related GOSI_NEW_ENTRANT_SCHEDULE_NOT_MODELLED code is an ERROR and would block approval; it does not appear on this run, and the step asserts zero errors."}`
- **Image** — `img/05-validation.jpg`, sha256 `c4b41c8a98403899208323c8f13f316517375f08956a471865d5d42cd548444d` (original sha256 `0a95ebbe59ddb0d589eff458e08da4b15a4a723c26c7d067ea3a1c746ba75ebd`, 152105 bytes)
- **Settled** — after 395 ms; waited out 0 request(s) and 0 loading indicator(s); 0 node(s) redacted

![Run payroll validation on the processed run and read every finding](img/05-validation.jpg)

## 6. The HR Manager (maker) approves the run and sends it to Finance

- **Actor** — IntelliFlow HR Manager (HR Manager), `hr*******@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:28.563Z (1443 ms)
- **Business record** — PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa`
- **Expected** — The run moves to Pending Finance Review, and the approval chain records the maker step. The maker is told, on screen, that their approval is not the final one.
- **Observed** — Screen and API agree: 14 employees, gross SAR 138,250.00, net SAR 119,730.47. The run is now Pending Finance Review.
- **API**
  - `GET /api/payroll/companies` → **200** (0 ms)
  - `GET /api/payroll/runs?page=1&pageSize=100` → **200** (0 ms)
  - `GET /api/payroll/runs/a9e97248-45d6-41c4-ad47-2db6759327fa/validation-overrides` → **200** (0 ms)
  - `GET /api/payroll/runs/a9e97248-45d6-41c4-ad47-2db6759327fa/approvals` → **200** (0 ms)
  - `GET /api/payroll/runs/a9e97248-45d6-41c4-ad47-2db6759327fa/population` → **200** (0 ms)
  - `POST /api/payroll/runs/a9e97248-45d6-41c4-ad47-2db6759327fa/approve` → **200** (0 ms)
  - `GET /api/payroll/runs?page=1&pageSize=100` → **200** (0 ms)
  - `GET /api/payroll/runs/a9e97248-45d6-41c4-ad47-2db6759327fa/validation-overrides` → **200** (0 ms)
  - `GET /api/payroll/runs/a9e97248-45d6-41c4-ad47-2db6759327fa/approvals` → **200** (0 ms)
  - `GET /api/payroll/runs/a9e97248-45d6-41c4-ad47-2db6759327fa/population` → **200** (0 ms)
  - `GET /api/payroll/runs?pageSize=100` → **200** (18 ms)
- **Persisted (fresh GET)** — status=`"PendingFinanceReview"`
- **Screen assertion** — the settled screen showed Awaiting Finance Controller approval.
- **Image** — `img/06-maker-approval.jpg`, sha256 `171a6a41626861d4fb37afeadcf55f54b655e8dcc304037aaa45a730edba9d8a` (original sha256 `a3e2a58af082d66d293de6c25a0666b81c072026b0daaf4b221e949f7742d11d`, 80438 bytes)
- **Settled** — after 392 ms; waited out 0 request(s) and 0 loading indicator(s); 0 node(s) redacted

![The HR Manager (maker) approves the run and sends it to Finance](img/06-maker-approval.jpg)

## 7. REFUSAL — the maker tries to approve a second time and complete the run alone

- **Actor** — IntelliFlow HR Manager (HR Manager), `hr*******@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:30.006Z (1161 ms)
- **Business record** — PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa`
- **Expected** — No approval control survives on the run the maker already signed, the API refuses a second approval with HTTP 400, and — the part that matters — the run does not move.
- **Observed** — Three approval controls absent from the maker's card; the API answered HTTP 400: {"message":"You cannot approve this run at its current stage."}
- **API**
  - `POST /api/payroll/runs/a9e97248-45d6-41c4-ad47-2db6759327fa/approve` → **400** (14 ms) — {"message":"You cannot approve this run at its current stage."}
  - `GET /api/payroll/runs?pageSize=100` → **200** (17 ms)
- **Persisted (fresh GET)** — status=`"PendingFinanceReview"`, unchanged=`true`
- **Screen assertion** — the settled screen showed Awaiting Finance Controller approval.
- **Image** — `img/07-refusal-maker-cannot-finalise.jpg`, sha256 `25e4f61e86e65269d9e87ae5d9356b2d1e3533d7a564ff7b9ce65fe3c5f758e1` (original sha256 `2f6b5498a543d88ca2728f11db63c72beff381b6a7a7647555f39d3d1f5cf81c`, 80373 bytes)
- **Settled** — after 391 ms; waited out 0 request(s) and 0 loading indicator(s); 0 node(s) redacted

![REFUSAL — the maker tries to approve a second time and complete the run alone](img/07-refusal-maker-cannot-finalise.jpg)

## 8. The Finance Approver — a different authorised person — gives the final approval

- **Actor** — IntelliFlow Finance Approver (Finance Approver), `fi*****@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:31.167Z (1714 ms)
- **Business record** — PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa`
- **Expected** — The run reaches Approved, reads as ready to lock, and the approval chain holds BOTH the maker step and the finance step, signed by two different people.
- **Observed** — Approval chain: FinanceReview → PayrollReview, signed by 2 distinct users.
- **API**
  - `GET /api/help-texts` → **200** (0 ms)
  - `GET /api/auth/me` → **200** (0 ms)
  - `GET /api/features/modules` → **200** (0 ms)
  - `GET /api/tenant-admin/localization` → **200** (0 ms)
  - `GET /api/notifications` → **200** (0 ms)
  - `GET /api/features/disabled-keys` → **200** (0 ms)
  - `GET /api/ai/insights?acknowledged=false&pageSize=5` → **403** (0 ms)
  - `GET /api/payroll/companies` → **200** (0 ms)
  - `GET /api/payroll/reports/summary` → **200** (0 ms)
  - `GET /api/payroll/overview?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/readiness?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/companies` → **200** (0 ms)
  - …and 12 more, in `manifest.json`
- **Persisted (fresh GET)** — status=`"Approved"`, approvalLevels=`["FinanceReview:Approved","PayrollReview:Approved"]`, distinctApprovers=`2`
- **Screen assertion** — the settled screen showed Payroll run has been approved and is ready to lock.
- **Image** — `img/08-finance-approval.jpg`, sha256 `3b1f51e3ba77aae8f3ee5886724a728031ca6f3f621fef35b564925a016f224c` (original sha256 `a2811791300e8c2f008263145e4b981feb407cc60089b43b5e6fc8a69c1b0893`, 82947 bytes)
- **Settled** — after 383 ms; waited out 0 request(s) and 0 loading indicator(s); 0 node(s) redacted

![The Finance Approver — a different authorised person — gives the final approval](img/08-finance-approval.jpg)

## 9. REFUSAL — open the bank export while the run is Approved but not yet Locked

- **Actor** — IntelliFlow Administrator (Admin), `ad***@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:32.881Z (1621 ms)
- **Business record** — PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa`
- **Expected** — Create Payment Batch is disabled, and the screen says why, naming the run's actual current status rather than a generic message. No batch exists.
- **Observed** — Create Payment Batch is disabled and the screen states "This run is Approved." — the lock, not the approval, is the gate.
- **API**
  - `GET /api/help-texts` → **200** (0 ms)
  - `GET /api/auth/me` → **200** (0 ms)
  - `GET /api/tenant-admin/localization` → **200** (0 ms)
  - `GET /api/features/disabled-keys` → **200** (0 ms)
  - `GET /api/features/modules` → **200** (0 ms)
  - `GET /api/notifications` → **200** (0 ms)
  - `GET /api/payroll/companies` → **200** (0 ms)
  - `GET /api/payroll/reports/summary` → **200** (0 ms)
  - `GET /api/ai/insights?acknowledged=false&pageSize=5` → **200** (0 ms)
  - `GET /api/payroll/overview?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/readiness?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/runs?page=1&pageSize=100` → **200** (0 ms)
  - …and 3 more, in `manifest.json`
- **Persisted (fresh GET of the run's payment batches — none may exist yet)** — paymentBatchesForThisRun=`0`, runStatusAtRefusal=`"Approved"`
- **Screen assertion** — the settled screen showed /A payment batch can only be created once the run is/
- **Image** — `img/09-refusal-batch-before-lock.jpg`, sha256 `1e04c3acd80be028c967069ee2c3d5d23d0f63f191327b504f6d0d3ad5612e84` (original sha256 `1e04c3acd80be028c967069ee2c3d5d23d0f63f191327b504f6d0d3ad5612e84`, 66089 bytes)
- **Settled** — after 428 ms; waited out 0 request(s) and 0 loading indicator(s); 0 node(s) redacted

![REFUSAL — open the bank export while the run is Approved but not yet Locked](img/09-refusal-batch-before-lock.jpg)

## 10. The Finance Approver locks the approved run

- **Actor** — IntelliFlow Finance Approver (Finance Approver), `fi*****@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:34.502Z (1549 ms)
- **Business record** — PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa`
- **Expected** — The run reaches Locked, carries a lock timestamp, and offers no further lifecycle control.
- **Observed** — The run is Locked and exposes no Lock or Process control.
- **API**
  - `GET /api/payroll/companies` → **200** (0 ms)
  - `GET /api/payroll/runs?page=1&pageSize=100` → **200** (0 ms)
  - `POST /api/payroll/runs/a9e97248-45d6-41c4-ad47-2db6759327fa/lock` → **200** (0 ms)
  - `GET /api/payroll/runs?pageSize=100` → **200** (84 ms)
- **Persisted (fresh GET)** — status=`"Locked"`, lockedAtUtc=`"2026-09-30T17:00:34.646487Z"`
- **Screen assertion** — the settled screen showed /Locked/
- **Image** — `img/10-run-locked.jpg`, sha256 `9edd9bd9116b7f02a607b0b0e33bafb17d38bee80ad340f2f44d831dcaff5cc8` (original sha256 `9edd9bd9116b7f02a607b0b0e33bafb17d38bee80ad340f2f44d831dcaff5cc8`, 70493 bytes)
- **Settled** — after 415 ms; waited out 0 request(s) and 0 loading indicator(s); 0 node(s) redacted

![The Finance Approver locks the approved run](img/10-run-locked.jpg)

## 11. Generate the payslips for the locked run

- **Actor** — IntelliFlow HR Manager (HR Manager), `hr*******@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:36.051Z (2511 ms)
- **Business record** — PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa`
- **Expected** — Exactly 14 payslips, one per distinct employee, each with a real payslip number, and each published to employee self-service (the run is locked, so the month is already being paid).
- **Observed** — 14 payslips, 14 distinct numbers, 14 distinct employees.
- **API**
  - `GET /api/help-texts` → **200** (0 ms)
  - `GET /api/auth/me` → **200** (0 ms)
  - `GET /api/tenant-admin/localization` → **200** (0 ms)
  - `GET /api/features/modules` → **200** (0 ms)
  - `GET /api/notifications` → **200** (0 ms)
  - `GET /api/features/disabled-keys` → **200** (0 ms)
  - `GET /api/ai/insights?acknowledged=false&pageSize=5` → **403** (0 ms)
  - `GET /api/payroll/companies` → **200** (0 ms)
  - `GET /api/payroll/reports/summary` → **200** (0 ms)
  - `GET /api/payroll/overview?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/readiness?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/runs?page=1&pageSize=100` → **200** (0 ms)
  - …and 4 more, in `manifest.json`
- **Persisted (fresh GET)** — payslips=`14`, distinctEmployees=`14`, distinctPayslipNumbers=`14`, publishedToEss=`14`
- **Screen assertion** — the settled screen showed Total Payslips
- **Image** — `img/11-payslips.jpg`, sha256 `db7b00b68733806d0a05088d92675ba5e76f5ac5672758d0503afbd18fa896dc` (original sha256 `4c04843659e3021e9ba6913aeaf351b84791b538ecef0d5977b7f3d4494b45cc`, 100702 bytes)
- **Settled** — after 410 ms; waited out 0 request(s) and 0 loading indicator(s); 14 node(s) redacted

![Generate the payslips for the locked run](img/11-payslips.jpg)

## 12. Create the payment batch now that the run is locked

- **Actor** — IntelliFlow Administrator (Admin), `ad***@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:38.562Z (2371 ms)
- **Business record** — PayrollPaymentBatch `f7bc2787-93da-4ed3-af79-821f63346972`
- **Expected** — The control is enabled, the blocking notice is gone, and the batch's total equals the run's net pay with one payment line per employee.
- **Observed** — Batch PAY-202609-170039 created in Draft for SAR 119,730.47.
- **API**
  - `GET /api/help-texts` → **200** (0 ms)
  - `GET /api/auth/me` → **200** (0 ms)
  - `GET /api/tenant-admin/localization` → **200** (0 ms)
  - `GET /api/features/disabled-keys` → **200** (0 ms)
  - `GET /api/notifications` → **200** (0 ms)
  - `GET /api/features/modules` → **200** (0 ms)
  - `GET /api/ai/insights?acknowledged=false&pageSize=5` → **200** (0 ms)
  - `GET /api/payroll/reports/summary` → **200** (0 ms)
  - `GET /api/payroll/companies` → **200** (0 ms)
  - `GET /api/payroll/overview?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/readiness?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/runs?page=1&pageSize=100` → **200** (0 ms)
  - …and 5 more, in `manifest.json`
- **Persisted (fresh GET)** — batchId=`"f7bc2787-93da-4ed3-af79-821f63346972"`, batchNumber=`"PAY-202609-170039"`, paymentRecords=`14`, sumOfPaymentLines=`119730.47`, recordStatuses=`["Pending"]`
- **Screen assertion** — the settled screen showed /Draft/
- **Image** — `img/12-payment-batch.jpg`, sha256 `6551d131d1fc82362f6aec05fcccda1b9157d688381072b9bfaa93865307d6f8` (original sha256 `9fe62cd2153a25cfb3ac2d7527882fea0de51e060af0e5bad87812e807f397a1`, 93415 bytes)
- **Settled** — after 486 ms; waited out 0 request(s) and 0 loading indicator(s); 14 node(s) redacted

![Create the payment batch now that the run is locked](img/12-payment-batch.jpg)

## 13. Generate the WPS/SIF bank file (generation only — this must not pay anybody)

- **Actor** — IntelliFlow Administrator (Admin), `ad***@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:40.933Z (1952 ms)
- **Business record** — PayrollPaymentBatch `f7bc2787-93da-4ed3-af79-821f63346972`
- **Expected** — The batch reaches File Generated with a named file, a content hash, one record per employee and a total equal to the run's net pay. The run stays Locked, the batch is not settled, and no payment record says paid.
- **Observed** — mudad_wps_0000000000_202609.xml: 14 records, total 119730.47, sha256 aae7df796a426369…
- **API**
  - `POST /api/payroll/payment-batches/f7bc2787-93da-4ed3-af79-821f63346972/wps-file` → **200** (0 ms)
  - `GET /api/help-texts` → **200** (0 ms)
  - `GET /api/auth/me` → **200** (0 ms)
  - `GET /api/features/disabled-keys` → **200** (0 ms)
  - `GET /api/features/modules` → **200** (0 ms)
  - `GET /api/tenant-admin/localization` → **200** (0 ms)
  - `GET /api/notifications` → **200** (0 ms)
  - `GET /api/payroll/reports/summary` → **200** (0 ms)
  - `GET /api/ai/insights?acknowledged=false&pageSize=5` → **200** (0 ms)
  - `GET /api/payroll/companies` → **200** (0 ms)
  - `GET /api/payroll/overview?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/readiness?year=2026&month=9` → **200** (0 ms)
  - …and 5 more, in `manifest.json`
- **Persisted (fresh GET of the run, the batch and every payment record — the "nobody was paid" check)** — runStatus=`"Locked"`, batchStatus=`"FileGenerated"`, batchWpsStatus=`"Generated"`, paymentRecordStatuses=`["Pending"]`, anyRecordMarkedPaid=`false`, settleEndpointCalled=`false`
- **Screen assertion** — the settled screen showed File Generated
- **Image** — `img/13-bank-export-generation-only.jpg`, sha256 `f4a4b49b6d4af517dc1f873cb512800c54849593b5d1841fcb90f4d8d108232c` (original sha256 `f4a4b49b6d4af517dc1f873cb512800c54849593b5d1841fcb90f4d8d108232c`, 60839 bytes)
- **Settled** — after 450 ms; waited out 0 request(s) and 0 loading indicator(s); 0 node(s) redacted

![Generate the WPS/SIF bank file (generation only — this must not pay anybody)](img/13-bank-export-generation-only.jpg)

## 14. Reconcile the displayed totals against the source records

- **Actor** — IntelliFlow Finance Approver (Finance Approver), `fi*****@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:42.885Z (1741 ms)
- **Business record** — PayrollRun `a9e97248-45d6-41c4-ad47-2db6759327fa`
- **Expected** — The reconciliation report's current-period gross and net equal the run's own totals, which equal the sum of the per-employee salary slips, which equal the sum of the payment lines, which equal the wage file total.
- **Observed** — Reconciliation: headcount 14, gross 138250.00, net 119730.47, 0 flagged variance(s).
- **API**
  - `GET /api/help-texts` → **200** (0 ms)
  - `GET /api/auth/me` → **200** (0 ms)
  - `GET /api/features/disabled-keys` → **200** (0 ms)
  - `GET /api/tenant-admin/localization` → **200** (0 ms)
  - `GET /api/notifications` → **200** (0 ms)
  - `GET /api/features/modules` → **200** (0 ms)
  - `GET /api/ai/insights?acknowledged=false&pageSize=5` → **403** (0 ms)
  - `GET /api/payroll/reports/summary` → **200** (0 ms)
  - `GET /api/payroll/companies` → **200** (0 ms)
  - `GET /api/payroll/overview?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/readiness?year=2026&month=9` → **200** (0 ms)
  - `GET /api/payroll/companies` → **200** (0 ms)
  - …and 6 more, in `manifest.json`
- **Persisted (fresh GETs of the run, its salary slips, the bank payment lines and the reconciliation report)** — runTotalNet=`119730.47`, sumOfSalarySlipNet=`119730.47`, sumOfBankPaymentLines=`119730.47`, reconciliationReportNet=`119730.47`, netShownOnApprovalScreen=`119730.47`, allFiveAgree=`true`
- **Screen assertion** — the settled screen showed /Reconciliation|Variance|Headcount/i
- **Image** — `img/14-reconciliation.jpg`, sha256 `3d1164578b86b1d51bcd2e13cf5c441b52715b7c6ff93bb3050e665160006c92` (original sha256 `ded8b5f4e9a312386edba51e90afeb1baf7d7c0b06dad0f9ea46260ef508ec5b`, 66521 bytes)
- **Settled** — after 452 ms; waited out 0 request(s) and 0 loading indicator(s); 0 node(s) redacted

![Reconcile the displayed totals against the source records](img/14-reconciliation.jpg)

## 15. DEFECT TRIPWIRE — the same incomplete KSA expat, created without a country, activates anyway

- **Actor** — IntelliFlow Administrator (Admin), `ad***@intelliflow.com`, tenant `intelliflow`, scope `group`
- **When (UTC)** — 2026-09-30T17:00:44.626Z (503 ms)
- **Business record** — Employee (Draft, wrongly activatable) `45`
- **Expected** — SHOULD be the same 422 as R1: the employee is attached to a KSA legal entity, so the KSA floor should apply. It does NOT. This step records the product as it is today — an HTTP 200 activation — and is written to FAIL the day the gap is closed.
- **Observed** — Employee #45 — same KSA legal entity, same Indian nationality, same absent Iqama and GOSI reference as R1 — was stored with an EMPTY countryCode and ACTIVATED with HTTP 200. R1's 422 differs only by a complianceRecords entry naming SA.
- **API**
  - `GET /api/companies?page=1&pageSize=10` → **200** (29 ms)
  - `POST /api/employees` → **201** (149 ms)
  - `POST /api/employees/45/activate` → **200** (224 ms)
  - `GET /api/employees/45` → **200** (95 ms)
- **Persisted (fresh GET of the employee — it is ACTIVE with no country, no Iqama and no GOSI reference)** — employeeId=`45`, status=`"Active"`, countryCode=`""`, iqamaNumber=`""`, gosiReference=`""`, activatedDespiteMissingStatutoryData=`true`
- **Image** — **NO CAPTURE.** Driven through the API, like R1, so there is no screen of it. The two API calls below, differing only in the absent complianceRecords, are the whole of the evidence.

## Not run

Areas this story did not execute. Recorded so that no one reads their absence as a pass.

- **The UI path for the incomplete-data refusal (R1)** — Not run. R1 was driven through the API, so the bundle has no image of it and does not claim one. Whether the employee form surfaces the same 422 legibly is untested here.
- **Payment settlement (marking employees paid)** — Deliberately not run. POST /api/payroll/payment-batches/{id}/settle is the endpoint that marks a batch and its records paid; this story stops at file generation, and step 10 records that nothing was marked paid. Settlement has NO evidence here and must not be read as covered.
- **Bank submission and the bank's own response** — Not run: no bank or WPS endpoint is reachable from a disposable stack. The file is generated and hashed; whether a bank would accept it is untested here.
- **Payroll for the other fixture tenants (Ras Al-Manar, Almarai group, Tata group)** — Not run. This story is one tenant, IntelliFlow, end to end. Multi-company group payroll is covered by e2e/group-company/ as assertions, not as evidence.

