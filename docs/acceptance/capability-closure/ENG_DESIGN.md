# ENG — Engagement surveys: design record

Branch `feat/engagement-surveys`, built on `origin/main` 76985c55. The privacy model is the one in
REGISTER.md §3.2, and it is binding. This record covers the schema (§1), the API (§2), what each party can and
cannot learn (§3), the suppression rule (§4), the metrics (§5), the jobs (§6), the runbook (§7) and rollback (§8).

**Skills used:** kynexone-schema, system-design-review, survey-design, state-machine, localization-design, dataviz,
playwright-cli (as Playwright suites).

---

## 1. Schema — `AddEngagementSurveys` (20261011124500, regenerated in the privacy fix round)

The migration is expand-only. It adds **six tables** against an ENG cap of 7, plus two defaulted columns on
`tenant_hr_configs`. The table budget moves from 335 to 341.

The orphan table `employee_sentiment_pulses` is **left in place**. No DROP ships in this release (contract rule).
It should be dropped in the release after this one, and it is listed in the V2 reconciliation below.

Two pieces of raw DDL are frozen as `AddEngagementSurveys.PostgresOnlyDdl` and mirrored into the test `PostgresFixture`:

- `trg_survey_templates__published_immutable` refuses any change to the content, names, family or version of a
  non-draft template.
- `trg_survey_responses__append_only` refuses any `UPDATE` of a response.

### 1.1 `survey_templates` — `EngagementSurveyTemplate`

- **Lifecycle and why it is a table.** A versioned questionnaire. It moves Draft → Published (frozen) → Retired.
  No existing table holds versioned questions. The orphan pulse table is a single score per employee, and 360
  feedback is identified by design.
- **Keys and constraints.**
  - Primary key: `id` uuid. `family_id` groups the versions.
  - Unique `(tenant_id, family_id, version_number)`.
  - `ck_survey_templates__status`, `__version_positive` and `__published_stamp`.
- **Indexes.**
  - The unique index above, which blocks a duplicate "version 2".
  - `(tenant_id, updated_at_utc)` for the library list.
- **Tenancy.** `ITenantOwned` and `ICompanyScoped` (the config tier). A null company means tenant-wide.
- **Concurrency.** `version` int is a concurrency token.
- **Retention.** Kept while any survey references a version (FK RESTRICT from `surveys`). This keeps old results
  explainable.
- **Audit.** `engagement.template.created`, `.published` and `.versioned`.

### 1.2 `surveys` — `EngagementSurvey`

- **Lifecycle.** One cycle. It moves Draft → Scheduled → Open → Closed. A draft or scheduled survey can move to
  Cancelled instead. Each cycle carries its own mode, audience (JSON), dates, k, reminders and recurrence.
- **Keys and constraints.**
  - FK `template_id` → `survey_templates`, ON DELETE RESTRICT.
  - Unique `(tenant_id, series_id, cycle_number)`, which guarantees recurrence creates each cycle once.
  - CHECKs:
    - `ck_surveys__mode`, `__status` and `__recurrence`;
    - `__min_cohort_floor` (k ≥ 3);
    - `__frozen_k_not_lower`: `frozen_min_cohort_size` (the k results were decided at, set at close) is null or ≥ the
      survey's own k (fix round 3);
    - `__closes_after_opens`, `__cycle_positive` and `__launched_stamp`;
    - `__comment_dimension`: `comment_dimension` ∈ none, company, department, designation, grade, tenure (default
      department). Comments are read for the whole survey or by this ONE attribute, never intersected.
- **Indexes.**
  - `(status, opens_at_utc)` and `(status, closes_at_utc)` for the worker's due scans.
  - `(tenant_id, opens_at_utc)` for the list.
- **Tenancy.** `ICompanyScoped` (the config tier):
  - A null company is a group-wide survey that every invited employee can see.
  - Managing a group-wide survey needs group scope (`EngagementController.CompanyDenial`).
  - Results are cut to the caller's companies by the operational response rows.
- **Concurrency.** `version`.
- **Retention.** A draft, cancelled or closed survey can be deleted. Invitations, responses and answers go with
  it (CASCADE). Actions are kept with `survey_title`.
