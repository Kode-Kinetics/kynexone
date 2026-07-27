# GL Mapping — Per-Company + Extensible Driver Store (Phase 2 Design)

> **Status: DESIGN ONLY.** Nothing in this document is implemented. The migrations, resolver
> changes, API changes and UI changes below are built and reviewed **separately**. Phase 1
> (account CRUD wiring + GL Journal preview correctness) shipped without any schema change; this
> document is the follow-on that moves GL mapping from tenant-wide to per-company and replaces the
> compile-time driver catalog with a persisted, editable store.
>
> **Migration discipline reminder (project rule):** every entity property added here **must** ship
> with its own `dotnet ef migrations add …`. A prop added to an entity without a matching migration
> produces `42703 column does not exist` 500s across every tenant. Do **not** batch unrelated column
> adds into one migration; each numbered step below is one migration.

---

## 1. Objective

1. **Per-company override with tenant-default fallback.** A GL account and a driver→account mapping
   may be defined once at the group (tenant) level and optionally overridden per company. Existing
   rows become tenant defaults with **zero data movement** — `CompanyId = NULL` *is* the backfill.
2. **Extensible drivers/segments.** Replace the static `PayrollGlCatalog.Drivers` (17 hard-coded
   drivers, `Models/FinanceGl.cs` L43–65) with a persisted, tenant/company-editable `GlDriver` table
   so a company can add its own posting variables, and add an optional cost-center/segment overlay so
   a driver can post to `account × segment`.
3. **Backward compatible.** No behavior change for a tenant that never touches company scope: a run
   with `CompanyId` resolves company overrides first, then falls back to today's tenant-level mapping,
   then to the seeded system-driver default — identical output to Phase 1 for un-overridden tenants.

### Grounded current state (verified against the repo)

| Fact | Location |
|---|---|
| `GlAccount` is `ITenantOwned`, no `CompanyId`; unique `(TenantId, Code)` | `Models/FinanceGl.cs` L6–16; `Data/ZayraDbContext.cs` L1014–1019 |
| `GlAccountMapping` is `ITenantOwned`, no `CompanyId`; unique `(TenantId, DriverKey)`; **no FK on `AccountId`** | `Models/FinanceGl.cs` L22–31; `ZayraDbContext.cs` L1021–1026 |
| `PayrollGlCatalog.Drivers` is a static compile-time list of 17 drivers | `Models/FinanceGl.cs` L39–69 |
| `LoadGlOverridesAsync(tenantId, ct)` is tenant-wide only | `Controllers/PayrollController.cs` ~L2330 |
| Shared routing helpers `EarningDriverKey` / `DeductionDriverKey` / `ResolveGlAccount` (added in Phase 1) encode Bonus-source precedence and the `EndsWith("-ER")` employer split | `Controllers/PayrollController.cs` (near `BuildPayrollGlEntries`) |
| `PayrollRun.CompanyId` exists and is already used at lock | `Controllers/PayrollController.cs` L1027–1028 |
| `FinanceGlEntry.DebitAccount/CreditAccount` store `"<code> - <name>"` | `Models/LoansAdvancesBonuses.cs` L356–357 |
| `CostCenter` is `ICompanyScoped` (nullable `CompanyId`) but its **unique index is `(TenantId, Code)` — tenant-global**, with only a *non-unique* `(TenantId, CompanyId)` index | `Models/CostCenter.cs`; `ZayraDbContext.cs` L1028–1035 |
| `FinanceGlController` `SetMappings` deletes-all-then-reinserts scoped by `TenantId` only | `Controllers/FinanceGlController.cs` |
| Stack: `net8.0`, `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10`, `EFCore.Design 8.0.11` | `Zayra.Api/Zayra.Api.csproj` |

---

## 2. Data model changes

### 2.1 `GlAccount` — add nullable `CompanyId`

```csharp
public Guid? CompanyId { get; set; }   // NULL = group/tenant default; non-null = company-specific account
```

**Visibility semantics:** a company sees accounts where `CompanyId == <its id> OR CompanyId IS NULL`
(company-specific accounts *plus* inherited group defaults).

### 2.2 `GlAccountMapping` — add nullable `CompanyId` (+ optional segment)

```csharp
public Guid? CompanyId { get; set; }             // NULL = tenant-default mapping; non-null = company override
public Guid? SegmentCostCenterId { get; set; }   // optional cost-center/segment overlay for this driver line
```

