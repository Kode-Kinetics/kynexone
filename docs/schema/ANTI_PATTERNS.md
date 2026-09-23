# Anti-patterns

Twelve mistakes, eleven this codebase already made at scale and one it came within a
review of shipping. Each is here with the real evidence, so the
rebuild does not recreate it and a new engineer can recognise the shape when someone proposes it
again in a design review.

**Every one of these was a reasonable local decision.** None was incompetence. Each was the
cheapest thing to do for one module on one day, and the cost only became visible at table 200.
That is why they need a stated rule rather than good judgement.

Evidence measured on `integration/backend-dotnet/Zayra.Api` (323 `DbSet`s in
`Data/ZayraDbContext.cs`).

---

## 1. An audit table per module

**Evidence — 15 audit tables, 14 of them per-module:**

```
AdminAuditLog   AdvanceAuditLog   AttendanceAuditLog   AuditLog   BonusAuditLog
ComplianceAuditLog   EmployeeSelfServiceAuditLog   EmployeePayslipAccessLog
LeaveAuditLog   LoanAuditLog   LoginActivity   OvertimeAuditLog
PayrollAuditLog   PerformanceAuditLog   RecruitmentAuditLog
```

A generic `AuditLog` already existed. Fourteen more were added anyway — each time, because the
generic one lacked one column the new module wanted.

**What it costs.** "What did this user do last Tuesday?" is a 15-way `UNION` that nobody writes,
so the answer is "we can't tell you". Tamper-evidence, retention and PDPL erasure must each be
implemented 15 times, and they were not. There is no single `seq` to checkpoint.

**The rule.** **One audit table for the product, plus at most one exception with a stated reason.**
The target schema has exactly two: `audit_logs` (everything, checkpointed) and
`payroll_audit_logs` (money movement, row-chained). The second exists because its write rate is
low and its evidentiary bar is highest — a written reason, not a preference.

A module needing "one more column" writes it into `audit_logs.personal_data jsonb`, which is also
the column the purge job nulls. That is the whole point.

---

## 2. An approval table per module

**Evidence — 16 tables with `Approval` in the name:**

```
ApprovalRequest  ApprovalWorkflow  ApprovalWorkflowStep  ApprovalPolicy  ApprovalPolicyStep
ApprovalAuthority  ApprovalDecision  ApprovalDelegation
AdvanceApproval  AttendanceCorrectionApproval  BonusApproval  LeaveApproval
LoanApproval  OfferApproval  OvertimeApproval  PayrollApproval
```

Plus the change-request tables they coexist with (`EmployeeChangeRequests`,
`EmployeeProfileChangeRequests`, `EmployeeDocumentRequests`, `LeaveDelegations`). There are also
**two competing engines**: `ApprovalWorkflow`/`Step` and `ApprovalPolicy`/`Step`. One of them —
`ApprovalPolicy` — is now dead: `ApprovalPoliciesController` answers **410 on every verb**, and
`ApprovalPolicyStep` sits in the orphan register.

**What it costs.** "What is waiting for my approval?" queries eight tables. Delegation works in
three modules. Escalation works in one. Adding an approvable thing means a table, a controller and
a screen instead of a row.

**The rule.** **One approval engine. Adding an approvable thing adds a `request_type` *value*, not
a table.** Target: four tables — `approval_workflows`, `approval_requests`, `approval_actions`,
`approval_delegations` — serving thirteen request types including `Timesheet`.

**The tell in a design review:** any table whose name is `<Module><GenericConcept>`.
`LeaveApproval`, `AttendanceAuditLog`, `PayrollException`, `LeaveAIInsight`. The module name in
front of a generic noun means the generic thing was not generic enough and nobody fixed it.

---

## 3. One payslip spread across six tables

**Evidence.** The result of running payroll for **one employee for one month** is assembled from:

| Table | What it holds |
|---|---|
| `PayrollSlip` | a slip |
| `Payslip` | **a second, competing slip** |
| `PayrollRunEmployee` | who was in the run |
| `PayrollRunEmployeeSelection` | who was selected for the run |
| `PayrollEarning` | the plus lines |
| `PayrollDeduction` | the minus lines |
| `PayslipComponent` | **a third shape for a line** |

And twenty-five `Payroll*`/`Payslip*` tables in total, including `PayrollAllowance`,
`PayrollException` and `PayrollCycle` — all three dead, all three in the orphan register.

**What it costs.** Gross is `SUM` over one table, deductions over another, and nothing makes them
agree. "Which is the real slip?" has no answer in the schema. Reconstructing a two-year-old payslip
means joining six tables whose rows may have drifted independently.

