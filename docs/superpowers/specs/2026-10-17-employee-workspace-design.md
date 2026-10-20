# Employee Workspace: one authoritative record surface

Status: design approved in conversation; written specification awaiting owner review. This document authorizes no production deployment and makes no completion claim.

## 1. Outcome

Replace the People page's persistent right-hand profile drawer and separate edit window with one authoritative Employee Workspace. Selecting an employee opens that workspace. Viewing, adding, editing, readiness resolution, and permitted lifecycle actions happen inside it without returning to a second representation of the same employee.

The directory remains a directory. The workspace becomes the employee record. Creation keeps its guided flow, but it runs inside the same workspace shell and transitions to the saved employee record after creation.

Success means an HR user can:

1. open an employee from a filtered list;
2. understand identity, employment state, readiness, and next actions immediately;
3. move directly to one record section without scanning an oversized page;
4. add or edit the relevant information in place;
5. understand approval and effective-date consequences before saving;
6. return to the same directory state;
7. refresh, share, bookmark, or open the employee record in another browser tab.

## 2. Decision and alternatives

### Selected: route-backed employee workspace

Use a dedicated, addressable workspace inside the established KynexOne application shell.

- `/people` — employee directory.
- `/people/new` — create employee workspace.
- `/people/new-hires` — the existing maker-checker draft review and activation queue; it is not a creation alias.
- `/people/[employeeId]` — employee record in view mode.
- `/people/[employeeId]/edit?section=employment` — destination-scoped edit entry.
- `/people/[employeeId]/edit?mode=guided&section=employment` — explicit full-record guided edit entry.
- `/people/[employeeId]?section=pay-benefits&panel=benefits` — deep link to a record destination and panel.

Static routes `new` and `new-hires` are matched before the numeric employee identifier route. A non-numeric value in the employee identifier position renders the workspace's invalid-link recovery state; it is never interpreted as an employee request.

This supports browser history, refresh, direct links, new tabs, long-running work, permission-aware sections, and mobile layouts.

### Not selected: full-screen modal

A modal preserves the directory behind it but weakens addressability, browser history, nested-dialog behavior, and long-lived work. It also keeps the conceptual pressure to “close the window” rather than treat the employee as a stable record destination.

### Rejected: wider or resizable drawer

The existing problem is structural, not a width defect. A drawer continues to split attention, compress record navigation, compete with the directory for space, and fail on smaller screens.

## 3. Information architecture

The workspace contains seven primary destinations. Only the selected destination is rendered as the main task surface.

| Destination | Content and actions |
| --- | --- |
| Overview | Identity summary, role and organization, status, readiness, missing requirements, self-service access, recent activity, and the most important next actions |
| Personal | Names, demographics, contact details, emergency contacts, and permitted personal information |
| Employment | Company, branch, department, designation, manager, grade, contract, dates, work location, transfers, and employment history |
| Pay & Benefits | Payroll identity, bank/WPS data, salary package, allowances and deductions, benefits, package, and approval state |
| Compliance & Documents | National identity, passport, Muqeem, GOSI, Qiwa, visas, expiry dates, required documents, upload status, and jurisdiction-specific requirements |
| Access & Onboarding | Work email, login/self-service state, sign-in issuance, employee completion, onboarding readiness, and activation controls |
| Activity | Status history, approvals, audit events, relevant changes, and development activity that does not belong in the core summary |

The current detail model must map losslessly into destinations and addressable panels. The destination is encoded by `section`; a subordinate surface is encoded by `panel` so a link can reopen the exact place without inventing parallel records.

| Current surface | Canonical destination and panel |
| --- | --- |
| Personal | `section=personal` |
| Employment | `section=employment&panel=details` |
| Payroll | `section=pay-benefits&panel=payroll` |
| Benefits | `section=pay-benefits&panel=benefits` |
| Package | `section=pay-benefits&panel=package` |
| Deductions | `section=pay-benefits&panel=deductions` |
| Compliance | `section=compliance-documents&panel=compliance` |
| Documents | `section=compliance-documents&panel=documents` |
| History | `section=activity&panel=history` |
| Transfers | `section=employment&panel=transfers` |
| Development | `section=activity&panel=development` |

