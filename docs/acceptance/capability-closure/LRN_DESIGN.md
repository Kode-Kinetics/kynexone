# LRN — Learning management: design, contract and runbook

Branch `feat/learning` (base `76985c55`). Requirements: REGISTER.md §1 LRN-01..15. Content standard: REGISTER.md §3.1
(native Lesson/Link + cmi5 AUs; KynexOne = LMS + minimal LRS; SCORM/AICC/LTI excluded). Evidence: `evidence/lrn/EVIDENCE.md`.

Skills applied: kynexone-schema, system-design-review, state-machine, localization-design, form-design, playwright-cli.

## 1. Shape

| Layer | Where |
|---|---|
| Entities + vocabularies | `backend-dotnet/Zayra.Api/Models/Learning.cs` |
| EF mapping (CHECKs, FKs, indexes) | `Models/LearningModelConfiguration.cs` (one call from `ZayraDbContext`, the Release A convention) |
| Migration | `Migrations/20261011194628_AddLearning.cs` — CREATE-only, 10 tables, no raw SQL |
| Services | `Infrastructure/Learning/*` — catalogue, assignment, progress (completion pipeline), cmi5/LRS, reports, sweep + worker, sanitiser, question bank, CORS provider |
| Registration | `LearningServiceCollectionExtensions.AddLearning()` — one line in `Program.cs` |
| HR API | `Controllers/Learning/LearningCatalogController.cs`, `LearningRecordsController.cs` (`/api/learning`) |
| Employee API | `Controllers/Ess/EssLearningController.cs` (`/api/ess/learning`) |
| AU API (anonymous, token-authenticated) | `Controllers/Learning/LearningCmi5Controller.cs` (`/api/learning/cmi5`, `/api/learning/xapi`) |
| Frontend | `app/(dashboard)/learning`, `app/(dashboard)/ess/learning/**`, `src/views/Learning*.tsx`, `src/views/MyLearningPage.tsx`, `src/components/learning/*`, `src/api/learning.ts`, `src/lib/learning.ts`, `src/i18n/learning.ts` |
| Sample AU (SAMPLE, test fixture — never shipped in the app) | `frontend/e2e/fixtures/cmi5-fire-safety/index.html`, served on its own origin by `e2e/playwright.learning-journey.config.ts` (`webServer`, 127.0.0.1:5411); it follows `returnURL` only when it is http(s) |

Rollout: `FeatureKeys.Learning = "learning"`, opt-in (`OptInFeatures`: `/api/learning`, `/api/ess/learning`), declared in
`ModuleCatalog.NonModuleKeys`; `/api/learning` is classified under Core HR (as `/api/entitlements` is) so the guard and
coverage test have an owner, and the opt-in flag is what opens it. Frontend: nav item and the ESS "Learning" tab carry
`requiredFeatureKey: 'learning'`; every screen is wrapped in `LearningGate`.

Permissions (AuthSeeder, explicit keys, no prefix grant): `learning.read`, `learning.manage`, `learning.assign`,
`learning.report`, `learning.records.correct`. HR Director and HR Manager: all five. HR Officer: read + assign.
Manager: report (scoped to their reporting tree by `IDataScopeService`). Admin: all (catalog). Employees use `ess.read` /
`ess.write`; the employee is always the one linked to the login (`CallerEmployeeResolver`).

## 2. Tables (per-table template, SCHEMA_IMPACT.md)

All tables: `ITenantOwned` (tenant query filter). Snake-case columns. Every FK states ON DELETE. Expand-only migration;
**rollback for every table: drop the `AddLearning` migration — no other table is altered, no data elsewhere is lost.
Forward recovery: switch the `learning` flag off.** No table is hard-deleted by the module (cancel / revoke / supersede /
retire / archive are statuses), so retention is "with the employee's training record" until the V2 retention rules
absorb these tables (see §8).

