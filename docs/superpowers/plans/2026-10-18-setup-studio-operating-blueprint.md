# Setup Studio Operating Blueprint Implementation Plan

> **Superseded before implementation on 2026-10-18:** The owner approved the existing five-step Guided Setup screen as the retained baseline. Do not execute this plan. Use `docs/superpowers/plans/2026-10-18-setup-configuration-center.md` instead.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the current scroll-heavy Setup Studio and visible five-step starter with the approved two-path home, Configuration Center, and adaptive Guided Operating Blueprint while preserving every canonical form, permission boundary, draft, and verified Apply contract.

**Architecture:** Introduce one pure route-state owner and enrich the existing destination registry so both paths resolve scope, access, capability, grouping, and navigation from the same source. Keep `SetupPage` as the integration boundary, split the current workspace into focused shell components, migrate the browser-local guided draft before unmounting inactive paths, and reuse the current assistant controller/API lifecycle behind a new blueprint presentation. This package changes frontend composition and local draft schema only; it adds no backend endpoint, database schema, generic form engine, universal update adapter, or combined transaction.

**Tech Stack:** Existing Next.js 15.5 / React 19 / TypeScript 6, Tailwind CSS 3.4, current KynexOne auth/company/feature/navigation contexts, browser `localStorage`, Playwright unit and browser tests, axe-core. No dependency upgrades.

**Spec:** `docs/superpowers/specs/2026-10-18-setup-studio-guided-operating-blueprint-design.md`

## Global Constraints

- Setup Studio has exactly two primary paths: `Guided Setup & Major Update` and `Configuration Center`.
- Guided V1 requires one authorized concrete company; `scope=group` remains unavailable and verified tenant-shared destinations are direct-configuration-only from a company Guided draft.
- Both paths use the same destination registry, canonical forms, current records, validation, permissions, feature checks, and server authorization.
- Keep `Save draft`, Configuration Center `Save`, `Apply batch proposals`, and any future `Apply <destination> changes` visibly and technically distinct; this package introduces no combined Apply action.
- Only `Employee Reference Lists` is an approved terminology rename. Do not rename other areas, destinations, routes, or operational modules.
- The main application sidebar and established operational modules are outside this redesign.
- Do not infer configured/completed status from a visit, upload, extraction, record count, default value, or successful navigation; do not show setup percentages.
- Only one path and one embedded editor may mount or fetch at a time. Restricted, unavailable, and unknown destinations never mount their editor.
- Preserve existing direct tabs and `mode=starter` as compatibility aliases; canonical transitions use the route contract from the spec.
- Preserve valid legacy draft inputs, reviewed preview, and uploaded source references. Browser `File` objects are not reload-persistable; active unuploaded files continue to block navigation.
- Preserve the existing atomic Setup Apply behavior and aggregate result truth. Do not invent per-task success where the response cannot prove it.
- Desktop, 390 px, 320 CSS px, 200% zoom, 400% reflow, keyboard-only, English, Arabic, and RTL must retain reachable actions and no horizontal page overflow.
- Preserve all pre-existing dirty and untracked work in `fix/policy-binder-draft`; never reset, stash, bulk-stage, or overwrite unrelated changes. Each task stages only its explicit files.
- Several integration files are already dirty before this plan. Before each task, record `git status --short` and the file-specific diff; at commit time use hunk-scoped staging for pre-existing files and inspect `git diff --cached`. If a task hunk cannot be separated safely from prior work, do not commit that file until the owner resolves the overlap.
- No merge, deployment, feature enablement, policy publication, or production/Evostel data mutation is authorized by this plan.

## Review Focus

1. A URL combines an unauthorized company, mismatched area/setting, legacy `mode=starter`, and canonical path parameters: it must fail closed, choose deterministic precedence, and canonicalize with `replace` without broadening scope (Tasks 1 and 4).
2. A valid v1 draft exists while the active session also has an unuploaded `File`: migration must preserve serializable inputs/preview/source references, retain recovery input until verified, and keep the live file navigation block without claiming reload recovery (Tasks 3 and 7).
3. Feature discovery fails or permission changes while a focused editor is open: the editor must unmount, stop protected reads, and show `Availability unknown` or the restricted explanation without losing another scope's draft (Tasks 2, 5, and 8).
4. Browser Back/Forward or a cancelled dirty-navigation confirmation occurs after a long focused form: URL, values, scroll, and initiating focus must remain stable; confirmed navigation executes exactly once and restores destination focus (Tasks 4, 5, and 8).
5. A tenant-shared destination is opened from company-context Guided V1 on Arabic mobile at 320 CSS px: it must be excluded from Guided counts, say `Applies to all companies`, offer only direct configuration, and remain usable without overflow or an incorrect company override (Tasks 2, 7, and 8).

---

## Package boundary and file ownership

This plan implements Phases 1-4 of the approved spec as one shippable frontend package. Capability-gated sparse-prefill adapters, destination-scoped staged canonical Apply, durable collaborative change sets, cross-device drafts, group inheritance, and universal Major Update automation remain separate later plans. In this package, Guided can prepare and apply only the existing verified Setup batch families; all other destinations are truthfully direct-save-only, review-only, related operations, or unavailable.

One integration owner edits `frontend/src/views/SetupPage.tsx`, `frontend/src/components/AiSetupAssistant.tsx`, `frontend/src/lib/setupAreas.ts`, and `frontend/src/lib/setupDraftLifecycle.ts`. Specialist agents may own isolated tests or focused new components, but must not edit these shared integration files concurrently.

### Files to create

