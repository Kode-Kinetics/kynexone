# Offer lifecycle completion implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete customizable offer terms, immutable revision history, explicit portal email delivery, and lossless compensation handoff without replacing the existing designer.

**Architecture:** Extend existing recruitment records, approval rules, PDF renderer, tenant email transport and employee salary writer. A shared lifecycle service owns revision state; an application-level issuance reservation serializes delivery against acceptance/withdrawal. One integration owner controls contracts, migrations and both controller adapters; independent reviewers gate each task.

**Tech Stack:** Existing ASP.NET Core/EF Core/PostgreSQL, QuestPDF, Next.js/React/TypeScript; no new mail provider or document editor dependency.

**Spec:** `docs/superpowers/specs/2026-10-19-offer-lifecycle-design.md`

**Status:** Approved by the user on 2026-10-19 for specialist-agent implementation. Execution in progress; completion requires the task reviews and verification gates below.

**Locked user decision — 2026-10-19:** Sender configuration stays at **Setup → Email / SMTP → From Address**, with **From Name** alongside it. Do not duplicate editable SMTP credentials inside Recruitment. Recruitment's send review will show the effective sender and an authorized link back to these settings. This confirmation locks the sender-settings location; it is not evidence that the planned delivery/history implementation is complete.

## Global constraints

- Preserve the six-tab letter designer and unrelated employee/Setup work. Existing worktree is `policy-binder-fix`, branch `fix/policy-binder-draft`, baseline HEAD `311f0b74`; substantial pre-existing uncommitted work is not part of these tasks.
- No live candidate emails, production schema changes, or payroll processing are authorized for development/testing. Use the isolated local QA environment with email capture mode and blocked external network access.
- All operational children carry tenant/company scope and validated parent references. Use database foreign keys/unique constraints and expected versions for concurrent mutations.
- No writable entity overposting; client input cannot set tenant/company, approval outcomes, computed totals or delivery results.
- Only SMTP acceptance or a confirmed external-send record advances a new offer to business `Sent`.
- Preserve the frozen response deadline; an extension requires revision, not a silent new date.
- Do not add Delivered, Opened or Read states without provider evidence.
- An unresolved cash component blocks confirmation of the compensation handoff; it is never silently dropped or automatically collapsed into Other.
- No new candidate portal, e-signature provider, payroll engine, reminder scheduler or general Setup redesign.
- Run the task's red/green tests and relevant regression checks; report full-suite failures separately. Never overwrite/stage unrelated existing changes to make a task commit.

## Integration decisions requiring review

1. **In-flight replacement reservation.** Persist a reservation on the application before a send request becomes eligible for transport. Acceptance, withdrawal, replacement and explicit external issue must honor it in their atomic predicates. Queued requests can be explicitly cancelled before a worker claims them. Sending/Unknown reservations do not expire into an automatic retry. Reconciliation requires an authorized user, an outcome, a reason and an immutable event; it never invents relay evidence.
2. **Money precision.** Existing offers/payroll use two decimal places and no inspected currency-precision configuration exists. Add an explicit company-scoped offer precision policy (0–3, default 2 for existing companies), snapshot precision and the parent company's configured currency on each revision, and widen offer projections to `numeric(19,3)` without reducing supported integer capacity. Reject excess fractional digits rather than silently rounding user-entered amounts. Required payroll-mapped amounts, especially BASIC, must already be exactly representable by the existing two-decimal salary writer before freeze/approval; they cannot produce an accepted but unactivatable hire. Optional unsupported amounts require an explicit non-salary process disposition before handoff confirmation. This does not infer currency precision from a country name.
3. **Sender identity.** Reuse the effective tenant/platform mail resolver; show the resolved sender and source, not merely tenant settings. Sender configuration remains **Setup → Email / SMTP → From Address / From Name**. Recruiters receive a credential-free readiness DTO, not access to SMTP secrets. Capture mode is always labelled as testing.
4. **Accepted SMTP, failed disconnect.** Once transport reports successful message submission, a disconnect failure must not erase that known outcome. An exception before submission is conclusively known remains Failed; an exception after transport may have started is conservatively Unknown unless acceptance was already observed.
5. **Compatibility.** Existing four-amount create requests map once into components. Legacy send endpoints return HTTP 409 `explicit_delivery_required` with new endpoint guidance, and perform no mail or state mutation. Both UI entry points move together. Legacy letters without original artifacts remain downloadable but are labelled reconstructed/legacy, never original.

