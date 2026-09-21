# PR #64 — the two failing gates

Branch `fix/ci-gates` off `develop` @ `976c95e`.

## Headline

**Neither failure was introduced by `fix/config-consumers` or `feat/tenant-configurability`.**
Both were introduced by **`8cb38bc` "test(integrity): make the e2e suite capable of failing"**
(2026-09-17), which added a pre-flight and a row-count assertion that had **never once run green
in CI**. They surfaced now only because `8cb38bc` reached `develop` through the wave merges.

Evidence that `8cb38bc` is the common ancestor of both failures:

```
$ git merge-base --is-ancestor 8cb38bc ae47d43   # ae47d43 = last PASSING run of both jobs
NO
$ git merge-base --is-ancestor 8cb38bc 976c95e   # develop
YES
$ git merge-base --is-ancestor 8cb38bc origin/main
NO
```

Both gates flipped from `success` to `failure` on **five branches simultaneously** at 21:57–21:59
on 2026-09-20 — `develop`, `feat/timesheets`, `fix/finance-concurrency`,
`refactor/approval-convergence`, `integration/wave5` — which is the signature of a shared base
change, not of either named stream. The last green run of both jobs was `ae47d43`
(`perf/dashboard-kpi-coverage`, 05:27), whose parent is `16ad1b3` from 09-19.

This matters for the merge decision: **the pilot is not broken.** Neither failure reflects a defect
that would reach Evostel. They are a CI-configuration gap and a test-authoring gap.

---

## Failure 1 — `/leave rendered 0 data row(s)`

### Root cause

`frontend/src/views/LeavePage.tsx` — the entire Leave module (1,990 lines, 14 tabs) renders **no
row-shaped DOM of any kind**. `renderedRowCount` (`frontend/e2e/helpers.ts:234-245`) recognises
exactly four shapes: `tbody tr`, `[role="row"]`, `[data-testid$="-row"]`, `li[data-id]`. The Leave
module matched none of them, so the assertion added at `frontend/e2e/pilot-critical.spec.ts:17`
(`MUST_HAVE_ROWS` includes `/leave`) was **structurally unsatisfiable** — it could not have passed
with any data, on any tenant, on any tab.

```
$ grep -c "<tbody" frontend/src/views/LeavePage.tsx        # 0
$ grep -c "<tbody" frontend/src/views/AttendancePage.tsx   # 3   (/attendance passes)
$ grep -c "<tbody" frontend/src/views/ApprovalsPage.tsx    # 1   (/approvals passes)
```

`/leave` defaults to the dashboard tab (`LeavePage.tsx:1956`, `useState<Tab>('dashboard')`), whose
two record panels were built from anonymous `<div>`s (`LeavePage.tsx:277-293` and `:305-318`).

### It is NOT a seed gap, and NOT a code regression

Live DOM probe against a locally seeded `intelliflow` tenant — every `/leave` API call returns 200
and real records render on screen:

```
=== LEAVE API CALLS ===
  200 leave/calendar/today
  200 leave/requests?status=PendingManagerApproval
  200 leave/reports/dashboard
  200 features/modules
=== ROW-SHAPED SELECTOR COUNTS IN <main> ===
{ tbodyTr: 0, roleRow: 0, testidRow: 0, liDataId: 0, tables: 0, listItems: 0, surfaces: 7 }
=== PANELS ===
 [5] Pending Approvals View all Sunita Patel Casual Leave · Sep 24, 2026 – Sep 25, 2026 2d
     Raj Krishnamurthy Annual Leave · Oct 12, 2026 – Oct 23, 2026 12d
```

So: the module is not gated off (`features/modules` 200, page renders), the query is not empty
(two real leave requests are on screen), and the page does not throw. The data was always there —
the counter simply could not see it. This rules out all four candidate causes in the brief,
including the seed-gap explanation.

`ModuleGate.tsx` and `ModuleCatalog.cs` are exonerated: the page renders its full nav and KPI
cards, which a blocked module would not.

### The fix

`frontend/src/views/LeavePage.tsx:277-296` and `:308-322` — the two dashboard record lists are now
semantic `<ul>` / `<li data-id>` instead of nested `<div>`s. **The test was not touched.** This is
a genuine markup defect independent of the gate: Leave was the only module rendering record lists
with no list or item semantics, leaving assistive technology with nothing to navigate. Every other
module already renders records as a `<table>`.

**Note for the reviewer:** with `/leave` in `MUST_HAVE_ROWS`, the gate now effectively asserts that
the Leave dashboard's *Pending Approvals* panel is non-empty on the seeded tenant. That is a fair
reading of the gate's stated intent ("these three screens must show actual rows"), but it is worth
knowing it is what the assertion now pins. Separately, the seeded tenant shows **"On Leave Today:
0 — No employees on leave today."** That is real and unrelated to this fix; it is a demo-quality
observation, not a defect, and I have left it alone.

### Reproduction — before

```
Error: /leave rendered 0 data row(s); at least 1 was required.
This is the blank-module failure the pilot feared. Main-region text (first 400 chars):
Leave & Absence Management
...
0

On Leave Today

3
   at expectNonEmptyList (frontend/e2e/helpers.ts:261:11)
1 failed
  [tenant-pilot] › e2e/pilot-critical.spec.ts:47:7 › client-pilot critical tenant lane
```

Byte-identical to CI, including the trailing `0 / On Leave Today / 3`.

### Reproduction — after

```
[pre-flight] OK — http://localhost:5273 → http://localhost:5217: ready, 6 active tenants, 0 pending migrations.
Running 1 test using 1 worker
[pilot] /attendance rendered 1 data row(s)
[pilot] /leave rendered 2 data row(s)
[pilot] /approvals rendered 1 data row(s)
  ✓  1 [tenant-pilot] › e2e/pilot-critical.spec.ts:47:7 › client-pilot critical tenant lane › real
        seeded tenant loads dashboard and every core module without API failures (3.2s)
  1 passed (3.9s)
```

