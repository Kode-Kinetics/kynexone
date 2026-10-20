# Offer lifecycle SME readiness review

Date: 2026-10-19. Worktree: `policy-binder-fix`; branch: `fix/policy-binder-draft`; inspected HEAD: `311f0b74`.

This records specialist-agent discovery and implementation planning, **not completion of the lifecycle feature**. Existing designer code and unrelated dirty work remain unchanged by this review.

## Team and findings

| Agent lane | Evidence-backed result | Plan treatment |
| --- | --- | --- |
| Backend / HR architecture (`offer_backend_sme`) | Existing acceptance CAS must also serialize against a replacement email in flight; approval attribution and tenant-explicit worker queries must be retained. | Application reservation, actor-preserving shared lifecycle service and explicit scoped worker predicates. |
| UX / HR customer (`offer_ux_sme`) | Both Send buttons only mark status; sender belongs in existing Email / SMTP settings; effective sender can fall back to platform configuration. | One shared send review, safe resolved-sender DTO/settings link, separate business and communication outcomes. |
| Independent QA / security (`offer_qa_sme`) | Current tests do not prove immutable original PDFs, migration upgrades, offer dispatch idempotency or lossless onboarding compensation. | Named PostgreSQL/HTTP/browser/migration suites and full compensation handoff gate. |
| Independent plan review (`offer_plan_reviewer`) | Eight corrections required: reconciliation, response races, unrepresentable basic salary, admin policy UI, external recipient/time rules, legacy approved letters, catalogue resolution and decimal capacity. | Added explicit contracts/state rules, legacy transition matrix, policy persistence/UI, dated catalogue precedence and numeric(19,3). |

Source anchors verified during discovery include:

- `Controllers/Recruitment/OffersController.cs`: create, legacy Send, download, approval submission.
- `Controllers/Recruitment/ApplicationsController.cs`: regenerate/delete draft, legacy Send/reset deadline, response/application rejection paths.
- `Infrastructure/Recruitment/RecruitmentService.cs`: exactly-once acceptance; only basic salary copied to employee draft today.
- `Infrastructure/Email/SmtpEmailService.cs`: successful submission followed by awaited disconnect; tenant/platform effective sender resolution.
- `frontend/src/views/SetupPage.tsx`: **From Address** and **From Name** under `/setup?tab=emailConfig`.
- `Models/SetupAdmin.cs`: `SystemSetting` is tenant-scoped, not company-scoped.
- `Controllers/EmployeesController.cs`: `/api/employees/drafts/{draftId}` and approval/activation route.

Backend anchors are relative to `backend-dotnet/Zayra.Api/`.

## Fresh baseline evidence

Command from worktree root:

```sh
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~OfferLetterDesignTests|FullyQualifiedName~OfferTemplatePersistenceTests|FullyQualifiedName~RecruitmentHireJourneyTests|FullyQualifiedName~RecruitmentOfferMakerCheckerTests|FullyQualifiedName~RecruitmentCompanyScopeTests|FullyQualifiedName~OfferAcceptanceExactlyOnceTests|FullyQualifiedName~OfferPlacementTests' --logger 'trx;LogFileName=offer-lifecycle-baseline.trx' --results-directory /tmp/kynex-offer-lifecycle-tests
```

Result: exit 0; **64 passed, 0 failed, 0 skipped**. TRX counters independently inspected in `/tmp/kynex-offer-lifecycle-tests/offer-lifecycle-baseline.trx`. Compilation emitted existing nullable/hiding/analyzer warnings; this is not a warning-free/full-product certification.

Read-only HTTP check: `http://127.0.0.1:5198/recruitment/offer-templates` returned **200**. This establishes route availability, not authenticated lifecycle behavior or email delivery. Existing screenshots/previous designer evidence remain in `docs/OFFER_LETTER_DESIGNER_VERIFICATION.md`.

The independent reviewer rechecked the eight findings after correction and marked all eight **Addressed at implementation-plan level**; this does not approve code not yet written. The reviewer also corrected its earlier unverified numeric(18,2) premise to the inspected numeric(12,2) mapping; numeric(19,3) preserves either integer range.

No SMTP settings were saved, no test email was sent, no production schema was changed, and no live customer records were edited.

## Handoff

Implementation plan: `docs/superpowers/plans/2026-10-19-offer-lifecycle.md`. Specialist-agent execution is the user's chosen method. The writing-plans review checkpoint requires review of the implementation plan before product-code changes. Do not report planned history, dispatch, original-artifact storage or handoff as implemented merely because the existing designer baseline is green.
