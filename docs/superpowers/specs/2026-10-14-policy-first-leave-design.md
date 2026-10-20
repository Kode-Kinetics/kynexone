# Policy-based Setup Studio navigation, with a Leave Policy pilot

Status: proposed written specification, awaiting owner approval. The policy-first direction was approved in conversation; this document does not authorize implementation or production deployment.

## Outcome and first-release boundary

An HR administrator chooses a policy area by name, supplies a document or reuses a binder, and reviews source-backed values in the application's existing form. No parallel questionnaire, policy configuration table, replacement form, or automatic operational save is introduced.

Owner clarification: the main problem is the outer five-step navigation (Company details, Working week, People & pay, Grades & benefits, Review & create). In the policy workspace, replace that navigation with directly selectable policy areas. Adding a dropdown inside Company details does not satisfy this requirement. The full policy index is part of the navigation scope; Leave is only the first fully verified extraction-to-save adapter, not the only policy visible.

The first acceptance target is the local Evostel single-company shadow tenant. The first connected path updates one explicitly selected existing leave policy at a time. Creating a new leave policy remains available through the current manual workflow; AI-assisted creation and Group/company overrides are later slices, not implied by this pilot. If there are no existing policies, show that fact and link to the existing creation workflow rather than fabricating a policy.

Production Evostel, employee leave balances, payroll runs, leave applications, policy publication, and employee notifications remain untouched.

## Policy navigation replaces the outer wizard

- Keep the tenant/company context visible above the workspace. Organization details and master-data setup remain available through their existing settings routes, separately from policy work. Preserve any unfinished old company-setup draft and provide an explicit return path; do not delete it or make it a prerequisite for editing an already configured company's policy.
- Replace the five numbered steps with a searchable, grouped policy index. Users can open any area in any order; there is no mandatory sequence, Step 1 of 5 counter, or company-creation Continue button in this policy workspace.
- The index must represent the organization's policy inventory, including client-specific titles when known. Reconcile the existing app areas with the actual supplied HR manual. Suggested grouping includes employment (manpower planning, recruitment, onboarding/orientation, probation/confirmation), time and rewards (working hours/attendance, leave, overtime, compensation/grades, benefits), and employee lifecycle/governance (performance, learning, conduct/grievances, travel/expenses, separation/offboarding). These are candidate navigation categories, not a declaration of legally mandatory policies or supported automation.
- Do not hide a policy because automated mapping is unavailable. Its detail view must accurately say whether it has a connected existing form, a manual existing workflow, document/reference support only, or a capability gap. Map only verified existing destinations; never generate fake working forms or configuration status for unimplemented modules.
- For each policy, display actual saved/draft state independently from automation availability. A policy may be configured manually even when AI preparation is unavailable. “Document uploaded” is not “Configured.” Unknown/load-error is not “Not configured.”
- Selecting a policy changes the main content area to that policy's existing records, sources, proposed field changes and relevant next action. Keep upload/prepare/review/save guidance inside the selected policy; it must not recreate the outer five-step company wizard.
- Preserve per-policy drafts and user edits when changing selection. A change in selected policy cannot silently reuse another area's extraction or overwrite its form. Full-binder upload remains a shared source option, with output restricted to the selected area.
- Retain existing policy-library support for organization-specific documents that lack a mapped form. Do not add a new custom-policy configuration engine or table in this slice.

## User journey

1. **Choose policy area in the left navigation.** Select Leave directly from the policy index replacing the old five-step rail. Leave receives the new guided path first. Other listed policies open verified existing destinations or clearly explain their capability gap; a label is not evidence of a completed mapping. Use a readable grouped list on desktop and a labelled searchable selector on narrow screens.
2. **Choose the record.** Keep tenant/company identity visible. For Leave, load actual saved records and let the user select the exact policy to update. Show its leave type, country, applicability and current status. Never select a record solely because its name resembles an AI-generated name. Loading, access failure and no records are separate states.
3. **Choose sources.** Reuse the existing document upload, library and binder controls. Accept a dedicated leave document, multiple chapters, or the whole binder. Selecting Leave restricts the output, not the filename or file packaging. Keep add/replace/remove, cancellation and resume.
4. **Prepare leave policy fields.** This is the single extraction action. Explain that only leave fields will be proposed and nothing will be saved. Retain explicit AI consent and existing file/processing limits; this slice does not claim increased capacity.
5. **Review prefilled leave form.** Open the existing Leave Policies editor for the selected record, populated with its real current values plus reviewed proposals. Show current/proposed values and expandable supporting passages. Keep unresolved items next to their fields. Do not route users through the company-creation wizard or a generic disposition form.
6. **Save changes.** Display a concise change summary, retain the existing explicit Save action and update endpoint, then read back the record. Extraction, navigation, task selection and draft resumption never save operational records.

