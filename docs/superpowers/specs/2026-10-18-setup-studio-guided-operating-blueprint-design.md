# Setup Studio Guided Operating Blueprint

Status: the two-path model, adaptive milestone approach, C+B visual direction, and shared twelve-area pattern were approved in conversation on 2026-10-18. This written specification is awaiting owner review. It authorizes no implementation, merge, deployment, production change, policy publication, or Evostel data mutation.

## 1. Purpose and audience

Setup Studio must support two materially different administrator jobs without creating two sources of truth:

1. **Guided Setup & Major Update** prepares coordinated work across selected HR areas, keeps proposed changes separate from live configuration, and provides an explicit review path.
2. **Configuration Center** gives authorized administrators direct, ongoing access to individual live settings through their existing canonical forms.

The primary users are Group Administrators, company HR administrators, implementation consultants, and authorized domain specialists. They arrive either to coordinate a substantial setup/change programme or to maintain one known setting. They must always understand:

- which company or tenant scope they are working in;
- whether they are editing a draft or live configuration;
- which permissions and product capabilities apply;
- what has actually been saved or applied;
- how to return to the relevant area without losing work.

The experience must feel like governed enterprise configuration, not an AI sandbox, a creative studio, or a second HR application.

## 2. Product outcome

An authorized administrator can enter Setup Studio, select the correct scope, choose either guided work or direct maintenance, navigate any of the twelve setup areas, and open the relevant existing form or operational destination without long page scrolling or detached actions.

Guided work adds draft organization, factual status, source-assisted preparation, and review. Configuration Center provides direct maintenance. Both paths use the same destination registry, form schema, record loading, validation, authorization, and canonical records. Their mutation actions remain deliberately different: Guided saves a draft and later applies only reviewed, supported changes; Configuration Center saves live configuration directly.

Success means:

- no duplicated settings forms or parallel configuration tables;
- no need to scroll to the bottom of an area to reach another setting;
- actions remain adjacent to the object they affect;
- one selected editor occupies the full working width;
- browser history, deep links, focus restoration, and dirty-state protection work predictably;
- document-assisted values enter the same existing forms and remain reviewable before save;
- policy publication remains distinct from operational configuration;
- unsupported capabilities are described honestly rather than represented by fake controls.

### 2.1 Terms and action contract

- **Area:** one of the twelve Setup business domains.
- **Business group:** a stable purpose-based subdivision of an area used only for navigation.
- **Destination:** one entry in the shared registry, representing a setting, template, or related operation.
- **Guided task:** a draft-specific instance of one destination selected into Guided work.
- **Proposal/change:** one staged create or sparse field/record mutation with an explicit target and commit strategy.
- **Draft saved:** recoverable guided inputs were checkpointed; live configuration did not change.
- **Applied:** the applicable authoritative mutation completed under its verified contract. Readback is required only where that destination contract supports it.
- **Stage:** a legacy route alias used during migration, never a visible milestone or completion sequence.

The user-facing actions are distinct:

- `Save draft` checkpoints Guided work and performs no operational write.
- `Save` in Configuration Center invokes the destination's existing live mutation workflow.
- `Apply batch proposals` invokes the existing atomic Setup Apply contract for its supported proposal group.
- `Apply <destination> changes` invokes one verified destination-scoped canonical mutation. V1 provides no single action spanning the batch group and canonical destination saves.

## 3. Approved information architecture

### 3.1 Setup Studio home

The Setup entry page keeps the existing KynexOne application shell and presents, in order:

1. **Page identity**
   - Title: `Setup Studio`.
   - Explanatory copy states that administrators can prepare reviewed changes or maintain live configuration.
2. **Configuration scope**
   - Selected authorized company, or a verified tenant-shared direct-configuration context where the destination actually supports it.
   - A concise permission/scope explanation.
   - Guided work requires an authorized concrete company in V1. Group baseline adoption/inheritance remains deferred.
   - Scope selection precedes starting or resuming guided work.
3. **Two primary paths**
   - `Guided Setup & Major Update`.
   - `Configuration Center`.
   - The paths receive equal navigational importance and explicitly distinguish draft preparation from live maintenance.
4. **Active guided work**
   - At most one real recoverable browser-local draft per selected company and intent in V1.
   - Optional draft label, intent, persistence type, and last saved time. No unsupported owner or collaboration metadata is shown.
   - Browser-local work is labelled `Stored in this browser`; it is never presented as collaborative or cross-device.
5. **Supporting setup tools**
   - Policy library.
   - Organization import.
   - Administration settings.
   - These remain supporting destinations rather than a third primary workflow.

The home does not display a setup-completion percentage. Existing records are not proof that the company is fully configured.

### 3.2 Guided Setup & Major Update

Guided work uses an **adaptive milestone blueprint**, not a numbered wizard, mandatory sequence, or draggable Kanban board.

