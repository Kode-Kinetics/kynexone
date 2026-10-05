# Separate loan processing

The user approved the separate loan lifecycle proposed in the preceding code review and requested implementation with Finance, Audit, HR Finance, HRM, and Consultant SME perspectives. The CTO integration owner maintains the shared contract, database changes, payroll boundary, and final verification.

## Accepted scope

Loans remain under Loans, Advances & Bonuses, with a dedicated Loan Payments workspace. New loans default to BankTransfer repayment. DirectDebit and Cash also use separate receipts; PayrollDeduction is an explicit alternative. Existing loans retain PayrollDeduction, including imported opening balances. Existing active loans are not automatically converted because past payroll consumption must remain reversible and attributable.

Application creates a Pending loan and a Finance approval step. Completing approval sets Approved, meaning awaiting disbursement. Approval records no cash movement and creates no repayment obligation. Loan types without approval requirements start at Approved.

Finance selects approved loans into a payment batch for one company and currency. The batch snapshots amounts and bank instructions, moves Draft → Approved → Paid, and requires a different user to approve it. Draft/Approved batches can be cancelled before payment. A loan cannot belong to two live batches. The export is payment instructions for external processing; it is not WPS and does not send money. Finance records a payment reference and actual date after external confirmation. Only then do loans become Active, balances and cent-exact schedules appear, and debit loan receivable / credit bank posts in the payment period.

Separate repayments use dated, referenced receipts. A receipt cannot exceed outstanding balance, precede disbursement, use negative/sub-cent amounts, or post into a closed period. Repeating the same reference and details is safe; changing the amount under an existing reference is refused. Receipts allocate cumulatively to the oldest open installments. Full recovery settles the loan. Legacy manual routes cannot bypass these controls. Payroll-linked loans use payroll recovery, with no manual receipt path that could race salary processing.

## Boundaries and controls

Regular payroll, final settlement planning, and final settlement residual-debt processing include only PayrollDeduction loans. Standalone loans remain visible in loan balances and employee summaries after an employee leaves, requiring separate collection. Advances and bonuses retain their existing workflows.

New batch and receipt data use tenant and company filters, scoped employee checks, finance role authorization, maker/checker separation, durable audit records, and per-aggregate transactions/advisory locks. Composite tenant foreign keys and uniqueness constraints reinforce reservation and receipt controls.

## Alternatives considered

An off-cycle payroll run would reuse salary payment infrastructure but would retain the payroll dependency the customer asked to remove. A simple Mark Paid button would separate screens but omit approved payment instructions and duplicate-payment controls. Dedicated loan batches with separate receipts provide the requested complete workflow within the existing finance module.

## Verification and delivery

Verify approval without cash, reservation conflicts, maker/checker, bank confirmation exactly once, cancellation release, partial and final receipt allocation, conflicting receipt replays, overpayments, closed periods, scope denial, payroll exclusion, and legacy payroll behavior. Run backend tests, TypeScript checks, UI journey checks, and disposable PostgreSQL migration checks. Do not change live customer data or deploy as part of local implementation.