### learning_courses — course VERSION rows
- **Lifecycle / why:** Draft → Published (immutable) → Retired. Identity (tenant, code); editing a published version
  creates the next Draft. No existing table holds content, a question bank, a pass mark or a completion rule.
- **Keys:** PK id. UNIQUE (tenant_id, code, version_number); partial UNIQUE (tenant_id, code) WHERE status='Draft'
  (`ux_learning_courses_one_draft_per_code`) and WHERE status='Published' (`ux_learning_courses_one_published_per_code`).
- **CHECKs:** `ck_learning_courses__status`, `__content_kind`, `__completion_rule`, `__move_on`, `__version_number`,
  `__pass_mark` (0–100), `__max_attempts` (1–20), `__validity_months` (1–120), `__completion_rule_fits_content`
  (§3.1: Assessment only on Lesson/Link with a bank; Cmi5MoveOn only on Cmi5; Acknowledgement only on Lesson/Link with no
  assessment — enforced for every non-Draft row), `__content_present`, `__published_stamped`.
- **Indexes:** the three above serve catalogue listing, "next version number", and "the" current version per code.
- **Tenancy:** tenant-wide configuration (one catalogue per group); no CompanyId.
- **Concurrency:** `version` int token. Question bank = immutable JSON snapshot (`assessment_json`, jsonb) — read whole,
  never filtered (rule 8). Lesson HTML stored **sanitised** (`LearningHtmlSanitizer`, rebuild-from-allow-list).
- **Audit:** created, updated, published (with versions retired), version_created, retired.

### learning_paths
- **Why:** assignment, order, prerequisites, eligibility and renewal window are about a set of courses.
- **Keys:** UNIQUE (tenant_id, code). CHECKs `__status`, `__effective_window`, `__renewal_window` (0–365).
- **Indexes:** (tenant_id, code); (tenant_id, status) for the list.
- **Tenancy:** tenant-wide; eligibility (company/department/designation/grade lists) is JSON evaluated in code
  (`LearningEligibilityCriteria`), never filtered in SQL.
- **Concurrency:** `version`. **Audit:** created, updated, activated, archived (archive also switches its rules off).

### learning_path_items
- **Why:** order, required/optional and the prerequisite per (path, course).
- **Keys:** FK path ON DELETE CASCADE (aggregate child); FK course RESTRICT; self-FK prerequisite RESTRICT.
  UNIQUE (path_id, sequence), UNIQUE (path_id, course_id). CHECKs `__sequence`, `__not_own_prerequisite`.
  Server rule: a prerequisite is an EARLIER, REQUIRED item (an optional prerequisite would be a one-way trap).
- **Indexes:** the two uniques (ordered list); (tenant_id, course_id) "which paths use this course".
- **Concurrency:** via the path's `version`; items are frozen once the path is Active.

### learning_assignment_rules
- **Why:** a standing instruction that must survive to catch future hires/transfers (sweep).
- **Keys:** FK path RESTRICT. Partial UNIQUE (tenant_id, path_id, target_kind, target_id) WHERE is_active.
  CHECKs `__target_kind`, `__due_in_days`.
- **Indexes:** (tenant_id, is_active) for the sweep. **Concurrency:** `version`. **Audit:** created, deactivated.
- **Scope rule:** only a group-scope user may create one (it is applied with tenant-wide reach by the sweep).

### learning_assignments — employee × path × cycle
- **Why:** due dates, mandatory flag, reminders, overdue are per person per path.
- **Keys:** FK employee RESTRICT, path RESTRICT, rule SET NULL, renewal_of_credential RESTRICT.
  **Partial UNIQUE (tenant_id, employee_id, path_id) WHERE status IN ('Assigned','InProgress')**
  (`ux_learning_assignments_one_active_per_employee_path`) — LRN-03's DB guarantee; a concurrent loser gets 409.
