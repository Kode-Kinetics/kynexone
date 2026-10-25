# Create-company wizard — design contract

Status: decided 2026-10-21 (Integration Owner). Builders implement to this; changes to a shape
below go through the Integration Owner, not around it.

## Why

Creating a legal entity today writes one `Companies` row and nothing else. Setup Studio only works
on a company that is already Active, and its Apply can create a company with no CR number and no
validation. A Group admin therefore creates a company in one place and configures it in five others,
and nothing tells them what is still missing. The fix is one guided journey: the user enters
details, each screen leads to the next, and one submit creates the company and its own setup, or
creates nothing.

## Decisions

1. **Deterministic only.** No model call anywhere in this flow (owner rule: policy intake off,
   manual setup). Defaults come from the country pack and seeders, never from `SetupAssistantService`'s
   LLM path. No free-text "notes" field.
2. **One authoritative create path.** The company is created through
   `OrganizationSetupService.CreateCompanyAsync` (all its validations), inside the same transaction
   as everything else. `SetupAssistantController`'s inline `new Company` is not used by this flow.
3. **Strict create.** A legal-name or CR-number match against an existing company is a 409, never a
   silent reuse.
4. **Atomic + idempotent.** One transaction, the existing tenant advisory lock `SETUP:{tenantId}`
   (the same lock is added to `CompanyEndpoints.CreateAsync` so the plan limit cannot race), and an
   `Idempotency-Key` (the draft id). A retry with the same key returns the first result.
5. **Dry-run and commit share one planner.** `POST /dry-run` and `POST /` call the same
   `CompanySetupPlanner.PlanAsync`; the review screen renders exactly what the commit will do.
   Every planned row is `create | reuse | conflict`. Any `conflict` blocks commit.
6. **Group-aware scope.** Per company: company row, branches, locations, cost centres, employee-ID
   rule, cutover, holiday calendar, statutory leave policies. Shared group catalog (tenant-wide):
   departments, designations, grades — existing codes are `reuse` (read-only), new codes need group
   scope (`Apply`'s rule at `SetupAssistantController.cs:1212-1225`). Tenant-wide admin areas
   (SMTP, notifications, system settings, reference lists, numbering, fiscal years) are NOT in the
   wizard; they appear in the readiness checklist only while unconfigured, labelled "Applies to all
   companies".
7. **Never re-link existing rows.** An existing department is never re-pointed to the new company's
   cost centre or branch. A cost-centre code clash is a `conflict`.
8. **Copy from a sister company.** A Group tenant may start from "Country defaults" or "Copy from
   <active company>": branches (renamed codes), cost centres, employee-ID rule shape, holiday
   calendar and leave-policy selection are copied into the draft for editing. Nothing is shared by
   reference.
9. **Server-side drafts.** New table `company_setup_drafts` (tenant-owned, RLS shape a). Autosave
   (debounced) + explicit "Save draft & exit". Drafts survive sign-out and devices. A draft never
   writes business rows. Optimistic concurrency by `version`.
10. **One MOL establishment number.** `Company.WpsEmployerId` is the authority, captured once,
    validated with `KsaWageFileRules.ValidateEstablishmentId` for SA. The wage file prefers it; if
    the bank-settings `MolEstablishmentId` or `GCCComplianceSetting.WpsAgentId` is also set and
    differs, the file refuses with a named mismatch (extend the existing check at
    `PayrollController.cs:9246-9283`). Only ids recorded for THIS company are compared; the tenant-wide
    GCC row is a fallback for companies with no number of their own. Ids are compared ignoring spaces and
    hyphens. Empty company value keeps today's behaviour.
11. **EOSB can't hard-fail on a new country.** If no `GCCComplianceSetting` row exists for the
    company's country, create the tenant-default row (CompanyId null) from the pack (SA: EOSB on,
    0.5 / 1.0, Fri,Sat). Never overwrite an existing row.
12. **Creation modes.** `GroupSelfServiceWithinLimit` → Active. `GroupDraftPlatformApproval` → the
    wizard says at step 1 that platform activation is currently disabled and refuses to start
    (no stranded drafts). `PlatformControlled` / single-company account with ≥1 company / plan limit
    → refused at step 1 with the gate's message, not at submit.
13. **Grades currency.** Grade bands are one currency per tenant. When the new company's currency
    differs from existing grades' currency, show a warning; per-company bands are out of scope.

## Steps (UI)

