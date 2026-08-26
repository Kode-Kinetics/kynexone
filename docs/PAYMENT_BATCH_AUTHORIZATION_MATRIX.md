# Payment-Batch Authorization Matrix

**Scope:** every route that reads or mutates `PayrollPaymentBatch`, `PayrollPaymentRecord`,
`BankPaymentConfirmation`, `WPSFileBatch` or `SIFFileRecord`.
**Last verified:** 2026-08-26, Wave 1 Gate 0 (B2), against `wave1/security-scope-browser-gate`.

---

## 1. Why these tables need an explicit authorizer

`PayrollPaymentBatch`, `PayrollPaymentRecord`, `BankPaymentConfirmation` and `SIFFileRecord` are
`ITenantOwned` **only** — they carry no `CompanyId` and no ambient company query filter reaches them.
There is therefore **no database backstop** on this path: whatever the controller decides is what the
caller gets. That is the opposite of most of the product, where a controller mistake is still caught by
the read filter.

The legal entity is not on the batch. It is derived:

```
PaymentBatch → PayrollRun → PayrollRun.CompanyId → IRequestEntityScopeResolver
```

`PayrollRun` is `ICompanyScopedOperational` and is the only trustworthy carrier of the entity on this
path. **The company is never taken from the request.**

Two mechanisms protect routes here, and the distinction matters:

| Mechanism | What it is | Failure shape |
|---|---|---|
| **Explicit batch guard** | `PaymentBatchScopeExtensions.PaymentBatchScopeErrorAsync` — one implementation, shared by both controllers | `403 Forbid` (cross-company), `404` (cross-tenant), `404 batch_run_not_resolvable` (broken ownership) |
| **Ambient run filter** | The route is keyed on a **run**, and `PayrollRun` is company-filtered, so a cross-company run reads back `null` | Whatever the route does with a null run — usually `404` or a `400` |

The ambient filter is real protection, but it is *accidental* protection: it fails closed only because
each route happens to check the run for null. Wave 0 found the same pattern failing **open** on
`wps-status`, where a null run made `run?.Status == "Voided"` false and skipped the refusal entirely.

---

## 2. The matrix

Route prefix `api/payroll`. All rows verified by reading the action bodies, not inferred.

### Batch-keyed routes — explicit guard required

| Route | Method | Permission | Scope source | Mutation risk | Guard | Positive test | Negative test | Status |
|---|---|---|---|---|---|---|---|---|
| `payment-batches/{id}/wps-file` | POST | `payroll.export` | batch→run | **High** — generates the SIF | explicit | ✅ | ✅ ratchet | **PASS** |
| `payment-batches/{batchId}/wps-status` | POST | `payroll.export` | batch→run | **High** — drives batch to Paid | explicit | ✅ | ✅ `CrossCompanyBatch_WpsStatus_IsDenied_AndMutatesNothing` | **PASS** |
| `payment-batches/{batchId}/settle` | POST | `payroll.export` | batch→run | **High** — posts settlement GL | explicit | ✅ | ✅ ratchet | **PASS** |
| `payment-batches/{batchId}/settle/reverse` | POST | `payroll.export` | batch→run | **High** — contra GL | explicit | ✅ | ✅ `CrossCompanyBatch_ReverseSettlement_IsDenied_AndPostsNoContraGl` | **PASS** |
| `payment-batches/{batchId}/wps-file/download` | GET | `payroll.export` | batch→run | **High** — SIF contents, IBANs | explicit | ✅ | ✅ `CrossCompanyBatch_WpsFileDownload_IsDenied` | **PASS** |
| `payment-batches/{batchId}/wps-export-history` | GET | `payroll.export` | batch→run | Low | explicit | ✅ | ✅ `CrossCompanyBatch_WpsExportHistory_IsDenied` | **PASS** |
| `payment-batches/{id}/records` | GET | `payroll.export` | batch→run | **High** — per-employee amounts + unmasked IBANs | explicit | ✅ `OwnCompanyBatch_PaymentRecords_AreReturned` | ✅ `CrossCompanyBatch_PaymentRecords_AreDenied_AndNoIbanIsDisclosed` | **PASS** |
| `payment-batches/{batchId}/bank-confirmations` | POST | `payroll.export` | batch→run | **High** — flips payment statuses | explicit | ✅ | ✅ denied before payload read | **PASS** |
| `payment-batches/{batchId}/bank-confirmations` | GET | `payroll.export` | batch→run | Medium | explicit | ✅ | ✅ | **PASS** |

### Collection route

| Route | Method | Permission | Scope source | Mutation risk | Guard | Tests | Status |
|---|---|---|---|---|---|---|---|
| `payment-batches` | GET | *(role-gated only)* | token scope → visible runs | Low (batch totals) | **list scoping** — restricted to runs the caller's resolved scope can see, applied explicitly from the token rather than via the ambient filter | ✅ `ListPaymentBatches_ShowsOnlyTheCallersOwnEntities`, `..._ShowsEverythingToAGroupCaller` | **PASS** |

