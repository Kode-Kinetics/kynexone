# Loan policy and employee lifecycle implementation plan

User approved implementation after reviewing the role visibility, company policy, and employee-event scenarios. CTO owns integration; Finance owns eligibility and HR routing; Audit/HRM owns lifecycle controls; HR Finance owns frontend flows. Existing unrelated worktree changes stay intact.

## Contracts and order

- [x] Extend existing LoanPolicy with company scope, immutable versions, monetary/service/employment/repayment rules. Snapshot the applied version and assessment on EmployeeLoan. Finance implements `LoanEligibilityService.EvaluateAsync` and `/api/finance/loans/policies`, `/eligibility`; application and approval enforce assessments. Missing company configuration falls back explicitly to existing tenant/type rules, not invented business limits.
- [x] Require HR Manager request approval, additional HR Director approval above configured limits, and retain separate Finance payout maker/checker. Update default role permissions and migrate only undecided legacy Finance request steps.
- [x] Persist employment snapshots, review flags, and collection holds. `LoanLifecycleService.RefreshAsync` detects employee/offboarding/pay changes without rewriting debt. Background monitoring and payout-time refresh prevent stale approvals from releasing funds. Reviewed continuations retain reason/actor/history; transfers keep original loan company.
- [x] Use LoanChangeRequest for separately approved policy exceptions, repayment rescheduling, receipt reversals, and payout reversals. Preserve transaction history; reject stale balance changes, self-approval, closed periods, and incompatible payroll-linked changes.
- [x] Extend payment lines with Pending/Paid/Failed/Cancelled/Reversed outcomes. Per-line completion affects only the matching loan. Whole-batch actions cannot cancel paid lines or repeat disbursements.
- [x] Render company policy configuration, eligibility reasons, own/team loan statements, repayment history, pending HR decisions, review holds, change requests, line outcomes, and financial correction controls. All access checks remain server-side; totals group by currency.
- [x] Generate EF migration from the model. Upgrade synthetic PostgreSQL data; verify legacy balances, request routing, line backfills, tenant foreign keys, and indexes.
- [x] Verify focused xUnit policy/lifecycle/correction/payroll cases; PostgreSQL duplicate races; TypeScript; desktop/mobile Playwright role journeys. Run broad non-integration suite and report failures honestly. Final: 133/133 loan/privacy-focused; 8/8 PostgreSQL; 16/16 browser; 3/3 frontend helpers; TypeScript passed. Full non-integration: 3,039 passed, 5 unrelated baseline failures, 3,044 total. EF model parity and 80/80 migration visibility passed; synthetic migration database removed. Evidence and release boundaries are in `docs/superpowers/standalone-loans-operations.md`.

## Safety boundaries

No production deployment, live conversion, automatic debt forgiveness, cross-company debt transfer, or unapproved final-payroll recovery. Bank processing stays external. HR policies are configurable business rules, not jurisdiction-specific legal determinations. A released unpaid instruction must not have been submitted to the bank; Finance must verify before cancelling/retrying.

## Verification commands

`dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter 'FullyQualifiedName~Loan'`

`dotnet ef migrations has-pending-model-changes --context ZayraDbContext --project backend-dotnet/Zayra.Api/Zayra.Api.csproj`

`npx tsc --noEmit` and `npx playwright test --config=e2e/playwright.loans.config.ts` from `frontend`.

The Browser plugin skill is unavailable; rendered validation uses the existing Playwright installation. Superpowers planning, bounded delegation, and verification guidance are used; the already-approved design does not require another approval pause.