A short start flow collects:

- authorized scope;
- intent: initial setup or major update;
- optional draft label;
- selected areas;
- optional source choice: manual, existing library documents, or new documents;
- optional target effective-date default. It appears only for supported proposal types, remains visible and overridable per proposal, and is never sent to destinations without a verified effective-date contract.

Guided V1 is company-context work. A tenant-shared destination may remain visible in the blueprint, but it is classified `Direct configuration only` and excluded from the company draft and its counts unless a future adapter explicitly supports tenant-targeted Guided work under a separately approved scope contract. Its action opens Configuration Center with the impact statement `Applies to all companies`.

The guided draft then opens the Guided Operating Blueprint:

- selected areas appear as guided milestones;
- permitted unselected areas appear under `Add areas` or with the explicit state `Not in plan`; they do not contribute to counts;
- the selected area becomes a grouped business-purpose canvas;
- factual task states appear on destination nodes;
- the right rail shows blockers and recommended next actions;
- the guided-draft bar shows factual counts and draft/live status;
- selecting a node opens one focused canonical setting workspace.

Each destination resolves three independent axes in this precedence order:

1. **Access/availability:** available, restricted, not enabled, or availability unknown. Any non-available result suppresses all capability and workflow actions, excludes the destination from counts, and shows only the explanation or safe retry.
2. **Capability:** staged canonical, verified batch, direct-configuration-only, review-only, or related operation. Direct-only, review-only, and operations remain outside Guided proposal/apply counts even when available.
3. **Workflow:** resolved only for available destinations with staged-canonical or verified-batch capability.

The authoritative workflow states are:

| State | Source and entry rule | Count behavior and available action |
| --- | --- | --- |
| Not in plan | Destination is permitted but not selected | Excluded; `Add to draft` |
| Not assessed | Selected task has no saved proposal, explicit disposition, or verified result | Excluded from ready/applied counts; `Open task` |
| Draft in progress | Local checkpoint contains edited inputs/proposals for the task | Count as draft; `Continue` |
| Needs information | Verified validation reports missing required input | Count as blocker; `Resolve` |
| Conflict to resolve | Verified proposal contains conflicting evidence or baseline | Count as blocker; `Review conflict` |
| Ready for review | Checkpoint contains a complete proposal that passes its verified pre-review validation | Count as proposed; `Review` |
| Applied | Current Apply aggregate or destination-specific save result proves success under that contract | Count as applied; `View result` |
| Result requires verification | A batch response proves aggregate success but cannot deterministically prove this destination/task result | Excluded from task-level applied count; `Verify configuration` |
| Failed | Current mutation result reports failure and retry semantics are known | Count as failed; `Retry` only when safe |
| Deferred / Not applicable | Explicit user disposition with reason, stored in the local draft | Count separately; `Reopen` |

`Ready to start` is presentation copy for a selected `Not assessed` task, not a persisted state. `Applied with warning` is shown only when an existing verified response contract supplies both success and a warning; otherwise the UI displays the exact aggregate or destination-specific result.

Recommended next action is deterministic: after access and capability precedence, choose the earliest selected stageable task in registry order with a blocker, then a reviewable proposal, then an in-progress draft, then an unassessed selected task. Restricted, unavailable, direct-only, review-only, operational, and unselected destinations cannot become the recommendation. If no actionable task exists, the rail explains why instead of manufacturing a recommendation.

Removing an area with no checkpointed work is immediate. Removing an area containing proposals, sources, or dispositions requires confirmation and removes only that area's local draft material; it never deletes uploaded library sources or live configuration.

Opening a task, visiting an area, uploading a document, or successfully parsing a source never marks configuration complete.

The blueprint uses factual counts such as `8 proposed changes`, `2 blockers`, and `3 applied records`. It does not use artificial progress percentages or `setup complete` language.

### 3.3 Configuration Center

Configuration Center is the permanent direct-maintenance path.

It uses the same twelve-area menu and the same business-purpose grouping as Guided Setup, but replaces guided-draft status with live configuration metadata:

- actual scope;
- availability;
- permission state;
- real record count where available;
- last modified information where available;
- document assistance availability;
- canonical setting or operational destination.

Selecting an available setting opens its existing focused form. The complete destination row or node is the action; there is no visually detached generic `Open settings` button.

Related operational destinations such as employee records, leave operations, payroll operations, contract renewals, and GOSI filing continue to open their established routes. Setup Studio does not absorb them into replacement forms.

### 3.4 Shared twelve-area pattern

The shared interaction pattern applies to:

1. Organization and core HR.
2. Recruitment.
3. Onboarding and confirmation.
4. Time and attendance.
5. Leave and absence.
6. Compensation and benefits.
7. Payroll.
8. Performance and talent.
9. Learning and development.
10. Employee services and relations.
11. Offboarding and settlement.
12. Compliance and statutory administration.