If a mapped panel is feature-disabled, the workspace explains that the feature is unavailable and links to the parent destination. If the user lacks permission, it is omitted from navigation and a direct link returns a permission-safe unavailable state without leaking content. Development remains an explicit Activity panel when its feature and permission are enabled; it is not silently removed or buried.

Feature-flagged or permission-restricted content appears only when genuinely available. A destination with no accessible content is omitted rather than rendered empty. Deep links to unavailable destinations return an explicit explanation and a safe fallback.

Readiness items deep-link to the exact destination and subsection that can resolve them. Empty states state what is missing and present the permitted Add action; they do not show unexplained dashes or blank grids.

### Guided information hierarchy

The screen drives the user without turning the record into a tutorial or a wall of help text.

- Overview presents one **Recommended next action** based on authoritative status, readiness, access, and permission data, followed by the remaining requirements in priority order.
- Each destination begins with a short purpose statement only when the task or consequence is not self-evident. Field-level help appears beside the affected field, not in a separate manual.
- Readiness blockers explain why the item matters, who can resolve it, whether it blocks activation or pay, and the exact permitted action. Recommendations remain visually distinct from blockers.
- Approval-required and effective-dated changes explain the consequence before the user enters Review; Review shows the before/after value, effective date, approver path, and which changes apply immediately.
- Progressive disclosure keeps advanced, rare, and historical information behind clearly named panels. Critical requirements and next actions are never hidden in accordions.
- Guidance is derived from real record and policy state. It must not invent compliance, legal, or completion claims and must not repeat information already visible in the header.

## 4. Workspace layout and behavior

### Desktop

- Keep the existing KynexOne shell and People navigation state.
- Use a sticky workspace header with **Back to People**, avatar, employee name, employee code, status, department/designation, readiness, and permission-aware actions.
- Use a compact left destination rail at large widths and a wide main content area. The rail is navigation, not a second application sidebar.
- Keep one primary content scroller. Header, destination navigation, and dirty-state actions remain reachable.
- Put secondary or dangerous lifecycle actions in a labelled **More actions** menu or a dedicated Overview action panel. Do not append status and delete controls beneath every destination.

### Mobile and narrow layouts

- The workspace remains a full route and uses the full viewport.
- Replace the left rail with a named Sections control that exposes the same destinations.
- Stack fields and cards in one column. Avoid narrow horizontal tab strips.
- Use safe-area padding and a sticky bottom Save/Cancel bar only while editing.
- The sticky bar must clear the existing mobile bottom navigation, assistant launcher, browser safe area, and on-screen keyboard rather than covering them.
- Support 390×844 and 320 CSS px/200% zoom without document-level horizontal overflow.

### Purposeful motion and feedback

Retain useful animation as functional feedback, with restrained duration and no decorative delay.

- Destination and wizard-step changes use a subtle directional transition that reinforces hierarchy and preserves the user's spatial model; content never flies across the full viewport.
- Readiness progress animates only when its authoritative value changes, and newly resolved items briefly acknowledge completion before moving to Complete.
- Save, upload, approval submission, and access issuance use immediate pressed/pending feedback followed by an announced success, pending-approval, partial, or failure state in place.
- Opening contextual help, the mobile Sections control, and child dialogs uses short opacity/scale transitions with stable focus and no layout shift.
- Destructive actions never use playful motion. Skeletons do not shimmer indefinitely, and animation never hides latency, blocks input, or replaces a textual status.
- Use the existing motion language and shared primitives. Typical micro-interactions complete in roughly 120–200 ms; destination changes in roughly 180–240 ms.
- `prefers-reduced-motion: reduce` removes non-essential translation, scale, shimmer, and animated progress while preserving state changes, focus movement, and live-region announcements.

### Directory relationship

- Remove the persistent employee aside and its empty “Select an employee” panel.
- Allow the directory table to use the full available content width.
- Make the employee name a real link. The row may remain a pointer convenience, but the link is the keyboard and new-tab target.
- Preserve directory search, status/readiness/self-service filters, import-batch/gap filters, Current/Ex-Employees view, pagination, and scroll/focus anchor in the canonical People URL when the user returns.
- Bulk selection is intentionally cleared when leaving the directory, is never written to the URL, and is announced on return so an invisible stale batch cannot be actioned.
- Browser Back keeps native history behavior. **Back to People** uses a validated `returnTo` query containing only a same-origin `/people...` path, otherwise it falls back to `/people`.
- Ex-Employees remains a separate read-only archive in this redesign. Its search and page state are preserved, but archived/deleted rows do not open the mutable active-employee workspace and expose no active lifecycle actions.

