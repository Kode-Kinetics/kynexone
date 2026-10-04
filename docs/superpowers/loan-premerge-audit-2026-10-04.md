# Loan pre-merge audit — 2026-10-04

Historical audit findings below were subsequently remediated in the local workspace. See `plans/2026-10-04-loan-audit-remediation.md` and the final verification record. This document preserves the original audit evidence; it is not a claim that these defects remain unfixed. Merge remains gated on isolation of unrelated changes, release checks and staging/legacy-data review.

Decision: **HOLD MERGE**. Read-only production-code review by CTO, Finance/Audit and schema reviewers. No merge, deployment, customer migration, or production-code correction was performed during this audit. Findings below are code-path findings, not claims that new adversarial reproducer tests have been executed.

## Release blockers

1. **P1: Original journal accounts are not retained for recovery/reversal.** `Controllers/Finance/LoansController.Payments.cs:341`, `LoansController.Corrections.cs:89` and `:93` call `LoansController.cs:510` (`PostGlEntry`), which resolves the current account mappings at `:523`. If the receivable mapping changes from A to B after disbursement, later recovery credits B while A remains debited. A reversal also uses current cash/receivable mappings instead of the exact original accounts. Persist journal linkage, recover the original receivable and reverse the exact original journal accounts. Add remapping regression tests.

2. **P1: Borrower can approve a loan entered on their behalf.** `Controllers/Finance/LoansController.cs:193` allows HR/Finance to enter another employee's application; `:342` checks only `CreatedBy` for maker/checker. An HR Manager borrower whose application was entered by someone else passes that restriction. Apply the existing borrower-identity check to initial approval; audit beneficiary exclusion on batch and correction decisions too.

3. **P1: New-company payroll can recover original-company debt.** The four loan selectors in `Controllers/PayrollController.cs:1678`, `:3272`, `:5885`, `:9243` filter by employee, mode and hold, not lending company. A group-scoped operator running company B payroll for a borrower transferred from A can collect A's payroll loan and reduce its balance while posting B's deduction. Transfer review flags alone do not stop these queries. Require lender/run-company agreement unless an explicit intercompany recovery workflow authorizes otherwise. Test regular and final-settlement paths.

4. **P1: Downgrade can destroy payment evidence.** `Migrations/20261004141732_AddStandaloneLoanPayments.cs:186` unconditionally drops lines, receipts and batches. The newer guard in `20261004145931_AddLoanPolicyLifecycleControls.cs:376` checks only change requests and PartiallyPaid/Completed batches, so ordinary Paid batches/receipts can pass through rollback. Guard removal of financial tables whenever evidence exists; require a forward corrective migration. Existing operational warnings are not an enforced rollback safeguard.

5. **P2: Frozen frequency is not used consistently in eligibility.** `Infrastructure/Finance/LoanEligibilityService.cs:85`, `:88`, `:125` uses mutable `LoanType.RepaymentFrequency`, while the loan/schedule retains its original frequency. A Weekly-to-Monthly type edit can understate affordability at approval/release even though actual collection remains weekly. Use the loan's frozen frequency after creation and test type edits in both directions.

6. **P1/P2, customer-data dependent: Existing manual collections need classification before migration.** `20261004141732_AddStandaloneLoanPayments.cs:21` assigns PayrollDeduction to every existing loan. Standalone receipts reject that method (`LoansController.Payments.cs:306`); prior settle/installment-payment routes now reject (`LoansController.cs:419`, `:435`). If existing customers used those manual routes, the upgrade removes their collection path. Inventory actual legacy usage on approved staging data; explicitly classify or implement controlled conversion with reconciled historical balances. No customer database was inspected here, so affected record count is unknown.

## Architecture and database hardening

7. **P2: Parallel change-approval state.** `LoanChangeRequest` is a useful typed payload but `LoansController.Lifecycle.cs:136` and `:209` create/decide it outside the shared `ApprovalRequest` orchestration. Link the payload to the shared workflow for queue/routing/decision ownership, or document and approve an intentional separate bounded workflow. Do not merely delete the payload table and lose domain evidence.

8. **P2: Missing database defense for new states/receipt links.** `Data/ZayraDbContext.cs:3947` maps the change request without a tenant/loan/receipt FK for `RepaymentId`; line states/paid evidence also lack the corresponding checks at `:3913`. Controllers validate these conditions, but another writer can insert inconsistent rows. Add composite relationship and status/evidence checks with migration backfill preflight.

## Four-table verdict

- `loan_disbursement_batches` and `loan_disbursement_lines`: justified. Existing payroll batches require `PayrollRunId`; reusing them directly would couple standalone loans back to payroll.
- `loan_repayments`: justified as an actual cash receipt ledger, distinct from installment schedules. Existing `LoanSettlement` mixes early/normal/waiver events and lacks receipt reference/method/reversal evidence.
- `loan_change_requests`: typed payload is justified; approval orchestration should reuse the existing engine instead of maintaining another independent decision system.

Positive controls: tenant-qualified restrictive loan/batch FKs, one uncancelled instruction per loan, unique receipt references, positive-amount/method checks and serialized payment transitions. These do not eliminate the findings above.

## Verification and remaining release work

Fresh command:

`dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore --filter '(FullyQualifiedName~Loan|FullyQualifiedName~EmployeeSnapshotMaskingTests)&Category!=Integration' -v quiet --logger 'trx;LogFileName=loan-premerge-audit.trx'`

Result: **133 passed, 0 failed**. Passing existing tests did not cover the newly identified cases. The previous delivery full-suite result recorded five unrelated baseline failures; the full suite was not repeated in this audit. Staging comparison, legacy collection inventory, fix-specific adversarial tests and a clean isolated change set remain mandatory before merge. The working tree contains substantial unrelated user changes on `fix/accept-postgres-uri-connection-string`; it must not be committed wholesale.
