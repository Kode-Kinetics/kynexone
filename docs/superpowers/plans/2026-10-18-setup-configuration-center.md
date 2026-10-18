# Setup Studio Configuration Center Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve the approved existing five-step Guided Setup screen while replacing the inefficient direct-maintenance workspace with a purpose-grouped, full-width Configuration Center shared across all twelve setup areas.

**Architecture:** Keep `AiSetupAssistant` and its current lifecycle as the Guided path without a presentation rewrite. Add a small route-state boundary that distinguishes Guided Setup from Configuration Center, enrich the existing destination catalogue only with grouping/order and truthful scope metadata, and split the current Configuration workspace into an area blueprint, single focused canonical editor, and accessible all-area navigator. Existing forms, APIs, permissions, feature gates, draft storage, preview, and Apply behavior remain authoritative.

**Tech Stack:** Existing Next.js 15.5 / React 19 / TypeScript 6, Tailwind CSS 3.4, KynexOne auth/company/feature/navigation contexts, Playwright unit/browser tests, and axe-core. No dependency upgrades.

**Spec:** `docs/superpowers/specs/2026-10-18-setup-studio-guided-wizard-retention-amendment.md` together with the non-superseded Configuration Center requirements in `docs/superpowers/specs/2026-10-18-setup-studio-guided-operating-blueprint-design.md`.

## Global Constraints

- Preserve the approved Guided Setup screen: its five-step rail, form layout, fields, copy hierarchy, policy entry, draft controls, Back/Continue flow, review, and supported Apply behavior.
- Existing `/setup?tab=aiSetup` and `mode=starter` links continue to reach Guided Setup; do not force a new landing page before it.
- Add one clear switch to Configuration Center outside the wizard card; switching paths uses the existing dirty/pending-file guard.
- Configuration Center uses the same twelve areas, canonical forms, current records, validation, permissions, feature checks, and server authorization.
- Only one Configuration Center editor may mount/fetch at a time. Restricted, unavailable, and unknown destinations never mount their editor.
- The complete destination row/node is the action; remove the detached generic far-right `Open settings` action.
- Selecting a setting replaces the area canvas with a full-width focused form and puts Back/previous/next actions near the heading and after long forms.
- Only `Employee Reference Lists` is an approved terminology change. Do not rename other destinations or modules.
- Preserve direct setting tabs and operational routes as compatibility paths.
- Configuration Center Save uses each canonical form's current API. It never invokes generic Setup Apply.
- Do not change Guided draft keys/schema, source lifecycle, policy publication, or batch Apply in this package.
- Desktop, 390 px, 320 CSS px, 200% zoom, 400% reflow, keyboard-only, English, Arabic, and RTL must keep all actions reachable without horizontal page overflow.
- Preserve all pre-existing dirty/untracked work in `fix/policy-binder-draft`. Record file-specific baselines and use hunk-scoped staging for files already dirty; inspect `git diff --cached` before every commit.
- No merge, deployment, feature enablement, policy publication, or production/Evostel mutation is authorized.

## Review Focus

1. An active Guided draft has unsaved values or an unuploaded file when the user switches to Configuration Center: cancellation must preserve the wizard, URL, focus, and values; confirmation navigates once (Tasks 1 and 2).
2. Permission or feature availability changes while a canonical editor is focused: it must unmount and stop protected reads, showing restricted/unknown truth rather than an empty form (Tasks 3 and 4).
3. A tenant-shared setting is opened from a company browsing context: the focused header must say `Applies to all companies` and never imply or send an unsupported company override (Tasks 3 and 4).
4. A user opens a long setting on Arabic mobile or at 400% reflow: Back and next/previous navigation remain reachable without two-axis scrolling or detached actions (Tasks 4 and 5).
5. Browser Back/Forward follows Guided → Configuration area → focused setting → area while a dirty form can block departure: history, scroll, focus, and guard replay must remain deterministic (Tasks 2, 4, and 5).

---

## File structure and ownership

### Create

- `frontend/src/lib/setupPathState.ts` — pure Guided/Configuration path resolution and compatibility serialization; no draft/task state.
- `frontend/unit/setupPathState.spec.ts` — route precedence, compatibility, scope, and preserved-parameter tests.
- `frontend/src/components/setup/SetupPathSwitch.tsx` — compact Guided Setup / Configuration Center switch outside the retained wizard card.
- `frontend/src/components/setup/SetupAreaBlueprint.tsx` — grouped whole-row destination canvas.
- `frontend/src/components/setup/SetupFocusedWorkspace.tsx` — one full-width canonical editor, scope impact, Back, and previous/next navigation.
- `frontend/src/components/setup/SetupAllAreasDialog.tsx` — large desktop area directory and full-screen mobile navigator with focus containment/restoration.