## 5. View, add, and edit model

The workspace opens in view mode. Record destinations display saved information and local actions such as **Edit section**, **Add bank details**, or **Upload document**.

Editing happens inside the same workspace identity and destination. It must not open a second employee profile or close back to a drawer.

- **Edit section** opens only the fields owned by the current destination. **Edit full record** is a separate, permission-aware action that starts the existing cross-destination sequence at `/people/[id]/edit?mode=guided&section=<current-section>`.
- Save from either entry mode advances to the existing before/after guided Review before any mutation. The review preserves the changed-field payload, URL/mode, Back behavior, effective-date handling, approval response, and precise success state. It is not a second employee profile.
- Step and destination changes within the same employee preserve draft values and do not trigger a discard prompt.
- A sticky Save/Cancel bar appears only when there are changes.
- Dirty destinations have a visible non-color indicator.
- Saving sensitive changes shows a compact before/after review, effective date, and approval consequence.
- Failed saves retain values and files, show an actionable message, and support retry without reconstructing the payload.
- Successful edits return to view mode in the same workspace and announce saved, pending approval, partially applied, or failed outcomes precisely.

Creation uses `/people/new` in the workspace shell and preserves the verified Person, Employment, optional Payroll/Salary/Identity, and Review behavior, including country gating, duplicate override, failed-draft retry, draft/resume, and sequential visit requirements. **Save draft and exit** persists the Draft employee, returns to the serialized People return URL, and exposes Resume at `/people/[id]/edit?section=<last-section>`. Successful final creation replaces `/people/new` with `/people/[newId]` and shows the saved record without adding a stale creation entry to history.

`/people/new-hires` remains the separate `EmployeeDraft` review and activation queue, protected by the existing any-of `employees.write` or `employees.approve` route rule. Direct **Add Employee** continues to create a Draft employee through `POST /api/employees`; it does not enter the `EmployeeDraft` New Hires queue. Direct activation retains its existing readiness and `employees.approve` contract. Maker-checker identity separation remains specific to `/people/new-hires`. Only New Hires employee links migrate to workspace routes. Making direct creation enter that queue would require separately approved backend scope and tests.

Remove the concepts and copy **Open full profile** and **Close employee profile**. There is only the workspace and **Back to People**.

## 6. State, data, and component boundaries

Split the current all-in-one `EmployeesPage.tsx` responsibilities without rewriting domain logic.

### `EmployeeDirectoryPage`

Owns search, filters, pagination, import, bulk selection/actions, list loading, and preserved return state. It does not own employee detail, readiness, or edit drafts.

### `EmployeeWorkspaceShell`

Owns the route, record header, destination navigation, view/edit mode, workspace-level loading and error states, permission-aware actions, and return behavior.

Each new route owns an explicit page-level gate: record view requires `employees.read`; create and edit use the applicable action permission; New Hires retains its existing any-of gate. These gates improve navigation behavior but never replace server enforcement.

### `EmployeeOverview` and destination components

Extract and organize the existing readiness, access, personal, employment, payroll, benefits, compliance, documents, history, transfer, and development panels. Existing domain panels remain canonical where already implemented.

### `EmployeeEditFlow`

Owns the existing snapshot, dirty keys, field availability, step validation, changed-field payload, approval response, catalog protection, and review-before-save behavior. A dedicated hook may isolate the edit draft, but the verified rules remain unchanged.

### `EmployeeCreateFlow`

Owns create-only duplicate guidance, quick/full setup choice, draft/resume, sequential steps, and post-create transition. It does not share an edit state machine merely because it shares the shell.

### `useEmployeeRecord(employeeId)`

