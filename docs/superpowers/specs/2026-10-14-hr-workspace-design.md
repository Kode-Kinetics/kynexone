# Setup Studio: unified HR configuration and policy workspace

Status: proposed design for owner review. This document authorizes no application changes or production deployment. It consolidates the agreed direction after specialist review; implementation planning follows written-design approval.

## 1. Outcome and authority

An administrator can discover every relevant HR setup area, edit its existing forms manually or prefill them from policy documents, review changes, and save with the appropriate authority. Both entry methods can prepare central policy-library content for authorized review and employee guidance through Kody.

Single window means consistent company context, connected navigation and recoverable work, not every module or form expanded on one page. Existing operational screens remain the destinations for daily transactions. Setup Studio does not become a second HR application.

This design supersedes the product-navigation scope of `2026-10-14-policy-first-leave-design.md`. That earlier document remains a candidate bounded leave-adapter design, not the authority for the whole workspace or a reason to exclude other areas. Its exclusion of publication applies to that pilot only. Current form contracts and safety constraints must be verified before reuse.

## 2. Coverage and scope

Twelve areas are non-linear destinations, not mandatory setup steps:

| Area | Setup coverage |
| --- | --- |
| Organization and core HR | Company/entity structure, departments, positions, grades, employee categories, employment records and contracts |
| Recruitment | Requisitions, hiring stages, assessment/offer templates, hiring authority |
| Onboarding and confirmation | Checklists, responsibilities, orientation, probation, reviews and confirmation |
| Time and attendance | Working calendars, shifts, attendance, timesheets, overtime and corrections |
| Leave and absence | Leave types, applicability, entitlements, accrual and request/approval rules |
| Compensation and benefits | Salary components, grade benefits, individual exceptions, loans, incentives and merit |
| Payroll | Calendars, cutoffs, calculation controls, deductions, review/approval and accounting handoff |
| Performance and talent | Goals, cycles, ratings, calibration, development and succession |
| Learning and development | Catalogues, training eligibility/approval, assignments and certification renewal |
| Employee services and relations | Requests, letters, engagement, grievances and restricted case procedures |
| Offboarding and settlement | Exit types, notice, clearance responsibilities, settlement and closure |
| Compliance and statutory administration | Jurisdiction profiles, documentation, filings, retention and audit |

These are coverage requirements, not assertions that each setting already exists or is legally required. Policies can contain documented procedures without corresponding automated rules. Such content is valid library content, not falsely labelled operational configuration.

Shared controls: permissions, delegation, approvals, reporting, notifications, integrations, employee-data privacy, migration/opening balances, accessibility, English/Arabic and RTL. Workforce planning, mobility and travel/expenses must have explicit ownership in the coverage inventory; they are not silently assumed implemented.

The supplied 13 Oct comparison of 36 families and 14 vendors is the separate competitive baseline. Preserve its implementation/test/flag/partial/gap distinctions. Do not rebuild reported learning, engagement, succession or merit capabilities without checking their current condition. Federation, broad connectors, general BI, travel/expenses, country depth and independent assurance remain separately tracked product work, not silently added to this implementation scope.

## 3. Navigation and roles

- Replace the primary five-step wizard with an overview and compact named area navigator. No numbered completion sequence or company-creation prerequisite for an existing company.
- Preserve current application identity, components and operational routes. Area selection opens existing canonical forms; use focused list-and-editor layouts for repeatable records.
- Keep tenant/company, active area and save state visible. Group-level controls must be explicitly distinguished from company controls.
- Overview reports configured work and outstanding decisions, with a useful next action. A visited area or uploaded file is not complete. Read failures are not empty configuration.
- Mobile uses an accessible area drawer/selector and full-width form, not twelve narrow horizontal tabs. Support keyboard, zoom, long labels and Arabic/RTL.
- Group admin manages permitted standards/adoption. Company HR manages company configuration. Specialists edit their domains. Managers approve within authority. IT/facilities receive only assigned responsibilities. Employees access their own services and permitted guidance. Auditors receive read-only evidence.
- Page visibility, extraction permission, operational editing and policy publication are separate authorities. Server checks are decisive.
- Features disabled for a tenant must not look configured or broken. Explain availability to authorized administrators. Never fabricate a working destination for an unimplemented capability.

