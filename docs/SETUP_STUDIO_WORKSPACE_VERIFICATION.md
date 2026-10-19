# Setup Studio Configuration Center final acceptance — 2026-10-18

Status: the Task 5 Configuration Center acceptance contract is implemented and its focused live-worktree gates pass. One test-proven Configuration defect was fixed: the mobile area navigator now portals to `document.body`, so the sticky application header no longer covers its title or Close control. The approved five-step Guided Setup was not restyled.

This is local acceptance evidence, not production approval. Fixture tests intercept every API request and issue no tenant writes. The authenticated shadow-tenant inspection was strictly read-only; no Save, Apply, archive, policy publication, or record mutation was performed.

## Task 5 evidence

The eight required cross-cutting contracts are committed in `frontend/e2e/setup-studio-workspace.spec.ts`:

- `guided_visual_contract_survives_configuration_redesign`
- `english_and_arabic_paths_have_no_serious_or_critical_axe_findings`
- `mobile_390_and_320_have_no_page_overflow`
- `rtl_long_labels_keep_navigation_reachable`
- `two_hundred_percent_zoom_keeps_actions_reachable`
- `four_hundred_percent_reflow_has_no_two_axis_scroll`
- `dirty_cancel_preserves_url_values_scroll_and_focus`
- `dirty_confirm_navigates_once`

RED: the first desktop-focused run passed 6 and failed 2. At 320/390 widths, the mobile navigator was visible but its Close control was not clickable because the shell's sticky header intercepted the pointer; the Arabic navigator failure was an ambiguous test locator rather than a product defect. After portaling the dialog and anchoring the Arabic accessible name, the exact desktop/mobile acceptance set passed **16/16**. The tests prove zero serious/critical Axe violations for English and Arabic Guided/Configuration regions, no document overflow at 320/390, reachable navigation at the 200%/400% reflow equivalents, and exact dirty cancel/confirm behavior including URL, authored value, scroll, focus, history, and zero writes.

Review correction: the first English/Arabic Axe loop was not trustworthy because the shared `boot` init script reset the locale to English on each navigation. A new Arabic `dir=rtl` assertion reproduced the false pass (`ltr` received). English and Arabic now boot in separate pages with independent locale init scripts. Before Axe, each path must expose its expected document direction and localized Configuration/Guided anchors. The corrected exact set remains **16/16**, and the related Arabic focused layout, authorization-recovery, and approved-rename cases pass **6/6**.

Stable screenshots under `docs/evidence/setup-studio-task5/`:

- `guided-preserved-desktop.png`
- `configuration-en-desktop.png`
- `configuration-ar-desktop.png`
- `configuration-390-navigator.png`
- `configuration-320-navigator.png`
- `configuration-rtl-320.png`
- `configuration-200-percent.png`
- `configuration-400-percent.png`

These screenshots use the intercepted browser fixture. They are responsive/layout evidence, not authenticated persistence evidence. The regenerated English and Arabic screenshots are visibly different and have distinct SHA-256 values: English `8fe06ae09dc83bed3d3696329cb85f55cd2f23105255d8e9c69d3edd49683a9e`; Arabic `8c6092db8673dc1357bff8f03eacd4bb9bce8fb16fab3106c32126c947c73df2`.

## Commands and results

- Setup path/catalogue/canonical-form units: **27 passed**.
- Exact Task 5 acceptance, desktop and mobile: **16 passed**; the final mobile screenshot refresh also passed **2/2**.
- Corrected Arabic focused layout/auth/approved-rename cases: **6/6 passed**.
- Setup lifecycle: **10 passed**.
- Policy binder: **6 passed**.
- Policy review: **8 passed**.
- Live-worktree TypeScript after the build: exit 0.
- Production build with `NEXT_PUBLIC_API_BASE_URL=http://127.0.0.1:5127`: exit 0, 87 static pages generated.

The broad dirty-worktree suites are not all green and are not reported as Task 5 regressions:

- Setup Studio workspace: **185 passed, 3 skipped, 48 failed**. All 16 Task 5 cases passed. The failures are older dirty/untracked expectations centered on native browser-dialog handling versus the current application dialog, plus one authorization focus flake and one mobile company-switch case.
- Setup experience: **61 passed, 3 intentional mobile skips, 2 failed**. Both failures are pre-existing owner-protected Guided desktop compactness expectations (`Upload and review` and `Continue` fully in viewport). Task 5 did not restyle Guided.
- Policy form prefill: **13 passed, 9 failed**. All nine failures are untracked workspace tests that still enter `/setup?tab=aiSetup&area=onboarding&setting=onboarding` without the Configuration path and expect the old area heading. The retained direct form contracts pass.