**The rule.** **One frozen result row per subject per run, and one line table under it.** Target:
`payroll_slips` (1 per employee per run, `UNIQUE (tenant_id, run_id, employee_id)`, frozen by
trigger on lock) + `payroll_slip_lines` (every line, whatever its sign).

The slip carries its own **witnesses** — `full_basic`, `full_housing`, `contributory_wage`,
`gosi_base_policy`, `proration_factor`, the YTD set — so it can be reconstructed without joining to
anything that might have changed. A result row that needs a live join to explain itself is not a
result row.

---

## 4. Foreign keys that exist in C# and not in the database

**Evidence.** `ZayraDbContext.cs` has **17** `HasForeignKey` calls and **13** `HasOne` calls,
against roughly **900** `<Entity>Id` properties across `Models/`. The modelling audit's count is
**636 of 653 FK columns with no database-level foreign key**. Two methods, same conclusion: the
overwhelming majority of references in this database are a naming convention, not a constraint.

**What it costs.**
- Orphans accumulate silently. Nothing rejects a `DeptId` pointing at a deleted department.
- **Nothing stops a row pointing at another tenant's row.** Tenant isolation was entirely in
  application code, on 900 columns, with no backstop.
- The database cannot tell you what references what, so every ERD, every impact analysis and every
  migration ordering is guesswork.
- `ON DELETE` behaviour is whatever each service happened to write.

**The rule.** **Every reference is a real FK, and every tenant-to-tenant FK is composite.**

```sql
UNIQUE (tenant_id, id),                                   -- on every tenant-tier table
FOREIGN KEY (tenant_id, x_id) REFERENCES x (tenant_id, id)
```

A cross-tenant pointer then cannot be written, even by a bug, an import or a hand-run `UPDATE`.
The redundant unique index is the price; an isolation guarantee that does not depend on 900
correct code paths is what it buys.

**Where an FK is deliberately absent, it is written down:** `audit_logs` and `payroll_audit_logs`
are FK-free so their rows outlive a PDPL purge; `approval_requests.subject_id` is polymorphic. Those
are decisions with reasons. 636 is not a decision.

---

## 5. Two key types for the same entity

**Evidence.** An employee currently has **three** key forms:

| Form | Count in `Models/` |
|---|---|
| `public int EmployeeId` | 92 |
| `public int? EmployeeId` | 13 |
| `public Guid EmployeeId` (pointing at `PublicId`, not `Id`) | 9 + 3 nullable |
| `public Guid PublicId` on `Employee` itself | 1 |

So `EmployeeId` means `Employee.Id` in 105 places and `Employee.PublicId` in 12 — **the same
property name, two different targets, no type error either way.** The 12 are on
`EmployeeContract`, the Visa/Passport/WorkPermit records, `EmployeeLoan`, `SalaryAdvance` and
`EmployeeBonus`.

**What it costs.** Every join needs someone to know which flavour this table uses. A wrong guess
compiles, runs, and silently joins to a different employee — `int 1481` and the employee whose
`PublicId` starts `1481…` are both plausible. Every API must decide which id to expose, and they
did not all decide the same way.

**The rule.** **One key type per entity, and it is the one in the URL.** Target: `employee_id uuid`
everywhere; humans see `employee_number`. `uuid` and not `int` because the key appears in mobile
and API URLs and must not be enumerable — which is exactly what `PublicId` was invented to fix,
by adding a second key instead of changing the first.

Cost of fixing it: ~105 properties. **Owner question Q1.** Cost of not fixing it: this table, forever.

---

## 6. A settings table per feature

**Evidence — 29 tables matching settings / policy / config / preference / branding / master-data:**

```
SystemSetting  SecuritySetting  GCCComplianceSetting  TenantLocalizationSetting
TenantIdentityProviderSetting  TenantHrConfig  TenantBranding  PlatformConfigEntry
AdvancePolicy  ApprovalPolicy  ApprovalPolicyStep  AttendancePolicy  CompanyRatePolicy
CompanyTaxPolicy  LeavePolicy  LeavePolicyEligibility  LoanPolicy  OvertimePolicy  ShiftPolicy
AIModelConfig  PricingConfig  PricingModuleConfig  MasterDataType  MasterDataValue
EmployeeIdRule  NumberingRule  ESSDashboardPreference
EmployeeNotificationPreference  EmployeeNotificationCategoryPreference
```