- `frontend/src/lib/setupRouteState.ts` — pure parser, resolver, canonicalizer, and serializer for Setup home/configuration/guided route state and compatibility aliases.
- `frontend/unit/setupRouteState.spec.ts` — table-driven route precedence, scope, alias, and serialization tests.
- `frontend/src/lib/guidedSetupModel.ts` — pure mapping from v2 local draft, existing `SetupDraft`, registry capability, and current Apply aggregate to factual blueprint tasks/counts/recommendations.
- `frontend/unit/guidedSetupModel.spec.ts` — task-state precedence, batch-family evidence, counts, and recommendation tests.
- `frontend/src/components/setup/SetupStudioHome.tsx` — selected-scope summary, equal two-path choices, active local-draft summary, and supporting tools.
- `frontend/src/components/setup/SetupPathHeader.tsx` — shared path identity/scope/switching header; all switches use the existing navigation guard.
- `frontend/src/components/setup/SetupAreaBlueprint.tsx` — grouped, whole-row/node area canvas shared by both paths.
- `frontend/src/components/setup/SetupFocusedWorkspace.tsx` — full-width single-editor host, Back/previous/next controls, scope/capability copy, and focus-return contract.
- `frontend/src/components/setup/SetupAllAreasDialog.tsx` — desktop large area dialog and mobile full-screen focus-contained navigator.
- `frontend/src/components/setup/GuidedSetupBlueprint.tsx` — adaptive selected-area board, factual draft bar, blockers/next action, direct-only nodes, and review entry.
- `frontend/src/components/setup/GuidedSetupTask.tsx` — focused Guided task shell that hosts only existing verified source/manual/batch preparation behavior.

### Files to modify

- `frontend/src/views/SetupPage.tsx` — sole route/composition owner; exclusive path/editor mounting; canonical renderer reuse; compatibility handoffs.
- `frontend/src/components/setup/SetupStudioWorkspace.tsx` — reduce to the Configuration Center controller/composition using the new focused components.
- `frontend/src/components/setup/SetupAreaNavigator.tsx` — retire the inline expanding mobile list in favor of the accessible all-areas navigator, retaining a small compatibility export only if tests/imports require it.
- `frontend/src/components/AiSetupAssistant.tsx` — expose the current guided lifecycle through controlled render modes/callbacks and remove its five-step rail/footer from the canonical Guided path after migration parity is proved.
- `frontend/src/lib/setupAreas.ts` — add business grouping/order, scope/capability dimensions, save ownership, and verified batch-family metadata without inventing adapters.
- `frontend/src/lib/setupDraftLifecycle.ts` — v2 intent-qualified draft schema, defensive migration, read-after-write verification, and legacy recovery.
- `frontend/src/hooks/useSetupCompanyQuery.ts` — consume resolved canonical route scope instead of keying fail-closed behavior only to `mode=starter`.
- `frontend/src/i18n/setupStudio.ts` and `frontend/src/i18n/setupExperience.ts` — exact English/Arabic copy for the two paths, scope, blueprint states, actions, and recovery messages.
- `frontend/src/i18n/translations.ts` — register only new/updated dictionaries through the incumbent merge pattern.
- `frontend/e2e/setup-studio-workspace.spec.ts` — route, Configuration Center, focus, dirty guard, capability, mobile/RTL, and inactive-fetch coverage.
- `frontend/e2e/setup-lifecycle.spec.ts` — v1 migration, intent isolation, checkpoint, preview, source, and pending-file regression coverage.
- `frontend/e2e/setup-experience.spec.ts`, `frontend/e2e/policy-form-prefill.spec.ts`, `frontend/e2e/policy-binder.spec.ts`, `frontend/e2e/policy-review.spec.ts` — update compatibility entry assertions without weakening existing policy/source behavior.
- `docs/SETUP_STUDIO_WORKSPACE_VERIFICATION.md` — fresh commands, local build/runtime identity, screenshots, accessibility results, known gaps, and explicit non-claims.

### Existing interfaces retained unchanged

- `SetupNavigationGuard` from `useSetupNavigationGuard.ts` remains the single risky-navigation authority.
- `renderExistingSettings(id: SettingsTab)` and each embedded form's existing Save/API behavior remain canonical.
- `setupAssistantApi.preview(profile)` and `setupAssistantApi.apply(draft, countryCode, currencyCode, legalEntityName?, companyId?)` remain the only batch preview/apply APIs.
- `SetupEditorState` and `SetupPendingFilesState` remain the dirty and pending-file signals consumed by the application guard.

### Task 1: Characterize and centralize Setup route state

**Files:**
- Create: `frontend/src/lib/setupRouteState.ts`
- Create: `frontend/unit/setupRouteState.spec.ts`
- Modify: `frontend/src/hooks/useSetupCompanyQuery.ts`
- Test: `frontend/e2e/setup-studio-workspace.spec.ts`

**Interfaces:**
- Consumes: `SetupAreaId`, `SETUP_AREAS`, authorized company IDs, and raw `URLSearchParams`.
- Produces:
  - `type SetupPath = 'home' | 'configuration' | 'guided'`
  - `type GuidedIntent = 'initial' | 'major-update'`
  - `type GuidedIntentParam = 'initial' | 'majorUpdate'` (URL only; the parser maps this to `GuidedIntent`)
  - `type GuidedView = 'board' | 'task' | 'review'`
  - `type SetupScope = { kind: 'company'; companyId: string } | { kind: 'tenant' } | { kind: 'unresolved'; reason: 'missing-company' | 'unauthorized-company' | 'unsupported-group' }`
  - `type SetupRouteState = { path: SetupPath; scope: SetupScope; area: SetupAreaId | null; setting: string | null; intent: GuidedIntent | null; view: GuidedView | null }`
  - `type SetupRouteResolution = { state: SetupRouteState; canonicalParams: URLSearchParams; needsReplace: boolean; notice?: 'scope-required' | 'scope-unauthorized' | 'setting-mismatch' | 'legacy-draft-task-unavailable' }`
  - `resolveSetupRoute(params: URLSearchParams, context: { authorizedCompanyIds: ReadonlySet<string>; selectedCompanyId: string | null; draftContainsTask?: (area: SetupAreaId, setting: string) => boolean }): SetupRouteResolution`
  - `serializeSetupRoute(state: SetupRouteState, preserve: URLSearchParams): URLSearchParams`

