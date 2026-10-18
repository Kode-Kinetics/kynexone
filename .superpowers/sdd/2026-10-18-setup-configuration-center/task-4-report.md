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

## Fix round 1

Review found four Important gaps and one Minor evidence gap in commit `3a0b2ff7`. The remediation adds deterministic origin restoration for explicit Back and browser Back/Forward after previous/next navigation, keeps the all-area navigator available beside the focused editor, gives focused navigation and dialog controls 44px minimum targets, makes the Setup Studio Arabic catalogue own the approved `Employee Reference Lists` rename, and proves live permission loss through an authoritative `/api/auth/me` refresh without reloading the page.

RED evidence:

```bash
cd frontend
npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts --project=desktop -g "back_after_next_restores_current_row_focus_and_recorded_scroll|browser_back_forward_restores_matching_row_focus_and_scroll|permission_loss_unmounts_focused_editor_and_stops_reads|focused_workspace_keeps_all_area_navigator_without_extra_editor|arabic_uses_employee_reference_lists_rename"
npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts --project=mobile -g "mobile_focused_navigation_controls_are_44px_touch_targets"
```

- Desktop: the four new behavioral contracts failed for missing current-row restoration, live auth refresh, and focused navigator access; the Arabic rendering assertion was already green because the broader setup catalogue also carried that translation.
- Mobile: the computed Back target height was 36px instead of the required 44px.

GREEN evidence:

```bash
cd frontend
npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts -g "whole_destination_row_is_action|focused_form_replaces_area_canvas|focused_form_opens_at_heading_without_manual_scroll|back_restores_origin_focus_and_scroll|back_after_previous_or_next_restores_current_row_focus_and_recorded_scroll|browser_back_forward_restores_matching_row_focus_and_scroll|previous_next_are_available_above_and_below_long_form|restricted_unknown_or_disabled_destination_never_mounts_editor|permission_loss_unmounts_focused_editor_and_stops_reads|tenant_setting_says_applies_to_all_companies|focused_workspace_keeps_all_area_navigator_without_extra_editor|browse_all_areas_is_large_desktop_dialog|mobile_area_navigator_is_full_screen|mobile_focused_navigation_controls_are_44px_touch_targets|navigator_contains_focus_closes_with_escape_and_restores_trigger|area_selection_focuses_area_heading|arabic_uses_employee_reference_lists_rename"
```

- 31 passed with 3 intentional desktop/mobile-specific skips.
- `npx tsc --noEmit`: exit 0 with no diagnostics.
- Relevant history/dirty subset: 6 passed and 10 known pre-existing dirty-guard failures. The passing contracts cover canonical-save guard clearing, URL Back area restoration, and absent-company history restoration. The failures are the previously documented stale onboarding/history tests: one leaves its confirmation modal open before trying a company switch, while the others expect native dialogs from the current custom navigation guard.
- The same exact Task 4 command ran from a fresh `git checkout-index` export: 31 passed and 3 intentional project-specific skips, proving the fix does not depend on unstaged user work.
- Staged-index TypeScript still reaches only the unrelated committed-index `workEmailDomainProblem` mismatch documented in Task 2/Task 4; live-worktree TypeScript remains green.

Fix commit message: `fix: complete Configuration navigation contracts`; final hash is reported in the controller handoff.
