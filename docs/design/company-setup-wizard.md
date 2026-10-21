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
