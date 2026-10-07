# Separate loan processing — operations and delivery

## Customer workflow

Open **Loans, Advances & Bonuses → Loans**. New applications default to **Bank Transfer** repayment. Cash and Direct Debit also mean separate collections; select Payroll Deduction only when salary recovery is intentionally required. Payroll recovery is monthly only.

1. Check eligibility and submit the application. Every new request requires HR Manager approval, regardless of the older loan-type automatic-approval setting. Policies can require an additional HR Director step above a threshold. Steps must be completed in order, not by the applicant/maker. An approved loan waits for Finance payment; approval alone does not disburse cash or establish an outstanding balance.
2. Finance opens **Loan Payments**, selects approved loans from one company and currency, and creates a batch. Amounts and employee bank details are snapshotted.
3. A different Finance/Finance Approver/Admin user reviews and approves the batch. Finance downloads the payment instructions and arranges the transfer outside KynexOne.
4. After transfers succeed, Finance records actual payment dates and references. Whole-batch confirmation remains available when all instructions succeed together; individual instructions can instead be marked Paid, Failed, or Cancelled. Paid lines alone activate their loans. Failed instructions can be retried with confirmed bank evidence; previously paid lines are not reposted. Batch totals separately show paid, remaining, cancelled, and reversed amounts. Delayed payments may defer the first repayment date, but cannot bring it forward before the approved date.
5. On each separate collection, Finance opens the active/overdue loan and records a repayment receipt with date, amount, reference, and method. Partial receipts reduce the oldest unpaid installments first. Full recovery settles the loan.

The payment-instruction CSV is not a WPS file, bank integration, or evidence that funds were sent. Confirmation records a completed external payment. Do not cancel or retry an instruction until the bank confirms it was not paid. Partially paid batches cannot be cancelled wholesale; cancel only confirmed unpaid instructions. Exports from partial batches contain only remaining instructions, while completed exports retain historical evidence.

## Company policy and eligibility

HR/Admin uses **Loans → Company Policies** to publish a new company/loan-type policy version. Previous versions and application snapshots are retained. Configure service/probation/employment/contract eligibility, fixed and salary-based limits, total exposure, active-loan count, cooldown, overdue/notice restrictions, installment affordability, repayment methods/frequencies, higher approval thresholds, and permissions for exceptions/rescheduling/early settlement.

Zero monetary limits mean no additional policy cap. Loan-type caps still apply: fresh assessments use the lower nonzero amount limit, lower installment limit, and greater minimum-service requirement. Without a company policy, existing tenant/type settings form an explicit baseline. Salary-based limits require a current salary structure in the loan currency. Pending and approved-but-unpaid requests reserve capacity. Prior-company debts count; incompatible currencies fail closed rather than being added together.

Employees can preview their own eligibility and reasons. Managers cannot query another employee's salary-derived eligibility. Creation, each HR approval, export, and payout verify current employment facts against the saved policy snapshot. Allowed soft-policy exceptions require a distinct HR Director/Admin decision bound to that exact policy version; exceptions cannot override invalid amounts, employment status, currency mismatches, or repayment-method restrictions.

## Lifecycle, statements, and corrections

Employee **My Loans**, manager team views, HR, and Finance read the same loan ledger, with role/company/employee scope enforced on the server. Statements distinguish requested, approved, actually disbursed, repaid, outstanding, due, and overdue amounts. Totals remain separated by currency. Cancelled/waived schedule history does not create a current amount due. Transferred employees retain access to their own historical receipts and change history; original-company financial staff retain control of that company's receivable without gaining access to new-company personnel data.

The lifecycle worker checks open loans every minute; payout/export also rechecks before release. Employment status, notice/resignation/withdrawal, company transfer, salary/grade/designation, contract, probation, joining/rehire, and current unpaid leave changes create review flags and targeted notifications. They do not alter principal or silently reschedule repayments. Paid debt remains outstanding after departure. Death/incapacity places collection on hold for authorized review, not automatic write-off. Holds stop automatic payroll recovery for explicitly payroll-linked loans; separately received voluntary repayments can still be recorded. Portal access after separation continues to follow existing account-security rules, not a new entitlement for inactive accounts.

