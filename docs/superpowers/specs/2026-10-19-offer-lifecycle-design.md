# Flexible offers, revision history and truthful delivery

Date: 2026-10-19

Status: approved by the user on 2026-10-19 together with the reviewed implementation plan. Execution in progress; this document does not claim implementation or deployment.

## Intent and scope

HR must be able to add/remove offer components, see the complete history of letters issued to a candidate, and know what actually happened when a letter was sent. Preserve the existing letter designer, company boundaries, maker-checker approval, and exactly-once recruitment acceptance.

The approved scope is three connected improvements: configurable compensation components; an offer/revision history workspace; and honest recorded-external versus application-email delivery. Candidate-specific revisions are included because history must retain the terms being changed. Additional reminders, clause-library management, automated expiry, public candidate portals and e-signature integrations are separate work, not silently included.

No live candidate emails, production schema changes, or payroll processing are authorized for development/testing. Use the isolated local QA environment with email capture mode and blocked external network access.

## Approach

Recommended: extend the existing recruitment entities and shared services, with additive revision/component/dispatch records. Reuse `PayComponent`, `IEmailService.DeliverAsync`, `IDocumentStorage`, existing approval rules, and the current PDF renderer.

Alternatives considered:

- A UI-only history and arbitrary text rows would be smaller, but would not reconcile monetary totals, preserve exact issue evidence or fix misleading sending.
- A new generic document/compensation engine would duplicate working infrastructure and expand this into a payroll rebuild. It is outside scope.

## Verified existing contracts

- `OfferLetter` has fixed basic/housing/transport/other amounts, gross, status and timestamps. The designer adds a self-contained design snapshot; current PDFs are rendered on download, not stored as original issued bytes.
- Both recruitment controllers expose sending paths that set `Sent` without emailing the candidate. The application path also resets the response deadline at send time, which can disagree with the generated document. Both paths must converge on one service and stop altering agreed terms when sending.
- Application offer generation deletes unapproved unsent drafts. Revision support must stop this deletion; it must not fabricate histories for drafts already deleted.
- `OfferRules` enforces approval policy and maker-checker. `RecruitmentService.AcceptOfferAsync` uses transactional compare-and-swap to produce one onboarding draft. Preserve those protections.
- Acceptance currently copies basic salary to `EmployeeDraft.Salary`, without a full offer-component snapshot. Existing employee salary assignment supports basic/housing/transport/food/mobile/other and an effective-dated writer. A catalogue definition is not an employee-specific amount.
- The existing email delivery result distinguishes `AcceptedByRelay`, `Captured` and `NotConfigured`. SMTP acceptance is not inbox delivery. Tenant-explicit overloads exist for background workers.

## 1. Offer components and totals

Separate document blocks from business amounts. Removing/hiding a paragraph or table row affects presentation only. Removing a compensation component changes the offer terms and requires a new revision once the offer has been submitted for approval or issued.

An offer component contains a stable identifier/code, English and optional Arabic label, kind (cash or descriptive benefit), amount for cash, frequency (monthly, annual or one-time), inclusion in the corresponding cash subtotal, required/optional rule, display order and optional existing pay-component definition reference. Cash uses the offer's single snapshotted currency; mixed-currency totals and implicit conversions are rejected. Descriptive benefits carry text, not invented monetary values.

Reuse applicable active tenant/company earning definitions from `PayComponent` as selectable sources. Also allow an offer-only custom item; label it as requiring payroll review rather than creating a shared payroll catalogue entry automatically. Snapshot labels/classification so catalogue edits cannot rewrite previous offers. Do not import tax, statutory deductions or employer contributions as guaranteed candidate cash.

Server-calculated totals:

- Monthly cash = included monthly cash items.
- Annual recurring cash = monthly cash × 12 plus included annual cash items, explicitly labelled as an annualized comparison, not the payment schedule.
- One-time cash = included one-time items, shown separately.
- Excluded cash and descriptive benefits remain visible with a reason/description and do not silently increase gross.

Use decimal arithmetic and the configured currency precision; reject duplicate component identifiers, negative amounts, unknown frequencies and inconsistent client totals. Store authoritative totals with the revision. Maintain legacy `GrossSalary` as monthly cash and legacy fixed fields as explicit projections, not an additional source summed a second time.

Basic salary remains a required identifiable monthly item for the existing hire flow. Company administrators may make other items required; ordinary offer authors cannot remove required items. Optional items can be removed freely from a new draft. This is operational policy enforcement, not a new claim about statutory salary rules.

Existing four-field API callers remain supported: translate their values into equivalent components once. New callers submit components; reject contradictory mixtures of component and legacy amounts. Document compensation rendering must use the authoritative component collection. Preserve old design schema behavior for legacy letters; add an explicit dynamic-compensation block for new templates, with full rows and monthly/annual/one-time totals. Amount fields in the review screen and the final PDF must reconcile exactly.

### Onboarding boundary

