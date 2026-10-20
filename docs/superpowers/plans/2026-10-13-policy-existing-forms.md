# Policy to Existing Forms Implementation Plan

**Superseded scope notice (2026-10-13):** The owner explicitly narrowed implementation to populating existing forms and reusing unchanged Save APIs. Task 3 and its server concurrency/audit/envelope extensions are deferred. Current implementation uses optional `onboardingTaskDrafts` in extraction, a browser-local draft handoff, and existing template Save/readback. Do not execute the broader persistence changes below without new authorization. No database/schema changes.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Populate the existing onboarding checklist/template-task controls from Policy 3, then verify explicit, scope-safe saves through the existing API.

**Architecture:** Extend existing extraction with typed, evidence-bound form drafts. Carry drafts into the existing OnboardingTab controls without writing records; extend the existing template-task save boundary for explicit identity, stale-update protection and atomic audit evidence. Retain the existing database tables and manual employee-task generation flow.

**Tech Stack:** Existing ASP.NET Core/.NET 8, EF Core/PostgreSQL, Next.js/React/TypeScript, xUnit and Playwright.

**Spec:** `docs/POLICY_TO_EXISTING_FORMS_DESIGN.md`

## Global Constraints

- Local Evostel shadow only. No deployment, production writes, new tables, replacement forms, or automatic employee task generation.
- Templates are tenant-wide. This first policy-assisted save path requires a verified single-company tenant and discloses tenant-wide scope; multi-company company-only drafts cannot be saved through it.
- Unknown deadlines remain blank and block new-task saves until their integer offset and start-date anchor are confirmed. Existing values survive policy silence.
- Conditional tasks do not become enforced eligibility rules. They remain draft-only until the user explicitly confirms applicability to the selected checklist.
- Scope is onboarding/orientation task content, not automatic probation calculations, appraisal outcomes, or proof of identical source-named document templates.

## Review Focus

1. Mixed source/recommendation cells and overlapping chunks: retain source meaning, deduplicate evidence, never silently promote proposed rules (Task 1).
2. Reload, company switch, source replacement and dirty existing inputs: preserve edits but refuse incompatible drafts (Task 2).
3. Missing deadlines and conditional duties: no zero-day default or unconditional assignment from absent information (Tasks 2–3).
4. Duplicate titles, renamed tasks, concurrent manual edits and lost save responses: no silent overwrite, duplicate creation or false success (Task 3).
5. Read-only users, stale/inaccessible evidence and audit failure: no unauthorized or unaudited policy-assisted write (Tasks 3–4).

### Task 1: Typed source-backed draft extraction

**Files:** Modify `backend-dotnet/Zayra.Api/Application/Setup/PolicyExtractionContracts.cs`, `Infrastructure/Setup/PolicyExtractionService.cs`, `Infrastructure/Setup/PolicyExtractionReview.cs`; create `Infrastructure/Setup/PolicyFormDraftExtractor.cs`; extend `backend-dotnet/Zayra.Api.Tests/PolicyExtractionTests.cs`.

**Interfaces:** Add optional `List<PolicyFormDraft>? FormDrafts = null` to `PolicyExtractionResult`. Define `PolicyFormDraft(Id, FormKey, ClauseId, SourceQuote, SourceStart, SourceLength, Fields, Conditions, MissingFields)` with FormKey fixed to `onboarding.templateTask`; Fields is a dictionary of `PolicyFormField(JsonElement Value, string Origin, string SourceQuote, int SourceStart, int SourceLength)`. Origin is `Policy` or `Suggestion`. Allow only taskTitle/taskDescription/category/assignedToName/dueOffsetDays/orderIndex/isMandatory. Unknown values are absent, never zero/default-filled. Server validation uses the current document text and classification; the document/version are inherited from the enclosing result.

- [ ] Write regression assertions: actual 3.1-R3 duties yield individual grounded title/description drafts; adjacent REC deadline is absent; orientation content retains conditions; fabricated quotes/fields are rejected; proposed sections cannot create Policy-origin fields.
- [ ] Run the new tests and observe their intended failure before changing extraction.
- [ ] Extend the existing AI prompt/output validation for this closed destination. Keep existing Setup proposals intact. Derive clause identity from verified source anchors; derive subtask identity from source spans, never generated summary text. Merge overlapping-chunk drafts by identity and retain unmatched old decisions as history, never approval of a new draft.
- [ ] Run `dotnet test backend-dotnet/Zayra.Api.Tests --filter 'FullyQualifiedName~PolicyExtractionTests|FullyQualifiedName~PolicyDocumentGovernanceTests'`; require zero failures.
- [ ] Integration owner reviews and records the exact contract before Task 2 consumes it.

### Task 2: Prefill the existing controls and retire the questionnaire as the primary path

**Files:** Modify `frontend/src/api/setupAssistant.ts`, `src/components/SetupPolicyExtraction.tsx`, `src/components/AiSetupAssistant.tsx`, `src/views/RecruitmentPage.tsx`, `src/api/recruitment.ts`, `src/i18n/policyBinder.ts`; create `src/lib/policyFormDraft.ts`, `unit/policyFormDraft.spec.ts`, `e2e/policy-existing-forms.spec.ts`.

