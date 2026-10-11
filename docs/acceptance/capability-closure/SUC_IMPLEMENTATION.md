# SUC — Succession and career development: implementation, contract and runbook

Branch `feat/succession` (base: `feat/learning` + `fix/performance-access-consistency` + main). Binding design:
`SUC_DESIGN.md` (talent worktree, §0 Integration Owner decisions). Requirements: REGISTER.md §1 SUC-01..09.
Evidence: `evidence/suc/EVIDENCE.md`.

Skills applied: kynexone-schema, system-design-review, state-machine, localization-design, dataviz, playwright-cli.

## 1. Shape

| Layer | Where |
|---|---|
| Entities + vocabularies | `backend-dotnet/Zayra.Api/Models/Succession.cs` |
| EF mapping (named CHECKs, explicit ON DELETE, commented indexes, generated column) | `Models/SuccessionModelConfiguration.cs` (one call from `ZayraDbContext`) |
| Migration | `Migrations/20261011210200_AddSuccession.cs` (after `20261011194628_AddLearning`) — CREATE-only, 7 tables; `PostgresOnlyDdl` (deferred exclusion + 2 triggers) mirrored into `PostgresFixture` |
| Band authority | `Infrastructure/Performance/PerformanceBands.cs` (shared; reads the default `PerformanceRatingScale`/options, else 75/60) |
| Services | `Infrastructure/Succession/*`: positions + pools, nominations, talent assessments + 9-box, slates + snapshot, approval sync, development plans, reports, eligibility sweep + worker, retention rule, access/SoD, candidate facts |
| Registration | `SuccessionServiceCollectionExtensions.AddSuccession()` — one line in `Program.cs` |
| HR / manager API | `Controllers/Succession/SuccessionController.cs`, `DevelopmentPlansController.cs` (`/api/succession`) |
| Employee API | `Controllers/Ess/EssDevelopmentPlanController.cs` (`/api/ess/development-plan`) |
| Frontend | `frontend/app/(dashboard)/performance/succession/**`, `ess/development-plan`, `src/api/succession.ts`, `src/i18n/succession.ts`, succession components (see the frontend hand-off) |

Rollout: `FeatureKeys.Succession = "succession"` (opt-in; `OptInFeatures`: `/api/succession`, `/api/ess/development-plan`)
and `FeatureKeys.SuccessionCandidateVisibility = "succession_candidate_visibility"` (opt-in, owns no route; read by the ESS
plan). `/api/succession` is classified under the Performance module (switching Performance off closes it too).

### Shared-file edits (Integration Owner authorised)
- `ApprovalWorkflowService.cs` — five call sites, no logic change elsewhere:
  1. `DecideCoreAsync`, after the Jawazat validation: `SuccessionSlateApprovalSync.ValidateDecisionAsync(...)`.
  2. Rejected branch and 3. final-step branch: `SuccessionSlateApprovalSync.ApplyAsync(...)` (inside the single SaveChanges).
  4. After the `approval.request_decided` audit: `SuccessionSlateApprovalSync.AfterCommitAsync(...)` (slate audit + notice).
  5. `CanDecideRequestAsync`, before the `approvals.override` short-circuit: the same validation (false on refusal).
  No cancel API was added: the retire path closes the pending request exactly as `WithdrawCoreAsync` does (status
  `Cancelled`, `CompletedAtUtc`, `DecisionVersion++` CAS) inside the retirement transaction (`SuccessionSlateApprovalSync.CancelPendingAsync`).
- `LearningProgressService.cs` — `ILearningAssignmentCompletedListener`, called after a completion commits (in
  `AuditCompletionAsync`, the one post-commit point all four completion paths share); failures are logged, never undo it.
- `ApprovalEntities.cs` — producer `SuccessionSlate`. `RetentionContracts.cs` — key `succession.post-employment-3y`.
- `AuthSeeder.cs` — 12 keys, explicit grants, `AdminWithheldPermissions` + its SQL literal + `SelfieReview3Tests` pin.
- `PrivilegedMfaPolicy.cs`, `ModuleCatalog.cs`, `OptInFeatures.cs`, `SaasPlatform.cs` (FeatureKeys), `NotificationCategories.cs`
  (`development`), `ZayraDbContext.cs` (7 DbSets + one Configure call).

## 2. Tables (per-table template, SCHEMA_IMPACT.md)

