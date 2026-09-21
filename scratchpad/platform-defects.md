# Platform defects — seeder escalation, dead Redis check, unapplied July migrations

Branch `fix/platform-defects`, base `develop` @ `d730313`. Not pushed, not merged.

No production writes were made. Production Postgres was read SELECT-only; the Render API was read-only.

**Headline: no production SQL is required.** The migration history row the brief anticipated is
already there. But two things the brief did not anticipate are true and worse, and both are fixed
in code — see defect 3.

---

## 1. Tenant admins silently re-promoted to full group scope on every startup

### Root cause

Two separate re-promotions, both unconditional, both on every boot.

* `backend-dotnet/Zayra.Api/Infrastructure/Seed/AuthSeeder.cs:142` (pre-fix) — a raw-SQL
  `UPDATE users SET is_group_scope = TRUE, updated_at_utc = NOW()` over **every tenant**, for every
  non-deleted user holding the Admin role with no active `user_entity_accesses` row.
* `backend-dotnet/Zayra.Api/Infrastructure/Seed/AuthSeeder.cs:95` (pre-fix) —
  `if (!admin.IsGroupScope) admin.IsGroupScope = true;`, re-promoting the bootstrap admin itself.

The stated intent was a one-time repair: admins created before the entity-scope rollout hold the
Admin role but neither group scope nor company grants, which resolves to `entity_scope=none`
(`Application/Common/EntityScopeContext.cs:183`) and blocks even creating an employee. Repairing
that once is reasonable.

The defect is that **"Admin role, no active grant" is also the exact shape of a deliberately
de-scoped administrator**, and there are two supported ways to produce it — both in
`Controllers/AccessController.cs`, both audited, both revoking the user's refresh tokens, i.e. both
unambiguously "this takes effect now":

| Path | Code | Result |
| --- | --- | --- |
| `PATCH /access/users/{id}/group-scope` with `false` | `AccessController.cs:687-698`, audited `GroupScopeRevoked` | `is_group_scope = FALSE` |
| Grant delete | `AccessController.cs:665`, `grant.IsActive = false` | revoking the **last** grant leaves zero active grants |

Either way the next restart silently widened the user back to full group scope. On Render that is
every deploy, plus three OOM restarts in four days. **Revoking an administrator's last company
grant did not narrow them to nothing — it handed them the whole group.** No audit row records the
reversal.

The raw SQL also meant `QueryFilterBypassRatchetTests` could not see it: that ratchet counts
`.IgnoreQueryFilters()` call sites, and raw SQL never had a query filter to ignore. A cross-tenant
privilege escalation was written in the one dialect the isolation lint is structurally unable to
read, and the guard reported green throughout.

### Production state (read-only, 2026-09-21)

The escalation predicate currently matches **0 rows**, so nothing is live right now. 55 of 56
Admin-role users are already group-scoped; the 1 that is not still holds an active grant, so it is
protected only by that grant continuing to exist. The one-time repair has already run everywhere it
could.

### Fix

* Deleted the tenant-wide `UPDATE` rather than narrowing it. Widening a live user's scope is an
  authorisation decision; it belongs to an authenticated administrator behind `AccessController`'s
  audit trail, never to an unattended boot path. The repair it existed for has already happened.
* Deleted the bootstrap admin's re-promotion. Group scope is now granted **on creation only** —
  `AuthSeeder.cs:62`, the `admin is null` branch. That is idempotent by construction and can never
  widen an existing user.
* Replaced the `UPDATE` with `CountStrandedAdminsAsync` (`AuthSeeder.cs:191`), a read-only
  `COUNT(*)` that logs a warning naming the remedy when any admin resolves to `entity_scope=none`.
  The operational value of the old block — noticing the broken state — is kept; the power to
  silently fix it by widening someone is not.

Everything that remains is idempotent under `NpgsqlRetryingExecutionStrategy` (the restart test
seeds twice for exactly this reason), and nothing writes `is_group_scope` for an existing user.

### Lint