Starts employee detail and readiness concurrently but settles them independently. Detail controls the workspace identity and shell; readiness has its own skeleton, failure state, and Retry and cannot blank a valid record. Every response is guarded by requested employee identity or cancellation generation, and one `refreshRecord()` refreshes both after mutations. It must never show employee A's stale data after navigation to employee B. Destination-specific panels may continue to load their own data and errors independently.

No backend schema or database migration is expected for this consolidation. Existing employee detail, readiness, update, status, activation, document, transfer, duplicate, completion, and access APIs remain authoritative. Before implementation acceptance, every API consumed by the workspace must be audited against the permission matrix below. Any endpoint that relies only on authentication, broad roles, or client-side visibility receives an explicit server-side permission gate before the workspace is accepted.

## 7. URL compatibility and navigation

Canonical record links use the new routes. Existing inbound links such as `/people?employeeId=901&tab=package` remain temporarily supported through a client-side replace/redirect to `/people/901?section=pay-benefits&panel=package` or the closest canonical destination.

The directory URL is the canonical return-state transport. Its supported query schema is `search`, `status`, `readiness`, `access`, `importBatchId`, `gapType`, `view=current|ex`, `page`, and `anchor=<employee-link-id>`. Employee links encode that complete internal path as `returnTo`; decoding must reject another origin, a non-`/people` pathname, credentials, protocol-relative paths, or malformed input. Bulk selection and raw scroll pixels are never serialized. The anchor restores the nearest visible employee link and focus after directory data loads.

Update link producers in application search, Payroll, Compliance, New Hires, package/benefits views, succession, and other modules. Keep a compatibility test until all known producers and bookmarked links have migrated.

History behavior:

1. directory → employee view creates a history entry;
2. view → edit creates an edit entry;
3. Back from clean edit returns to view;
4. Back from view returns through native history; **Back to People** uses only the validated `returnTo` path and otherwise `/people`;
5. Forward restores employee, destination, and mode;
6. destination changes inside the same edit flow do not prompt, but a dirty departure invokes one reusable employee-navigation guard for links, imperative routing, Back/Forward, shell navigation, company switching, sign-out, refresh, and close/tab unload where browser limits allow;
7. a persistent `NavigationGuardProvider` lives above the application shell's pathname-keyed `<main>` so route remounting cannot discard an active draft before the decision; the editor registers/unregisters dirty state with that provider;
8. the provider queues one intended destination/action, opens the shared accessible confirmation modal asynchronously, and resumes through a one-shot bypass after confirmation so history or router interception cannot loop; cancelling restores the current URL and focus;
9. company switching and sign-out call the same provider contract; refresh/close uses the browser's native unload prompt because custom asynchronous UI is unavailable there.

## 8. Permissions and sensitive information

The server remains decisive. Effective permission claims plus tenant, company, and employee scope are authoritative; frontend role-name allowlists are not an additional security gate. A custom role carrying `employees.write` receives the same edit capability as another in-scope writer, while a named role without that claim does not. The backend and frontend must enforce the same decision. This intentionally replaces the current frontend-only named-role restriction and its obsolete read-only custom-role test.

| Capability | Required authority |
| --- | --- |
| List or view active records and the read-only Ex-Employees archive | `employees.read` |
| Create, edit, duplicate resolution, employee-completion request, status change | `employees.write` plus tenant/company/employee scope |
| Activate or terminate | `employees.approve` |
| View sensitive payroll/identity values | `employees.read` plus `employees.sensitive` |
| Mutate sensitive payroll/identity values | the action's write permission plus `employees.sensitive` |
| Upload, update, verify, or reject a document | `employees.documents` |
| Archive a document | `employees.approve` |
| Request a transfer | `manager.approve` |
| Decide an HR transfer | `employees.approve` |
| Issue self-service access | `employees.access.issue` |
| Reset self-service access | `employees.access.reset` |
| Delete an employee | `employees.delete` |
| Import preview or import | `employees.bulk_import` |
| Bulk activate | `employees.approve` |
| Bulk deactivate | `employees.write` |
| Bulk delete | `employees.delete` |
| Bulk export | `employees.write` |
| Bulk issue access | `employees.access.issue` |

Unauthorized controls are absent rather than merely disabled, the browser emits no unauthorized mutation request, and a direct request to every protected API returns 403. Sensitive values are masked or omitted and never enter mutation payloads, review deltas, logs, or client caches. Deletion is not exposed to a user lacking deletion authority.

