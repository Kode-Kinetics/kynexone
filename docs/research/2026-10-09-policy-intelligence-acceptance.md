# Policy intelligence acceptance contract

Date: 2026-10-09. Status: acceptance specification, not a record of completed implementation or professional certification.

Scope: use an organization's existing HR documents to propose company setup, then make approved policy documents available to employees through Kody. Reuse the existing setup apply path and policy-document service. This document does not authorize production deployment or claim statutory defaults have been independently verified.

## Two connected, separately controlled workflows

**Setup:** upload/paste source → extract readable text and provenance → propose typed settings → resolve omissions/conflicts → review exact change set → existing authorized setup Apply → persisted records and audit receipt.

**Employee knowledge:** retain source version → select company/audience and effective dates → authorized publication → retrieve only applicable published excerpts → Kody answer with document/version/clause citations. Applying setup must not implicitly publish the document, and publishing must not implicitly change operational configuration.

Both flows must identify the same source version where they use the same document. Draft sources, confidential appendices, unsupported clauses and superseded documents must not become employee answers merely because extraction succeeded.

## Source and extraction acceptance

- Reuse supported PDF/DOCX/TXT parsing; accepted extensions must match actual parsers. Reject empty, unreadable, malformed or oversized input with an actionable message. An image-only PDF is not a successful text extraction. OCR, if added, needs explicit quality/coverage reporting.
- Preserve source identity/version and content hash, uploader, creation time, original name, available page/section anchors and extracted text. State whether the original file is retained or only extracted text; do not imply a downloadable original exists when it does not.
- Treat document content as untrusted data. Embedded instructions cannot broaden access, invoke tools, change statutory rules, publish documents or apply setup.
- Show each proposed value with supporting text and one of: **extracted**, **suggested**, **missing**, **conflicting**, or **unsupported**. These labels must derive from evidence and runtime capability, not a model's confidence claim alone.
- Do not turn “not mentioned” into zero, false, no entitlement, or deliberate exclusion. Existing null-list versus empty-list semantics must survive extraction and review. Manual edits take precedence and retain their reason/provenance.
- AI transmission remains an explicit choice with an honest explanation of what is sent. No silent provider fallback that broadens sharing. Failed extraction or unavailable AI must preserve source and manual setup without claiming AI completion.

## Typed configuration coverage

| Area | Required review coverage | Completion rule |
|---|---|---|
| Company and organization | Legal entity, country, currency, branches, departments, cost centers, designation/grade relationships | Resolve references and entity scope; do not invent registration data or monetary facts from industry alone. |
| Working arrangements | Work pattern, rest days, shifts/breaks, capture methods, thresholds/rounding, applicable population | Distinguish an attendance policy from activating devices, geofences or check-in channels. |
| Leave | Type, entitlement, balance year, accrual frequency, joining/leaving proration, probation availability, population, weekends/holidays, encashment | Separate entitlement, earned balance, available balance and accounting liability. Missing rounding, service bands, carryover or eligibility clauses remain unresolved/unsupported as appropriate. |
| Overtime | Cash/compensatory treatment, wage basis/components, divisor, thresholds/caps, rounding, day categories, approval | Compare configuration and payroll calculation; prevent unintended cash-plus-compensatory double benefit. Do not guess jurisdictional rates. |
| Pay and grades | Salary frequency/currency, grade range, pay components, pay cycle/cutoff/payday | Salary bands are guidance, not actual employee salary or a market benchmark. Nonmonthly source rules cannot be coerced into monthly salary values. |
| Benefits | Plan, grade eligibility, effective dates, enrollment, employer cost and employee contribution clauses | Identify separately what creates a catalog plan, assigns eligibility, enrolls employees or posts deductions. Unsupported contribution/coverage clauses remain visible. |
| Approval/reporting | Supervisor/department/HR roles, thresholds, delegation, escalation, self-approval restrictions | Preferences cannot be represented as activated operational routing. |
| Localization and publication | Language, timezone, company/audience, effective version | Localization defaults do not resolve conflicting policy versions or statutory applicability. |

The review must state what can be configured now, what needs a decision and what cannot yet be enforced. “All features configured” is unacceptable while unsupported policy requirements remain.

## Explicit runtime boundaries at the reviewed baseline

Baseline evidence: `docs/qa/2026-10-09-company-policy-setup.md`, `Application/Setup/SetupAssistantContracts.cs`, and existing setup/leave tests.

- Leave balances use January–December; anniversary/custom dated leave periods are not supported.
- Carryover caps/expiry have no supported rollover engine in setup; do not store decorative rules.
- Salary storage/setup assumes monthly amounts. Custom/nonmonthly pay calendars require separate runtime work.
- Monthly accrual has explicit invocation; an automatic scheduler and missed-month catch-up are not delivered by document extraction.
- Benefits setup creates supported plans and grade eligibility, not all enrollments/contributions/payroll deductions.
- Approval and management preferences do not establish every operational workflow. Attendance preferences do not activate devices/geofences.
- Posted accruals and closed payroll must not be silently rewritten. Retroactive source changes require a traceable adjustment process.

Extraction may recognize these requirements and retain their source; recognition does not remove the runtime boundary.

## Publication and Kody acceptance

