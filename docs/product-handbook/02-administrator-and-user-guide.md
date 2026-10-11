# Administrator and User Guide

This guide describes the controlled operating sequence for the reviewed product. Screen names follow the source documentation and may vary with enabled modules and role. Complete each process with a fresh reload or server read; a success toast alone is insufficient evidence of a saved business outcome.

## 1. Configure an organization

**Owner:** authorized administrator and HR. **Prerequisites:** active tenant, agreed legal entity scope and country/jurisdiction, separate operational approvers and a signed configuration workbook.

Create the company with legal identifiers, currency and applicable jurisdiction. Add branches, departments, cost centers and grades before employee placement. Configure leave, attendance, salary and benefit policies for the intended company. Decide whether the tenant operates as SingleCompany or Group: the group account type enables multi-company surfaces; a commercial company allowance alone does not unlock the behavior. Assign each user an appropriate company scope and permissions.

Save and reload every configuration. Check the resolved company, currency, policy dates and grade defaults from a second authorized session. Then use a company-restricted user to confirm sibling-company records are inaccessible. Avoid changing a default while a linked process is pending unless its governing workflow allows the change. Operational records must retain their original company and history; a personnel transfer is a governed process, not a direct reassignment of financial rows.

**Recovery:** if a record has the wrong placement, correct draft data through the supported editor before activation. If financial or approval history exists, use the relevant amendment or transfer process. Escalate unexpected company visibility with tenant/company identifiers and record IDs, without exporting another company's personnel data.

## 2. Recruit, hire and activate

**Owners:** recruiter, HR and configured offer approver. **Prerequisites:** approved headcount or requisition where required, position/company/grade, employment dates and complete candidate evidence.

Record the candidate and application; capture interview or assessment outcomes; prepare the offer and route it for the configured independent approval. Confirm approved terms before creating the employee and onboarding assignments. During employee creation, review company, grade, salary and benefit defaults, including future eligibility dates. Complete onboarding tasks and required documents, then activate the employee through the supported workflow and verify the linked self-service account.

The acceptance result is one correctly linked employee with approved employment terms, completed required tasks, retained offer history and correct self-service visibility. The first payroll is a separate acceptance point. If a draft's grade or company changes, verify benefit defaults are reconciled and replaced history remains visible. Individual exceptions and financial evidence must not be silently overwritten. Missing mandatory data should produce an actionable refusal; do not bypass activation guards by editing tables.

## 3. Submit and approve time or leave

**Owners:** employee, manager and authorized HR/payroll reviewer. **Prerequisites:** active employment, applicable policy, working calendar and correct approver route.

The employee submits attendance corrections, overtime, leave or project time through the appropriate view. Verify dates, quantities, supporting explanation and current balance before submission. The assigned approver reviews the request within company/team scope. HR resolves policy exceptions through the authorized route. Payroll reviews the resulting payable or deductible effect in the intended period.

Accept normal and refusal scenarios: insufficient balance, overlapping requests, overnight shifts, late entries, duplicate submissions, rejected requests and corrections after payroll lock. For location/selfie attendance, verify consent, device permission, location failure and configured exceptions. A stored photo is not face recognition, liveness detection or proof against impersonation.

**Recovery:** correct a refused request using the displayed validation reason. Withdraw or amend through the supported route. Do not resubmit a successful request after a network timeout until history confirms whether it was persisted. Locked payroll effects require a payroll correction, not silent retrospective edits.

## 4. Manage grade and additional benefits

**Owners:** HR maker, independent approver and employee beneficiary. **Prerequisites:** eligible catalogue plan, company policy, grade placement, payment classification, approval route and dates.

During Add Employee, review eligible grade defaults in Employment and Review. Open **People → employee → Benefits → Manage benefits**, or the Benefits management view. For an additional benefit, select the plan, set limits and coverage dates, provide a confidential reason and, where coverage is ongoing, set a review date. Save changes. Successful proposals are locked; correct failed rows without resubmitting successes.

The approver reviews each proposal in Approvals. Multi-step routes create the entitlement only after the final independent decision. Pending requests do not change the employee package. Use Edit terms for an additional-benefit amendment or Adjust existing benefit for an authorized grade exception. Ending an additional benefit requires a last covered day and a fresh approval; mandatory benefits cannot be removed through this flow. A review date flags review and does not itself end coverage.

Verify both the approval record and the resulting employee assignment after reload. Check that grade defaults remain unchanged, employee-visible terms exclude confidential HR justification, and history identifies the original policy and subsequent decisions. Changed company, grade or policy facts can invalidate a pending proposal: withdraw and resubmit rather than approving stale terms. Tenants using the separate `release_a` package authority must stay on that controlled path; this guide does not certify unfinished offer/renewal exceptions there.

## 5. Submit a benefit reimbursement