- **Audit.**
  - created, launched, closed (by hand or by schedule), cancelled, deleted (with `responsesDeleted`);
  - `threshold_changed`;
  - `cycle_created` (worker).

### 1.3 `survey_invitations` — `EngagementSurveyInvitation`: the participation ledger

- **Lifecycle.** Invited → Opened → Submitted. Either of the first two can move to Cancelled. Each row is one
  invitee × survey and **never holds an answer**.
- **Keys and constraints.**
  - FK `survey_id` CASCADE and `employee_id` → `employees` CASCADE.
  - Unique `(tenant_id, survey_id, employee_id)` for dedupe.
  - CHECKs:
    - `__status` and `__mode`;
    - `__identified_link_only_when_identified`: `identified_response_id` may be set only in Identified mode;
    - `__submitted_has_date` (Identified and Confidential only);
    - `__anonymous_ledger_minimal`: an Anonymous row never becomes Opened, keeps no `first_opened_on`,
      `submitted_on`, `draft_saved_on` or `session_token_hash`, and its `version` is 0 before submit and 1 after —
      so opening, re-opening or saving a draft leaves no date, no session and no counter behind (fix round 2);
    - `__submitted_no_draft`: a submitted row keeps neither a draft nor a session hash.
- **Indexes.**
  - `(tenant_id, employee_id)` for "my surveys".
  - `(survey_id, status)` for participation and reminders.
- **Deliberately absent.**
  - No `UpdatedAtUtc`/`UpdatedBy`, because the context would stamp the exact submission time.
  - `submitted_on` and `first_opened_on` are dates, Identified/Confidential only. Confidential keeps them so HR
    can target reminders at people who have not responded (the purpose of the mode).
  - `draft_cipher` is DataProtection-encrypted.
  - `session_token_hash` is SHA-256 of a single-use token — Identified/Confidential only. An Anonymous session token
    is a stateless, 12-hour DataProtection token bound to (tenant, survey, invitation) and stored nowhere; a re-open
    therefore does not revoke the previous token (the same employee), and a double submit is still one winner.
- **Tenancy.** `ITenantOwned` and `ICompanyScopedOperational`, using the employee's company at launch.
- **Concurrency.** `version` turns a double submit into one winner. Anonymous rows keep it as a concurrency token
  without bumping it before submit (a draft saved after a submit fails on `version = 0`).

### 1.4 `survey_responses` — `EngagementSurveyResponse`: no identity

- **Columns.**
  - `id` is a random uuid.
  - `tenant_id` and `survey_id`.
  - The cohort snapshot at submission: `company_id`, `department_id`, `designation_id`, `grade_band`,
    `tenure_band` (CHECK) and `cohort_manager_employee_id`.
  - `cohort_manager_employee_id` is the respondent's **line manager**, a team attribute with no FK. It is used only
    to cut a manager's own view, and it is allow-listed by name in the ratchet.
- **What is not there.** No CreatedBy/UpdatedBy/user/employee/invitation column, and **no date or time at all**: the
  survey cycle is the only "when". Two tests enforce
  this:
  - `EngagementPrivacyRatchetTests`, a reflection test on the entity and the EF model;
  - `Anonymous_ResponseRows_HaveNoIdentityColumn_AndTheDatabaseHasNoJoinPathToAPerson`, which checks
    `information_schema` on real Postgres.
- **FK and indexes.** The only FK is `survey_id` CASCADE. Index `(tenant_id, survey_id)`. Append-only by trigger.

### 1.5 `survey_answers` — `EngagementSurveyAnswer`: no identity

- **Columns and constraints.**
  - One row per response × question, unique `(response_id, question_key)`.
  - `numeric_value` 0–10 (CHECK), `choice_keys` (comma-separated, keys ≤ 16 chars) and `text_value` ≤ 4000.
  - `text_purged` flag; CHECKs `__has_value` and `__purged_has_no_text`.
- **FKs and indexes.** FKs `response_id` and `survey_id` CASCADE. Index `(tenant_id, survey_id, question_key)`
  for aggregation and the purge.
- **Tenancy.** `company_id` repeats the response's, so the company filter applies here too.

### 1.6 `survey_actions` — `EngagementAction`