- [ ] **Step 1: Write failing route-unit tests**

Add table-driven tests named `legacy_starter_resolves_to_guided_initial`, `canonical_path_beats_legacy_mode`, `existing_area_setting_resolves_to_configuration`, `guided_board_strips_task_coordinates`, `guided_task_requires_matching_destination`, `legacy_tab_strips_studio_parameters`, `unauthorized_company_never_falls_back_to_group`, and `serializer_preserves_destination_specific_parameters`. Assert canonical values from Spec §5, `needsReplace`, and preservation of `department`, `level`, `setupArea`, and unrelated allowlisted return context.

- [ ] **Step 2: Run the route-unit test and verify the expected failure**

Run: `cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupRouteState.spec.ts`

Expected: FAIL because `setupRouteState.ts` and its exports do not exist.

- [ ] **Step 3: Implement the pure route resolver and serializer**

Implement the exact interfaces above. Parse URL `intent=majorUpdate` to domain value `major-update` and serialize it back to `majorUpdate`. Use registry membership for area/setting validation; use `replace` only for compatibility cleanup or invalid-state recovery. Do not read React context, mutate the router, infer authorization, or place draft/source content in the URL.

- [ ] **Step 4: Run route-unit tests to green**

Run: `cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupRouteState.spec.ts`

Expected: all route-state tests PASS.

- [ ] **Step 5: Add failing browser characterization for history and fail-closed scope**

In `setup-studio-workspace.spec.ts`, add `route_aliases_canonicalize_without_duplicate_history`, `browser_back_forward_restores_home_area_and_setting`, and `unauthorized_guided_company_stays_on_scope_picker`. Assert canonical URL, history length behavior, no protected editor request, and scope explanation.

- [ ] **Step 6: Integrate canonical scope resolution into `useSetupCompanyQuery`**

Replace the `mode === 'starter'` special case with the resolved path/scope contract. The hook may reconcile CompanyContext and the canonical query, but must never substitute a broader company/group scope for an invalid Guided target.

- [ ] **Step 7: Run focused tests and typecheck**

Run: `cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupRouteState.spec.ts unit/setupAreas.spec.ts`

Then run: `cd frontend && npx tsc --noEmit`

Expected: PASS with no TypeScript errors.

- [ ] **Step 8: Commit Task 1**

```bash
git add frontend/src/lib/setupRouteState.ts frontend/unit/setupRouteState.spec.ts frontend/src/hooks/useSetupCompanyQuery.ts frontend/e2e/setup-studio-workspace.spec.ts
git commit -m "feat: centralize Setup Studio route state"
```

### Task 2: Enrich the shared destination registry with truthful capabilities

**Files:**
- Modify: `frontend/src/lib/setupAreas.ts`
- Modify: `frontend/unit/setupAreas.spec.ts`
- Create: `frontend/src/lib/guidedSetupModel.ts`
- Create: `frontend/unit/guidedSetupModel.spec.ts`

**Interfaces:**
- Consumes: existing `navigationItems`, `SetupDraft` families, auth/role/feature verdicts, and current Setup Apply aggregate shape.
- Produces additions to `SetupDestination`:
  - `businessGroup: { id: string; label: string }` (stable within its owning area)
  - `groupOrder: number`, `destinationOrder: number`
  - `allowedGuidedScopes: readonly ('company')[]`
  - `guidedIntents: readonly GuidedIntent[]`
  - `inputAssistance: readonly ('manual' | 'document-prefill')[]`
  - `targetOperations: readonly ('create' | 'update')[]`
  - `commitStrategy: 'verified-batch-apply' | 'direct-save-only' | 'review-only' | 'unavailable'`
  - `effectiveTargetScope: 'company' | 'tenant' | 'recordDependent'`
  - `saveOwner: 'setupAssistantBatch' | 'canonicalDestination' | 'operationalRoute' | 'none'`
  - `type SetupBatchFamily = 'benefitPlans' | 'branches' | 'departments' | 'costCenters' | 'designations' | 'grades' | 'gradePayComponents' | 'leaveTypes' | 'shifts' | 'workingWeek' | 'payComponents' | 'statutoryRules' | 'employeeIdRule' | 'hrConfig' | 'leavePolicies' | 'holidayCalendar' | 'attendancePolicy' | 'overtimePolicy' | 'localization'`
  - `batchFamilies?: readonly SetupBatchFamily[]`
- Produces:
  - `type GuidedWorkflowState = 'notInPlan' | 'notAssessed' | 'draftInProgress' | 'needsInformation' | 'conflict' | 'readyForReview' | 'applied' | 'requiresVerification' | 'failed' | 'deferred' | 'notApplicable'`
  - `deriveGuidedTask(destination, input): GuidedTaskState`
  - `summarizeGuidedTasks(tasks): { draft: number; blockers: number; proposed: number; applied: number; deferred: number }`
  - `recommendNextTask(tasks): GuidedTaskState | null`

- [ ] **Step 1: Write failing registry tests for grouping, scope, and capability**

Extend `setupAreas.spec.ts` with `every_destination_has_stable_group_and_order`, `tenant_shared_destinations_are_direct_only_in_company_guided_v1`, `operations_never_enter_guided_apply_counts`, `employee_reference_lists_is_the_only_renamed_label`, and `batch_families_are_unique_and_real_setup_draft_keys`. Assert exactly twelve area IDs and retain all current destination paths.

