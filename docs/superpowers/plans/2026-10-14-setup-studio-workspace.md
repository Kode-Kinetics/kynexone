# Setup Studio Workspace Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the five-step-only Setup Studio entry with a permission-aware twelve-area workspace that reuses canonical settings forms and preserves existing document/draft work.

**Architecture:** Add a typed navigation catalogue and a focused workspace shell within the existing Setup page. Reuse canonical form components and their save APIs, not copies or a generic new form engine. Keep source preparation and policy publication separate from operational saving.

**Tech Stack:** Existing Next.js 15 / React 19 / TypeScript, Tailwind, application contexts, Playwright unit/browser tests and existing ASP.NET Core APIs. No dependency upgrades or schema changes in this work package.

**Spec:** `docs/superpowers/specs/2026-10-14-hr-workspace-design.md`

**Status:** Package 1 locally verified and independently code-reviewed; owner acceptance and safe release integration remain separate. User approved the design and this bounded workspace work; automated acceptance, independent review and authenticated local evidence are tracked in `docs/SETUP_STUDIO_WORKSPACE_VERIFICATION.md`. Packages 2 and 3 remain required and open; this package does not complete the entire programme.

## Global Constraints

- Single window means consistent company context, connected navigation and recoverable work, not every module or form expanded on one page.
- Existing operational screens remain the destinations for daily transactions. Setup Studio does not become a second HR application.
- Twelve areas are non-linear destinations, not mandatory setup steps.
- Saving settings is not publication; publication is not proof of runtime enforcement.
- Preserve current application identity, components and operational routes.
- Production changes require separate explicit approval and a documented deployment/recovery plan.
- Preserve all pre-existing dirty/untracked work in `fix/policy-binder-draft`; do not reset, stash or bulk-stage it.
- Existing extraction remains bounded. Do not label unmapped domains as automatically fillable or enable tenant feature flags as a side effect.

## Programme boundaries

The approved design contains three independently reviewable work packages:

1. **This plan: workspace and shared-form access.** Twelve-area discovery, canonical forms, preserved source/draft access, truthful permissions and capabilities, responsive navigation, and continued editing after setup.
2. **Document-to-form adapters.** Per-domain server enforcement, record identity, sparse updates, source/target version checks and shared manual/assisted save paths. The earlier leave design supplies bounded requirements but needs reconciliation before execution. This package must provide its own exact interface and test plan; generic wizard Apply must not substitute for update APIs.
3. **Policy-library convergence and Kody.** Manual configuration to policy draft, approval/version/audience lifecycle, group/company applicability and source-backed employee guidance. Specific schema/API decisions require their own reviewed plan; existing upload/publication does not already deliver these capabilities.

All three are required for the complete concept. Package 1 is not a completion certificate for packages 2 or 3. Publication prerequisites must precede employee release; the existing scoped library remains available without advertising unimplemented approval or inheritance.

## Review Focus

1. A resumed legacy draft contains uploads and edits: visiting the new workspace or browser Back must not delete, publish or re-extract it (Task 4).
2. A tenant administrator has limited company access: a forged area/company URL cannot mount an unauthorized editor or broaden record scope (Tasks 1, 3, 5).
3. A feature is off or feature discovery fails: show an honest unavailable/unknown state rather than a broken form or false configuration status (Tasks 1, 2).
4. An administrator edits a populated record after setup: the canonical update path must retain record identity and omitted values, not create a duplicate through wizard Apply (Tasks 3, 5).
5. A mobile/RTL user has a long company name and validation error: navigation, focused inputs and save actions remain reachable without horizontal page overflow (Task 5).

## Current anchors and ownership

- `frontend/src/views/SetupPage.tsx`: existing settings tabs, company loads, authorization gates and library entry. Main integration owner alone edits its composition.
- `frontend/src/components/AiSetupAssistant.tsx`: legacy company starter, source preparation and saved drafts. Preserve behind an explicit secondary entry, not the new default workspace.
- `frontend/src/routes/navigation.ts`: real module routes and permission/feature metadata; do not duplicate a conflicting authorization catalogue.
- `frontend/src/contexts/CompanyContext.tsx`: selected company is a narrowing view preference, not a new grant.
- `frontend/src/contexts/FeatureFlagContext.tsx`: feature gates; loading/error must not be treated as enabled.
- `frontend/src/views/LeavePage.tsx`: `LeaveTypesTab`, `PoliciesTab`, `HolidayCalendarTab`, and `PolicyModal`; current tabs are local state, not deep-linked.
- `frontend/src/views/RecruitmentPage.tsx`: `OnboardingTab`; `/recruitment/onboarding` is an existing dedicated entry.
- `frontend/src/components/PolicyDocumentManager.tsx`: current library, source review, company publication/withdrawal; retain existing warnings and authorization.
- `frontend/src/lib/setupDraftLifecycle.ts`, `policyFormDrafts.ts`: preserve storage compatibility and existing stale-input checks.