Two of these (`EmployeeIdRule`, `NumberingRule`) hold **numbering counters**.

**What it costs.** "Where is this configured?" has 29 possible answers. Precedence between
`SystemSetting`, `TenantHrConfig` and `CompanyRatePolicy` is undefined and implemented differently
per module. Every new feature adds a thirtieth, because adding one is easier than finding out
where the twenty-ninth lives. And a **counter in a settings row** is a lost-update waiting to
happen: two concurrent reads, two writes, one duplicated employee number.

**The rule, in three parts:**

1. **One settings row per tenant** (`tenant_settings`), keyed by **section**, each section a
   versioned, validated C# record. Writes are `jsonb_set` on **one key**, guarded by that key's
   version, so two admins editing different sections cannot clobber each other — and a whole-row
   `PUT` is deliberately **not an API the service exposes**.
2. **Company-level overrides go in `companies.settings`**, with a stated precedence: company
   overrides tenant — **except money**. Revision 3 moved contractual above-floor pay out of that
   JSON into the `company_pay_policies` table, because a dated money rule needs an `EXCLUDE … gist`
   no-overlap constraint and JSON cannot carry one. *If a config value is an amount or a rate with
   an effective date, it is a table.*
3. **No counter ever lives in a settings blob.** Every human-facing number comes from
   `number_sequences`, allocated by `UPDATE … RETURNING` in one statement.

Reference data a *tenant* extends (leave types, pay components, roles) stays a **table**, because
rows need FKs. Reference data the *platform* owns (statutory rules, permissions) stays a table for
the same reason. Configuration one owner reads as a unit is JSON. That is the whole rule
(`CONVENTIONS.md` §8).

---

## 7. A cartesian graph query on every request

**Evidence** — `Infrastructure/Auth/TenantSessionSecurity.cs:101-105`:

```csharp
.Include(x => x.Tenant)
.Include(x => x.UserRoles).ThenInclude(x => x.Role)
      .ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
.Include(x => x.PermissionOverrides)
.Include(x => x.EmployeeUserAccounts)
.Include(x => x.EntityAccesses)
```

The same shape is repeated in `AuthService.cs:438-442`, `:1503-1507` and `:1524-1528`.

**Five sibling collection `Include`s on one root.** EF Core emits a single `JOIN` across all of
them, so the row count is the **product**: a user with 4 roles × 60 permissions × 3 entity accesses
× 2 overrides × 2 accounts materialises ~2,880 rows, most of them duplicate `Tenant` and `User`
columns, to answer "who is this and what may they do".

**What it cost.** Production login returned **502**. The cause was OOM kills: this query per
request, compounded by misconfigured GC environment variables. Fixed on the Render service
2026-09-22. It was not a slow query anyone profiled — it was the service dying.

**The rules.**

1. **Never `Include` two sibling collections from one root.** Use `AsSplitQuery()`, or separate
   queries, or project to a DTO. EF will not warn you.
2. **An authorisation check is not a graph traversal.** The target schema collapses
   `PermissionOverrides`, `EntityAccesses` and `EmployeeUserAccounts` into `user_roles` (scope
   columns + `expires_at`) and `users.employee_id`. The per-request question becomes a flat read
   over one narrow table.
3. **Anything on the per-request path gets a row-count budget.** "It works locally with one test
   user" is not a measurement — the cartesian only appears at realistic permission counts.

---

## 8. An AI table per module

**Evidence — 11 tables:**

```
AIInsight  AIRecommendation  AIModelConfig  AIHRQueryCache  AIHRQueryLog
AttendanceAIInsight  ComplianceAIInsight  LeaveAIInsight  EmployeeAIQueryLog
CandidateAIScore  PayrollAIValidationResult
```

Plus `BurnoutRiskSignals`, `EmployeeChurnPredictions`, `DocTypes` and `DocumentChunks`. A generic
`AIInsight` existed; four module-specific insight tables were added next to it. `AIModelConfig` is
a settings table (see §6) *and* an AI table.

**What it costs.** Cost and usage cannot be totalled, because there is no one place a model call is
recorded. Redaction, prompt-version tracking and provider policy are per-module or absent. 14
tables — **4% of the schema** — serve a feature set the target scope removes entirely.

**The rule.** **AI output is not a new storage shape.** A model-produced value belongs on the
record it is about, with its provenance columns (`method`, `confidence`, `model_version`,
`reviewer_action`) — exactly as the target schema freezes `statutory_rule_id` and `rules_version`
onto a payroll line. Model *calls* belong in one usage/cost table behind one gateway, never one per
caller.