The shell is consistent; capability is not assumed to be uniform. Each area exposes only verified settings, templates, and operations. An area with no reusable policy editor must say so and link to the real manual or operational path. It must not receive invented controls merely to make the blueprint look complete.

The main application sidebar is outside this redesign. People, Payroll, Attendance, Leave, and other operational modules retain their existing navigation and workflows.

### 3.5 Authoritative area and business-group matrix

This matrix governs the initial composition. Registry entries retain their current scope, permission, feature, presentation, and assistance metadata; implementation must not infer broader capability from the group name.

| Area | Business group | Destinations in display order | Current presentation boundary |
| --- | --- | --- | --- |
| Organization and core HR | Legal entities and structure | Companies; Branches; Departments; Designations; Grades | Existing embedded Setup forms |
| Organization and core HR | Financial structure | Cost centers; Cost centres and budget | Existing embedded Setup forms |
| Organization and core HR | Employee data definitions | Employee Reference Lists | Existing embedded Setup form |
| Organization and core HR | Related operations | Employee records and contracts; Contract renewals | Canonical operational routes |
| Recruitment | Recruitment operations | Recruitment and workforce planning | Canonical operational route; no generic embedded policy editor claimed |
| Onboarding and confirmation | Templates and tasks | Onboarding checklists | Existing onboarding template surface; assistance limited to verified onboarding behavior |
| Time and attendance | Work configuration | Locations and geofences; Shift definitions and policy | Existing Setup form or canonical route |
| Time and attendance | Related operations | Attendance operations; Overtime requests; Project timesheets | Canonical operational routes |
| Leave and absence | Policy definitions | Leave types; Leave policies; Holiday calendars | Existing Leave forms/routes; adapter capability remains destination-specific |
| Leave and absence | Related workspace | Leave policies and holidays | Canonical Leave route |
| Compensation and benefits | Plans and entitlements | Benefit plans and enrollments; Benefits by grade; Loan and advance policies; Merit planning and benchmarks | Canonical module routes |
| Payroll | Posting and calendars | GL and rates; Fiscal years | Existing embedded Setup forms |
| Payroll | Payroll configuration | Payroll and salary structures; Payslip templates | Canonical module routes |
| Performance and talent | Cycles and templates | Performance cycles and templates | Canonical settings route |
| Performance and talent | Related operations | Succession and development | Canonical operational route |
| Learning and development | Catalogue and rules | Learning catalogue and rules | Canonical settings route |
| Employee services and relations | Templates and communication | Engagement templates and privacy; Notification templates | Canonical route or existing embedded Setup form |
| Employee services and relations | Related operations | Request center; HR letters | Canonical operational routes |
| Offboarding and settlement | Exit operations | Offboarding and settlement | Canonical operational route; no generic embedded policy editor claimed |
| Compliance and statutory administration | Rules and profiles | GCC settings; Statutory rules; Compliance profiles; Tax policies | Existing embedded Setup forms or canonical settings routes |
| Compliance and statutory administration | Filings and evidence | Documents and renewals; Saudi compliance; GOSI filing; Administrative audit logs | Canonical operational routes or existing audit-log surface |

The registry adds `businessGroup`, `groupOrder`, and `destinationOrder`. It also records the independent capability dimensions defined in section 6. Any later destination or grouping change must update this matrix or its generated successor and corresponding tests; implementers do not invent groups locally in components.

## 4. Approved visual and interaction direction

The approved direction combines:

- **Operating Blueprint:** a visual but restrained business-area canvas, grouped by purpose, with truthful node states and a focused next-action rail.
- **Twin Workspaces:** an unmistakable distinction between reviewed guided work and live configuration maintenance.

The visual world remains the established KynexOne enterprise system:

- existing application shell and sidebar;
- sapphire accent, slate text, restrained neutral surfaces;
- workhorse UI typography and Arabic-compatible type;
- borders and hierarchy rather than decorative card stacks;
- color reserved for selection and semantic state;
- familiar controls and restrained task-focused motion.

The blueprint must not become a decorative node graph. It does not draw guessed dependency lines. Business-purpose groups, destination nodes, states, and next actions are all derived from verified product metadata or saved work.

### 4.1 Desktop composition

The main desktop hierarchy is:

1. KynexOne application shell.
2. Setup title and selected scope.
3. Guided/Configuration path switch.
4. Guided-draft bar when in Guided mode.
5. Internal Setup area menu.
6. Selected area canvas with grouped destinations.
7. Next-action rail in Guided mode, or contextual live metadata in Configuration mode.
8. Supporting setup tools on Setup home, outside focused task work.

The internal workspace has two exclusive states:

- **Blueprint state:** area menu, grouped area canvas, and contextual rail.
- **Focused setting state:** all three internal columns are replaced by one full-width canonical editor.