### Task 1: Typed coverage catalogue and availability rules

**Files:** Create `frontend/src/lib/setupAreas.ts`, `frontend/unit/setupAreas.spec.ts`, `docs/SETUP_STUDIO_COVERAGE.md`. Modify `frontend/src/contexts/FeatureFlagContext.tsx` additively to expose its existing state discriminator.

**Interfaces:**
- `SetupAreaId = 'organization' | 'recruitment' | 'onboarding' | 'time' | 'leave' | 'compensation' | 'payroll' | 'performance' | 'learning' | 'services' | 'offboarding' | 'compliance'`.
- `SetupDestination = { id: string; areaId: SetupAreaId; label: string; path: string; kind: 'settings' | 'templates' | 'operations'; requiredPermissions: readonly string[]; requiredFeatureKey?: string; scope: 'company' | 'tenant' | 'mixed'; presentation: 'embedded' | 'route'; assistance: 'existing-onboarding' | 'not-connected' }`.
- `SetupAreaDefinition = { id: SetupAreaId; label: string; description: string; destinations: readonly SetupDestination[] }`.
- `SETUP_AREAS: readonly SetupAreaDefinition[]`.
- `resolveSetupArea(value: string | null): SetupAreaId | 'overview'` returns overview for absent/unknown values; never interprets arbitrary paths.
- `SetupAccessContext = { hasPermission: (permission: string) => boolean; isFeatureEnabled: (key: string) => boolean; verdictForPath: (path: string) => { allowed: boolean }; flagStatus: 'loading' | 'ready' | 'error'; isCompanyScopedGroup: boolean }`.
- `destinationAvailability(destination: SetupDestination, context: SetupAccessContext): 'available' | 'restricted' | 'unavailable' | 'checking' | 'unknown'`. Feature context exposes `status: 'loading' | 'ready' | 'error'` without changing existing methods. Error yields unknown rather than claiming a module is disabled. Existing permission and feature gates remain authoritative.

- [ ] **Step 1:** Inspect current module forms, route wrappers and save APIs; write the coverage table for all twelve areas and map all 36 comparison families to an area/shared concern. Record form component, actual scope, API, permission/flag, manual path, extraction state and evidence. Operational-only screens must be marked operations. Do not invent missing settings controls.
- [ ] **Step 2:** Write unit tests `catalogue_has_twelve_unique_areas`, `unknown_area_resolves_to_overview`, `restricted_destination_is_not_available`, `loading_or_disabled_feature_is_not_available`, `flag_error_is_unknown_not_disabled`, `operation_is_not_a_settings_form`. Assert exactly twelve unique IDs and that all paths are internal allowlisted paths.
- [ ] **Step 3:** Run `cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupAreas.spec.ts`; capture failure because catalogue/functions do not exist.
- [ ] **Step 4:** Implement the interfaces above. Reuse permission/flag definitions from existing route metadata and preserve additional editor-level authorization. Do not infer configured status from availability or role names.
- [ ] **Step 5:** Re-run tests and typecheck (`npx tsc --noEmit`); commit only this task's explicit files after review.

### Task 2: Responsive non-linear workspace shell

**Files:** Create `frontend/src/components/setup/SetupStudioWorkspace.tsx`, `SetupAreaNavigator.tsx`, `frontend/src/i18n/setupStudio.ts`, `frontend/e2e/setup-studio-workspace.spec.ts`, `frontend/e2e/playwright.setup-studio-workspace.config.ts`. Modify `frontend/src/i18n/translations.ts` only to register translations using the incumbent pattern.

**Interfaces:**
- `SetupAreaNavigator({ activeArea, onSelect }: { activeArea: SetupAreaId | 'overview'; onSelect: (area: SetupAreaId | 'overview') => void })`.
- `SetupStudioWorkspace({ companies, renderSettings, onOpenLibrary, onOpenLegacyDraft }: { companies: CompanyDto[]; renderSettings: (id: string) => React.ReactNode; onOpenLibrary: () => void; onOpenLegacyDraft: () => void })` consumes the catalogue and existing auth/company/feature contexts.
- Selection uses `/setup?tab=aiSetup&area=<allowlisted-id>` with existing authorized company context preserved; unrelated valid query parameters remain intact.