A final index-only export independently passed **32/32** setup and draft-lifecycle units. Its combined desktop/mobile acceptance run passed **22/22**: all **16/16** Task 5 cases, both Guided path-switch cases, and the Companies group-register wording contract. The committed package now contains the approved `Save for later`, `Policy library / updates`, and `Cancel setup` controls plus company/user-scoped browser draft recovery; the five-step Guided layout was not restyled. Companies now uses explicit `group` target metadata and the truthful label `Group company register`, not selected-company copy. Index-only TypeScript reaches only the known unrelated `unit/employeeAccess.spec.ts` / `workEmailDomainProblem` export mismatch.

## Authenticated shadow-tenant read-only check

The controller repaired only the local runtime target and opened the existing signed-in tenant at `http://127.0.0.1:5197` with API `http://127.0.0.1:5127`.

- Persona: **Evostel Shadow Administrator**, role **Admin**.
- Explicit company scope: **Evostel Policy Shadow — Local**, company ID `009f78a6-91ee-4a86-b7a8-28caa07e0e64`.
- Guided route visibly retained all five named steps, Company details, the HR policy call to action, `Step 1 of 5`, Continue, and the Employee Reference Lists label.
- Configuration Leave canvas showed whole-row Leave types, Leave policies, and Holiday calendars.
- Focused Leave policies showed top Back/Previous/Next navigation, **14 populated Active policies**, and the incumbent New/Edit/Archive actions; no action was invoked.
- Frontend `/build-info`: `{ "service": "kynexone-web", "commit": "unknown" }`.
- Backend `/health/live`: HTTP 200, service `zayra-api`, commit `2e992b3da05dc3f923376638d0e205b7de0132cd`.
- Source HEAD at the time of verification: `a6c48b080cc170acec5b311f617ae0fb43f72096` before the Task 5 commit.

The frontend does not expose a matching source commit, and the backend identity differs from the frontend source HEAD. Therefore this proves a healthy local signed-in UI against a live local API and populated canonical data; it does **not** prove same-commit frontend/backend provenance, server authorization exhaustiveness, production deployment, or persistence of a new Task 5 write.

### Native Chrome zoom, keyboard, and RTL gate

This gate used the authenticated tenant and actual Chrome zoom controls, not only emulated viewport widths.

- At 100%: `innerWidth=1728`, `devicePixelRatio=2`, `clientWidth=scrollWidth=1724`.
- At native 200%: Chrome showed 200%; `innerWidth=864`, `devicePixelRatio=4`, `clientWidth=scrollWidth=860`, with no horizontal overflow. Shift+Tab reached Next, Previous, and Back. Browse all areas exposed Workspace overview and all 12 named areas.
- At native 400%: Chrome showed 400%; `innerWidth=432`, `devicePixelRatio=8`, `clientWidth=scrollWidth=428`, with no horizontal overflow. Return opened the area dialog, Tab focused Close, Shift+Tab wrapped to Compliance and statutory administration, and Escape closed and restored focus to Browse all areas.
- Live Arabic at native 400% exposed `html lang="ar" dir="rtl"`, localized heading `الإعداد والإدارة`, `innerWidth=432`, `devicePixelRatio=8`, `clientWidth=scrollWidth=428`, and reachable localized Back/Previous/Next actions.
- The controller restored English/LTR/100% (`innerWidth=1728`, `devicePixelRatio=2`) and left the focused Leave policies tab open.

## Task 5 remaining gates and nonclaims

- The 48 broad workspace failures, 2 Guided compactness failures, and 9 legacy prefill-entry failures remain outside this bounded Configuration-only fix and require separate baseline ownership.
- Browser automation supplies repeatable focus, RTL, 320/390, and reflow regression evidence; native Chrome separately verifies actual 200%/400% zoom and keyboard behavior. Authenticated shadow-tenant validation remained read-only by design.
- No backend, schema, database, authentication, deployment, feature-flag, or production mutation was made by Task 5.

---

# Historical Setup Studio Package 1 verification

Status: Package 1 locally verified and independently code-reviewed, 2026-10-14. Bounded authenticated local QA passed on the final build. Owner acceptance and release integration remain separate; this document does not authorize production deployment.

## Delivered scope