`SegmentCostCenterId` keeps chart-of-accounts (driver/account, the *natural* account axis) orthogonal
to the *dimension* (cost center / segment) — the correct GL coding-block model. See §2.4 for the
cardinality decision that governs the unique index.

### 2.3 New `GlDriver` — persisted, editable driver + routing store

Replaces the static `PayrollGlCatalog`. The seeded system rows reproduce the current 17 drivers
exactly (same keys, so live `DriverKey` values continue to resolve unchanged).

```csharp
public class GlDriver : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }        // NULL = tenant-wide driver; non-null = company-specific
    public string Key { get; set; } = string.Empty;   // "EARN:BASIC" or a custom "EARN:WELLNESS"
    public string Label { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;   // "Earning" | "Deduction" | "Balancing"
    public string PostingSide { get; set; } = string.Empty; // "DR" | "CR" — custom drivers MUST declare a side
    public string AccountType { get; set; } = "Expense";    // natural-side hint for the account picker
    public string DefaultCode { get; set; } = string.Empty; // fallback account code when unmapped
    public string DefaultName { get; set; } = string.Empty;

    // ── Data-driven routing (see §3 — this is the hard part; equality alone is insufficient) ──
    public string? MatchSource { get; set; }         // e.g. "Bonus", "Statutory"; NULL = any source
    public string MatchMode { get; set; } = "Exact"; // "Exact" | "Suffix" | "Prefix" | "Any"
    public string? MatchComponentCode { get; set; }  // pattern interpreted per MatchMode; NULL = category catch-all
    public bool EmitsEmployerExpensePair { get; set; } // true = this CR deduction also emits a paired balancing DR
    public string? PairedExpenseDriverKey { get; set; } // the DR driver used for that paired expense (e.g. EMPLOYER_STATUTORY_EXPENSE)

    public bool IsSystem { get; set; }          // true for the seeded 17 — routing predicates & Key are read-only
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
```

### 2.4 Segment cardinality — **DECISION REQUIRED before build**

Adding `SegmentCostCenterId` to the mapping while keeping the unique index at
`(TenantId, CompanyId, DriverKey)` means **one driver resolves to exactly one `account × segment` per
scope** — you cannot split a driver (e.g. Basic Salary) across multiple cost centers. That is usually
the *whole reason* to introduce a segment dimension, so the two designs conflict. Pick one, explicitly,
with the product owner:

- **Model A — single segment per driver (overlay).** Unique stays `(TenantId, CompanyId, DriverKey)`.
  `SegmentCostCenterId` is a decorative dimension stamped on that driver's single line. Simple; no
  splitting. Adequate if "segments" just means "tag each posting line with a cost center."
- **Model B — split a driver across segments.** Unique becomes
  `(TenantId, CompanyId, DriverKey, SegmentCostCenterId)` and the resolver + `SetMappings` must handle
  **multiple rows per driver** (each emitting its own line, amounts apportioned per segment — the
  apportionment rule itself is a further open question: headcount, fixed %, cost-center of the
  employee, etc.). More powerful, materially more complex, and changes the resolver return type from
  `DriverKey → (Code,Name)` to `DriverKey → list of (Code, Name, Segment, Weight)`.

**Recommendation:** ship **Model A** first (overlay only, unique unchanged at
`(TenantId, CompanyId, DriverKey)`), and treat Model B (true split with apportionment) as a
requirements-gated follow-on — it needs a concrete client apportionment requirement to design
correctly. The rest of this document assumes Model A unless a section is marked *(Model B)*.

> **CostCenter reality check.** Cost-center *codes are tenant-global today* (unique `(TenantId, Code)`,
> `ZayraDbContext.cs` L1032), **not** per-company-unique — the `(TenantId, CompanyId)` index is
> non-unique. So the segment picker for a company must filter cost centers by
> `CompanyId == <company> OR CompanyId IS NULL`, but must **not** assume a company can mint its own
> code that collides with another company's. If per-company segment codes are ever required, that is a
> separate change to `CostCenter`'s unique index and is **out of scope** for this design.

---

## 3. Data-driven routing — the hard part (must be specified before build)

The live routing is **not** a flat key lookup. `EarningDriverKey` / `DeductionDriverKey`
(`PayrollController.cs`, extracted in Phase 1) and their pre-Phase-1 switch encode three behaviors a
naïve `MatchComponentCode == "X"` equality table **cannot** reproduce:

1. **Source precedence over code.** `Source == "Bonus"` routes to `EARN:BONUS` *regardless* of the
   component code. A source predicate must win over a code predicate.
