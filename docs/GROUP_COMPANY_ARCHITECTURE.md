# Organisation → Company Architecture

**Summary.** A KynexOne installation serves one **Organisation** (the group), created once by `--setup-organisation`, which owns one or more Companies (legal entities — the compliance and operational boundary). Until the single-organisation schema restructure, the organisation is stored as the only row of `tenants` and every row still carries its `TenantId`. Companies own Branches (physical work locations) and are organized through Departments. Company-level data isolation is enforced in the data layer via two EF Core global-query-filter tiers over `ICompanyScoped` / `ICompanyScopedOperational`, plus write-side CompanyId stamping/validation in `ZayraDbContext.SaveChangesAsync`. The licence's `AccountType` (`SingleCompany` | `Group`), copied onto the organisation when the licence is imported, decides whether the multi-company product surface is unlocked at all.

---

## 1. The four boundaries

```
┌──────────────────────────────────────────────────────────────────────────┐
│ ORGANISATION  (the installation's one group)                             │
│  Tenant.cs — Id, Name, Slug ("main"), AccountType, IsActive              │
│  Exactly one row; created only by --setup-organisation. The EF tenant    │
│  filter on ITenantOwned/INullableTenantOwned still applies until the     │
│  schema restructure removes TenantId.                                    │
│                                                                          │
│   ┌────────────────────────────┐   ┌────────────────────────────┐        │
│   │ COMPANY  (legal entity)    │   │ COMPANY                    │        │
│   │ compliance + operational   │   │ (sibling — invisible to    │        │
│   │ boundary: CR/Tax/GOSI/Qiwa │   │  company-scoped users of   │        │
│   │ ids, jurisdiction, payroll │   │  the other company)        │        │
│   │ runs, tax policy,          │   │                            │        │
│   │ compliance profile         │   │                            │        │
│   │  ┌──────────┐ ┌──────────┐ │   │  ┌──────────┐              │        │
│   │  │ BRANCH   │ │ BRANCH   │ │   │  │ BRANCH   │              │        │
│   │  │ physical │ │ (work    │ │   │  └──────────┘              │        │
│   │  │ location │ │ location)│ │   │                            │        │
│   │  └──────────┘ └──────────┘ │   │                            │        │
│   │  ┌────────────────────┐    │   │                            │        │
│   │  │ DEPARTMENT (org    │    │   │                            │        │
│   │  │ boundary/hierarchy)│    │   │                            │        │
│   │  └────────────────────┘    │   │                            │        │
│   └────────────────────────────┘   └────────────────────────────┘        │
└──────────────────────────────────────────────────────────────────────────┘
```

| Boundary | Entity | Nature | Enforcement |
|---|---|---|---|
| Organisation (group) | `Domain/Entities/Tenant.cs` | The installation's one organisation (a group of companies or a single company). There is no second organisation to isolate from. | `OrganisationProvisioningService` is the only code that inserts it (`OrganisationCreationDoorTests`). The EF tenant filter (`Data/ZayraDbContext.cs`, `ApplyTenantQueryFilters`) and `TenantOwnershipBootAssertion` remain until the restructure. |
| Legal entity / compliance / operations | `Models/Company.cs` | Carries the legal identity: `LegalNameEn/Ar`, `RegistrationNumber`, `TaxNumber`, `WpsEmployerId`, `GosiEmployerId`, `QiwaEstablishmentId`, `CountryCode`, `Jurisdiction`, `DefaultCurrency`. Payroll runs, attendance, leave, finance and compliance records hang off it. | Company-tier EF query filters + write-side stamping (below). Per-entity governance via `CompanyTaxPolicy` / `CompanyComplianceProfile` (`Models/CompanyGovernance.cs`). |
| Physical / work location | `Branch` | Non-nullable `Guid CompanyId` child — a branch cannot exist outside a company. Visibility follows the parent Company. | Deliberately allow-listed out of the scoped-filter interface in `Infrastructure/Boot/CompanyScopeBootAssertion.cs` (the nullable-CompanyId interface contract cannot apply to a non-nullable FK). |
| Organizational | `Department` | Hierarchy for reporting lines and data scoping (`DataScopeLevel.Department`). | Organizational, not an isolation boundary; relates to Company through the org structure. |

## 2. AccountType: SingleCompany vs Group

The account type comes from the licence (`LicencePayload.AccountType`) and is copied onto the organisation by `LicenceImporter`. It is a **product-behavior** switch, distinct from the licence's `MaxCompanies` limit: *AccountType drives what the product does; MaxCompanies drives how many companies the licence allows*.