HR can Hold, Continue after eligibility review, or cancel an unpaid, unreserved loan with a reason. Notice-period continuation is possible only when the frozen policy explicitly permits it; closed employment/company mismatch cannot be overridden to release money. Withdrawal/rehire never clears an existing hold automatically.

Standalone rescheduling requires policy permission, a reason, a distinct Finance/Admin approver, a current unchanged balance, and dates/installments within permitted limits. Paid history is preserved, and the remaining exact principal is redistributed. Payroll-linked restructuring remains in the payroll correction process.

Finance can request a receipt or disbursement reversal with effective date, reference, and reason; another Finance/Admin user decides. Receipt records remain visible as reversed and receive a compensating journal. A payout can be reversed only after effective repayments are reversed; it cancels the loan while preserving its instruction, schedule, and audit history. Closed periods, duplicate/conflicting evidence, self-approval, and stale balances are refused. Debt write-offs and legal-entity debt reassignment are not automatic actions.

## Payroll and existing loans

- New standalone loans are excluded from regular salary deductions, final-settlement planning, and final-settlement residual debt handling. Departure does not forgive the balance: Finance must collect it separately.
- Existing loans retain PayrollDeduction, their balances, and their historical payroll behavior. Existing active loans are not automatically converted. Such conversion needs a separately reviewed reconciliation/migration process; changing the database field manually is unsafe.
- Both repayment modes use the separate payout workflow for newly approved loans. PayrollDeduction controls collection, not disbursement.
- The final payroll installment collects any remaining cent residual within the existing affordability cap.
- Advances and bonuses are not converted by this feature. Interest-bearing loans are explicitly unsupported. Legacy direct installment-payment and settlement routes cannot bypass receipt controls; waivers/write-offs are not implemented here.

## Access and controls

Finance/Admin creates batches, confirms external payments, and records receipts. Finance Approver can review/approve/export but cannot perform these cash-recording operations. A separate Finance role is seeded; no user is automatically assigned this role. Assign authorized staff through the existing user administration process, keeping at least two distinct users for maker/checker control.

Tenant/company/employee scope checks apply to batches and loans. A batch requires one company and currency. A loan can belong to only one uncancelled payment batch. Bank instructions cannot change after creation; cancel an unpaid batch and recreate it after correcting employee bank details. Identical paid confirmations and receipt replays are idempotent; conflicting references, overpayments, invalid dates, and closed accounting periods are refused. A bank payment reference is unique per tenant/company; a repayment reference is unique per loan. References have a 160-character limit.

Receipt journals debit Cash/Bank and credit Loan Receivable; payout journals debit Loan Receivable and credit Cash/Bank, using the existing company account resolver and actual payment period. Financial audit events accompany the transitions. Batch/receipt and loan updates share transactions; PostgreSQL advisory locks and database uniqueness protect concurrent requests.

## Release requirements

Local implementation only: no production deployment or customer-data change was performed.

Deploy compatible backend/frontend code with these EF migrations in order, using the established backed-up release procedure:

- `20261004141732_AddStandaloneLoanPayments`
- `20261004142030_AddLoanPaymentReferenceUniqueness`
- `20261004145931_AddLoanPolicyLifecycleControls`

The first two migrations establish separate payments and preserve legacy repayment modes. The lifecycle migration versions policy settings and backfills paid instruction evidence. It reroutes undecided Finance request steps to HR and adds HR review to unpaid previously approved loans lacking HR authorization. Existing active loans, balances, and completed approvals are preserved. Unpaid reserved batches affected by renewed HR review should be cancelled/recreated after approval. The previous employee-draft migrations already present in this worktree remain prerequisites. Existing users may need to refresh their session after seeded role-permission updates.

Do not downgrade after real batches/receipts have been recorded: downgrade drops the new financial tables. The initial standalone-payment downgrade was tested only against disposable synthetic data; the additional policy/lifecycle migration was upgrade-tested, not downgrade-tested. For production problems, preserve records and use a forward corrective release.

## Initial standalone-payment verification (before policy/lifecycle extension)