- **CHECKs:** `__status`, `__source`, `__cycle`, `__completed_stamped`, `__cancel_reason`, `__rule_source`, `__renewal_source`.
- **Indexes:** (tenant, employee, status) My learning; (tenant, status, due_date) reminder sweep; (tenant, path, status) reports.
- **Tenancy:** `ICompanyScopedOperational`, CompanyId stamped from the employee. Department/designation/grade
  snapshotted at assignment (reports by "department when assigned").
- **Concurrency:** `version`. Reminder dedupe columns `due_reminder_sent_at_utc`, `overdue_notice_sent_at_utc`.
- **Audit:** created (one row per person), cancelled (reason), completed.

### learning_enrollments — assignment × item, pinned to a course version
- **Keys:** FK assignment/path_item/course/employee RESTRICT. UNIQUE (assignment_id, path_item_id).
- **CHECKs:** `__status`, `__completion_source`, **`__completion_evidence`** ((status IN ('Completed','PendingVerification'))
  ⇔ source + evidence ref + completed_at all present — completion is never implied), `__verification_after_evidence`
  (verified_at only on Completed), `__progress`, `__attempts` (used ≤ allowance). `status` is varchar(24).
- **Verification columns:** `verified_at_utc`, `verified_by_user_id` — set when HR verifies a content-reported completion.
- **`cmi5_registration`** (cmi5 units): the enrollment's current cmi5 registration. Every launch uses it; moveOn is
  evaluated only over its statements. HR rejecting a reported completion, or revoking the credential, abandons the open
  sessions and starts a new registration, so the old statements stay as history but can never complete it again.
- **Indexes:** (tenant, employee, status) transcript / matrix. **Tenancy:** operational. **Concurrency:** `version`.
- **Audit:** content.opened (launch), enrollment.completed, enrollment.locked, attempts.reset (reason).

### learning_attempts
- **Keys:** FK enrollment RESTRICT. UNIQUE (enrollment_id, attempt_no); partial UNIQUE (enrollment_id) WHERE
  status='InProgress' (one open attempt). CHECKs `__status`, `__attempt_no`, `__score` (0–100), `__scored_when_submitted`.
- Question order and answers kept as JSON evidence; pass mark copied onto the attempt (witness).
- **Concurrency:** `version` — the loser of a double submit returns the stored result. **Audit:** started, passed/failed.

### learning_credentials
- **Lifecycle:** Active → Expired (sweep) / Revoked (HR, reason) / Superseded (correction or renewal; new row).
- **Keys:** FK employee/enrollment/course/path RESTRICT; self-FK supersedes RESTRICT; renewal_assignment SET NULL.
  UNIQUE (tenant_id, certificate_number); partial UNIQUE (supersedes_credential_id) WHERE NOT NULL (a chain, never a fork).
- **CHECKs:** `__status`, `__completion_source`, `__revoked_stamped`, `__superseded_stamped`, `__expiry_after_completion`,
  `__not_own_predecessor`.
- **Indexes:** (tenant, status, expires_on) sweep + expiring report; (tenant, employee); (enrollment_id).
- **Tenancy:** operational. **Concurrency:** `version`. Verification code printed on the certificate; verification needs
  number + code. **Audit:** issued, revoked, corrected, expired.

### learning_launch_sessions — cmi5 launch credentials
- **Keys:** FK enrollment RESTRICT. UNIQUE fetch_token_hash. CHECKs `__status`, `__move_on`, `__fetch_then_auth`,
  `__auth_expiry`. Only SHA-256 hashes stored.
- **Indexes:** fetch_token_hash (fetch); (tenant, enrollment, launched_at) relaunch/registration; (au_origin, status)
  CORS preflight; (status, auth_expires_at_utc) abandonment sweep.
- **Tenancy: `ITenantOwned` only — deliberately NO CompanyId.** The AU calls anonymously; in strict scope mode an
  anonymous principal has an EMPTY company scope, so a company-scoped table would read as empty and cmi5 would fail in
  production only. Reached only by token; its company is its enrollment's. (Integration test runs the AU path under
  strict scope.)
- **Concurrency:** `version` (a double fetch: one wins, the other gets error-code 1). **Audit:** cmi5.launched.