---

## Failure 2 — security gate never ran (`/health/ready` 503)

### Root cause

`.github/workflows/ci.yml` — the `chrome-security-gate` job **never applied migrations.** It set no
`Database__RunMigrationsOnStartup` and ran no `--migrate` step, so `Program.cs:840` took the
"Skipping EF Core migrations on startup" branch. The schema still appeared, because
`backend-dotnet/Zayra.Api/Infrastructure/Seed/AuthSeeder.cs:32` calls `EnsureCreatedAsync`, which
materialises all ~320 tables straight from the model and writes **no `__EFMigrationsHistory`**.

`Database.GetPendingMigrations()` therefore correctly reported *every* migration as pending, and
`ProductionReadinessEvidence.ResolveStatus` (`ProductionReadinessEvidence.cs:56`) failed closed.

The app was **never unhealthy** — this is the important part, because a genuine startup regression
would also break the pilot on deploy. All six workers registered heartbeats normally; readiness
reported them as `unavailable` only because `ProductionReadinessEvidence.cs:30-34` short-circuits
the worker probe when migrations are pending. The DI / `Program.cs` / `SecurityHeaders.cs` changes
from `feat/tenant-configurability` are **not** implicated.

Local repro with the security gate's exact env, proving both halves:

```
$ curl -s -o ready.json -w '%{http_code}' http://localhost:5217/health/ready
503
  "status": "not_ready",
  "database": { "status": "ok", "healthy": true, "latencyMs": 1 },
  "workers":  { "healthy": false, "missingCount": 6 },
  "pendingMigrations": 69

$ docker exec cifix-pg psql -U postgres -d zayra -tAc 'select count(*) from "__EFMigrationsHistory"'
ERROR:  relation "__EFMigrationsHistory" does not exist
$ docker exec cifix-pg psql -U postgres -d zayra -tAc \
    "select count(*) from information_schema.tables where table_schema='public'"
322
```

322 tables, zero migration history — `EnsureCreated`, exactly as diagnosed. And the workers had in
fact all reported in:

```
$ docker exec cifix-pg psql -U postgres -d zayra -tAc 'select worker_name,status from worker_heartbeats'
ai-insights|Started
compliance-reminders|Started
report-schedules|Healthy
notification-delivery|Started
background-jobs|Healthy
qiwa-sync|Healthy
```

Only the `chrome-security-gate` job was affected. The `browser-pilot` job brings the stack up with
docker compose, where `RUN_MIGRATIONS_ON_STARTUP` defaults to `true` (`docker-compose.yml:37`) —
which is why its `/health/ready` wait has always succeeded.

### The fix

`.github/workflows/ci.yml` — two changes to the `chrome-security-gate` backend step, both
strengthening:

1. `Database__RunMigrationsOnStartup: 'true'`. Migrations run first, so the history table exists,
   `EnsureCreatedAsync` no-ops on the already-created schema, and readiness becomes a real signal
   instead of a permanent 503. This matches what the Browser Pilot lane has always done.
2. The readiness wait now polls **`/health/ready`** rather than `/health/live`, and prints the
   readiness JSON on timeout. Waiting on `live` declared the stack up while readiness was still
   503 and handed the failure to Playwright minutes later with no payload.

**The pre-flight was not weakened.** `global-setup.ts` is unchanged.

### Reproduction — before / after

Before (`/health/live` only, no migrations): `/health/live` 200 after 9s, `/health/ready` **503**,
`pendingMigrations: 69`, gate aborts with zero tests run.

After (`Database__RunMigrationsOnStartup=true`):

```
ready HTTP 200
status: ready   pendingMigrations: 0   activeTenants: 4
workers healthy: True  healthyCount 4  starting 2  missing 0
```

And the gate that never ran now runs, and passes — roles and tenant isolation verified on this
build:

```
  ✓  11 [security-gate] › isolation.spec.ts:43:7 › a tenant user cannot reach platform administration
  ✓  13 [security-gate] › isolation.spec.ts:113:7 › a company-scoped user sees only their own company
  ✓  14 [security-gate] › isolation.spec.ts:129:7 › a company-scoped user cannot read a sibling company
  ✓  16 [security-gate] › isolation.spec.ts:167:7 › a selected-companies user sees exactly their granted companies
  ✓  19 [security-gate] › isolation.spec.ts:228:7 › the auditor is genuinely read-only (API)
  ✓  22 [security-gate] › isolation.spec.ts:284:7 › logout invalidates browser access
  24 passed (1.1m)
```

---

## Gate output

| Gate | Result |
|---|---|
| Backend full suite | `Failed: 0, Passed: 2557, Skipped: 0, Total: 2557` — baseline held, no ratchet bumped |
| `npx tsc --noEmit` | exit 0, no output |
| `npx next build` | `✓ Compiled successfully in 6.3s` |
| `npx playwright test -c e2e/playwright.browserless.config.ts` | `12 passed (2.0s)`, RTL pin intact at 9 |
| Migration drift | `No changes have been made to the model since the last migration.` |
| `tenant-pilot` lane | `1 passed` |
| Chrome security gate | `24 passed` |

RTL ratchet explicitly confirmed green (`the pinned exception count may only go down` ✓); the fix
introduces no physical direction utilities.

### No deletions

```
$ git diff --diff-filter=D --name-only develop fix/ci-gates
(empty)
```

## Local reproduction environment

Isolated from the product owner's stack throughout — postgres `cifix-pg` on **:55490**, backend on
**:5217**, frontend on **:5273**. The `zayra-*` containers on :5117/:5173 were never touched.