On acceptance, retain the accepted offer ID, immutable full component snapshot and currency alongside the employee draft. HR sees a compensation handoff review before activation. Supported monthly mappings feed the existing effective-dated employee salary writer only after explicit confirmation. Do not change a tenant-wide `PayComponent.Value` to represent one employee's salary.

Each component must have a visible disposition: supported monthly payroll mapping, existing bonus/benefit process handoff, or offer-only/non-payroll benefit. An unresolved cash component blocks confirmation of the compensation handoff; it is never silently dropped or automatically collapsed into Other. Offer acceptance itself remains exactly once and does not execute payroll. Building a new arbitrary recurring-payroll engine is outside this work.

## 2. Revisions, original PDFs and history

Keep `OfferLetter.Id` as the revision-specific identity used by current approvals and acceptance. Add an offer-series identity, revision number, predecessor ID, revision reason and concurrency token. A series groups explicitly related revisions; separate applications and companies cannot share one.

`Create revision` copies terms and candidate-specific design into a new draft; it never edits the shared template or the old offer. Editing an unsubmitted draft is permitted with expected-version checks and field-change audit events. Approval submission freezes its terms, design, currency, recipient identity and original PDF. Changing frozen terms creates a new revision and requires fresh approval; approvals never carry forward automatically.

Persist the exact generated PDF through tenant-scoped `IDocumentStorage`, with offer/revision ID, byte count, SHA-256 hash and creation time. Storage success plus database finalization is required before approval submission or dispatch. Failed storage/commit leaves a recoverable pending artifact, not an apparently complete offer; orphan cleanup must never remove referenced artifacts. Downloads of frozen revisions return stored bytes, not a fresh rendering. Access is authenticated and company-authorized; no public storage URLs.

New revisions do not invalidate an already issued revision merely because HR started drafting. Issuing the replacement atomically supersedes the previous current issued revision. Record the successor link and event without erasing the older revision's sent/accepted facts. Acceptance, replacement issue and withdrawal share an application-level concurrency guard. An accepted revision cannot be superseded or accepted again into another employee draft. Withdrawn, expired or superseded revisions cannot be accepted. Unknown/in-flight sending blocks revision replacement until reconciled.

History UI: a paginated searchable register, not a long stack of cards. Filters: candidate/offer number, company, position, business status, communication outcome and issue-date range. Stable ordering includes timestamp and ID. Opening a record shows the selected revision, complete component breakdown, original PDF, approvals, candidate-response records, and chronological events. A revision selector and side-by-side changed-field summary explain differences in amounts, dates, clauses and branding. `Download original`, `Create revision`, `Send/record external sending` and permitted response actions are contextual.

Every dispatch attempt remains available under its revision, including recipient, channel, actor, time, outcome and safe failure reason. Do not represent a staff-recorded acceptance as a candidate-authenticated signature; label the recorder/source accurately.

### Existing records

Retain existing IDs, timestamps, approval references, audit events, design snapshots and legacy download behavior. Do not invent missing recipients, delivery proof, original PDF bytes or revision links. Existing `Sent` records display `Previously marked sent — delivery unverified`. Existing records without series metadata are independent legacy roots until explicitly revised; chronological similarity is not proof of lineage. A newly stored reconstruction of an old PDF must be labelled reconstructed, not original.

## 3. Truthful sending and delivery

Keep business state and communication outcome separate. Replace the ambiguous Send button with a review dialog offering two explicit actions:

1. **Send email:** review the candidate address, subject, message and exact frozen PDF; dispatch through the tenant's configured mail service. Changing the recipient requires an explicit authorized edit and audit record, never hidden lookup substitution at send time.
2. **Record external sending:** HR confirms that the specified revision was sent outside the app, records channel, recipient, actual time and optional reference. This does not send an email and is not provider-verified delivery.

Use the same authorization and approval checks on both controller routes and both actions. Legacy send routes must not silently begin emailing because a caller invokes an old status-update endpoint. Deprecate the ambiguous request with an actionable response requiring the new explicit method; update both frontend callers together.

| Communication outcome | Meaning and behavior |
| --- | --- |
| Queued | Persisted send request; nothing is claimed sent yet |
| Sending | One leased worker owns the attempt |
| Accepted by mail server | Existing transport reports SMTP acceptance; not inbox delivery |
| Captured for testing — not sent | Test/capture result; leave business offer unissued |
| Not configured — not sent | No applicable relay; show settings guidance, leave offer unissued |
| Failed | Definite failure with a sanitized reason; user can initiate a controlled new attempt |
| Outcome unknown | Worker interruption/ambiguous timeout after delivery may have begun; no automatic resend |
| Externally recorded | Staff declaration of external sending; clearly unverified by the application |
| Legacy unverified | Old status transition without delivery evidence |

Only SMTP acceptance or a confirmed external-send record advances a new offer to business `Sent`. Preserve the frozen response deadline; an extension requires revision, not a silent new date. Do not add Delivered, Opened or Read states without provider evidence. Inbox tracking/webhooks and e-signature-provider evidence are deferred.