2. **Suffix predicate.** `Source == "Statutory" && ComponentCode.EndsWith("-ER")` splits employer (ER)
   from employee (EE) statutory. This is a **suffix** test, not equality — hence `MatchMode` in §2.3.
3. **A CR deduction that also emits a paired DR expense.** The employer-side statutory deduction
   accumulates `employerStatutoryTotal` and generates a *separate* balancing DR to
   `EMPLOYER_STATUTORY_EXPENSE` (`BuildPayrollGlEntries` ~L2300–2310 / preview equivalent). A generic
   `PostingSide = "DR"|"CR"` cannot express "this CR line *also* produces a DR line." Hence
   `EmitsEmployerExpensePair` + `PairedExpenseDriverKey` in §2.3.

### 3.1 Required routing semantics (spec)

**Match evaluation.** For each payroll component group `{ ComponentCode, Source }`, evaluate candidate
drivers in the group's `Category` and pick the **most specific** match:

- A candidate matches when: (`MatchSource IS NULL` **or** `MatchSource == group.Source`) **and** the
  code test for `MatchMode` passes:
  - `Exact`  → `group.ComponentCode == MatchComponentCode`
  - `Suffix` → `group.ComponentCode.EndsWith(MatchComponentCode)`
  - `Prefix` → `group.ComponentCode.StartsWith(MatchComponentCode)`
  - `Any`    → `MatchComponentCode IS NULL` (category catch-all)
- **Specificity (most-specific-first) ordering:** `Exact` > `Suffix`/`Prefix` > `Any`; within the same
  mode, a driver *with* a `MatchSource` beats one without. Ties broken by `SortOrder`, then
  `IsSystem == true` wins over a custom driver (system routing is the safe default), then by `Key`
  ordinal for full determinism.
- **Catch-all guarantee.** Each category **must** retain a seeded `Any` catch-all driver
  (`EARN:OTHER`, `DED:OTHER`) so every component always resolves — never leave a component unroutable.

**Employer-expense pairing.** When the winning deduction driver has `EmitsEmployerExpensePair == true`,
accumulate that group's amount and, after the deduction loop, emit one DR line to the account resolved
for `PairedExpenseDriverKey`. This reproduces today's employer-statutory DR/CR pair as **data**:
the seeded `DED:STATUTORY_ER` row carries `MatchSource="Statutory"`, `MatchMode="Suffix"`,
`MatchComponentCode="-ER"`, `EmitsEmployerExpensePair=true`,
`PairedExpenseDriverKey="EMPLOYER_STATUTORY_EXPENSE"`.

**Compose static + custom routing.** System drivers keep their seeded predicates (which reproduce the
current switch byte-for-byte). Custom drivers are evaluated in the same specificity pass. A custom
driver may only *win* over a system driver by being **strictly more specific** (e.g. an `Exact` custom
code beats a system `Suffix`/`Any`); on a specificity tie the system driver wins. This guarantees a
custom driver can add a new component route but cannot silently hijack an existing system route.

### 3.2 Validation on driver save (`POST/PUT gl_drivers`)

- `IsSystem` drivers: `Key`, `Category`, all `Match*` predicates, `EmitsEmployerExpensePair`, and
  `PairedExpenseDriverKey` are **read-only** (only `Label`, `DefaultCode/Name`, `SortOrder`, `IsActive`
  may change). Reject attempts to edit the locked fields.
- Custom driver: `PostingSide ∈ {DR,CR}` required; `Category` required; if `EmitsEmployerExpensePair`
  then `PairedExpenseDriverKey` must reference an existing DR driver in scope. Reject a custom driver
  whose `(MatchSource, MatchMode, MatchComponentCode)` triple **exactly duplicates** another active
  driver in the same category+scope (ambiguous route). Reject `MatchMode != "Any"` with a null
  `MatchComponentCode`, and `MatchMode == "Any"` with a non-null one.
- **Reachability warning (non-blocking):** if a new custom driver can never be the most-specific match
  for any known component (fully shadowed by a system `Exact`), surface a warning on save so the author
  knows it will never post.

---

## 4. Resolution logic

`LoadGlOverridesAsync` gains a company parameter and layered precedence:

```csharp
private async Task<IReadOnlyDictionary<string,(string Code,string Name)>> LoadGlOverridesAsync(
    Guid tenantId, Guid? companyId, CancellationToken ct)
```

- Load mappings where `TenantId == tid AND (CompanyId == companyId OR CompanyId IS NULL)`, joined to
  accounts scoped `(CompanyId == companyId OR CompanyId IS NULL) AND IsActive`.
