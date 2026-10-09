# Company benefit policies: market review and delivery scope

Reviewed 9 October 2026 using official product documentation and the current KynexOne implementation. The comparison concerns documented workflows; it is not a claim of complete vendor parity, edition availability, or measured usability superiority.

## What competitors document

| Product | Relevant capabilities | Design consequence |
| --- | --- | --- |
| SAP SuccessFactors Global Benefits | Configurable allowances and reimbursements; approved reimbursement claims map to nonrecurring pay components, and employee contributions can map to deductions. The employee claim view shows entitlement, used and remaining amounts; pending claims reserve the available amount. | Keep enrollment, claim approval and actual payment distinct. Reserve claim budgets before approval and release rejected/withdrawn reservations. |
| Oracle Fusion HCM | Benefit eligibility supports grade and legal entity. Rates/elements can communicate costs and distributions, including tuition reimbursement, to payroll. Receipt policies are documented in the separate Fusion Expenses capability. | Treat each company's payment and evidence rules explicitly. Do not infer that a benefit's name determines its payroll behaviour. |
| Microsoft Dynamics 365 HR | Configurable benefit plans, eligibility, employee/employer contributions and payroll deduction codes. Receipt capture, expense policies and reimbursement workflows are documented under Expense Management. | Offer one clear benefits journey to users while preserving the underlying financial controls and statuses. |