Seven steps. Rail with per-step state (not started / done / warning), live structure tree on the right
(Company → Branches → Locations; Cost centres; "Shared group catalog" node with Departments,
Designations, Grades, each row tagged new / reused). Next is blocked only on `*` fields.
EN/AR throughout, AR inputs `dir="rtl"`.

1. **Start** — plan usage ("2 of 5 companies"), creation-mode check, resume drafts list,
   "Start from: Country defaults | Copy from <sister company>".
2. **Company** — legal name EN*, legal name AR* (SA), trade name, country*, currency* (SA → SAR,
   locked), CR number* (SA: 10 digits), MOL establishment no.* (SA, validated), GOSI no.,
   Unified National No. (700, SA, 10 digits), VAT number, Qiwa establishment id (defaults to MOL
   no.), work-email domain + pattern, ERP company code, first payroll month* (default: 1st of next
   month → `CompanyCutover` Planned).
3. **Branches & locations** — inline rows: code*, name EN*, name AR, city*, time zone (pack default
   Asia/Riyadh), head office (exactly one). Locations per branch optional; if none, show the
   warning that employees can punch at any geofenced site in the workspace.
4. **Organisation** — departments (code, EN, AR, parent, branch, cost centre) with cost centres
   (code, EN, AR) on the same screen; designations (code, EN, AR, department, grade); grades (code,
   EN, AR, level, min ≤ mid ≤ max). "Use defaults" per section. Existing shared codes shown as
   reused, read-only.
5. **Work & pay rules** — resolved working week (confirm; company override only if changed),
   EOSB row status (exists / will be created), holiday calendar (pack), statutory leave policies
   (pack floors read-only), employee numbering (prefix, country/branch/department/year flags,
   padding, next number → `EmployeeIdRule` for this company).
6. **Company Super Admin** — the admin hierarchy is Group Super Admin (Admin role, group scope) →
   Company Super Admin (Admin role, scope = this company only). Choose one: "Invite a new company
   super admin" (full name EN/AR, official work email, which must be on the company's email domain
   when one is set; owner rule: KynexOne never creates mailboxes), "Make an existing user the company
   super admin" (picker of active tenant users; adds an Admin role scoped to the new company and
   leaves their other access unchanged), or "Group super admins will manage this company" (no
   company admin; readiness shows it as recommended). Only a group-scope Admin who holds the Admin
   role may grant it (the role ceiling the User Management `companyAdmin` flow already enforces).
   A company-scoped admin can never widen to group scope or grant group access.
7. **Review & create** — dry-run result grouped by step, tree again, conflicts block, Create.
   After create: readiness checklist (server-computed) with deep links
   `/setup?tab=<id>&companyId=<new>`: glMapping, establishment, locations, gccSettings,
   statutoryRules, bank/WPS settings ("before first payment"), Nitaqat profile, company admin
   user (User Management), plus tenant-wide items while unconfigured.

## API (all under `/api/company-setup`, roles Admin or HR Manager, permission `organization.setup.apply` for writes)

```
GET    /context                         → CompanySetupContext
GET    /defaults?country=SA&copyFrom={companyId?}  → CompanySetupDraftBody (prefilled)
GET    /drafts                          → CompanySetupDraftSummary[]   (current user's + tenant's open drafts)
GET    /drafts/{id}                     → CompanySetupDraft
POST   /drafts                          body CompanySetupDraftBody → CompanySetupDraft (version 1)
PUT    /drafts/{id}                     body { version, step, body } → CompanySetupDraft | 409 stale
DELETE /drafts/{id}                     → 204 (status Discarded, row kept for audit)
POST   /dry-run                         body CompanySetupDraftBody → CompanySetupPlan
POST   /                                header Idempotency-Key: {draftId}; body { draftId, version } → CompanySetupResult
GET    /readiness/{companyId}           → ReadinessItem[]
```

Shapes (C# records in `Application/CompanySetup/CompanySetupContracts.cs`; TS mirror in
`frontend/src/api/companySetup.ts`; camelCase JSON):