**The general form:** *speculative capacity*. Fourteen tables were built for a capability before it
had a single consumer that changed behaviour. The `OrphanEntityRatchetTests` docstring names this
as the repository's single structural defect: **the read is the only optional step.** A model, a
migration, a controller, an API client and a form are all visible in a demo; the line that reads
the value and changes behaviour is invisible, and nothing in the build catches its absence. Hence
the ratchet: **do not add a `DbSet` before the code that reads it.**

---

## 9. Statutory values hardcoded next to an effective-dated table for them

**Evidence.** `Models/StatutoryRule.cs` exists. So does this:

| Location | Content |
|---|---|
| `Infrastructure/Payroll/PayrollValidationEngine.cs:34` | `private const decimal DefaultGosiCoveredWageCeiling = 45_000m;` |
| `Infrastructure/Compliance/GosiContributoryWageBasis.cs:60` | `public const decimal DefaultCoveredWageCeilingSar = KsaGosiWageBounds.DefaultMonthlyCeilingSar;` |
| `Infrastructure/Setup/SetupAssistantService.cs:375` | `new("GOSI_DED", "GOSI Deduction", "Deduction", "Percentage", 0, 9.75m, false)` |
| `Infrastructure/CountryPack/Ksa/KsaDescriptor.cs:13` | the rates as prose: *"Annuities (9% EE + 9% ER) + SANED (0.75% each) + OH (2% ER) … ≤ SAR 45,000; total EE 9.75%, ER 11.75%"* |
| `Infrastructure/CountryPack/Ksa/KsaCalculators.cs:9` | the same rates as a comment |

**What it costs.** A `const` has no effective date. When GOSI changes a rate — and the Entrant2024
ladder changes one **every July** — recalculating last year's payroll produces this year's number.
That is not a rounding difference; it is a wrong statutory figure on a document a regulator reads.
And a value named `Default…` invites a silent fallback, which is exactly the thing a statutory
engine must never do.

**The rule.** **A value with a legal effective date lives in an effective-dated table, and
nowhere else.** `statutory_rules` + `statutory_rule_bands`, resolved for the period being
calculated, with `rules_version`, `source_reference` and `verified_by`/`verified_at` naming who
checked it against the circular.

A regulation change is a **new row**, never an edit — otherwise recalculating a 2025 payslip uses
2026 law.

**And it must be frozen onto the result.** `payroll_slip_lines` carries `statutory_rule_id`,
`statutory_rule_band_id` and `rules_version` on every line, so an auditor in 2030 can reproduce a
2025 figure without trusting that the rule table still says what it said.

---

## 10. Two sources of truth for the same statutory value

The sharpest form of §9, and it deserves its own entry because the fix for §9 *created* it.

**Evidence.** `KsaCalculators.cs:102` reads the rate from the rules service:

```csharp
decimal sanedRate = await _rules.GetDecimalAsync( /* "gosi.saned_rate" */ );
```

…while `GosiContributoryWageBasis.cs:60` and `PayrollValidationEngine.cs:34` hold the ceiling as a
`const`, and `SetupAssistantService.cs:375` seeds a pay component with `9.75m` baked in. Three
tables also model the same rules: `StatutoryRule`, `GosiContributionRule`, `CountryPayrollRule` —
plus `CompanyStatutoryOverride`, a **live** tenant-editable surface
(`RatesController.cs:185,256,271`) that lets a customer change a statutory value.

So a GOSI rate has, simultaneously: a rules-service lookup, a C# constant, a seeded component
value, three modelling tables, and a tenant override.

**What it costs.** *Which one is right?* Nobody can answer from the code. Calculation, validation
and setup can disagree about the same month, and the disagreement surfaces as a variance against
the GOSI invoice that nobody can explain.

**The rules.**

1. **One table** for statutory values: `statutory_rules` (+ `statutory_rule_bands` for band-shaped
   ones). `GosiContributionRules`, `CountryPayrollRules`, `OvertimeMultipliers` and
   `NitaqatWeightRules` all merge into it.
2. **No tenant or company may override a statutory row.** Legally they may only *exceed* the floor,
   and that is the `company_pay_policies` table, applied as `max(statutory, contractual)`. The
   `CompanyStatutoryOverride` CRUD surface is removed and the entity merges into
   `company_pay_policies`, above-floor values only (**owner question Q6**).
