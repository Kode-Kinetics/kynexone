# D3 — Data retention: making the promise enforceable

Branch `feat/data-retention`, base `develop` @ `d730313`. **Ships disabled.** Three switches, all off.
Nothing in this change deletes anything until somebody turns it on, and even then the first thing it
produces is a report.

---

## 1. What was actually wrong

Two findings, both confirmed against the production database (read-only, `SELECT` only).

**`Employee.RetentionUntilUtc` is written and never read.** `EmployeesController.SoftDeleteEmployeeAsync`
(`Controllers/EmployeesController.cs:3026-3060`) stamps `RetentionUntilUtc = DeletedAtUtc + 7 years` and
`PrivacyStatus = "RetainedForStatutoryAudit"` on every deleted employee, and there was no consumer
anywhere. `Employee.RedactedAtUtc` — the column the redaction was meant to set — was referenced by
**nothing at all** outside the model and the EF migration snapshots. This is the repository's own named
defect class: the model, the migration, the write and the display all shipped; the one line that makes
the value mean something did not.

**The published privacy policy and the code disagree by 6.9 years.** `frontend/app/privacy/page.tsx:173`
promises "Deleted accounts — anonymised within 90 days of account closure, except where legal hold
applies." The code writes a 7-year deadline and then anonymises nothing, ever. That is not an
engineering discrepancy; see §7.

**Soft-deleted tenants keep everything, and there is no deletion date anywhere.**
`PlatformController.DeleteTenant` renames the slug to `{slug}__deleted_{id8}`, clears `IsActive`,
deactivates users and revokes tokens. It records the moment nowhere: `tenants` had no deletion timestamp
and `admin_audit_logs` contains **zero** `TenantDeleted` rows. Production sizing:

| | count |
|---|---|
| tenants with the `__deleted_` marker | **48** |
| of which `evostel__deleted_*` | **15**, holding **45** employee rows (3 each) |
| employee rows across all 48 soft-deleted tenants | **496** |
| inactive tenants WITHOUT the marker (suspended, not deleted) | **1** |

The reviewer's 45 is the Evostel share; the platform-wide number is 496. The single unmarked inactive
tenant is why the tenant rule keys on the **slug marker**, never on `IsActive` alone — a suspended tenant
must never be mistaken for a deleted one, and there is one in production right now to get that wrong on.

---

## 2. The per-entity policy, and the reasoning

The three dispositions are **not interchangeable**. Erasing a payroll record to satisfy an erasure
request breaks an obligation that outlives the request; keeping an expired credential hash is pure
liability with nothing on the other side of the scale.

| Subject | Trigger | Disposition | Why this one and not another |
|---|---|---|---|
| **`Employee`** (soft-deleted, `RetentionUntilUtc` elapsed, `RedactedAtUtc` null) | the deadline the product already writes | **Anonymise** | **There is not one foreign key in the database pointing at `employees`.** ~130 `EmployeeId` columns across ~45 model files are unenforced integers, so a hard delete would neither cascade nor be blocked — it would silently orphan payroll runs, payslips, GL postings, WPS/SIF records, GOSI references and final settlements, and the database would not say a word. Clearing the identifying columns in place satisfies the erasure interest, keeps the statutory record intact, and leaves every relationship valid. |
| …**with payroll evidence inside the statutory floor** | same deadline | **Retain + reason** | Two different clocks. The employee's erasure clock is deletion + 7y; a payroll record's floor is *the record's own date* + 7y. A final settlement is normally computed **after** the leaving date, so the payroll floor routinely outlives the erasure deadline. Where they conflict the rule keeps the record and writes why. It never resolves the conflict toward deletion. |
| **Payroll / GL / WPS / GOSI / final settlements** | never | **Retain, untouched** | Statutory employer evidence. Separately: `payroll_audit_logs` is a hash chain with a `BEFORE UPDATE/DELETE` Postgres trigger — deleting a row in a hash-chained ledger destroys the integrity proof for every row after it. Worse than doing nothing. |
| **`AuditLog` / `AdminAuditLog`** | never | **Retain** | `ZayraDbContext.EnforceAuditLogAppendOnly` throws on any modify/delete through the change tracker. They are the legal record of the erasure itself. The privacy policy's "audit logs — 2 years" is therefore **not implemented and cannot be without a design decision** — see §7. |
| **`RefreshToken`** | `ExpiresAtUtc` + 30-day grace | **Hard delete** | The only true delete here. A credential, not a business record: nothing references it, no statute covers it, and the policy commits to 30 days. The grace is not for authentication (an expired token is already unusable) but for **reuse-detection forensics** — the `FamilyId` lineage question "which session, from which IP, was replayed" is answered by rows that still exist. |
| **Soft-deleted tenant, window elapsed** | `SoftDeletedAtUtc` + `SoftDeletedTenantRetentionDays` (90) | **Hard delete of tenant-owned data; tenant shell kept** | The window is 90 days because the privacy policy already promises 90; the number is configurable because it is a commercial parameter, not an engineering one. The shell is kept as a tombstone so the retained audit trail and the erasure's own evidence still have an anchor. |
| **Soft-deleted tenant, deletion date unknown** | — | **Retain + flag, and start the clock** | 48 of 48 production tenants are in this state. A rule that guessed the date (from `CreatedAtUtc`, say) would be deleting real data on a fabricated basis. |
| **Suspended tenant** (inactive, no `__deleted_` marker) | — | **never a candidate** | One exists in production. Covered by a test. |