### Modify

- `frontend/src/views/SetupPage.tsx` — sole path/composition owner; retains `AiSetupAssistant` unchanged for Guided and mounts Configuration Center exclusively.
- `frontend/src/components/setup/SetupStudioWorkspace.tsx` — Configuration Center controller using the new area/focused components.
- `frontend/src/components/setup/SetupAreaNavigator.tsx` — delegate mobile/desktop browse-all behavior to the accessible dialog or retire its old inline disclosure.
- `frontend/src/lib/setupAreas.ts` — add stable business-group/order, effective-scope display metadata, and keep existing access rules/routes.
- `frontend/unit/setupAreas.spec.ts` — group/order/scope and unchanged destination coverage.
- `frontend/src/hooks/useSetupCompanyQuery.ts` — fail-closed path/scope reconciliation without changing Guided draft ownership.
- `frontend/src/i18n/setupStudio.ts`, `frontend/src/i18n/translations.ts` — path switch, grouping, scope, navigation, and recovery copy.
- `frontend/e2e/setup-studio-workspace.spec.ts` — Configuration Center, canonical form, history/focus, responsive/RTL, and security behavior.
- `frontend/e2e/setup-lifecycle.spec.ts`, `frontend/e2e/policy-form-prefill.spec.ts`, `frontend/e2e/policy-binder.spec.ts`, `frontend/e2e/policy-review.spec.ts` — lock the approved Guided screen and behavior.
- `docs/SETUP_STUDIO_WORKSPACE_VERIFICATION.md` — fresh evidence and explicit non-claims.

One integration owner edits `SetupPage.tsx`, `SetupStudioWorkspace.tsx`, and `setupAreas.ts`. No concurrent edits to these files.

### Task 1: Lock the approved Guided Setup screen with regression tests

**Files:**
- Modify: `frontend/e2e/setup-lifecycle.spec.ts`
- Modify: `frontend/e2e/policy-form-prefill.spec.ts`
- Modify: `frontend/e2e/policy-binder.spec.ts`
- Modify: `frontend/e2e/policy-review.spec.ts`

**Interfaces:**
- Consumes: current `AiSetupAssistant`, `SetupPendingFilesState`, current local draft key/schema, policy/source components, `setupAssistantApi.preview/apply`.
- Produces: a locked browser-test contract; no runtime interface.

- [ ] **Step 1: Add Guided visual/behavior contract tests**

Add `guided_setup_keeps_five_named_steps`, `guided_setup_keeps_company_scope_and_change_company`, `guided_setup_keeps_save_policy_and_cancel_controls`, `guided_setup_keeps_policy_upload_entry`, `guided_setup_continue_reaches_existing_review`, and `guided_setup_apply_remains_explicit`. Assert the five exact step names, current major controls, and no automatic mutation during navigation.

- [ ] **Step 2: Add draft/pending-file preservation tests using existing Guided exits**

Add `guided_policy_library_cancel_preserves_values_and_focus`, `guided_policy_library_confirm_discards_once`, and `guided_unuploaded_file_blocks_existing_exit`. Seed the current draft schema; do not introduce a new schema.

- [ ] **Step 3: Run the four Guided suites**

Run separately:

```bash
cd frontend
npx playwright test -c e2e/playwright.setup-lifecycle.config.ts
npx playwright test -c e2e/playwright.policy-form-prefill.config.ts
npx playwright test -c e2e/playwright.policy-binder.config.ts
npx playwright test -c e2e/playwright.policy-review.config.ts
```

Expected: PASS. These tests lock the owner-approved baseline before any composition change.

- [ ] **Step 4: Commit Task 1 tests**

Stage only Task 1 hunks, inspect `git diff --cached`, then commit:

```bash
git commit -m "test: lock approved Guided Setup experience"
```

### Task 2: Add minimal Guided/Configuration route ownership

**Files:**
- Create: `frontend/src/lib/setupPathState.ts`
- Create: `frontend/unit/setupPathState.spec.ts`
- Create: `frontend/src/components/setup/SetupPathSwitch.tsx`
- Modify: `frontend/src/views/SetupPage.tsx`
- Modify: `frontend/src/hooks/useSetupCompanyQuery.ts`
- Modify: `frontend/src/i18n/setupStudio.ts`
- Modify: `frontend/src/i18n/translations.ts`
- Modify: `frontend/e2e/setup-studio-workspace.spec.ts`