### learning_xapi_statements — the minimal LRS
- **Keys:** FK session/enrollment RESTRICT. **UNIQUE (tenant_id, statement_id)** — duplicate callbacks are no-ops.
  CHECK `__progress`. Index (tenant, registration) for moveOn evaluation.
- **Tenancy:** `ITenantOwned` only (same reason as sessions). Statement stored with LMS `stored`/`authority`.

Table budget: 335 → **345** (`table-budget.txt`, `TableBudgetRatchetTests` comment). Orphans: none (every DbSet read in
`Infrastructure/Learning`). CompanyScopeBootAssertion: passes (operational tables implement the interface; sessions and
statements have no CompanyId). No `IgnoreQueryFilters`, no raw SQL: the two places that must not depend on the caller's
company scope use the sanctioned `ScopedBypass.TenantWide` (tenant re-applied).

## 3. State machines

```
Course version:   Draft --publish--> Published --publish of next version / retire--> Retired      (Published, Retired immutable)
Path:             Draft --activate (≥1 required item, all codes published)--> Active --archive--> Archived
Assignment:       Assigned --first launch/attempt--> InProgress --all required items Completed--> Completed
                  Assigned|InProgress --HR cancel (reason)--> Cancelled
                  Assigned|InProgress --employee left (sweep: "Employee left", audited)--> Cancelled
                  Completed --a credential of it revoked (HR sets the new due date, audited learning.assignment.reopened)--> InProgress|Assigned
Enrollment:       NotStarted --launch--> InProgress --evidence (one of three)--> Completed
                  InProgress --cmi5 moveOn met, course requires HR verification--> PendingVerification
                  PendingVerification --HR verify (not own record)--> Completed (+ credential)
                  PendingVerification --HR reject (reason)--> InProgress (statements kept as evidence)
                  Completed --credential revoked (HR, due date)--> InProgress|NotStarted (fresh attempts; assignment reopened)
                  InProgress --last failed attempt--> Locked --HR reset (reason, +MaxAttempts)--> InProgress
                  any open --assignment cancelled--> Cancelled
Attempt:          InProgress --submit (server-scored)--> Passed | Failed      (a second submit returns the stored result)
Credential:       Active --expires_on reached (sweep)--> Expired
                  Active|Expired --HR revoke (reason)--> Revoked
                  Active|Expired --HR correct / renewal completed--> Superseded  (+ new Active row naming it)
cmi5 session:     Launched --fetch--> Fetched --initialized--> Initialized --terminated--> Terminated
                  any open --relaunch / auth window closed / fetch never used--> Abandoned (LMS "abandoned" statement)
```
Guards checked for one-way traps: prerequisites must be earlier REQUIRED items; a locked item always has HR reset; an
archived path's renewals are logged and notified, never retried hourly; a retired course keeps pinned enrollments working.

**Completion evidence (§3.1):** launch = `first_launched_at_utc` (+ audit `content.opened` / `cmi5.launched`); progress =
`progressed` statements or lesson view; completion only via `LearningProgressService.CompleteAsync`, whose three callers
are a passed attempt, an acknowledgement (Acknowledgement-rule course, content opened first; labelled "Acknowledged" on
the transcript), and a cmi5 statement set satisfying moveOn. No endpoint accepts a status, score or completion.

**cmi5 evidence is content-reported, not proof (Integration Owner decision).** The AU runs in the learner's browser and
the learner holds its auth token, so a learner can send any statement the AU could. Therefore:
- the transcript and certificate label a cmi5 completion **"Completed — reported by course content"**;
- each course version has `requires_hr_verification` (cmi5 only; CHECK). It defaults ON when the version issues a
  certificate or has a validity period. With it on, a met moveOn makes the enrollment **PendingVerification** — no
  credential, the assignment is not complete, the item shows "Waiting for HR to verify" to the learner; audited
  `learning.enrollment.completion_reported`, while `learning.enrollment.completed` is written once, on verification — until a user
  with `learning.records.correct` verifies it (`POST /enrollments/{id}/verify`, audited `learning.enrollment.verified`,
  then the credential is issued) or rejects it with a reason (`/reject-verification`, back to InProgress). The verifier
  can never be the learner (SoD, below). KPI `pending` + `/reports/pending` list what waits;