### Reconciliation and cancellation state rules

- `GET {id}/dispatches/{dispatchId}` returns the immutable request summary, attempts, reservation state and allowed next actions; authorize through both scoped offer and dispatch parent.
- `POST {id}/dispatches/{dispatchId}/cancel` accepts expected dispatch version and reason. Only Queued may CAS to Cancelled before worker claim; if Sending won, return 409 and do not release the reservation. Cancelled is an additional truthful communication outcome, never Sent.
- Reconciliation is Admin/HR Manager-only, still company-scoped. `ApplyRetainedRelayEvidence` requires an existing persisted AcceptedByRelay transport result and finalizes without SMTP. `RecordConfirmedExternalSending` requires explicit external evidence reference/reason and the shared issuance eligibility check; it creates a separate External evidence event and does not relabel an Unknown transport attempt as SMTP acceptance.
- `AuthorizeRiskAcknowledgedResend` requires reason, explicit duplicate-message-risk acknowledgement and expected reservation version. Preserve the old Unknown attempt, record the authorization, and atomically replace its reservation with a new dispatch request/new idempotency key. No gap may permit acceptance between reservation release/reacquisition. The UI explains that two emails may result. Refuse if already accepted/withdrawn/expired or otherwise ineligible.
- Captured, NotConfigured and conclusively pre-transport Failed release the reservation atomically with their outcome. Unknown retains it. A transport exception without proof of non-submission is Unknown, not a convenient Failed retry.
- Both decline endpoints, application rejection and any stage mutation that changes application eligibility use the same application serialization boundary as acceptance/issue. Queued/Sending/Unknown blocks conflicting mutation until cancellation/reconciliation. Expected-version conflicts leave both offer/application unchanged.

### Legacy transition matrix

| Existing state without frozen original | Allowed transition |
| --- | --- |
| Draft, no approval ever submitted | Explicitly adopt as a new managed draft; resolve current recipient/currency/components, show review, then freeze. New artifact starts now. |
| PendingApproval / Approved / rejected after approval submission | Read-only legacy terms; create a linked managed revision with new review and fresh approvals. Earlier approval cannot endorse a newly rendered PDF. |
| Sent | Retain status/timestamps, label delivery unverified and reconstructed download. May create revision or record a real response using existing legacy acceptance rules; never silently fabricate original or delivery proof. |
| Accepted | Read-only history, exactly-once replay returns existing draft; never supersede. Preserve the existing legacy missing-draft recovery path without inventing compensation not recorded. |

Any managed revision frozen once remains immutable after rejection; rejection permits a fresh revision, not editing frozen terms in place.

## Review focus

- A replacement is being sent while someone accepts the previous revision: reservation/CAS tests in Tasks 2–3 must prevent two valid issued/accepted outcomes.
- SMTP succeeds but disconnect or DB commit fails: Task 3 retains acceptance evidence or Unknown, blocks automatic resend and exposes reconciliation.
- Catalogue/candidate/template changes after approval: Tasks 1–2 prove frozen terms, recipient and exact PDF bytes remain unchanged.
- Unusual precision, excluded benefits and annual/one-time amounts: Tasks 1 and 4 prove totals and handoff do not double-count, round or discard them.
- A same-tenant user guesses another company's child ID: Tasks 2–3 test HTTP authorization as well as direct service scope; no artifacts, history or sender secrets leak.

## Shared contract and ownership

All paths below are relative to this worktree. Backend paths are beneath `backend-dotnet/Zayra.Api/`; test paths beneath `backend-dotnet/Zayra.Api.Tests/` unless fully qualified.

Integration owner approves model/DTO changes before an implementer edits shared files. Only one implementation agent runs at a time; independent read-only review may run alongside it. Capture a task-start diff of already-dirty shared files; reviews compare the task delta, not all earlier changes.

New contracts live in `Application/Recruitment/OfferLifecycleContracts.cs`:

- `OfferComponentInput`: `Code`, `LabelEn`, `LabelAr?`, `Kind` (`Cash|Benefit`), `Amount?`, `Description?`, `Frequency?` (`Monthly|Annual|OneTime`), `Included`, `ExclusionReason?`, `PayComponentId?`, `Order`. Required flags and payroll mappings are server-derived.
- `OfferCompensationSnapshot`: schema version, currency, precision, immutable validated component rows, `MonthlyCash`, `AnnualRecurringCash`, `OneTimeCash`, and legacy projections.
- `OfferActor(Guid TenantId, Guid? CompanyId, Guid UserId, string Name)`: constructed server-side from authenticated scope; never model-bound.
- `OfferDraftInput`: existing placement/start/deadline/probation/template fields plus recipient, optional components, optional legacy amount fields and optional candidate-specific design. Contradictory mixed amount forms are rejected.
- `OfferMutationRequest(long ExpectedVersion)`; revision includes `Reason`; recipient edit includes `Reason`; withdrawal includes `Reason`.
- `OfferEmailRequest`: `ExpectedVersion`, `IdempotencyKey`, `Subject`, `Message`, `ResendReason?`; recipient and artifact are taken from the reviewed frozen revision, not substituted from current candidate data.
- `OfferExternalSendRequest`: `ExpectedVersion`, `IdempotencyKey`, `Channel` (`Email|InPerson|Other`), `OccurredAtUtc`, `Reference?`, `Reason?`. Recipient identity always comes from the frozen revision: email uses its exact email; in-person/other uses its candidate identity and requires a channel reference. No arbitrary replacement-recipient input. Require frozen-at ≤ occurred-at ≤ server-now and current eligibility/deadline at recording time; backdating cannot bypass expiry.
- `OfferResponseRequest`: `ExpectedVersion`, `Decision` (`Accepted|Declined`), `Source` (`StaffRecorded`), `OccurredAtUtc`, `Reason`, `EvidenceReference?`. Actor is authenticated, not supplied. This is not a candidate signature. New `POST {id}/responses` delegates to the shared acceptance/decline transaction; old response endpoints require this explicit request/version before mutation and return actionable 409 for bare legacy requests.
- `OfferMailReadiness`: `Configured`, `Mode`, `Source`, `FromAddress?`, `FromName?`, `CanManageSettings`, `BlockedReason?`; no password, host credentials or arbitrary settings bag.
- `OfferPolicyDto`: selected company ID, read-only company currency, precision, required component codes and expected version. Persist in a dedicated `OfferCompanyPolicy` configuration entity; existing `SystemSetting` is tenant-only and must not masquerade as company-scoped storage. Admin-only policy writes use the selected authorized company, audit changes and never rewrite frozen revision snapshots. Drafts retain their last-saved policy version; a changed policy requires explicit review/revalidation at next save/freeze, never silent mutation.
- `OfferHistoryQuery`: search, application/company/position, business status, communication outcome, issued-from/to, page, page size (1–100). Stable ordering is timestamp descending, then ID descending.
- `OfferDetailDto`: offer/series/revision IDs, expected version, terms/snapshot, frozen recipient/currency, artifact provenance, approvals, action capabilities/reasons, latest communication, revision summaries and changed fields. Events/attempts are paginated separately.

HTTP routes stay under `/api/recruitment/offers`:

| Route | Contract |
| --- | --- |
| `GET component-options` | eligible persisted earning definitions and current offer precision policy |
| `GET policy` / `PUT policy` | scoped offer policy read / Admin-only expected-version update |
| `GET mail-readiness` | safe effective sender and delivery mode |
| `GET history` | paginated register using `OfferHistoryQuery` |
| `GET {id}/workspace` | `OfferDetailDto` |
| `GET {id}/events` | paginated lifecycle events and attempt summaries |
| `PUT {id}/draft` | draft input plus expected version; unsubmitted only |
| `POST {id}/revisions` | expected version and reason; creates draft with fresh approvals |
| `POST {id}/freeze` | expected version; freezes before approval-disabled issue |
| `GET {id}/original` | authenticated stored PDF only; absent original is an explicit error |
| `POST {id}/email` | idempotent reviewed email request; returns dispatch ID/outcome |
| `POST {id}/external-sends` | idempotent staff-recorded external sending |
| `POST {id}/withdraw` | expected version/reason; refuses in-flight or accepted offers |
| `POST {id}/responses` | explicit versioned staff-recorded acceptance/decline |
| `GET {id}/dispatches/{dispatchId}` | scoped request/attempt/reservation status |
| `POST {id}/dispatches/{dispatchId}/cancel` | expected-version Queued cancellation only |
| `POST {id}/dispatches/{dispatchId}/reconcile` | authorized observed outcome and reason; no fabricated provider proof |