- [ ] **Step 2: Write failing pure-model tests for state precedence**

In `guidedSetupModel.spec.ts`, test access precedence over capability/workflow, exclusion of direct-only/review-only/operations/unselected tasks, blockers before proposals/in-progress/unassessed in recommendation order, and aggregate Apply mapping to `requiresVerification` unless the declared family evidence deterministically proves the task.

- [ ] **Step 3: Run tests and verify expected failures**

Run: `cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupAreas.spec.ts unit/guidedSetupModel.spec.ts`

Expected: FAIL for missing capability metadata and model exports.

- [ ] **Step 4: Add registry metadata conservatively**

Populate metadata for every existing destination. Mark only families currently accepted by `setupAssistantApi.apply` as `verified-batch-apply`; mark reusable live forms without a reviewed staging adapter `direct-save-only`; keep operations owned by `operationalRoute`; do not infer major-update support from a route or form.

- [ ] **Step 5: Implement pure state/count/recommendation derivation**

Implement the exact model exports. Apply precedence is access/availability, then capability, then workflow. Use registry order as the deterministic tie-breaker. Never use visited state, upload state, or extraction completion as applied/configured evidence.

- [ ] **Step 6: Run tests and typecheck**

Run: `cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupAreas.spec.ts unit/guidedSetupModel.spec.ts`

Then run: `cd frontend && npx tsc --noEmit`

Expected: PASS.

- [ ] **Step 7: Commit Task 2**

```bash
git add frontend/src/lib/setupAreas.ts frontend/unit/setupAreas.spec.ts frontend/src/lib/guidedSetupModel.ts frontend/unit/guidedSetupModel.spec.ts
git commit -m "feat: model Setup Studio destination capabilities"
```

### Task 3: Migrate browser-local guided drafts without data loss

**Files:**
- Modify: `frontend/src/lib/setupDraftLifecycle.ts`
- Modify: `frontend/unit/setupDraftLifecycle.spec.ts`
- Modify: `frontend/e2e/setup-lifecycle.spec.ts`
- Modify: `frontend/src/api/clearSession.ts` only if its prefix cleanup does not already remove both v1 and v2 keys.

**Interfaces:**
- Consumes: current `SavedSetupDraft` v1, `CompanyProfile`, `SavedSetupPreview`, tenant/user/company IDs, immutable `GuidedIntent`.
- Produces:
  - `interface SavedSetupDraftV2 { version: 2; savedAt: string; createdAt: string; label?: string; intent: GuidedIntent; selectedAreas: SetupAreaId[]; dispositions: Record<string, { state: 'deferred' | 'notApplicable'; reason: string }>; lastActive?: { area: SetupAreaId; setting?: string }; legacyStep?: number; profile: CompanyProfile; reviewedPreview?: SavedSetupPreview }`
  - `setupDraftStorageKeyV2(tenantId: string, userId: string, companyId: string, intent: GuidedIntent): string`
  - `readSetupDraftV2(storage, key): SavedSetupDraftV2 | null`
  - `saveSetupDraftV2(storage, key, draft): SavedSetupDraftV2`
  - `migrateSetupDraftV1(storage, identity): { draft: SavedSetupDraftV2 | null; source: 'v2' | 'migrated-v1' | 'none'; warning?: 'write-failed' | 'verification-failed' }`

- [ ] **Step 1: Write failing migration unit tests**

Add `valid_v1_migrates_to_initial_intent_and_preserves_preview_sources`, `migration_writes_then_reads_before_trusting_v2`, `failed_migration_keeps_v1_recovery_key`, `v2_isolated_by_tenant_user_company_and_intent`, `malformed_v2_is_ignored`, and `draft_never_serializes_file_objects_or_claims_file_recovery`. Assert the legacy key remains readable until verified v2 exists.

- [ ] **Step 2: Run unit tests and verify failure**

Run: `cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupDraftLifecycle.spec.ts`

Expected: FAIL for missing v2 interfaces/functions.

- [ ] **Step 3: Implement strict v2 validation and two-phase migration**

Reuse the existing profile/preview validation. Treat every valid v1 draft as `initial`, derive selected areas only from its persisted sections, preserve `reviewedPreview` and `configuration.policySources`, and store `legacyStep` only as compatibility position—not as a visible milestone. On write/readback failure, return the valid v1-derived draft in memory with a warning and leave v1 untouched.

- [ ] **Step 4: Run migration unit tests to green**

Run: `cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupDraftLifecycle.spec.ts`

Expected: PASS.

- [ ] **Step 5: Add browser tests for intent isolation and pending files**

In `setup-lifecycle.spec.ts`, add `legacy_draft_resumes_in_guided_initial`, `major_update_uses_a_separate_local_key`, `migration_preserves_reviewed_preview_and_policy_sources`, `unuploaded_file_blocks_path_switch_until_removed`, and `reload_does_not_claim_unuploaded_file_recovery`.

- [ ] **Step 6: Run lifecycle browser tests against the pre-integration UI**

Run: `cd frontend && npx playwright test -c e2e/playwright.setup-lifecycle.config.ts`

Expected: new UI-facing migration tests FAIL while existing lifecycle tests remain passing.

- [ ] **Step 7: Update session cleanup for both key versions if required**

If `clearSessionKeepingLocale` uses an exact v1 prefix, broaden it only to `kynexone-setup-draft:` and add a unit assertion. Do not clear drafts on ordinary Setup navigation.

- [ ] **Step 8: Commit Task 3**