**Interfaces:** Mirror Task 1 DTOs in TypeScript. `PolicyFormDraftBundle` contains version=1, tenantId, userId, companyId, source document IDs/hashes, drafts and per-draft edits/save outcomes. `savePolicyFormDraft(bundle)` and `loadPolicyFormDraft(tenantId,userId,companyId)` use one scoped browser-storage namespace and surface storage failure. `mergePolicyTaskFields(current, draft)` returns only supported draft patches plus unresolved fields, preserving current values for absent fields. Existing route `/recruitment/onboarding` consumes this bundle and selects Checklist Templates; it must not write on mount.

- [ ] Write failing unit/browser cases asserting existing Task title/Description inputs contain extracted values, missing deadline is blank, existing offset is retained on policy silence, and there are no POSTs on open/switch/reload. Assert dirty destination input requires an explicit replace/keep choice.
- [ ] Observe intended failures, then implement scoped handoff and draft-task selector around the existing templateForm controls—not another editor. Preserve edits when selecting another draft or returning to Setup.
- [ ] Replace the primary review questionnaire with prepared destination/action summary, specific unresolved fields and compact capability gaps. Keep audit history in secondary details; do not require comments/owners for every clause.
- [ ] Resolve company context by selected record ID before extraction/handoff; expose loading/error/ambiguous states once. Show tenant-wide template scope and draft-only behavior where saving is unsafe.
- [ ] Gate save controls by effective write permission. Represent policy-derived dueOffsetDays as blank until confirmed; retain existing manual workflow behavior outside the policy-draft path. Require explicit conditional applicability and distinguish role text from actual user assignment.
- [ ] Run `npm run test:unit` and the new browser suite; require zero failures and verify English/Arabic string coverage.

### Task 3: Safe existing-endpoint persistence and audit

**Files:** Modify `backend-dotnet/Zayra.Api/Controllers/Recruitment/OnboardingController.cs`; create `Application/Setup/PolicyFormSaveContracts.cs`, `Infrastructure/Setup/PolicyFormSaveValidator.cs`, `backend-dotnet/Zayra.Api.Tests/PolicyFormSaveTests.cs`, `PolicyFormSavePostgresTests.cs`; extend `frontend/src/api/recruitment.ts` and the existing save handler in `RecruitmentPage.tsx`.

**Interfaces:** Add optional `PolicyFormSaveContext? PolicyDraft = null` to `UpsertOnboardingTemplateTaskRequest`. Context carries companyId, draftId, action (`create`/`update`), taskId for update, expectedVersion (UpdatedAtUtc ?? CreatedAtUtc), source document references and Task 1 field evidence, explicitly manually-entered field names, deadline-anchor confirmation and conditional-applicability confirmation. Return the existing task entity, including timestamps, so the client can reread by ID. Existing non-policy callers retain their contract.

- [ ] Write failing tests: forged/inaccessible/stale evidence rejected; multi-company save rejected; missing deadline confirmation rejected; no role-to-user guess; changing a title updates the selected ID; existing-title create returns conflict; stale version returns conflict; audit failure rolls back the task.
- [ ] Observe failures, then validate effective authorization, verified single-company scope, source hash/ranges/classification and allowed fields server-side. Source-derived and manual values must be distinguishable in audit metadata.
- [ ] Make create/update identity explicit. Use transaction-bound concurrency control covering the existing manual upsert too; reread the target after acquiring the lock, reject stale versions and duplicate creates, and keep retries idempotent using draftId plus payload identity in existing audit evidence. Do not add a table. Any new advisory-lock call requires a narrowly justified security-ratchet entry and PostgreSQL proof.
- [ ] Commit task mutation plus existing central AuditLog event in one transaction. Read back and compare returned values before the UI marks a draft saved. Retain unsaved drafts after errors or lost responses; never automatically generate employee tasks.
- [ ] Run new unit and disposable-PostgreSQL tests plus existing onboarding, tenant-isolation, permission, raw-SQL and audit-chain guards; require zero failures.

### Task 4: Integrated evidence and local handoff

**Files:** Extend `frontend/e2e/policy-existing-forms.spec.ts`; update `docs/POLICY_BINDER_FIX_2026-10-13.md`. No unrelated rewrites or migrations.

- [ ] Run project-wide backend and frontend test commands; report every failing test by name. Run frontend build, TypeScript/i18n checks and `git diff --check`.
- [ ] Restart only the isolated local API/frontend with their existing shadow database/provider configuration. Verify readiness and the served build before browser testing.
- [ ] Use Chrome and the actual uploaded Policy 3, not a fixture response: extract, open the existing Checklist Templates page, inspect populated controls, change a draft, reload and verify retention. Do not tell the user to retest before this proof exists.
- [ ] In a clearly labelled shadow-only verification checklist, supply any necessary test-only manual inputs explicitly, save through the existing form and read back identical IDs/values. Repeat the save/reload to verify no duplicates; verify no employee tasks or employment decisions were created. Test records are not approved customer policy.
- [ ] Independently review requirement coverage (7R/4CD/3 source forms as inventory), actual delivered task content, remaining capability gaps and scope boundaries. Capture screenshots of populated existing fields and saved-record readback.
- [ ] Commit only reviewed task-owned files after inspecting the dirty worktree; preserve all unrelated changes. Report local implementation versus production status separately. No merge/deployment.

## Plan self-review

The four tasks cover extraction, existing-control handoff, identity/scope/audit-safe persistence and real acceptance. Review Focus items each have explicit tests above. Task 2 consumes Task 1 DTOs; Task 3 uses the same evidence and extends the existing save request. Employee-specific workflows and unsupported probation automation are excluded rather than simulated. Execution method remains the user's requested specialist-agent implementation with one integration owner and independent review.