**What the employee rule deliberately does NOT reach, stated plainly.** It anonymises the `employees`
row only. Denormalised `EmployeeName` / `EmployeeCode` / `Iban` copies on ~40 dependent tables
(`PayrollPaymentRecord.Iban`, `SIFFileRecord.Iban`, `EmployeeFinalSettlement.EmployeeName`, …), the
full-PII `EmployeeHistory.SnapshotJson` blobs, and the object-store blobs behind
`EmployeeDocument.StorageUrl`, `EmployeeDocumentVersion.StorageUrl` and `Employee.ProfilePhotoStorageKey`
all survive. **The audit row counts and names them** (`residual` in `DetailsJson`) so the gap is measured
rather than implied. Reaching them is a second phase with its own design problem: blob deletion is not
transactional with the database, so a row-and-blob purge needs a two-phase protocol that does not exist
yet.

---

## 3. What was built

| File | What |
|---|---|
| `Infrastructure/Retention/DataRetentionOptions.cs` | the switches; every default is the safe one |
| `Infrastructure/Retention/RetentionContracts.cs` | `IRetentionRule` (read-only `EvaluateAsync` + mutating `ApplyAsync`), dispositions, outcomes, rule keys |
| `Infrastructure/Retention/Rules/ExpiredEmployeeRecordRule.cs` | reads `RetentionUntilUtc`; anonymise or statutory-retain |
| `Infrastructure/Retention/Rules/ExpiredRefreshTokenRule.cs` | the one hard delete |
| `Infrastructure/Retention/Rules/SoftDeletedTenantRule.cs` | tenant window, all-or-nothing erasure |
| `Infrastructure/Retention/DataRetentionSweepJobHandler.cs` | job type `retention.sweep` |
| `Infrastructure/Retention/DataRetentionScheduler.cs` | one job per tenant per day; off by default |
| `Models/RetentionPurgeAudit.cs` + mapping | the audit trail |
| `Migrations/20260921142407_AddDataRetentionMechanism.cs` | additive only |

**Scheduling is not invented.** The sweep is an ordinary F3 job: claimed with
`SELECT … FOR UPDATE SKIP LOCKED`, held by a fenced lease, checkpointed per item, resumed from its last
checkpoint after a crash. The scheduler does exactly one thing — enqueue `retention.sweep:{yyyy-MM-dd}`
per tenant — and the store's `(tenant, type, key)` idempotency means N instances waking at once produce
one job per tenant per day.

**`AsOfUtc` is fixed at enqueue**, not read from the clock inside the job. A retry four hours later must
reach the same verdict as the first attempt; a deletion decision that depends on when a worker happened
to pick the work up is not a policy.

**Idempotent under retry**, which `NpgsqlRetryingExecutionStrategy` forces: (a) the per-item checkpoint
and the effects commit in one transaction — there is no ordering in which a deletion lands without its
evidence; (b) every `ApplyAsync` re-reads its subject *inside* that transaction and no-ops if the work is
done (`RedactedAtUtc != null`, `PurgedAtUtc != null`), or uses a set predicate rather than a captured id
list (refresh tokens), so re-running removes whatever remains and nothing twice.

**Tenant erasure is all-or-nothing.** The manual `PlatformController.PurgeTenant` reports "unresolved
tables" and leaves a partially erased tenant behind. This one uses **savepoints** inside the runner's
transaction (PostgreSQL aborts a whole transaction on any failed statement, so try/catch alone would
poison it) and, if a table is still unresolved after two passes, throws
`BackgroundJobPermanentFailureException` — the item rolls back, **nothing** is deleted, and a human is
told which tables blocked it. A half-erased tenant is neither recoverable nor compliant.