- Initial full non-integration backend run: **2,988 passed / 5 failed / 2,993 total**. Remaining failures are in the unrelated dirty-worktree employee/seed area: employee activation (`EmployeeModuleTests.ApproveDraft_ActivatesEmployeeCreatesUserAndHistory`), employee query-filter justification lint, two query-filter budget checks, and AuthSeeder raw-SQL budget. This is not a claim of a green full repository suite.
- TypeScript check passed. Desktop and phone Playwright journeys passed using intercepted API fixtures, covering separate batch creation/approval/payment and receipt entry, overdue loans, loan-specific currency, and supported frequencies. These are UI-contract tests, not full-stack bank tests.
- Disposable PostgreSQL 16 migration upgrade preserved a synthetic active loan's 66.67 balance and 33.33 repaid amount, defaulted both existing loans to PayrollDeduction, created exactly one missing Pending Finance step, and installed uniqueness indexes. Downgrade preserved original loans/balances and removed only the synthetic repair step/new schema.
- Final focused backend run: **156/156 passed**, including legacy/fractional-EMI accounting regressions after narrowing final-installment rounding recovery. Real-PostgreSQL concurrency run: **6/6 passed**, including concurrent reservations, paid confirmations, and receipts. Workflow helper unit tests: **2/2 passed**.
- EF pending-model check passed; migration visibility gate found **79/79** migrations. Re-upgrade assertions also passed. The disposable database/container was removed after verification; no customer database was accessed for migration execution.

## Policy/lifecycle delivery verification — 2026-10-04

- Final full non-integration run: **3,039 passed / 5 failed / 3,044 total** (`loan-lifecycle-delivery-unit.trx`). The five failures are the same unrelated baseline employee activation, employee bypass justification, two bypass-budget, and AuthSeeder raw-SQL budget failures listed above. Final loan/privacy-focused run: **133/133 passed**; real-PostgreSQL concurrency/reschedule/correction run: **8/8 passed**. Restricted financial snapshots retain necessary salary/commitment evidence while adversarial allowlist tests exclude bank, identity, medical, disciplinary, and termination secrets; exact lint exemptions do not permit arbitrary employee serialization. No claim of a fully green repository suite is made.
- TypeScript passed; workflow helper checks **3/3 passed**; desktop/phone Playwright journeys **16/16 passed**. The corrected journey uses independent HR Manager approval, Finance batch maker, Finance Approver, and Finance payment recording. Additional journeys exercise employee self-service, unlimited eligibility, immutable policies, employment reviews, rescheduling, explicit exception requests, failed/retried bank instructions, corrections, and cancelled/reversed balance presentation.
- Browser plugin not available: used existing Playwright at `http://localhost:5183/loans`, 1440×900 desktop and Pixel 7 emulation. URL/title, meaningful content, console/runtime errors, responsive screenshots, and interaction state changes were checked. Screenshots are outside the repository under `/tmp/loan-*`. API responses are intercepted fixtures; these tests are not a live bank or complete HTTP backend integration.
- Disposable PostgreSQL upgrade through `20261004145931_AddLoanPolicyLifecycleControls` preserved the active legacy loan's **66.67 outstanding / 33.33 repaid**, all legacy PayrollDeduction modes, completed approval history, and original paid bank reference/date. The two unpaid legacy requests require pending HR Manager review. Existing policy caps became version 1 with the original **5000 amount / 12 installments / 6 months service** limits. The synthetic test database/container was removed afterward; no customer database was migrated.
- EF reports **no pending model changes**; the migration visibility gate passed **80/80**. Independent HRM review confirmed closure of all six findings: eligibility salary inference, transferred-lender access, employee historical receipts/changes, nullable policy limits, cancelled/waived dues, and payroll collection holds.

Finance implemented eligibility and HR routing; HR Finance implemented the UI; Audit implemented lifecycle controls and supplied adversarial tests; HRM and Consultant reviewed the user flow. The CTO integrated schema, payments, corrections, monitoring, and payroll boundaries and verified delivery. Superpowers planning, parallel-work, and verification guidance shaped this implementation and handoff; ASP.NET and React guidance informed the implementation review.