The three-column Blueprint state exists only where viewport width supports it. No hidden area canvas, rail, or inactive editor remains mounted beneath the Focused setting state.

### 4.2 Focused setting workspace

Opening a setting produces one exclusive focused view:

- Back to the owning area.
- Current path and scope.
- Plan/draft status when entered through Guided Setup.
- Setting title and truthful capability statement.
- The existing canonical editor.
- In Guided staged mode, the editor reuses current values, field definitions, and validation but exposes `Save draft`; it does not expose or invoke the live `Save` action. A direct-save-only destination exits to Configuration Center instead of masquerading as staged work.
- Previous and next available task actions where useful.
- Access to all areas through a full navigation surface rather than a narrow nested dropdown.

The area canvas and another editor are not mounted underneath the focused form. Only one embedded editor may mount or fetch at a time.

Back restores the prior area position and focus to the originating destination. Browser Back and Forward reproduce the same hierarchy.

On desktop, `Browse all areas` opens a large labelled directory dialog rather than a sticky nested rail or compact dropdown. Initial focus moves to the dialog heading, focus remains within it, and Escape or Close returns focus to the trigger when no dirty-state decision is pending. Opening/closing the directory does not change history. Selecting an area first passes the dirty-state guard; after success it closes the dialog, creates the area navigation entry, and focuses the new area heading.

### 4.3 Responsive behavior

At narrow widths:

- the two paths stack vertically;
- the area menu becomes a labelled full-screen navigation dialog;
- the blueprint becomes a single-column grouped list;
- the next-action rail becomes an inline `Next actions` section before the first task or after the area summary;
- focused forms use the full viewport width;
- review comparisons stack `Current` and `Proposed` values;
- previous/next actions are available after long forms as well as near the heading;
- no twelve-tab horizontal strip or competing nested horizontal scroller is introduced.

The design must work at 390 px, 320 CSS px, 200% zoom, and 400% reflow without hidden actions or obscured fields.

The full-screen mobile navigator focuses its heading on open, contains focus while open, closes with its explicit Close control or Escape when no dirty-state decision is pending, and restores focus to its trigger. Selecting an area closes the navigator and focuses the resulting area heading. Directional placement and icons mirror in RTL while DOM and keyboard order remain logical.

## 5. Navigation and route contract

The redesign adds one route-state owner and preserves existing deep links during migration.

Recommended canonical query states:

```text
/setup?tab=aiSetup&path=configuration&scope=company&companyId=<authorized-id>
/setup?tab=aiSetup&path=configuration&scope=company&companyId=<authorized-id>&area=organization
/setup?tab=aiSetup&path=configuration&scope=company&companyId=<authorized-id>&area=organization&setting=companies

/setup?tab=aiSetup&path=guided&scope=company&companyId=<authorized-id>&intent=initial&view=board
/setup?tab=aiSetup&path=guided&scope=company&companyId=<authorized-id>&intent=majorUpdate&view=task&area=leave&setting=leavePolicies
/setup?tab=aiSetup&path=guided&scope=company&companyId=<authorized-id>&intent=majorUpdate&view=review
```

V1 permits one browser-local draft per tenant, user, company, and immutable intent, so no `planId` is claimed. A future durable multi-plan design must add an authorized opaque plan identifier rather than overloading the local draft route.

The canonical Guided views are `board`, `task`, and `review`. Legacy stage values remain compatibility aliases only:

- `company`;
- `work`;
- `people`;
- `rewards`;
- `review`.

These values preserve current compatibility; they canonicalize to the closest Guided view and are never presented as a five-step completion wizard.

Scope is explicit:

- `scope=company` requires an authorized `companyId` and is the only V1 Guided scope.
- `scope=tenant` is permitted only in Configuration Center for destinations verified as tenant-shared.
- `scope=group` is reserved and unavailable until group baseline/inheritance receives a separate approved design.
- Missing, conflicting, or unauthorized scope returns to the scope picker with an explanation. It never defaults to all companies.

Compatibility rules:

- `mode=starter` resolves to Guided Setup with initial intent only when canonical `path` is absent.
- Existing `area` and `setting` URLs resolve to Configuration Center.
- Existing direct setting tabs remain compatibility aliases to the same canonical forms.
- `tab=policyLibrary` remains the policy library.
- `tab=importOrganization` remains organization import.
- `companyId` remains an explicit authorized company target and is ignored for verified tenant-shared destinations only after canonicalization.
- Destination-specific parameters such as `department`, `level`, and `setupArea` are preserved.
- Invalid route combinations resolve safely without creating an additional history entry.
- Draft values, policy text, and document contents never enter the URL.

Deterministic precedence and canonicalization:

| Input combination | Resolution |
| --- | --- |
| `tab` is not `aiSetup` | Render that legacy/canonical tab and remove `path`, `intent`, `view`, `area`, and `setting` by `replace` |
| `path=configuration` with `intent`, `view`, or legacy `stage` | Ignore/remove Guided-only parameters by `replace` |
| `path=guided` with `view=board` | Ignore/remove `area` and `setting` |
| `path=guided` with `view=task` | Require a selected in-plan `area` and compatible `setting`; otherwise return to the board with an explanation |
| `path=guided` with `view=review` | Ignore/remove `area` and `setting` |
| `mode=starter` plus canonical `path` | Canonical `path` wins; remove `mode` |
| `mode=starter` plus legacy `area`/`setting`, with no canonical `path` | `mode=starter` wins; if the recovered draft contains the valid selected task, canonicalize to `view=task` and retain it; otherwise canonicalize to `view=board` and remove `area`/`setting` |
| `setting` does not belong to `area` | Resolve to that area's index with a permission-safe explanation |
| invalid or unauthorized scope | Resolve to scope picker; never infer a broader scope |

Path, view, area, setting, and authorized scope transitions create history entries. Intent is immutable within a local draft; choosing another intent switches to or creates the separate company+intent draft after dirty-state protection, and that draft switch creates the history entry. Compatibility cleanup and invalid-state recovery use `replace`. Dialogs, filters, source expansion, and review accordions do not create history entries.

## 6. Shared destination architecture

The two paths are shells over one typed destination registry and one canonical form host.

Each destination records:

- stable ID;
- owning area;
- label and description;
- canonical route;
- renderer key for embedded forms;
- kind: setting, template, or operation;
- tenant, company, or mixed scope;
- allowed Guided context scopes and deterministic effective target-scope resolution;
- read/write permissions and allowed roles;
- feature requirements;
- presentation: embedded canonical form or external route;
- business group and deterministic display order;
- supported Guided intents;
- input-assistance modes;
- target-operation modes;
- commit strategy;
- current batch-family mapping, when the existing Apply response can prove it;
- actual save owner;
- task-state derivation adapter, when one exists.

Guided capability dimensions are independent and destination-specific:

```ts
type GuidedIntent = 'initial' | 'major-update';
type InputAssistance = 'manual' | 'document-prefill';
type TargetOperation = 'create' | 'update';
type CommitStrategy =
  | 'staged-canonical-save'
  | 'verified-batch-apply'
  | 'direct-save-only'
  | 'review-only'
  | 'unavailable';
```

A destination may support more than one intent, assistance mode, or target operation. Automated major update requires a verified update target, sparse preservation, safe baseline/concurrency behavior, and a commit strategy other than `direct-save-only`, `review-only`, or `unavailable`. A create proposal requires stable proposal identity and a server-side nonexistence/idempotency check.

In V1, `allowedGuidedScopes` is company only. Destinations whose effective target scope is tenant-shared are direct-configuration-only from a company Guided draft. In Configuration Center, the company remains the browsing/return context while the destination header and server request use the registry's effective tenant target and state `Applies to all companies`; the UI never implies a company override or silently broadens authorization.

Discovery does not imply edit permission. Visibility does not imply document assistance. Document assistance does not imply batch Apply. A route does not imply that Setup Studio owns the operational workflow.

The registry consolidates current duplicated knowledge across Setup tabs, direct-setting order, and the twelve-area catalogue. The canonical form renderer remains the single composition seam. This design does not require moving all form implementations before the new shell can be introduced.

The same-form requirement applies only to destinations with a verified reusable embedded editor and a compatible commit strategy. Direct-save-only, review-only, unavailable, and operational-route destinations open their established workspace and retain its API contract.

## 7. Guided draft and configuration truth

The current product has several different objects that must not be conflated.

### 7.1 Guided journey draft

The existing browser-local setup draft stores recoverable user inputs and progress. V1 supports at most one local draft per tenant, user, company, and immutable intent. The local schema adds only the fields the approved shell can truthfully own: optional label, intent, selected areas, explicit task dispositions/reasons, task checkpoint references, created/updated timestamps, and last active area/task. Proposal bodies and source references retain their existing compatible structures.

Existing v1 local drafts are migrated defensively:

1. Discover the current tenant/user/company key.
2. Validate it using the existing draft lifecycle.
3. Treat a valid legacy draft as `initial` intent.
4. Preserve its persisted reviewed preview, inputs, and uploaded source references.
5. Write the intent-qualified key only after validation succeeds.
6. Retain the old key as recovery input until the new checkpoint is confirmed readable; failed migration leaves the legacy draft accessible.

Unuploaded browser `File` objects are not persisted and cannot be recovered or migrated after reload. If an unuploaded file exists in the currently mounted session, navigation remains blocked until the user uploads or removes it; the UI makes no post-reload recovery claim.

It is not:

- a server-side change set;
- collaborative work;
- cross-device storage;
- a maker/checker approval record;
- proof of committed configuration.

### 7.2 Prepared proposal snapshot

Preview/extraction can prepare proposed values with source evidence. A proposal remains a draft until either `Apply batch proposals` or one destination-scoped Guided Apply succeeds under its verified contract.

Source identity, version/hash, passage, destination field, target record identity, scope, and the loaded baseline accompany a proposal where the capability supports them. Automatic stale-target detection is claimed only by an adapter that defines target identity, baseline value/version, re-read point, and conflict behavior. Other proposals must re-enter the canonical workflow for validation and must not display an automatically derived stale state.

Missing source content never means zero, false, blank, delete, or permission to use a form default. Explicit zero and false remain valid proposed values.

### 7.3 Operational configuration

Canonical domain records remain authoritative. Guided and Configuration actions remain distinct:

- Guided `Save draft` checkpoints proposals and performs no operational write.
- Configuration Center `Save` invokes the destination's established live API.
- Guided `Apply batch proposals` invokes only the verified atomic Setup Apply group.
- Guided `Apply <destination> changes` invokes one verified destination-scoped canonical mutation at a time.

A guided task applies only through:

- the canonical form's existing create/update API; or
- the existing Setup Apply endpoint for a specifically verified supported record family.

The existing Apply endpoint is not treated as a universal major-update engine. Each destination is classified as:

1. Canonical manual update.
2. Guided sparse-prefill update.
3. Verified supported batch apply.
4. Review-only/manual process.
5. Product gap.

Unsupported destinations remain available through their real manual workflow.

### 7.4 Policy review and publication

Policy review evidence does not change configuration, publish policy, send tasks, or mark a setting applied. Policy publication remains a separate authorized lifecycle.

The interface reports separate outcomes for:

- operational configuration;
- policy draft/review;
- publication;
- employee guidance.

## 8. Review and Apply behavior

Guided review is a grouped change register, not one large AI-generated summary.

Each proposed change displays, where supported:

- owning area and destination;
- target record identity;
- current value;
- proposed value;
- affected scope;
- effective date;
- manual or document source;
- source version and passage;
- dependency or conflict state;
- reviewer decision;
- capability and save owner.

Filters include blockers, conflicts, manual changes, document proposals, deferred work, ready changes, and failed results.

The review screen separates three commit groups before submission:

1. **Verified batch Apply:** only proposals supported by the current Setup Apply contract.
2. **Staged canonical saves:** destination adapters that can safely submit a reviewed proposal through the established domain API.
3. **Direct-save or review-only items:** shown outside the Apply count with an explicit route/action; never submitted as unsupported batch items.

V1 exposes separate commit actions and results:

- one `Apply batch proposals` action for the existing atomic batch group;
- one `Apply <destination> changes` action at a time for a verified staged canonical destination;
- no single button, count, success state, or retry operation spans those transaction models.

Each action names the actual operation and its supported count. It never says `Complete setup` and never includes direct-only or review-only items in its count.

Current batch Apply behavior remains authoritative: the existing endpoint performs one relational transaction, revalidates its supported draft families, and returns aggregate family counts and skip summaries. A failure rolls back the transaction; the UI does not present partially committed per-record results that the endpoint does not supply. Network uncertainty prompts a configuration re-read before any retry.

Every supported batch family maps in the registry to the Guided tasks it can prove. A task becomes `Applied` only when the response family/count plus the post-apply configuration evidence required by that mapping identifies the task deterministically. If the response proves only aggregate batch success, the batch result is shown separately and the task becomes `Result requires verification`; the UI does not infer task-level success.

For a staged canonical save, the destination adapter must define target identity, allowed sparse fields, baseline/concurrency rule, API request, success response, readback behavior where supported, and idempotent recovery. Without those facts the destination is direct-save-only or review-only.

Granular cross-destination partial commit, per-record batch outcomes, universal rollback, and coordinated retry require a separately approved durable change-set backend. This specification does not claim them.

## 9. Permissions, availability, and scope

The UI distinguishes:

- discovering/reading a destination;
- editing its canonical form;
- preparing values from policy;
- previewing guided changes;
- applying supported guided changes;
- recording policy review evidence;
- publishing policy.

Current action gates remain explicit rather than collapsed into one registry role:

| Action | Current minimum contract to preserve |
| --- | --- |
| Discover/read destination | Registry read permission, role/feature metadata, company scope, and server route verdict |
| Edit canonical form | Destination/domain write permission and server authorization |
| Extract policy proposals | `organization.write` plus authorized company/document access |
| Preview guided batch | Current controller role gate: Admin or HR Manager, plus validated scope |
| Apply guided batch | Current controller role gate plus `organization.setup.apply` and relevant domain permissions |
| Apply a Guided staged canonical destination | The destination API's existing server-enforced write permission, scope, validation, and concurrency contract; the adapter grants no new authority |
| Record policy review evidence | `organization.write` plus authorized company/document access |
| Publish policy | Existing separate policy-publication authority |