```bash
git add frontend/src/lib/setupDraftLifecycle.ts frontend/unit/setupDraftLifecycle.spec.ts frontend/e2e/setup-lifecycle.spec.ts frontend/src/api/clearSession.ts
git commit -m "feat: migrate intent-scoped Setup Studio drafts"
```

### Task 4: Introduce the two-path home and exclusive route composition

**Files:**
- Create: `frontend/src/components/setup/SetupStudioHome.tsx`
- Create: `frontend/src/components/setup/SetupPathHeader.tsx`
- Modify: `frontend/src/views/SetupPage.tsx`
- Modify: `frontend/src/i18n/setupStudio.ts`
- Modify: `frontend/src/i18n/translations.ts`
- Modify: `frontend/e2e/setup-studio-workspace.spec.ts`

**Interfaces:**
- Consumes: `SetupRouteResolution`, authorized company scope, `SetupNavigationGuard`, v2 draft summary, existing Policy Library/Organization Import/Administration callbacks.
- Produces:
  - `SetupStudioHome({ scope, activeDraft, onChoosePath, onOpenPolicyLibrary, onOpenImport, onOpenAdministration })`
  - `SetupPathHeader({ path, scopeLabel, persistenceLabel?, onBackHome, onSwitchPath })`
  - `SetupPage` mounts exactly one of home, Configuration Center, or Guided path for `tab=aiSetup`.

- [ ] **Step 1: Write failing home/composition browser tests**

Add `home_shows_exactly_two_equal_primary_paths`, `home_requires_company_before_guided_start`, `supporting_tools_are_not_a_third_path`, `inactive_path_is_unmounted_and_issues_no_requests`, and `path_switch_uses_single_dirty_guard`. Assert visible copy distinguishes draft preparation from live configuration and browser-local persistence.

- [ ] **Step 2: Run the dedicated workspace test and verify failure**

Run: `cd frontend && npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts --project=desktop -g "home|inactive path|path switch"`

Expected: FAIL because the current default renders `SetupStudioWorkspace` and retains the starter hidden/mounted.

- [ ] **Step 3: Implement home and shared path header**

Keep scope selection above the two choices. `Guided Setup & Major Update` and `Configuration Center` receive equal heading/button treatment. Show at most one real local draft for the selected company+intent, with `Stored in this browser`; omit owner/collaboration/cross-device claims.

- [ ] **Step 4: Make `SetupPage` the sole route-state owner**

Resolve and canonicalize once, use `router.replace` for cleanup/recovery, and pass explicit navigation callbacks to child paths. Remove `visitedStarterCompany` and the hidden retained `<AiSetupAssistant>` only after Task 3 restoration is wired. Preserve legacy direct tabs, policy library, import, and destination-specific query parameters.

- [ ] **Step 5: Add English/Arabic path, scope, and recovery copy**

Translate every new visible string in `setupStudio.ts` and register it through the current translation merge. Use administrator language: `Applies to <company>` and `Applies to all companies`, not internal-only scope jargon.

- [ ] **Step 6: Run tests and typecheck**

Run: `cd frontend && npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts --project=desktop -g "home|inactive path|path switch"`

Then run: `cd frontend && npx tsc --noEmit`

Expected: PASS; inactive Guided/Configuration components and their fixture API requests are absent.

- [ ] **Step 7: Commit Task 4**

```bash
git add frontend/src/components/setup/SetupStudioHome.tsx frontend/src/components/setup/SetupPathHeader.tsx frontend/src/views/SetupPage.tsx frontend/src/i18n/setupStudio.ts frontend/src/i18n/translations.ts frontend/e2e/setup-studio-workspace.spec.ts
git commit -m "feat: add Setup Studio two-path home"
```

### Task 5: Build Configuration Center blueprint and focused workspace

**Files:**
- Create: `frontend/src/components/setup/SetupAreaBlueprint.tsx`
- Create: `frontend/src/components/setup/SetupFocusedWorkspace.tsx`
- Create: `frontend/src/components/setup/SetupAllAreasDialog.tsx`
- Modify: `frontend/src/components/setup/SetupStudioWorkspace.tsx`
- Modify: `frontend/src/components/setup/SetupAreaNavigator.tsx`
- Modify: `frontend/e2e/setup-studio-workspace.spec.ts`
- Modify: `frontend/src/i18n/setupStudio.ts`

**Interfaces:**
- Consumes: enriched `SETUP_AREAS`, `destinationAvailability`, `renderSettings`, `SetupNavigationGuard`, canonical route callbacks, existing company/auth/feature contexts.
- Produces:
  - `SetupAreaBlueprint({ path: 'configuration' | 'guided'; area; groups; selectedSetting; onOpenDestination })`
  - `SetupFocusedWorkspace({ destination, scopeLabel, onBack, onPrevious?, onNext?, children })`
  - `SetupAllAreasDialog({ open, activeArea, onSelect, onClose, triggerRef })`
  - `SetupStudioWorkspace` renders either an area blueprint or one focused full-width editor, never both.

- [ ] **Step 1: Replace detached-button assertions with failing whole-row tests**

Update existing tests that click `Open settings`. Add `complete_destination_row_is_the_action`, `focused_editor_replaces_area_canvas`, `back_restores_origin_focus_and_scroll`, `previous_next_are_reachable_after_long_form`, `browse_all_areas_uses_dialog_on_desktop`, and `mobile_navigator_is_full_screen_focus_contained_and_escape_closable`.

- [ ] **Step 2: Add security/capability regression tests before implementation**

Add `permission_loss_unmounts_open_editor`, `feature_error_shows_unknown_without_fetch`, `tenant_shared_header_says_applies_to_all_companies`, and retain the existing canonical PUT/identity/save-failure tests. Assert no generic Setup Apply request occurs on Configuration Center Save.

- [ ] **Step 3: Run focused tests and verify failure**

