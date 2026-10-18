# SDD ledger — plan: docs/superpowers/plans/2026-10-18-setup-configuration-center.md

Workspace: /Users/zackkhan/Downloads/KynexOne-wt/policy-binder-fix
Branch: fix/policy-binder-draft
Plan start HEAD: cef8dbf08bbb1907535ae99178cb6b7222b2f825
Isolation: existing linked worktree verified; no nested worktree created.

## Pre-flight scan

| Scope | Produces / consumes | Finding |
| --- | --- | --- |
| Task 1 internal | Guided regression tests against existing `AiSetupAssistant`, draft, policy, preview, and Apply contracts | Consistent: this task locks existing behavior and makes no production change. |
| Task 2 internal | `setupPathState.ts`, `SetupPathSwitch.tsx`, and exclusive path composition in `SetupPage.tsx` | Consistent: default Guided route is an intentional owner-approved change; path-switch tests must fail before implementation. |
| Task 3 internal | Adds navigation-only group/order/effective-scope metadata to existing registry | Consistent: no authorization or route contract may change. |
| Task 4 internal | Uses registry metadata to render whole-row area canvases, one focused canonical editor, and responsive navigator | Consistent: canonical renderer and save APIs remain unchanged. |
| Task 5 internal | Cross-cutting acceptance and evidence; CSS only for demonstrated Configuration defects | Consistent: Guided styling is explicitly protected. |
| Tasks 1 → 2 | Task 1 locks Guided behavior; Task 2 re-composes it behind the path switch | Compatible if `AiSetupAssistant` props and internal markup remain unchanged. |
| Tasks 2 → 3 | Route resolver validates `SetupAreaId`/destination ownership; Task 3 enriches metadata | Compatible: Task 3 must preserve IDs and paths used by Task 2. |
| Tasks 2 → 4 | Task 2 owns path/area/setting URLs; Task 4 consumes callbacks in Configuration workspace | Compatible: `SetupPage` remains sole router owner. |
| Tasks 2 → 5 | Both touch `setup-studio-workspace.spec.ts` and i18n | Compatible with hunk-scoped staging; Task 5 extends, not rewrites, Task 2 coverage. |
| Tasks 3 → 4 | Task 3 produces group/order/effective-scope metadata consumed by Task 4 | Compatible and load-bearing. |
| Tasks 4 → 5 | Task 4 produces rendered behavior; Task 5 adds responsive/RTL/axe evidence | Compatible; Task 5 may fix only test-proven defects. |

Ruling: The user-supplied screenshot is the binding visual contract for Guided Setup, while the C+B redesign applies only to Configuration Center — changing Guided layout would violate the latest owner decision — cost if wrong: Configuration Center will improve without applying the new spatial treatment to the wizard.

Ruling: Existing uncommitted files are user work and remain in place; task commits use hunk-scoped staging and may defer a commit when hunks cannot be separated safely — cost if wrong: some completed implementation may remain uncommitted until provenance is resolved, but no user work is overwritten or silently absorbed.

Baseline: `npx playwright test -c playwright.unit.config.ts unit/setupAreas.spec.ts unit/setupDraftLifecycle.spec.ts` — 24 passed, 0 failed. Frontend-local `node_modules` is absent, but the repository/toolchain resolves Playwright successfully from the existing environment.

Task 1 Ruling: The four named Guided suites are pre-existing untracked user files, so Task 1 will add its visual/behavior lock to the tracked `frontend/e2e/setup-studio-workspace.spec.ts` fixture and run the four suites as regression evidence without editing or staging them — this preserves provenance while testing the same rendered `AiSetupAssistant` — cost if wrong: Guided assertions live beside workspace tests instead of their semantically ideal suite.

Task 1 dispatch attempt 1: infrastructure failure before work began — `glm-5.3:cloud` returned provider weekly rate limit. No files or commits produced. Re-dispatching with a different available model.

Task 1 dispatch attempt 2: infrastructure failure before work began — `kimi-k3:cloud` returned the same provider weekly rate limit. No files or commits produced. Re-dispatching with another provider model.

Task 1 dispatch attempt 3: infrastructure failure before work began — `deepseek-v4.1-flash:cloud` returned the same provider weekly rate limit. No files or commits produced.

Task 1 Ruling: All selectable fresh-worker cloud models are unavailable due the shared provider billing cap, so reuse the completed `/root/ux_flow_review` agent for implementation rather than abandoning the owner-authorized build — task review will still use a separate agent if available — cost if wrong: the implementer is not context-fresh, though its requirements remain isolated in the brief/report files.

Task 1 implementation: commit `9222cf80` added nine isolated Guided contract tests; desktop 9/9 and mobile 9/9 pass. Broader regression evidence: setup-lifecycle 0/10, policy-form-prefill 21/22, policy-binder 6/6, policy-review 0/8. Review verdict: spec issues, task quality needs fixes.

Task 1 Ruling: The reviewer confirmed the failing direct `mode=starter` company reconciliation is a binding Guided-preservation defect. Although Task 1 was planned as tests-only and Task 2 later owns route composition, the owner-approved screenshot and compatibility contract are authoritative; fix the smallest tracked production root cause now, without editing untracked fixtures — cost if wrong: Task 1 grows from characterization into a narrow direct-entry bug fix and Task 2 must treat that hook behavior as an established interface.