Policy-area indicators distinguish configured records, pending local drafts and attention needed. A local draft is not a saved configuration; counts are scoped and loaded from actual records, not inferred from uploaded filenames. The single-company tenant-wide nature of a legacy record must be visible, not labelled a company override.

## Existing implementation to reuse

- `frontend/src/components/AiSetupAssistant.tsx`: Setup Studio and tenant/company context.
- `SetupPolicySource.tsx`: existing revisable document binder.
- `SetupPolicyExtraction.tsx`: bounded extraction, source evidence and draft checkpoints.
- `frontend/src/views/LeavePage.tsx`, `PolicyModal`: existing Leave Policies form, used for both manual and policy-assisted editing.
- `frontend/src/api/leave.ts`: paginated policy loading, GET by ID and PUT by ID.
- `backend-dotnet/Zayra.Api/Controllers/Leave/LeavePoliciesController.cs`: existing save, validation and audit path.
- `PolicyExtractionContracts.cs` and `PolicyExtractionService.cs`: extraction contract and validated destinations.

Observed constraints: extraction currently has no domain selector; it validates a global destination allowlist. PolicyModal has create defaults including UAE, 21 days, Monthly and false. Leave policy listing is tenant-wide; company override safety is not established by the selected-company banner. These are design inputs, not claims that the new pilot is implemented.

## Extraction and destination contract

Extend the existing extraction request with an explicit closed policy domain. The scoped pilot requires `Leave`; retain the legacy unscoped binder contract for existing callers rather than silently changing their behavior. Unknown domain values fail validation. The server validates company access and target policy identity independently of client display data.

For Leave, the server allows only supported leave-policy proposals/partial proposals. It must not run onboarding-task projection, return accepted attendance/grade/benefit/company proposals, or silently reclassify unrelated text. Domain enforcement occurs after model output validation and at draft acceptance/save. Prompt instructions and UI filtering alone are not controls.

The document may contain other policies. They can produce a compact informational notice, but never changes to unselected areas. Content classification must be evidence-based: no leave content means “No leave rules found”; ambiguous classification requests confirmation; multiple conflicting leave clauses remain unresolved until the user chooses.

The pilot does not configure the leave-year basis or create referenced catalog records. It retains the existing supported calendar-year behavior. Existing binder parsing/chunking is reused; the UI must not claim that unrelated text is excluded from AI transmission unless the implementation actually guarantees that.

## Field mapping and preservation

| Source-backed proposal | Existing destination / rule |
| --- | --- |
| Policy name | Editable name of the explicitly selected record; never changes its identity. |
| Annual entitlement, accrual, partial-month proration | Existing numeric/select/boolean controls, preserving units and explicit zero/false. |
| Encashment, request limits, notice | Existing controls with normal server validation. |
| Weekend/holiday counting, probation applicability, payroll impact | Existing controls; preserve conditional language and do not invent a simple flag where the source needs a more complex rule. |
| Leave type | Resolve source code to an accessible existing type ID and require it to match the selected policy. Preserve that record's type: the existing update path does not change LeaveTypeId. A different or ambiguous type requires choosing the correct target policy, not changing type through this pilot. Missing type does not create a catalog entry. |
| Grade/department/employment eligibility | Resolve against existing values and the destination representation. Missing or ambiguous mappings require selection; blank must not silently broaden eligibility to everyone. |
| Country | Preserve the selected policy's country in this pilot. Normalize equivalent aliases at boundaries; conflicting source jurisdiction is a review blocker, not an automatic jurisdiction change. |

Use an explicit proposed-field/presence mask. Missing source text is not zero, false, an empty string, an instruction to clear, or permission to substitute a form default. Unmentioned fields retain the loaded record value. The pilot preserves record ID, leave-type ID, tenant/company/branch scope, approval workflow, contract/gender filters, policy status and statutory waiver controls. Other supported changes must use the separate existing manual workflow; they are not inferred by extraction.

Distinct review states:

- **Not stated:** keep current value and show provenance accurately. Ask only if a required value is genuinely unavailable.
- **Conflicting:** block acceptance of the affected field and show the competing passages. Do not use last-upload-wins.
- **Unsupported:** explain the capability gap and retain the clause as read-only evidence. Do not ask the user to approve it into existence.

Carry-forward and expiry are explicitly unsupported by the current API/runtime. Anniversary/custom leave years are not delivered by this pilot. A legacy record with nonzero carry-forward values must not be silently reset by opening or saving the assisted form; show the incompatibility and keep the assisted update blocked until separately resolved through an explicit supported action.

## Scope, draft lifecycle and save safety

- Enable the assisted update only for a verified SingleCompany account and a policy whose scope belongs to that tenant and is compatible with the selected company. Preserve null legacy scope as tenant-wide; never relabel or silently migrate it. Group accounts see an honest unavailable state for this pilot.
- Preserve the existing server write-role requirements. Page visibility or an AI permission alone never grants leave-policy write access. Negative tests cover forged tenant, company and target-record IDs.
- Bind draft state to tenant, user, company, domain, target policy ID, source IDs/hashes and the record baseline. Source replacement, domain/target changes and company changes invalidate the affected unaccepted proposals; already saved records remain unchanged.
- Persist reviewed edits through back/reload. Do not overwrite dirty forms or another tab's newer draft without an explicit choice. Compare canonical values rather than JSON property order. Source ordering remains significant where it affects review.
- Retain source ID/hash, exact passage/span and destination field with each proposal. Distinguish extracted values from manual corrections. Local evidence is not described as a durable server audit record.
- Reuse PUT `/api/leave/policies/{id}` with a bounded optional assisted-update envelope containing domain, scope, expected baseline, proposed-field mask and source references. Existing manual callers remain compatible. The server revalidates source access/hash, target scope and allowed fields before writing.
- Use the existing `UpdatedAtUtc` baseline for an atomic conditional update; reject a concurrent change without overwriting it. A client-side GET-before-PUT is useful for review but insufficient as the sole concurrency guard. No new table or version column is proposed.
- Preserve existing validation and statutory safeguards; the AI must not certify legal compliance or bypass a refusal. Any validation failure retains the draft and identifies the affected field.
- Save the policy and policy-assisted audit evidence through the existing persistence/audit structures in one transaction. Verify that those structures can carry bounded evidence during planning; do not silently introduce a new audit store.
- Read back and compare saved ID and changed values before reporting success. Network uncertainty does not trigger an automatic mutation retry; re-read first. Saving policy settings does not claim recalculation of historical balances or payroll.

## Acceptance gates for the local shadow pilot

1. The policy workspace replaces the old five-step rail with the full inventoried policy index, not a dropdown nested within Company details. Any policy can be opened without completing another. Named Leave selection leads to the selected existing record and form without repeating company setup; existing company-setup drafts remain recoverable.
2. Leave-only and mixed-binder inputs prepare only allowed leave fields. An onboarding-only document yields no leave values and performs no operational writes.
3. Unexpected/out-of-scope model fields are rejected on the server, including onboarding recovery output. Forged domain/target/scope submissions are refused.
4. Current values are loaded from the selected record. Explicit false/zero survive; missing fields, unrelated settings, identity and scope are preserved.
5. Conflicting evidence, ambiguous dependencies and unsupported rules appear as different outcomes; no generic required questionnaire is introduced.
6. Extract/open/back/reload/resume perform no policy save. Drafts remain isolated across tenant/user/company/record, and unsaved edits are protected.
7. Concurrent record/source changes fail safely. Unauthorized and Group-scope assisted saves are blocked server-side, not only visually.
8. One explicit Save updates the intended record through the existing endpoint; readback and audit evidence match, with no duplicate policy or unrelated record mutation.
9. English/Arabic controls, keyboard access and desktop/mobile layout are verified. Existing onboarding/binder/manual leave flows retain their regression coverage.
10. Use an owner-approved real leave-policy source for final coverage testing. Fixtures can prove mechanics but cannot substitute for policy completeness, legal review or owner acceptance.

## Deferred and next approval

Deferred: AI-assisted creation, all other new domain adapters, automatic cross-domain binder routing, Group baseline/inheritance/overrides, scheduled effective-date activation, carry-forward implementation, and production release. The existing whole-binder upload remains usable as a source for the selected Leave run.

The next step after approval of this specification is a bounded implementation/test plan with ownership and release gates. No product implementation or production action is authorized by this document alone.
