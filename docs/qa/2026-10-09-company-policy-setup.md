# Company policy setup — implementation and acceptance record

This extends the setup experience in PR #220. Review is coordinated across HR policy/GCC research, finance, internal audit, backend integration, leave calculations and frontend UX. Agent reviews are implementation reviews, not external professional certification.

## Implemented scope

- Five guided steps, with a dedicated fourth step for grades and benefits.
- Optional approved policy excerpts/plain-text intake; explicit opt-in before excerpts enter the governed AI request. No document text is an executable instruction.
- Multiple attendance capture preferences and paid/compensatory overtime choices; custom attendance rules and editable draft overtime limits.
- Multiple scoped leave policy rows (grade, department, employment type), separate accrual frequency and partial-month proration.
- Manual salary grades or assisted proposals; ranges/currency/reference validation. Generated amounts are not market benchmarks.
- Benefit plan catalog with effective dates and grade eligibility. Existing Benefits-by-grade restrictions remain enforced.
- Custom management/approval preferences are labeled as preferences; they do not activate operational routing.
- Preview capability version check prevents a new client from silently accepting a legacy API that discards configuration.
- Canonical module permissions, entity boundaries, transaction/retry controls, and reviewed-draft audit snapshot.

## Deliberate capability boundaries

These requirements remain open and must not be described as delivered:

- Anniversary/custom leave periods: Year-based balances, requests, ESS/mobile, encashment and settlement all need an immutable dated-period migration. The current runtime remains January–December.
- Custom pay calendars: existing salary storage assumes monthly amounts. Nonmonthly setup is refused to prevent accidental per-run amounts being treated as monthly salaries.
- Document ingestion: this increment accepts plain text/excerpts; it does not parse PDF/DOCX, retain approved source document versions, or map every policy automatically.
- Benefits: setup creates plans and supported eligibility; enrollment, employee deductions, employer contributions and grade-default auto-enrollment are separate flows.
- Attendance devices/geofences and operational approval workflows need their respective runtime configuration.
- Posted leave amounts are never silently recalculated. Late-recorded termination or policy changes require a governed adjustment. Monthly accrual posts the current UTC month through an explicit HR action; no background scheduler or missed-month catch-up was added.
- Ordinary private-sector GCC research does not certify every jurisdiction or worker category. Kuwait's current consolidated wording remains unverified.

## Verification

Frontend checks completed: 30 desktop/mobile browser cases, including English and Arabic RTL, accessible custom grades/benefits, scope payloads, invalid salary ranges, and legacy-API refusal; 199 browserless checks plus the deliberately skipped i18n repin; 228 unit tests; production build with type checking; and design-context lint (zero errors/warnings). The final production build also passed the two targeted desktop/mobile cases after adding manual-grade draft preservation and clearing monthly proration when switching to yearly allocation.

Backend validation: 143 tests passed, zero failed/skipped, on the final setup/leave source. The fresh-build filter covered SetupConfiguration, SetupAssistant, statutory special-leave setup, Release-A apply, LeaveAccrualInvariantTests, and the GOSI all-tenant-write-path safeguard. This includes PostgreSQL apply/retry/concurrency and the finance/audit regression cases. The leave specialist separately passed 49 focused tests including PostgreSQL migration upgrade/replay/guarded rollback/reapply and concurrent accrual; migration visibility was 94/94 and the model had no pending changes. Counts overlap and are not additive.

Commands:

```sh
# worktree root
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~SetupConfiguration|FullyQualifiedName~SetupAssistant|FullyQualifiedName~KsaStatutorySpecialLeaveTests.SetupApply|FullyQualifiedName~EntitlementMatrixFixRoundPostgresTests.AiSetupApply|FullyQualifiedName~LeaveAccrualInvariantTests|FullyQualifiedName~PilotPayrollCorrectnessPostgresTests.GosiStatutory_EveryTenantWritePath'
# frontend/
npm run build
npm run test:unit
npx --no-install playwright test -c e2e/playwright.browserless.config.ts
SETUP_EXPERIENCE_BASE_URL=http://127.0.0.1:5186 npx --no-install playwright test -c e2e/playwright.setup-experience.config.ts
```

The independent finance/audit source review closed four P1 findings: editable salary validation/replay, scope normalization, competing leave populations, and monthly accrual ignoring specific yearly policies. It also closed ambiguous/mismatched branch assignment. Runtime results must accompany this source review.

Frontend browser tests intercept API responses and demonstrate UI behavior; PostgreSQL tests separately verify persisted service/controller behavior. Neither is a deployed full-stack acceptance claim.

## Release and rollback

Production has not been changed. Release requires the API contract and leave migration before the matching frontend. `AddLeavePartialMonthProration` backfills false for legacy policies; new setup monthly policies opt in explicitly. Its down migration refuses to discard configured true values. Preserve posted ledgers; do not reset the flag or balances merely to force a downgrade.

## Research

- [HRM competitor patterns and GCC policy sources](../research/2026-10-09-flexible-hr-policies-and-gcc-leave.md)
- [Finance acceptance gates](../research/2026-10-09-finance-policy-acceptance-gates.md)
