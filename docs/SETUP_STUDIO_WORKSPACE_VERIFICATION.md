# Setup Studio Package 1 verification

Status: implementation and automated verification complete, 2026-10-14. Bounded authenticated local QA passed on the final build; independent scoped re-review is pending. This document does not authorize production deployment.

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
- Automated fixture checks do not certify server security, live tenant persistence, deployment or complete module translation. The bounded native Leave persistence proof above is distinct. Independent scoped re-review must finish before owner handoff is described as accepted.