- publishing refuses `NotApplicable` (satisfied at launch) with a certificate or validity
  (`move_on_not_applicable_with_certificate`), a Passed-family moveOn without a pass mark (`mastery_score_missing`), and
  an assessment with a 0% pass mark (`pass_mark_missing`; the editor also refuses ≤ 0). The cmi5 pass mark is sent as
  `masteryScore` in LMS.LaunchData; a `passed` below it, or a `failed` at/above it, is refused (400);
- AU calls must carry the AU's `Origin` (missing or other → 403 origin_not_allowed). This is **defence in depth, not a
  security boundary**: it stops a copied token being replayed from a tool or another page, but the learner can forge any
  header — which is why the label and HR verification above exist.

**Separation of duties.** HR record decisions — verify / reject a reported completion, reset attempts, revoke, correct —
are refused (403 `self_decision_forbidden`) when the record is the actor's own (the login's linked employee). A
correction cannot leave a current (Active) certificate stating a score below the version's pass mark (400
`score_below_pass_mark`; revoke instead).

**Revocation reopens the learning.** Revoke requires `dueDate` (today or later, tenant date): the enrollment returns to
InProgress/NotStarted with a fresh set of attempts, the assignment is live again with that due date and fresh reminder
stamps, unless a newer live assignment of the path already carries the obligation. Completing again issues a new
credential; the revoked one stays.

**A rejected or revoked cmi5 completion stays undone.** Reject and revoke start a new cmi5 registration for the
enrollment (open sessions abandoned — their tokens get 403 session_closed). A relaunch uses the new registration and
moveOn sees only its statements, so an `initialized` alone completes nothing; real completed/passed statements on the new
registration complete it again (pending HR verification when the version requires it). A statement for an older
registration can never change the enrollment.

**Overdue — one definition (`LearningOverdue`), used by the KPI, the reports, the assignment views and the sweep's
notices:** an open assignment past its due date (tenant date), of a person whose training is chased — **Active**;
**Suspended** (still employed, the obligation stands; report rows carry `employeeStatus` and the UI labels them);
**Invited** only once their start date (`joining_date`) has come — and with something left for the employee to do. An
assignment whose open required items are all completions waiting for HR verification is shown as **"Waiting for HR to
verify"** (`awaitingVerification` on rows and views; KPI `pending`, which counts assignments so it equals its drill-down
rows), never as overdue, and gets no LEARNING_DUE / LEARNING_OVERDUE.

**Dates** (due, overdue, expiry, renewal windows, completed-on) use the tenant's own date (`ITenantClock`: Saudi tenants on
Riyadh time, others on their stored zone), not UTC.

## 4. API contract

Errors: `{ error, message, details? }` with the status below (`LearningErrorsAttribute`); cmi5 fetch errors use the
spec shape `{ "error-code", "error-text" }`. `DbUpdateConcurrencyException` → 409 `concurrent_change`.