- Reduce to `DriverKey → (Code,Name)` with the **company-scoped row winning** over the tenant-default
  row per key (Model A). *(Model B: reduce to `DriverKey → list`, company rows replacing the default
  set for that key.)*
- **Account precedence for a driver:** company mapping → tenant-default mapping → `GlDriver` default
  (company driver row → tenant driver row) → `("9999","Unmapped")`. Identical fallthrough shape to the
  Phase-1 `ResolveGlAccount`, just scope-layered.
- **Both call sites pass `run.CompanyId`:** `Lock` (posting) and `GlJournal` (live preview). Keep a
  `companyId: null` overload for non-payroll callers so nothing else breaks during rollout.
- **Locked runs unchanged:** the Phase-1 `GlJournal` already serves the immutable posted
  `FinanceGlEntries` for a locked run and never re-resolves — that behavior stays. Per-company
  resolution only affects the *draft preview* and the *next* lock.

---

## 5. API changes (`FinanceGlController`)

> `FinanceGlController` was intentionally **out of scope for Phase 1**; all of the following are Phase-2.

### 5.1 Scope every query by company

- Accounts + mappings + drivers `GET` accept `?companyId=<guid>` (absent = group/defaults view).
- A **company view** returns that company's own rows **plus** inherited tenant-default rows, each
  flagged `inherited: true` so the UI can ghost them. The group view returns only `CompanyId IS NULL`
  rows.

### 5.2 `SetMappings` — highest-severity correctness change

`SetMappings` currently does `RemoveRange(where TenantId == tid)` then reinserts. Under company scoping
the delete filter **must** become `where TenantId == tid AND CompanyId == scope` (where `scope` is the
posted `companyId`, possibly NULL). **If the `CompanyId` filter is omitted, saving one company's
overrides wipes every other company's overrides *and* the tenant defaults.** This is the single
highest-risk change in the whole feature — it must have a dedicated regression test (§8).

### 5.3 Cross-scope integrity validation (a FK cannot express these)

Enforce in `SetMappings` / account save, **not** the FK:

1. **Forward rule:** a company-override mapping (`CompanyId = X`) must reference an account visible to
   company X — i.e. account `CompanyId == X OR CompanyId IS NULL`.
2. **Reverse rule (do not omit):** a **tenant-default mapping (`CompanyId IS NULL`) must reference a
   tenant-default account (`CompanyId IS NULL`)**. Otherwise a shared group default would leak one
   company's private account to *every* company. This is the mirror of rule 1 and is easy to forget.
3. `DriverKey` is validated against the **persisted `GlDriver` set for the scope** (company drivers +
   tenant drivers), not the old static list. `SegmentCostCenterId`, if present, must reference a cost
   center visible to the scope (`CompanyId == scope OR CompanyId IS NULL`).

### 5.4 New `GlDriver` CRUD + updated seeding

- `GET/POST/PUT/DELETE api/finance/gl/drivers?companyId=` with the §3.2 validation. `IsSystem` drivers
  are read-only for the locked fields and cannot be deleted.
