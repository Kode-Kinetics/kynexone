# Query-Filter Bypass Register

**Control owner:** Lead Orchestrator · **Enforced by:** `QueryFilterBypassRatchetTests`, `BypassLintTests`
**Approved abstraction:** `Zayra.Api/Infrastructure/Data/ScopedBypass.cs`
**Status at Wave 0 close:** 209 raw call sites across 55 files (ratcheted down from 210)

---

## 1. Why this register exists

A bare `.IgnoreQueryFilters()` drops **both** global filters at once — tenant *and* company. Nothing in
the type system records which one the author meant to drop, or what replaces it. Reviewing such a call
means reconstructing intent from a comment.

Wave 0 found **31 such calls in new POD-D code with no justification at all**. All 31 were reviewed
individually. **30 bypass only the company filter and re-apply `TenantId` explicitly in the predicate.
One is a genuinely cross-tenant system sweep.** None leaked across tenants — but "reviewed once" is not
a control, so this register plus two enforcing tests replaces the review.

---

## 2. The approved abstraction

New code must not call `.IgnoreQueryFilters()` directly. Use `ScopedBypass`, which forces the author to
name the actor and re-applies whatever restriction must survive:

| Helper | Actor | Tenant restriction | Company restriction | Use when |
|---|---|---|---|---|
| `ScopedBypass.TenantWide<T>(set, tenantId, why)` | User or service inside one tenant | **Applied by the helper** | Deliberately dropped | Group-level finance, reconciliation, integrity reads spanning entities |
| `ScopedBypass.ForCompanies<T>(set, tenantId, accessibleCompanyIds, why)` | **User-triggered request** | Applied by the helper | **Applied — caller's authorised entities**; empty set ⇒ empty result (fails closed) | Any request path spanning entities where the caller is not group-level |
| `ScopedBypass.SystemWide<T>(set, batchSize, why)` | Background worker, no principal | None (by design) | None | Cross-tenant sweeps only; **bounded batch enforced** (1..1000) |

All three reject a justification shorter than 20 characters.

**Rule for user-triggered paths:** `TenantId` alone is *never* sufficient. A request path that crosses
entities must apply the caller's authorised company IDs **and** an explicit permission check.

---

## 3. Enforcement

| Guard | What it prevents |
|---|---|
| `QueryFilterBypassRatchetTests.NoNewRawQueryFilterBypassMayBeIntroduced` | Any file gaining a raw call site, or a new file introducing one |
| `QueryFilterBypassRatchetTests.RatchetBaselineMustNotDriftUpwardsSilently` | Total surface growing; counts may only go **down** |
| `BypassLintTests.IgnoreQueryFilters_EachCallMustHaveJustificationComment` | Any raw call without a justification comment within 10 preceding lines |

The ratchet was **negative-tested**: lowering one pinned count to 7 while the file contains 8 produced
both the per-file failure (`grew from 7 to 8`) and the total failure (`found 210`, approved 209). The
guard fires; it is not vacuously green.

---

## 4. Registered bypasses — POD-D (Wave 0 scope)

Legend — **Actor**: `SU` scoped user · `GU` group user · `SW` system worker · `RC` reconciliation.

### 4.1 User-triggered — `Controllers/Finance/GlJournalExportsController.cs` (6)

| Line | Method | Actor | Why filter insufficient | Tenant | Company | Permission | Tests |
|---|---|---|---|---|---|---|---|
| 107 | `Create` | SU/GU | Duplicate-export probe must see a live group-level export to supersede it | `TenantId == tid` | `ScopeError(companyId)` → `CanAccessCompany`; null ⇒ group-level only | `finance.gl.export` \| `finance.gl.manage` | `CrossCompanyAccessTests`, `ReportExportScopeTests` |
| 143 | `List` | SU/GU | Export list spans entities by design | `TenantId == tid` | **Explicitly re-applied**: `AccessibleCompanyIds` for non-group callers | `finance.gl.read` \| `finance.gl.manage` | as above |
| 167 | `Get` | SU/GU | Stored export lines are read frozen, as emitted | `TenantId == tid` | `ScopeError(export.CompanyId)` before read | `finance.gl.read` | as above |
| 275 | `Confirm` | SU/GU | Confirmation must cover every entry the file carried | `TenantId == tid` | `ScopeError` after `LoadAsync` | `finance.erp.confirm` \| `finance.gl.manage` | as above |
| 354 | `Reject` | SU/GU | As above | `TenantId == tid` | `ScopeError` after `LoadAsync` | `finance.erp.confirm` \| `finance.gl.manage` | as above |
| 391 | `LoadAsync` | SU/GU | Single-export lookup by id; caller scope-checks the result | `TenantId == tid` | Caller applies `ScopeError` | inherited from caller | as above |