- **`SingleCompany`** (the default): the organisation operates exactly one company. `CompanyCreationGate` blocks creating a second active company whatever `MaxCompanies` says. The frontend shows no company switcher; the company dimension exists in the schema but is invisible to the user.
- **`Group`**: unlocks multi-company provisioning, the company switcher and group-admin surfaces. `CompanyCreationGate` enforces the licence's `MaxCompanies`. `CompanyScopeBackfill` step 5 still promotes an organisation that already operates more than one active company.
- **Downgrade guard**: importing a `SingleCompany` licence is refused while the organisation has more than one active company ("Deactivate the extra companies or obtain a Group licence", `LicenceImporter`).

## 3. The two company-scoping tiers

Defined in `Domain/Entities/ICompanyScoped.cs`; filter composition in `Data/ZayraDbContext.cs` (`SetTenantFilterNonNull` / `SetTenantFilterNullable`, ~lines 2740-2816). Both tiers AND a company predicate into the single per-entity `HasQueryFilter` alongside the tenant and soft-delete predicates. The naming contract is strict: the property MUST be exactly `CompanyId` of type `Guid?`.

### `ICompanyScoped` (base) — config / templates
`CompanyId == null` means **"organisation-wide / shared across companies"** and is visible to every user in the organisation. Use only for configuration and template entities (e.g. `LeavePolicy`, `SalaryStructure`, organisation-default `CompanyTaxPolicy` rows).

Filter: `_isGroupScope || CompanyId == null || _companyScopeIds.Contains(CompanyId)`

### `ICompanyScopedOperational` — operational records
Attendance, leave, payroll, loans, documents, compliance evidence, etc. Same filter with one hardening: **for company-scoped (non-group) users, `CompanyId == null` rows are NOT visible.** On operational tables a null is a migration/backfill transient, never a legitimate "shared" state — treating it as broadly visible would leak any row whose insert path forgot to stamp CompanyId. This is the **poison-default prevention**. Group-scope users still see null rows so unassigned data remains discoverable and repairable.

Filter: `_isGroupScope || (CompanyId != null && _companyScopeIds.Contains(CompanyId))`

A mis-declared entity (has `CompanyId` but no interface, wrong CLR type, or unmapped property) fails boot via `Infrastructure/Boot/CompanyScopeBootAssertion.cs` — see the StrictMode/cutover doc for the production rollout of that assertion.

## 4. Convention indexes

Every company-scoped table carries a `(tenant_id, company_id)` composite index, added by migration `20260705053606_Phase1BCompanyScopeFoundation` (32 CreateIndex operations). Hot paths get wider composites: attendance `(tenant_id, company_id, work_date)`, leave_requests and payroll_runs `(tenant_id, company_id, status)`, and governance tables `(tenant_id, company_id, status, effective_from)` for policy resolution. See `Migrations/PHASE1B_ROLLBACK_NOTES.md`.

## 5. Write-side stamping resolution order

Read filters cannot stop a request from *writing* into another company, and a forgotten CompanyId would create poison-default rows. `ZayraDbContext.EnforceCompanyScopeOnWritesAsync` (`Data/ZayraDbContext.cs:141-253`) therefore runs inside every `SaveChangesAsync` for `ICompanyScopedOperational` entries. System contexts (no HttpContext / tenant claim — seeders, backfill, background workers) are trusted group-scope by design; for user contexts:

**Added rows, `CompanyId` set** → the actor's `EntityScopeContext` must cover that company, else `UnauthorizedAccessException("company_scope_denied…")`.

**Added rows, `CompanyId` null** → server-side resolution, in order:
1. **(a) Owning employee's company** — via `EmployeeId` (int/int?) or `EmployeeIntId` linkage. The "safe route": ESS and manager flows never carry a company explicitly. Actor must have access to the resolved company.
2. **(b) The organisation has exactly one active company** → that company (the SingleCompany case).
3. **(c) Actor's scope covers exactly one of the organisation's active companies** → that one.
4. **(d) Otherwise FAIL CLOSED** — `InvalidOperationException("company_scope_required…")`: Group organisations require explicit company context.
5. (Zero active companies → no company dimension yet; null passes through.)

**Modified rows** → reassigning or nulling-out a non-null `CompanyId` is blocked (`company_reassignment_blocked`) — inter-company transfers must arrive as an explicit workflow that opts in. Assigning a previously-null `CompanyId` is allowed (repair) but access-validated.

## 6. Data backfill and boot order

On startup: `MigrateAsync` → `CompanyScopeBackfill.RunAsync` (`Infrastructure/Boot/CompanyScopeBackfill.cs`, idempotent, config-gated by `CompanyScope:Backfill`) → `CompanyScopeBootAssertion`. The backfill ensures the organisation has a default company, assigns employees, walks employee-linked operational tables per company, sweeps stragglers to the default company, and promotes a multi-company organisation to `AccountType = Group`. Details in the migration runbook (`docs/GROUP_COMPANY_MIGRATION_RUNBOOK.md`).