**Interfaces:**
- Consumes: raw `URLSearchParams`, authorized company IDs, selected company, `SetupNavigationGuard`.
- Produces:
  - `type SetupStudioPath = 'guided' | 'configuration'`
  - `type SetupPathResolution = { path: SetupStudioPath; area: SetupAreaId | 'overview'; setting: string | null; companyId: string | null; needsReplace: boolean; notice?: 'unauthorized-company' | 'setting-mismatch' }`
  - `resolveSetupPath(params: URLSearchParams, context: { authorizedCompanyIds: ReadonlySet<string>; selectedCompanyId: string | null }): SetupPathResolution`
  - `serializeSetupPath(state: SetupPathResolution, preserve: URLSearchParams): URLSearchParams`
  - `SetupPathSwitch({ active, onSelect }: { active: SetupStudioPath; onSelect: (path: SetupStudioPath) => void })`

- [ ] **Step 1: Write failing route-unit tests**

Add `ai_setup_defaults_to_guided`, `mode_starter_remains_guided`, `path_configuration_opens_configuration`, `area_or_setting_without_path_is_configuration_compatibility`, `setting_must_belong_to_area`, `unauthorized_company_fails_closed`, and `serializer_preserves_department_level_and_setup_area`.

Also add browser tests `guided_path_switch_cancel_preserves_values_and_focus`, `guided_path_switch_confirm_discards_once`, and `guided_unuploaded_file_blocks_configuration_switch` to `setup-studio-workspace.spec.ts`.

- [ ] **Step 2: Run unit tests and verify failure**

Run: `cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupPathState.spec.ts`

Then run: `cd frontend && npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts --project=desktop -g "path switch|defaults to guided"`

Expected: unit tests FAIL because the route module does not exist; browser tests FAIL because the guarded path switch does not exist.

- [ ] **Step 3: Implement the pure path resolver/serializer**

Default `tab=aiSetup` and `mode=starter` to Guided. Resolve explicit `path=configuration` and legacy `area`/`setting` links to Configuration. Validate area/setting against `SETUP_AREAS`; compatibility cleanup uses `replace`. Preserve `companyId`, `department`, `level`, and `setupArea`; never place form/draft content in the URL.

- [ ] **Step 4: Add the compact guarded path switch**

Render it outside the Guided form card so the screenshot's card layout remains unchanged. On switch, call `SetupNavigationGuard` with the intended URL/action. Cancel retains URL, focus, and wizard state; confirm executes once.

- [ ] **Step 5: Make `SetupPage` mount one path only**

Guided mounts the current `AiSetupAssistant` with its current props and pending-file provider. Configuration mounts `SetupStudioWorkspace`. Do not hide/retain the inactive path; current draft persistence plus the guard remain the preservation mechanism.

- [ ] **Step 6: Run unit, Guided, workspace, and type checks**

Run separately:

```bash
cd frontend
npx playwright test -c playwright.unit.config.ts unit/setupPathState.spec.ts
npx playwright test -c e2e/playwright.setup-lifecycle.config.ts
npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts --project=desktop -g "path|Guided|Configuration"
npx tsc --noEmit
```

Expected: PASS; switching away does not mutate configuration or publish policy.

- [ ] **Step 7: Commit Task 2**

Stage only Task 2 hunks, inspect `git diff --cached`, then commit:

```bash
git commit -m "feat: separate Guided Setup and Configuration Center"
```

### Task 3: Group the twelve-area Configuration catalogue truthfully

**Files:**
- Modify: `frontend/src/lib/setupAreas.ts`
- Modify: `frontend/unit/setupAreas.spec.ts`

**Interfaces:**
- Consumes: existing `navigationItems`, current permission/role/feature/scope checks, and all current destinations.
- Produces additions to `SetupDestination`: `businessGroup: { id: string; label: string }`, `groupOrder: number`, `destinationOrder: number`, and `effectiveTargetScope: 'company' | 'tenant' | 'recordDependent'`.

- [ ] **Step 1: Write failing catalogue tests**

Add `catalogue_keeps_exactly_twelve_areas_and_all_existing_destinations`, `every_destination_has_stable_group_and_order`, `employee_reference_lists_is_the_only_renamed_label`, `tenant_shared_destinations_have_tenant_effective_scope`, and `operations_remain_external_routes`.

- [ ] **Step 2: Run tests and verify failure**

Run: `cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupAreas.spec.ts`

Expected: FAIL for missing group/order/effective-scope fields.