Added `backend-dotnet/Zayra.Api.Tests/Security/RawSqlExecutionRatchetTests.cs` — an **additive**
ratchet pinning raw non-query SQL (`ExecuteSqlRaw*`, `ExecuteSqlInterpolated*`) per file, 13 sites
across 10 files. Read-side APIs (`SqlQueryRaw`, `FromSqlRaw`) are deliberately excluded; migrations
are excluded. It carries a second test refusing an approved count above the real count (the
pre-authorised-slot mistake `QueryFilterBypassRatchetTests` already had to correct once), and a
third asserting `AuthSeeder` never writes `is_group_scope` in raw SQL again.

**`QueryFilterBypassRatchetTests` is untouched** — not read, not relaxed, not re-pinned. Its counts
are byte-identical to `develop`. `OrphanEntityRatchetTests` is unaffected (no new entities).
`AuthSeeder.cs` drops from 2 raw-SQL sites to 1 — the ratchet moves down, never up.

### Evidence

New: `backend-dotnet/Zayra.Api.Tests/Security/AuthSeederScopeRestartTests.cs`, real Postgres,
`[Collection("Integration")]`. Asserts the observable consequence — a deliberately narrowed scope is
still narrow after a restart — by re-running `SeedAsync` through a **fresh** `DbContext` and reading
`is_group_scope` straight out of Postgres, so no stale tracked entity can satisfy it.

**Fail before** (`develop` sources, same test file):

```
Zayra.Api.Tests.Security.AuthSeederScopeRestartTests.BootstrapAdmin_DeliberatelyNarrowedScope_SurvivesRestart [FAIL]
  Expected (GroupScopeAsync(adminId)) to be False because a scope reduction an administrator made
  deliberately must survive a restart, but found True.

Zayra.Api.Tests.Security.AuthSeederScopeRestartTests.TenantAdmin_WhoseLastGrantWasRevoked_IsNotPromotedToGroupScopeByRestart [FAIL]
  Expected (GroupScopeAsync(narrowedId)) to be False because revoking an administrator's last
  company grant must not widen them to the whole group, but found True.

Zayra.Api.Tests.Security.RawSqlExecutionRatchetTests.NoNewRawSqlWriteMayBeIntroduced [FAIL]
  Infrastructure/Seed/AuthSeeder.cs: 2 raw non-query SQL call sites, approved 1

Zayra.Api.Tests.Security.RawSqlExecutionRatchetTests.AuthSeederExecutesNoRawScopeWidening [FAIL]
  Expected statements {"SET is_group_scope = TRUE,", "WHERE COALESCE(u.is_group_scope, FALSE) = FALSE"}
  to not have any items matching (...), but found {"SET is_group_scope = TRUE,"}.
```

**Pass after**: all 5 tests in the two classes green (see the combined run below).

---

## 2. The Redis health check can never report healthy

### Root cause

`backend-dotnet/Zayra.Api/Controllers/PlatformController.cs:324` (pre-fix) resolved
`IConnectionMultiplexer`. Nothing has ever registered it: `Program.cs:282`'s
`AddStackExchangeRedisCache` registers `IDistributedCache` backed by a `RedisCache` and nothing
else, and the whole repository held exactly one reference to `IConnectionMultiplexer` — that line.
`GetService` returned null unconditionally, so `/platform/health` reported `not_configured` whether
Redis was live, dead or absent. No reachable passing state and no reachable failing state.

### What the live service actually has

Read from Render (`srv-d8slkb77f7vs73d2k92g`, 2026-09-21, 32 env vars): **no `REDIS_URL`, no Redis
variable of any kind.** The service genuinely runs on `AddDistributedMemoryCache`
(`Program.cs:288`), so `/health/ready`'s `redis: fallback_memory` happens to be true — but it is
true by reading configuration, not by checking anything.

That settles the choice. Registering an `IConnectionMultiplexer` would add a dependency nothing
connects to and would still report nothing useful on this deployment. **Interrogate what is
actually registered.**

### Fix

`PlatformController.cs:327-370`. Resolve `IDistributedCache` and classify:

| Registered | Verdict |
| --- | --- |
| nothing | `not_configured` (composition-root bug; unreachable in the real app) |
| `MemoryDistributedCache` | `fallback_memory` — same word `/health/ready` uses, so the two endpoints cannot disagree |
| anything else (i.e. `RedisCache`) | real round trip `await cache.GetAsync(...)` → `ok`, exception → `error` |