- **Lifecycle.**
  - An action moves Open → InProgress → Done, and can be Cancelled from Open or InProgress.
  - Done can be reopened to InProgress.
  - Progress history is JSON. It is never queried.
- **FKs.**
  - `survey_id` → `surveys` SET NULL; the title is snapshotted.
  - `owner_employee_id` → `employees` RESTRICT.
- **CHECKs.** `__status`, `__source_kind` and `__done_stamp`.
- **Indexes.**
  - `(tenant_id, owner_employee_id, status)` for "my actions".
  - `(status, due_on)` for the overdue scan.
  - `(tenant_id, survey_id)`.
- **Concurrency.** `version`.
- **Audit.** `engagement.action.created`, `.updated` and `.status_changed`.

### 1.7 Tenant settings: two defaulted columns on `tenant_hr_configs` (no new table)

- `engagement_min_cohort_size`: default 5, CHECK ≥ 3.
- `engagement_free_text_retention_months`: default 24, CHECK 1–120.

Results use **max(survey k, tenant k)** at read time (`EngagementResultsService.EffectiveKAsync`). Raising the
tenant minimum therefore protects surveys that are already running.

### 1.8 V2 reconciliation note

The six tables map onto the V2 baseline as a "talent/engagement" domain. RLS shape (a) applies: tenant column
and policy. The rebuild must keep five things:

- no identity on responses and answers;
- the CHECKs;
- the two triggers;
- the cascade topology;
- the drop of `employee_sentiment_pulses`, which is deferred to the release after this one.

### 1.9 Rollback

- **Code.** Revert the branch.
- **Schema.** `dotnet ef database update AddEosbProvisionSnapshotsAndGlAdjustments` runs `Down`. It drops the
  triggers, functions, six tables and two columns, and no other data is lost.
- **Forward recovery without a deploy.** Switch the `engagement` flag off per tenant. The API answers 403
  `feature_not_enabled`, the worker skips the tenant, and its data stays as it is.

---

## 2. API contract

No route carries a response id or a session token. The token travels in the request body only. Error bodies are
`{ error, code, message }`, with these codes:

- `not_found`, `invalid_request`, `invalid_state`, `concurrency_conflict`;
- `survey_already_submitted`, `survey_closed`, `survey_session_expired`, `not_invited`;
- `group_scope_required`, `anonymous_no_person_view`, `survey_not_identified`, `no_employee_record`;
- `results_after_close` (409), `too_many_filters` (400), `scope_counts_as_filter` (400), `comment_filter_not_allowed` (400).

Every hidden value's `suppressionReason` is `suppressed` (one generic reason, by design).

**When results exist.** Confidential and Anonymous: results, comments, exports, trend points and scheduled delivery
only once the survey is **Closed**. While open, `results` returns `resultsAvailable: false`,
`resultsAvailableFromUtc` (the closing time) and the response rate of the caller's own scope (participation family); comments and export
answer 409 `results_after_close`; the report arm returns a note. Identified surveys stay live.

**Views.** At most two of: the caller's scope, filters and a breakdown (more is 400 `too_many_filters`, or
`scope_counts_as_filter` for a company- or team-scoped caller unless the filter's respondents all lie inside the
scope). `results` returns `scopeDimension` (`team`, `company` or null) so the UI offers one filter or one breakdown to a
scoped reader. Comments accept no filter or one filter on the survey's `comment_dimension`, and no breakdown (else
400 `comment_filter_not_allowed`).

### 2.1 HR and managers: `/api/engagement`

Gated by the opt-in flag.