3. **A missing or ambiguous statutory value blocks, it never defaults.** An unknown GOSI cohort or
   an unconfirmed GCC rate raises a `Block` in `payroll_issues` — and `Block` is **never
   overridable**, by design. The code already flags this `[COUNSEL]` and raises
   `WARN_GOSI_ENTRANT_COHORT_NOT_MODELLED`; the baseline turns that warning into a hard block.

**The general rule behind 9 and 10:** if a value can be wrong in a way a regulator would notice,
there is exactly one place it lives, exactly one way to resolve it for a date, and a named human
in `verified_by` who checked it.

---

## 11. Tenant isolation as an ambient default, with an escape hatch on every query

The most expensive one, and the reason `TARGET_SCHEMA.md` §19.2 exists.

**Evidence.**

| Fact | Measured |
|---|---|
| `IgnoreQueryFilters()` call sites in `Zayra.Api` (excluding `bin/`, `obj/`) | **654** — the audit counted 330; either way, it is not a surface anyone can review |
| The application's database role | **`neondb_owner`** — superuser-class, holds `BYPASSRLS`. `SECURITY_BLOCKERS.md:19` records that anyone obtaining the connection string "gets full read/write/drop on all tenant HR + payroll PII" |
| Unauthenticated requests | `ZayraDbContext.cs:67` — `if (user?.Identity?.IsAuthenticated != true) return true;` inside `_isSystemScope`. **An unauthenticated principal is system scope.** |
| Database-level isolation | **None.** No RLS, no policies. Isolation is 654 correct decisions in C#. |

**What it costs.** Three separate failure modes, all of them silent:

1. **`IgnoreQueryFilters()` returns every tenant**, not "this tenant plus platform". One
   misplaced call in a list endpoint is a cross-tenant data leak that no test without two seeded
   tenants can see.
2. **The bypass is ambient, so it is granted by *position*, not by *declaration*.** A background
   worker, a seeder or a pre-HTTP context is system-scoped because of where it runs. Nothing
   declares "this code may cross tenants", so nothing can audit it.
3. **A counted bypass surface cannot shrink to zero**, because some of those 654 sites are
   legitimate — login before a tenant is known, platform administration, migrations. Counting
   conflates the necessary with the accidental, so the metric never reaches a state anyone can
   defend.

To the codebase's credit, the comment above that line records that a tenantless authenticated
principal now matches **zero** rows — "failing CLOSED — rather than the old fail-OPEN". That is a
real improvement, and it is also the tell: the safety of the whole scheme depends on a subtlety
documented in a comment on one property.

**The rule.** **Isolation belongs in the database, and every bypass is a named place, not a flag.**

1. **RLS on all 76 tables**, `ENABLE` **and** `FORCE`, three generated policy shapes and one
   hand-written `p_auth`. **Fail-closed by construction:** with `app.tenant_id` unset,
   `tenant_id = app.current_tenant()` is NULL, no row is visible, and every INSERT raises `42501`.
2. **The application stops being a superuser.** Five roles; **no LOGIN role holds `BYPASSRLS`**, and
   a test asserts it from `pg_roles.rolbypassrls` rather than from review.
3. **Four named bypass surfaces replace the ambient one** — `app.resolve_login()` for login,
   `kynex_platform` for administration, `kynex_job` with an explicit per-tenant loop for workers,
   `kynex_owner` for migrations. Each is one auditable place with its own test.
4. **Reference data stops being filtered at all** (policy shape (c)), which removes the largest
   bypass category rather than auditing it.
5. **The measurement changes from a count to a coverage ratchet**: `SELECT relname FROM pg_class …
   WHERE relkind IN ('r','p') AND NOT relrowsecurity` must return zero rows. **A new table without a
   policy fails by absence rather than by review** — and that is the whole difference. A reviewer
   must notice a missing filter; a ratchet cannot fail to.

**The EF filters stay.** RLS is the second layer, not a substitute. What is retired is the
bypass-*count* ratchet, because counting call sites stops being the measurement once there are four
surfaces.

**The general form:** *a safety property enforced by convention at every call site*. It is the same
shape as anti-pattern §4 — 636 foreign keys that existed only in C# — and it fails the same way: not
all at once, but in the one place someone forgot, discovered by a customer.

---

## 12. A view that reads as its owner — the safe path that was the leak

The only entry here that was **caught before it shipped**, by the final adversarial review (F1). It
earns its place because it is the most instructive failure in the set: every individual decision was
correct, and the combination was a cross-tenant leak on the one read path the design *mandates*.

**The setup, all of it deliberate and all of it right:**