**Owners:** employee or authorized HR maker; separate approver; payroll/finance. **Prerequisites:** active reimbursement entitlement, remaining budget, permitted claim window, owned receipt and supported payroll mapping.

Create the claim against the correct benefit plan. Attach the required receipt, amount, date and explanatory fields. Inspect entitlement, reserved or used amount and remaining balance. Pending claims reserve the available budget; a second concurrent request cannot assume the same money remains available. Approve independently after checking evidence and eligibility. Review the approved claim's inclusion in payroll exactly once.

Keep approval, payroll inclusion and payment status separate. An approved claim is not automatically paid. Rejected or withdrawn reservations should release capacity under the implemented lifecycle. A void or replacement payroll must preserve claim lineage and prevent duplicate consumption. Do not reuse another employee's receipt or replay a receipt against another plan.

Dependent age/count limits, academic or fiscal-year budgets, insurer settlement and OCR are not covered by the reviewed delivery. Free-text policy instructions do not implement these rules. If a customer requires them, add explicit executable acceptance criteria before configuration is signed off.

## 6. Prepare, approve and reconcile payroll

**Owners:** payroll preparer, HR/payroll reviewer and independent Finance Approver. **Prerequisites:** complete employee salary structures, applicable country policies, correct statutory rows, reviewed time/leave, approved adjustments and open accounting period.

Select company and period, create the appropriate run and process it. Resolve incomplete salary/bank data and statutory validation failures before approval. Review employee calculations and run totals. Submit through the configured approval chain; a different authorized actor approves and locks the run. Lock freezes the payroll evidence and accounting outcome. Generate payslips and confirm each eligible employee sees only their own slip.

Create payment instructions only when the run meets the required state. Check company, currency, employee count, beneficiary details and total against the locked payroll. Reconcile payroll net to payslip net, payment lines and exported file total. Generated instructions leave cash settlement pending. Record actual external payment only after reliable bank evidence and under the authorized workflow.

Alternative runs include off-cycle, supplementary, correction and replacement behavior in source. Accept YTD continuity, arrears, proration and void/replacement together before using them operationally. The recent GL report describes serialization of lock/void and balanced company journals; verify those protections on the accepted deployment. A correction must retain posted history and use reversal or replacement evidence rather than editing a locked result.

## 7. Process a loan and repayment

**Owners:** HR Manager, optional HR Director, Finance maker, independent checker and Finance recorder. **Prerequisites:** permitted interest-free loan type, eligible company policy, employment/salary facts and intended repayment method.

Open **Loans, Advances & Bonuses → Loans** and preview eligibility. New separate collections use Bank Transfer, Cash or Direct Debit; choose Payroll Deduction only when salary collection is intended. Submit the application. HR Manager approval is required and higher thresholds can add HR Director review. Approval reserves capacity but does not disburse money or establish a cash-funded outstanding balance.

Finance creates a one-company, one-currency payment batch from approved loans. Employee bank instructions are snapshotted. A different authorized checker approves it. Export instructions and arrange transfers externally. Record actual payment dates and references after confirmation. Paid lines alone activate loans; failed lines remain distinct and can be retried after bank evidence confirms nonpayment. Never cancel a partially paid batch wholesale.

Record separate repayments with amount, date, reference and method. Partial receipts reduce the oldest unpaid installments; complete recovery settles the loan. Statements distinguish requested, approved, disbursed, repaid, outstanding and overdue amounts, by currency. Holds preserve principal and stop automatic payroll collection where applicable. Departure does not forgive standalone debt or automatically collect it in final payroll.

For rescheduling or receipt/disbursement reversals, request the controlled correction with reason and effective evidence, obtain a separate approval and verify compensating ledger entries. Do not overwrite receipts or manually convert an existing payroll loan's mode. Interest-bearing loans and automatic write-offs are unsupported in this documented flow.

## 8. Complete offboarding

**Owners:** HR, payroll, finance and access administrator. **Prerequisites:** authorized separation, effective dates, applicable country calculation and complete employee liabilities/entitlements.

Record separation and assigned tasks. Review settlement inputs, end-of-service calculation and remaining payroll-linked obligations. Independently approve the outcome, process the relevant payroll and record external settlement evidence separately. Confirm access removal and retained historical evidence. Standalone loans continue as receivables and require separate finance collection.

Verify partial failures independently: an offboarding task completion must not conceal failed access revocation or unrecorded external payment. Restore or correct only through the authorized process. The full exit-to-payment-to-access-removal journey remains a required customer acceptance gate.

## Sources

- [Organization architecture](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/GROUP_COMPANY_ARCHITECTURE.md)
- [Governed individual benefits](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/quality/additional-benefits-delivery-2026-10-09.md)
- [Benefit policy and claim evidence](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/quality/company-benefit-policies-review-2026-10-09.md)
- [Persisted payroll journey](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/acceptance/payroll-journey/index.md)
- [Loan operations](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/superpowers/standalone-loans-operations.md)