- Processing status and publication status are separate. Only reviewed, published, currently applicable versions are eligible for employee retrieval; future, withdrawn and superseded versions are excluded unless an authorized historical-policy workflow explicitly requests them.
- Tenant, company and audience filters are enforced server-side before retrieval/model transmission. Permission to ask Kody does not grant access to every tenant document. A restricted source's title, snippet, citation or existence must not leak through answers or listing.
- Each answer identifies the source document, version/effective date and available clause/page. Returned citations must refer to retrieved authorized sources; arbitrary model-generated document references are not accepted.
- General policy explanation uses published clauses. “What is my balance?” uses the authorized employee's live balance service; a handbook cannot substitute for personal ledger data. Where employee-specific retrieval is not implemented, Kody must say so and link the relevant self-service screen.
- Missing/conflicting evidence produces an explicit limitation and HR next action. Kody must not invent rules, adjudicate eligibility from incomplete facts, or perform writes based on document instructions.
- English/Arabic source/question combinations must be tested. A translated explanation preserves units, dates, exceptions, source language and citations.

## P0 integration gates

These are required test scenarios, not claims of current passing coverage.

| Gate | Required evidence |
|---|---|
| Tenant isolation | Real database requests for tenant A cannot read, extract, reference, publish, apply or cite tenant B's document/version IDs. No text reaches a provider on a denied request. |
| Company/audience isolation | Two companies in one tenant, employee and HR audiences, and an employee moved between companies: retrieval uses current authorized scope and never leaks excluded titles/snippets. |
| Publication gate | Upload/extract/apply leave a source private; only authorized publication enables applicable employee answers. Future/superseded/withdrawn versions are excluded; cached answers respect revocation. |
| Typed-setting gate | Unsupported leave year/pay frequency, invalid grade ranges, unknown references, conflicting populations and out-of-range values fail before any operational write. Source omissions do not become explicit zeros. |
| Review binding | Modified source or edited configuration cannot reuse an approval for a different version/hash. Apply validates the exact reviewed proposal through existing module/entity permissions. |
| Atomicity and replay | Simultaneous/repeated apply preserves one logical change, no duplicate benefit/leave/grade records, no half-applied company, and consistent source/config audit references. |
| Injection and citation integrity | Hostile document/question cannot trigger writes, include secret source text, change access filters or cite unauthorized/fabricated sources. |
| Failure recovery | Parser/provider timeout, malformed output, oversized/empty scan and lost browser response leave a truthful recoverable state; retry cannot publish or apply implicitly. |
| Financial immutability | Source replacement cannot change posted leave transactions or closed payroll. Prospective changes and explicit corrections are distinguishable in audit records. |
| Employee execution | Signed-in employee asks against persisted published documents, receives applicable citations and is denied HR-only content. This requires real auth/database/API/browser evidence, beyond intercepted frontend fixtures. |

## Representative finance calculations

The following are arithmetic fixtures with explicitly chosen inputs, not recommended statutory defaults. Responsible HR/finance owners must approve the applicable policy and valuation basis before real use.

| Scenario | Inputs and expected result |
|---|---|
| Midyear joining | 24 days/year, monthly accrual, inclusive calendar-day proration; joining 16 June 2026 gives June `24 / 12 × 15 / 30 = 1.00` day. July–December add 12.00 days; year total 13.00, not 24.00. No accrual before joining. |
| Leap February | Same 24-day policy; join 15 February 2024: `2 × 15 / 29 = 1.03448…`, posted 1.03 at the existing two-decimal convention. Join 15 February 2025: `2 × 14 / 28 = 1.00`. |
| Joining and leaving | Join 11 April and last working day 20 April under the same policy: `2 × 10 / 30 = 0.666…`, posted 0.67. Subsequent months post nothing. |
| Replay and correction | Repeating an accrual period creates no new transaction. Changing entitlement after a period posted does not recalculate that posting; an authorized correction is a separate audited transaction. |
| Split overtime basis | Approved fixture: wage 30,000, basic 18,000, divisor 240, multiplier 1.5; existing calculator gives hourly `125 + 75 × 0.5 = 162.50`; two hours = 325.00. A displayed formula of `125 × 1.5` would incorrectly imply 187.50/hour. Jurisdiction/rule applicability must be independently resolved. |
| Benefit cost separation | Approved fixture: employer contribution 600/month and employee deduction 100/month. Employer contribution is 600; employee net pay reduces by 100; combined plan funding is 700. Creating a plan alone must post neither amount. |
| Band versus salary | Band 4,000/5,000/6,000 means min/mid/max guidance. An employee salary of 5,400 remains 5,400 unless separately authorized; selecting that grade does not automatically pay its midpoint. |

Calculation previews must disclose inputs, units, rounding and applicable source/configuration version. Compare preview output with the actual runtime calculation and ledger/payslip, not an independent UI formula.

## Release evidence

Record exact candidate SHA, migration requirements, source/API/frontend parity, targeted integration results, real signed-in HR and employee scenarios, audit receipts, unresolved gaps and rollback behavior. Keep source-only review, fixture browser testing, real persisted integration and deployed acceptance separate. Approvals are the organization's authorized decisions, not a claim of external SME certification.