- `seed-defaults` seeds `gl_drivers` (the 17 system rows, `CompanyId=NULL, IsSystem=true`, keys equal to
  today's `DriverKey` values) **plus** the default accounts + tenant-default mappings. Idempotent —
  mirror the existing `SeedDefaults` up-sert pattern; safe to re-run.

### 5.5 New server guards (fold the Phase-1 residual holes in here)

- **`UpdateAccount` must guard deactivation.** Today `DeleteAccount` is guarded ("in use by a payroll
  mapping; remap it first") but `UpdateAccount` setting `IsActive=false` is **not** — the Phase-1 UI
  mitigates this only with a client-side `confirm()` that reads possibly-stale local mapping state.
  Add a server guard: reject `IsActive=false` while a mapping (in the same scope) references the
  account, or require an explicit `force=true`.
- **`UpdateAccount` null/empty-name guard.** `UpdateAccount` calls `req.Name.Trim()` with no
  null/empty check (unlike `CreateAccount`), so a blank name currently 500s. Add the same
  `string.IsNullOrWhiteSpace` guard `CreateAccount` has.

### 5.6 DTO changes

- `GlAccountRequest`, `GlMappingRequest` gain optional `CompanyId`. `GlMappingRequest` gains optional
  `SegmentCostCenterId`. New `GlDriverRequest` for driver CRUD. List DTOs gain `inherited: bool` and
  (for accounts/mappings) the resolved `companyId`.

---

## 6. UI changes (`GlMappingTab`, `financeGl.ts`)

- **Company selector** at the top: "All group (defaults)" + one entry per company. Selecting a company
  reloads accounts/mappings/drivers for that scope; inherited tenant-default rows render **ghosted**
  and badged "Inherited from group".
- **Override affordance:** an inherited mapping row shows an "Override for this company" action that
  writes a `CompanyId`-scoped mapping row without touching the group default; a company override shows
  a "Revert to group default" action that deletes the company row.
- **Driver manager** (new panel): add/edit custom drivers (Key, Label, Category, PostingSide,
  MatchSource/MatchMode/MatchComponentCode, EmitsEmployerExpensePair + paired driver, default account).
  System drivers are shown read-only/locked with a "System" badge.
- **Segment column** (Model A): optional cost-center picker per mapping row, filtered to the selected
  company's visible cost centers. *(Model B: a driver row can expand to multiple segment lines.)*
- `financeGl.ts`: thread `companyId` through
  `listAccounts/listMappings/createAccount/updateAccount/deleteAccount/setMappings`, and add
  `listDrivers/createDriver/updateDriver/deleteDriver`. Keep the existing no-arg signatures working
  (companyId optional) so the group/defaults view is the zero-arg call.

---

## 7. Migration steps (ordered — each bullet is **one** `dotnet ef migrations add`)

> **PostgreSQL NULL-distinct trap (critical).** A plain Postgres unique index treats `NULL` as
> *distinct*, so two tenant-default rows (`CompanyId IS NULL`) with the same `Code` would **not**
> collide → duplicate defaults → non-deterministic resolution. The new uniques must treat NULLs as
> equal. See the EF-version caveat immediately below.

> **EF API caveat (verified against this stack).** The repo is `net8.0` with
> `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10`. The fluent
> `entity.HasIndex(...).IsUnique().AreNullsDistinct(false)` API is **EF Core 9 / Npgsql 9 only and will
> not compile here.** Therefore:
> - **Primary (recommended) path — raw SQL in the migration** (valid on Neon PG15+):
>   ```csharp
>   migrationBuilder.Sql(
>     "CREATE UNIQUE INDEX ix_gl_accounts_scope_code ON gl_accounts (tenant_id, company_id, code) NULLS NOT DISTINCT;");
>   ```
>   Mirror for `gl_account_mappings (tenant_id, company_id, driver_key) NULLS NOT DISTINCT` and
>   `gl_drivers (tenant_id, company_id, key) NULLS NOT DISTINCT`.
> - **Alternative — upgrade to EF Core 9 / Npgsql 9 as an explicit prerequisite** if you want the
>   fluent `AreNullsDistinct(false)`. Treat this as a separate, reviewed dependency bump — do **not**
>   fold a major EF upgrade into the GL migration.
> - **Portable fallback (pre-PG15 only)** — two partial indexes per table:
>   `(tenant_id, code) WHERE company_id IS NULL` **and**
>   `(tenant_id, company_id, code) WHERE company_id IS NOT NULL`. Verify the deployed Postgres version
>   first; on Neon PG15+ prefer `NULLS NOT DISTINCT`.

1. **Add nullable `company_id`** to `gl_accounts` and `gl_account_mappings` (default NULL). Existing
   rows *become* tenant defaults — **NULL is the backfill, zero data movement.**
2. **Create `gl_drivers`** + a data-migration seed of the 17 system drivers **per existing tenant**
   (`CompanyId=NULL, IsSystem=true`, keys = live `DriverKey` values, predicates per §3 so routing is
   byte-identical to the current switch). Idempotent/resumable; **batch across tenants** (prod has
   400+ tenants — see risks).
3. **Add `segment_cost_center_id`** (nullable) to `gl_account_mappings`.
4. **Pre-flight orphan cleanup, then re-key uniques.** First delete/repair any mapping whose
   `account_id` no longer exists (rows hard-deleted before the FK existed — see step 5). Then **drop**
   the old `(tenant_id, code)` / `(tenant_id, driver_key)` uniques and **create** the new
   company-scoped `NULLS NOT DISTINCT` uniques (or partial-index pair) per the caveat above.
5. **Add FK** `gl_account_mappings.account_id → gl_accounts.id`, `OnDelete(Restrict)` (the app already
   guards delete). FK creation **will fail** if step 4's orphan cleanup did not run — the pre-flight is
   mandatory, not optional.
   ```csharp
   entity.HasOne<GlAccount>().WithMany().HasForeignKey(m => m.AccountId).OnDelete(DeleteBehavior.Restrict);
   ```
6. **Ship code** behind the new `companyId` signature: layered `LoadGlOverridesAsync`, `GlJournal`/
   `Lock` passing `run.CompanyId`, `FinanceGlController` scoping + guards, `GlDriver` CRUD, UI. Keep the
   tenant-only overload alive during rollout for non-payroll callers.

---

## 8. Risks & mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| **`SetMappings` delete filter omits `CompanyId`** → saving company A wipes every other company's overrides + the tenant defaults | **Critical** | §5.2; dedicated regression test "save company A leaves company B + defaults intact" |
| **NULL-distinct unique trap** → duplicate tenant defaults, non-deterministic posting | **Critical** | `NULLS NOT DISTINCT` (raw SQL, §7 caveat) or partial-index pair; verify PG version |
| **EF fluent `AreNullsDistinct(false)` doesn't exist on 8.x** → migration won't compile | High | Raw-SQL path is primary; EF9 upgrade only as an explicit, separate prerequisite |
| **FK creation fails on legacy orphan mappings** (account hard-deleted pre-FK) | High | Mandatory pre-flight cleanup query in step 4 before step 5 |
| **Custom-driver routing can't express suffix/`-ER` + paired-DR semantics** → employer driver produces no balancing DR → unbalanced journal (only caught by the `gl_unbalanced` backstop at `Lock` = a hard failure, not correct posting) | High | §3: `MatchMode` (Exact/Suffix/Prefix/Any), specificity precedence, `EmitsEmployerExpensePair` + `PairedExpenseDriverKey`; validate reachability on save |
| **Missing reverse cross-scope rule** → a tenant-default mapping pointing at a company account leaks that account to all companies | High | §5.3 rule 2 (default mapping must reference default account) |
| **Segment cardinality vs. unique index mismatch** → "add segments" silently can't split a driver | Medium | §2.4 decision: ship Model A (overlay), gate Model B behind a real apportionment requirement |
| **Silent deactivation fallback** (an account deactivated via `UpdateAccount` drops its override in preview + posting) | Medium | §5.5 server guard on `UpdateAccount` (parallels `DeleteAccount`); Phase-1 client `confirm()` is only advisory |
| **Seeding cost across 400+ tenants** | Medium | Batch + idempotent + resumable `gl_drivers` seed (step 2) |
| **Balance invariant for custom drivers** | Medium | Require `PostingSide`; keep the existing `gl_unbalanced` guard at `Lock` as the backstop |
| **CostCenter codes are tenant-global, not per-company** | Low | §2.4 note: segment picker filters by visibility but does not assume per-company codes; per-company codes are out of scope |

---

## 9. Test matrix (Phase 2)

- Company override wins over tenant default for the same driver.
- An unset company override **inherits** the tenant default.
- A deactivated **company** account falls back to the tenant default, then to the driver default.
- **`SetMappings` on company A leaves company B and the tenant defaults intact** (the critical wipe test).
- Forward rule: company mapping cannot reference another company's private account.
- **Reverse rule:** a tenant-default mapping cannot reference a company-scoped account.
- Custom driver with an `Exact` route posts to its account **and the journal still balances**; a custom
  employer-side driver with `EmitsEmployerExpensePair` produces the paired DR and balances.
- Specificity precedence: `Exact` custom beats system `Suffix`; system wins on a specificity tie.
- Orphan-mapping cleanup precedes FK creation (FK add succeeds only after cleanup).
- Duplicate tenant-default `Code` rejected by the new `NULLS NOT DISTINCT` unique.
- Locked-run `GL Journal` still shows the immutable as-posted entries after a post-lock mapping edit
  (Phase-1 behavior preserved under company scoping).

---

## 10. Explicit open questions for the product owner

1. **Segment model A vs. B** (§2.4) — overlay tag, or true split-across-cost-centers with apportionment?
   If B, what is the apportionment rule (headcount / fixed % / employee cost center)?
2. **Custom-driver authority** — may a company-scoped custom driver override a *system* route, or only
   add new routes? (This design says: add-only / more-specific-only; confirm.)
3. **Per-company cost-center codes** — required, or is tenant-global code space acceptable for segments?
   (Changing `CostCenter`'s unique is out of the current scope.)
4. **Rollout** — do all existing tenants get the 17 `gl_drivers` seeded eagerly in the migration, or
   lazily on first Setup visit? (Affects the 400+-tenant seeding cost.)
