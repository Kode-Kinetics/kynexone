# Multi-Tenant Isolation Audit — Zayra (KynexOne)

_Adversarial, code-grounded audit (data-isolation + pentest + company-scope) → CTO verdict. 2026-08-02._

## CTO Verdict
I have independently verified every load-bearing claim across the three audits against the actual code. All three auditors' core factual claims check out; their disagreement is purely on severity labeling, which I resolve below by trusting what the code proves. Here is the CTO synthesis.

---

# CTO VERDICT — Multi-Tenant Isolation, Zayra (KynexOne)

## (1) VERDICT

**SOLID-WITH-GAPS. Multi-tenancy is REAL and practical, not cosmetic — no cross-tenant read or write leak is reachable by a tenant customer's own credentials; the only defects are defense-in-depth/latent gaps reachable solely by the platform superadmin, who can already cross tenants by design.**

All three audits converge on this, and my own trace confirms it. The isolation is architectural (interface-driven EF global filter + unconditional boot assertion + signed-claim tenant resolution), not per-endpoint `WHERE TenantId=` discipline that a developer can forget.

## (2) Ranked ISOLATION "DEFECTS"

Blunt truth first: **there are zero provable cross-tenant read/write leak paths from a tenant customer's credentials.** I looked for one and could not construct it; neither could any of the three auditors. The DATA auditor's "HIGH" and the two "MEDIUM"s describe *latent/structural* gaps, not live leaks. Ranking them honestly as **defense-in-depth gaps** (severity = how bad if a future change trips them), none currently exploitable:

| # | Sev (as risk *today*) | Gap | Exact path (verified) | Why it is NOT a live leak | Fix |
|---|---|---|---|---|---|
| 1 | MEDIUM (structural) | **Tenant filter fails OPEN on null `tenant_id`; no tenant StrictMode** | `ZayraDbContext.cs:3054,3060,3066,3070,3075,3080` + nullable twins `3091-3117`: predicate is `_tenantId == null \|\| TenantId == _tenantId`. JWT accepts BOTH audiences with no per-route gate (`Program.cs:223`). Platform tokens carry no `tenant_id` (`JwtTokenService.cs:33` only stamps it on tenant tokens). | Reachable **only** by a platform-audience token (no `tenant_id`). A tenant customer's token ALWAYS carries `tenant_id` (`JwtTokenService.cs:33`), so this is never tenant-vs-tenant. Every tenant endpoint today either throws on missing claim (`RequireTenant()`, `Guid.Parse(...!)`) or ANDs an explicit `TenantId ==` (null param → zero rows). No live instance. | Gate the bypass on genuine no-`HttpContext` system work, not on `_tenantId == null`. Add per-route audience segregation (reject `aud=platform` on `/api/*`). |
| 2 | MEDIUM (asymmetry) | **No write-side TENANT enforcement** (company dimension IS guarded) | `ZayraDbContext.cs:98-122` `SaveChangesAsync` calls only `EnforceCompanyScopeOnWritesAsync` (`:120`); no `ISaveChangesInterceptor` anywhere (grep clean); no `TenantId` stamp/validate; company reassignment is blocked (`:184-187`) but there is no analogous `TenantId` block. | No request DTO binds `TenantId` (grep for `TenantId = req/request/dto/...` returns nothing); all ~630 call sites stamp from the JWT tenant; a forgotten stamp yields `Guid.Empty` → invisible orphan (fail-safe), **not** a cross-tenant write. | Add a tenant write interceptor mirroring the company one: stamp `TenantId` from `_tenantId` on Added, reject any Added/Modified where `TenantId != _tenantId` in a user context. |
| 3 | LOW-MED | **Crown-jewel PII tables use nullable `TenantId`** | `Models/Employee.cs:26` (`INullableTenantOwned`), `AttendanceRecord.cs`. | Null-tenant row is invisible to every real tenant (`null != T1`) — not a read leak. Only matters combined with #2 (orphan on a forgotten stamp). No DB `NOT NULL` guarantee on the most sensitive table. | Make these `ITenantOwned` (non-nullable) — no cross-tenant-shared use exists. |
| 4 | LOW-MED | **False-assurance test hides #1** | `Zayra.Api.Tests/Security/TokenAudienceIsolationTests.cs:145-163` validates the platform token against `new[] { TenantAudience }` only (`:159`) — a single-audience restriction the real pipeline does NOT have (`Program.cs:223` uses both). The test's own comment (`:148-150`) concedes it. | Test-only; asserts a boundary the running system doesn't enforce, which is why #1 reads as "covered." | Rewrite to assert the real pipeline: platform token IS accepted by the middleware, and containment comes from `RequireTenant()`/policy — then add the real per-route gate. |
| 5 | LOW | Company boot assertion is log-only in prod by default | `Program.cs:640-643` — `CompanyScopeBootAssertion` non-strict in prod unless `ZAYRA_COMPANY_SCOPE_ASSERT=strict`. | Tenant boot assertion is unconditionally strict (`Program.cs:636`, throws), so exposure is intra-tenant cross-*company* only, and CI runs strict. | Set `ZAYRA_COMPANY_SCOPE_ASSERT=strict` in prod. |

