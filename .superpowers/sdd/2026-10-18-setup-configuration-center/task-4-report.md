# Task 4 report — Configuration Center area and focused-form experience

## Status

Implemented the approved Configuration Center interaction model without changing Guided Setup. Each area now renders a grouped business-purpose canvas whose available destination row is the action. Opening an embedded destination replaces that canvas with exactly one canonical editor, destination-specific scope language, Back, and previous/next navigation above and below the form.

## Dirty baseline

- `frontend/src/components/setup/SetupStudioWorkspace.tsx` already contained one user-owned onboarding-description edit.
- `frontend/src/i18n/setupStudio.ts` already contained user-owned company-form Arabic strings and the Arabic `Employee Reference Lists` rename.
- `frontend/e2e/setup-studio-workspace.spec.ts` already contained user-owned company-actions, single-company, Arabic-suggestion, and company-limit fixture work.
- Those baseline hunks were preserved in the working tree and excluded from the Task 4 index.
- The configured Browser plugin named by the frontend testing skill was not available in this session, so repository Playwright was used for all browser evidence.

## RED evidence

Command:

```bash
cd frontend
npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts -g "destination|focused|permission|tenant|Browse|navigator"
```

The pre-implementation run failed on the expected Task 4 contracts. In the captured partial RED before interruption, `whole_destination_row_is_action` could not find a destination-labelled row button and `focused_form_replaces_area_canvas` could not find the focused workspace; the old tenant-scope and detached-action contracts were also red. Result at interruption: 3 passed, 5 failed, 1 interrupted, 21 not run.

## Implementation

- Added `SetupAreaBlueprint` to render registry business groups in stable group/destination order.
- Available embedded settings are whole-row buttons; external destinations are whole-row links; restricted, disabled, checking, and unknown destinations are explanatory non-actions.
- Added `SetupFocusedWorkspace` with a focus-targeted destination heading, truthful effective-target scope, Back, and previous/next controls before and after the canonical editor.
- Refactored `SetupStudioWorkspace` into exclusive blueprint/focused states. `renderSettings` is invoked only for the one currently available embedded destination; permission/feature loss replaces and unmounts the editor.
- Back restores the originating destination focus and scroll position; direct-entry Back returns to the area heading.
- Added `SetupAllAreasDialog` and simplified `SetupAreaNavigator` to a `Browse all areas` trigger. Desktop uses a large labelled modal; mobile uses the viewport; focus is contained; Escape/Close restore the trigger; accepted area selection focuses the new area heading.
- Added customer-facing company/tenant/record-dependent impact language and Arabic strings. Internal `tenant-wide` terminology is no longer the only administrator-facing explanation.
- Preserved canonical routes, form components, form-level Save/API/validation, feature/role checks, and the existing navigation guard.

## GREEN evidence

Focused Task 4 and retained canonical-save/accessibility command:

```bash
cd frontend
npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts -g "manual_leave_edit_uses_existing_put|failed_save_retains_values|existing_record_identity_survives_edit|workspace_axe|whole_destination_row_is_action|focused_form_replaces_area_canvas|focused_form_opens_at_heading_without_manual_scroll|back_restores_origin_focus_and_scroll|previous_next_are_available_above_and_below_long_form|restricted_unknown_or_disabled_destination_never_mounts_editor|permission_loss_unmounts_focused_editor_and_stops_reads|tenant_setting_says_applies_to_all_companies|browse_all_areas_is_large_desktop_dialog|mobile_area_navigator_is_full_screen|navigator_contains_focus_closes_with_escape_and_restores_trigger|area_selection_focuses_area_heading"
```

- 32 passed across desktop/mobile.
- 2 intentional project-specific skips: desktop-only dialog sizing on mobile and mobile-only fullscreen sizing on desktop.
- Canonical leave PUT, failed-save value retention, record identity, English/Arabic axe, whole-row/focused/access/scope/dialog/focus contracts all passed.

TypeScript:

```bash
cd frontend
npx tsc --noEmit
```

- Exit 0, no diagnostics.

Staged/index-only Task 4 contract run:

```bash
git checkout-index --all --prefix=/tmp/kynexone-task4-index.OYkLEv/
cd /tmp/kynexone-task4-index.OYkLEv/frontend
npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts -g "whole_destination_row_is_action|focused_form_replaces_area_canvas|focused_form_opens_at_heading_without_manual_scroll|back_restores_origin_focus_and_scroll|previous_next_are_available_above_and_below_long_form|restricted_unknown_or_disabled_destination_never_mounts_editor|permission_loss_unmounts_focused_editor_and_stops_reads|tenant_setting_says_applies_to_all_companies|browse_all_areas_is_large_desktop_dialog|mobile_area_navigator_is_full_screen|navigator_contains_focus_closes_with_escape_and_restores_trigger|area_selection_focuses_area_heading"
```

- 22 passed and 2 intentional project-specific skips in 42.0 seconds.
- This run used only `HEAD` plus the staged Task 4 index, proving the new behavior and tests do not depend on unstaged user work.
- Index-only `npx tsc --noEmit` reaches one pre-existing unrelated mismatch in `unit/employeeAccess.spec.ts`: `workEmailDomainProblem` is not exported by the committed `employeeAccess` source. The live worktree TypeScript gate is green because its user-owned employee-access source/test work remains together and unstaged.

## Full dirty-worktree suite

Command:

```bash
cd frontend
npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts
```

Result: 136 passed, 2 skipped, 52 failed in 11.5 minutes. All new Task 4 tests passed in this full run. The failures are outside the focused Task 4 acceptance set and repeat on both projects in pre-existing dirty contracts: configuration-only onboarding tests toggle the template form closed even though `configurationOnly` initializes it open; direct GL/company/history dirty-state expectations do not observe the current guard behavior; and Guided pending-file/history contracts depend on broader dirty user-owned Setup work. They were documented rather than expanded into unrelated production fixes.

## Commit

Prepared as the isolated `feat: redesign Configuration Center navigation` commit; the final hash is reported in the controller handoff.
