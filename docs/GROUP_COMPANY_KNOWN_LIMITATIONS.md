# Group → Company Known Limitations

**Summary.** This is the honest list of what the Group→Company product does **not** do yet, with the engineering reason and the planned direction for each. Nothing here is a security gap — isolation is complete and fail-closed — but several conveniences and depth features are deliberately deferred. Read the compliance disclaimer at the end before selling or configuring compliance features.

---

## 1. `CompanyId` NOT NULL promotion is pending

Operational `company_id` columns remain nullable until production null-count validation passes (all-zero counts for N consecutive days across a full payroll cycle — SQL and decision rules in `docs/GROUP_COMPANY_MIGRATION_RUNBOOK.md` §6). **Isolation does not depend on this**: the `ICompanyScopedOperational` filter treats null rows as invisible to scoped users (fail closed, `Domain/Entities/ICompanyScoped.cs`), and write-side stamping prevents new nulls. The residual cost is schema-level self-documentation, not safety.

## 2. Feature flags are tenant-level

`TenantFeatureFlag` is keyed by TenantId only (`Models/SaasPlatform.cs`), the guard filter and its 2-minute cache (`ff:{tenantId}:{featureKey}`) have no company segment. **Per-company feature overrides are not supported** — every company in a group gets the same module set. Company-level module variation (e.g. payroll enabled for 3 of 5 entities) is a Phase-next item: nullable `CompanyId` on the flag, company-segmented cache key, guard + background jobs honoring it.

## 3. Qiwa connection is per-tenant

Qiwa integration is configured once per tenant; **per-establishment/per-company connections are not yet modeled**, even though `Company.QiwaEstablishmentId` exists (`Models/Company.cs`). The sync worker does skip employees whose company is inactive/deleted (`Infrastructure/Qiwa/QiwaSyncWorker.cs:218`), so suspended entities are not synced — but distinct Qiwa credentials per establishment within one group are a future item.

## 4. No inter-company employee transfer workflow

Moving an employee between legal entities (new CR/GOSI/Qiwa establishment, sponsorship transfer, EOSB/leave-balance continuity) does not exist yet. Correspondingly, **`CompanyId` reassignment on operational rows is deliberately blocked** at the data layer (`company_reassignment_blocked`, `Data/ZayraDbContext.cs:161-173`) so ad-hoc "transfers" cannot corrupt history. The future transfer workflow must opt in explicitly. Until then: terminate-and-rehire is the (lossy) workaround, with its known EOSB implications.

## 5. Group billing is quote-level only

`PricingQuote` understands `OrgType = single | group | enterprise_holding` and `NumCompanies` (`Models/PricingModels.cs`), but **live invoices, payments, and subscriptions remain tenant-level** (`TenantInvoice`/`TenantPayment`/`TenantSubscription`). No per-company seat counting or invoice rollup by legal entity yet.

## 6. Legacy v1 claim parser still present

The `entity_access`/`is_group_scope` v1 parsing path and the v1 co-emission in `EntityScopeClaims.Build` remain in `Application/Common/EntityScopeContext.cs` as the documented rolling-deploy compat path. They are dead code once StrictMode has been live for one token lifetime and are scheduled for removal in post-StrictMode cleanup. Note the safety property holds meanwhile: a malformed v2 claim never falls back to v1.

## 7. Consolidated dashboards read operational tables

Group-level rollups (headcount, payroll cost, compliance readiness) aggregate directly from live operational tables — there is **no warehouse, no materialized marts, no snapshotting**. Consequences: heavy group dashboards add read load to the OLTP database, and historical "as-of" group reporting is limited to what the operational schema retains.

## 8. Compliance profiles are readiness tooling — not certification

`CompanyComplianceProfile` and `CompanyTaxPolicy` (`Models/CompanyGovernance.cs`) are configurable, effective-dated policy records with fail-closed enforcement hooks. Their own doc comments state the boundary: *"CONFIGURABLE READINESS PROFILE — country rules are configuration requiring legal validation; nothing here asserts statutory completeness or legal certification"* and *"values are customer-entered configuration and carry no claim of statutory/legal correctness."*

---

## ⚠️ COMPLIANCE DISCLAIMER

**Zayra's compliance features are configurable readiness tooling, NOT legal certification and NOT legal or tax advice.**

- Country rule packs (KSA / UAE / Qatar CountryPack strategies, statutory rules, GOSI/WPS validators) and the values in `CompanyTaxPolicy` / `CompanyComplianceProfile` are **configuration that requires validation by qualified legal and tax professionals** in each jurisdiction before being treated as authoritative.
- A green compliance-readiness dashboard means the *configured* checks pass — it does not assert that the configuration itself is statutorily complete or current with law changes.
- Nothing in the product, its defaults, its seeded rule packs, or this documentation constitutes legal, tax, or regulatory advice. Customers remain solely responsible for their statutory obligations (GOSI, WPS, Qiwa, income tax, labor law, and all others).
- When onboarding a new jurisdiction or legal entity, engage local counsel to validate the compliance profile and tax policy before the first payroll run.