```
CompanySetupContext { canCreate: bool, refusalCode?: string, refusalMessage?: string,
  creationMode: string, accountType: string, companiesUsed: int, companiesAllowed?: int,
  isGroupScope: bool, sisterCompanies: { id, legalNameEn, countryCode }[],
  sharedCatalog: { departments: CodeName[], designations: CodeName[], grades: GradeRef[] },
  gradesCurrency?: string }
CodeName { code, nameEn, nameAr? }        GradeRef { code, nameEn, nameAr?, currency }

CompanySetupDraftBody {
  startFrom: "defaults" | "copy", copyFromCompanyId?: Guid,
  company: { legalNameEn, legalNameAr?, tradeName?, countryCode, currency, registrationNumber,
    molEstablishmentNumber?, gosiEmployerId?, unifiedNationalNumber?, taxNumber?,
    qiwaEstablishmentId?, emailDomain?, workEmailPattern?, erpCompanyCode?,
    firstPayrollMonth: "YYYY-MM" },
  branches: { code, nameEn, nameAr?, city, timeZoneId, isHeadOffice,
    locations: { code, nameEn, nameAr?, latitude?, longitude?, radiusMeters? }[] }[],
  costCentres: { code, nameEn, nameAr? }[],
  departments: { code, nameEn, nameAr?, parentCode?, branchCode?, costCentreCode? }[],
  designations: { code, titleEn, titleAr?, departmentCode?, gradeCode? }[],
  grades: { code, nameEn, nameAr?, level, minSalary, midSalary, maxSalary }[],
  rules: { workWeek: { weekendDays: string, override: bool },
    holidayCalendar: { usePack: bool }, leavePolicies: { usePack: bool },
    employeeNumbering: { prefix, useCountryPrefix, useBranchPrefix, useDepartmentPrefix,
      useYear, paddingLength, nextSequence } },
  companyAdmin: { mode: "invite" | "existing" | "groupOnly", existingUserId?: Guid,
    fullNameEn?, fullNameAr?, email? } }

CompanySetupDraft { id, version, step: int (1-7), status: "Open"|"Submitted"|"Discarded",
  body: CompanySetupDraftBody, createdByUserId, createdByName, createdAtUtc, updatedAtUtc,
  resultCompanyId? }
CompanySetupDraftSummary { id, legalNameEn, step, updatedAtUtc, createdByName }

CompanySetupPlan { ok: bool, items: PlanItem[], problems: Problem[], warnings: Problem[] }
PlanItem { area: "company"|"branch"|"location"|"costCentre"|"department"|"designation"|"grade"
  |"employeeIdRule"|"cutover"|"holidayCalendar"|"leavePolicy"|"gccSetting"|"workWeek"|"companyAdmin",
  code, label, labelAr?, action: "create"|"reuse"|"conflict", reason? }
Problem { step: int, field: string, code: string, message: string, messageAr?: string }

Errors (400/409/422): { code, message, messageAr?, field?, step? }. Problem.field and error.field
are dotted lower-camel paths with numeric indexes: `company.registrationNumber`, `branches.0.code`.
Refusal codes: company_limit_reached, account_type_single_company,
company_creation_platform_controlled, company_creation_platform_approval, not_group_scope.
CompanySetupContext may carry canGrantCompanyAdmin?: bool (same role ceiling as User Management).

CompanySetupResult { companyId, created: Record<area, int>, reused: Record<area, int>,
  readiness: ReadinessItem[], replayed: bool }
ReadinessItem { key, label, labelAr?, status: "done"|"todo"|"recommended",
  scope: "company"|"workspace", href, userId?: Guid }
  userId: only on the companyAdmin item while that admin has not signed in. The success screen's
  "Send sign-in link" posts it to /api/access/users/{userId}/password-reset-link (security.manage):
  emailed, or handed back once to copy and pass on through the company's own channel. A caller
  without security.manage gets the User Management link (href) instead.
```

## Schema (one migration, owned by the backend builder)

- `company_setup_drafts`: id uuid pk, tenant_id, created_by_user_id, status (CHECK Open/Submitted/
  Discarded), step smallint CHECK 1-7, version int, body jsonb (shape nobody queries; ≤128 KiB
  CHECK), result_company_id uuid null FK companies ON DELETE SET NULL, created_at_utc,
  updated_at_utc, submitted_at_utc. Index (tenant_id, status, updated_at_utc DESC) for the resume
  list. Data dictionary + ownership/retention row (Open drafts purge after 90 days idle) + RLS
  shape a.
- `companies.unified_national_number` varchar(20) null.

## Company Super Admin — how it is written

The admin is created/granted by the SAME service the User Management create-user and role-grant
paths use (no second user writer), with the same role-ceiling check, email-domain rule and
onboarding-access mechanism (welcome code / bilingual slip, not a password typed by the group
admin). If that service writes through the same `ZayraDbContext` connection, it joins the setup
transaction (company + admin are atomic). If it cannot, it runs immediately after commit inside the
same request, and on failure the result carries `companyAdmin: todo` in readiness with a one-click
retry from the checklist; the company is never rolled back for an admin failure. The builder
verifies which case applies and states it in the PR.