**Ratchets.** `QueryFilterBypassRatchetTests` is unchanged and unbumped: the new code adds **zero** raw
`.IgnoreQueryFilters()` call sites. The erasure sweep needs a tenant-pinned bypass reached by *open
generic* (it discovers its types from `DbContext.Model`, so it cannot satisfy `TenantWide`'s
`ITenantOwned` constraint), so a new sanctioned helper `ScopedBypass.TenantWideByConvention<T>` was added
inside `ScopedBypass.cs` — the file the ratchet explicitly exempts as "the approved wrapper". That is the
conversion the ratchet asks for, not a way around it. `OrphanEntityRatchetTests` stays at 29:
`RetentionPurgeAudit` has real consumers outside `Models/`, `Data/` and `Migrations/`.

---

## 4. Evidence

### 4.1 Fail before / pass after

`RetentionColumnConsumerTests` is a pure source scan that references **no retention type**, so it
compiles and runs unchanged against the branch point. That is what makes it meaningful rather than
tautological. Run in a detached worktree at `develop` (`d730313`):

```
  Failed Zayra.Api.Tests.RetentionColumnConsumerTests.EveryRetentionLifecycleColumnHasAnEnforcementConsumerOutsideTheControllerThatWritesIt [283 ms]
  Employee.RetentionUntilUtc is referenced by [Models/Employee.cs, Controllers/EmployeesController.cs] and by no enforcement path outside Controllers/. A retention column that only the write path touches is a deadline nothing acts on.
  Employee.RedactedAtUtc is referenced by [Models/Employee.cs] and by no enforcement path outside Controllers/. A retention column that only the write path touches is a deadline nothing acts on.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1
```

The bar is deliberately "outside `Controllers/`": `EmployeesController` both *sets*
`RetentionUntilUtc` and *projects* it into a DTO, and neither makes the deadline enforceable.

After, on `feat/data-retention`, together with every ratchet and lint the change could plausibly move:

```
$ dotnet test --filter "DataRetentionSweepPostgresTests|RetentionColumnConsumerTests|QueryFilterBypassRatchetTests|
                        OrphanEntityRatchetTests|BackgroundJobInfrastructureTests|BypassLintTests|
                        ConfigurationConsumerTests|ExecutionStrategyLintTests|ScopedBypassTests"
Passed!  - Failed:     0, Passed:    71, Skipped:     0, Total:    71, Duration: 9 s
```

Full suite, on a machine gated to load < 18 with no competing test processes:

```
Passed!  - Failed:     0, Passed:  2596, Skipped:     0, Total:  2596, Duration: 1 m 17 s
```

Reconciled **by name** against the 2586/0 baseline: **+10**, all of them new and all of them mine —
nine in `DataRetentionSweepPostgresTests` and one in `RetentionColumnConsumerTests`.

The first full run failed one pre-existing test,
`PayComponentMigrationBackfillTests.Migration_BackfillsEveryUnseededTenant_…`, with
`42703: column "purged_at_utc" of relation "tenants" does not exist`. That was a real regression and is
fixed rather than worked around. The test migrates a container to `20260816200703` and then inserted
tenants with `db.Tenants.Add(...)` — an EF insert writes every column the **current** model has, so any
new `tenants` column breaks it. It now inserts with raw SQL naming the pre-F2 column list explicitly,
exactly as the same test already did for `pay_components`, so the next column added to `tenants` will
not break it either.

### 4.2 The dry run is honest