Task 1 fix round 1/5 started: open findings are direct Guided entry/company reconciliation plus red required suites; missing intermediate-step/Back preservation; missing exactly-once confirmation assertion; substring rather than exact step-name matching.

Task 1 fix round 1 complete: commit `dc2d43df` resolves direct Guided company-scope reconciliation and closes every review finding. Verification: Guided desktop/mobile 20/20, setup lifecycle 10/10, policy form prefill 22/22, policy binder 6/6, policy review 8/8, and TypeScript passed. Independent re-review verdict: spec compliant, no critical/important/minor issues, Approved.

Task 2 RED: `unit/setupPathState.spec.ts` failed because `src/lib/setupPathState` does not exist; the three path-switch browser contracts failed because `Guided Setup` does not exist; the direct default browser contract failed because bare `tab=aiSetup` still mounted Configuration.

Task 2 Ruling: the plan's browser grep text `defaults to guided` does not match its required underscore test name `ai_setup_defaults_to_guided`; keep the exact required name and run it through an additional exact `-g ai_setup_defaults_to_guided` command — cost if wrong: Task 2 has one extra focused RED/GREEN command but no product behavior changes.
Task 2: complete (commits dc2d43d..411f517, tests: bash -lc 'cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupPathState.spec.ts && npx playwright test -c e2e/playwright.setup-lifecycle.config.ts && npx playwright test -c e2e/playwright.setup-studio-workspace.config.ts --project=desktop -g "path|Guided|Configuration" && npx tsc --noEmit' →   17 passed (31.9s))

Task 2 review: needs fixes. Fix round 1/5 started for (1) committed Guided return-to-Configuration callback integration rather than dependence on unstaged work, (2) fail-closed unauthorized company handling that does not broaden into all-company Configuration or mount/fetch editors, and (3) clean-index acceptance evidence for lifecycle plus retained Guided/policy suites.

Task 2 fix round 1 complete: commit `4982fe15` wires Guided return-to-Configuration and blocks forged company scope without mounting/fetching Configuration editors. Clean-index focused contracts 3/3 and route units 7/7 pass. Broader clean-index failures were independently adjudicated as pre-existing user-owned baseline gaps rather than Task 2 regressions. Re-review verdict: compliant, no findings, Approved.

Task 3 complete: commit `37ad68ad` adds truthful business grouping, stable order, and effective target-scope metadata while retaining exactly 12 areas and 45 destination IDs/routes. Verification: 19/19 catalogue tests and TypeScript pass. Independent review: compliant, no findings, Approved.

Task 2 fix round 1 implementation complete: staged-only contracts pass 3/3 and route unit tests pass 7/7 from an independently served `git checkout-index` export. Unauthorized explicit company scope now stays blocked until an authorized recovery choice; Guided return is committed and pending files remain guarded. Clean-index broad-suite runs also proved that the prior all-green claim depended on unstaged user work: lifecycle 0/10, Guided selection 20/32, prefill 0/22, binder 0/6, review 0/8, and TypeScript has only the known unrelated `workEmailDomainProblem` index mismatch. Those user-owned baseline gaps were documented and left unstaged.

Task 2 fix round 1 committed as `4982fe15` (`fix: fail closed Setup path scope`); the commit contains exactly five Task 2 files.

Task 3 RED: the five exact catalogue contracts produced the expected missing-metadata failure — 17 passed, while stable business grouping/order and effective target scope failed because those fields were absent. The existing destination/path, approved rename, and operations-route characterizations passed.
Task 3: complete (commits 4982fe1..37ad68a, tests: bash -lc 'cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupAreas.spec.ts && npx tsc --noEmit' →   19 passed (458ms))

Task 4 RED: the existing detached `Open settings` and stacked editor/canvas implementation failed the new whole-row and exclusive-focused workspace contracts. The captured partial focused run ended 3 passed, 5 failed, 1 interrupted, with the required whole-row/focused assertions failing for absent new semantics.

Task 4 implementation: grouped full-row area canvases, an exclusive single canonical editor workspace, truthful effective-scope copy, Back plus previous/next controls, focus/scroll restoration, permission-loss unmounting, and the large-desktop/fullscreen-mobile all-area navigator are implemented. Focused Task 4 plus canonical PUT/identity/failed-save/axe evidence is 32 passed and 2 project-specific skips; TypeScript passes. The full dirty-worktree suite is 136 passed, 2 skipped, 52 failed, with all Task 4 exact contracts green and remaining failures confined to pre-existing dirty onboarding/Guided/direct-GL guard expectations documented in the Task 4 report.

Task 4 staged snapshot: exact Task 4 desktop/mobile contracts pass 22/22 with 2 project-specific skips from a `git checkout-index` export, independent of unstaged user work. The staged-only TypeScript run reaches only the already documented unrelated committed-index `workEmailDomainProblem` test/source mismatch; live-worktree TypeScript is green.
