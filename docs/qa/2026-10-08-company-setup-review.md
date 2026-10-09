# Company setup implementation review

Date: October 8, 2026. Revalidated October 9, 2026. Candidate branch: `feat/company-setup-experience`, rebased onto `main` at `3e39abd3`. This candidate has not been deployed or certified against production.

## Delivered scope

- Three explicit paths: Guided setup, Import organization, Manage settings. Settings are grouped by organization, people/pay, and system administration; existing deep links and read-only restrictions remain supported.
- Four guided steps: company details, working week, people/pay, review/create. Values survive step and setup-path changes while this page remains mounted. This is not persistence across reloads or sign-out.
- An explicit legal entity is required. Existing entity names are suggested, and selecting one supplies its country/currency. The review explains the difference between company-specific records and shared workspace configuration. Existing server creation, entity-scope and application-permission gates remain authoritative.
- Profile and section changes invalidate the reviewed draft. Draft records remain editable/removable; a refused application retains the exact draft.
- Package-first import with individual CSV files and formats available on demand. A replacement package replaces previous contents; individual CSV uploads update individual sections. Validation, reconciliation, blocking findings, warnings and audit details remain available.
- Import operations are serialized, with synchronous UI locks and revision checks. File changes and revalidation invalidate prior batches. Failed/blocked/conflicting applications preserve actionable recovery, including results with missing row arrays.

## Findings resolved during specialist review

| Finding | Disposition |
|---|---|
| Many profile changes left an older draft available to apply | Every draft-affecting input invalidates the preview. |
| Blank company name implicitly selected an active entity | Legal entity name is required and displayed at review. |
| A late validation response could restore a stale import batch | Shared operation lock, disabled editing, payload revision checks and invalidation. |
| New packages retained sections omitted from the new upload | Whole-package replacement; section-level upload remains incremental. |
| Failed import results could have null rows and crash rendering | Guarded result parsing and batch-level findings. |
| Payroll-model options and Finance approval implied unsupported configuration | Unsupported payroll choices removed; salary grades are stated explicitly. Finance approval option removed. |
| Non-calendar leave-year options were recorded but not used by leave balances | Calendar-year behavior stated explicitly. |
| Governance/term preferences implied operational enforcement | Planning preferences are clearly qualified; actual employee terms, approval workflows, payroll periods and access controls remain separate. |

## Product boundaries and next work

The setup assistant produces a starter draft. It does not provide a persistent implementation project, task ownership, complete relationship visualization, or go-live approval. Generated/applied does not mean ready for payroll.

Some existing backend values are stored preferences rather than operational controls: governance flags and notice/term defaults are not a substitute for configured workflows or employee records. Pay-cycle preference does not schedule payroll runs. Extending these into operational behavior needs a separate domain specification and end-to-end acceptance tests. The current change makes these boundaries visible rather than inventing behavior.

The [six-vendor benchmark](../research/2026-10-08-company-setup-benchmark.md) records the evidence and prioritizes durable ownership/resume, relationship review, operational readiness, and spreadsheet correction workflows.

## Verification scope

Browser tests run the actual frontend with synthetic users and intercepted API responses; they cover rendering and client behavior, not a live authorization deployment. The backend suites run separately against their disposable PostgreSQL fixture and existing controller/service contracts. No customer records are used.

Commands:

```sh
cd frontend
npm ci --no-audit --no-fund
npm run build
npm run start -- --hostname 127.0.0.1 --port 5185
npx playwright test -c e2e/playwright.setup-experience.config.ts
```

```sh
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj \
  --filter 'FullyQualifiedName~SetupAssistantTemplateTests|FullyQualifiedName~SetupAssistantGovernanceTests|FullyQualifiedName~MigrationImportBatchTests|FullyQualifiedName~OrganizationStructureImportScopeTests'
```

## Final execution results

- Production frontend build: **passed**, including type validity checks; exact committed lockfile restored, TypeScript 6.0.3.
- Backend: **92 passed, 0 failed, 0 skipped** across the four named suites, using disposable PostgreSQL.
- Browser: **22 passed**, 11 desktop (1440 × 1000) and 11 mobile (390 × 844), against the production build. No retries. Includes explicit target, payload fidelity, edit/remove, application refusal, draft invalidation, read-only restrictions, import concurrency/replacement, 409/422 recovery, and an Arabic RTL smoke journey through the setup shell and organization import path.
- Focused accessibility scans: **no critical or serious axe violations** in the initial guided/import regions at either viewport. This is scoped automation, not complete accessibility certification.
- Independent source review: the two material policy-truth findings were verified resolved. Independent visual review: **ship within the five captured states**. Mobile lower controls and People & pay were exercised by browser interaction; they are outside the visual crops.
- `git diff --check`: passed.

The first expanded browser run exposed an ambiguous test locator matching both the form alert and Next’s route announcer. Scoping the locator to Guided setup fixed the test; the entire suite then passed against unchanged product code. The GitHub i18n ratchet later identified new English-only setup copy; the setup shell and import path now use the existing English/Arabic dictionaries and tenant-aware date and number formatting.

Screenshots use a synthetic Admin and synthetic organization data:

- [Company details — desktop](assets/company-setup-2026-10-08/company-details-desktop.png)
- [Company details — mobile, scrolled form](assets/company-setup-2026-10-08/company-details-mobile.png)
- [Import entry — desktop](assets/company-setup-2026-10-08/import-entry-desktop.png)
- [Import entry — mobile](assets/company-setup-2026-10-08/import-entry-mobile.png)
- [Draft retained after refused application](assets/company-setup-2026-10-08/review-retained-after-refusal-desktop.png)

No production deployment, live tenant mutation, full-product Arabic localization assessment, scale benchmark, or go-live rehearsal was performed. Those are distinct release/implementation activities.
