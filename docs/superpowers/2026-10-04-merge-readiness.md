# Standalone loans and internal Jawazat workflow — isolated merge record

## Scope and provenance

Integration base: `origin/main` at `6a93b5a2`. Candidate branch: `feat/standalone-loans-jawazat`. This record supersedes the earlier mixed-worktree verification checkpoint, not the remaining production prerequisites.

The original workspace was 183 commits behind main and contained unrelated uncommitted development. Its five recorded test blockers were repaired without relaxing security baselines; its fresh non-integration suite passed 3,126 tests (`merge-gates-full.trx`). Those unrelated employee-draft/leave-provisioning changes were not copied into this feature. The original workspace is preserved.

Only the loan/Jawazat vertical slices and their tests were ported to an isolated worktree. Main's effective-dated employee change application, employee-country derivation, GOSI facts, payroll changes, permission checks and 100-row loan cap remain intact. Migration target models were rebuilt on main's snapshot, retaining non-feature entities rather than replacing the snapshot with the older workspace's model.

Independent integration review found one new change-history permission omission. The endpoint now requires loan read/write permission, including a direct-controller denial check, and has deny/allow regressions. Independent re-review closed that finding. The payment-batch source ratchet checks the distinct loan and payroll company-scope guards; separate loan payments are not payroll batches.

## Database boundary

Four justified new loan tables across the feature: `loan_disbursement_batches`, `loan_disbursement_lines`, `loan_repayments`, `loan_change_requests`. Runtime EF table budget: 323 → 327. No Jawazat tables. Jawazat reuses company compliance profiles, HR requests, approvals and notifications. The V2 baseline schema remains unchanged.

Migration sequence:

- `20261004141732_AddStandaloneLoanPayments`
- `20261004142030_AddLoanPaymentReferenceUniqueness`
- `20261004145931_AddLoanPolicyLifecycleControls`
- `20261004191027_AddLoanJournalEvidenceAndJawazatPolicy`

The upgrade rehearsal starts at main's `20260929065334_AddGosiEntrantCohortFacts` in disposable PostgreSQL 16. It inserts synthetic old-shape pending/approved/active loans, upgrades through early payment tables, inserts historical paid evidence, and applies remaining migrations twice. Assertions preserve 100 principal, 33.33 repaid and 66.67 outstanding, retain bank evidence, avoid fabricated journal links and reject destructive downgrade without losing debt. This is not a customer backup restore or customer-ledger reconciliation.

## Verification

Commands run in the isolated worktree; frontend commands run under `frontend/`. Test artifacts stay under ignored `backend-dotnet/Zayra.Api.Tests/TestResults/`.

- `npx tsc --noEmit`: passed.
- `npm run build`: passed; 69 static pages generated.
- `npx playwright test --config=playwright.unit.config.ts`: 25 passed.
- `npx playwright test --config=e2e/playwright.loans.config.ts`: 18 passed.
- `JAWAZAT_EVIDENCE_DIR=/tmp/kynex-isolated-qa.3y5ZZA npx playwright test --config=e2e/playwright.jawazat.config.ts`: 12 passed.
- `./scripts/schema-gates.sh`: all gates and 10 deliberate-defect self-tests passed. Existing retention-classification warnings remain outside this feature.
- `dotnet ef migrations has-pending-model-changes --project backend-dotnet/Zayra.Api --context ZayraDbContext --no-build`: no pending model changes on the rebuilt candidate.
- First all-category backend run: 4,161 passed / 3 failed (`isolated-merge-full.trx`). The changes-read regression detected the still-running build's pre-fix controller; the batch guard ratchet assumed payroll ownership for loan batches; K6's legacy payroll fixture lacked a lender company. Corrections preserve the permission/company guards and frozen monetary expectations.
- Fresh final `dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore --logger 'trx;LogFileName=isolated-merge-final.trx' -v quiet`: **4,165 passed, 0 failed, 0 skipped**, all categories including PostgreSQL integration and migration rehearsal. Existing compiler/analyzer warnings remain; no build error.

All pre-merge gates passed. Independent review approves the bounded local merge with no remaining critical/important findings. Production prerequisites below are not waived by this decision.

## Local merge outcome

Feature commit `72dd53d2` was fast-forwarded into local `main`. The merged tree was verified identical to the tested feature tree. The original mixed workspace remains on `fix/accept-postgres-uri-connection-string`; its uncommitted work was not swept into main. The isolated main worktree is `.claude/worktrees/loan-jawazat-merge-20261004`.

Post-merge command:

```sh
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Loan|FullyQualifiedName~Jawazat|FullyQualifiedName~PaymentBatchScopeTests|FullyQualifiedName~PayComponentGoldenMasterTests|FullyQualifiedName~FinanceDecisionConcurrencyPostgresTests|FullyQualifiedName~CompanyScopeBootAssertionTests|FullyQualifiedName~TableBudgetRatchetTests' --logger 'trx;LogFileName=merged-main-verification.trx' -v quiet
```

Result: **253 passed, 0 failed, 0 skipped**, including the corrected permission/company regressions, golden-master payroll and disposable PostgreSQL upgrade rehearsal. Post-merge `npx tsc --noEmit` and all 25 frontend unit checks also passed. This subsequent record-only update changes no executable code.

Browser plugin not available; regular Playwright used. Browser checks exercise rendered Next.js at `http://localhost:5183`, Desktop Chrome 1440×900 and Pixel 7, with intercepted synthetic APIs. They verify meaningful page content, interaction states, console/page errors, responsive width, policy-only updates, retry identity and provider-unavailable state. Worker desktop and HR phone screenshots were visually inspected under `/tmp/kynex-isolated-qa.3y5ZZA/`. These checks do not establish live backend/government connectivity or certify other browsers.

## Release prerequisites and recovery

Subsequent client-scope decision (2026-10-04): unavailable authorised government access is an accepted deferral to client integration, not a blocker for the internal-workflow delivery. The [client-package checklist](../CLIENT_INTEGRATION_PREREQUISITES.md) records required inputs and engineering acceptance. This does not waive live-transaction, customer migration or other deployment gates.

No push, deployment, live government submission or customer database change is part of this local merge. Before rollout, take a tested backup, rehearse against approved customer-shaped staging data, inventory/reconcile legacy payroll-linked/manual-collection loans, configure company policy and HR routes, and verify actual role grants and company scopes. Do not infer historical classifications, forgive debt or fabricate journal evidence.

Live government integration remains **not implemented/enabled**: the provider is disabled. Authorised provider contract, operation/API specifications, sandbox credentials, idempotency/reconciliation evidence and production onboarding are still required. Internal HR approval never issues a visa. This slice covers exit/re-entry policy workflows and notifications, not final exit, renewal, extension or cancellation. See `jawazat-operations.md` and `standalone-loans-operations.md`.

Financial/Jawazat evidence intentionally blocks destructive downgrade. Recovery is a reviewed forward fix or a tested backup restore, not deletion of financial history.