The probe **reads** a single key (`RedisProbeKey`, `PlatformController.cs:51`, carrying the
`kynexone:` prefix the cache applies) and never writes — a miss is a success, because it still
required a live connection. `using StackExchange.Redis;` is gone from the file; the package stays,
`Program.cs` uses it.

### Evidence

New: `backend-dotnet/Zayra.Api.Tests/Platform/PlatformRedisHealthTests.cs`, 5 tests pinning all four
verdicts plus read-only-ness.

**Fail before** (`develop` controller, unchanged test file — one inert `const` line added so it
compiles):

```
Health_WithLiveRedisBackedCache_ReportsOk [FAIL]
  Expected status to be "ok" ... but "not_configured" ... differs near "not" (index 0).
Health_WhenRedisBackedCacheIsUnreachable_ReportsError [FAIL]
  Expected status to be "error" ... but "not_configured" ... differs near "not" (index 0).
Health_WithInMemoryFallback_ReportsFallbackMemory [FAIL]
  Expected status to be "fallback_memory" ... but "not_configured" ... differs near "not" (index 0).
```

Three different worlds, one answer — which is the defect, stated precisely.

**Pass after**: all 5 green.

---

## 3. The two unapplied July migrations — and what is actually wrong with them

### Verified production state (SELECT-only, 2026-09-21)

| Migration | In `__EFMigrationsHistory`? | Will the next `--migrate` run it? |
| --- | --- | --- |
| `20260713061000_AddSalaryStructureEligibilityAndVersioning` | **YES** | No |
| `20260713062000_BackfillEmployeeChangeApprovalRequests` | No | Yes |
| `20260713073000_AddApprovalQueueAccountability` | No | Yes |

### 061000 — no action, and no SQL to hand back

It is already recorded, so restoring its `[Migration]` attribute does not re-run it. This matters
more than the other two: it is the only one of the three that uses `AddColumn` — bare
`ALTER TABLE … ADD COLUMN` with no `IF NOT EXISTS` — and would abort with 42701 on a second run. All
eight columns confirmed present on `salary_structures`.

**The brief's premise that a history row must be inserted is out of date. No production SQL is
required for this, or for anything else in this work.** (The brief also describes all three as
"pure `migrationBuilder.Sql(...)` data backfills"; 061000 is not — it is typed `AddColumn` DDL. That
is precisely why it would be destructive, and precisely why it is fortunate it is already applied.)

### 062000 — would have **failed**, not double-applied

This is the finding that changes the deployment outcome. Its `INSERT INTO approval_requests` lists
ten columns. On production, `approval_requests` has **seven further columns that are NOT NULL with
no DEFAULT**:

```
current_approver_name, current_approver_role, current_approver_type,
current_queue, sla_hours, escalated_to_role, priority
```

`20260816013100_RepairMigrationModelParity` added them *with* defaults and then explicitly
`DROP DEFAULT`-ed all seven (that file, lines 37-43) to match the EF model. An INSERT omitting them
aborts with **23502 not_null_violation** — and there is exactly one row to insert (the known
residual: 1 of 9 `PendingApproval` employee change requests with a NULL `approval_request_id`,
employee 4776, tenant `91dcd203…`). So the next pre-deploy migration step would have **failed
outright**, exactly like the incident `7687d16` was written to prevent.

This never surfaced because the migration was invisible to EF for 70 days: the only database that
ever ran it was one where those columns did not yet exist.

Being idempotent is not the same as being able to run at all. The `develop` comment asserting
062000 is "intended and safe" was checking the guards, not the column list.

**Fix** (in `Up()`, additive): before the INSERT, `ADD COLUMN IF NOT EXISTS` the seven columns with
the defaults `20260713073000` declares for them, then `ALTER COLUMN … SET DEFAULT` (needed on
production, where the columns exist without one); after the INSERT, `DROP DEFAULT` on all seven,
restoring the prior schema exactly. On a fresh database the `ADD` creates them, 073000's own
`ADD COLUMN IF NOT EXISTS` then no-ops, and `RepairMigrationModelParity` drops the defaults as it
always did. **Both orderings converge on identical schema and identical data** — asserted by the
test, which re-checks the no-default shape after the replay.

### 073000 — would have silently re-routed a pilot client's live approvals

