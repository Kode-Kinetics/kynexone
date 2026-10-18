# Task 5 report — Configuration Center final acceptance

Date: 2026-10-18

## Outcome

Implemented all eight named cross-cutting acceptance contracts and fixed one demonstrated Configuration-only defect. The mobile all-area navigator is now portaled to the document body so the sticky application header cannot obscure or intercept its title and Close control. Guided Setup production styling and composition were not changed.

## TDD evidence

Initial focused run: 6 passed, 2 failed.

- Real defect: `mobile_390_and_320_have_no_page_overflow` could see the navigator but could not click Close because the shell header intercepted pointer events.
- Test defect: the Arabic browse selector matched multiple dialog triggers and was changed to the exact localized accessible name.

After the bounded fix:

```text
npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts \
  -g 'guided_visual_contract_survives_configuration_redesign|english_and_arabic_paths_have_no_serious_or_critical_axe_findings|mobile_390_and_320_have_no_page_overflow|rtl_long_labels_keep_navigation_reachable|two_hundred_percent_zoom_keeps_actions_reachable|four_hundred_percent_reflow_has_no_two_axis_scroll|dirty_cancel_preserves_url_values_scroll_and_focus|dirty_confirm_navigates_once'
16 passed
```

## Required gates

| Gate | Result |
| --- | --- |
| setupPathState/setupAreas/setupCanonicalForms | 27 passed |
| exact Task 5 desktop/mobile | 16 passed |
| setup lifecycle | 10 passed |
| setup experience | 61 passed, 3 skipped, 2 pre-existing Guided compactness failures |
| policy form prefill | 13 passed, 9 stale untracked workspace-entry failures |
| policy binder | 6 passed |
| policy review | 8 passed |
| live-worktree `tsc --noEmit` | passed after build |
| live-worktree production build | passed |
| broad setup workspace | 185 passed, 3 skipped, 48 pre-existing dirty-suite failures; all Task 5 tests passed |

The first TypeScript invocation ran concurrently with `next build` and saw `.next/types` files being regenerated. It was rerun after the build and passed. This was a command-order race, not a source defect.

## Index-only proof

A fresh `git checkout-index` export passed 27/27 units, 14/16 Task 5 cases, and the production build. Both index-only Task 5 failures were the Guided contract's missing `Save for later` control, demonstrating the already documented dependency on user-owned, unstaged Guided toolbar work. Index-only TypeScript reached the unrelated existing `unit/employeeAccess.spec.ts` import of missing `workEmailDomainProblem`. No user-owned hunk was staged to hide either baseline gap.

## Responsive, RTL, keyboard, and accessibility evidence

The exact tests cover keyboard focus restoration, dialog focus containment/Escape, dirty cancel/confirm, English and Arabic Axe analysis, 320/390 widths, and the 720/320 CSS-pixel equivalents used for 200%/400% reflow. Stable screenshots are in `docs/evidence/setup-studio-task5/`.

## Authenticated read-only validation

The local frontend was restarted at 5197 with API target 5127. The controller verified Evostel Shadow Administrator/Admin in Evostel Policy Shadow — Local, company ID `009f78a6-91ee-4a86-b7a8-28caa07e0e64`.

- Guided showed all five steps and the approved first-step controls.
- Configuration Leave showed whole-row Leave types, Leave policies, and Holiday calendars.
- Focused Leave policies reopened 14 populated Active policies with canonical New/Edit/Archive actions and top/bottom navigation.
- No save, apply, archive, publish, or other tenant mutation occurred.

Frontend build info reports commit `unknown`. Backend `/health/live` is HTTP 200 and reports commit `2e992b3da05dc3f923376638d0e205b7de0132cd`; source HEAD before this commit was `a6c48b080cc170acec5b311f617ae0fb43f72096`. Matching source/runtime provenance is therefore not claimed.

## Remaining gates

- Reconcile the user-owned unstaged Guided toolbar/integration into a separately reviewed commit so a clean checkout can pass the full Guided acceptance contract.
- Update or retire the 48 stale dirty workspace expectations and the 9 untracked prefill workspace-entry expectations under their owning workstream.
- Resolve the unrelated employee-access test/source export mismatch in the committed baseline.
- This task does not authorize deployment or claim production acceptance, exhaustive server authorization, or a new live persistence write.
