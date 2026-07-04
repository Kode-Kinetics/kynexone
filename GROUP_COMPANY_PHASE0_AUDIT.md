# Phase 0 Audit — Group of Companies Architecture

**Date:** 2026-07-04 · **Scope:** read-only audit, zero code changes · **Verdict: READY FOR PHASE 1** (with 3 pre-existing P0 security defects that must be fixed first — see §8)

---

## 0. Executive summary

The proposed design does **not** conflict with the existing schema — it largely *matches* it. A Company / legal-entity layer is already ~60% built under SAP/Workday naming (`Company`, `ICompanyScoped`, `UserEntityAccess`, `entity_access` JWT claim, `EntityScopeContext`, EF dual query filters, `MaxCompanies` subscription gating, `CompaniesController`, and 5 dedicated company-scope isolation tests). **Tenant already means "customer/group"; Company already means "legal entity."** The Group concept is therefore a *completion + hardening* program, not a rebuild.

What stands between today and a sellable Group product:

1. Company scoping is adopted by only **3 of ~250 tables** (`Employee`, `PayrollRun`, `SalaryStructure`).
2. The scope model is **fail-open** (`StrictMode=false`; missing claims ⇒ see all companies), and the `is_group_scope` claim is consumed but **never emitted**.
3. **All policy/config surfaces are tenant-level** (tax, GL mapping, leave/OT/attendance policies, feature flags) even where the runtime data is company-scoped.
4. Background jobs, exports, analytics, purge, and audit logging are **company-blind**.
5. Frontend has company CRUD but **no ambient company context, no switcher, no company info on `/auth/me`**.

Three pre-existing security defects violate the program's own data-classification rules and must be fixed before (or as the opening of) Phase 1 — see §8.

---

## 1. Current architecture truth