Run: `cd frontend && npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts -g "destination row|focused editor|Browse all|navigator|permission loss|feature error|all companies"`

Expected: FAIL against the current stacked editor/list layout and inline mobile disclosure.

- [ ] **Step 4: Implement grouped whole-row area canvases**

Group registry destinations in stable `groupOrder`/`destinationOrder`. Make the entire available row/node one semantic button or link with its specific destination label; keep restricted/unavailable rows non-actionable. Remove the generic far-right `Open settings` control.

- [ ] **Step 5: Implement one full-width focused editor host**

When `setting` is present and available, mount only its canonical editor. Move scope/impact/capability copy next to the heading. Provide Back near the heading and after long forms, plus previous/next only among available destinations in the same area. On entry focus the named region; on Back restore the originating row and stored scroll position.

- [ ] **Step 6: Implement desktop and mobile all-area navigation**

Desktop uses a large labelled dialog; mobile uses a full-screen dialog. Focus the dialog heading on open, contain focus, close on explicit Close/Escape when no dirty decision is pending, restore trigger focus, and focus the selected area heading after navigation. Do not add a horizontal twelve-tab strip.

- [ ] **Step 7: Run Configuration Center tests, axe subset, and typecheck**

Run: `cd frontend && npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts -g "Configuration|destination|focused|Browse all|navigator|permission|feature|canonical|save"`

Then run: `cd frontend && npx tsc --noEmit`

Expected: PASS with no serious/critical axe findings in the tested desktop/mobile views.

- [ ] **Step 8: Commit Task 5**

```bash
git add frontend/src/components/setup/SetupAreaBlueprint.tsx frontend/src/components/setup/SetupFocusedWorkspace.tsx frontend/src/components/setup/SetupAllAreasDialog.tsx frontend/src/components/setup/SetupStudioWorkspace.tsx frontend/src/components/setup/SetupAreaNavigator.tsx frontend/src/i18n/setupStudio.ts frontend/e2e/setup-studio-workspace.spec.ts
git commit -m "feat: build Configuration Center blueprint"
```

### Task 6: Separate the guided controller from the five-step presentation

**Files:**
- Modify: `frontend/src/components/AiSetupAssistant.tsx`
- Modify: `frontend/src/lib/setupDraftLifecycle.ts`
- Modify: `frontend/e2e/setup-lifecycle.spec.ts`
- Modify: `frontend/e2e/policy-form-prefill.spec.ts`
- Modify: `frontend/e2e/policy-binder.spec.ts`
- Modify: `frontend/e2e/policy-review.spec.ts`

**Interfaces:**
- Consumes: current assistant state, policy source/extraction/review components, `setupAssistantApi.preview/apply`, v2 draft persistence, `SetupPendingFilesState`.
- Produces additions to `AiSetupAssistant`:
  - `mode: 'legacyCompatibility' | 'guidedTask' | 'guidedReview'`
  - `activeDestinationId?: string`
  - `intent: GuidedIntent`
  - `onSnapshotChange?: (snapshot: GuidedSetupSnapshot) => void`
  - `onNavigate?: (target: { view: GuidedView; area?: SetupAreaId; setting?: string }) => void`
  - `GuidedSetupSnapshot = { draft: SavedSetupDraftV2; preview: SavedSetupPreview | null; pendingFiles: boolean; busy: boolean; lastApplyResult?: { applied: Record<string, number>; total: number; skipped?: Record<string, { count: number; reasonCode: string; reason: string }> } }`

- [ ] **Step 1: Add failing lifecycle parity tests**

Add `guided_task_restores_same_profile_and_sources_as_legacy`, `guided_review_restores_edited_preview`, `guided_modes_do_not_render_numbered_step_rail_or_continue_footer`, `save_draft_performs_no_operational_write`, `preview_performs_no_operational_write`, and `batch_apply_uses_the_existing_endpoint_and_result_shape`.

- [ ] **Step 2: Run lifecycle and policy suites to establish the failure/baseline**

Run these separately so every baseline is recorded even if one suite fails:

```bash
cd frontend
npx playwright test -c e2e/playwright.setup-lifecycle.config.ts
npx playwright test -c e2e/playwright.policy-form-prefill.config.ts
npx playwright test -c e2e/playwright.policy-binder.config.ts
npx playwright test -c e2e/playwright.policy-review.config.ts
```

Expected: new guided-mode assertions FAIL; pre-existing scenarios retain their current baseline.

- [ ] **Step 3: Add controlled render modes without changing API contracts**

Keep one assistant controller/state owner. `legacyCompatibility` may retain the old visual shell only for uncanonicalized direct compatibility during the transition. `guidedTask` renders the existing relevant company/source/policy preparation content for the selected verified batch destination without the step rail/footer. `guidedReview` renders the current editable preview and separate batch Apply result. Do not duplicate form state into a second controller.

- [ ] **Step 4: Wire v2 checkpoint and snapshot callbacks**

Persist label/intent/selected areas/dispositions/last-active location with current profile and reviewed preview. Expose pending files and busy state. Storage failure becomes a protected unsaved state and visible error; it never reports `Saved draft`.

- [ ] **Step 5: Preserve exact preview/apply and policy boundaries**

Preview and extraction remain non-mutating. Batch Apply keeps the current endpoint, role/permission checks, atomic behavior, aggregate counts/skips, and company target. Policy review/publication remain separate. Do not introduce destination-scoped Apply in this task.

- [ ] **Step 6: Run lifecycle and policy regression suites**

Run the four commands from Step 2, then `cd frontend && npx tsc --noEmit`.

Expected: PASS; no new endpoint or request shape appears in traces.

- [ ] **Step 7: Commit Task 6**