## 4. Common area interaction

1. Open an area and the intended existing record, with real current values and scope.
2. Edit normally, or choose the secondary action **Fill from document**. Upload a source or reuse a library source; a document is never required for manual entry.
3. Prepare changes into those same fields. Expose sources on demand, distinguish manual edits and show conflicts beside affected fields.
4. Review the changed values, affected scope and effective date. Unstated source values do not clear saved settings. Unknown required values remain unresolved rather than guessed.
5. Save through the canonical module workflow and confirm persistence. Save draft, Save settings and Next area have distinct meanings. Navigation and extraction never save operational settings.
6. Prepare/review the related policy draft where appropriate, then explicitly approve/publish with the proper authority.

Users can leave, return, cancel or resume without silent data loss. State whether a draft is browser-local or durably stored; do not imply cross-device collaboration for local storage. Conflicting concurrent edits require reconciliation, not last-writer-wins.

The absence of a policy document is a normal alternative. Required business choices remain in their relevant forms. Optional unresolved decisions may be assigned/deferred or marked not applicable with an appropriate reason. No giant questionnaire and no automatic assumption of a company policy.

## 5. Documents and extraction

- One source library serves full binders and per-area uploads. Full-binder processing can prepare area-specific proposals; only reviewed selected changes are committed.
- Selected area, authorized company and target identity constrain accepted output on the server. UI filtering and model prompts alone are insufficient.
- Add, replace and remove sources remain available. Distinguish removal from the current draft, library deletion, revision replacement and withdrawal from employee access.
- Source identity/version, passage and destination accompany proposals. Replacement invalidates affected unaccepted proposals while preserving unrelated work.
- Conflicting clauses require an explicit decision. Upload order is not policy precedence. Unsupported automation is reported without preventing useful document-only policy guidance.
- Display actual validated size/format/page limits before upload. Oversized, scanned, password-protected, malformed or partially processed documents need actionable outcomes; never silently claim complete extraction.
- Current code inspection found limits of 20 MB, 5 million extracted characters and 2,000 PDF pages, with no OCR in the inspected parser. These are current constraints, not a new capacity promise or benchmark.
- Original binary retention, resumable processing and scanned-document handling require explicit capability decisions before any UI promises them. Treat source text as untrusted; document instructions cannot authorize actions.
- Extraction failures preserve manual entry. Use bounded retries, observable progress and cancellation without duplicate committed records.

## 6. Configuration, policy and guidance

Maintain three connected but distinct concerns:

- Operational configuration: values and rules the existing module actually executes.
- Approved policy: organization-owned statements, procedures and decisions.
- Employee guidance: authorized published content retrieved by Kody.

Manual values may generate a readable policy draft, but cannot invent unprovided procedures, conditions or entitlements. Uploaded documents retain source evidence and must not automatically publish confidential annexes. Changes in either policy text or settings prompt reconciliation when the other would become inconsistent; no silent bidirectional overwrite.

Show configuration and publication status separately. A single review can coordinate both outcomes for an authorized user, but must report partial failure precisely. Saving settings is not publication; publication is not proof of runtime enforcement. Track pending reconciliation so a failure cannot disappear behind a generic success message.

Proposed publication lifecycle: draft -> authorized review/approval -> published or scheduled -> superseded/withdrawn, with attributable history. Review controls should be proportional to the organization's approval model; do not impose a separate person for every low-risk draft unless configured governance requires it.

Define policy identity/revision, audience, effective dates and source bindings before relying on them. Reuse existing publication/audit structures where sound. Any required schema extension needs a specific gap and approved design; neither blanket new tables nor a blanket promise of zero backend changes is justified.