Package 1 supplies twelve-area discovery, company context, permission/feature-aware destinations, canonical settings-form reuse, ongoing maintenance, and preserved draft/source navigation. Existing operational routes remain available. Settings save through their incumbent APIs; starter-draft Apply and policy publication remain distinct actions.

Task 5 adds tenant-admin maintenance and read-only acceptance, forged URL and company-isolation checks, populated Leave dialog accessibility, failure/retry retention, and reference-request ordering protection. Leave reuses the shared portal dialog (focus trap, Escape, focus return and body scroll lock), associates controls with their labels, stacks policy fields at phone width, and focuses save errors. Request lifetimes discard responses from departed company scopes; same-scope company refreshes retain the newest result. No backend, schema, database, authentication or production changes belong to this package.

## Automated evidence

Browser plugin not available; repository Playwright was used as authorized. The flow under test is `/setup?tab=aiSetup` → area → populated canonical editor → save/error/retry → reopen, plus company changes and retained drafts. Every browser API is intercepted: no real tenant test records were written. Fixture read-back proves frontend request/state behavior, not server persistence or authorization.

Required cases cover `tenant_admin_can_revisit_and_edit_saved_settings`, `read_only_user_cannot_edit`, `company_switch_does_not_reuse_other_company_draft`, `forged_area_or_return_path_is_ignored`, `failed_save_retains_values`, and English/Arabic `rtl_mobile_long_labels_remain_accessible` variants. Read-only uses the existing Auditor/organization.read fixture contract; maintenance uses Admin and incumbent organization/module permissions, never a SuperAdmin bypass.

RED evidence: `/tmp/task5-red.log` captured missing named dialog/control access. `/tmp/task5-red-corrected.log` captured newest-company overwrite and old company options reappearing after both successful and failed current-scope reads. Populated desktop axe then caught policy Save contrast at 4.49:1 in `/tmp/task5-focused.log`; the button now uses the existing darker blue utility.

Task5 build and tsc passed; its unit suite was **310/310**. Task5 six browser configurations: workspace **102/102**, setup **57 passed / 3 intentional desktop-only layout skips on mobile**, binder **6/6**, prefill **13/13**, review **8/8**, lifecycle **10/10**. Total: **196 passed / 3 intentional skips** on that build. Earlier focused acceptance was26/26.

The first broad workspace run found a double confirmation on Change company (101 passed/1 failed), reproduced five times on mobile: an unguarded URL write preceded the context decision. The handler now requests only the guarded context transition; existing query synchronization updates the URL after acceptance. The unchanged prompt-count assertion is strengthened with history/file/draft retention and accepted round-trip checks; **10/10 repeated desktop/mobile runs pass**, as does the final full workspace suite. Detailed commands and outcomes are retained in `.superpowers/sdd/2026-10-14-setup-studio-workspace/task-5-report.md`.

Previous Task5 candidate: frontend BUILD_ID `eGVPEB0zdeTSL4yqHwtUl`. The final review-fix candidate below supersedes it; neither build identity establishes live tenant acceptance.

Screenshots are outside the repository under `/tmp/kynex-setup-final-evidence/`: workspace and populated Leave dialog desktop/mobile EN/AR, plus focused save-error states. Accessibility assertions scope the workspace and populated dialog, require no serious/critical axe findings, and check 390px page/dialog overflow, keyboard trapping/return and unobscured Save/Cancel centers. Canonical Leave currently retains some English form labels in Arabic locale; these tests certify RTL layout and accessible controls, not complete module translation.

## Authenticated local acceptance — bounded controller evidence

Controller verified the exact final build `qb12z79QkXet73wsMg5Ui` through native Chrome at local `/setup?tab=aiSetup&area=leave&setting=leavePolicies`, signed in as **Evostel Shadow Administrator**. With explicit user authorization, the controller created **QA Setup Studio 2026-10-14**, Annual Leave / KSA / 7 days / Monthly / **Draft**. The Draft filter showed one record. Edit reopened 7 days; the controller changed it to 8 and saved. After a browser reload, the Draft filter again showed the one row with 8 days; Edit retained its name, type, KSA, 8 days, Monthly and Draft status. Native screenshot and accessibility-tree evidence are in the controller transcript.

The earlier Chrome-control blocker was resolved after user reconnection. The controller retained this uniquely named QA draft and closed its clean editor. No seeded records were touched, no policy was activated and production was untouched. This is bounded live create/edit/reload/read-back proof for the existing Leave editor; it does not certify every area, all authorization boundaries or production deployment. Implementer browser suites still use isolated intercepted contexts.

