# Group → Company Access Model

**Summary.** A user's effective access is the intersection of five layers: identity (User), functional authority (Role → Permission claims), record-level visibility (DataScope), and legal-entity visibility (Company access grants → the `entity_scope` claim v2). Company scope is decided **once, at token issuance**, by `EntityScopeClaims.Resolve`, carried as a single explicit JSON claim, parsed per-request by `EntityScopeContext.FromClaims`, and enforced in the data layer (EF query filters + write-side stamping). Malformed claims always fail closed; impersonation and support tokens are strict-by-marker regardless of the global cutover flag.

Load-bearing sources: `Application/Common/EntityScopeContext.cs`, `Models/AccessControl.cs`, `Infrastructure/Common/DataScopeService.cs`, `Data/ZayraDbContext.cs`, `Migrations/PHASE2_CUTOVER_RUNBOOK.md`.

---

## 1. The access chain

```
User ──▶ Roles ──▶ Permissions        "what actions can I perform"
  │                 (permission claims, HasPermission checks)
  ├────▶ DataScopeLevel               "which employee records"
  │       Own / DirectReports / Department / Team / Organization
  │       (DataScopeService — now intersected with company boundary)
  └────▶ UserEntityAccess grants      "which legal entities"
          ──▶ EntityScopeClaims.Resolve (at token issuance)
          ──▶ entity_scope claim v2 (in the JWT)
          ──▶ EntityScopeContext.FromClaims (per request)
          ──▶ EF company query filters + write-side stamping (data layer)
```

All five compose with AND semantics: having `payroll.read` permission does not show you payroll of a company you have no grant for; a group-wide grant does not give you actions your role lacks.

## 2. Grants: `UserEntityAccess` and grant modes

`Models/AccessControl.cs`. One row per grant: `(TenantId, UserId, CompanyId?, GrantMode, Role, IsActive, GrantedBy, GrantedAt)`. `GrantedBy`/`GrantedAt` provide explicit grant provenance for audit.

`EntityGrantModes` (`Models/AccessControl.cs:140-151`):

| Mode | Semantics | Dynamic? |
|---|---|---|
| `SelectedCompanies` | Access to exactly the company on this grant row (`CompanyId` required). | No |
| `AllCurrentCompanies` | Access to the companies **active at token issuance** — a snapshot frozen into the claim. A company created after issuance is NOT covered until the next token. | **No — issuance snapshot, not dynamic** |
| `AllCurrentAndFutureCompanies` | Dynamic group-wide access: all companies now and in the future. | Yes |

Legacy rows with null `CompanyId` were migrated to `AllCurrentAndFutureCompanies` by `20260705114353_Phase2GrantModesAndInsightCompany` — behavior-preserving, since a null-company grant always meant dynamic group access.

## 3. Issuance: `EntityScopeClaims.Resolve`

One resolver used by **normal login, impersonation, and break-glass support** tokens, so the three can never drift apart (`EntityScopeContext.cs:149-189`, shared minted-token path at `Controllers/PlatformController.cs:974`). Rules, least-privilege and explicit:

1. `User.IsGroupScope == true` **or** any `AllCurrentAndFutureCompanies` grant (or a legacy null-company `SelectedCompanies` row) → **group** (dynamic).
2. Otherwise, union of: companies from `SelectedCompanies` grants + (each `AllCurrentCompanies` grant expands to the **snapshot** of companies active at issuance) → **companies** mode with that id list.
3. Grants exist but resolve to nothing → **none** (deny). Misconfigured grants must never widen access.
4. **No grants at all → group.** This is the documented tenant default: tenant users are tenant-wide until per-user company scoping is assigned. Critically, it is an *explicit issuance-time decision* (auditable, revocable per user) rather than a parser fallback — which is exactly what makes the StrictMode flip safe.

## 4. Claim format v2

A single `entity_scope` claim (`EntityScopeContext.V2ClaimType`):

```json
{"v":2,"m":"group","c":[]}
{"v":2,"m":"companies","c":["<companyGuid>","<companyGuid>"]}
{"v":2,"m":"none","c":[]}
```

