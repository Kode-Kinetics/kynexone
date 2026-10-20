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

Task 4 initial implementation: commit `3a0b2ff7` adds grouped whole-row area canvases, focused canonical editors, and the responsive area dialog. Exact staged-index contracts passed 22 with 2 intentional project skips; review found no critical issue but requires fixes for browser-history/previous-next focus and scroll restoration, navigator access while focused, 44px mobile targets, committed Arabic Employee Reference Lists translation, and stronger live permission-loss evidence.

Task 4 fix round 1: commit `592c53e3` closes navigation focus/scroll, focused navigator, 44px targets, and Arabic rename findings. Re-review keeps Task 4 open because permission revocation depends on a synthetic same-window event; fix round 2/5 must add real focus/visibility and relevant 403 revalidation, latest-response-wins authorization refresh, and independent-context evidence.

Task 4 fix round 2: commit `7172e852` adds authoritative focus/visibility/403 revalidation, coalescing, latest-wins protection, canonical Leave editor composition, and Setup Studio dictionary aggregation. Re-review keeps Task 4 open for public-route refresh suppression, non-destructive transient refresh failures with unsafe actions blocked, a real canonical Onboarding checklists editor, and truly independent-context evidence. Fix round 3/5 started.

Task 4 fix round 3: commit `a55f79d7` closes public-route refresh, transient dirty-state preservation/action blocking, definitive 401 clearing, independent-context evidence, and tracked canonical onboarding composition. Re-review finds functional/security compliance; fix round 4/5 is limited to onboarding form labels, Arabic authorization-recovery translations, and focus handoff/restoration around the inert authorization boundary.

Task 4 complete: commit `a6c48b08` closes the final onboarding-label, Arabic authorization-recovery, and focus-handoff findings. Clean-index Task 4 evidence: 61 passed, 3 intentional project skips; English/Arabic onboarding Axe coverage has no serious/critical violations. Final independent review: compliant, no findings, Approved.

Task 4 fix round 4: canonical onboarding configuration inputs now have durable accessible names; Arabic authorization checking/unavailable/retry copy is complete; and authorization checking, transient failure, and successful recovery move focus to status, Retry, and the preserved originating control respectively. Clean-index focused contracts pass 4/4 and the complete exact Task 4 desktop/mobile set passes 61 with 3 intentional project skips. English/Arabic focused-editor Axe reports zero serious/critical violations, live TypeScript passes, and clean-index TypeScript retains only the documented unrelated employee-access export mismatch.

Task 4 fix round 3: public auth routes now suppress background identity reads; transient background failures preserve the established user, mounted dirty editor, and values while visibly blocking Setup mutation/navigation until a successful authoritative retry; definitive 401/403 still clears access. Onboarding checklists now mount the tracked canonical recruitment checklist/template form and API in configuration-only mode, and the revocation fixture uses an independent browser context. Live focused contracts pass 6/6, auth units 9/9, live TypeScript passes, and a clean-index exact Task 4 run passes 53 with 3 intentional project skips. Clean-index TypeScript retains only the previously documented unrelated employee-access export mismatch.

Task 2 fix round 1 implementation complete: staged-only contracts pass 3/3 and route unit tests pass 7/7 from an independently served `git checkout-index` export. Unauthorized explicit company scope now stays blocked until an authorized recovery choice; Guided return is committed and pending files remain guarded. Clean-index broad-suite runs also proved that the prior all-green claim depended on unstaged user work: lifecycle 0/10, Guided selection 20/32, prefill 0/22, binder 0/6, review 0/8, and TypeScript has only the known unrelated `workEmailDomainProblem` index mismatch. Those user-owned baseline gaps were documented and left unstaged.

Task 2 fix round 1 committed as `4982fe15` (`fix: fail closed Setup path scope`); the commit contains exactly five Task 2 files.

Task 3 RED: the five exact catalogue contracts produced the expected missing-metadata failure — 17 passed, while stable business grouping/order and effective target scope failed because those fields were absent. The existing destination/path, approved rename, and operations-route characterizations passed.
Task 3: complete (commits 4982fe1..37ad68a, tests: bash -lc 'cd frontend && npx playwright test -c playwright.unit.config.ts unit/setupAreas.spec.ts && npx tsc --noEmit' →   19 passed (458ms))

Task 4 RED: the existing detached `Open settings` and stacked editor/canvas implementation failed the new whole-row and exclusive-focused workspace contracts. The captured partial focused run ended 3 passed, 5 failed, 1 interrupted, with the required whole-row/focused assertions failing for absent new semantics.

Task 4 implementation: grouped full-row area canvases, an exclusive single canonical editor workspace, truthful effective-scope copy, Back plus previous/next controls, focus/scroll restoration, permission-loss unmounting, and the large-desktop/fullscreen-mobile all-area navigator are implemented. Focused Task 4 plus canonical PUT/identity/failed-save/axe evidence is 32 passed and 2 project-specific skips; TypeScript passes. The full dirty-worktree suite is 136 passed, 2 skipped, 52 failed, with all Task 4 exact contracts green and remaining failures confined to pre-existing dirty onboarding/Guided/direct-GL guard expectations documented in the Task 4 report.

