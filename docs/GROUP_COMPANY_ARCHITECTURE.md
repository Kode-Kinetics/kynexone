# Group → Company Architecture

**Summary.** Zayra models a customer as a Tenant (the group / subscriber — the hard multi-tenancy boundary) that owns one or more Companies (legal entities — the compliance and operational boundary). Companies own Branches (physical work locations) and are organized through Departments. Company-level data isolation is enforced in the data layer via two EF Core global-query-filter tiers over `ICompanyScoped` / `ICompanyScopedOperational`, plus write-side CompanyId stamping/validation in `ZayraDbContext.SaveChangesAsync`. A tenant's `AccountType` (`SingleCompany` | `Group`) decides whether the multi-company product surface is unlocked at all.

---

## 1. The four boundaries

```
┌──────────────────────────────────────────────────────────────────────────┐
│ TENANT  (customer / group)               HARD ISOLATION BOUNDARY         │
│  Tenant.cs — Id, Name, Slug, AccountType, IsActive                       │
│  EF global tenant filter on every ITenantOwned/INullableTenantOwned      │
│  entity. Cross-group access is impossible: no claim, grant, or scope     │
│  mode ever crosses a TenantId.                                           │
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
| Customer / group | `Domain/Entities/Tenant.cs` | **Hard isolation.** One tenant = one subscribing customer (a group of companies or a single company). | EF global query filter on every tenant-owned entity (`Data/ZayraDbContext.cs`, `ApplyTenantQueryFilters`); `TenantOwnershipBootAssertion` fails boot if an entity carries `TenantId` without the interface. Cross-tenant access has no code path. |
| Legal entity / compliance / operations | `Models/Company.cs` | Carries the legal identity: `LegalNameEn/Ar`, `RegistrationNumber`, `TaxNumber`, `WpsEmployerId`, `GosiEmployerId`, `QiwaEstablishmentId`, `CountryCode`, `Jurisdiction`, `DefaultCurrency`. Payroll runs, attendance, leave, finance and compliance records hang off it. | Company-tier EF query filters + write-side stamping (below). Per-entity governance via `CompanyTaxPolicy` / `CompanyComplianceProfile` (`Models/CompanyGovernance.cs`). |
| Physical / work location | `Branch` | Non-nullable `Guid CompanyId` child — a branch cannot exist outside a company. Visibility follows the parent Company. | Deliberately allow-listed out of the scoped-filter interface in `Infrastructure/Boot/CompanyScopeBootAssertion.cs` (the nullable-CompanyId interface contract cannot apply to a non-nullable FK). |
| Organizational | `Department` | Hierarchy for reporting lines and data scoping (`DataScopeLevel.Department`). | Organizational, not an isolation boundary; relates to Company through the org structure. |

## 2. AccountType: SingleCompany vs Group

`Tenant.AccountType` (`Domain/Entities/Tenant.cs`) is a **product-behavior** switch, deliberately distinct from `TenantSubscription.MaxCompanies`, which stays the commercial limit: *AccountType drives what the product does; MaxCompanies drives how much of it the customer paid for* (verbatim intent from the `TenantAccountTypes` doc comment).

- **`SingleCompany`** (default for existing and new tenants): the tenant operates exactly one internal default company. `CompaniesController` blocks creating a second active company for non-Group tenants regardless of MaxCompanies (`Controllers/CompaniesController.cs:92-101`). The frontend shows no company switcher; the company dimension exists in the schema but is invisible to the customer.
- **`Group`**: unlocks multi-company provisioning, the company switcher, and group-admin surfaces. Set by Platform Admin (`PlatformController.SetAccountType`, `Controllers/PlatformController.cs:934-970`) or auto-promoted by `CompanyScopeBackfill` step 5 when a tenant already operates >1 active company.
- **Downgrade guard**: `Group → SingleCompany` is rejected while the tenant has multiple active companies ("Deactivate the extra companies first", `PlatformController.cs:951-961`).

## 3. The two company-scoping tiers

Defined in `Domain/Entities/ICompanyScoped.cs`; filter composition in `Data/ZayraDbContext.cs` (`SetTenantFilterNonNull` / `SetTenantFilterNullable`, ~lines 2740-2816). Both tiers AND a company predicate into the single per-entity `HasQueryFilter` alongside the tenant and soft-delete predicates. The naming contract is strict: the property MUST be exactly `CompanyId` of type `Guid?`.

### `ICompanyScoped` (base) — config / templates
`CompanyId == null` means **"tenant-wide / shared across companies"** and is visible to every user in the tenant. Use only for configuration and template entities (e.g. `LeavePolicy`, `SalaryStructure`, tenant-default `CompanyTaxPolicy` rows).

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
2. **(b) Tenant has exactly one active company** → that company (the SingleCompany case).
3. **(c) Actor's scope covers exactly one of the tenant's active companies** → that one.
4. **(d) Otherwise FAIL CLOSED** — `InvalidOperationException("company_scope_required…")`: Group tenants require explicit company context.
5. (Zero active companies → no company dimension yet; null passes through.)

**Modified rows** → reassigning or nulling-out a non-null `CompanyId` is blocked (`company_reassignment_blocked`) — inter-company transfers must arrive as an explicit workflow that opts in. Assigning a previously-null `CompanyId` is allowed (repair) but access-validated.

## 6. Data backfill and boot order

On startup: `MigrateAsync` → `CompanyScopeBackfill.RunAsync` (`Infrastructure/Boot/CompanyScopeBackfill.cs`, idempotent, config-gated by `CompanyScope:Backfill`) → `CompanyScopeBootAssertion`. The backfill ensures every active tenant has a default company, assigns employees, walks employee-linked operational tables per company, sweeps stragglers to the default company, and promotes multi-company tenants to `AccountType = Group`. Details in the migration runbook (`docs/GROUP_COMPANY_MIGRATION_RUNBOOK.md`).