> **Verified:** every one of these six is preceded by an explicit permission check (`CanRead` /
> `CanExport` / `CanConfirm`) **and** `ScopeError`, which requires group level for a tenant-wide
> request and `CanAccessCompany` otherwise. These satisfy the "not TenantId alone" rule.

### 4.2 Tenant-wide service reads (company filter dropped, tenant re-applied)

| File | Sites | Actor | Why | Company restriction |
|---|---|---|---|---|
| `Infrastructure/Finance/PeriodHandoffReconciler.cs` | 6 | RC | A period hand-off is a **group-level statement** and must span every legal entity or it reports a reconciliation it did not perform | Dropped deliberately; caller scope-checked at controller |
| `Infrastructure/Finance/JournalExportService.cs` | 2 | RC | Entity code is stamped into the file; download **regenerates from the frozen line set** — re-filtering by present-day scope would change bytes already handed to the ERP | Dropped deliberately |
| `Infrastructure/Finance/ErpPostingEvidence.cs` | 3 | RC | "Is this run fully confirmed?" is a statement about the whole run | Dropped deliberately |
| `Infrastructure/Payroll/FinalSettlementGlLedger.cs` | 3 | RC | Settlement ledger integrity spans entities | Dropped deliberately |
| `Infrastructure/Payroll/EosbProvisionLedger.cs` | 2 | RC | Provision balance/consumption must net across entities | Dropped deliberately |
| `Infrastructure/Payroll/PayrollVoidService.cs` | 1 | RC | A contra must reverse the **original** entries exactly, including pre-company rows | Dropped deliberately |

### 4.3 Worker-context reads (no ambient tenant; tenant pinned in predicate)

| File | Sites | Actor | Notes |
|---|---|---|---|
| `Infrastructure/Notifications/NotificationRecipientResolver.cs` | 4 | SW | Runs in a child scope; each `WHERE` pins the tenant |
| `Infrastructure/Notifications/NotificationService.cs` | 3 | SW | Enqueue on child scope; tenant pinned |
| `Infrastructure/Notifications/NotificationDeliveryWorker.cs` | 1 | SW | Device prune; tenant pinned |
| `Infrastructure/Email/SmtpEmailService.cs` | 1 | SW | Template lookup from worker scope; tenant pinned |

### 4.4 The single cross-tenant bypass — lease reclaimer

`NotificationDeliveryWorker.ReclaimExpiredLeasesAsync`

| Requirement | How it is met |
|---|---|
| System-only worker boundary | `private` method on an `IHostedService`; **no controller, route, or public API reaches it** |
| Bounded batches | `ScopedBypass.SystemWide(..., BatchSize = 50, ...)` — the helper **throws** outside 1..1000 |
| Leases | `LeaseOwner`, `LeaseExpiresAtUtc`, `LeaseVersion`; only rows whose lease has expired are reclaimed; `DbUpdateConcurrencyException` is swallowed so a competing instance wins safely |
| Traceable tenant context | Logs swept count, distinct tenant count and tenant IDs. **IDs and counts only** — never recipient, destination, or body |
| Retry safety | `AttemptCount` is **not** reset, so a repeatedly-crashing send still terminates at `MaxAttempts` |
| No user request path | Confirmed by inspection; enforced by the method's visibility |

Migrated from a raw bypass to `ScopedBypass.SystemWide` in Wave 0; the file's pinned count was ratcheted
`3 → 2`.

---

## 5. Legacy surface — tracked debt

The remaining **179** raw call sites predate this control. They are frozen by the ratchet and carry
justification comments enforced by `BypassLintTests`, but they have **not** each been individually
re-verified against the metadata schema above.

**This is stated as debt, not as assurance.** Concentrations:

| File | Sites |
|---|---|
| `Controllers/FinanceGlController.cs` | 20 |
| `Controllers/EstablishmentController.cs` | 12 |
| `Infrastructure/Seed/TenantProvisioningBundle.cs` | 11 |
| `Controllers/RatesController.cs` | 11 |
| `Infrastructure/Payroll/PayrollVoidService.cs` | 10 |
| `Infrastructure/Organization/EstablishmentGuardService.cs` | 8 |
| `Controllers/EmployeesController.cs` | 8 |
| `Controllers/PayrollController.cs` | 8 |
| `Infrastructure/Qiwa/QiwaSyncWorker.cs` | 7 |

**Planned retirement:** migrate to `ScopedBypass` highest-count-first, lowering the pinned count with
each change. Priority order is by *actor risk*, not count — controller sites (user-reachable, where
`ForCompanies` is mandatory) before service/worker sites.

> **Open risk (recorded, not closed):** `FinanceGlController` (20) and `EmployeesController` (8) are
> user-reachable. Until migrated to `ForCompanies`, their company restriction rests on hand-written
> predicates rather than an enforced abstraction. Scheduled as the first Wave 1 security task.
