---
name: kynexone-schema
description: Use when touching the KynexOne database — adding or changing a table, column, index, constraint, migration, EF entity or DbContext mapping; writing a query that crosses companies; changing partitioning, retention or audit; or reviewing any of these in a PR. Triggers include "add a table", "new column", "write a migration", "change the schema", "IgnoreQueryFilters", "cross-company", "why is this query slow", "add an index", "soft delete", "retention", "audit log", and any question about how the data model is meant to work.
---

# KynexOne schema rules

The database is being rebuilt from 323 tables to 76 because the old one grew by copy-paste:
14 audit tables, 17 approval tables, payroll results in 6 tables, and 636 of 653 relationships
unenforced. **Every rule below exists because breaking it already cost this product an outage,
a wrong number, or a silent data leak.**

**Status check first.** The design (`TARGET_SCHEMA.md`, revision 6) is **not yet built**, and its
RLS/multi-tenant parts are superseded by the client-installed product (rule 4); partitioning and the
SQL baseline describe the target, not today's database. Read
`docs/schema/CONVENTIONS.md` §16, which separates "undecided" from "decided but not built",
before assuming a rule is live.

## Read before changing anything

| Question | Document |
|---|---|
| What are the rules? | `docs/schema/CONVENTIONS.md` |
| What is this table for? | `docs/schema/DATA_DICTIONARY.md` |
| How do the domains relate? | `docs/schema/ERD.md` |
| Who owns it, how long is it kept? | `docs/schema/OWNERSHIP_AND_RETENTION.md` |
| How do I change it safely? | `docs/schema/HOW_TO_CHANGE_THE_SCHEMA.md` |
| What must I not do? | `docs/schema/ANTI_PATTERNS.md` |
| Why is it shaped this way? | `TARGET_SCHEMA.md` (revision 6, **not yet built**; RLS/tenant parts superseded) |
| How is it installed and run? | `docs/CLIENT_INSTALLATION.md` |

If those documents and the code disagree, the code is right and the documents are a
bug — the `SchemaDocsSync` test exists to catch exactly that.

## The ten rules

1. **No new table without a named capability.** If an existing table can carry the fact,
   it does. A new table needs a line in the data dictionary, an owner, a `retention_policies`
   row and a declared RLS policy *shape* (a/b/c/grant-only) before it ships (the RLS shape is still demanded until the restructure) — the sync gate
   fails without them.
2. **One fact, one place.** A total and its lines, a status and its approval: one is
   authoritative, the other is derived, and the derivation is written down. Never both.
3. **The database enforces, not the service.** Foreign keys with an explicit ON DELETE,
   CHECKs for every status column, EXCLUDE for overlapping periods, triggers for state
   machines on the money path. "The code always remembers" is how the 330 bypasses happened.
4. **One organisation per installation; companies are the boundary.** KynexOne is installed on the
   client's server for one organisation, so there is no customer to isolate from another and the RLS
   and tenant design of `TARGET_SCHEMA.md` is **superseded** — do not build it, and do not add new
   tenant machinery (tenant GUCs, per-tenant sequences, composite `(tenant_id, …)` keys). What must
   hold is company isolation: company-scoped rows carry `company_id`, the two company filter tiers
   apply, and a bypass names its company restriction through `ScopedBypass`. `TenantId` still exists
   on every row until the single-organisation restructure (`docs/schema/CLIENT_SCHEMA.md` once it
   lands) removes it; until then keep stamping it, but never design a new table around it.
5. **Statutory values live in effective-dated rows, never in C#.** GOSI rates, leave
   entitlements and Nitaqat bands change by date and must be reproducible for a past month.
6. **Money is reproducible.** A payslip stores its own witnesses: the rate version, the
   contributory wage, the proration basis, the issued document's hash. Nothing that was
   filed may be recomputed from today's rules.
7. **Status changes follow the state machine.** No free-string updates. The payroll run's
   machine is a database trigger; the rest name one owning service and a test.
8. **JSON is for shapes nobody queries, joins or reports on.** Contractual money, anything
   audited and anything filterable stays relational.
9. **Indexes come from queries.** Each one names the query it serves. Not every foreign key
   gets an index; the deliberately unindexed ones are listed with their reason.
10. **Expand → migrate → contract, always.** A DROP never ships in the release that stops
    using the column. Migrations are forward-safe and re-runnable, with a database branch
    taken before production migrates.

## Before you open the PR

```
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj \
  --filter "FullyQualifiedName~Schema|FullyQualifiedName~Bypass|FullyQualifiedName~Orphan"
```

That covers the docs sync, the bypass ratchet and the orphan register. The catalogue
assertions that need a real database run in the `schema-gates` CI job, not this filter. An `EXPLAIN` for
any new hot query goes in the PR description.

**Bypass counts always carry their denominator.** Today: 346 raw `IgnoreQueryFilters()` sites
in `Zayra.Api` (7 inside the `ScopedBypass` helper), ratchet-approved 330, across 60 files,
tests excluded. A bare number in a review comment is how the register drifted last time.

## When something here is wrong

Say so rather than working around it. These rules were written against one product at one
moment; a rule that no longer fits is a design decision to reopen, not an obstacle to route
around. Routing around it is what produced the 323 tables.