- [ ] **Step 3: Add metadata without changing authorization**

Group destinations by stable business purpose within their owning area. Preserve all paths, permissions, roles, feature keys, scopes, presentation types, and current availability rules. Metadata describes navigation only; it grants no access and claims no configuration completeness.

- [ ] **Step 4: Run unit tests and typecheck**

Run: `cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupAreas.spec.ts`

Then run: `cd frontend && npx tsc --noEmit`

Expected: PASS.

- [ ] **Step 5: Commit Task 3**

Stage only Task 3 hunks, inspect `git diff --cached`, then commit:

```bash
git commit -m "feat: group Configuration Center destinations"
```

### Task 4: Build the Configuration Center area and focused-form experience

**Files:**
- Create: `frontend/src/components/setup/SetupAreaBlueprint.tsx`
- Create: `frontend/src/components/setup/SetupFocusedWorkspace.tsx`
- Create: `frontend/src/components/setup/SetupAllAreasDialog.tsx`
- Modify: `frontend/src/components/setup/SetupStudioWorkspace.tsx`
- Modify: `frontend/src/components/setup/SetupAreaNavigator.tsx`
- Modify: `frontend/src/i18n/setupStudio.ts`
- Modify: `frontend/e2e/setup-studio-workspace.spec.ts`

**Interfaces:**
- Consumes: enriched `SETUP_AREAS`, `destinationAvailability`, existing `renderSettings`, `SetupNavigationGuard`, auth/company/feature contexts.
- Produces:
  - `SetupAreaBlueprint({ area, destinations, onOpenDestination })`
  - `SetupFocusedWorkspace({ destination, scopeLabel, onBack, onPrevious?, onNext?, children })`
  - `SetupAllAreasDialog({ open, activeArea, onSelect, onClose, triggerRef })`
  - `SetupStudioWorkspace` renders either one grouped area canvas or one focused canonical editor.

- [ ] **Step 1: Write failing whole-row/focused-form tests**

Replace old `Open settings` clicks with destination-labelled row actions. Add `whole_destination_row_is_action`, `focused_form_replaces_area_canvas`, `focused_form_opens_at_heading_without_manual_scroll`, `back_restores_origin_focus_and_scroll`, and `previous_next_are_available_above_and_below_long_form`.

- [ ] **Step 2: Write failing access/scope tests**

Add `restricted_unknown_or_disabled_destination_never_mounts_editor`, `permission_loss_unmounts_focused_editor_and_stops_reads`, `tenant_setting_says_applies_to_all_companies`, and retain canonical PUT, record identity, failed-save-value, and no-generic-Apply assertions.

- [ ] **Step 3: Write failing navigator tests**

Add `browse_all_areas_is_large_desktop_dialog`, `mobile_area_navigator_is_full_screen`, `navigator_contains_focus_closes_with_escape_and_restores_trigger`, and `area_selection_focuses_area_heading`.

- [ ] **Step 4: Run focused workspace tests and verify failure**

Run: `cd frontend && npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts -g "destination|focused|permission|tenant|Browse|navigator"`

Expected: FAIL against the current stacked editor/list and detached-button layout.

- [ ] **Step 5: Implement grouped whole-row canvases**

Render registry groups in stable order. The available row/node itself is the labelled button/link; restricted/unavailable rows remain non-actionable and explain why. Do not render a generic far-edge `Open settings` button.

- [ ] **Step 6: Implement the single full-width canonical editor**

When an available embedded setting is selected, replace the area canvas and mount only `renderSettings(destination.id)`. Put destination-specific heading, scope impact, Back, and previous/next controls adjacent to the form and after long content. Preserve each form's existing Save/API/validation.

- [ ] **Step 7: Implement accessible all-area navigation**

Desktop uses a large labelled dialog; mobile uses a full-screen dialog. Focus its heading on open, contain focus, close on explicit Close/Escape when no dirty decision is pending, restore trigger focus, and focus the selected area heading.

- [ ] **Step 8: Run workspace tests and typecheck**

Run: `cd frontend && npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts`

Then run: `cd frontend && npx tsc --noEmit`

Expected: PASS; Configuration navigation issues no mutations and only the selected editor fetches.

- [ ] **Step 9: Commit Task 4**

Stage only Task 4 hunks, inspect `git diff --cached`, then commit:

```bash
git commit -m "feat: redesign Configuration Center navigation"
```

### Task 5: Prove Guided preservation and responsive Configuration acceptance

