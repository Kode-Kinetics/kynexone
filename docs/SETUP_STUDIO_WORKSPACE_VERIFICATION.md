# Setup Studio Package 1 verification

Status: approved implementation under validation, 2026-10-14. Independent review and authenticated local acceptance are pending controller evidence. This document does not authorize production deployment.

## Delivered scope

Package 1 supplies twelve-area discovery, company context, permission/feature-aware destinations, canonical settings-form reuse, ongoing maintenance, and preserved draft/source navigation. Existing operational routes remain available. Settings save through their incumbent APIs; starter-draft Apply and policy publication remain distinct actions.

Task 5 adds tenant-admin maintenance and read-only acceptance, forged URL and company-isolation checks, populated Leave dialog accessibility, failure/retry retention, and reference-request ordering protection. Leave reuses the shared portal dialog (focus trap, Escape, focus return and body scroll lock), associates controls with their labels, stacks policy fields at phone width, and focuses save errors. Request lifetimes discard responses from departed company scopes; same-scope company refreshes retain the newest result. No backend, schema, database, authentication or production changes belong to this package.

## Automated evidence

Browser plugin not available; repository Playwright was used as authorized. The flow under test is `/setup?tab=aiSetup` → area → populated canonical editor → save/error/retry → reopen, plus company changes and retained drafts. Every browser API is intercepted: no real tenant test records were written. Fixture read-back proves frontend request/state behavior, not server persistence or authorization.

Required cases cover `tenant_admin_can_revisit_and_edit_saved_settings`, `read_only_user_cannot_edit`, `company_switch_does_not_reuse_other_company_draft`, `forged_area_or_return_path_is_ignored`, `failed_save_retains_values`, and English/Arabic `rtl_mobile_long_labels_remain_accessible` variants. Read-only uses the existing Auditor/organization.read fixture contract; maintenance uses Admin and incumbent organization/module permissions, never a SuperAdmin bypass.

RED evidence: `/tmp/task5-red.log` captured missing named dialog/control access. `/tmp/task5-red-corrected.log` captured newest-company overwrite and old company options reappearing after both successful and failed current-scope reads. Populated desktop axe then caught policy Save contrast at 4.49:1 in `/tmp/task5-focused.log`; the button now uses the existing darker blue utility.

Final build and tsc pass; unit suite is **310/310**. Final six browser configurations: workspace **102/102**, setup **57 passed / 3 intentional desktop-only layout skips on mobile**, binder **6/6**, prefill **13/13**, review **8/8**, lifecycle **10/10**. Total: **196 passed / 3 intentional skips** on the final build. Earlier focused acceptance was26/26.

The first broad workspace run found a double confirmation on Change company (101 passed/1 failed), reproduced five times on mobile: an unguarded URL write preceded the context decision. The handler now requests only the guarded context transition; existing query synchronization updates the URL after acceptance. The unchanged prompt-count assertion is strengthened with history/file/draft retention and accepted round-trip checks; **10/10 repeated desktop/mobile runs pass**, as does the final full workspace suite. Detailed commands and outcomes are retained in `.superpowers/sdd/2026-10-14-setup-studio-workspace/task-5-report.md`.

Local candidate: `http://127.0.0.1:5197`, API `http://127.0.0.1:5127`, frontend BUILD_ID `eGVPEB0zdeTSL4yqHwtUl`. This identifies the automated candidate; it does not establish live tenant acceptance.

Screenshots are outside the repository under `/tmp/kynex-setup-final-evidence/`: workspace and populated Leave dialog desktop/mobile EN/AR, plus focused save-error states. Accessibility assertions scope the workspace and populated dialog, require no serious/critical axe findings, and check 390px page/dialog overflow, keyboard trapping/return and unobscured Save/Cancel centers. Canonical Leave currently retains some English form labels in Arabic locale; these tests certify RTL layout and accessible controls, not complete module translation.

## Authenticated local acceptance — pending controller evidence

Controller owns native Chrome verification on the local Evostel shadow UI. Record frontend URL/build identity, API identity, tenant/company, signed-in role/permissions, visible controls, and any explicitly authorized dedicated-record saved/read-back values here after observation. Do not infer these from intercepted fixtures. Existing client records must not be changed merely for screenshots. No credentials belong in this document.

Controller's current blocker: native Chrome reports `cgWindowNotFound`; extension tab control reports `Debugger unattached`. User reconnect is requested. No live record writes occurred. Local acceptance remains pending, and this UI is not described as fully accepted or production-certified.

## Remaining programme work and limitations

- Package 2 document-to-form adapters remain required and open: per-domain mapping, source/target version checks, record identity and sparse update enforcement need their own reviewed implementation and evidence.
- Package 3 policy-library convergence/Kody remain required and open: authored configuration to policy drafts, approval/version/audience lifecycle, group/company applicability and grounded employee guidance are not delivered by area navigation.
- Twelve discoverable areas do not mean twelve automatic extraction adapters. Recruitment, services and offboarding operations are not advertised as generic policy editors. Onboarding templates remain tenant-wide.
- Starter drafts are browser-local, not durable collaborative drafts. Unuploaded files cannot survive a full page reload. Existing Task 4 guards protect supported transitions and storage failures.
- GL conservatively may prompt after a successful save because incumbent GL subeditors do not expose save-state callbacks. This is an explicit accepted boundary, not seamless save/reset behavior.
- On browsers without Navigation API support, history entries lacking the application's position tag cannot be safely reversed; the fallback does not invent a delta. Tagged same-document history and native full-page beforeunload protections remain tested.
- Automated fixture checks do not certify server security, live tenant persistence, deployment or complete module translation. Independent reviewer and controller local acceptance must finish before owner handoff is described as accepted.