## Tests that must exist

Lost-response retry replays the same result; name clash 409; CR clash 409; cost-centre code clash
blocks; existing department never re-linked; company-scoped user cannot add shared codes;
draft-approval tenant refused at context; plan limit refused at context and re-checked at commit;
dry-run and commit produce the same item list; GCC row created only when missing; MOL mismatch
refuses the wage file; draft version conflict 409; another tenant's draft is 404; company super
admin is scoped to the new company only and cannot read a sister company; a company-scoped admin
cannot open the wizard or grant group scope; invite with an off-domain email is refused; existing
user keeps their other access; Browser Pilot e2e of the seven steps in EN and AR including save-draft → sign out → resume.

## Addendum A — one wizard for new AND existing companies (decided 2026-10-21)

The owner chose one guided journey for both cases. Guided Setup opens a chooser:
**Create a new company** or **Complete setup for an existing company** (one card per active company
the caller may manage, showing its readiness, e.g. "5 of 9 done"). The old 5-step Setup Studio
leaves Guided Setup.

### A1. Draft target
`CompanySetupDraftBody.existingCompanyId?: Guid`. Null = create (everything above, unchanged).
Set = complete that company. One open draft per (tenant, existingCompanyId): opening the
wizard for a company that already has an open draft resumes it (the drafts list shows both kinds).

### A2. Prefill
`GET /defaults?existingCompanyId={id}` returns a body built from what the company HAS today:
company fields, its branches + locations, its cost centres, the shared-catalog rows it uses
(departments/designations/grades referenced by its branches, cost centres or employees), its
EmployeeIdRule, its cutover month, its working-week/EOSB resolution, holiday calendar and leave
selection. Missing items are filled from the country pack and marked as new.
Every prefilled existing row carries `existingId` (Guid) in the body so the planner can tell
"edit this row" from "new row with the same code".

### A3. Planner actions
`PlanItem.action` gains `"update"` and `"unchanged"`:
create (new row) · update (existing row, changed fields; `reason` lists them) · unchanged (existing
row, no change) · reuse (shared catalog row used as-is) · conflict (blocks).
**No deletes.** Removing an existing branch, cost centre, location or admin is not offered in the
wizard (the row is shown read-only with "Manage in Setup → <tab>"); the wizard only adds and edits.

### A4. Locked identity on an existing company
Country and currency are read-only in existing mode, always (payroll, GOSI and bank files
depend on them). Legal name EN/AR, trade name, CR, MOL, GOSI, 700, VAT, Qiwa, email domain/pattern
and ERP code are editable and go through `UpdateCompanyAsync` with the same validations as create
(SA MOL format, CR uniqueness excluding itself, email-domain claimability). The first payroll month
is editable only while the cutover is still `Planned`; otherwise it is shown read-only. The prefill and every
draft read send `company.cutoverStatus` (`"Planned" | "Active" | "Closed" | null`, the status *now*; ignored on
input), so the screen locks the month up front with its reason; the planner's `first_payroll_month_locked` stays
the backstop.

### A5. Who may run it
- New company: group-scope Admin/HR Manager only (unchanged).
- Existing company: group-scope admins for any active company; a **Company Super Admin**
  (company-scoped Admin) for their own company only. Shared-catalog additions still require group
  scope (company admins pick from the catalog only), and step 6 lets a company admin add another
  company-scoped admin for their own company but never group scope.
- The context endpoint returns `manageableCompanies: { id, legalNameEn, legalNameAr?,
  countryCode, readinessDone, readinessTotal, openDraftId? }[]` for the chooser.

### A6. Commit
Same transaction, tenant lock, draft-id idempotency, audit (`company.setup_completed` for existing
mode, counts by action). The company row is updated, never recreated. A Draft/inactive company is
refused (the existing activation gap still applies).

### A7. The old Setup Studio's other capabilities (retired 2026-10-25)
The old studio ("Policy starter", `/setup?tab=aiSetup&path=policyStarter`) is gone. Everything it could configure now
has a Setup section or a wizard step, and a saved link to it opens All setup areas (`path=configuration`):