Existing download route uses stored bytes when available and retains its legacy fallback. Existing approval submission freezes before entering PendingApproval. Both create/generate controller paths call the same terms/lifecycle services.

### Task 1: Authoritative compensation and policy

**Files:** Create `Application/Recruitment/OfferLifecycleContracts.cs`, `Infrastructure/Recruitment/OfferCompensation.cs`, `Infrastructure/Recruitment/OfferComponentOptions.cs`, `Models/OfferLifecycle.cs`; modify `Models/Recruitment.cs`, `Data/ZayraDbContext.cs`, both recruitment creation controllers. Test `OfferCompensationTests.cs`, `OfferComponentOptionsTests.cs`.

**Interfaces:** Produce `OfferCompensation.Calculate(IReadOnlyList<OfferComponentInput>, string currency, int precision, IReadOnlySet<string> requiredCodes) -> OfferCompensationSnapshot`, legacy conversion, and async scoped catalogue validation. Produce persisted revision terms JSON/projections consumed by Tasks 2–5. No dispatch transport in this task.

- [ ] Write failing calculator tests: basic 1000 + housing 200 monthly + annual 1200 + one-time 500 gives monthly 1200, annual recurring 15600, one-time 500. Excluded cash and descriptive benefits do not increase totals; removing the presentation block changes none of them.
- [ ] Add rejection assertions for missing/non-monthly basic, duplicate case-insensitive codes, negative/overflow amounts, unsupported frequencies, excess precision at 0/2/3, mixed currency, forged required flags, unavailable/cross-company catalogue references and contradictory legacy/component values.
- [ ] Run `dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter 'FullyQualifiedName~OfferCompensationTests|FullyQualifiedName~OfferComponentOptionsTests'`; record expected red failures before implementation.
- [ ] Implement server-derived required basic and administrator-managed additional required codes in `OfferCompanyPolicy`, with Admin-only GET/PUT policy routes and optimistic concurrency. Resolve only applicable persisted active earning definitions; do not synthesize payroll seeds or shared employee amounts. Preserve labels/mapping provenance in snapshots. Test selected-company/admin boundaries and policy changes affecting future revisions only.
- [ ] Resolve catalogue eligibility at offer StartDate (not today's date): applicable company definition overrides tenant default; newest effective-from not later than StartDate wins, respecting effective-to/active state. Options must be earning definitions with explicit offer-entered amounts and no integration/statutory/deduction/employer-contribution provider. Catalogue formula/default Value is not an offer amount. BASIC comes from the server canonical basic mapping, never a caller-chosen display code. Reject composite structure-field mappings that overlap another selected mapping; custom items never impersonate canonical BASIC. Snapshot definition ID, effective dates, classification and mapping provenance; test overrides, effective boundaries, formula/provider rejection and duplicate composite projections.
- [ ] Connect both create paths to validation/projections and maintain equivalent legacy four-field results; add comparative controller tests with no double-counting.
- [ ] Run focused tests and existing placement/designer/maker-checker regressions; record pass/fail counts. Commit only this task's delta after reviewing dirty-file ownership.

### Task 2: Revision, artifact and acceptance-safe lifecycle

**Files:** Create `Infrastructure/Recruitment/OfferLifecycleService.cs`, `OfferArtifactService.cs`, `OfferHistoryQueryService.cs`, `Controllers/Recruitment/OfferLifecycleController.cs`; modify `Models/Recruitment.cs`, `Models/OfferLifecycle.cs`, `Data/ZayraDbContext.cs`, both recruitment controllers, `RecruitmentService.cs`, `OfferRules.cs`, DI registration; one owner-generated `OfferLifecycle` migration/snapshot. Tests `OfferRevisionPostgresTests.cs`, `OfferArtifactPostgresTests.cs`, `OfferHistoryHttpTests.cs`, `OfferLifecycleMigrationPostgresTests.cs`, `OfferAcceptanceExactlyOnceTests.cs`.

**Interfaces:** Consume Task 1 snapshot; produce series/revision/version, pending/final artifact metadata, immutable events, application reservation and all lifecycle routes above except email transport. `OfferArtifactService.FreezeAsync(OfferActor, Guid offerId, long expectedVersion, CancellationToken)` returns finalized artifact metadata. `OfferLifecycleService` owns draft/revision/freeze/withdraw/external issuance, history capabilities and shared issuance finalization.

- [ ] Write red tests for saved/reloaded frozen PDF byte/hash equality after template/company/catalogue changes; pending upload/commit failures; tampered/missing blob; no approve/issue with an unfinished artifact; cleanup never removes referenced blobs.
- [ ] Write red PostgreSQL tests for stale edits, simultaneous revision numbering, preserved unapproved drafts, fresh approval chains, old issue remains valid while a replacement is merely drafted, deadline/currentness checks and accept/replace/withdraw/decline/application-reject reservation races. Preserve existing accept/accept and legacy recovery assertions. Cover every legacy transition row above and frozen rejected revisions.
- [ ] Model additive series/revision metadata, immutable artifact keys/hash/size/provenance, scoped events and application reservation. Add scoped composite foreign keys and unique constraints. Legacy roots remain independent and delivery-unverified; do not invent original bytes or previous draft history.
- [ ] Implement pending-row → unique-key upload → version-checked finalization using `IDocumentStorage`. Frozen downloads verify hash and return stored bytes. No overwrite of finalized keys. Freeze occurs before approval submission or approval-disabled issue.
- [ ] Share scope and action checks through the new service; stop draft deletion in application generation. Add HTTP authorization tests for every child route and stable paginated filters/ties; approval/audit reads authorize through their parent.
- [ ] Extend existing tenant erasure conventions (`Infrastructure/Retention/Rules/SoftDeletedTenantRule.cs` and owner-authorized purge path) to include new scoped rows and durable artifact deletion obligations. Keep pending deletion metadata until blob deletion succeeds, retry safely, and never introduce a hard-delete action for history users. Test failed storage deletion, referenced-artifact protection during normal orphan cleanup, tenant purge ordering and no cross-tenant deletion. Do not execute purge against any user database.
- [ ] Apply migration only to a disposable local predecessor-schema clone; test upgrade twice, preserved legacy rows and FK/uniqueness enforcement. Do not use EnsureCreated as migration proof. New creation/dispatch feature flags never hide existing history.
- [ ] Run task tests plus acceptance, placement, company-scope and maker-checker baselines; retain exact counts and migration evidence. Commit reviewed task delta.

### Task 3: Explicit, durable email delivery and sender readiness

**Files:** Create `Infrastructure/Recruitment/OfferDispatchService.cs`, `OfferDispatchWorker.cs`, `OfferMailReadinessService.cs`; extend `Models/OfferLifecycle.cs`, `OfferLifecycleController.cs`, `IEmailService.cs`, `SmtpEmailService.cs`, `EmailDelivery.cs`, DI/worker configuration and the owner migration as an additive follow-up; update both old send endpoints. Tests `OfferDispatchPostgresTests.cs`, `OfferMailReadinessTests.cs`, `OfferSmtpOutcomeTests.cs`, existing send/maker-checker tests.

**Interfaces:** Consume Task 2 artifact/reservation/finalization; produce `QueueAsync(OfferActor, Guid offerId, OfferEmailRequest, CancellationToken)` and `GetReadinessAsync(OfferActor, CancellationToken)`. Worker always uses tenant-explicit `DeliverAsync`; web request never performs SMTP. Outcomes are `Queued`, `Sending`, `Cancelled`, `AcceptedByRelay`, `Captured`, `NotConfigured`, `Failed`, `Unknown`, `External`, `LegacyUnverified`.

- [ ] Write red tests for same key/same payload replay, changed payload conflict, concurrent double clicks, two-worker lease claim, queued-cancel versus claim CAS, scoped worker parent lookup, frozen recipient, pre-transport approval/expiry checks and no SMTP from either deprecated legacy route. Cover each typed reconciliation action, authorization, retained evidence validation and atomic risk-acknowledged replacement reservation.
- [ ] Write red tests for Captured/NotConfigured/Failed remaining unissued; successful submission followed by disconnect failure; interruption after transport starts; stale Sending lease; accepted result followed by finalization failure. Assert no automatic repeat transport for Unknown/accepted attempts and reservation remains until safe reconciliation.
- [ ] Persist request fingerprint, immutable PDF/message/recipient, human initiator, lease and separate append-only attempt evidence. Store a known transport result before lifecycle finalization; reconcile by dispatch ID without calling transport again. SMTP cannot guarantee exactly-once, so document the residual unknown interval.
- [ ] Implement the bounded SMTP disconnect correction and safe effective-sender resolution including platform fallback/capture. Do not expose secrets; show no sender rather than invent one when capture has no relay. Persist the reviewed From/source and a non-secret configuration version/fingerprint; changed sender/source at dispatch blocks for re-review instead of silently substituting identity. Revalidate tenant active state before transport.
- [ ] Implement explicit external sending with actor/source/time and shared approval/currentness checks. Resend requires a new key, reason and confirmation; ambiguous outcomes require reconciliation first. Preserve deadlines. Old routes return actionable 409 without side effects; update both frontend callers in Task 5 before enabling feature flags.
- [ ] Run new PostgreSQL/transport tests and existing `IntegrationDeliveryHonestyTests`, `NotificationDeliveryTests`, `PlatformEmailSettingsTests` plus recruitment regressions. All integration mail is captured/fake transport. Commit reviewed delta.

### Task 4: Lossless accepted-compensation handoff

**Files:** Extend `Models/EmployeeDraft.cs`, `RecruitmentService.cs`, `Controllers/EmployeesController.cs`, `Application/Employees/EmployeeDraftLifecycle.cs`, DbContext and owner migration. Create `Application/Recruitment/OfferCompensationHandoff.cs`; reuse `EmployeeSalaryBreakdownWriter.cs`. Tests `OfferCompensationHandoffPostgresTests.cs`, `EmployeeDraftLifecyclePostgresTests.cs`, existing hire journey tests.

**Interfaces:** Persist accepted offer ID, immutable full compensation/currency snapshot and expected-version handoff dispositions. GET/PUT `/api/employees/drafts/{draftId}/offer-compensation` extends the existing `/api/employees/drafts/{draftId}` route; PUT accepts explicit per-component dispositions and confirmation, never authoritative amounts. Activation through existing `POST drafts/{draftId}/approve` consumes confirmed mappings only.

- [ ] Write red tests proving acceptance preserves every component, same accepted-offer replay returns one draft, and catalogue edits cannot rewrite the draft snapshot.
- [ ] Assert unresolved cash, duplicate mappings, excess payroll precision and stale confirmations block confirmation/activation. Required mapped precision is already validated before freeze, preventing irreparable accepted-basic amounts. Supported monthly fields map explicitly; annual/one-time benefits require a visible named process disposition. Required basic cannot be excluded. Changing draft salary/company/currency/joining date or mapping inputs invalidates confirmation; activation rechecks a fingerprint of these inputs under its lock.
- [ ] Implement confirmation in the existing draft authorization/maker history. Under the existing activation lock, apply all supported salary fields through `EmployeeSalaryBreakdownWriter.ApplyAsync`; do not edit shared `PayComponent.Value`, infer Other, run payroll or allow grade fallback to replace reviewed values.
- [ ] Run new handoff tests plus `EmployeeDraftLifecyclePostgresTests`, `RecruitmentHireJourneyTests`, acceptance/placement tests; verify effective date and single activation side effects. Commit reviewed delta.

### Task 5: Professional offer workspace, designer integration and send review

**Files:** Create `frontend/src/api/offerLifecycle.ts`, `frontend/src/components/recruitment/OfferWorkspace.tsx`, `OfferComponentsEditor.tsx`, `OfferHistoryRegister.tsx`, `OfferSendDialog.tsx`, `OfferPolicySettings.tsx`, `OfferCompensationHandoff.tsx`, `frontend/src/i18n/offerLifecycle.ts`; extend `frontend/src/views/RecruitmentPage.tsx`, `frontend/src/views/NewHiresPage.tsx`, `frontend/src/api/employees.ts`, existing designer/types/renderer and translation tests. Add `frontend/app/(dashboard)/recruitment/offers/[id]/page.tsx`. Tests `frontend/unit/offerLifecycle.spec.ts`, `frontend/e2e/offer-lifecycle.spec.ts`, backend `OfferLetterDesignTests.cs`.

**Interfaces:** Consume frozen backend contracts, never recalculate authoritative totals for saving. Both offer-list and application-drawer actions open the same revision workspace. Preserve six template tabs; introduce explicit draft-design adapter with actual candidate preview and separate save target.

- [ ] Add failing UI/API contract tests for both entry paths, server capability-driven actions, payload idempotency-key retention on retry, dirty edit protection, stale conflict recovery and literal legacy/capture/unknown state labels.
- [ ] Implement the existing sapphire/slate Operate design: candidate/role/revision/status/next action above **Terms & components / Letter / Approvals / History**. Shared component editor supports add/remove/reorder, catalogue/custom, required explanations and separate monthly/annualized/one-time totals. No unrelated shell/Setup redesign.
- [ ] Add an Admin-only **Offer settings** action beside the offer register toolbar for selected-company precision and required-component policy, with company currency shown read-only. Ordinary authors see the applied policy, not editable required flags. Keep advanced policy controls out of the daily offer form; explain future-draft revalidation and frozen-revision protection.
- [ ] Add a schema-2 dynamic compensation block to the existing Content tab and PDF renderer; retain schema-1 validation/rendering. Rows/totals come from authoritative snapshot; hiding the block is presentation only. Test PDF text/totals and legacy compatibility.
- [ ] Replace both bare Send actions with review of effective From, immutable To, subject/message, original PDF/revision, deadline and approval. Explain **Send email** versus **Record external sending**; authorized settings link goes to `/setup?tab=emailConfig`. Unknown outcomes have reconciliation guidance, not a normal Retry button.
- [ ] Implement paginated history/filtering, revision selector and field comparison, original/legacy download labels, events and attempts. Staff responses say **Record acceptance/decline** and capture source/reason. Add the explicit compensation handoff review to the existing draft flow.
- [ ] Verify EN/AR, independent document direction, visible labels/focus/error summaries, keyboard dialogs, 390px labelled history records, normal-flow PDF preview and navigation guards. Run TypeScript, unit tests and matched local browser journey; capture desktop/mobile/RTL once, batch fixes and one confirmation pass. Independent UX reviewer scores the evidence; document only this surface.
- [ ] Commit reviewed delta with backend dynamic-block changes owned/reviewed by the integration owner.

### Task 6: Independent integration and release handoff

**Files:** Add `docs/OFFER_LIFECYCLE_VERIFICATION.md`; update the existing designer verification with an accurate boundary and links. Retain plan-specific reports/evidence.

**Interfaces:** No new behavior; verify Tasks 1–5 as one connected slice.

- [ ] Run the previous 64 focused backend tests (updating legacy positive Send expectations deliberately), four designer translation tests and all new lifecycle suites. Run full backend/unit suites and production build; report unrelated failures by name, not as passed.
- [ ] Verify actual matched frontend/API identity before signed-in create → components → design → freeze/approval → capture/external issue → revision → history/original → acceptance → handoff → activation. Use only synthetic QA records; never send a live candidate message.
- [ ] Verify migration from the current QA predecessor, capture artifact contents/hash, role/company boundaries, reservation races, reload persistence, empty/error/loading/RTL/mobile/keyboard states. Store exact commands, results and screenshots.
- [ ] Independent final review covers entire task delta, not unrelated worktree changes. Resolve material findings, then record implementation versus local verification versus remaining external/provider/production gates separately.
- [ ] Hand over working local URL, sender configuration location, remaining provider credentials/verification required, migration/rollback runbook and feature flags. Do not claim production delivery until an explicitly authorized real transport check and release are completed.

## Self-review

Spec sections 1–3 map to Tasks 1–5; original/history/legacy behavior to Task 2; dispatch/retries to Task 3; onboarding to Task 4; UX and dual entry points to Task 5; all ten acceptance gates to Task 6 plus the named tests above. Shared contracts are defined once; the three SME-discovered concurrency, precision and effective-sender gaps are explicit decisions above. No task creates a second mail service, payroll engine or shared template mutation path.