All seven: `ITenantOwned` + `ICompanyScopedOperational`, `company_id NOT NULL` (EF `IsRequired`), snake_case,
`version int` concurrency token, every FK with explicit ON DELETE, `(tenant_id, company_id)` index by convention.
**Rollback for every table: drop the `AddSuccession` migration (Down drops the triggers, functions and constraint, then
the tables) — no other table is altered, no data elsewhere is lost. Forward recovery: switch `succession` off.**
Retention for all: kept for 3 years after the person's employment ends, then anonymised in place by
`SuccessionRetentionJob` (D3 retention sweep, dry-run unless application is enabled) — never deleted.

### succession_critical_positions
- **Why:** criticality, risk, impact, accountable executive and a designate/retire lifecycle are succession facts most
  `Position` rows never have; `Position` stays the slot authority.
- **Keys/FKs:** `position_id` → positions RESTRICT; `accountable_employee_id` → employees RESTRICT. Partial UNIQUE
  `(tenant_id, position_id) WHERE status='Active'` (`ux_succession_critical_positions_one_active`).
- **CHECKs:** `__status`, `__risk`, `__impact`, `__rationale` (non-blank), `__retired_stamped`.
- **Indexes:** `(tenant, company, status)` — workspace list; `(accountable_employee_id)` — "positions I own".
- **Audit:** designated, updated, retired (with the counts of withdrawn nominations and cancelled slates).

### talent_pools
- **Why:** a development population with an owner and purpose; membership is a nomination row (no member table).
- **Keys:** UNIQUE `(tenant, company, code)`; `owner_employee_id` → employees RESTRICT. CHECKs `__status`, `__code`.
- **Audit:** created, archived (live memberships withdrawn).

### succession_nominations
- **Why:** one candidate × one target with its own state machine, evidence and readiness history; competing candidates
  are separate rows.
- **Keys/FKs:** employee, critical position, pool — all RESTRICT. Partial UNIQUEs per target WHERE status NOT IN
  (Declined, Withdrawn): `ux_succession_nominations_one_live_per_position`, `..._per_pool` (race loser → 409).
- **CHECKs:** `__one_target`, `__status`, `__readiness`, `__readiness_previous`, `__readiness_required` (position +
  Calibrated/OnSlate), `__on_slate_position_only`, `__exit_reason`, `__closed_has_reason`, `__readiness_change_reason`, `__evidence`.
- **Indexes:** `(critical_position_id, status)`, `(talent_pool_id, status)`, `(employee_id)` — position detail/bench,
  pool members, the sweep and conflict checks.
- **Audit:** created, in_review, calibrated, readiness_changed, declined, withdrawn, auto_withdrawn,
  flagged, review_cleared. Company = the TARGET's company (the candidate may sit in a sibling company).

### talent_assessments (append-only)
- **Why:** potential is a separate judgement from the appraisal, with its own evidence, assessor and calibration; the
  row snapshots the performance input (review id, score, band, band rule version) so the placement is reproducible.
- **Keys/FKs:** employee, appraisal review, supersedes — RESTRICT. Partial UNIQUE `(tenant, employee, review_period)
  WHERE status <> 'Superseded'`.
- **CHECKs:** status, band, potentials 1..3, evidence, `__calibration_reason` (≥ 20 chars when calibrated ≠ proposed),
  `__calibrated_stamped`, `__unrated_has_no_score`, score 0..100, superseded stamped, not own predecessor.
- **Generated column:** `nine_box_cell` = `P{calibrated or proposed potential}-{band}`, NULL when Unrated (portable SQL).
- **Trigger:** `trg_talent_assessments__guard` — a Calibrated/Superseded row is immutable (only → Superseded once, or
  the retention redaction with `anonymised_at_utc`), and cannot be deleted.
- **Indexes:** `(tenant, review_period, status)` — calibration session and 9-box.

### succession_slate_versions
- **Why:** what was approved, by whom, when — immutable hashed snapshots, one row per version.
- **Keys/FKs:** critical position, approval request, revised_from — RESTRICT. UNIQUE `(critical_position_id, version_no)`;
  partial UNIQUE one open (Draft/Submitted) version; **deferred** exclusion `ex_succession_slate_versions__one_approved`
  (one Approved per position, checked at commit — approving supersedes the previous version in the same batch).