Parsing rules in `EntityScopeContext.FromClaims` (`EntityScopeContext.cs:54-109`):

- `m=group` → all companies in the tenant, now and future (explicit — never inferred).
- `m=companies` → exactly the listed ids (Selected grants + AllCurrent snapshots), de-duplicated.
- `m=none` → no company-scoped data (default-deny).
- Unknown version/mode → **fail closed** (`Empty`).
- **Malformed JSON → ALWAYS fail closed** (`Empty`). A malformed v2 claim never falls back to v1 parsing.

`EntityScopeClaims.Build` emits v2 plus, during the rollout window only, the legacy v1 claims (`entity_access` rows, `is_group_scope`) so pods still running the previous release enforce the same boundary.

## 5. Legacy v1 compat path and its removal

Tokens **without** an `entity_scope` claim fall back to v1 parsing (`EntityScopeContext.cs:82-108`): `entity_access` rows `{"c":companyId,"r":role}` (null company = group grant) and the `is_group_scope=true` claim. Absence of everything resolves:

- Non-strict → backward-compat **GroupLevel** (pre-migration behavior).
- Strict (global `EntityScope:StrictMode` **or** the per-token marker) → **default-deny** (`Empty`).

**Removal plan** (documented in the class header, `EntityScopeContext.cs:20-25`): once StrictMode is on and one access-token lifetime (30 min; impersonation 60 min) has elapsed, every live token carries v2 and the legacy path is dead code — remove the v1 parser and the v1 co-emission in `Build` in the next phase. Until then it is the documented compat path for previous-release pods during rolling deploys.

## 6. `entity_scope_strict` — impersonation and support tokens

`EntityScopeContext.StrictScopeClaim` (`EntityScopeContext.cs:29-35`): stamped on high-privilege minted tokens (platform-admin impersonation, break-glass support access — `PlatformController.cs:2192+`). When present, **absence of scope claims fails closed regardless of the global StrictMode flag** — an impersonated session must never inherit tenant-wide company access by claim omission. Support-access start/end are individually audited (`SupportAccessStarted` / `SupportAccessEnded` audit rows).

## 7. StrictMode semantics

`EntityScopeOptions.StrictMode` (env `EntityScope__StrictMode`) governs only one thing: what happens when a token carries **no scope claims at all**.

| Token state | StrictMode=false | StrictMode=true |
|---|---|---|
| v2 claim present, valid | Enforced as issued | Enforced as issued |
| v2 claim present, malformed | **Deny** | **Deny** |
| v1 claims only (pre-cutover token) | Enforced per v1 | Enforced per v1 |
| No scope claims, no strict marker | GroupLevel (compat) | **Deny** |
| No scope claims, `entity_scope_strict` | **Deny** | **Deny** |

After cutover there is no fail-open path. Cutover procedure: `docs/GROUP_COMPANY_STRICTMODE_CUTOVER.md`.

## 8. DataScopeService company boundary

`Infrastructure/Common/DataScopeService.cs` resolves the permission-level `DataScopeLevel` and then **intersects it with the company boundary** (`ApplyCompanyBoundaryAsync`): the allowed employee-id set is filtered to employees whose `CompanyId` is inside the caller's `EntityScopeContext.AccessibleCompanyIds` (group-level scope passes through). This closes the pre-Phase-2 gap where an Organization-level data scope crossed company boundaries at the service layer (flagged as P1 #7 in `GROUP_COMPANY_PHASE0_AUDIT.md` §8).

## 9. Enforcement summary (defence in depth)

1. **Read**: EF global query filters (two company tiers) — see `docs/GROUP_COMPANY_ARCHITECTURE.md` §3.
2. **Write**: `EnforceCompanyScopeOnWritesAsync` — stamping + access validation + reassignment block (`ZayraDbContext.cs:141-253`).
3. **Service**: `DataScopeService` company intersection.
4. **Issuance**: explicit least-privilege scope decision, single resolver for all token types.
5. **Boot**: `CompanyScopeBootAssertion` guarantees no `CompanyId` column escapes the filter.