Sources: [SAP payroll integration](https://help.sap.com/docs/successfactors-employee-central/implementing-global-benefits/3b033405d5774e9fadb8b00b0424642c.html), [SAP employee claims](https://help.sap.com/docs/successfactors-employee-central/implementing-global-benefits/79dac2f420c642b2871af2b8d6a6ff5c.html), [Oracle eligibility](https://docs.oracle.com/en/cloud/saas/human-resources/faibf/eligibility-profiles.html), [Oracle benefits rates](https://docs.oracle.com/en/cloud/saas/human-resources/faibf/Chunk1010502095.html), [Oracle Expenses receipt policies](https://docs.oracle.com/en/cloud/saas/financials/26a/faiex/options-for-configuring-expense-policies.html), [Dynamics benefits](https://learn.microsoft.com/en-us/dynamics365/human-resources/hr-benefits-management-overview), [Dynamics Expense Management](https://learn.microsoft.com/en-us/dynamics365/project-operations/prod-exp/expense-management).

SAP additionally documents dependent-based entitlement rules and maximum claim frequency. These are meaningful future comparison criteria, not capabilities this delivery claims to match. [SAP reimbursement configuration](https://learning.sap.com/courses/sap-successfactors-employee-central-global-benefits-academy/configuring-and-using-reimbursements_c3c8f8c9-ddd5-408c-86da-8a02e5d33fe7), [SAP dependent rules](https://help.sap.com/docs/r/6e6f99c62c644fc9a3111a3063dc8a62/latest/en-US/2864e56230144b7f85383662199ed809.html).

## Product decisions

- Retain arbitrary benefit names and custom categories, with no artificial catalogue-count limit. Examples are templates, not a closed list of supported benefits.
- Keep grade defaults at hire and the simple employee checklist. Put company policy configuration in the plan, with conditional fields for coverage, salary allowance, salary deduction or reimbursement.
- An entitlement ceiling is not a cash instruction. A payroll benefit requires an explicit amount/frequency and a dedicated earning/deduction mapping. A reimbursement requires an approved claim.
- School-fee receipts are configurable evidence: required/optional receipt, receipt label, claim window and company instructions. Existing grade/employee ceilings and their periods bound claims; pending and approved amounts consume the balance.
- Freeze policy/version and payroll classification in employee enrollment and claim witnesses. Later catalogue edits apply to future assignments, not to an existing agreement or processed payroll.
- Keep a configured plan's payment method fixed. Use a new plan to switch from reimbursement to allowance, so historical claims and salary budgets cannot be mixed by a concurrent change.
- Present approval status separately from payroll inclusion/payment. An approved claim is not marked paid until payroll payment evidence says so.
- Reuse shared approvals, employee-document storage, audit history and source-linked payroll adjustments. No new tables are required. Preserve the separate `release_a` package authority.

## Acceptance and practical limits

Verification must cover company/tenant isolation, receipt ownership and bytes, duplicate evidence, concurrent budget reservation, approval separation, payroll addition/deduction/tax lines, exactly-once source usage, void/replacement, and desktop/mobile presentation. The local preview must be exercised with persisted data.

This slice does not claim universal benefit coverage: per-child age/count limits, academic/fiscal-year budgets, insurer/provider integrations, external vendor settlement and automatic OCR extraction require separate executable policies. Administrators can name custom plans and supply instructions, but instructions are explanatory text, not executable substitutes for those rules. Loan eligibility and disbursement remain with the loan lifecycle. Payroll amounts use the existing payroll and country-pack rules; a benefit policy does not redefine statutory wage classifications.

## Verification

The isolated PostgreSQL preview has migration `20261009164858_AddCompanyBenefitPaymentPolicies` applied, no pending model changes, and 96/96 migrations visible to EF.

Final verified results:

- Backend: 847 focused benefits, payroll, approval, scope, schema and security tests passed.
- Browser fixtures: 66 desktop/phone cases passed on the final production preview; TypeScript and production build passed; translation coverage 8 passed with 1 existing maintenance skip.
- Supported API payroll proof: 41 checks passed. Engine and legacy replacement payroll both produced gross 10,810, deductions 2,156 and net 8,654. Approved receipt claims reached payroll once; pending claims did not. Supplemental taxable claims produced 200 gross / 20 tax / 180 net and 100 gross / 10 tax / 90 net. The artificial QA tax policy was archived and the prior engine setting restored.
- Supported API adversarial proof: 9 checks passed. Simultaneous claims of 600 + 600 against a 1,000 ceiling persisted only one reservation, leaving 400. Cross-plan receipt replay and other-company receipt access were refused.
- Actual PostgreSQL trigger proof: 8 checks passed, including immutable claim payloads, employee policy snapshots and payment authority, deletion refusal and nonvoid run-rebinding refusal. Every attempted SQL mutation was rolled back.

Durable evidence: [payroll](evidence/benefit-policies-2026-10-09/payroll.json), [claim security](evidence/benefit-policies-2026-10-09/claims-security.json), [PostgreSQL guards](evidence/benefit-policies-2026-10-09/database-guards.json), [HR and ESS journey](evidence/benefit-policies-2026-10-09/hr-ess-journey.json), [final visuals](evidence/benefit-policies-2026-10-09/final-visual.json). Backend test results: `/tmp/kynex-benefit-policy-tests/benefit-policy.trx`.

The persisted desktop HR and phone ESS journey passed: a custom company plan and payroll mapping were created through the UI, a governed individual assignment was approved, and HR/employee claims of 250 and 175 were independently approved. The saved policy cap of 2,000 took precedence over the assigned ceiling of 5,000: 425 approved and 1,575 remaining. There were no browser page/console errors or horizontal overflow. Receipt evidence and approval/payroll statuses were visible. Evidence and screenshots: `/tmp/kynex-benefit-policy-browser/verification.json` and `/tmp/kynex-benefit-policy-browser/01-company-policy.png` through `11-ess-mobile-approved.png`.

The final presentation pass adds catalogue search and frozen payment terms to the additional-benefit approval card, stacks claim cards in the narrow employee panel, and removes the inapplicable legacy empty-deduction message for configured policies. All 66 browser cases passed on that final production preview. The read-only visual refresh verified the actual frozen allowance of 310/month, stacked claim cards, catalogue search and the employee balance, with no page/console errors or horizontal overflow. Screenshots 12–15 are in the same evidence directory.

This is a local preview at http://127.0.0.1:5281/benefits, backed by isolated synthetic QA data. No production deployment was performed.

## Publication gate follow-up

The first PR run exercised 6,456 backend tests and identified four failures outside the earlier focused suite. These were corrected before merge: benefit authorization now uses the shared cached request-scope resolver; immutable audit/benefit checks share one change-tracker scan; the snapshot lint preserves coverage of every `*SnapshotJson` property and its exact loan exemptions, while benefit eligibility, grade defaults and payroll use explicit restricted serializers; and the approval registry test requires the exact seven implemented producers. Final review also added scanner regressions for unsafe loan assignments and exact JSON compatibility checks for the typed benefit witnesses. No security threshold or allowlist was weakened.

The corrections passed 630 focused regression checks, including all four failing classes and the bulk-save performance guard, plus a repeated 9/9 persisted concurrent-claim and cross-company access proof. The complete GitHub suite remains the merge gate in [PR #221](https://github.com/Kode-Kinetics/kynexone/pull/221).