- Feature-disabled destinations explain availability only to authorized administrators; ordinary users do not see a broken destination.
- A permission change during editing blocks submission, preserves the draft for review, and explains the changed authority.

## 9. Accessibility and interaction contract

- Use page landmarks for the route-backed workspace; do not assign modal semantics.
- Focus the workspace heading on entry and the destination or step heading after navigation.
- Returning to the directory restores focus to the triggering employee link when possible, otherwise to the directory heading or search.
- Use `<nav aria-label="Employee record sections">` with links and `aria-current="page"`; do not simulate tabs unless the UI implements the full keyboard tab pattern.
- Nested actions such as add work email, discard, delete, and constrained fixes use the shared accessible modal. The child modal alone traps focus; Escape closes only the top layer and returns focus to its trigger.
- Validation provides an announced summary linked to the affected fields and focuses the first invalid field when appropriate.
- Loading, save, upload, approval, and failure states use suitable live regions without duplicate announcements.
- Preserve hidden/inert inactive wizard panels, reduced motion, visible focus, logical RTL ordering, translated labels, and input preservation on failure.
- Every new or moved label, empty state, validation message, destination name, and action is translated in English and Arabic. Layout uses logical properties and directional icons with explicit RTL behavior.
- Controls meet or closely approach 44px targets without reducing dense table readability.
- The workspace has one primary vertical content scroller; destinations and panels must not introduce a competing full-height vertical scrollbar.

## 10. Error and concurrency behavior

- Open the workspace immediately with a stable identity-aware skeleton and `aria-busy`.
- Never display the previous employee while a new employee is loading.
- Detail 404, forbidden, network failure, and invalid ID each receive a clear recoverable state with Retry and Back to People.
- Readiness or destination-panel failure does not blank the record header or unrelated destinations.
- Lookup or catalog failure cannot silently replace a stored value with an empty editable option.
- Save/upload pending states prevent duplicate submission and unsafe departure while keeping readable context.
- Uncertain or partial outcomes state exactly what was saved, submitted for approval, rejected, or still needs retry.

## 11. Preserved business rules

The redesign must retain these regression invariants:

- HR/IT enters the complete IT-issued work email; conflict suggestions are advisory and never auto-applied.
- Department selection continues to populate the cost center and constrain manager choices.
- Full setup cannot jump to Review until Payroll, Salary, and Identity have been visited.
- Arabic-name suggestions remain explicit Use/Dismiss actions with a safe manual fallback.
- Salary range, breakdown, allowances, deductions, effective dates, and approval semantics remain unchanged.
- Field-catalog updates do not erase draft edits.
- Duplicate checks, readiness, completion, access issuance, document uploads, transfers, benefits, compliance, history, and approval workflows remain available according to permission and feature state.
- No table, entity, or database migration is introduced solely for this UI consolidation.

## 12. Delivery sequence

1. Inventory every directory and workspace endpoint against the action matrix, add missing server-side gates and 403 regressions, and freeze the compatibility tests before UI extraction.
2. Add focused workspace routing and compatibility redirects without changing domain payloads.
3. Extract directory and record-loading responsibilities from `EmployeesPage.tsx` while preserving current behavior behind tests.
4. Introduce the workspace shell, header, navigation, responsive layout, loading/error states, and full-width directory.
5. Move existing profile panels into their canonical destinations and individually gate actions.
6. Move edit into the workspace and remove the duplicate drawer transition.
7. Move creation into the workspace and preserve verified draft/review behavior.
8. Migrate all internal employee links and retain legacy-link coverage.
9. Remove obsolete drawer state, copy, layout, and tests only after replacement coverage passes.

Work must be isolated from unrelated dirty changes. The implementation plan must identify exact file ownership and test gates before editing the shared employee surface.

## 13. Verification and acceptance

### Required browser scenarios