| Old studio section | Where it lives now |
|---|---|
| departments, designations, grades, cost centres, branches | Setup sections of the same names, and the wizard's organisation steps |
| grade pay components | Setup → Grades |
| leave types and policies, holiday calendars | Setup → Leave types, Leave policies, Holiday calendars |
| shifts | Setup → Shifts, Shift policy |
| working week, localisation | Setup → Language and region |
| pay components, statutory rules | Setup → Payroll accounting, Statutory rules |
| employee ID rule | Setup → Record numbering |
| benefit plans | Setup → Benefit plans |
| overtime policy | Setup → Overtime policies |
| attendance policy | Setup → Attendance rules (new; needs `attendance.policy_manage`) |
| HR governance config (`TenantHrConfig`) | no screen; eleven of its switches were never read and are retired, `establishmentEnforcementMode` is edited in Setup → Headcount & budget |

`/setup?tab=aiSetup` and any `returnTo` that pointed at the old studio open the chooser (with `companyId` → that
company's existing-mode wizard). The backend `/api/setup-assistant` draft endpoints remain until a follow-up removes them.

### A9. Changes made elsewhere since the draft started (three-way comparison)
The prefill stamps the company block, every existing branch, location and cost centre, and the
`rules.employeeNumbering` and `rules.workWeek` blocks with `baseline`: an opaque `{ field: string|null }`
map of the values it read (normalized: trimmed text, scale-free numbers, `"true"`/`"false"`; the company's
`firstPayrollMonth` is the cutover month, null with no cutover; the numbering fields are null with no rule).
The client carries it back unchanged through every edit, autosave and resume (rows are spread, never rebuilt);
new rows have none. Country and currency are locked (A4) and not compared. Per field, at plan time
(`CompanySetupBaseline`, one projection for the database row and the draft):

| user changed (draft ≠ baseline) | changed elsewhere (db ≠ baseline) | result |
|---|---|---|
| no | either | the database value stands; nothing is written for it |
| yes | no | update |
| yes | yes, draft = db | unchanged |
| yes | yes, draft ≠ db | problem `changed_since_draft_started` (blocks) |

The problem names the field (`branches.1.nameEn`, `company.tradeName`, `rules.workWeek.weekendDays`…), says in EN
and AR that it changed elsewhere and what it is now, and carries `current` (the value now, baseline form). The
review offers **Use the current value** (field := current) and **Keep mine** (that one field's baseline :=
current, saved by the normal autosave PUT; the next plan reads it as an edit on top of the newer value), and "Go
to" focuses the field. A draft with an existing row or block that has **no** baseline (saved before A9) is
refused as a whole with `draft_needs_refresh` (step 1, field `draft`, no items): nothing is guessed. The review's
**Reload the company's current setup** replaces the draft body with a fresh prefill (same draft, autosaved).

### A8. Tests (in addition to the list above)
existing-mode prefill round-trips with no changes → plan all `unchanged`/`reuse`, commit writes
nothing but the audit row; editing a branch name → `update`, one row changed, no duplicate;
adding a branch → `create`; country/currency change refused; company admin can run it for own
company and is 403 for a sister company; company admin cannot add shared-catalog codes; one open
draft per existing company; retry of an existing-mode commit replays; old `tab=aiSetup` links land
on the chooser; Browser Pilot real spec: complete an existing company in EN and AR.
A9 (Postgres): for a branch, a company field and a cost centre — draft opened, row changed directly, commit →
`changed_since_draft_started`, nothing written; untouched field → the newer database value stands; "keep mine"
then commits the edit; a counter moved by hiring is not written back; a draft without baselines →
`draft_needs_refresh`, nothing written; prefill and draft reads carry `cutoverStatus`. Unit: the decision table,
scale-free projection, baseline JSON round trip; client: baselines survive every form edit + JSON round trip.

## Addendum B — per-company organisation, live section saves, policies in the wizard (decided 2026-10-23)

The owner decided: separate companies in a group means separate everything. **Departments, designations and
grades are owned by one company**, like cost centres. There is no shared group catalog. Decision 6's
"shared group catalog" and Decision 13 (one grade currency per tenant) are **withdrawn**. Group convenience is
**copy by value** from a sister company (B3), never a live link.

### B1. Data model (one migration, owned by the schema builder)
- `departments`, `designations`, `grades` gain `company_id uuid null FK companies`, and the entities implement
  `ICompanyScoped` (the config-tier filter: the company switcher and company-scoped users see only their
  company's rows).
- Code uniqueness moves from (tenant, code) to (tenant, company_id, code) with `NULLS NOT DISTINCT`.
- `cost_centers` gains `name_ar varchar(160) null`. The wizard's cost-centre Arabic name is stored, no longer
  dropped.
- `grades.currency` defaults to the owning company's `DefaultCurrency`.
- Backfill (inside the migration, deterministic, idempotent; an audit row per moved or copied row):
  1. A row used by exactly one company is assigned to it. "Used by" means referenced by that company's
     employees, branches or cost centres, or by a department or designation that is itself used by it.
  2. A row used by several companies keeps its id for the company with the most employees on it (ties go to the
     oldest company). Every other user company gets a **copy** with the same code and values. That company's
     references are re-pointed to its copy, in every FK column to the table: employees, positions, grade
     entitlements, loan limits, approval rules, benefits, learning and the rest. Child config (e.g. grade pay
     scale components, grade entitlements) is copied too.
  3. A row used by nobody goes to the only company when the tenant has one. Otherwise it stays `company_id null`
     and Setup lists it as "Unassigned — choose the company".
- A read-only precheck SQL (`docs/runbooks/org-per-company-precheck.sql`) counts, per tenant, the rows each
  rule touches and the references that would move. The owner runs it on production before deploy.

### B2. Live section save (existing mode only)
`POST /api/company-setup/drafts/{id}/sections/{section}`, body `{ version }`, where section is one of
`costCentres | departments | grades | designations`.
- Same caller rules as the commit (A5).
- Same tenant setup lock and one transaction.
- Refused with 409 `section_apply_needs_company` for a create-mode draft. Create mode keeps rows in the draft
  until Create.
- The planner runs with the section in focus. Only problems whose field is in that section, plus the step-1
  gates, block.
- A reference to another section must already exist in the database:
  - a department's cost centre or parent (a parent may be new in the same section),
  - a designation's department or grade.
  Otherwise it is problem `reference_not_saved` ("Save Cost centres first: CC_X is not saved yet.").
- Rows are written through `IOrganizationSetupService`, so every row keeps its own validation and audit. The
  section gets one marker audit `company.setup_section_applied` with { draftId, section, fromVersion, counts }.
- The draft is rebased **in place**:
  - each row of the section gets `existingId` and a fresh A9 `baseline`;
  - order and other sections are untouched;
  - version + 1 and step 4.
- Response: `SectionApplyResult { draft, section, created, updated, unchanged, replayed }`.
- Retry: a request whose version is one behind, with a marker for (draft, section, fromVersion), returns the
  current draft with `replayed: true`. A blind retry never writes twice.
- A9 baselines now cover departments, designations and grades too (same decision table). The final
  `Update company` sees saved sections as unchanged.
- No deletes (A3 unchanged). An existing row is deactivated in its Setup tab.

### B3. Copy from a sister company
`GET /api/company-setup/copy-rows?fromCompanyId=&section=` returns that company's active rows of the section as
NEW draft rows: no existingId, no baseline, links kept by code.
- Group scope only, and the source must be an active company in the tenant.
- The client appends rows whose code is not already in the draft and says how many it skipped.
- Nothing is linked afterwards.

### B4. Wizard parts (frontend contract)
`PartId = 'costCentres' | 'departments' | 'grades' | 'designations' | 'workRules' | 'leave' | 'endOfService' | 'moreSettings'`.

| Step | Parts |
|---|---|
| 4 | costCentres, departments, grades, designations |
| 5 | workRules, leave, endOfService, moreSettings |

- The rail nests the parts under their step. The URL carries `&part=`.
- Back on a step's first part goes to the previous step's last part.
- Step 4 parts:
  - Existing mode: "Save & next" flushes the draft, then calls B2, adopts the returned draft and advances.
  - Create mode: "Save & next" validates the part, flushes the draft and advances.
- Step 5 parts:
  - `workRules` (working week, employee numbering) stays draft data applied at Update or Create.
  - `leave` embeds the existing Leave types, Leave policies and Holiday calendar editors, which save live.
  - `endOfService` embeds the existing Country rules (GCC settings) editor.
  - `moreSettings` lists the other settings. Embeddable ones open in place; the rest open their page with a
    "Back to company setup" bar.
- `RulesStep({ defaults, preview, previewState, part })` renders one part.
- No new draft step number (the DB CHECK stays 1–7). The part is UI state plus the URL.

### B5. Motion and responsiveness
Restrained motion: part and step transitions (150–200 ms fade/slide), rows animating in and out, a saved-state
tick, and rail progress.
- Only `transform` and `opacity` are animated, never layout.
- Everything is off under `prefers-reduced-motion`.
- No horizontal page scroll at phone width. Editable rows are a compact table when the column is at least 640px
  wide, and a stacked list below that.
