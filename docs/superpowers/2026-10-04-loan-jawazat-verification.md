# Loan remediation and Jawazat internal workflow — verification

Historical checkpoint from the original mixed worktree. The isolated merge candidate and resolution of the blockers below are tracked in `2026-10-04-merge-readiness.md`; do not treat the counts below as results for that rebased candidate. Production and live-provider prerequisites remain applicable.

CTO decision: retain the implementation locally; **hold merge/deployment**. Finance/Audit, HR Finance/HRM and consultant-style independent agent reviews informed the changes. Superpowers planning, adversarial tests and separate spec/quality reviews were used. The dirty worktree contains unrelated user changes and must not be committed wholesale.

## Delivered scope

- Loan borrower/maker-checker exclusion, lender-company payroll isolation, frozen-frequency eligibility, original-account journal evidence and exact reversal, guarded legacy manual-collection conversion, HR-hold payout race fix, evidence constraints and non-destructive migration guards.
- Jawazat internal exit/re-entry requests, route-specific company policy, HR approval for employer assistance, worker notification without employer-consent gating, own/company/employee-scope visibility, immutable request policy snapshots and provider-unavailable status.
- Dedicated Jawazat policy PATCH preserves unrelated compliance fields. Server records the authenticated reviewer and review time after validating explicit review. Shared approval decisions and typed HR ticket changes are atomic.
- No new tables in `20261004191027_AddLoanJournalEvidenceAndJawazatPolicy`. The earlier standalone-loan feature has four justified tables; the EF budget remains 327. Jawazat reuses existing profile, HR request, approval and notification records. No visa is fabricated.

## Verification evidence

Commands run from repository root unless indicated. TRX files are under `backend-dotnet/Zayra.Api.Tests/TestResults/` (local test artifacts, not source deliverables).

- `cd frontend && npx tsc --noEmit`: passed.
- `cd frontend && npx playwright test --config=e2e/playwright.jawazat.config.ts`: **12/12 passed**, Desktop Chrome and Pixel 7. Policy-only save regression was observed failing before the frontend PATCH change.
- `cd frontend && npx playwright test --config=e2e/playwright.loans.config.ts`: **18/18 passed**, same desktop/mobile coverage. These rendered-browser tests intercept APIs; they do not establish live backend or government connectivity.
- `dotnet ef migrations has-pending-model-changes --project backend-dotnet/Zayra.Api --context ZayraDbContext --no-build`: no pending model changes.
- PostgreSQL final subset (`LoanLifecyclePostgresTests`, `LoanMigrationHistoryPostgresTests`, `FinanceDecisionConcurrencyPostgresTests`, `LoanPayoutHoldConcurrencyPostgresTests`, `JawazatPostgresTests`): **20/20 passed** in `loan-jawazat-final-postgres.trx`. Testcontainers PostgreSQL 16; no customer database. Covers actual advisory-lock contention, duplicate creates, atomic rollback, null payment-method rejection and downgrade evidence preservation.
- Both payout hold regressions first failed against the stale-tracking implementation, then passed after no-tracking authorization reads. Legacy conversion accounting/explicit-HR-hold regressions also produced observed failures before correction.
- Full non-integration run `loan-jawazat-final-full.trx`: 3,115 passed / 6 failed. Five match previously recorded unrelated failures below; the sixth was new Jawazat direct scope parsing. It was corrected to use the existing strict, header-aware resolver and independently re-reviewed. Final post-correction results are recorded below.
- Fresh post-correction targeted build/run: **228/228 passed**, including all Loan/Jawazat matches, FinanceDecisionConcurrencyPostgresTests, EntityScopeResolutionRatchetTests, CompanyScopeBootAssertionTests and TableBudgetRatchetTests. Artifact: `loan-jawazat-final-targeted.trx`. Command: `dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~Loan|FullyQualifiedName~Jawazat|FullyQualifiedName~FinanceDecisionConcurrencyPostgresTests|FullyQualifiedName~EntityScopeResolutionRatchetTests|FullyQualifiedName~CompanyScopeBootAssertionTests|FullyQualifiedName~TableBudgetRatchetTests' --logger 'trx;LogFileName=loan-jawazat-final-targeted.trx' -v quiet`.
- Final post-correction full non-integration run: **3,116 passed / 5 failed / 3,121 total**; only the five known unrelated failures below remain. Artifact: `loan-jawazat-post-correction-full.trx`. Command: `dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-build --no-restore --filter 'Category!=Integration' --logger 'trx;LogFileName=loan-jawazat-post-correction-full.trx' -v quiet`. Independent final loan and Jawazat reviews report no remaining P0/P1/P2 findings within the bounded changed scope; this does not waive the listed release gates.

## Known unrelated full-suite failures

1. `EmployeeModuleTests.ApproveDraft_ActivatesEmployeeCreatesUserAndHistory`: expected 200, received 422.
2. `BypassLintTests.IgnoreQueryFilters_EachCallMustHaveJustificationComment`: existing EmployeesController line 2584.
3. `QueryFilterBypassRatchetTests.NoNewRawQueryFilterBypassMayBeIntroduced`: EmployeesController and HireLeaveBalanceProvisioner changes.
4. `QueryFilterBypassRatchetTests.RatchetBaselineMustNotDriftUpwardsSilently`: existing bypass count 338 versus approved 329.
5. `RawSqlExecutionRatchetTests.NoNewRawSqlWriteMayBeIntroduced`: AuthSeeder has two sites versus one approved.

These were not hidden by raising unrelated baselines or changing user work.

## Release gates and limits

- Live government integration is **not completed or enabled**. Authorised provider contract, technical documentation, secure sandbox access, supported-operation/fee rules and reconciliation tests are required. No government submission or paid transaction occurred.
- Final exit, exit/re-entry cancellation/extension and Iqama renewal are outside this implemented slice.
- Employer-assisted operation requires a configured HR-only `JawazatRequest` workflow through the existing workflow API; see `jawazat-operations.md`.
- A clean isolated change set, resolution of the five unrelated test failures, a backed-up staging upgrade rehearsal and legacy-loan inventory/reconciliation are still required before merge/release. PostgreSQL tests build the current model and exercise specific migration guards/constraints; they do not replace a real customer-shaped migration rehearsal.
- Migrations refuse destructive downgrade when financial/Jawazat evidence exists. Recover through reviewed forward fixes or a tested backup restore. No automatic classification, journal backfill, debt forgiveness or payroll fee deduction was introduced.

UI evidence (local): `/tmp/kynex-jawazat-evidence.L0efyd/`, including `jawazat-hr-phone.png` and `jawazat-worker-desktop.png`.