**Resolution of the auditors' severity disagreement:** DATA rated #2 HIGH, COMPANY rated it LOW, PENTEST didn't list it. The code settles it: no DTO binds `TenantId`, forgetting to stamp fails safe (`Guid.Empty`), so it is a real *architectural asymmetry* but not a live HIGH leak. I land it at MEDIUM. On #1, PENTEST's "LOW (platform-superadmin only)" is factually right about reachability; DATA/COMPANY's MEDIUM is right about structural fragility. I keep it MEDIUM because the codebase actively advertises the filter-only pattern (`ZayraDbContext.cs:3011-3012`) that this gap silently voids for tenantless principals — a trap for the next endpoint.

## (3) What is genuinely SOLID (credited, with proof I verified)

- **Universal, convention-driven read filter — the anti-cosmetic proof.** `ApplyTenantQueryFilters` (`ZayraDbContext.cs:3022-3034`) reflects over `ITenantOwned`/`INullableTenantOwned` and filters every such entity. No per-entity `HasQueryFilter` to forget; a renamed column can't lose its filter. This is the difference between real isolation and a bypassable `TenantId` column.
- **Boot guard makes the "missing filter" class impossible.** `TenantOwnershipBootAssertion.Assert` (`TenantOwnershipBootAssertion.cs:66-70`) hard-throws at startup if any mapped entity has a `TenantId`-shaped property but doesn't implement the interface, or has a type mismatch. Wired unconditionally at `Program.cs:636`. A mis-declared entity is a failed boot, not a live leak.
- **Tenant is resolved only from the signed JWT.** `tenant_id` stamped server-side from `tenant.Id`, HMAC-SHA256 (`JwtTokenService.cs:33,53`); full validation (issuer/audience/lifetime/signature) at `Program.cs:216-226`. No header/query/body override exists (grep clean).
- **Company (sub-tenant) write side IS fail-closed.** `EnforceCompanyScopeOnWritesAsync` (`ZayraDbContext.cs:157-271`) validates `CanAccessCompany` on add/modify, blocks company reassignment (`:184-187`), and fail-closed-resolves a null CompanyId. This is exactly the interceptor pattern the tenant dimension should copy (gap #2).
- **Company scope is fail-CLOSED in production.** `EntityScopeOptions.ResolveStrictMode = isProduction || configured` (`EntityScopeOptions.cs:20-21`), wired via `PostConfigure` (`Program.cs:150-154`) → claimless token = default-deny.
- **`X-Company-Id` can only NARROW**, never widen (`EntityScopeContext.NarrowTo`, fails closed to Empty).
- **PlatformAdmin is dual-gated** (`is_platform_admin=true` AND `aud=platform`, `Program.cs:233-235`); **default-deny fallback** on every unattributed endpoint (`Program.cs:242-244`).
- **`IgnoreQueryFilters()` bypasses are disciplined** — every runtime site re-applies an explicit `TenantId == <server value>` (verified across Finance GL, Employees, Establishment, Qiwa worker, attendance device path); the tenant-agnostic ones are genuine pre-HTTP system contexts.
- **Raw SQL is not a bypass** — the only sites are a parameterized advisory lock, DDL/seeders, and a health check. No user-facing `FromSqlRaw` reading tenant rows.

## (4) Must-fix-before-pilot

**None of these blocks the pilot as a cross-tenant leak.** For a defensible enterprise posture, do these two before onboarding a *second real paying tenant alongside the first* (they are hardening, not emergency):

1. **Fix #1** — treat a null request-context tenant as deny, and add per-route audience segregation. This closes the one structurally fragile door and removes the "filter-only pattern is a trap" liability. (~half a day.)
2. **Fix #2** — add the tenant write interceptor. Cheap insurance that makes write isolation defense-in-depth, matching the company dimension. (~half a day.)

Nice-to-have same sprint: #3 (non-null `TenantId` on Employee), #4 (correct the misleading test), #5 (`ZAYRA_COMPANY_SCOPE_ASSERT=strict`).

## (5) Does isolation block the employee-batch ship or the payroll pilot?

**No — neither is blocked by tenant isolation.** Cross-tenant read/write leakage from a customer's own credentials is not achievable through any surface the three audits or I tested: signed-claim tenant resolution, interface-driven filters over ~275/290 tables, unconditional boot drift-assertion, DTO-only binding with JWT-stamped `TenantId`, narrow-only company switching, and company-dimension write guards. The residual gaps are (a) reachable only by the platform superadmin (who is already omnipotent across tenants by design) and (b) latent, with no live instance. Ship the employee batch and run the payroll pilot. Schedule fixes #1 and #2 into the same release as a fast-follow so the posture is defense-in-depth on both the read and write axes before the tenant count grows.

---

## Data-isolation lens
I have completed a thorough data-layer audit. I traced the global filter mechanism, the boot guards, all ~130 production `IgnoreQueryFilters()` call sites, the write path (`SaveChangesAsync`), the platform/impersonation token model, raw-SQL paths, and the full entity/interface coverage. Here are the findings.

---

# Multi-Tenancy Data-Isolation Audit — Zayra (KynexOne)

**Verdict: READ isolation is REAL and well-engineered. WRITE isolation for the *tenant* dimension is NOT enforced at the data layer (only the *company* sub-dimension is). No live cross-tenant leak was provable, but there are genuine defense-in-depth gaps a rigorous enterprise review must flag.**

The tenant filter is convention-driven, not per-entity, so the classic "forgot a filter on one table" leak is structurally impossible here and is additionally caught at boot. The weaknesses are on the write side and in fail-open defaults, not the read filter coverage.

---

## FINDINGS (most-severe first)

### 1. [HIGH — architectural gap] No write-side tenant enforcement; only the company dimension is guarded
`SaveChangesAs

## Pentest lens
I have completed a thorough adversarial review of the multi-tenancy enforcement. Here is my final assessment.

---

# Multi-Tenancy Pentest — Zayra/KynexOne

**VERDICT: Multi-tenancy is REAL and PRACTICAL, not cosmetic.** Tenant isolation is enforced by defense-in-depth (EF global query filters keyed to a signed JWT claim + boot-time assertions + write-side guards), not by scattered `WHERE TenantId=` clauses that a developer can forget. I attempted every cross-tenant vector in the brief and could not break tenant-to-tenant isolation. One LOW-severity defense-in-depth gap affects only the platform superadmin (not tenant customers).

## Controls verified (the foundation)

- **Global query filter is interface-driven and total.** Every entity implementing `ITenantOwned`/`INullableTenantOwned` gets an auto filter `(_tenantId == null || TenantId == _tenantId)` AND-ed with soft-delete and company scope — `ZayraDbContext.cs:3022-3118`. Discovery is by interface, not property name, so a renamed column can't silently lose its filter. I enumerated all 290 `DbSet`s: **only 15 have no tenant filt

## Company-scope lens
I have a complete, evidence-backed picture across all six requested areas. Here are my findings.

---

# Company-Scope + Auth Isolation Review — Zayra (KynexOne)

**Verdict: Multi-tenancy is REAL, not cosmetic.** Tenant isolation is enforced by interface-driven EF global query filters + a startup boot assertion that hard-fails if any tenant entity escapes them; the company sub-tenant layer is fail-CLOSED in production; write-side company enforcement exists; and the test suite proves isolation at the DATA level (not just token audience). Four issues found, the top one a structural fail-open reachable only by a platform-audience principal.

## Answers to the six questions
1. **Company scoping is fail-CLOSED in production.** `EntityScopeOptions.ResolveStrictMode` returns `isProduction || configured` (`Application/Common/EntityScopeOptions.cs:20-21`), so StrictMode is forced on in prod. Under strict, an absent/`none`/malformed scope claim resolves to `Empty` = default-deny (`EntityScopeCon
