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

Review fix RED: the first Arabic acceptance used one page whose `boot` init script reset the locale to English on every navigation. Adding `html[dir="rtl"]` and Arabic anchor assertions reproduced the false positive: expected `rtl`, received `ltr`. The test now boots English and Arabic in separate pages with locale-specific init scripts, asserts `dir` plus localized Configuration and Guided anchors before Axe, captures runtime errors from both pages, and then runs Axe. The regenerated screenshots are no longer identical: English SHA-256 `8fe06ae09dc83bed3d3696329cb85f55cd2f23105255d8e9c69d3edd49683a9e`; Arabic `8c6092db8673dc1357bff8f03eacd4bb9bce8fb16fab3106c32126c947c73df2`.

## Required gates

| Gate | Result |
| --- | --- |
| setupPathState/setupAreas/setupCanonicalForms | 27 passed |
| exact Task 5 desktop/mobile | 16 passed |
| Arabic focused layout/auth/approved-rename cases | 6 passed |
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

A final fresh `git checkout-index` export passed 32/32 setup and draft-lifecycle units. Its combined desktop/mobile acceptance run passed 22/22: all 16 Task 5 cases, both Guided path-switch cases, and the Companies group-register scope contract. The approved Guided toolbar and browser-scoped draft lifecycle are now committed without changing the five-step layout. The Companies destination now carries explicit `group` target metadata and says `Group company register`, rather than implying that the register belongs to the currently selected company.

The clean-export TypeScript check reaches only the unrelated existing `unit/employeeAccess.spec.ts` import of missing `workEmailDomainProblem`; no Setup Studio type error remains.

## Responsive, RTL, keyboard, and accessibility evidence

The exact tests cover keyboard focus restoration, dialog focus containment/Escape, dirty cancel/confirm, separately booted English and Arabic Axe analysis, 320/390 widths, and the 720/320 CSS-pixel equivalents used for 200%/400% reflow. Stable screenshots are in `docs/evidence/setup-studio-task5/`.

The controller also performed the actual native Chrome gate on the authenticated tenant rather than relying only on viewport emulation:

- Baseline 100%: `innerWidth=1728`, `devicePixelRatio=2`, `clientWidth=scrollWidth=1724`.
- Native Chrome 200%: visible 200% zoom badge, `innerWidth=864`, `devicePixelRatio=4`, `clientWidth=scrollWidth=860`; no horizontal overflow. Shift+Tab reached Next, Previous, then Back. The all-area dialog exposed Workspace overview plus all 12 named areas.
- Native Chrome 400%: visible 400% zoom badge, `innerWidth=432`, `devicePixelRatio=8`, `clientWidth=scrollWidth=428`; no horizontal overflow. Return opened Browse all areas, Tab focused Close, Shift+Tab wrapped to Compliance and statutory administration, and Escape closed the dialog and restored focus to Browse all areas.
- Live Arabic at native 400%: `html lang="ar" dir="rtl"`, localized `الإعداد والإدارة`, `innerWidth=432`, `devicePixelRatio=8`, `clientWidth=scrollWidth=428`; localized Back/Previous/Next remained reachable.
- Chrome was restored to English/LTR/100% (`innerWidth=1728`, `devicePixelRatio=2`) and the focused Leave policies tab was left open.

## Authenticated read-only validation

The local frontend was restarted at 5197 with API target 5127. The controller verified Evostel Shadow Administrator/Admin in Evostel Policy Shadow — Local, company ID `009f78a6-91ee-4a86-b7a8-28caa07e0e64`.

- Guided showed all five steps and the approved first-step controls.
- Configuration Leave showed whole-row Leave types, Leave policies, and Holiday calendars.
- Focused Leave policies reopened 14 populated Active policies with canonical New/Edit/Archive actions and top/bottom navigation.
- No save, apply, archive, publish, or other tenant mutation occurred.

Frontend build info reports commit `unknown`. Backend `/health/live` is HTTP 200 and reports commit `2e992b3da05dc3f923376638d0e205b7de0132cd`; source HEAD before this commit was `a6c48b080cc170acec5b311f617ae0fb43f72096`. Matching source/runtime provenance is therefore not claimed.

## Remaining gates

- Update or retire the 48 stale dirty workspace expectations and the 9 untracked prefill workspace-entry expectations under their owning workstream.
- Resolve the unrelated employee-access test/source export mismatch in the committed baseline.
- This task does not authorize deployment or claim production acceptance, exhaustive server authorization, or a new live persistence write.