The current frontend/backend mismatch that exposes some policy controls to HR Officer while preview/apply remains Admin/HR Manager-only must not be normalized away. UI availability is the intersection of registry metadata and the current server action contract. A staged canonical adapter invokes the existing destination authority; it does not create a separate maker/checker permission. Any such authority requires backend design. Negative direct-API tests cover forged tenant, company, document, target, role, and action combinations.

Frontend discovery is the intersection of role, permission, feature, company scope, and server-supplied route availability. Server authorization remains decisive.

Restricted and unavailable destinations never mount their editor or issue its data requests. A feature-discovery failure is `Availability unknown`, not `Empty` or `Not configured`.

Customer-facing scope copy uses clear language such as `Applies to Evostel` or `Applies to all companies`. Internal `tenant-wide` terminology may remain in technical metadata but is not the only explanation presented to administrators.

Changing company scope:

- triggers dirty-state protection;
- never retargets a draft silently;
- invalidates incompatible prepared proposals only where the adapter can prove target/scope incompatibility; otherwise requires revalidation in the canonical workflow;
- does not reuse another company's draft;
- rechecks permission and feature availability.

## 10. Unsaved work, history, and focus

The existing application-level navigation guard remains the single authority for risky departures.

Protected states include:

- dirty canonical form;
- local files not yet uploaded;
- failed browser-draft persistence;
- edited prepared proposal not checkpointed;
- in-flight extraction or preview where cancellation has consequences.

The guard applies to:

- Guided/Configuration switching;
- area and setting changes;
- guided-view changes;
- company switching;
- supporting tools;
- application sidebar and command palette;
- browser Back/Forward;
- sign-out and refresh.

Cancel keeps the URL, current values, scroll position, and focus. Confirm performs the intended transition once.

After successful navigation:

- Setup home focuses its heading;
- path entry focuses the path heading;
- area entry focuses the area heading;
- setting entry focuses the canonical form heading or named region;
- Back restores focus to the originating area or destination row;
- guard cancellation restores the initiating control.

## 11. Accessibility, localization, and content

The shared pattern uses ordinary links, buttons, headings, regions, and lists. The blueprint is not implemented as an inaccessible canvas-only diagram or mandatory drag interaction.

Requirements:

- complete keyboard operation;
- visible focus;
- 44 px minimum touch targets where touch applies;
- semantic status text in addition to color;
- error summary linked to affected fields;
- announcements for asynchronous status changes without unexpected focus movement;
- unique accessible action names rather than repeated `Open settings`;
- reduced-motion support;
- no competing page-height scroll regions on mobile or at high zoom.

RTL behavior uses logical inline properties. Area order, breadcrumbs, chevrons, previous/next controls, and full-screen navigator direction mirror appropriately. Mixed Arabic/English codes, dates, IDs, and monetary values use bidirectional isolation. Long Arabic labels wrap without displacing status or actions.

The approved visible rename is `Employee Reference Lists` in place of `Master Data`. No additional module or area rename is authorized by this specification. Future terminology changes require separate owner approval.

## 12. States and recovery

Every primary view covers:

- loading with structured skeletons;
- empty state with an accurate next action;
- restricted access;
- disabled feature;
- availability unknown;
- recoverable API error;
- validation error;
- stale source or target;
- local-draft storage failure;
- atomic batch Apply failure with no committed batch records;
- destination-specific save failure;
- successful save with the evidence supported by that destination contract.

Read failure never renders as no records. A successful upload or extraction never renders as applied configuration. An uncertain write outcome prompts a destination-appropriate re-read rather than an automatic mutation retry. Per-record partial Apply is not shown for the current atomic batch endpoint.

## 13. Delivery strategy

This redesign is delivered incrementally to preserve compatibility and reduce risk.

### Phase 1: route and behavior characterization

- Freeze current URL, history, dirty-guard, company-scope, and direct-tab behavior with tests.
- Record the actual settings/operations inventory for all twelve areas.
- Make no visual change.

### Phase 2: shared route state and registry

- Introduce one parser/serializer for Setup route state and compatibility aliases.
- Consolidate the typed destination registry.
- Keep the existing canonical renderer and save APIs.

### Phase 3: Configuration Center shell

- Introduce Setup home, twelve-area directory, grouped area canvas, and focused setting view.
- Retain legacy links and external operational destinations.
- Prove desktop, mobile, keyboard, zoom, and RTL behavior.

### Phase 4: Guided blueprint shell

