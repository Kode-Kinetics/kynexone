# Group → Company Administration Guide

**Summary.** Administration of a Group account is split across three personas with strictly separated authority: the **Platform Admin** owns the SaaS commercial layer (what a customer is entitled to), the **Group Admin** owns customer-side governance (who inside the group may see what), and the **Company Admin** runs one legal entity's day-to-day HR operations. The separations are enforced in code — platform users are a separate identity population with a separate JWT audience, and company-scoped users cannot see sibling companies at the data layer (see `docs/GROUP_COMPANY_ACCESS_MODEL.md`).

---

## 1. Platform Admin — SaaS commercial layer

Platform Admins operate the Platform Admin console (`frontend/app/platform/`, backed by `Controllers/PlatformController.cs`) under separate `PlatformUser` identities and platform RBAC bundles. Responsibilities:

### Account type
- Set `AccountType` per tenant: `SingleCompany` | `Group` (`PlatformController.SetAccountType`, `Controllers/PlatformController.cs:934-970`; also settable at tenant creation, `:1085-1088`). AccountType drives product behavior (switcher, group surfaces, multi-company provisioning); it is distinct from the commercial limits.
- **Downgrade rule:** `Group → SingleCompany` is **blocked while the tenant has more than one active company** — the API returns "Cannot downgrade to SingleCompany while the tenant has multiple active companies. Deactivate the extra companies first." (`PlatformController.cs:951-961`). Deactivate/merge companies first, then downgrade.
- Every account-type change writes an `AccountTypeChanged` admin audit row (`PlatformController.cs:966-968`).

### Commercial limits
- `TenantSubscription.MaxCompanies` (plus `MaxEmployees` / `MaxUsers` / `MaxAdminUsers`) is the paid ceiling. Company creation is rejected once `companyCount >= MaxCompanies` with an upgrade message (`Controllers/CompaniesController.cs:75-89`). Independently, non-Group tenants cannot create a second active company at all (`CompaniesController.cs:92-101`).

### Company creation mode
Per-tenant governance of *who* may create companies:

| Mode | Behavior |
|---|---|
| `PlatformControlled` | Only Platform Admins create companies for the tenant. |
| `GroupSelfServiceWithinLimit` | Group Admins create active companies themselves, up to `MaxCompanies` and only while `AccountType = Group`. |
| `GroupDraftPlatformApproval` | Group Admins submit **draft** companies; a Platform Admin reviews and approves (activates) or rejects them. Drafts do not count as active companies until approved. |

Platform Admin duties here: set the mode per tenant, work the draft-approval queue, and suspend/reactivate companies when a legal entity is offboarded or a commercial dispute requires it. Suspended/inactive companies are excluded from issuance snapshots (`activeCompanyIdsAtIssuance`) and skipped by integration workers (e.g. the Qiwa sync worker only processes companies with `IsActive && !IsDeleted`, `Infrastructure/Qiwa/QiwaSyncWorker.cs:218`).

### Impersonation and break-glass
- Impersonation and structured support access (`StartSupportAccess` / `EndSupportAccess`, `PlatformController.cs:2192-2320`) mint tenant tokens through the **same** claim-v2 scope emission as normal login (`PlatformController.cs:974`), and always carry the `entity_scope_strict` marker — an impersonated session can never fail open to group-wide access by claim omission (`Application/Common/EntityScopeContext.cs:29-35`).
- Both start and end of support access are audited (`SupportAccessStarted` / `SupportAccessEnded`). Rule of engagement: break-glass requires a stated reason (`StartSupportAccessRequest.Reason`), is time-boxed by the 1-hour minted-token lifetime, and must be ended explicitly when the work is done.

## 2. Group Admin — customer governance

A Group Admin is a tenant user with group scope (`User.IsGroupScope` or an `AllCurrentAndFutureCompanies` grant). Responsibilities:

- **Companies list**: view all legal entities in the group, their status, jurisdiction and registration identity (`Models/Company.cs`).
- **Create companies / drafts** where the tenant's creation mode allows: directly under `GroupSelfServiceWithinLimit`, or as drafts pending platform approval under `GroupDraftPlatformApproval`. Under `PlatformControlled`, request via the platform.
- **Assign Company Admins via entity grants**: create `UserEntityAccess` rows (`Models/AccessControl.cs:159-178`) with `GrantMode = SelectedCompanies` binding a user to specific legal entities. Grants record `GrantedBy` / `GrantedAt` for audit. Choosing `AllCurrentCompanies` gives a snapshot of today's companies (future companies excluded until re-issuance); `AllCurrentAndFutureCompanies` is a deliberate group-wide appointment — treat it as such.
- **Group dashboards**: consolidated cross-company views (headcount, payroll cost, compliance readiness) with per-company drill-down. These aggregate live operational tables — see limitations doc.
- **Company switcher rules**: the switcher is only rendered for Group tenants. The **"All Companies" aggregate view is available only to group-scope users**; a user whose token is `m=companies` sees only their granted companies in the switcher and no aggregate option. Switching companies changes the UI lens — it never widens data access beyond the token's `entity_scope` (enforcement is in the data layer, not the switcher).

What a Group Admin **cannot** do: exceed `MaxCompanies`, activate draft companies under approval mode, change the tenant's account type or creation mode, or see any other tenant.

## 3. Company Admin — legal-entity operations

A Company Admin is a tenant user with a `SelectedCompanies` grant (plus appropriate roles/permissions) for one or more legal entities. Their world is the granted company:

- **Org structure**: branches (non-nullable children of the company) and departments.
- **Workforce**: employees, contracts, documents, visa/passport/work-permit records.
- **Operations**: attendance, leave, overtime, shifts.
- **Finance**: payroll runs, payslips, loans, advances, bonuses, WPS batches — all `ICompanyScopedOperational`, so records of sibling companies are structurally invisible.
- **Compliance readiness**: maintain the company's `CompanyComplianceProfile` and `CompanyTaxPolicy` (`Models/CompanyGovernance.cs`) — jurisdiction, compliance pack binding, required statutory fields, effective-dated tax policy. Note the built-in disclaimer: these are configurable readiness profiles, not legal certification (see `docs/GROUP_COMPANY_KNOWN_LIMITATIONS.md`).

**Sibling companies are invisible.** This is not a UI convention: the EF company filters exclude other companies' rows (and even unassigned `CompanyId == null` operational rows — poison-default prevention), the write guard blocks creating or touching rows in non-granted companies (`company_scope_denied`), and `DataScopeService` intersects employee visibility with the company boundary. A Company Admin also cannot move records between companies: `CompanyId` reassignment is blocked pending an explicit transfer workflow (`company_reassignment_blocked`, `Data/ZayraDbContext.cs:161-173`).

## 4. Quick matrix

| Action | Platform Admin | Group Admin | Company Admin |
|---|---|---|---|
| Set AccountType / creation mode / limits | Yes | No | No |
| Approve draft companies | Yes | No | No |
| Create company (mode-dependent) | Yes | Self-service or draft | No |
| Suspend/reactivate a company | Yes | No | No |
| Grant company access to users | Via impersonation only (audited) | Yes | No |
| See all companies in group | Via strict, audited access | Yes | No — granted companies only |
| Run payroll / HR ops in a company | No (not their job) | Only if also granted/group-scope | Yes |
| Cross-tenant anything | No | No | No |