`DryRun_ReportsTheExpiredEmployee_AndLeavesEveryColumnUntouched` seeds an employee soft-deleted 8 years
ago (so the product's own 7-year deadline elapsed a year ago), runs the real job on the real runner
against real PostgreSQL with the **default** options, and asserts, in this order:

1. non-vacuity — exactly one `RetentionPurgeAudit` row exists for `employee.retention-expired`, its
   `EntityId` is the seeded employee's id, `Disposition = Anonymise`, `Outcome = Reported`,
   `DryRun = true`;
2. and then that the row is **still there**: `FullName == "Expired Person"`, `IqamaNumber ==
   "2123456789"`, `BankIban == "SA03…7519"`, `DateOfBirth` not null, `RedactedAtUtc` null,
   `PrivacyStatus` unchanged.

`RefreshTokens_AreReportedInADryRunAndDeletedOnlyWhenEnabled` makes the same point on the hard-delete
path: after the dry run all **4** seeded tokens are still present and the audit row says
`HardDelete / Reported / "3 refresh token(s)…"`.

### 4.3 Enabling it works

`Enabled_AnonymisesTheExpiredEmployee_KeepsTheRow_AndWritesTheAuditRow`: same seed,
`ApplyDeletions = true`. Afterwards `FullName == "[erased]"`, `IqamaNumber`/`PassportNumber`/`BankIban`/
`PersonalEmail`/`MedicalInformation` empty, `DateOfBirth` null, `EmployeeCode == "ANON-{id}"`,
`PrivacyStatus == "Anonymised"`, `RedactedAtUtc` set — **and the row still exists with its `JoiningDate`
intact**, because anonymise is not delete. The audit row is `Applied / Anonymise / DryRun=false` and its
`DetailsJson` names `IqamaNumber` and `BankIban` among the cleared columns.

For the refresh-token path: the three dead tokens are gone, the live one survives, and the audit row
records `deletedTokens: 3`.

`Enabled_RunTwice_IsIdempotent_AndDoesNotWriteASecondDecision` runs the sweep again the next day and
asserts `RedactedAtUtc` is unchanged and there is still exactly **one** decision row.

### 4.4 It cannot delete what statute requires it to keep

`Enabled_RetainsAnEmployeeWhosePayrollRecordIsStillInsideTheStatutoryFloor` seeds the same expired
employee **plus one payslip dated last month**, then runs with **every switch on**
(`ApplyDeletions = true`, `AllowTenantErasure = true`) so the refusal can only come from the rule:

- `FullName` still `"Expired Person"`, `IqamaNumber` still `"2123456789"`, `RedactedAtUtc` still null;
- the audit row is `Disposition = Retain`, `Outcome = Retained`, `DryRun = false`;
- `Reason` contains *"RETAINED under statute"* and *"payroll retention floor"*, and `DetailsJson`
  carries `statutoryFloorUntilUtc`.

Two more refusals are proved the same way:
`SoftDeletedTenantWithNoRecordedDeletionDate_IsRetained_AndItsClockIsStarted` (all switches on; the
tenant is retained because the *date* is unknown, and `SoftDeletedAtUtc` is stamped with today so
erasure is at least a further 90 days away) and
`AnInactiveTenantWithoutTheDeletedSlugMarker_IsNeverATenantErasureCandidate`.

### 4.5 Non-vacuity

Every "nothing was purged" assertion is paired with proof the run actually considered the record:
`SingleAuditAsync` fails if the decision count is anything but exactly one, and the suspended-tenant test
additionally asserts the sweep *did* act on that tenant through a different rule.
`ATenantWithNothingExpired_ProducesNoDecisionsAtAll` is the control: when there is genuinely nothing to
do the sweep writes zero rows, so a zero elsewhere means something.

### 4.6 Nothing was deleted from the repository

```
$ git diff --diff-filter=D --name-only d730313 feat/data-retention
(no output)
```

`develop` has since moved on to `cb78f971` (a parallel workstream landed `AuthSeeder` and two historical
migration edits), so the comparison is against this branch's actual point, `d730313`, which is also
`git merge-base develop feat/data-retention`. That merge added **no new migration and no model-snapshot
change**, so this branch's snapshot edit should merge cleanly.

### 4.7 Migration gate

```
$ dotnet tool restore --tool-manifest dotnet-tools.json
$ dotnet tool run dotnet-ef migrations has-pending-model-changes --project backend-dotnet/Zayra.Api/Zayra.Api.csproj
No changes have been made to the model since the last migration.
```

The migration is **additive only**: two nullable `timestamptz` columns on `tenants` and one new table.
No drop, no rename, no `NOT NULL` without a default on an existing table.

### 4.8 SQL-dependent behaviour against real Postgres

All ten tests are `[Collection("Integration")]` on the Testcontainers `postgres:16-alpine` fixture, with
the `RowLockingInterceptor` registered exactly as `Program.cs` does. The behaviour under test —
`ExecuteDeleteAsync`, savepoints inside the runner's transaction, `jsonb` columns, `GROUP BY … Max()`
translation for the statutory floor, and the queue's `FOR UPDATE SKIP LOCKED` claim — is not modelled by
an in-memory provider.

---

## 5. A dry run sized against Evostel's real data

Read-only `SELECT`s against production, reproducing each rule's exact predicate. **As of 2026-09-21,
running the sweep today in dry-run mode would report the following for Evostel.**

### Rule `employee.retention-expired` — **0 candidates**

| tenant | soft-deleted employees | past their deadline today | earliest deadline |
|---|---|---|---|
| `evostel` (live) | 3 | **0** | **2033-09-20** |
| each of the 15 `evostel__deleted_*` | 0 | 0 | — |

The three soft-deleted Evostel employees were deleted this month, so the product's own 7-year stamp puts
their deadline in **2033**. Nothing is due. Platform-wide the number is the same: **0 of 76** soft-deleted
employees are past their deadline, and the earliest deadline anywhere is 2033-07-14.

**This is the honest headline: the employee rule would purge nothing today, and will purge nothing for
seven years.** That is not a reason the rule is unnecessary — it is the reason it had to be built before
the first deadline arrives rather than after — but it does mean the 45 Evostel employee rows the review
identified are *not* reachable by it.

### Rule `auth.refresh-token-expired` — **0 candidates for Evostel**

Evostel has 128 users and 41 refresh tokens, none more than 30 days past expiry. Platform-wide,
**903 tokens across 5 users** would be deleted — the only rule with anything to do today, and the only one
where deletion has no statutory counterweight.

### Rule `tenant.soft-deleted-expired` — **15 Evostel candidates, all RETAINED**

All 15 `evostel__deleted_*` tenants match the marker and are inactive. Every one has
`SoftDeletedAtUtc = NULL`, because until this change nothing recorded a deletion date and no
`TenantDeleted` audit row exists for any of them. Each therefore produces:

> `Disposition = Retain`, `Outcome = Retained`, reason: *"this tenant is soft-deleted but no deletion date
> was ever recorded … the retention window has no start and cannot be shown to have elapsed."*

**The 45 Evostel employee rows are reachable only through this rule, and it refuses.** With erasure fully
enabled the first sweep would stamp today's date and still retain — the 45 rows would not become eligible
until **2026-12-20 at the earliest**, and only then with all three switches on.

Platform-wide: **48 soft-deleted tenants, 496 employee rows, 0 erasable today.**

### Summary of a dry run executed today

| rule | Evostel | platform-wide |
|---|---|---|
| `employee.retention-expired` | 0 | 0 |
| `auth.refresh-token-expired` | 0 | 903 tokens / 5 users |
| `tenant.soft-deleted-expired` | 15 candidates, **15 retained** | 48 candidates, **48 retained** |
| **rows actually removed** | **0** | **0** (903 tokens if `ApplyDeletions=true`) |

No sweep was run against production. These are `SELECT`s reproducing the rules' predicates.

---

## 6. How to turn it on

Three switches, in this order, and **do not skip a step**.

1. **Get a dry run.** `DataRetention__ScheduleEnabled=true`. Within 90 seconds of the next deploy the
   scheduler enqueues one `retention.sweep` per tenant per day. Every run is a dry run because
   `ApplyDeletions` defaults to false. Read the results:
   ```sql
   SELECT rule_key, disposition, outcome, count(*)
   FROM retention_purge_audits WHERE dry_run GROUP BY 1,2,3 ORDER BY 1,2;
   ```
   Per-run summaries are also on the job row (`GET /api/jobs/{id}`, gated on `audit.read`).
2. **Enable the reversible dispositions.** `DataRetention__ApplyDeletions=true`. This lets the employee
   rule anonymise and the refresh-token rule delete. It does **not** let a tenant be erased.
3. **Enable tenant erasure, separately and last.** `DataRetention__AllowTenantErasure=true`. Note the
   built-in delay: every existing soft-deleted tenant has an unknown deletion date, so the first enabled
   sweep only *starts* its clock. Nothing can be erased for a further
   `SoftDeletedTenantRetentionDays` (90) days.

Tunable, all with safe defaults: `SoftDeletedTenantRetentionDays` (90),
`StatutoryPayrollRetentionYears` (7), `RefreshTokenGraceDays` (30), `MaxCandidatesPerRule` (200),
`ScheduleInterval` (6h). To stop everything instantly, set `ScheduleEnabled=false` (or
`BackgroundJobs__WorkerEnabled=false`) and redeploy; an in-flight job yields its lease at the next
checkpoint without half-applying anything.

**Reverting is a redeploy, not a restore.** Anonymisation and deletion are irreversible. The switches are
the control; there is no undo.

---

## 7. Questions that need a lawyer, not an engineer

Stated plainly, because assuming any of these is worse than asking.

1. **PDPL erasure vs. Saudi labour-law retention — which wins, and on what basis?** The code now
   resolves the conflict in favour of retention and records the reason. That is the conservative
   default, not a legal determination. We need to know whether PDPL's erasure right can be refused on
   the basis of the employer's retention obligation, whether the refusal must be *communicated* to the
   data subject, and whether the answer differs for special-category data
   (`Employee.MedicalInformation`).
2. **Is `StatutoryPayrollRetentionYears = 7` correct for KSA?** The product's own privacy policy says
   "minimum 7 years or as required by tax authority"; `docs/CONFIGURABILITY_PROGRAM.md` says
   "payroll ≥ 5yr". GOSI, ZATCA and the Labour Law may each impose a different floor, and the binding
   one is the longest. 7 is an engineering default over a legal parameter.
3. **The published policy says 90 days; the code says 7 years.** `frontend/app/privacy/page.tsx:173`
   promises deleted accounts are "anonymised within 90 days of account closure"; the code stamps 7
   years and anonymised nothing. Which is the commitment we are held to — and if it is 90 days, the
   *existing* 76 soft-deleted employee records are already six-plus years out of compliance and need a
   remediation decision, not a forward-looking policy.
4. **Does "deleted account" in that clause mean the tenant, the employee record, or the login?** The
   implementation reads it as the tenant (90-day window) and the employee record separately (7 years).
   They cannot both be right.
5. **Is anonymisation-in-place sufficient to discharge an erasure request under PDPL** when
   denormalised copies of the name, employee code and IBAN persist on statutorily-retained payroll rows
   (§2, "what the rule does not reach")? If not, the statutory records themselves have to be
   pseudonymised, which is a materially larger change to evidence the employer may need to produce
   intact.
6. **Audit logs: the policy says 2 years; the system says never.** `audit_logs` and `payroll_audit_logs`
   are append-only by design (a context guard and a database trigger), and `payroll_audit_logs` is
   hash-chained, so deleting any row destroys the integrity proof for every row after it. Either the
   policy is wrong or the hash chain needs a documented truncation protocol. Engineering cannot pick.
7. **Attendance and leave logs: the policy says 3 years; nothing implements it.** Deliberately out of
   scope here — an attendance record is also payroll evidence, so the 3-year ceiling and the payroll
   floor may contradict each other for the same row.
8. **There is no legal-hold mechanism.** The policy's "except where legal hold applies" has nothing
   behind it: no field, no API, no UI. Nothing here can honour a hold, and a purge that cannot be
   suspended for litigation is a problem in its own right. We need to know whether a hold must be
   supported before any of this is enabled.
9. **Who is the controller for the retention decision?** KynexOne processes on behalf of the employer.
   If the employer is the controller, is a vendor-set retention window even ours to choose, or must it
   be per-tenant configurable with the tenant's own value? `docs/CONFIGURABILITY_PROGRAM.md` OD-14
   already flags this as an open program input needing a compliance advisor.
10. **Data residency interacts with all of the above.** Production Postgres is Neon and object storage
    is Backblaze B2 **in a US region** (see the outstanding-infra note). A retention policy over data
    that should not be where it is is the smaller of the two problems.

---

## 8. Follow-ups owed (engineering, not legal)

1. **`PlatformController.DeleteTenant` must set `Tenant.SoftDeletedAtUtc`** — a one-line write at the
   point of deletion, so a newly deleted tenant has a true date rather than a first-observed one. Not
   made here: that file is being changed concurrently by another workstream.
2. **`PlatformController.PurgeTenant`'s retained set must add `RetentionPurgeAudit`.** Today a manual
   purge would delete the retention evidence for the tenant it is erasing. Same file, same reason.
3. **The denormalised-identifier and blob sweep** (§2). Needs a two-phase protocol because blob deletion
   is not transactional with the database.
4. **No read API for the report.** Per-run summaries are on the job row; the per-record decisions are
   queryable only by SQL. A small `audit.read`-gated endpoint over `retention_purge_audits` would make
   the assessment answerable without database access.
5. **The scheduler intentionally registers no `ProductionWorkerNames` heartbeat.** `All` drives the
   readiness report and every name in it is expected to be beating; a worker that is off by default
   would report permanently stale. If the scheduler is turned on permanently, give it a name then.
6. `OrphanEntityRatchetTests`' doc comment still says "the only registered background job type in the
   product is attendance.process". Prose, not an assertion, but now stale.