- **CHECKs:** status, version_no ≥ 1, submitted/approved/superseded/returned/cancel stamps, not own predecessor.
- **Trigger:** `trg_succession_slate_versions__guard` — from Submitted on, identity, hash and submission stamps are frozen;
  status moves only Submitted → Approved/ReturnedForRevision/Cancelled and Approved → Superseded; decision stamps are
  written once; the snapshot changes only on anonymisation (hash kept); only a Draft can be deleted.
- **Indexes:** `(approval_request_id)` — the approval sync's lookup.

### development_plans
- **Keys/FKs:** employee, owner → employees RESTRICT; `nomination_id` → nominations **SET NULL** (HR context only).
  Partial UNIQUE `(tenant, employee) WHERE status IN ('Active','OnHold')`.
- **CHECKs:** status, purpose, completed stamped, cancel reason. **Indexes:** `(tenant, status, next_review_on)` — the
  review-due sweep and progress report; `(nomination_id)`.

### development_plan_actions
- **Keys/FKs:** plan, learning assignment, goal, mentor, owner — RESTRICT. **CHECKs:** type, status, method,
  `__link_matches_type` (Learning ⇔ assignment, Goal ⇔ goal, Mentoring ⇒ mentor), `__completed_stamped`,
  `__linked_method_fits`, title. **Indexes:** `(plan_id, sort_order)`, `(learning_assignment_id)`, `(employee_goal_id)`
  (linked-completion sync), `(tenant, status, due_on)` (overdue sweep/report).

## 3. State machines
- **Nomination:** Proposed → InReview (`slates.manage`) → Calibrated (`calibrate`; needs a calibrated assessment; reason
  if readiness changes) → OnSlate (by slate approval) ⇄ Calibrated (when a later approved version drops them). Declined
  (HR, reason) from any live state; Withdrawn (nominator before calibration; system: Terminated, Ineligible, Appointed,
  PositionRetired; pool archived). Readiness change: InReview/Calibrated/OnSlate, reason ≥ 10 chars, audited.
- **Slate:** Draft → Submitted (approval request created; snapshot rebuilt from live data and hashed; a candidate who
  closed since drafting is refused) → Approved (previous Approved → Superseded, same transaction) or ReturnedForRevision
  (terminal, reason required). Revise (from Returned or Approved) clones into the next Draft. Retire → open versions Cancelled.
- **Assessment:** Draft → Submitted → Calibrated (immutable) → Superseded (re-assessment in the same period).
- **Plan:** Draft → Active (≥ 1 action) ⇄ OnHold → Completed (no open action) / Cancelled (open actions cancelled).
  Review stamps `last_reviewed_at`, moves `next_review_on` forward and re-arms the reminder.

## 4. Access (SUC-07)
| Who | Gets |
|---|---|
| HR Director | all 12 keys |
| HR Manager | all but `succession.calibrate` |
| Manager | `nominate`, `devplans.read`, `devplans.manage`, `self` — own reporting line via `IDataScopeService` |
| Employee | `self` (own plan in ESS) |
| Admin | configuration only (F2): `succession.read`, `positions.manage`, `pools.manage`; every other key withheld (`AdminWithheldPermissions` + its SQL literal + `SelfieReview3Tests` + e2e `role-policy.ts`) |
| Accountable executive | relationship: `positions/mine`, their position, its Approved/Superseded versions (read-only) |
| Routed approver | relationship: the one pending version routed to them (user, employee or role) |
| Everyone else | 404 |

Caller identity (N2): the caller's employees are the `employee_id` claim (`CallerEmployeeResolver`) PLUS every employee
whose `user_account_id` or active `employee_user_accounts` row is the caller's login. A `succession.slates.read` holder with
none is refused every candidate-level read (403 `no_employee_record`, "link your login to your employee record") and
cannot decide a slate — conflicts are checked against the employee, so an unlinked login never fails open.

Candidate bar (F4/N1, `SuccessionAccess.BarAsync`): the "management line" walks every reporting line the approval router
and login privilege use — `manager_employee_id`, `supervisor_employee_id` (dotted line) and the stored
`second_level_manager_employee_id` — transitively (bounded BFS), for reads and for the decision bar alike. A caller who — or anyone above whom in that line — is a live
nominee for a position or pool, or appears in any of its slate snapshots, never reads that target's slates, nominations,
9-box or the assessments of anyone nominated to it (404), and every write on it is 409 `reviewer_conflict` (readiness
change, decline, start/clear review, slate create/edit/submit/revise, assess, calibrate). Every candidate-level read or
`slates.manage` operation also requires `succession.slates.read` (403 otherwise, F2). A nominator without slate access gets
the masked `MyNominationDto` back (F1).

