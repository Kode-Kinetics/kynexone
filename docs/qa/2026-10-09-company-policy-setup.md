# Company policy setup — implementation and acceptance record

This extends the setup experience in PR #220. Review is coordinated across HR policy/GCC research, finance, internal audit, backend integration, leave calculations and frontend UX. Agent reviews are implementation reviews, not external professional certification.

## Implemented scope

- Five guided steps, with a dedicated fourth step for grades and benefits.
- Prominent policy entry above company fields, with an English/Arabic three-stage tutorial: add text, choose AI assistance, then review/apply guidance. Compact mobile navigation keeps the primary policy action visible on entry.
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

## Policy guide follow-up

The file is read locally at selection. Generate sends the text with configuration to the setup service; AI sharing requires separate explicit opt-in. This is reference text for suggestions, not automatic extraction into every operational field. The guide states these limits, the .txt/12,000-character limit, manual configuration needs, template fallback and review-before-Apply. Closing/reopening preserves the text and choice. Invalid files preserve prior text; pending reads cannot overwrite newer edits.

Guide validation: production build and all 34 desktop/mobile browser cases passed, including English/Arabic, first-viewport policy visibility, keyboard focus, file validation, preserved text/consent, and no writes before Generate. The i18n gate passed 8 checks with only the intentional baseline-repin test skipped. Browser APIs remain intercepted; this is UI verification, not deployed full-stack acceptance.

The previous remote backend gate failed only the raw-SQL inventory (6,260/6,261 tests passed). The new parameterized, tenant-keyed transaction advisory lock is now documented in that inventory, with a real PostgreSQL simultaneous cross-tenant isolation regression. Local focused ratchet/apply tests passed 17/17; the broader setup/leave/security selection passed 160/160, no skips. These counts overlap. The remote full gate requires a fresh run after push.

The premium static audit scanned the whole repository and reported existing ownership/style findings beyond this task. Its new policy-textarea finding was corrected; native setup control ownership remains explicitly documented in DESIGN.md. This is not a claim that the repository-wide static audit passes.

## Desktop density follow-up

The setup heading and path navigation share a row on wide screens. Company details use four columns; the step rail shows only the active description, and introductory copy remains available in the expanded guide or smaller layouts. Footer spacing protects the primary action from the Kody launcher.

Verified against the production build: 35 setup browser cases passed; the desktop-only viewport case is intentionally skipped for mobile. All eight company fields, the policy action and Continue fit simultaneously at 1728×900, 1440×900 and 1366×768 with the MFA reminder and Kody enabled, and Continue passes the browser clickability check. English/Arabic mobile regression coverage remains passing. The i18n gate passed 8 checks with its intentional repin skip. No runtime policy/API behavior changed. Expanded tutorials, smaller viewports and zoomed layouts may scroll normally; no fixed-height clipping was introduced.

## Module-wide density and focused policy guide

The policy guide replaces the company fields while open, using side-by-side editor/file guidance, side-by-side AI review, and a three-column final explanation on desktop. Close/finish restores the company fields and values. All three guide screens keep Close and Next/Finish visible and clickable at 1728×900, 1440×900 and 1366×768, including an 11,900-character source with the MFA prompt and Kody enabled. A regression covers completing setup while the guide is open and then starting another setup.

All five default guided steps were checked at 1728×900 and 1440×900. Attendance/overtime, people/pay, salary grades/benefits, and review choices now use desktop width; nested attendance and grade fields remain at two columns for readability. Import spacing and settings navigation are compacted. Browser sweeps cover organization import and all 17 settings entry pages on desktop/mobile with explicit API fixtures. This establishes presentation/entry-state behavior, not populated production-data acceptance or all CRUD-dialog states. Repeated custom records, expanded optional policies, generated drafts, and tables may still scroll naturally. No fields or records are clipped to force a fit.

Final validation: production build passed; 41 browser cases passed with three desktop-only fit cases intentionally skipped on mobile. Translation gate: 8 passed, one intentional repin skip. Independent source review findings on reset state and nested-field width were corrected before this run.