| Method + path | Permission | Notes |
|---|---|---|
| GET `settings` · PUT `settings` | read or manage · manage (group scope) | k ≥ 3 (400 below), retention 1–120; audited |
| GET `options` | read/manage | Companies (in scope), departments, designations, grades, bands, mode notices |
| GET/POST `templates`, GET/PUT `templates/{id}` | manage | Edit only Draft (409 otherwise) |
| POST `templates/{id}/publish` · `templates/{id}/versions` | manage | Freeze · new draft version |
| GET `surveys` · GET `surveys/{id}` | read/manage | Readers see Open/Closed only; `submitted` only when ≥ k invited (whole survey) |
| POST `surveys/audience-preview` | manage | Count only |
| POST `surveys` · PUT `surveys/{id}` | manage | Draft only; k change audited |
| POST `surveys/{id}/launch` · `close` · `cancel` · DELETE `surveys/{id}` | manage | State machine; `version` required; audited |
| GET `surveys/{id}/non-respondents` | manage | Confidential/Identified only; 409 `anonymous_no_person_view` |
| GET `surveys/{id}/results?department&designation&grade&tenure&company&breakdown` | read | ≤ 2 dimensions; decided by the view family; cut to scope; after close (non-identified) |
| GET `surveys/{id}/trend` | read | Per cycle; an open non-identified cycle gives its response rate only |
| GET `surveys/{id}/comments?question=…[&<comment_dimension>=v]` | comments.read + read | Whole survey or one declared dimension; suppressed, shuffled, no names; access audited |
| GET `surveys/{id}/identified-responses` | identified.read | Identified mode only; data-scoped; audited |
| POST `surveys/{id}/export` `{format: csv\|xlsx, filters, includeComments}` | read (+comments.read for comments) | Same object as the dashboard; audited |
| GET `actions` · GET `actions/owners` · POST `actions` · PUT `actions/{id}` · POST `actions/{id}/progress` | read · actions.manage | Owners limited to the caller's data scope |

### 2.2 Respondents: `/api/ess/surveys`

The employee is always the one linked to the login (`CallerEmployeeResolver`).

| Method + path | Permission | Body |
|---|---|---|
| GET `` | ess.read | My invitations (no ids beyond the survey's) |
| POST `open` | ess.write | `{surveyId}`, which returns a fresh single-use token, the questions, the verbatim notice and the decrypted draft |
| PUT `draft` | ess.write | `{surveyId, token, answers}` |
| POST `submit` | ess.write | `{surveyId, token, answers}`. Returns 200, or 409 `survey_already_submitted` or `survey_session_expired`, or 410 `survey_closed` |
| GET `actions` · POST `actions/progress` | ess.read · ess.write | Actions I own |

### 2.3 Reports

Report key `engagement.results`:

- **Access.** `DataDomains.Engagement` (engagement.read). Its catalogue entry is "Engagement Survey Results".
- **Validation.** `ReportFilters.SurveyId` is required when scheduling.
- **Output.** The arm calls `EngagementResultsService` and returns `ToReportSections`, the same object the export
  endpoint serialises.
- **Comments.** Never included.

---

## 3. Privacy threat model: who can learn what

| Party | Can learn | Cannot learn |
|---|---|---|
| Respondent | Their own draft (encrypted at rest; only they can open it); that they submitted; the verbatim notice | Anyone else's anything |
| HR with engagement.read (group) | After close: aggregates of views of ≤ 2 dimensions that the survey's ONE view family shows. While open: the whole-survey response rate (participation family) | Any group of 1..k−1 people that is a linear combination of ANY visible views — those of every other reader included (R6, span audit); any live result to difference before/after one submission; who answered what |
| HR, company-scoped (or a group reader who selects a company) | The same family's views inside their companies: their company is one of the two dimensions (one filter or one breakdown more; two when the filter's people all lie inside the company). While open: their companies' response rate | Another company's slices; anything a group reader's views and theirs could isolate together — they are the same family |
| Manager (team-scoped) | The family's views of the teams (line manager at submission) in their reporting tree, each team shown only if each is; their team's participation (≥ k and safe against other scopes) | The rest of the survey; small teams; a sub-team below k hides the manager's whole view |
| engagement.comments.read | After close: comments of the whole survey or of one value of the survey's comment dimension, within the reader's scope, when every atom of the set family is 0 or ≥ k; shuffled, no names/ids/dates; audited | Comments under any other or intersecting filter (400); any comment that intersecting or subtracting two visible lists — across readers and scopes — would single out among fewer than k |
| engagement.identified.read | Per-person answers **of Identified surveys only**, in data scope; audited | Anything in Confidential/Anonymous |
| engagement.manage (Confidential) | Who has not responded yet (to target reminders) | What anyone answered |
| engagement.manage (Anonymous) | Counts only (≥ k invitees); 409 on the person view | Who has or has not responded or opened — the ledger records neither opened nor dates, and reminders go to every invitee |
| Audit log | Admin actions at survey level (create, launch, close, threshold, export, comment access, delete) | Any response, answer or invitation row (test `Audit_HoldsSurveyLevelAdminActionsOnly_NeverAResponse`) |
| Application logs and traces | Request paths contain survey ids only; the submit path writes no log line (ratchet) | Response ids, tokens, user/employee ids from engagement code (ratchet `NoEngagementLogCall_NamesAPersonAResponseOrAToken`) |
| Notification outbox | That an employee was invited or reminded (entity = survey id + reminder ordinal). In Anonymous mode everyone invited is reminded | Any response id; in Anonymous mode, who has not responded |
| Export file / scheduled email | Exactly the dashboard's suppressed numbers (byte-equal CSV, test `Golden_ScheduledDelivery_IsSuppressedIdentically…`); recipients re-checked every run | Comments (never scheduled); team-scoped recipients are refused |

