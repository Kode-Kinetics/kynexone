# Separate Loan Processing Implementation Plan

> Execution: user-authorized SME agents work in separate file scopes; CTO reviews and integrates each result using Superpowers parallel-work and verification-before-completion guidance.

**Goal:** Deliver separate loan payment batches and repayment receipts with no automatic salary recovery for separately collected loans.

**Architecture:** Extend the current ASP.NET Core loan module and React loan screen. Approval authorizes; external payment confirmation disburses; referenced receipts recover. Preserve existing loan repayment modes during migration.

**Tech Stack:** .NET 8, EF Core, PostgreSQL, Next.js/React, TypeScript, xUnit, Playwright.

## Work and ownership

- [x] Finance SME: extend `Models/LoansAdvancesBonuses.cs`, `Application/Finance/FinanceDtos.cs`, and `Controllers/Finance/LoansController*.cs` with guarded approval, batch, export, confirmation, and receipt operations.
- [x] HR Finance SME: extend `frontend/src/api/loans.ts` and `frontend/src/views/LoansPage.tsx` with application repayment method, pending payout queue, batch actions, and receipt history.
- [x] HRM SME: review labels, employee visibility, next actions, and employee departure behavior after UI implementation.
- [x] CTO: wire tenant/company-aware entity mappings in `Data/ZayraDbContext.cs`; generate EF migration and review legacy defaults and foreign keys.
- [x] CTO: filter all four loan selection paths in `Controllers/PayrollController.cs` by `RepaymentMethod == "PayrollDeduction"`, including final settlement.
- [x] Audit SME: add `Zayra.Api.Tests/StandaloneLoanWorkflowTests.cs` and payroll regression coverage; prove no approval cash, exact payments/receipts, scope denial, and duplicate safety.
- [x] Consultant SME: review implementation against the accepted lifecycle and raise concrete control gaps for correction.
- [x] CTO: inspect feature diffs; run the non-integration backend suite, targeted PostgreSQL tests, migration discovery/pending-change checks, and frontend TypeScript/UI checks. Record actual failures and fixes.
- [x] CTO: publish [operations and delivery notes](../standalone-loans-operations.md) with migration requirements and verification evidence.

## Final focused verification

- Backend: 156 focused tests passed after the final payroll residual and nonmonthly-payroll guards, including the legacy/fractional-EMI accounting regressions.
- PostgreSQL: all six FinanceDecisionConcurrencyPostgresTests passed, including simultaneous batch reservations, disbursement confirmations, and receipt submissions.
- EF reports no pending model changes. Disposable PostgreSQL upgrade, downgrade, and re-upgrade assertions passed; old balances and repayment modes were preserved.
- Frontend: TypeScript passed; both desktop/phone API-fixture journeys passed after final reference-limit corrections. Pure workflow-helper tests cover ordered role approvals and readable API errors.
- Independent reviews led to loan-specific currency rendering, overdue repayment actions, frequency alignment, and consistent reference length limits.
- Full repository suite is not green: unrelated employee and seed security-regression failures remain documented in the operations notes. No unrelated worktree changes were reverted or rewritten to conceal them.

No production deployment or live data conversion is included. Existing user edits are preserved.