**Files:**
- Modify: `frontend/e2e/setup-studio-workspace.spec.ts`
- Modify: `frontend/e2e/setup-experience.spec.ts`
- Modify: `frontend/src/i18n/setupStudio.ts`
- Modify: `frontend/src/styles/index.css` only for defects proved by these tests.
- Create or modify: `docs/SETUP_STUDIO_WORKSPACE_VERIFICATION.md`

**Interfaces:**
- Consumes: retained Guided path, completed Configuration Center, existing Playwright configs/fixtures, axe-core.
- Produces: acceptance evidence only; no new runtime API.

- [ ] **Step 1: Add failing cross-cutting acceptance tests**

Add `guided_visual_contract_survives_configuration_redesign`, `english_and_arabic_paths_have_no_serious_or_critical_axe_findings`, `mobile_390_and_320_have_no_page_overflow`, `rtl_long_labels_keep_navigation_reachable`, `two_hundred_percent_zoom_keeps_actions_reachable`, `four_hundred_percent_reflow_has_no_two_axis_scroll`, `dirty_cancel_preserves_url_values_scroll_and_focus`, and `dirty_confirm_navigates_once`.

- [ ] **Step 2: Run acceptance tests before fixes**

Run: `cd frontend && npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts`

Expected: any remaining translation, focus, responsive, or overflow defect FAILS with a reproducible trace/screenshot.

- [ ] **Step 3: Fix only demonstrated cross-cutting defects**

Prefer existing Tailwind tokens and logical properties. If CSS is necessary, scope it to Configuration Center. Do not restyle the approved Guided card/step rail except to repair a demonstrated regression introduced by this package.

- [ ] **Step 4: Run all affected gates**

Run separately:

```bash
cd frontend
npx playwright test -c playwright.unit.config.ts unit/setupPathState.spec.ts unit/setupAreas.spec.ts unit/setupCanonicalForms.spec.ts
npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts
npx playwright test -c e2e/playwright.setup-lifecycle.config.ts
npx playwright test -c e2e/playwright.setup-experience.config.ts
npx playwright test -c e2e/playwright.policy-form-prefill.config.ts
npx playwright test -c e2e/playwright.policy-binder.config.ts
npx playwright test -c e2e/playwright.policy-review.config.ts
npx tsc --noEmit
NEXT_PUBLIC_API_BASE_URL=http://127.0.0.1:5127 npm run build
```

Expected: PASS. Record fixture evidence separately from authenticated backend security evidence.

- [ ] **Step 5: Perform manual keyboard/zoom/RTL verification**

Verify Guided remains visually/behaviorally consistent with the owner-approved screenshot. Verify Configuration path switch, all twelve areas, focused setting, Back/previous/next, dirty cancellation, desktop/mobile area dialogs, English/Arabic, 320/390 widths, 200% zoom, and 400% reflow.

- [ ] **Step 6: Validate local shadow-tenant behavior read-only first**

Confirm signed-in persona, tenant/company scope, frontend/backend/build identity, and populated canonical records before any dedicated test-record write. Reopen one saved Configuration Center record; do not mutate production or existing client records.

- [ ] **Step 7: Record evidence and request whole-diff review**

Document commands/results, screenshots including the preserved Guided view, runtime provenance, saved/read-back behavior, accessibility findings, and remaining gaps. Reviewer checks Guided preservation, route/scope fail-closed behavior, canonical form identity, exclusive mounting, dirty navigation, and no backend/production expansion.

- [ ] **Step 8: Commit Task 5**

Stage only Task 5 hunks, inspect `git diff --cached`, then commit:

```bash
git commit -m "test: verify Configuration Center redesign"
```

## Self-review outcome

- **Spec coverage:** The plan implements the latest owner decision: preserve Guided Setup and redesign only Configuration Center. It covers all twelve areas, whole-row actions, focused forms, route compatibility, permissions/scope, history/focus, responsive/RTL/accessibility, and local verification.
- **Step scan:** Every runtime task begins with a named failing test, records the expected failure, implements one bounded interface, reruns focused gates, and ends with safe hunk-scoped commit instructions.
- **Type consistency:** `SetupStudioPath`, `SetupPathResolution`, the registry grouping fields, and the three Configuration components are introduced once and consumed under the same names.
- **Review Focus:** All five high-risk cases map to named tests: guarded wizard departure, permission/feature loss, tenant scope, Arabic/reflow navigation, and dirty Back/Forward behavior.
- **Proportion:** The plan omits the previously proposed Guided draft migration/status/controller rewrite. It introduces no backend or form duplication and leaves later adapter/change-set work outside this package.