### Run-keyed routes that touch batch tables — protected by the ambient run filter

| Route | Method | Permission | Scope source | Mutation risk | Guard | Status |
|---|---|---|---|---|---|---|
| `runs/{id}/payment-batches` | POST | `payroll.export` | ambient `PayrollRun` filter | High — creates the batch | ambient | **PASS (indirect)** |
| `runs/{id}/wps-validation` | POST | `payroll.export` | ambient `PayrollRun` filter | None (read-only) | ambient | **PASS (indirect)** |
| `runs/{id}/reopen` | POST | `payroll.lock` | ambient `PayrollRun` filter | High | ambient | **PASS (indirect)** |
| `runs/{id}/pdf-bundle` | GET | `payroll.export` | ambient `PayrollRun` filter | Low | ambient | **PASS (indirect)** |

> **Residual risk, recorded not hidden.** These four are safe today because each reads the run and
> handles `null`. That is a property of each route's own code, not an enforced invariant, and Wave 0
> already found one route in this family (`wps-status`) failing open on exactly that pattern. They are
> keyed on a run rather than a batch, so the batch authorizer does not apply to them as written. A
> run-scope authorizer with the same shape is the obvious follow-up — recorded as **PB-1** below.

---

## 3. The rules, and where each is enforced

| # | Rule | Where |
|---|---|---|
| 1 | Never trust a request-supplied company id | The authorizer reads `PayrollRun.CompanyId`; no route accepts a company parameter on this path |
| 2 | Verify batch tenant ownership | `.Where(b => b.TenantId == tenantId && b.Id == batchId)` |
| 3 | Verify parent-run tenant ownership | `ScopedBypass.TenantWide(db.PayrollRuns, tenantId, …)` re-applies the tenant inside the helper |
| 4 | Missing parent run fails closed | `404 batch_run_not_resolvable` |
| 5 | Tenant mismatch fails closed | The run query is tenant-pinned, so a foreign run is simply not found |
| 6 | Selected-company callers reach only their companies | `scope.CanAccessCompany(run.CompanyId)` |
| 7 | Null-company legacy runs are group-only | `run.CompanyId is null → scope.IsGroupLevel ? allow : Forbid` |
| 8 | Cross-tenant probes disclose nothing | Bare `404`, no body |
| 9 | Authorization precedes body read / parse / load / history / download / mutation / audit | Guard is the first statement after the permission check and tenant resolution — see the honest limit below |
| 10 | Permission checks remain independent and mandatory | `HasPermission("payroll.export")` still runs first and is unchanged |
| 11 | A denied request mutates nothing | Asserted for reverse-settlement (no contra GL) and wps-status (status untouched) |
| 12 | Company-switcher narrowing is respected | **New in Wave 1 B1** — the guard reads `GetEntityScope()`, which now routes through `IRequestEntityScopeResolver` and therefore honours `X-Company-Id`. Before B1 the switcher narrowed the *data* but not this *authorization check*. |
| 13 | One authorizer, not copied controller logic | `PaymentBatchScopeExtensions`; `BankConfirmationsController`'s private helper delegates to it |

**Honest limit on rule 9.** For actions with a `[FromBody]` parameter, ASP.NET Core model-binds and
deserializes the body before the action runs, and `[ApiController]` may already have returned an
automatic 400. No action can change that. What holds everywhere is that **no batch data is read or
written first**, and that body *validation* now runs after the entity check. The stronger "the payload is
never consumed" claim holds only where the action reads the stream itself — `BankConfirmationsController.Import`.

---

## 4. Enforcement that survives the next feature

`PaymentBatchScopeTests.EveryBatchSpecificRoute_CallsTheSharedCompanyGuard` scans every controller in
the tree, brace-matches each action body, and fails if a route whose template carries a batch id ships
without the guard. It also asserts a **minimum discovery count**, so a scan that silently stops matching
cannot read as success — the failure mode that made its own first version useless.

---

## 5. Open items

| # | Severity | Item |
|---|---|---|
| **PB-1** | Medium | The four run-keyed routes depend on each route's own null-handling rather than a shared authorizer. Introduce a run-scope authorizer of the same shape and extend the ratchet to `runs/{id}/…` routes that touch batch tables. |
| **PB-2** | Medium | `already_imported_to_batch` returns `priorBatchIds` probed tenant-wide, so a 409 can name a sibling entity's batch id. Filter to ids the caller's scope resolves, or return a count. |
| **PB-3** | Low | Cross-**company** existence is distinguishable (403 vs 404 vs typed 404). Cross-**tenant** is correctly opaque. Acceptable, recorded. |