### 3.1 Residual risks: out of scope of the promise, stated in the notice

1. **A DB or infrastructure operator.**
   - The response insert and the invitation update commit in one transaction. `xmin`, the WAL and physical row order
     can correlate them. Responses carry no date and an anonymous ledger no date at all.
   - Before submit, an **Anonymous** ledger row shows exactly: invited (status Invited, version 0, no dates, no
     session) and — only if the respondent saved a draft — a non-null `draft_cipher`, until submit or close clears
     it. So an operator can tell "saved a draft and has not submitted yet", nothing about merely opening. A draft
     save also rewrites the row (new `xmin`), which an operator watching tuples can see. After submit: status
     Submitted, version 1 — the participation flag the mode keeps by design.
   - Identified/Confidential rows keep Opened, `first_opened_on`, `draft_saved_on` and the session hash by design.
   - Drafts are decryptable by whoever holds the DataProtection key ring.
   - The notices say "People with direct access to the database or servers are outside this promise". The product
     never claims absolute anonymity.
2. **Timing.** A request log line (path `/api/ess/surveys/submit`, without a user) plus an access log of who was
   signed in at that second could narrow a submitter. Mitigated by not logging identity on that path; not eliminated.
3. **Across cycles.** Differences between recurring cycles with changed populations are not suppressed.
4. **Non-linear inference on aggregates.** R6 guarantees that no group of 1..k−1 people is a LINEAR combination of
   visible views, at the frozen k and across any later k changes. It does not stop reasoning from a single visible
   aggregate: a unanimous distribution ("100% agree") tells every member of that group (≥ k people) their answer, and
   rounded percentages can narrow a mean. These are properties of any shown aggregate and are not suppressed.
5. **Invitee drift.** Scope nesting and breakdown cells use the invitees' CURRENT department, designation, grade,
   tenure band and manager (company is the one at launch). Someone who moved after answering is listed under their
   new value, and a "nested" view can include a respondent whose answer-time attributes put them outside the
   reader's scope. This shows an aggregate of ≥ k people, never a person; it is a scope-accuracy gap, not a
   small-group leak.
6. **Free text.** A comment can identify its author by its content. The respondent is warned in the UI.
7. **Participation.** In Confidential mode, HR seeing who has not responded is the design of the mode; with
   results only after close it cannot be combined with a live result.

Closed in fix round 2 (review 2): **collusion between differently scoped readers** (one family per survey, scope as
a dimension) and **linear combinations of three or more views** (R6 atom closure; the linear-span audit is the test
oracle on the golden data, both reviews' scenarios and 400 randomised populations with every reader's scoped views
added). The second review's attacks run as regressions in `EngagementReview2RegressionTests` and
`EngagementViewFamilyPropertyTests`.

Closed in fix round 3 (review 3):
- **k-change differencing.** The survey's k is frozen at close (`surveys.frozen_min_cohort_size`; a survey closed
  earlier is frozen on its first results computation). Results are decided at the frozen k; a later, larger k only
  hides views of that family (`CohortViewFamily.BuildFrozen`); nothing lowers it (CHECK
  `ck_surveys__frozen_k_not_lower`, and the larger of frozen and current is always used). Participation is decided at
  the survey's own k (fixed at launch) the same way. Tests: the union of visible views at k … k+3 passes the span audit
  (numbers) and the atom audit (comment lists) on 300 populations, and the API test raises the tenant k to 6, 7 and 8,
  lowers it to 5 and 3, and every read stays inside the frozen visible set.