- Backend: .NET 8, EF Core + Npgsql, `backend-dotnet/Zayra.Api` (~250+ DbSets, 69 controllers). Tests: `Zayra.Api.Tests` with Testcontainers Postgres, ~64 test files, run via `dotnet test` (CI `.github/workflows/ci.yml:34`).
- Frontend: **Next.js 15 App Router + React 19** at `frontend/` (README's "React + Vite" is stale). No Redux/React Query — Context + `useEffect` + axios. Tenant app under `app/(dashboard)/`, Platform Admin console under `app/platform/` with a separate axios client and separate token.
- Migrations: EF, timestamp-named, auto-`MigrateAsync()` on startup (`Program.cs:513`) plus `--migrate`-and-exit mode. Seeders run at boot (AuthSeeder, demo seeders, StatutoryRuleSeeder, GosiRuleSeeder, PricingConfigSeeder).
- Boot invariant: `Infrastructure/Boot/TenantOwnershipBootAssertion.cs` — startup **fails** if any mapped entity has `TenantId` without the correct `ITenantOwned`/`INullableTenantOwned` interface. There is **no equivalent assertion for `CompanyId`/`ICompanyScoped`** (gap: a `CompanyId` column without the interface is silently unfiltered).

## 2. Current TenantId meaning

**Tenant = the customer/subscriber account = the Group.** Evidence:

- `Domain/Entities/Tenant.cs:3-11` is bare (`Id, Name, Slug, IsActive, CreatedAtUtc`) — no legal-entity fields.
- Legal-entity identity lives on `Company` (`Models/Company.cs:8-18`): `LegalNameEn/Ar`, `TradeName`, `CountryCode`, `Jurisdiction`, `RegistrationNumber`, `TaxNumber`, `WpsEmployerId`, `GosiEmployerId`, `QiwaEstablishmentId`, `DefaultCurrency`.
- One tenant → N companies, gated by `TenantSubscription.MaxCompanies` (`Models/SaasPlatform.cs:129`, enforced `CompaniesController.cs:75-89`, default 1).
- Pricing already models `OrgType = "single" | "group" | "enterprise_holding"` + `NumCompanies` (`Models/PricingModels.cs:60-63`) — at **quote** level only; nothing carries it to the live subscription.
- Isolation: EF global query filters over `ITenantOwned`/`INullableTenantOwned` (`Data/ZayraDbContext.cs:2508-2575`), tenant resolved lazily from the `tenant_id` JWT claim (`ZayraDbContext.cs:31-39`); **254 tables** have `tenant_id` with `TenantId`-leading composite indexes (model snapshot).
- Caveats: `Employee.TenantId` is **nullable** (`Models/Employee.cs:26,29`) unlike nearly everything else; TenantId/CompanyId are **not auto-stamped on writes** (only audit fields are — `ZayraDbContext.cs:97-119`); when there is no HttpContext (jobs/seeders) the tenant filter is a **no-op**.

## 3. Current Company / legal-entity support

Already present, end-to-end but narrow:

- **Entities:** `Company` (above); `Branch` with non-nullable `CompanyId`; `Department` (hierarchy, relates to Company only **via Branch** — no direct `CompanyId`); `Designation`, `Grade` (+ pay-scale components), `CostCenter` (has `{TenantId, CompanyId}` index), `Location`.
- **Employee** already carries nullable FKs: `CompanyId, BranchId, DepartmentId, DesignationId, GradeId, CostCenterId` (`Models/Employee.cs:49-54`) — alongside legacy string fields (`Department`, `Branch`, …).
- **`ICompanyScoped`** (`Domain/Entities/ICompanyScoped.cs`): nullable `CompanyId`; EF filter auto-AND-s `(_isGroupScope || CompanyId == null || _companyScopeIds.Contains(CompanyId))` (`ZayraDbContext.cs:2530-2571`). **`CompanyId == null` rows are visible to everyone** — a load-bearing semantic and a poison default for forgotten inserts.
- **Adopters: only `Employee`, `PayrollRun`, `SalaryStructure`.** Attendance, Leave, Loans, Advances, Bonuses, Documents, Contracts, Visa records, etc. are tenant-scoped only.
- CRUD: `CompaniesController`, `OrganizationSetupService.CreateCompanyAsync`, frontend Setup page "Companies" tab; `companyId` already threads as a query param on branches/cost-centers and in Employees/Leave pages.

## 4. Current RBAC / scope behavior

- **Permissions:** hardcoded registry in `Infrastructure/Seed/AuthSeeder.cs:262-367` — ~78 `module.action` keys across ~24 modules, persisted (`Permission`/`RolePermission`/`UserRole`). Custom roles + permission matrix via `AccessController`. 15 seeded tenant roles with `AuthorityLevel`. Platform roles are a separate bundle (`PlatformRoles`, separate `PlatformUser` identities, separate JWT audience, `[RequirePlatformRole]`).
- **Enforcement:** no `[RequirePermission]` attribute — controllers call `HasPermission("key")` against `permission` claims; role checks via `[Authorize(Roles=…)]`; company scope enforced at the **data layer** (EF filter), not attributes.
- **Two orthogonal scope axes:**
  - `DataScopeLevel` (Own/DirectReports/Department/Team/Organization) — employee-record visibility, service-side (`DataScopeService`). **Does NOT consider company** — an Organization-level user bypasses company boundaries at the service layer.
  - `EntityScopeContext` (`Application/Common/EntityScopeContext.cs`) — company scope from `entity_access` JWT claims `{c: companyId?, r: role}`; group-level = all companies in tenant.
- **Multi-company grants already exist:** `UserEntityAccess` (`Models/AccessControl.cs:143`) — per-user rows, `CompanyId==null` ⇒ group-level; unique `(TenantId, UserId, CompanyId, Role)`; admin CRUD at `AccessController.cs:506-567`; `User.IsGroupScope` + `SetGroupScope` endpoint.
- **Live defects:**
  - `is_group_scope` claim is checked (`EntityScopeContext.cs:34`) but **never emitted** by `JwtTokenService.CreateAccessToken` — group scope works only via *absence* of claims (fail-open).
  - `EntityScopeOptions.StrictMode` defaults to **false**: token with no `entity_access` ⇒ sees ALL companies.
  - **Impersonation tokens** (`PlatformController.cs:906-926`) omit `permission` AND `entity_access` claims ⇒ impersonated sessions fail open to group scope.
  - `UserEntityAccess.Role` (per-company role) is stored but **ignored by enforcement** — permissions are global-per-user in the token.
  - Access modes: "selected companies" (rows) and "all incl. future" (null-CompanyId/group) exist; **"all current companies" (frozen snapshot) has no representation.**
- **Tests:** strong suite — `CompanyScopeTests` (5 facts incl. cross-tenant zero-leak, pooled-context no-leak, backfill compat), CrossTenantQueryFilter/Controller, TenantIsolation, TokenAudienceIsolation, RbacAuthorization, SensitiveFieldMasking (19), BypassLint, Subscription/FeatureFlag guards, MFA (25).

## 5. Current compliance model

- **CountryPack strategy framework** (`Application/CountryPack/` + `Infrastructure/CountryPack/`): KSA / UAE (mainland + DIFC) / Qatar packs — statutory deduction, EOSB, WPS/wage-protection exporters, nationalization trackers — resolved `country:jurisdiction → country → Default`, keyed DI (`Program.cs:239-303`). **Fail-closed at payroll:** `PayrollController.cs:204-218` returns `422 statutory_pack_not_configured` when only Default resolves — the one place already keyed off `Company.CountryCode + Jurisdiction`.
- **`StatutoryRule`**: effective-dated versioned config (`CountryCode + Jurisdiction + RuleKey + EffectiveFrom/To`), platform default + tenant override, reader ranks override > default (`StatutoryRuleReader.cs:41-55`). Parallel legacy `GosiContributionRule`. **These are the ready-made primitives for compliance readiness profiles** — extend precedence to company > tenant > platform.
- **Fail-closed validators exist:** `GosiReadinessValidator` (blocks on missing GosiReference / non-positive salary), `WpsSifValidator` (blocks on invalid IBAN ISO-13616, missing IdNumber, unapproved run).
- **Mismatches found:**
  - **`CompanyTaxPolicy` does not exist.** Income tax is a tenant-wide `SystemSettings Payroll/IncomeTaxRate` row (`PayrollController.cs:221-225`) despite `PayrollRun` being company-scoped. (Memory said "per-company tax policy" was built — it is per-*tenant*.)
  - GL mapping (`GlAccountMapping`/`GlAccount`) is tenant-wide (`LoadGlOverridesAsync`, `PayrollController.cs:1804-1811`) — different legal entities cannot post to different charts.
  - `LeavePolicy` **has** `CompanyId` + `CountryCode` columns (`Models/Leave.cs:30-31`) but doesn't implement `ICompanyScoped` — the column is unenforced.
  - Overtime/Attendance/Shift policies, holiday calendars, `SalaryComponent`, `PayrollCycle`, `TenantHrConfig`: tenant-only.
  - **ISO-2 vs ISO-3 split:** CountryPack uses ISO-3 (`SAU`), the new `IsoReference` uses ISO-2 (`SA`); `CountryCode` fields everywhere are unvalidated free text. Must standardize before compliance profiles key off it.
  - `SaudiComplianceDashboardService` / `ComplianceReportsController` aggregate strictly by tenant — no per-entity readiness view.

## 6. Current feature flag model

- `TenantFeatureFlag` (`Models/SaasPlatform.cs:141-150`) keyed by TenantId only; ~17 `FeatureKeys`. Enforced by `FeatureFlagGuardFilter` (URL-prefix → feature key), **fail-open when flag absent**, cached 2 min as `ff:{tenantId}:{featureKey}` (no company segment).
- `SubscriptionGuardFilter` blocks Suspended/Cancelled/expired at tenant level. Seat counters (`MaxEmployees/MaxUsers/MaxCompanies/MaxAdminUsers`) are tenant-wide, counted on read.
- Billing entities (`TenantInvoice`/`Line`/`TenantPayment`) tenant-level; `PricingQuote` knows about groups, live subscription does not.
- Frontend `FeatureFlagContext` is tenant-wide and fail-closed while loading.

## 7. Current audit log model

- Two models: `AuditLog` (`Domain/Entities/AuditLog.cs`, free-JSON `Metadata`, written via `AuditService.WriteAsync`) and `AdminAuditLog` (`Models/SetupAdmin.cs:184-197`, explicit `OldValuesJson`/`NewValuesJson`, written per-controller). No interceptor — writes are explicit per call site. No retention job; `PurgeTenant` intentionally keeps audit rows.
- **Neither model carries `CompanyId`** — cross-company admins reading audit trails will see other companies' entries once Group mode exists.
- **P0 defect:** `EmployeeHistory.SnapshotJson = JsonSerializer.Serialize(employee)` (`EmployeeManagementService.cs:507,512`; `EmployeesController.cs:1430`) persists **raw Salary, BankIban, WpsBankDetails, PassportNumber, IqamaNumber, MedicalInformation, DisciplinaryRecords** — bypassing `EmployeeSensitiveMask` (which is applied on API read paths only). Same full-entity serialization feeds the LLM prompt at `AiAdvisoryService.cs:485`. This violates the program's data-classification rules today, before any Group work.
- Redaction primitives exist and are unused here: `EmployeeSensitiveMask.Apply`, `AiRedactionService` (regex + SHA-256 hash).

## 8. Exact risks (ranked)

**P0 — fix before/with Phase 1 (pre-existing, independent of Group):**
1. **Unmasked PII in history/audit snapshots** (§7). Mask before serialize + stamp CompanyId. Also redact the `AiAdvisoryService.cs:485` employee prompt.
2. **Impersonation fail-open** — platform-minted tenant tokens have no `permission`/`entity_access` claims (`PlatformController.cs:906-926`); align with `CreateAccessToken`.
3. **`is_group_scope` never emitted + `StrictMode=false`** — company isolation is advisory until claims are emitted everywhere and strict mode flips (requires re-auth of all sessions; plan the cutover).

**P1 — will break/leak when Group mode ships if forgotten:**
4. Background workers see all companies: `_isGroupScope==true` when `HttpContext.User is null` (`ZayraDbContext.cs:58-66`); `QiwaSyncWorker`/`AiInsightEngine` loop by TenantId with `IgnoreQueryFilters()` — per-company entitlement/suspension will be ignored.
5. Salary/employee CSV exports rely on the ambient filter (`PayrollController.cs:1550,2408`; `EmployeesController.cs:121`) — group-scope caller exports every company's IBANs/salaries in one file; needs explicit company predicate.
6. No write-side CompanyId stamping + `CompanyId==null ⇒ visible to all` — one forgotten insert path leaks rows to every company user.
7. `DataScopeService` ignores company scope — Organization-level data scope crosses company boundaries at the service layer.
8. Feature-flag/subscription guards + 2-min cache are tenant-keyed; per-company entitlements unenforceable; cache would serve one company's entitlement to another.
9. `PurgeTenant` (`PlatformController.cs:875-881`) hard-deletes a fixed table list — will orphan company-scoped data.
10. `entity_access` claim bloat for many-company users; "AllCurrent" mode missing; per-company `Role` unenforced.
11. Frontend: no query cache ⇒ stale cross-company data after switch; `TenantSettingsContext`/`FeatureFlagContext` tenant-wide (wrong currency/RTL/features per company); localStorage current-company not cleared on logout; two-token platform/tenant split must not leak.
12. ISO-2/ISO-3 country-code inconsistency; free-text `CountryCode` unvalidated.
13. No boot assertion for `CompanyId`↔`ICompanyScoped` (silent unfiltered columns); `Department` lacks direct `CompanyId`; `Employee.TenantId` nullable anomaly; global exception handler echoes `InvalidOperationException.Message` to clients (`Program.cs:418-419`).

## 9. Exact missing pieces (delta to a competitive Group product)

Beyond the original spec (which covers data model, scoped access, company-aware APIs, admin UX, seeds, hardening), a Group offering that competes with Workday/SuccessFactors/Oracle HCM legal-entity models and beats Bayzat/ZenHR/Jisr in holding-company RFPs also needs:

1. **Group-consolidated reporting** — cross-company headcount, payroll cost, compliance readiness dashboards with per-company drill-down (extend `SaudiComplianceDashboardService`/analytics with company dimension + group rollup).
2. **Inter-company employee transfer** — move an employee between legal entities (new CR/GOSI/Qiwa establishment, sponsorship transfer) preserving service continuity for EOSB, leave balances, and history. This is *the* killer feature for GCC holding companies and is absent from the spec.
3. **Policy inheritance model** — group default → company override for tax policy, GL mapping, leave/OT/attendance/shift policies, holiday calendars, document requirements (reuse the `StatutoryRule` platform>tenant precedence pattern, extended to company).
4. **Real `CompanyTaxPolicy`** table (replacing the tenant `SystemSettings` row) + per-company GL charts.
5. **Group-level billing** — carry `OrgType`/`NumCompanies` from quote into live `TenantSubscription`; per-company seat counting; invoice rollup by company.
6. **Per-company feature flags/entitlements** (nullable CompanyId on `TenantFeatureFlag`, company-segmented cache key, guard + jobs honoring it).
7. **Company-stamped audit logs** + masked before/after values (data-classification rules).
8. **Account type** on tenant (single_company | group) surfaced in Platform Admin provisioning, driving UX (switcher visibility, group admin menus) — `maxCompanies` alone is not a product concept.
9. **Country-code canonicalization** (pick ISO-2 or ISO-3 once; map CountryPack keys; validate all `CountryCode` fields against `IsoReference`).
10. Later / backlog: group-level SSO & domain claiming, cross-company approval chains (group CFO approving all entities), data-residency per company, group org chart.

## 10. Phased implementation plan (amended)

Original phase gates retained; contents adjusted to repo reality. Migration safety rules apply throughout (no deletes/renames, nullable-then-backfill, generate-only, rollback notes, preserve all isolation tests).

- **Phase 1 — data model (non-destructive):**
  a) P0 fixes #1–2 from §8 (masking + impersonation claims — small, self-contained, zero schema risk) — or as an immediate standalone batch.
  b) `ICompanyScoped` adoption wave: add nullable `CompanyId` + `(TenantId, CompanyId)` indexes to remaining operational tables (attendance, leave, loans, advances, bonuses, documents, contracts, visa records, overtime, shifts…); wire `LeavePolicy.CompanyId` into the interface.
  c) New tables: `CompanyTaxPolicy`, `CompanyComplianceProfile`; add `AccountType` + group metadata to `Tenant`; `CompanyId` on `TenantFeatureFlag`, `GlAccountMapping`, audit models; company tier on `StatutoryRule`.
  d) Boot assertion: `CompanyId` property ⇒ must implement `ICompanyScoped` (mirror of tenant assertion).
  e) Default-company backfill plan: every tenant gets/uses a default `Company`; backfill all new `CompanyId` columns from `Employee.CompanyId`/default; only then consider required-ness per table.
  f) Migration + backfill tests.