```bash
git add frontend/src/components/AiSetupAssistant.tsx frontend/src/lib/setupDraftLifecycle.ts frontend/e2e/setup-lifecycle.spec.ts frontend/e2e/policy-form-prefill.spec.ts frontend/e2e/policy-binder.spec.ts frontend/e2e/policy-review.spec.ts
git commit -m "refactor: expose guided Setup Studio lifecycle"
```

### Task 7: Build the adaptive Guided Operating Blueprint

**Files:**
- Create: `frontend/src/components/setup/GuidedSetupBlueprint.tsx`
- Create: `frontend/src/components/setup/GuidedSetupTask.tsx`
- Modify: `frontend/src/views/SetupPage.tsx`
- Modify: `frontend/src/components/AiSetupAssistant.tsx`
- Modify: `frontend/src/i18n/setupStudio.ts`
- Modify: `frontend/src/i18n/setupExperience.ts`
- Modify: `frontend/e2e/setup-studio-workspace.spec.ts`
- Modify: `frontend/e2e/setup-lifecycle.spec.ts`

**Interfaces:**
- Consumes: `GuidedSetupSnapshot`, `deriveGuidedTask`, `summarizeGuidedTasks`, `recommendNextTask`, registry groups, v2 route state, existing assistant guided modes.
- Produces:
  - `GuidedSetupBlueprint({ snapshot, area, tasks, onOpenTask, onAddArea, onRemoveArea, onReview, onSaveDraft })`
  - `GuidedSetupTask({ destination, snapshot, onSnapshotChange, onBack, onReview })`
  - canonical Guided route views `board`, `task`, and `review`.

- [ ] **Step 1: Write failing Guided board tests**

Add `guided_start_collects_scope_intent_label_areas_and_optional_source`, `board_shows_selected_milestones_not_numbered_steps`, `unselected_permitted_area_is_not_in_plan_and_excluded_from_counts`, `recommended_action_uses_registry_precedence`, `removing_area_with_checkpointed_work_requires_confirmation`, and `visit_upload_or_parse_never_marks_task_applied`.

- [ ] **Step 2: Write failing capability/result tests**

Add `tenant_shared_task_is_direct_only_and_excluded_from_counts`, `review_separates_verified_batch_from_direct_only_items`, `batch_apply_button_names_supported_count_only`, `aggregate_success_without_task_evidence_requires_verification`, `failed_apply_retains_reviewed_draft`, and `major_update_does_not_offer_unverified_automatic_updates`.

- [ ] **Step 3: Run Guided-focused tests and verify failure**

Run: `cd frontend && npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts -g "guided|board|milestone|recommended|direct only|batch|major update"`

Expected: FAIL because no Guided blueprint exists.

- [ ] **Step 4: Implement start and board views**

Require authorized company scope before creating/resuming a draft. Intent is immutable per draft. Render selected areas as adaptive milestones, permitted unselected areas as `Not in plan`, factual counts only, and the deterministic next action. Provide `Add areas`, guarded removal, `Save draft`, `Review changes`, and `Browse all areas` without completion percentage or mandatory order.

- [ ] **Step 5: Implement task view with capability gates**

For verified batch destinations, host `AiSetupAssistant mode="guidedTask"` with the relevant existing preparation content. For tenant-shared/direct-only destinations, show impact and open Configuration Center; do not add to proposal counts. For operations/review-only/unavailable destinations, show only the truthful route/explanation. Mount no inactive task/editor.

- [ ] **Step 6: Implement grouped review using current batch truth**

Show verified batch proposals under `Apply batch proposals`; show direct-save/review-only items outside its count with their real actions. Reuse the existing editable preview and Apply call. Map task `Applied` only where registry family/evidence proves it; otherwise show the aggregate result and `Result requires verification`.

- [ ] **Step 7: Remove the canonical visible five-step experience**

Canonical `path=guided` never renders `Company details / Working week / People & pay / Grades & benefits / Review & create` as a numbered completion rail or previous/continue footer. Legacy `mode=starter` canonicalizes into the new views after migration; keep only the minimum compatibility parsing needed for old links.

- [ ] **Step 8: Run Guided, lifecycle, policy, and type checks**

Run these separately:

```bash
cd frontend
npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts
npx playwright test -c e2e/playwright.setup-lifecycle.config.ts
npx playwright test -c e2e/playwright.policy-form-prefill.config.ts
npx playwright test -c e2e/playwright.policy-binder.config.ts
npx playwright test -c e2e/playwright.policy-review.config.ts
npx tsc --noEmit
```

Expected: PASS; Guided navigation/preview causes no operational mutation, and only explicit batch Apply calls `/api/setup-assistant/apply`.

- [ ] **Step 9: Commit Task 7**

```bash
git add frontend/src/components/setup/GuidedSetupBlueprint.tsx frontend/src/components/setup/GuidedSetupTask.tsx frontend/src/views/SetupPage.tsx frontend/src/components/AiSetupAssistant.tsx frontend/src/i18n/setupStudio.ts frontend/src/i18n/setupExperience.ts frontend/e2e/setup-studio-workspace.spec.ts frontend/e2e/setup-lifecycle.spec.ts
git commit -m "feat: add Guided Setup operating blueprint"
```

### Task 8: Complete responsive, RTL, accessibility, regression, and evidence gates

**Files:**
- Modify: `frontend/e2e/setup-studio-workspace.spec.ts`
- Modify: `frontend/e2e/setup-experience.spec.ts`
- Modify: `frontend/src/i18n/setupStudio.ts`
- Modify: `frontend/src/styles/index.css` only for defects proven by this task's tests and not expressible with existing utilities.
- Create or modify: `docs/SETUP_STUDIO_WORKSPACE_VERIFICATION.md`