Slate SoD: nobody in the snapshot, or reporting (up the chain) to someone in it, may decide or open it — enforced in
`ValidateDecisionAsync` (decision) and `CanDecideRequestAsync` (listing); `approvals.override` alone does not reach a
slate. Reviewer conflicts (409 `reviewer_conflict`): assessor/calibrator = candidate; candidate in the reviewer's manager
chain; reviewer is a live nominee for a position the candidate is nominated to; incumbent/accountable executive as candidate.

## 5. API contract (`/api/succession`, errors `{ error, message, details }`)
Positions: `GET positions?status`, `GET positions/available`, `GET positions/mine`, `GET positions/{id}`, `POST positions`,
`PUT positions/{id}`, `POST positions/{id}/retire`, `GET|POST positions/{id}/slates`. Pools: `GET|POST pools`,
`GET pools/{id}`, `POST pools/{id}/archive`. Nominations: `GET nomination-targets`, `GET nominations`, `GET nominations/mine`,
`GET nominations/{id}`, `GET nominations/{id}/history`, `POST nominations`, `POST nominations/{id}/review|calibrate|readiness|decline|withdraw|clear-review`.
Talent review: `GET assessments`, `GET assessments/periods`, `GET assessments/{id}`, `POST assessments`,
`POST assessments/{id}/submit|calibrate`, `GET nine-box?period&positionId&poolId`. Slates: `GET|PUT slates/{id}`,
`POST slates/{id}/submit|revise|cancel` (decision through `POST /api/approval-requests/{id}/decisions`). Reports:
`GET reports/summary`, `GET reports/drill?metric=`, `GET reports/trend?months=`. Pickers: `GET people?search`,
`GET development-plans/learning-paths`, `GET development-plans/goals?employeeId`. Plans: `GET|POST development-plans`,
`GET development-plans/{id}`, `POST development-plans/{id}/activate|hold|complete|cancel|review|actions`,
`PUT development-plans/actions/{id}`. ESS: `GET /api/ess/development-plan`, `PUT /api/ess/development-plan/actions/{id}`.

Notifications (stable EventCodes, ids only, no candidate named to a manager or employee): `SUCCESSION_SLATE_SUBMITTED`
(routed approver), `SUCCESSION_SLATE_DECIDED` (submitter), `DEVPLAN_REVIEW_DUE` (owner + employee), `DEVPLAN_ACTION_OVERDUE`
(action owner + plan owner); category `development`.

## 6. Deviations from SUC_DESIGN (with reasons)
1. **One-Approved-slate rule is a DEFERRABLE exclusion constraint, not a partial unique index** — approving supersedes the
   previous version in the engine's single SaveChanges; an immediate index would refuse whichever row EF updated first.
2. **One current plan = Active OR OnHold** (design: Active). A plan on hold is still the person's plan; two would be ambiguous.
3. **`approvals.override` does not reach slates** (decide or read) — otherwise Admin, withheld `slates.read`, could open
   candidates through the override. The decider must be the routed approver or hold `slates.read`.
4. **Manager also gets `succession.self`** (design: Employee only) — every role is an employee first; a manager reads
   their own plan in ESS like anyone else.
5. **Default approval chain installed on first submit** (`SUCCESSION-SLATE-DEFAULT`: one HR Director step), insert-if-absent,
   rather than in the tenant provisioning bundle — the feature is opt-in, so tenants that never use it get no chain.
6. **Candidate visibility (`succession_candidate_visibility`) is platform-switched** like every opt-in key; when on, the
   employee sees target title, a coarse stage and readiness — never other candidates, a slate, an order or a 9-box cell.
7. **Snapshot column is `snapshot_json`** (EF naming of `SnapshotJson`), hashed over the canonical record serialisation so
   the hash is verifiable after jsonb's key reordering (`HashVerified` in the API).
8. **"Positions retired by the sweep"** also covers a designation whose org position was closed/deleted.

### Fix-round rulings implemented (Integration Owner)
- **F3 audit:** succession audit metadata carries ids (nomination/assessment/plan/action/slate/approval/position/pool) and
  hashes only — no employee id, potential, band, readiness, reason text or nomination link. The approval engine's own
  `approval.request_decided` row still carries the approver's free-text comment (engine-wide behaviour, unchanged).