- [ ] **Step 1:** Add intercepted-API browser tests `all_areas_reachable_without_company_wizard`, `url_back_restores_area`, `feature_unavailable_does_not_mount_editor`, `overview_does_not_claim_configuration_complete`, `keyboard_and_mobile_area_navigation`. Assert no five-step primary wizard, all twelve named destinations, no mutation requests from navigation, and correct disabled/restricted states.
- [ ] **Step 2:** Create a dedicated Playwright config following `playwright.setup-lifecycle.config.ts`: desktop 1440x1000 and Pixel 7, one worker, no retries, local base URL through `SETUP_EXPERIENCE_BASE_URL`. Run the new tests against the unchanged frontend and capture the expected missing-workspace failure.
- [ ] **Step 3:** Build compact overview rows and a desktop vertical area navigator. Mobile uses a labelled disclosure/list with focus restoration. Render one active area; distinguish settings/templates from operational links. No twelve-card wall, giant horizontal stepper or fabricated completion percentages.
- [ ] **Step 4:** Add English/Arabic copy through the existing translation pipeline. Preserve current visual tokens. Company and draft/storage scope remain visible, including tenant-wide controls where applicable.
- [ ] **Step 5:** Run the new browser tests, typecheck and relevant translation unit tests; commit only explicit task files after review.

### Task 3: Reuse canonical settings panels and save paths

**Files:** Modify `frontend/src/views/SetupPage.tsx`, `frontend/src/views/LeavePage.tsx`, `frontend/src/views/RecruitmentPage.tsx`; extend workspace browser tests.

**Interfaces:**
- Inside SetupPage, `renderExistingSettings(id: SettingsTab): React.ReactNode` reuses existing tab components and already loaded company/grade/cost-center props. Use the same renderer for old direct settings URLs and the new workspace; no broad relocation of the 2,000-line settings implementation. Invalid IDs from the workspace catalogue are rejected through an explicit ID guard before this function is called.
- Export the existing canonical `LeaveTypesTab`, `PoliciesTab`, `HolidayCalendarTab`, and `OnboardingTab` for controlled reuse; do not fork their modal/save implementations. A wrapper must enforce both existing page-level and editor-level access before mounting.
- `renderSettings` supports only inventoried, authorized panel IDs. Remaining destinations open verified existing routes in the same browser tab with a safe return path; these are explicitly transitional, not claimed in-place form integrations.

- [ ] **Step 1:** Add tests `manual_leave_edit_uses_existing_put`, `existing_record_identity_survives_edit`, `unauthorized_editor_does_not_mount`, `tenant_wide_onboarding_not_labelled_company_only`, `existing_setup_deep_links_still_work`. Use actual API paths from the canonical client; assert no call to generic setup Apply for ordinary record updates.
- [ ] **Step 2:** Run tests and capture failure for unavailable embedded forms. Existing manually rendered defaults must not be used as evidence of saved values.
- [ ] **Step 3:** Extract only the composition boundaries required for reuse. Keep modal fields, validation, request shapes and save behavior unchanged. Company data reloads on actual context changes; decline embedding where the existing component's scope cannot safely match the workspace and retain its explicit scoped route instead.
- [ ] **Step 4:** Preserve source-page usage of the same exported forms. Do not broaden any permission to make embedding work; an authorization mismatch is a gap to resolve explicitly.
- [ ] **Step 5:** Run unit/browser regressions for original pages and new workspace, then review and commit the explicit changes. Existing operations and restricted controls must behave the same outside Setup Studio.

### Task 4: Preserve legacy draft, source and library lifecycle

**Files:** Modify `frontend/src/views/SetupPage.tsx`, `frontend/src/components/AiSetupAssistant.tsx`, `frontend/src/components/setup/SetupStudioWorkspace.tsx`; extend `frontend/e2e/setup-lifecycle.spec.ts` and new workspace tests. Modify `setupDraftLifecycle.ts` only if a minimal compatible navigation helper is required.

**Interfaces:**
- `mode=starter` selects the existing company starter deliberately; it is never the default for an established company.
- Library continues using `/setup?tab=policyLibrary`; workspace preserves its return area through an allowlisted `area` parameter.
- Existing policy source/draft storage schemas and keys remain unchanged. Do not automatically migrate, re-extract or discard them on workspace entry.

- [ ] **Step 1:** Write tests `legacy_draft_is_preserved_on_workspace_entry`, `return_from_library_preserves_area_and_company`, `cancel_return_keeps_dirty_edits`, `unsupported_area_does_not_claim_prefill`, `document_navigation_does_not_publish`. Seed the exact existing draft format from lifecycle fixtures.
- [ ] **Step 2:** Run and confirm failures against the old default layout.
- [ ] **Step 3:** Mount the new workspace as Setup Studio's primary view. Provide explicit resume/access to existing source preparation and legacy starter drafts. Preserve state across section changes without mounting unauthorized hidden components. Use the existing dirty-form confirmation where applicable; never destroy another user's scoped draft.
- [ ] **Step 4:** Show optional document assistance only with an honest destination/capability statement. Existing onboarding handoff remains available; other area-level extraction is labelled not connected until the adapter package delivers it. Do not route a Leave upload into global Apply and imply Leave-only processing.
- [ ] **Step 5:** Run existing binder, form-prefill, review and lifecycle fixture suites plus the new workspace suite. Review and commit explicit files only.