Group standards and company variations require explicit adoption, precedence, scope and approved exceptions. Existing group read access does not prove inheritance. Employee applicability must be resolved, not guessed from company names. Changes must not silently rewrite historical transactions or retroactively recalculate payroll/leave.

## 7. Kody contract

- Employee answers use authorized, effective, published policy versions for the requesting employee, with useful citations.
- HR draft preview is an explicitly separate mode, never represented as employee-visible policy.
- Restricted HR procedures, individual information and employee editions have distinct audiences. Do not publish an entire confidential binder to all employees.
- Missing evidence, ambiguous scope or contradictory authority produces a clear limitation and appropriate HR handoff, not an invented answer.
- Policy explanation is distinct from personal balances or transaction execution. Any account-specific answer/action uses its existing authorized operational source and confirmation workflow.
- Evaluate Arabic and English retrieval, paraphrases, dates, overlapping policies, withdrawn revisions and malicious source instructions. Company isolation and publication prerequisites must be satisfied before releasing employee guidance.

## 8. Verified foundations and limitations

Read-only specialist inspection of the working tree at commit `3e419665` found existing forms, module routes, extraction/prefill infrastructure, scoped publication metadata and employee policy retrieval. The tree includes pre-existing dirty/untracked work. This is code evidence, not fresh runtime certification.

Important limitations found:

- Manual settings do not yet demonstrably produce approved policy-library content.
- The inspected PolicyDocument entity has company/date/publication/hash fields, but not complete approval, revision lineage, audience or group-inheritance semantics.
- Setup Apply skips matching existing leave policies and creates attendance only when absent; it is not a universal amendment engine. Use appropriate existing module update paths.
- Some drafts are browser-local. Onboarding templates may be tenant-wide; a selected-company banner does not change their actual scope.
- Policy retrieval uses bounded keyword matching. Multilingual accuracy, precedence and comprehensive answer quality require evaluation.

## 9. Delivery boundaries and acceptance

Before implementation, inventory each supported destination: area/family, existing form and API, allowed roles, tenant enablement, actual record scope, manual fields, extractable fields, policy output, operational consumer and test scenario. Mark verified, partial, disabled and unavailable separately. This inventory is not yet complete for all 36 families.

Delivery order after approval:

1. Complete the destination inventory and review desktop/mobile journeys.
2. Establish navigation, reusable form integration and explicit draft/save contracts without duplicate business logic.
3. Establish version/audience/publication prerequisites before any new employee-policy release.
4. Prove representative connected scenarios: manual configuration to reviewed library policy; document to existing form; changed/conflicting policy to a reviewed amendment; published policy to the correct Kody answer.
5. Extend the proven pattern across the agreed areas, closing or explicitly classifying gaps. The initial reference scenario does not reduce the full workspace scope to Leave alone.
6. Run isolated Evostel shadow acceptance. Production changes require separate explicit approval and a documented deployment/recovery plan.

Acceptance includes:

- Every area is discoverable with truthful capabilities and real destinations.
- Manual/document paths reach the same validation and canonical save workflow.
- Extraction, navigation and preview cause no operational writes or employee publication.
- Unmentioned values, explicit zero/false, user edits and target identity survive correctly.
- Source replacement, stale targets, parallel edits, interruption and uncertain save outcomes recover without silent overwrite or duplicate records.
- Tenant/company/role/audience boundaries hold through both UI and direct API requests.
- Group exceptions, future/expired/withdrawn versions and HR preview are resolved correctly before corresponding release claims.
- Real module behavior agrees with the saved configuration; published guidance agrees with its reviewed version and scope.
- Mobile, keyboard, zoom, Arabic/RTL, extraction failures and validation errors remain usable.
- Measure task success/time, correction rates, recovery, policy-answer/source agreement and critical isolation failures. No competitive superiority claim without comparative evidence.

## 10. Approval boundary

This document is the consolidated design for review, not a completed implementation or an exhaustive verified feature inventory. The next approval permits a concrete implementation plan with bounded ownership, dependencies and test gates. Production Evostel remains untouched throughout design and local/shadow work.