Persist an offer-specific dispatch/outbox record with tenant/company/offer IDs, immutable recipient/message/PDF reference, actor, idempotency key and request fingerprint. Persist immutable per-attempt outcomes/events; do not discard earlier failures during retry. A database uniqueness constraint handles duplicate clicks; identical requests with the same key return the original result, while changed payloads with that key conflict. A new intentional resend needs confirmation, reason and a new key, and sends the same frozen revision.

Use leased background processing and the existing tenant-explicit `DeliverAsync`; no alternate SMTP implementation or direct provider calls. Revalidate tenant/company, offer state, expiry and approval eligibility before transport. Retry transient pre-dispatch infrastructure failures safely. Because SMTP cannot guarantee exactly-once delivery across a process crash, conservatively classify stale Sending leases and ambiguous transport failures as Outcome unknown. Require explicit reconciliation/resend confirmation rather than automatically risking duplicate candidate messages. If relay acceptance is known but state persistence fails, retain/reconcile the attempt; never silently resend.

## Persistence/API ownership

One integration owner owns schema, shared contracts and both controller integrations. Additive entities cover offer components, series/revision metadata, artifact metadata, immutable lifecycle events, and dispatch/attempt records. All operational children carry tenant/company scope and validated parent references. Use database foreign keys/unique constraints and expected versions for concurrent mutations.

Introduce focused shared services for compensation validation/projection, revision/artifact lifecycle, dispatch and history queries. Existing controllers delegate; neither gets its own competing calculator or send policy. Reuse the existing exactly-once acceptance transaction and attach the compensation snapshot to its winning draft. Retention/erasure must cover new child rows and stored PDFs using existing tenant lifecycle conventions; offer-history users get no hard-delete endpoint.

API operations include component options, create/update draft, create revision, submit/freeze, paginated history, revision detail/events, original PDF, queue email, record external sending, dispatch status, and controlled resend. Detailed DTOs and endpoint names belong in the implementation plan after this design is approved. No writable entity overposting; client input cannot set tenant/company, approval outcomes, computed totals or delivery results.

## UX and accessibility

Preserve the existing Recruitment shell and six-tab letter designer. Keep offer work in a clear record workspace: summary and next action, then Terms & components / Letter / Approvals / History. Show exact business and delivery states together without conflating them. Add/remove controls have text labels, keyboard support and immediate recalculated totals; required components explain why removal is blocked.

The pre-send review includes recipient, revision, PDF and current approval state. Errors identify the unresolved item. Protect unsaved edits and version conflicts. Use English/Arabic interface dictionaries, independent document direction and responsive layouts; history becomes labelled record cards on narrow screens rather than a horizontally clipped table. Do not reintroduce the overlapping sticky header/preview pattern removed from the designer.

## Acceptance tests and release gates

1. Add/remove/reorder components; required-item rejection; monthly/annual/one-time totals; exclusions; rounding; negative/duplicate/currency rejection; legacy request equivalence and no double-counting.
2. UI, API, PDF and saved revision totals agree; removing a presentation block does not change compensation. Real candidate values replace sample values.
3. Saved template edits do not alter existing revisions. Frozen artifact bytes/hash remain identical across downloads, renderer updates and later template edits.
4. Revision creation preserves old letters and approval records. Stale edits conflict. Concurrent revisions/issue/accept/withdraw cannot create two current issuances or onboarding drafts. New terms require new approval.
5. Both send routes enforce identical maker-checker and company/tenant scope. Legacy send calls cannot unexpectedly email. Missing SMTP, capture, definite failure and ambiguous outcome never falsely report successful issue/delivery.
6. Duplicate idempotency keys, double clicks, worker restarts, lease expiry, send-result persistence failure and deliberate resend behavior are covered in database-backed tests. No automatic retry of ambiguous delivery.
7. Historical migration does not invent lineage, original artifacts or delivery evidence. Previously deleted drafts are not represented as recovered.
8. Acceptance preserves every component in onboarding; unsupported mappings are visible and block compensation confirmation until explicitly resolved. Existing salary effective dates and payroll/statutory calculations remain unchanged.
9. Verify scoped history pagination/search, original downloads, revision comparison, mobile/Arabic/keyboard workflows, dirty navigation and empty/error/loading states in the matched local frontend/API pair.
10. Run the previous 64 focused backend tests and 4 designer translation tests as regression baselines, then new unit/integration/browser checks. Apply migration only to the isolated QA clone first. Capture SMTP messages locally; send no real candidate messages.

No production release claim until migration, build/CI, authorization, regression and real configured transport validation are explicitly completed. Feature flags must disable new creation/dispatch without hiding history or deleting evidence. Rollback preserves issued artifacts and new records; it must not downgrade destructively after real offers exist.

## Review checkpoint

Review the proposed behavior, especially (a) no silent payroll changes, (b) original documents and every issued revision retained, (c) explicit email versus recorded external sending, and (d) SMTP acceptance never described as inbox delivery. After approval, produce the implementation plan and select execution method before product-code changes.