### Task 5: Tenant-admin maintenance, accessibility and local acceptance

**Files:** Extend `frontend/e2e/setup-studio-workspace.spec.ts`; create `docs/SETUP_STUDIO_WORKSPACE_VERIFICATION.md`; update `DESIGN.md` only to reflect the approved runtime navigation after implementation.

- [ ] **Step 1:** Write tests `tenant_admin_can_revisit_and_edit_saved_settings`, `read_only_user_cannot_edit`, `company_switch_does_not_reuse_other_company_draft`, `forged_area_or_return_path_is_ignored`, `failed_save_retains_values`, `rtl_mobile_long_labels_remain_accessible`. Use real permission sets from existing auth fixtures, not an invented SuperAdmin bypass.
- [ ] **Step 2:** Run new tests before fixes and capture any failures. Add axe checks for desktop/mobile in English and Arabic; assert no serious/critical findings and no horizontal page overflow at 390px, including with long labels. Verify keyboard focus and errors are not hidden by sticky controls.
- [ ] **Step 3:** Fix only workspace integration defects exposed by these tests. Module business-rule defects outside this package are recorded as blockers, not quietly rewritten or bypassed.
- [ ] **Step 4:** Run `cd frontend && npx tsc --noEmit`, `npm run test:unit`, `NEXT_PUBLIC_API_BASE_URL=http://127.0.0.1:5127 npm run build`, and the dedicated workspace plus existing setup/binder/prefill/review/lifecycle browser configurations. Record each result separately; fixture tests are not server-security certification.
- [ ] **Step 5:** Validate the authenticated local Evostel shadow UI and tenant-admin maintenance with dedicated test records only. Verify local host, tenant and API before any write; do not touch production or existing client records merely to make a screenshot. Capture matching build, permissions, visible controls, actual saved/read-back values and remaining gaps.
- [ ] **Step 6:** Independent review of the complete diff and evidence, then explicit owner handoff. Report workspace delivered separately from document-adapter and policy-library/Kody packages still open. No production push, deployment, flag enablement or library publication without separate authority.

## Execution and review ownership

Use the requested specialist-agent approach: catalogue/coverage, UI integration and independent QA/review lanes with one integration owner. Only one agent edits SetupPage or shared contracts at a time. Review each task against its tests before proceeding; final independent review covers the combined behavior. Existing working tree changes are not assumed to belong to the new task.

## Destination evidence from the read-only specialist pass

| Area | Verified initial destination / limitation |
| --- | --- |
| Organization | Existing `/setup?tab=companies`, branches, departments, designations, grades, costCenters, establishment and locations |
| Recruitment | `/recruitment` exists; requisitions/candidates are operations, not a general recruitment-policy editor |
| Onboarding | `/recruitment/onboarding` opens checklist templates; actual template scope is tenant-wide |
| Time | `/shifts` definitions/policy and `/overtime` policies exist as internal tabs; arbitrary query-tab links do not work |
| Leave | Existing types/policies/holidays editors; reuse directly because arbitrary query-tab links do not work |
| Compensation | `/benefits`, `/benefits/by-grade`, `/setup?tab=grades`; retain entitlement permissions and Release A gating |
| Payroll | `/setup?tab=glMapping`, fiscalYears, gccSettings, statutoryRules; salary structures are an internal Payroll tab |
| Performance | Existing cycles/templates internally; preserve cycle-management permission and module flag |
| Learning | `/learning?tab=courses`, paths and assign are supported; preserve LearningGate and authoring versus assignment permissions |
| Services | Notifications configure messages; HR Request Center is operations, not a general relations-policy editor |
| Offboarding | Existing exit/settlement screens are operations; no reusable generic offboarding-policy editor verified |
| Compliance | `/compliance-profiles` includes a specific Jawazat editor; general monitoring is not general policy authoring |

The inventory must not promote operational screens into settings merely to mark all twelve areas complete. Unmapped policies retain an honest source/library route while the related form gap remains on the programme backlog. Opening or publishing documents does not close such a gap.

## Self-review outcome

This package implements the approved navigation, existing-form reuse, continued tenant-admin editing and preservation boundary. It deliberately does not claim full document mapping, durable collaborative drafts, policy generation, approval/versioning, group inheritance or improved Kody intelligence. Those remain required programme work with separate plans. All five listed review risks are assigned explicit test cases above. Final user-visible completion of the whole concept requires all three packages, not merely a passing workspace fixture suite.