- **F5 ESS:** the employee may add notes, move NotStarted → InProgress and *propose* completion of a manual action
  (`completion_proposed_at_utc`); `POST development-plans/actions/{id}/confirm-completion` (owner/HR) completes it; cancel and
  due dates are owner/HR only; linked actions are refused in ESS; reports count confirmed completions only.
- **F6 read-time eligibility:** `BuildSnapshotAsync` refuses leavers (409 `candidate_ineligible`), revise drops them, and
  summary/bench/coverage/gaps/over-commitment treat a snapshot candidate who has left as closed before the sweep runs.
- **F7 integrity:** anonymisation may rewrite only names, codes and free text (`succession_snapshot_skeleton` must be
  unchanged), records `anonymised_sha256`; the DTO's `integrity` is `Verified`, `Anonymised` or `Tampered`.
- **F8 learning edges:** linking an already-completed assignment is refused (link the path for a fresh cycle); a linked
  action completed by its link reopens when the link no longer is (revoked certificate), on the next read or sweep.
- **Slate decision bar** also refuses a decider who (or whose manager) is a live nominee for the position, even when the
  version leaves them off.

### Final-round rulings implemented (Integration Owner, N1–N6)
- **N1 chain:** see §4 — direct, dotted-line and stored skip-level lines, for the read bar and the decision bar.
- **N2 identity:** see §4 — claim ∪ login link; no resolvable employee ⇒ 403 `no_employee_record`, never fail open.
- **N3 own plan:** `devplans.manage` create/update/transition/add-action/update-action/confirm on a plan whose employee is
  one of the caller's own employees is 403 `self_decision_forbidden`; the employee uses ESS (notes, start, propose).
- **N4 audit + counts (choice: minimise, not redact-by-viewer):** succession audit metadata is the row's own entity id(s)
  only — no target position/pool id, no position/company id, no nominator marker, no reason code, no linked
  assignment/goal id, no risk/impact (the audit row's company and entity id stay, as for every module). Minimising at
  write time keeps the audit-log viewer unchanged for every module and needs no per-viewer filtering. Candidate counts
  (`liveNominations`, bench by readiness, `needsReviewCount`, a pool's `liveMembers`) are `null` for a caller without
  `succession.slates.read` (the position's accountable executive still sees its own); the summary report's aggregate
  KPIs are unchanged (definition + drill), and its position drills return masked rows.
- **N6 pseudonym:** retention writes `Former employee #<first 8 hex of sha256(tenantN:employeeId)>` to nameEn and nameAr
  and empties the code; the slate trigger (`succession_snapshot_rewrite_ok`, `succession_pseudonym`) refuses any other
  rewritten name/code, and any rewritten rationale/calibration reason other than the fixed redaction text.

## 7. Runbook
- Enable for a tenant: `PUT /api/platform/tenants/{id}/features/succession {isEnabled:true}`; optionally
  `succession_candidate_visibility`. Learning actions additionally need `learning` on (else 409 `learning_not_enabled`).
- Sweep: `SuccessionEligibilitySweep` (worker `succession-sweep`, singleton lease, daily; `Succession:SweepInterval`,
  `Succession:InitialDelay`). Heartbeat visible in `/health/ready/details`; deliberately not in `ProductionWorkerNames.All`.
- Retention: runs inside the D3 data-retention sweep as rule `succession.post-employment-3y` (report-only until the
  operator enables application, like every D3 rule).
- Diagnose a slate decision refusal: the 400 message names the bar (candidate / reports to a candidate / not routed).
- Verify a snapshot: `GET slates/{id}` → `hashVerified` (re-serialises the stored snapshot and compares `snapshot_sha256`).

## 8. Rollback
Code: revert the branch's commits. Schema: only while AddSuccession is the latest landed migration, `dotnet ef database update <the migration landed immediately before it>` runs `AddSuccession.Down` (re-derive the target from the landed list; migrations are regenerated at landing)
(drops triggers, functions incl. `succession_snapshot_skeleton`, the exclusion constraint and the 7 tables; nothing else is touched). Data: succession rows
are lost on rollback — export first if any tenant used it. Forward recovery without data loss: switch `succession` off.

## 9. V2 reconciliation note
The 7 tables are a named capability for the V2 rebuild (TARGET_SCHEMA.md): they map to a succession domain with RLS
shape (a) tenant + company; `talent_assessments.nine_box_cell` stays generated; the two triggers become state-machine
triggers of the target baseline; retention rows go into `retention_policies` when V2 introduces it.