- Open an employee by mouse, keyboard, direct URL, and legacy URL.
- View → edit → save/pending approval → view without leaving the workspace.
- Clean and dirty Back/Forward, shell navigation, company switching, Escape in nested dialogs, and refresh.
- Rapid employee route changes with delayed detail/readiness responses.
- Reader, writer, no-sensitive, custom-role, and action-specific permission personas.
- Long documents/history content at 1440×1000, 390×844, and 320 CSS px/200% zoom.
- English and Arabic/RTL, light/dark, keyboard-only, and reduced-motion passes.
- Guided-next-action accuracy, blocker/recommendation distinction, field-level help, and approval/effective-date explanation.
- Motion timing, interruption, focus stability, no layout shift, and reduced-motion equivalence.
- Detail, readiness, destination, save, approval, and upload failure recovery.
- Creation draft/resume, sequential setup, review, and successful transition to the record.
- New Hires maker-checker behavior for write-only and approve-only personas.
- Ex-Employees archive search/pagination and the absence of mutable active-record actions.
- Destination-scoped edit and explicit full-record guided edit, each reaching Review before any mutation.
- Custom roles governed by effective permission claims rather than frontend role names, including allowed and denied in-scope/out-of-scope cases.

### Automated coverage

- Add a focused `employee-workspace.spec.ts` for routing, directory query serialization, hostile/malformed `returnTo` rejection, history, permissions, independent loading/error states, race protection, nested dialogs, every dirty-departure path, one-shot guard resumption, responsiveness, keyboard behavior, focus restoration, and accessibility.
- Rewrite `employee-edit-wizard.spec.ts` around workspace continuity. Remove only the obsolete drawer expectation; cover both destination-scoped and full-record guided entry and prove both reach Review before any mutation; preserve draft retry, document upload, approval, catalog, race, and validation coverage.
- Update create, access, onboarding completion, benefits, package, deductions, succession, and truthfulness tests that currently assume the drawer or query-only deep link.
- Preserve and adapt `employee-create-country-gate`, `employee-create-wizard`, `employee-edit-draft`, `employee-import-preview`, New Hires review, command-palette routing, and sidebar active-route coverage. Replace the obsolete custom-role-name edit assertion with effective-permission and scope cases.
- Add backend authorization tests proving a caller without `employees.read` receives 403 from the employee list, Ex-Employees archive, employee detail, documents, history, readiness, and every destination API; add action-matrix 403 tests for every directory and workspace mutation.
- Run TypeScript, production build, focused browser suites, affected frontend unit suites, employee-access security tests, and an axe pass for the workspace and nested dialogs with zero serious or critical violations.
- At 390×844 and 320 CSS px/200% zoom, assert that Save/Cancel clears the mobile bottom navigation, assistant launcher, safe area, focused input, and virtual-keyboard viewport; do not accept a screenshot-only judgment.
- In Arabic, assert document `dir="rtl"`, logical Back/directional icons, destination navigation, sticky actions, and keyboard focus order.
- Assert exactly one workspace-height vertical scroller while long Documents and History content scrolls and the header/navigation/actions remain reachable. A bounded list inside a child dialog may scroll, but it cannot become a competing workspace-height scroll region.
- Assert that animations are interruptible, do not delay actions, preserve focus, avoid measurable layout shift, and are reduced to an equivalent instant state under `prefers-reduced-motion`.

### Acceptance criteria

- No persistent employee profile drawer exists on `/people`.
- Every active employee-record entry path reaches the same addressable workspace; New Hires remains its protected queue and Ex-Employees remains its read-only archive.
- View, add, edit, readiness resolution, and permitted lifecycle actions remain available without a competing record surface.
- Only one primary content region scrolls; section navigation and dirty actions remain reachable.
- Returning to People restores directory state and focus.
- Permissions and sensitive masking prevent unauthorized controls and mutations.
- Dirty-departure confirmation, keyboard-only completion, and focus restoration pass for links, Back/Forward, shell navigation, company switching, sign-out, refresh, and nested dialogs.
- Existing employee creation/edit business rules pass unchanged.
- Desktop, mobile, zoom, keyboard, Arabic/RTL, reduced motion, loading, error, and concurrency evidence is captured from the implemented UI.

## 14. Approval boundary

Owner approval of this written specification permits creation of a detailed implementation plan. It does not authorize skipping test-first implementation, overwriting unrelated work, merging, deploying, or claiming production acceptance.