All 14 columns and all 4 indexes it adds already exist, so its DDL half no-ops. Only the routing
`UPDATE` does work — and the `develop` comment claiming "re-derivation is idempotent … a second run
is a no-op" is **wrong**. `current_approver_*`, `current_queue` and `sla_hours` are assigned
unconditionally, not `COALESCE`-d.

Production's 8 pending `EmployeeChangeRequest` approvals have since been routed by the application:

```
priority | current_approver_type | current_queue   | sla_hours | manager_employee_id
High     | Role                  | Role:HR Manager | 48        | (1 on six rows, NULL on two)
```

Re-deriving from July-era employee data would have moved the six with a manager off the HR Manager
queue onto `Manager:<name>`, set `current_approver_employee_id`, and **halved their SLA from 48h to
24h** — changing who must act on eight of Evostel's live approvals, with no audit row. Destructive
in the way that matters, even though nothing crashes.

**Fix**: the routing `UPDATE` is now restricted to rows that were never routed
(`AND ar.current_queue = ''`, the column default) — precisely the population a backfill is for. In
production that matches exactly one row: the approval request 062000 creates immediately before, in
the same `--migrate`. The 8 already-routed rows are untouched.

Chosen over a history row deliberately: a history row would skip 073000 entirely and leave the row
062000 creates unrouted, and it would need a manual production step. The guard needs neither.

*Noted, not a defect:* the backfilled row lands at `priority = 'Normal'`, while its 8 siblings are
`'High'`. That is the original design's outcome — on a fresh database 073000's
`ADD COLUMN … DEFAULT 'Normal'` backfills `'Normal'`, so its `CASE WHEN ar.priority = ''` branch is
unreachable. Not invented behaviour, and it keeps fresh and production identical.

### Evidence

New: `backend-dotnet/Zayra.Api.Tests/JulyBackfillMigrationReplayTests.cs`. Own container. Migrates
to head, withdraws the two migrations from `__EFMigrationsHistory`, and rebuilds production's shape
— including **asserting** the seven columns are NOT NULL with no default before proceeding, so the
test cannot pass by drifting off the hazard. Then replays and checks the residual row is backfilled
and routed, the already-routed rows keep queue/SLA/approver-type, the workflow and step are not
duplicated, and the schema is restored.

**Fail before** (`develop` migration files):

```
Replay_AgainstProductionShape_Succeeds_BackfillsTheUnroutedRow_AndLeavesLiveRoutingAlone [FAIL]
  Did not expect any exception because the two unapplied July migrations must be able to run
  against production's schema, but found Npgsql.PostgresException (0x80004005):
  23502: null value in column "current_approver_name" of relation "approval_requests"
  violates not-null constraint

Replay_IsIdempotent_ASecondMigrateChangesNothing [FAIL]
  Npgsql.PostgresException : 23502: null value in column "current_approver_name" of relation
  "approval_requests" violates not-null constraint
```

That is the production failure, reproduced.

The idempotence test first passed both before and after — because with no seeded data the INSERT
selects zero rows, no constraint is evaluated, and it proved only that migrating twice does not
crash. It now seeds a pending change request and asserts the first replay creates exactly one
approval request before asserting the second creates none. Re-verified: it fails before, passes
after.

**Pass after**: both green.

### Migration gate

```
$ dotnet tool restore --tool-manifest dotnet-tools.json
Tool 'dotnet-ef' (version '8.0.11') was restored.
Restore was successful.

$ dotnet tool run dotnet-ef migrations has-pending-model-changes --project backend-dotnet/Zayra.Api/Zayra.Api.csproj
Build succeeded.
No changes have been made to the model since the last migration.
```

Both edited migrations are raw SQL, so the model snapshot is untouched.

---

## Production SQL required

**None.**

`20260713061000_AddSalaryStructureEligibilityAndVersioning` is already in `__EFMigrationsHistory`,
so there is no history row to insert. The two genuinely-unapplied migrations are now safe to run on
their own, and the next `--migrate` applies them without a manual step.

Verify after the next deploy (read-only):