- Reuse the existing guided draft, source, extraction, preview, and supported Apply lifecycle behind the new Guided path; do not mount its visible five-step rail/footer as the primary navigation.
- Prove local checkpoint restoration parity, migrate valid legacy drafts, and then unmount the inactive Guided or Configuration shell rather than hiding it.
- Assert that inactive shells, editors, effects, and API reads are absent.
- Add intent isolation and truthful persistence labels.
- Replace false wizard completion language with adaptive milestone states.
- Preserve the existing supported preview/apply behavior without widening its claims.

### Phase 5: capability-gated form convergence

- Reuse canonical forms in Guided tasks one destination at a time.
- Add sparse-prefill adapters only where target identity, source evidence, baseline preservation, update semantics, and readback are verified.
- Label remaining destinations manual, direct-only, review-only, or product gap.

### Phase 6: honest Major Update

- Enable coordinated automatic preparation only for capability-verified destinations.
- Invalidate stale guided proposals after direct configuration changes only when the adapter supplies verified target identity and baseline comparison; otherwise force canonical revalidation.
- Keep current atomic batch results and destination-specific save results visible under their actual contracts.

Durable collaborative change sets, cross-device drafts, scheduled application, generic approval, rollback, and group inheritance require separate backend specifications. They are not hidden inside this UI redesign.

## 14. Acceptance criteria

1. Setup Studio exposes exactly two primary paths for an authorized selected scope.
2. All twelve Setup areas use the approved shared shell and expose only verified destinations.
3. Guided Setup uses an adaptive milestone blueprint and never requires a false numbered completion sequence.
4. For destinations with a verified reusable embedded editor, Configuration Center and Guided Setup render the same canonical form schema, current records, and validation. Direct-only, review-only, and operational destinations retain their established workspace.
5. `Save draft`, Configuration Center `Save`, `Apply batch proposals`, and destination-scoped Guided Apply remain visibly and technically distinct; V1 exposes no combined commit action across transaction models.
6. Only one embedded editor mounts and fetches at a time.
7. The complete destination row/node is the action; no detached generic action remains at the far edge of a wide page.
8. Opening a destination reveals its heading without manual up-scroll.
9. Back returns to the correct area, scroll position, and originating focus target.
10. Legacy Setup URLs and direct setting tabs remain functional during migration.
11. Browser Back/Forward reproduces Setup home, path, area, and setting state without duplicate entries.
12. Dirty navigation is guarded once; cancel preserves all state and confirm navigates once.
13. Guided extraction, preview, navigation, and draft resume perform no operational write or policy publication.
14. A visited task, uploaded document, or processed source never becomes completion evidence.
15. Guided status uses persisted or verified facts rather than guessed readiness or progress percentages.
16. Major Update automation appears only for destinations with verified adapters.
17. A capability-verified adapter detects direct changes against its defined target/baseline and makes affected staged proposals stale; other destinations require canonical revalidation and never silently overwrite or merge.
18. Restricted, disabled, and unknown destinations never mount or fetch protected data.
19. Company, tenant, role, feature, source, and target boundaries hold through direct API requests.
20. Current batch Apply remains atomic and reports only its verified aggregate counts/skips; task-level Applied requires an explicit batch-family mapping and evidence, while staged canonical saves use separate destination-scoped actions/results.
21. Operational save, policy review, policy publication, and employee guidance remain separate states and authorities.
22. Desktop, 390 px, 320 CSS px, 200% zoom, 400% reflow, keyboard-only, English, Arabic, and RTL complete the core journey without horizontal overflow or obscured actions.
23. Axe reports no serious or critical violations, supplemented by manual focus, keyboard, zoom, and screen-reader checks.
24. Existing policy library, source replacement/removal, organization import, company switching, operational routes, and validated v1-to-v2 local-draft migration retain regression coverage.
25. Inactive paths and editors are unmounted rather than hidden; they issue no inactive API reads.
26. No new backend endpoint, schema, generic form engine, or duplicated business form is introduced merely to render the new shell.
27. Tenant-shared destinations in company-context Guided V1 are direct-configuration-only, excluded from draft counts, and explicitly labelled `Applies to all companies`.

## 15. Explicit anti-goals

This specification does not authorize:

- redesigning the main application sidebar or operational modules;
- automatic completion percentages;
- a universal AI document-to-form mapper;
- a universal batch amendment engine;
- automatic policy publication;
- silent group-to-company inheritance;
- group baseline setup/adoption before a separate approved inheritance design;
- cross-device or collaborative drafts without durable server design;
- multiple named/collaborative plans in V1;
- per-record partial outcomes from the current atomic batch Apply endpoint;
- invented settings for operational-only modules;
- production Evostel changes;
- release, merge, or deployment.

## 16. Approval boundary

Owner review of this written specification is required before an implementation plan is created. After approval, the next artifact is a phased implementation plan that identifies file ownership, migration sequencing, test-first changes, independent review, and local shadow-tenant evidence. Product implementation begins only after that plan is reviewed and an execution method is selected.
