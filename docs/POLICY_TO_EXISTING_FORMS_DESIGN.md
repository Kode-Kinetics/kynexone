# Policy to existing forms: corrected implementation design

Status: proposed cross-module integration; requires owner review before implementation.
Scope: local Evostel shadow only. No deployment, production writes, new tables, replacement forms, or automatic employee task generation.

## Agreed outcome

The uploaded policy supplies values for existing application forms. AI prepares reversible drafts, shows existing versus proposed values, and asks only for information the policy cannot supply. The user reviews and saves through the existing workflow. A policy review questionnaire is not the deliverable.

## First connected destination

Existing route: `/recruitment/onboarding`, **Checklist Templates** in `frontend/src/views/RecruitmentPage.tsx` (`OnboardingTab`).

| Existing controls | Source mapping | Values not to invent |
| --- | --- | --- |
| Checklist Code, Name, Description, Applicable to, Department | Select an existing checklist explicitly; source name/description may prepare a new checklist draft only when the user chooses Create | Existing record identity, code, department restrictions, or enforced eligibility not represented by this form |
| Task title, Description | Separate source-backed duties from 3.1-R1/R2/R3 and orientation elements from 3.2-R1; retain conditions and source references | Completion, actual medical result, invoice values, signatures |
| Category | Suggest only existing General/Document/IT/Training/Policy/Access values; identify this as a proposed mapping | A new category or an assertion that the policy itself chose that application category |
| Owner | Carry an explicitly stated responsible role as role text; require task-specific confirmation when responsibility is ambiguous | A person assignment from a role name; source lists of several responsible teams are not a per-task assignment |
| Due offset days | Fill only an unambiguous source interval with the same start-date anchor as the existing field | Zero for an unknown deadline; recommended before-day-1 timing as approved policy |
| Order, Mandatory | Proposed order is editable; obligation/conditional language remains visible before confirmation | Treating conditional tasks as unconditional obligations |

The policy cannot supply an actual employee/application, joining date, appraisal score, medical result, acknowledgement signature or employment decision. Those remain inputs to the existing employee-specific workflows.

## User journey

1. Confirm the existing tenant and company context once. Resolve by record ID, not a typed legal-name match. Show loading/failure/unavailable scope states before extraction.
2. Add/replace/remove policy chapters using the existing binder. Preserve its cancel/resume behavior.
3. Extract structured, field-level task drafts with exact source/version evidence. Separate source facts, recommendations, assumptions, proposed configuration and unresolved questions. Mixed table cells must retain their individual meaning.
4. Show a compact summary of prepared existing destinations, questions that unlock fields, and genuine gaps. Keep generic review-history cards out of the primary path.
5. Open the existing Checklist Templates form. A draft-task selector loads each prepared task into its existing controls. Preserve edits to every draft task when switching, navigating back or resuming. Do not POST while prefilling.
6. Ask missing values at the relevant field. Unknown deadlines stay blank and must not become zero through number conversion. A new task cannot be saved until its required integer offset and start-date anchor are confirmed. When editing an existing task, retain its existing offset unless the user explicitly changes it. Source references are expandable supporting detail, not mandatory comments.
7. Before each explicit save, show create/update identity, current versus proposed values and the actual tenant-wide scope. Use the existing checklist/template endpoints. Do not describe this as an atomic bulk save; retain unsaved/failed items independently.
8. Read back saved records and match IDs and field values. Repeat navigation/extraction must not duplicate saved tasks or silently overwrite edits. Keep Generate Tasks From Checklist separate and untouched.

## Save and scope safeguards

- Existing templates have TenantId, not CompanyId. For the first local shadow acceptance, the selected tenant must be confirmed single-company and the UI must disclose that templates are tenant-wide. A company-only policy in a multi-company tenant remains draft-only until an approved scope solution exists; this design does not add CompanyId or imply inheritance support.
- Preserve existing authorization and gate write controls with effective write permission, not merely page-read access. Before policy-assisted writes, validate source access, current hash, bounded offsets/length, exact excerpt, allowed destination and source classification at the server boundary. Distinguish source-derived values from user-entered values in provenance. A matching quote alone does not establish semantic support for every proposed field.
- The current template upsert identifies a task by checklist ID and exact title. Editing a title can create another task. The integration must explicitly distinguish create from update, show matching records, and protect existing values. No silent name-based target selection.
- Conditional medical/nationality/sponsorship/orientation duties remain marked conditional. The current task generator does not evaluate description text or template applicability metadata. These rows stay unsaved until the user explicitly places them in an appropriately scoped, manually selected checklist; otherwise they remain draft-only. This cannot be represented as an enforced eligibility rule, and Mandatory does not resolve applicability.
- Do not promise atomic whole-template writes: the existing APIs save a checklist header and individual tasks separately. A failure retains the unsaved draft; saved rows are read back before retry.
- Reuse the existing audit mechanism for policy-assisted saves and source provenance; no parallel policy configuration table. The data write and its audit evidence must commit together, or neither is reported as successful. A local draft alone is not durable server audit evidence.
- No automatic employment confirmation, probation extension, employee task generation, payroll change, notification, or policy publication.