```sql
-- All three July migrations recorded; the backfill did its one row and nothing else.
SELECT "MigrationId" FROM "__EFMigrationsHistory"
 WHERE "MigrationId" LIKE '20260713%' ORDER BY 1;                      -- expect all of 061000/062000/073000

SELECT count(*) FROM employee_change_requests
 WHERE status = 'PendingApproval' AND approval_request_id IS NULL;      -- expect 0 (was 1)

SELECT current_queue, sla_hours, count(*) FROM approval_requests
 WHERE entity_name = 'EmployeeChangeRequest' AND status = 'Pending'
 GROUP BY 1, 2;                                                         -- expect Role:HR Manager/48 = 8, Role:HR Manager/24 = 1
```

The third is the one that matters: eight rows must still read `Role:HR Manager` with `sla_hours = 48`.
If any show `Manager:<name>` or `sla_hours = 24` beyond the single new row, the 073000 guard did not
hold and the pilot's approval routing has moved.

---

## Full suite and by-name reconciliation

```
$ dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj -v q --nologo
Passed!  - Failed:     0, Passed:  2598, Skipped:     0, Total:  2598, Duration: 2 m 14 s
```

Never `--no-build`. Launched only once machine load fell below 18 with zero competing test
processes (three other agents were running; the gate waited through load 48 → 16).

Reconciliation, **by name**: baseline 2586, now 2598, delta **+12** — exactly the 12 tests added,
listed by name below. Nothing was removed or renamed: no pre-existing test file was touched, and
`--list-tests` on the branch returns 2598 names of which precisely these 12 are new, leaving 2586.

```
Zayra.Api.Tests.JulyBackfillMigrationReplayTests.Replay_AgainstProductionShape_Succeeds_BackfillsTheUnroutedRow_AndLeavesLiveRoutingAlone
Zayra.Api.Tests.JulyBackfillMigrationReplayTests.Replay_IsIdempotent_ASecondMigrateChangesNothing
Zayra.Api.Tests.Platform.PlatformRedisHealthTests.Health_ProbeIsReadOnly
Zayra.Api.Tests.Platform.PlatformRedisHealthTests.Health_WhenRedisBackedCacheIsUnreachable_ReportsError
Zayra.Api.Tests.Platform.PlatformRedisHealthTests.Health_WithInMemoryFallback_ReportsFallbackMemory
Zayra.Api.Tests.Platform.PlatformRedisHealthTests.Health_WithLiveRedisBackedCache_ReportsOk
Zayra.Api.Tests.Platform.PlatformRedisHealthTests.Health_WithNoDistributedCacheRegistered_ReportsNotConfigured
Zayra.Api.Tests.Security.AuthSeederScopeRestartTests.BootstrapAdmin_DeliberatelyNarrowedScope_SurvivesRestart
Zayra.Api.Tests.Security.AuthSeederScopeRestartTests.TenantAdmin_WhoseLastGrantWasRevoked_IsNotPromotedToGroupScopeByRestart
Zayra.Api.Tests.Security.RawSqlExecutionRatchetTests.ApprovedRawSqlCountsAreNotInflated
Zayra.Api.Tests.Security.RawSqlExecutionRatchetTests.AuthSeederExecutesNoRawScopeWidening
Zayra.Api.Tests.Security.RawSqlExecutionRatchetTests.NoNewRawSqlWriteMayBeIntroduced
```

All ratchets satisfied, none suppressed, weakened or re-pinned.

---

## Files

Changed:

* `backend-dotnet/Zayra.Api/Infrastructure/Seed/AuthSeeder.cs`
* `backend-dotnet/Zayra.Api/Controllers/PlatformController.cs`
* `backend-dotnet/Zayra.Api/Migrations/20260713062000_BackfillEmployeeChangeApprovalRequests.cs`
* `backend-dotnet/Zayra.Api/Migrations/20260713073000_AddApprovalQueueAccountability.cs`

Added:

* `backend-dotnet/Zayra.Api.Tests/Security/AuthSeederScopeRestartTests.cs`
* `backend-dotnet/Zayra.Api.Tests/Security/RawSqlExecutionRatchetTests.cs`
* `backend-dotnet/Zayra.Api.Tests/Platform/PlatformRedisHealthTests.cs`
* `backend-dotnet/Zayra.Api.Tests/JulyBackfillMigrationReplayTests.cs`

Deleted: none.

```
$ git diff --diff-filter=D --name-only develop fix/platform-defects
(empty)
```
