# Loan audit remediation

**Owner:** CTO/integration owner. Implementers: Finance/Audit and HR Finance/HRM SME agents; independent consultant review follows implementation.

**Architecture:** Preserve the four justified loan tables. Journal links extend existing payment rows; no new tables. Financial corrections retain their bounded, transaction-locked maker/checker workflow (not a second generic configurable approval engine). Customer data must not be reclassified automatically. Preserve unrelated workspace changes; no merge/deployment in this task.

- [x] Borrower approval, lender-company payroll isolation and frozen frequency regressions in `LoanPremergeControlRegressionTests.cs`; fix `LoansController.cs`, `LoanEligibilityService.cs` and the four payroll collectors. Defect RED/GREEN observed.
- [x] Mapping changes, exact reversal and beneficiary separation in `LoanJournalEvidenceTests.cs`; implement `LoansController.Ledger.cs` and integrate payment/correction partials. Tests passed; no claim of an observed failing ledger test before implementation.
- [x] Add nullable journal evidence links to `Models/LoansAdvancesBonuses.cs`; tenant-bound relational mapping in `Data/ZayraDbContext.cs`; generate one migration with preserved-history downgrade guards. Never infer ambiguous legacy journal links.
- [x] Guard original standalone/lifecycle migration downgrades whenever payment/history rows exist; regression-test migration operations and synthetic PostgreSQL downgrade refusal.
- [x] Add independently approved, explicitly selected legacy repayment-method conversion using existing change-request payload; never automatically classify historical manual collections or forgive debt.
- [x] Verify targeted backend/security tests, PostgreSQL constraints/concurrency and EF model consistency. Independent spec review, then quality review. See `docs/superpowers/2026-10-04-loan-jawazat-verification.md`; known unrelated failures and staging gates mean **no merge**.

Commands: `dotnet test backend-dotnet/Zayra.Api.Tests --filter FullyQualifiedName~Loan --no-restore`; `dotnet ef migrations has-pending-model-changes --project backend-dotnet/Zayra.Api`. Serialize backend builds across agents.