| Route | Permission | Notes / errors |
|---|---|---|
| GET `/api/learning/courses[?status]`, GET `/courses/{id}` | learning.read \| manage | answer key only with learning.manage |
| POST `/courses`, PUT `/courses/{id}` | learning.manage | 400 invalid_request; 409 course_code_taken, course_immutable |
| POST `/courses/{id}/publish[?version]`, `/new-version`, `/retire` | learning.manage | 409 course_not_draft, draft_exists, course_not_published; 422 course_incomplete |
| GET/POST `/paths`, GET/PUT `/paths/{id}`, POST `/paths/{id}/activate`, `/archive` | read \| manage (\| assign for GET) | 409 path_items_locked, path_archived, path_not_draft; 422 path_incomplete, path_course_unpublished |
| GET/POST `/rules`, POST `/rules/{id}/deactivate` | read \| manage | 403 group_scope_required; 409 rule_exists, path_not_active; POST returns `{ rule, applied }` |
| POST `/assignments` `{pathId,targetKind:Employee\|Department\|Designation\|Grade,employeeIds?,targetId?,dueDate?,dueInDays?,isMandatory}` | learning.assign | `{created,skipped,refused,results[{employeeId,employeeName,outcome,reasons[]}]}`; single employee duplicate → 409 duplicate_assignment; 409 path_not_active/path_not_effective/path_course_unavailable |
| GET `/assignments[?pathId&employeeId&status]`, GET `/assignments/{id}`, GET `/enrollments/{id}/attempts` | assign \| report \| records.correct | data-scoped; out of scope → 404 |
| POST `/assignments/{id}/cancel {reason}` | learning.assign | 409 assignment_not_live |
| POST `/enrollments/{id}/reset-attempts {reason}` | learning.records.correct | 409 enrollment_not_locked; 403 self_decision_forbidden |
| POST `/enrollments/{id}/verify`, `/enrollments/{id}/reject-verification {reason}` | learning.records.correct | 409 not_pending_verification; 403 self_decision_forbidden |
| GET `/credentials[?employeeId&status]`, GET `/credentials/{id}` | report \| records.correct | |
| GET `/credentials/verify?number&code` | read \| report \| records.correct | 404 unless both match |
| POST `/credentials/{id}/revoke {reason,dueDate}`, `/correct {reason,completedOn?,scorePercent?}` | learning.records.correct | 400 (no/past dueDate), score_below_pass_mark; 403 self_decision_forbidden; 409 credential_not_current |
| GET `/reports/summary`, `/reports/status[?pathId&departmentId&status=open\|…]`, `/reports/overdue`, `/reports/locked`, `/reports/pending`, `/reports/completion?groupBy=path\|department`, `/reports/expiring?days=` (+`&format=csv`) | learning.report | HR: their companies; manager: their reporting tree. CSV via ReportTabulator (formula-safe) |
| GET `/api/ess/learning`, GET `/enrollments/{id}`, GET `/credentials/{id}` | ess.read | own records only; others → 404 |
| POST `/api/ess/learning/enrollments/{id}/launch {returnUrl?}` | ess.write | Lesson html / Link url / cmi5 launchUrl; 409 prerequisite_incomplete, enrollment_locked, already_completed, assignment_not_live |
| POST `…/enrollments/{id}/acknowledge` | ess.write | 409 completion_rule_mismatch, content_not_opened |
| POST `…/enrollments/{id}/attempts`, POST `/api/ess/learning/attempts/{id}/submit {answers}` | ess.write | 409 no_assessment, attempts_exhausted; repeat submit → stored result |
| POST `/api/learning/cmi5/fetch/{token}` | anonymous (token) | `{"auth-token"}` once; reuse → 400 error-code 1; expired/unknown → error-code 2; Origin other than the AU's → 403 error-code 3 |
| POST/PUT `/api/learning/xapi/statements[?statementId]` | Basic auth-token + `X-Experience-API-Version: 1.0.x` + the AU's `Origin` | 401 auth_required/invalid_token/token_expired; 403 session_closed, origin_not_allowed, actor_mismatch, registration_mismatch, activity_mismatch, session_mismatch, verb_reserved, feature_not_enabled; 400 invalid_statement (incl. passed below mastery); 409 statement_conflict; 413 request_too_large / statement_too_large; 429 statement_quota_exceeded (Retry-After 60) and the rate limit; 503 busy (Retry-After 1) |
| GET `/api/learning/xapi/activities/state?stateId=LMS.LaunchData&activityId&agent&registration` | Basic auth-token | contextTemplate (sessionid), launchMode Normal, launchMethod OwnWindow, moveOn, returnURL; other stateIds 404; writes 403/501 |
| GET `/api/learning/xapi/about`, `/agents/profile` | — / token | version 1.0.3; profile 404 (no preferences stored) |