| Decision | Why it was right |
|---|---|
| `v_leave_balances` is the **only sanctioned way to read a leave balance** (§11.6) | There is no balance table. A balance is `SUM(days)` over the append-only `leave_ledger`, and a view is how you stop 40 call sites each writing their own SUM. |
| Every object is owned by `kynex_owner` | One owner, one DDL role, clean grants. |
| `kynex_owner` holds `BYPASSRLS` | It has to — it applies migrations and backfills. |
| RLS is `ENABLE`d **and** `FORCE`d on every table | `FORCE` exists precisely so the owner does not escape its own policies. |

**The defect.** A view without `WITH (security_invoker = true)` evaluates its base tables with the
privileges and policies of the **view owner**, not the caller. So `v_leave_balances` would have read
`leave_ledger` as `kynex_owner` — and **`BYPASSRLS` outranks `FORCE`**, so the one guard that looks
like it covers this does not.

Result: the mandated, documented, reviewed way to read a leave balance would have returned **every
tenant's balances** to any caller. Not a forgotten predicate; not a bypass someone added — the
happy path.

**Why it is the instructive one.** Four correct decisions composed into a leak, and **nothing in the
existing design would have caught it**: the policy-coverage ratchet as specified in revision 5
scanned `relkind IN ('r','p')` — tables and partitioned parents — so a view was not merely
unprotected, it was **not even in the population being checked**. A ratchet with the wrong
population is a gate that cannot fail, which this repo has shipped before (a lint that passed
vacuously, a health check that always returned zero).

**The rule.**

1. **Every view is `WITH (security_invoker = true)`.** No exceptions; the invoker's policies are
   what a view is for here.
2. **CI asserts `pg_class.reloptions` contains `security_invoker=true` for every view in the
   baseline** — not "we remembered on these two".
3. **The coverage ratchet spans `relkind IN ('r','p','v')`**, so a future view fails by absence.
4. The general form: **whenever you add a new kind of database object, widen the population of every
   ratchet that was written before it existed.** A gate's blind spot is invisible by definition —
   it reports green.

**And the sibling rule it implies:** a security property that depends on *who owns the object*
rather than *who is asking* will eventually be wrong, because ownership is an operational detail
that changes for reasons unrelated to security. `security_invoker` moves the question from "who
created this" to "who is calling it", which is the only question that has a defensible answer.

---

## The shapes, in one page

Recognise these in a design review. All ten are one of five:

| Shape | Examples here | The question that kills it |
|---|---|---|
| **`<Module><GenericConcept>`** — the generic thing was not generic enough, so it was copied | 14 audit tables, 12 approval tables, 4 AI insight tables, 9 policy tables | *Why can the shared one not carry this?* |
| **Two models of one truth** — the replacement shipped, the original never left | `PayrollSlip`/`Payslip`, `ApprovalWorkflow`/`ApprovalPolicy`, `Employee.Id`/`PublicId`, `StatutoryRule`/`GosiContributionRule`/`const` | *Which one is authoritative, and what deletes the other?* |
| **A constraint that lives only in code** | 636 missing FKs, no-overlap in services, tenant isolation across 654 bypass sites | *What happens if a bug, an import or psql writes this row?* |
| **A safety property that is ambient rather than declared** | system scope by position; `IsAuthenticated != true` → bypass; `neondb_owner` everywhere | *Which one place grants this, and what test proves nowhere else does?* |
| **A guard whose population is narrower than the thing it guards** | the coverage ratchet that scanned tables but not views; the bypass lint that misses `SqlQueryRaw`; a direct `rolbypassrls` check that a `GRANT` defeats | *What kind of object could exist that this check would not even look at?* |
| **Speculative capacity** — storage built before a consumer | 14 AI tables, 29 orphan entities, `AttendanceRules` (*"a generic rule engine. There is no rule engine"*) | *Which line of code reads this and changes behaviour?* |
| **A per-request graph traversal** | the auth `Include` chain, the OOM | *How many rows does this return for a realistic user?* |

And one meta-rule, from the orphan ratchet's own docstring — the reason all of this accumulated
without anyone noticing:

> **The read is the only optional step.** A model, a migration, a controller write path, an API
> client and a form are all visible in a demo and survive a save-and-reload check. The consumer
> comes last, is invisible, and nothing in the build catches its absence. The failure surfaces a
> quarter later at a customer, as a control that was never there.

Which is why the fixes in this schema are constraints, triggers, ratchets and CI gates — not
guidelines. A rule that depends on remembering it has already failed here, 636 times.