### Existing endpoint extensions required (no new forms or tables)

- Extend the existing template-task save request with an optional policy-draft envelope: explicit create/update action, expected task ID for update, expected existing version derived from the record's current timestamps, and field-level evidence. Old clients remain supported; the policy-assisted path must require this envelope.
- Evidence records tenant/company context, document ID/hash, requirement or source-clause identity, source offset/length/excerpt, destination field, proposed value and whether the value was source-derived or manually supplied. Server validation rejects stale/inaccessible documents and unsupported destinations before saving.
- Policy-assisted updates address the exact existing task ID, not its new title. Reject stale versions atomically, not through a read-then-write comparison alone. Omitted source fields preserve existing values; explicit clears need a reviewed user action. Creates reject an existing matching title and require a fresh create/update choice instead of silently becoming updates.
- Save the task and an existing central AuditLog event together. Use existing transaction/concurrency facilities and verify concurrent manual/policy updates; no audit-only success or task-only success is accepted. Source-invalid, stale, conflicting and permission-denied saves retain the local draft without changing records.
- Checklist headers use the existing create action only in this slice. Editing an existing checklist header is not implied by prefilling task fields and remains out of scope.

## Other Policy 3 mappings and boundaries

| Requirement | Existing destination / boundary |
| --- | --- |
| 3.1-R1 Medical | Onboarding task title/description can retain medical-before-joining and location/cost conditions. Actual result and failed-medical decision remain unresolved. |
| 3.1-R2 Iqama/transfer | Onboarding task content can retain nationality/sponsorship conditions and Admin/BU responsibilities. Cross-policy recovery conflict is not guessed. |
| 3.1-R3 Checklist | Prepare each stated duty in existing task fields; clarify missing owners/deadlines without reasking the task content. |
| 3.2-R1 Orientation | Prepare programme, briefing, acknowledgement and filing task content in existing controls. This is not proof that a source-named document template already exists. |
| 3.3-R1 Probation | Existing employee probation date workflows exist, but Setup probationMonths is not an applied policy control. Do not convert 90 calendar days into three months or invent an end-date rule while CD-03-04 is unresolved. |
| 3.3-R2 Appraisals | Existing probation-review persistence exists; actual dates/scores/feedback remain employee-specific. A reusable monthly/14-day/7-day scheduling adapter is a separate verified capability, not implied by filling a description. |
| 3.3-R3 Restrictions/outcomes | Existing HR decision actions are employee-specific. Do not infer confirmation/termination from policy. Leave probation flags alone do not express the emergency/approval exceptions. |

The Employee Joining Report, Employee Orientation Form and Probation Period Appraisal Form require exact destination/template verification. Similar workflow records must not be claimed as identical forms. Tenant-defined templates have not been ruled out by source inspection.

## Acceptance before handoff

- Inventory all 7 named requirements, all 4 clarification IDs and the 3 named source forms. Each has a verified form/field destination, explicit unresolved decision or specific capability gap. This is a coverage inventory, not a claim that all requirements/forms are implemented. First-slice save/readback proves only the task content from 3.1-R1/R2/R3 and 3.2-R1; descriptions do not implement probation controls or complete the source-named forms.
- Real Policy 3 fills the existing onboarding/orientation task controls; there is no parallel questionnaire standing in for those fields.
- Every stated duty/condition is retained. Recommendations in neighbouring cells do not demote source facts; proposed rules cannot become approved policy.
- Draft task identity uses source/version/clause/destination identity, not AI summary wording. Repeated extraction preserves review/edit associations and explains changed content.
- No write occurs on extract, open, switch task, navigate back or reload. User edits and source evidence survive the existing draft lifecycle.
- Missing deadlines remain blank; named-user assignment, employee data and confirmation decisions are never invented.
- Explicit save on the shadow tenant is read back from the existing API with the same IDs/values; partial failure/retry does not duplicate or overwrite unrelated records.
- Tests include read-only users, cross-tenant access, changed source versions, mismatched company context, dirty existing forms, duplicate titles and failed saves.
- Source-backed draft, saved template, generated employee tasks and operationally enforced policy are reported as separate states.

## Sequence and ownership

1. Integration owner finalizes the extraction-to-existing-form draft contract and source provenance, with no database schema changes.
2. Extraction specialist adds field-grounded output; frontend specialist connects the existing form controls and draft lifecycle.
3. Independent acceptance reviewer checks actual Policy 3 coverage and real shadow save/readback, not only fixture tests.
4. User accepts the first connected onboarding/orientation slice before extension to other Policy 3 destinations or production repetition.

The isolated mixed-cell classification bug is separately being repaired with regression tests. That correction alone does not implement this form integration.