### Ingestion under concurrency, and limits

`StoreStatementsAsync`: (1) each statement row is inserted on its own; the UNIQUE (tenant, statement_id) index turns a
replay or a concurrent duplicate into a no-op (the module's ON CONFLICT DO NOTHING — a duplicate id from another session
is 409); stored times keep the batch order. (2) Session/enrollment state is then **recomputed from every stored statement
of the registration** (status, furthest progress, latest judged score — a pass outranks a fail at the same instant —
and moveOn) under the rows' version tokens, retried up to 8 times with jitter; completion is applied exactly once (the
enrollment's version token; one `satisfied`, one credential, one audit row). A conflict outlasting the retries is 503
`busy` with `Retry-After: 1`, never a 500 and never a lost `completed`/`passed` (they are stored before state is applied).
Proven by `ConcurrentStatements_FourteenParallelWithDuplicates_AllStored_CompletionExactlyOnce`.

Limits (`Learning:` settings): request body ≤ 256 KB on the AU routes (declared length → 413 before binding;
`[RequestSizeLimit]` for chunked bodies); one statement ≤ `MaxStatementChars` (64 K chars, 413); ≤ 50 statements per
request; ≤ `MaxStatementsPerSession` (2 000) per launch (429, relaunch); `Cmi5RequestsPerMinute` (600) per client address
on the anonymous fetch/xAPI routes (fixed window, the app's rate limiter and `ClientIpResolver`). Assessment submissions
must fit the bank: known question ids only, no more choices than options, ids ≤ 32 chars (400, no attempt consumed).

## 5. cmi5 conformance notes and limitations

Implemented: launch URL with `endpoint`, `fetch`, `actor` (account: homePage = `Learning:ActorHomePage` or API base,
name = Employee.PublicId — no personal data), `registration` (constant per enrollment), `activityId`; one-time fetch
returning a Basic credential; LMS.LaunchData (incl. `masteryScore` from the version's pass mark); LMS-issued `launched`, `satisfied`, `abandoned`; statement validation of
actor/registration/object/session id, verb/result consistency (passed⇒success true, failed⇒false, completed⇒completion
true); reserved LMS verbs refused from the AU; idempotency by statement id (also under concurrent replays); moveOn
Passed / Completed / CompletedAndPassed / CompletedOrPassed / NotApplicable (satisfied at launch); OwnWindow; CORS only
for origins of open sessions, re-checked per session after authentication.

Limitations (declared): one AU per course (no multi-AU course structure / cmi5 course import); no
`waived`; State API only LMS.LaunchData (AU-written state documents → 501); no agent profile (learner preferences);
statements GET (query) not provided; voiding not applied; fetch errors return HTTP 400 with the spec body. SCORM, AICC,
LTI, video telemetry, authoring beyond sanitised rich text and provider catalogues stay excluded (§3.1 item 4).

## 6. Operator runbook

- **Enable for a tenant:** `PUT /api/platform/tenants/{id}/features/learning {"isEnabled":true}` (platform Owner/Admin).
  Off by default; switching off closes `/api/learning` and `/api/ess/learning` (403 feature_not_enabled) and makes the AU
  endpoints refuse the tenant's tokens; data is kept.
- **Settings (`Learning:` section):** `FetchTokenTtl` (default 00:10:00), `AuthTokenTtl` (02:00:00), `PublicApiBaseUrl`
  (absolute API URL the AU calls; set it behind a proxy — default is the request's scheme/host), `ActorHomePage`,
  `DueReminderDays` (7), `SweepInterval` (01:00:00), `SweepPageSize` (500), `MaxStatementChars` (65536),
  `MaxStatementsPerSession` (2000), **`Cmi5RequestsPerMinute` (600)** — the rate limit on the anonymous cmi5 routes
  (`/api/learning/cmi5/fetch`, `/api/learning/xapi/*`), a fixed one-minute window per client address as resolved by
  `ClientIpResolver` (proxy-asserted address honoured the same way as the sign-in limits; over the limit → 429). Set it
  with `Learning__Cmi5RequestsPerMinute`; a whole office behind one NAT address shares one window, so raise it for large
  sites. The cmi5 `returnURL` is accepted only for an origin in `Cors:AllowedOrigins`/`CORS_EXTRA_ORIGINS`.
- **Acceptance stacks write no files into the repo:** local document storage is `<content root>/storage`, so the e2e /
  journey API is started with `--contentRoot <temp dir>` (holding a copy of `appsettings.json`); `backend-dotnet/Zayra.Api/storage/`
  is git-ignored as a backstop.
- **Sweep (`LearningSweepWorker`, heartbeat name `learning-sweep`, not part of readiness):** hourly, one instance
  (SingletonWorkerLease). Per enabled tenant, every step filters in SQL, orders oldest-first and **pages through every
  row** (`SweepPageSize`, default 500; a row handled but still pending is skipped and logged, so the sweep cannot spin):
  close open assignments of people who left (Offboarded/Archived/Inactive/Terminated/Exited or deleted → Cancelled
  "Employee left", audited; they never count as overdue); apply active rules; LEARNING_DUE (due within 7 days) and
  LEARNING_OVERDUE once per assignment (active employees only); credentials inside their path's renewal window (one SQL
  window per distinct `renewal_window_days`) → one renewal assignment per path cycle (cycle+1, only the expiring courses)
  + LEARNING_EXPIRING once; past expiry → Expired + LEARNING_EXPIRED once; stale cmi5 sessions → Abandoned. Completing a
  course supersedes EVERY current credential of the same employee + course code (a multi-course renewal leaves nothing to
  expire). Idempotent: stamp columns + outbox dedupe; one failed row is logged and skipped. Notification category:
  `learning`.
- **Who is notified:** the employee's own login. With no login, the notice goes to their line manager's login (the
  title names the employee); with neither, nothing is sent, `no_recipient_at_utc` is stamped on the assignment /
  credential and the row is retried at most once a day (counted as `NoRecipient`). The sweep never writes a user-less
  (tenant-wide) in-app notice.
- **Failure recovery:** AU never reports → enrollment stays InProgress, session abandoned by the sweep, employee
  relaunches (same registration, new session). Locked learner → Records → Reset attempts (reason). Wrong certificate →
  Correct (new number, old Superseded) or Revoke. Sweep errors → logs "Learning sweep: …" and `worker_heartbeats`.
- **Strict scope:** the AU path is proven under `EntityScopeOptions.StrictMode = true` (production) with an anonymous
  principal.

## 6a. Integration tasks (not built)

- **External completion evidence (e.g. Moodle):** not built. The path is: an `ILearningEvidenceSink`-style method on
  `LearningProgressService` that records an externally reported completion with its source and reference, goes through
  `CompleteAsync` (so the same `requires_hr_verification` rule and PendingVerification apply), and labels it with its
  source. It needs a new `completion_source` value (CHECK change) and the evidence contract, so it waits for that contract.

## 7. Rollback

Code: revert the branch's commits (no shared table altered). Schema: `dotnet ef database update 20261011004635_AddEosbProvisionSnapshotsAndGlAdjustments --context ZayraDbContext`
(drops the 10 learning tables — destroys learning data only). Prefer forward recovery: switch the flag off.
Permissions rows (`learning.*`) are additive and harmless if left.

## 8. V2 reconciliation note

For the 76-table rebuild: the ten tables map to a learning domain (catalogue: courses/paths/items/rules; records:
assignments/enrollments/attempts/credentials; interop: sessions/statements). V2 must give them RLS shape (a) tenant
policy, a `retention_policies` row (training record: employment + statutory minimum; session token hashes may be purged
30 days after expiry), and data-dictionary lines. Audit rows go through the single audit table as today.