**Interfaces:**
- Consumes: completed home, Configuration Center, Guided blueprint, existing Playwright configs, axe-core, local shadow-tenant fixtures.
- Produces: test and evidence package proving the approved frontend scope; no new product/runtime API.

- [ ] **Step 1: Add failing cross-cutting acceptance tests**

Add `english_and_arabic_core_journeys_have_no_serious_or_critical_axe_findings`, `mobile_390_and_320_have_no_page_overflow`, `rtl_long_labels_keep_actions_reachable`, `two_hundred_percent_zoom_keeps_editor_navigation_reachable`, `four_hundred_percent_reflow_has_no_two_axis_scroll`, `dirty_cancel_preserves_url_values_scroll_and_focus`, `dirty_confirm_navigates_once`, and `inactive_editor_stops_requests_after_permission_change`.

- [ ] **Step 2: Run the acceptance tests before fixes**

Run: `cd frontend && npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts`

Expected: any remaining responsive, focus, translation, or composition defects FAIL with a reproducible assertion/screenshot.

- [ ] **Step 3: Fix only defects demonstrated by the acceptance tests**

Prefer existing Tailwind tokens and logical properties. If CSS is required, scope selectors to Setup Studio. Preserve DOM/keyboard order in RTL, mirror directional icons only, keep sticky actions from covering focused fields/errors, and do not reduce tests to make the UI pass.

- [ ] **Step 4: Run all affected unit and browser suites**

Run:

```bash
cd frontend
npx playwright test -c playwright.unit.config.ts unit/setupRouteState.spec.ts unit/setupAreas.spec.ts unit/guidedSetupModel.spec.ts unit/setupDraftLifecycle.spec.ts unit/setupCanonicalForms.spec.ts
npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts
npx playwright test -c e2e/playwright.setup-lifecycle.config.ts
npx playwright test -c e2e/playwright.setup-experience.config.ts
npx playwright test -c e2e/playwright.policy-form-prefill.config.ts
npx playwright test -c e2e/playwright.policy-binder.config.ts
npx playwright test -c e2e/playwright.policy-review.config.ts
npx tsc --noEmit
NEXT_PUBLIC_API_BASE_URL=http://127.0.0.1:5127 npm run build
```

Expected: every command PASS. Record failures separately; do not collapse fixture evidence into backend security certification.

- [ ] **Step 5: Perform manual keyboard, focus, zoom, and screen-reader checks**

Verify home → both paths → area → focused setting → Back, dirty cancellation, all-areas dialog, Guided review, English/Arabic, 320/390 widths, 200% zoom, and 400% reflow. Confirm spoken path/scope/draft/live distinctions and no hidden action after a long form.

- [ ] **Step 6: Validate the local shadow-tenant runtime read-only first**

Confirm signed-in persona, tenant/company scope, frontend/backend/build identity, and populated canonical records before any dedicated test-record write. Reopen one saved Configuration Center record and one resumed Guided draft. Do not use production or existing customer records.

- [ ] **Step 7: Capture verification evidence and explicit gaps**

Document commands/results, runtime URLs, build provenance, screenshots for desktop/mobile/Arabic, actual saved/read-back behavior, draft migration evidence, and remaining later-plan work: destination adapters, staged canonical Apply, durable collaboration, group inheritance, and broader Major Update automation.

- [ ] **Step 8: Request independent whole-diff review**

The reviewer checks route/scope fail-closed behavior, exclusive mounting, canonical form identity, draft migration/recovery, batch-result truth, dirty navigation, accessibility, and no accidental backend/production change. Resolve P0/P1 findings and rerun the affected gates.

- [ ] **Step 9: Commit Task 8**

```bash
git add frontend/e2e/setup-studio-workspace.spec.ts frontend/e2e/setup-experience.spec.ts frontend/src/i18n/setupStudio.ts frontend/src/styles/index.css docs/SETUP_STUDIO_WORKSPACE_VERIFICATION.md
git commit -m "test: verify Setup Studio operating blueprint"
```

## Deferred follow-on plans

1. Per-destination document-prefill and sparse-update adapters with target identity, baseline/concurrency, source evidence, API contract, and readback.
2. Destination-scoped staged canonical Apply and durable server-backed change sets where transaction/retry/approval requirements justify them.
3. Group baseline/inheritance and authorized tenant/group Guided scope.
4. Cross-device collaboration, multiple plans, scheduling, generic maker/checker approval, rollback, and coordinated retry.
5. Broader Major Update automation only after each destination proves create/update and stale-target semantics.

## Self-review outcome

- **Spec coverage:** Tasks 1-8 cover route precedence, the two-path IA, all twelve shared areas, Configuration Center, Guided board/task/review, v1 migration, exclusive mounting, canonical form reuse, current batch truth, permissions/scope, dirty navigation, focus/history, Arabic/RTL/responsive/accessibility, and local evidence. Spec Phases 5-6 are explicitly deferred because they require per-destination contracts or backend design.
- **Step scan:** Each task has a failing test, expected failure, one bounded implementation action per step, passing verification, and an explicit commit. No task authorizes product behavior outside its tests/interfaces.
- **Type consistency:** `GuidedIntent`, `GuidedView`, `SetupRouteState`, registry capability values, `SavedSetupDraftV2`, `GuidedSetupSnapshot`, and task-state names are introduced once and consumed consistently downstream.
- **Review Focus:** Each of the five high-risk input classes maps to named tests in Tasks 1-8, including combined invalid routes, migration plus pending files, permission/feature changes, dirty Back/Forward focus, and tenant-shared Arabic mobile behavior.
- **Proportion:** The plan decides file ownership, interfaces, assertions, and delivery gates without reproducing component bodies. Backend adapters and durable change-set behavior remain separate plans rather than being hidden in this UI package.