Task 4 staged snapshot: exact Task 4 desktop/mobile contracts pass 22/22 with 2 project-specific skips from a `git checkout-index` export, independent of unstaged user work. The staged-only TypeScript run reaches only the already documented unrelated committed-index `workEmailDomainProblem` test/source mismatch; live-worktree TypeScript is green.

Task 4 fix round 1: deterministic current-row focus and scroll now survive explicit Back plus browser Back/Forward after previous/next, the full area navigator remains available in focused mode, all new focused/mobile navigator controls measure at least 44px, Setup Studio owns the approved Arabic Employee Reference Lists translation, and a same-origin auth-change event refreshes the authoritative session so permission loss unmounts the editor without reload. Exact Task 4 browser contracts pass 31 with 3 project-specific skips; live TypeScript passes. Relevant legacy history/dirty subset remains 6 passed/10 pre-existing failures as documented in the Task 4 report.

Task 4 fix round 2: authoritative authorization revalidation now runs on focus, visible tab restoration, relevant protected 403s, and explicit role-change signals with 150 ms event coalescing. Latest-response-wins generation guards prevent stale privileged `/api/auth/me` replies from restoring revoked access, including across login/logout transitions. Index-only verification also closed masked composition gaps by mounting the canonical leave editors and aggregating the committed Setup Studio Arabic catalogue without staging unrelated dirty work. Cross-page revocation, real client-403, and deterministic response-race contracts pass; the exact Task 4 desktop/mobile set passes both live and from a fresh index export (35 passed, 3 intentional project skips), auth load-state units are 9/9, and live TypeScript passes.

Task 5 RED: the eight exact acceptance contracts initially passed 6 and failed 2. At 320/390 widths, the mobile all-area navigator was rendered beneath the sticky application shell so its Close control was intercepted; the Arabic navigator failure was an ambiguous test locator. No Guided styling defect was fixed.

Task 5 GREEN: the area navigator now portals to `document.body`, and the exact desktop/mobile acceptance set passes 16/16 with zero serious/critical English/Arabic Axe findings, no 320/390 overflow, reachable 200%/400% reflow actions, and deterministic dirty cancel/confirm state. Units pass 27/27, lifecycle 10/10, binder 6/6, review 8/8, live TypeScript passes, and the production build succeeds. Stable fixture screenshots are in `docs/evidence/setup-studio-task5/`.

Task 5 broad-baseline ruling: workspace is 185 passed/3 skipped/48 pre-existing dirty-suite failures; setup experience is 61 passed/3 skipped/2 protected Guided compactness failures; policy prefill is 13 passed/9 stale untracked workspace-entry failures. The index-only export passes 14/16 Task 5 tests and exposes the pre-existing unstaged Guided-toolbar dependency; its TypeScript reaches only the known unrelated employee-access export mismatch, and its production build passes. These gaps are documented without staging unrelated user work.

Task 5 authenticated read-only acceptance: local 5197 was restarted with API target 5127; Evostel Shadow Administrator/Admin selected Evostel Policy Shadow — Local. Guided retained five steps and the approved controls; Configuration Leave exposed whole-row settings; focused Leave policies reopened 14 populated Active canonical records with Back/Previous/Next and New/Edit/Archive available. No save, apply, archive, publish, or tenant mutation was performed. Frontend build identity remains `unknown`; API health is HTTP 200 at commit `2e992b3da05dc3f923376638d0e205b7de0132cd`, so matching runtime provenance is not claimed.

Task 5 review fix: a new `dir=rtl` assertion proved the original Arabic Axe loop was resetting to English. English and Arabic now use separate page/init-script lifecycles and must expose locale-specific direction plus Configuration and Guided anchors before Axe. Corrected exact acceptance passes 16/16; related Arabic focused cases pass 6/6; regenerated EN/AR screenshot hashes differ. Native authenticated Chrome additionally passed actual 200% and 400% zoom, keyboard dialog/focus-loop/restore, no-horizontal-overflow, and live Arabic RTL/localized-navigation checks before restoration to English/LTR/100%. No production UI or tenant data changed.

Task 5 final package reconciliation: the approved Guided toolbar and browser-scoped draft lifecycle were isolated from the dirty worktree and committed without restyling the five-step wizard. Companies now has explicit group-register target metadata and account-type-aware scope copy. A fresh index-only export passes 32/32 setup/draft units and 36/36 combined desktop/mobile acceptance cases, including all 16 Task 5 cases plus Guided Return/storage/preview/pending-file coverage and both Group/SingleCompany scope-copy contracts; TypeScript reaches only the unrelated committed employee-access test/export mismatch.

Ownership clarification: `8e17f270` is a separate reconciliation of the owner-approved Guided baseline, not a Task 5 runtime change; `c22b79bc` is a Task 3 scope-metadata correction. Final review follow-up makes the Companies label account-type-aware, corrects the clean unit fixture's complete SetupSections shape, and adds committed saved-preview recovery coverage.