## Final whole-package review fixes

Three confirmed Important navigation gaps were addressed in a single bounded wave from `08a9cbe7`: GL's scope/mouse/keyboard/banner transitions now reuse the active Setup guard and remount editors after an accepted scope change; shell palette/profile/signout use the same cancellable seam as Sidebar; reopening the selected embedded setting or direct tab is a no-op before confirmation or dirty clearing. Search/type filters and read-only group statutory-country browsing remain harmless. Canonical save paths, command authorization and accepted logout behavior remain intact.

Old-build red tests reproduce missing prompts and same-target dirty clearing. Final focused **20/20** and repeated immediate-edit **18/18** checks pass, including exact retained fields/URLs on decline, one prompt per accepted departure, account reset and correct company ID in the subsequent GL save request. Typecheck, **310 units**, and one final production build pass. Full six-suite results are recorded in `.superpowers/sdd/2026-10-14-setup-studio-workspace/final-fix-report.md`.

Final six-suite result on `qb12z79QkXet73wsMg5Ui`: workspace **120 passed**, setup **57 passed / 3 existing skips**, binder **6**, prefill **13**, review **8**, lifecycle **10** — **214 passed / 3 intentional skips**. The strengthened profile case also passed **6/6** repetitions. Existing Node `url.parse()` and Next standalone-start warnings remain disclosed in the report.

The first broad workspace run had 119 passes and one fixture failure: accepted profile navigation correctly redirected the fixture without `ess.read` to Dashboard with Access Denied. The test now asserts that final authorization outcome while retaining its exact draft/URL-on-decline and prompt-count assertions; no production permissions changed.

Current local candidate: `http://127.0.0.1:5197`, API `http://127.0.0.1:5127`, frontend BUILD_ID **`qb12z79QkXet73wsMg5Ui`**, PID61570/session6093. API PID57186 was untouched; both health checks returned200. No production-source edits followed this build. Essential `SetupPage.tsx` integration remains deliberately unstaged, so the scoped commit alone cannot reconstruct the tested working tree. The report records source hashes, protected baseline comparison, commands, logs and remaining limits. All browser tests are intercepted; no native or server acceptance is inferred.

## Remaining programme work and limitations

- Package 2 document-to-form adapters remain required and open: per-domain mapping, source/target version checks, record identity and sparse update enforcement need their own reviewed implementation and evidence.
- Package 3 policy-library convergence/Kody remain required and open: authored configuration to policy drafts, approval/version/audience lifecycle, group/company applicability and grounded employee guidance are not delivered by area navigation.
- Twelve discoverable areas do not mean twelve automatic extraction adapters. Recruitment, services and offboarding operations are not advertised as generic policy editors. Onboarding templates remain tenant-wide.
- Starter drafts are browser-local, not durable collaborative drafts. Unuploaded files cannot survive a full page reload. Existing Task 4 guards protect supported transitions and storage failures.
- GL conservatively may prompt after a successful save because incumbent GL subeditors do not expose save-state callbacks. This is an explicit accepted boundary, not seamless save/reset behavior.
- On browsers without Navigation API support, history entries lacking the application's position tag cannot be safely reversed; the fallback does not invent a delta. Tagged same-document history and native full-page beforeunload protections remain tested.
- Automated fixture checks do not certify server security, live tenant persistence, deployment or complete module translation. The bounded native Leave persistence proof above is distinct.

## Final independent gate and handoff

The independent final reviewer verified all three navigation findings as addressed, with no new Critical/Important breakage. Approval covers `6e401b73` plus the protected integration, not that commit alone. Final totals are **214 browser passes / 3 intentional skips**, **310 units**, **20 focused checks**, **18 timing repeats**, and **6 profile repeats**. Root independently reran typecheck (exit0), checked source hashes against the build, and verified frontend/API HTTP200 and unchanged tracked backend diff hash. Native QA is the bounded Leave scenario above.

Essential pre-dirty integration remains uncommitted by design; preserve this worktree and `.superpowers/sdd/2026-10-14-setup-studio-workspace` baselines until it is safely captured. Do not merge or deploy the commit range as if it reconstructs the tested candidate. No push, merge, deployment, feature enablement or policy publication was performed. Existing DEP0169 and standalone startup warnings remain; earlier color-environment warnings were avoided in final commands with `env -u NO_COLOR`.