- **Phase 2 — scoped access:** emit `is_group_scope`; grant-mode enum on `UserEntityAccess` (SelectedCompanies / AllCurrent / AllCurrentAndFuture) + claim format v2 (flag, not id enumeration, for all-mode); write-side CompanyId stamping/guard; make `DataScopeService` company-aware; `StrictMode=true` cutover plan (forced re-auth); negative security tests (cross-company read/write/export/impersonation).
- **Phase 3 — company-aware APIs:** company filters + explicit company predicates on exports/reports; per-company feature-flag enforcement (guard + cache key + jobs); compliance profile enforcement fail-closed; company-aware background workers; per-company payroll config resolution (tax, GL, statutory precedence company>tenant>platform); `PurgeTenant` cascade; ComplianceReports/analytics per company + group rollup.
- **Phase 4 — frontend:** Platform Admin account-type setup; Group Admin company management; `CompanySwitcher` (clone `LanguageSwitcher`, TopBar slot); `CurrentCompanyProvider` + `X-Company-Id` injection in `client.ts` interceptor; company list on `/auth/me`; company-scoped nav gating; per-company `TenantSettings`/flags; switch ⇒ remount/refetch strategy; clear company selection on logout.
- **Phase 5 — seeds + scenarios:** group demo seed (2–3 companies, KSA+UAE mix), enterprise test scenarios (scoped admin, group admin, cross-company denial), Playwright e2e.
- **Phase 6 — regression, security testing (incl. StrictMode verification, claim-bloat limits), performance review (filter/index plans on `(TenantId, CompanyId)`), final hardening.

**Verdict: READY FOR PHASE 1.** No schema conflict; the safest path is completion of the existing `ICompanyScoped`/`UserEntityAccess` scaffold, with §8 P0 items fixed first.