- **Participation existence.** Scope nesting, the team/company scope shape, and the list of breakdown cells (hidden
  ones included) are decided on the INVITEES, never the respondents; every hidden value carries the one reason
  `suppressed` (the engine's detailed reasons never leave the server). Test
  `ARefusalOrAListedCell_DoesNotChange_WhenOnePersonResponds`.

---

## 4. Suppression algorithm

`Infrastructure/Privacy/CohortSuppression.cs` — `CohortViewFamily` (used by every engagement surface) built on the
single-view primitives `EvaluateView` / `EvaluateBreakdown`. Pure; tests: `CohortSuppressionTests`,
`EngagementViewFamilyPropertyTests` (with `LinearSpanAudit`), `EngagementReview2RegressionTests`.

### 4.1 Definitions

- A **population** is one record per person counted in the WHOLE survey. For an item metric it is only the people
  who answered that item; metrics with the same people (every required item) share one family.
- **One family per survey and metric.** Its dimensions are company, department, team (line manager at submission),
  designation, grade band and tenure — the reader scopes included. Every reader's view is a view of this family.
- A **view** is at most two single-value filters on those dimensions; a breakdown counts as one.
- A **scoped view** (company HR, a group reader with a company selected, a line manager) is a union of family views
  `{scope = s} ∩ F` over the scope's values s, shown only if every non-empty one is, and its numbers are their sum.
  The scope therefore uses one of the two dimensions, unless every respondent F selects lies inside the scope — then
  the view is F itself (nested case; test `AScopedReader_WhoseFilterIsNestedInTheScope_KeepsTwoDimensions`). A
  manager who is also cut by company is scoped by team alone when the team lies inside their companies, else by
  (company, team) pairs. Participation uses the same construction over the invitees (dimensions company and team).

### 4.2 Rules

1. **R1 minimum.** n(view) ≥ k, where k is the larger of the survey's and the tenant's current k, never below 3.
2. **R5 four quadrants.** A two-filter view X ∩ Y is shown only if X∩Y, X∩¬Y, ¬X∩Y and ¬X∩¬Y are each 0 or ≥ k;
   otherwise root − X − Y + X∩Y isolates the small quadrant (review 2, attack A).
3. **R6 atom closure (final pass).** People are grouped by exactly WHICH visible views they are in — the atoms of the
   Boolean algebra the visible views generate. Every atom must have 0 or ≥ k people. While one does not, it is merged
   with its cheapest neighbouring atom by hiding every visible view that tells the two apart; cost is lexicographic:
   fewest company views, then department, team, designation, grade, tenure views, then two-filter views, then fewest
   people. **Why it is enough:** any linear combination of visible views is constant on atoms, so any group whose
   total a reader can compute is a union of atoms — 0 or ≥ k people. R2 (nested difference, including nesting by
   coincidence of the data) and R4 (residue of a partition) are consequences and are asserted by the property tests.
   Cost: incremental, O(people × views) — about 0.3 s for one 3,000-person metric family.
4. **R3 inheritance.** Finally, a two-filter view whose parent is hidden is hidden.
5. **(d) Contained items.** In any view (and any breakdown cell), a driver or the overall favourable score is shown
   only if every item it contains is shown there (an item nobody in that view answered adds nothing and does not
   count).
6. **Frozen k.** All of the above runs at the k frozen when the survey closed; a larger current k builds a second
   family and hides whatever it hides (`BuildFrozen`). The visible set is always a subset of the frozen family's.
7. **Invitees, not respondents,** decide scope nesting, the scope's shape and which breakdown cells are listed. The
   API reports every hidden value with the single reason `suppressed`.

**Comments** use the same family in **set mode** — R1 and R6 only — over the commenters, with dimensions company,
the survey's comment dimension and team. For lists (which a reader can intersect and subtract) R6 is exactly the
right condition.

**Oracle.** `LinearSpanAudit`: atomic cells over all six dimensions; a group of 1..k−1 people (any union of cells)
must not lie in the row space of the visible views plus every scoped view; exact elimination modulo 2^61−1; small
unions found by meet-in-the-middle. It runs on the golden data, both reviews' scenarios and 400 randomised populations
(k = 3 and 5, one company/team and several). Comment lists are audited by atoms (`FindSetLeak`, 200 populations).

### 4.3 Worked examples (k = 5)

Each one is pinned in tests.

| Situation | Counts | Decision |
|---|---|---|
| Department below k | Legal 3 of 30 | Legal hidden (every API reason is `suppressed`; the engine's reasons are given here) |
| Complement attack | total 7, A 5 | A hidden: 7 − 5 = 2 (R6: the 2 are an atom) |
| Complementary totals | total 30: Ops 20, Fin 7, Legal 3 | Legal hidden. Fin is hidden **secondarily**, otherwise 30 − 20 − 7 = 3 recovers Legal. Ops shown (complement 10) |
| Sibling-sum attack (review) | Root − Ops − `?department=Fin` | `?department=Fin` is hidden too (R3: hidden in the department breakdown) |
| Coincidental nesting | A = 7 (5 supervisors + 2 engineers), Supervisors = 5, all in A | One of them hidden: A − Supervisors = 2 |
| Three dimensions | `?department&designation&tenure` or two filters + a breakdown | Refused, 400 `too_many_filters` |
| Inclusion–exclusion (review 2, A) | X∩Y 10, X∩Z 10, W∩Y 10, W∩Z 1 | X∩Y hidden (`quadrant_too_small`); and R6 keeps root, X, Y from isolating W∩Z |
| Scope switch (review 2, B) | Ops: 14 in A1 + 1 in A2; group `?department=Ops` vs A1-selected `?department=Ops` | Never both shown with different members — one family; the A2 Ops person is an atom of one |
| Scoped reader + filter + breakdown | Manager: `?department=Ops&breakdown=tenure` | Refused, 400 `scope_counts_as_filter` (the team is one of the two); decided on invitees |
| k raised after close (review 3) | Golden survey frozen at 5; tenant k → 6, 7, 8, 5, 3 | Shown k is max(5, tenant k); every visible view is one of those visible at 5 |
| Cell existence (review 3) | Fin × designation; the only Finance engineer did / did not respond | The Engineer cell is listed (hidden) either way |
| Contained item | q3 answered by 3 of 12 | q3 hidden, so the enablement driver and the overall favourable score are hidden too |
| Live differencing (review) | Confidential survey, results before and after one submission | No results until close; then a single snapshot |
| Intersecting filter below k | Ops ∩ Analyst = 4 | Hidden (`below_minimum`) |
| Filter difference attack | Engineers 11 = Ops 10 + Fin 1; Ops 20 | Designation Engineer hidden (`secondary`): the Finance engineer is an atom of one; Ops stays |
| Item skips | q3 answered by 17 of 20 in Ops, 25 overall | Shown (8 ≥ 5); a 3-answer item would be hidden on its own |
| Comments | Ops wrote 7 of 10 comments | Ops comments hidden (`secondary`): the other 3 would be isolated |
| Tenant raises k after launch | k → 31, 30 respondents | Whole survey hidden |

A suppressed cell renders "Not enough responses to protect confidentiality (minimum k)." The UI's `metricCell`
always lets the visible flag win over any value. It is unit-tested in `unit/engagementDisplay.spec.ts`.

---

## 5. Metric definitions

These follow REGISTER §3.2.6. Code: `EngagementResultsService`.

**Population metrics.**

- **Respondent.** A submitted response. Drafts never reach these tables.
- **Response rate.**
  - Submitted ÷ invitations not cancelled, over the caller's whole scope. Both numbers are shown.
  - Per-cell response rates are **not** shown. The invitation cohort (the company at launch) and the response
    cohort (attributes at submission) can differ, which would produce misleading rates above 100 %.
  - Participation is shown only for scopes of ≥ k invitees — on results, on the survey list and while open.
  - While a Confidential/Anonymous survey is open, the only live figure is the whole-survey response rate.

**Answer metrics.**

- **Item mean.** The mean of answered values. A skipped item leaves that item's denominator.
- **Favourable.** Answers of 4 or 5 on the 5-point scale ÷ answered.
- **Driver.** The same over all answered 5-point items in the driver. Its population is people who answered at least
  one of them.
- **Overall favourable.** Over every answered 5-point item.
- **eNPS.** %promoters (9–10) − %detractors (0–6) over those who answered the eNPS item. At most one eNPS item per
  template.

**Rounding.** Half away from zero: percentages and eNPS to 1 dp, means to 2 dp.

The golden test (`EngagementGoldenReconciliationTests`) checks a 33-invitee dataset against a hand-computed table.
The checks are:

- response rate 90.9;
- q1 mean 3.50 / favourable 60.0;
- q2 3.77 / 63.3;
- q3 3.72 / 64.0 (25 answered);
- eNPS 13.3 (43.3 / 26.7 / 30.0);
- overall favourable 62.4;
- Operations 77.2 / eNPS 40.0;
- Mona's team 78.6 / 50.0.

---

## 6. Jobs and notifications

`EngagementSurveyWorker` is a `BackgroundService`. It runs every 5 minutes under `SingletonWorkerLease`
(`engagement-surveys`) and reports a heartbeat under its own name. That name is deliberately not in
`ProductionWorkerNames.All`, so a default-off feature cannot take `/health/ready` down. It processes only tenants
with the flag on. Each survey's step is isolated: one bad survey is logged by id and skipped.

| Step | Idempotent because |
|---|---|
| Open due Scheduled surveys → ledger | Unique (survey, employee); re-reads existing |
| Send invites (also catch-up when `InvitesSentAtUtc` is null; a launch sends inline only for ≤ 200) | Outbox dedupe key (tenant, `SURVEY_INVITE`, survey id, recipient, channel, content) |
| Reminders on day offsets: Identified/Confidential → not submitted; Anonymous → **every** invitee (the ledger keeps no opened state) | `RemindersSent` ordinal + entity id `{survey}:r{n}` |
| Close at `closes_at_utc`; discard drafts and sessions | State machine; audited once |
| Next recurring cycle | Unique (series, cycle) + `NextCycleCreatedAtUtc` |
| Overdue actions → owner (`SURVEY_ACTION_DUE`) | `OverdueRemindedOn` + entity id `{action}:{dueOn}` |

The **retention rule** `engagement.free-text-expired` (`EngagementFreeTextRetentionRule`) removes comment text from
surveys closed more than N months ago. It sets `text_purged` and keeps every number. It produces one audit row per
survey. Like every rule, it acts only with `DataRetention__ScheduleEnabled` and `DataRetention__ApplyDeletions`.

---

## 7. Operator runbook

- **Enable for a tenant.** `PUT /api/platform/tenants/{id}/features/engagement {isEnabled:true}` (platform owner).
  Existing HR Director and HR Manager roles get the new keys the next time `EnsureTenantRolesAsync` runs for the
  tenant. Admin gets them through the boot backfill. Otherwise grant `engagement.*` in Roles.
- **Purge comments.** Run the retention sweep (`DataRetention__ScheduleEnabled=true`; `ApplyDeletions=true` to act).
  A dry run reports candidates in `retention_purge_audits`.
- **A stuck survey.** Check the heartbeat row `engagement-surveys` and the log line "Engagement sweep step {Step}
  failed for {SurveyOrTenant}". The survey stays where it was. Fix the data (for example a broken `audience_json`)
  and the next sweep continues.
- **Delete a closed survey's data.** `DELETE /api/engagement/surveys/{id}` with engagement.manage. This is audited
  with the response count.
- **Never.** Do not query `survey_responses` joined to anything personal. Do not add logging to the submit path; the
  ratchet will fail the build.

## 8. Known gaps (also in the hand-back)

- Validation messages from the API are English. Arabic users get Arabic for every coded error (`error.<code>`), but
  free-text 400 messages arrive in English.
- An employee who moves company **while a survey is open** cannot submit. The ledger row keeps the company at
  launch, and the write guard refuses it (`company_scope_denied`).
- Existing tenants' HR roles receive the keys only when roles are re-ensured. There is no data migration of grants.
