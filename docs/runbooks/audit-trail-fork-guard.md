# Audit trail: fork guard, append-only triggers, and the erasure path

Migration: `20261026000100_HardenAuditTrailIntegrity`. All SQL lives in
`Infrastructure/Audit/AuditTrailGuardSql.cs` (the migration and the Postgres tests share it).

## What the migrations do

Two migrations, in order:

1. `20261026000100_HardenAuditTrailIntegrity` (transactional)
   - Installs `audit_trail_append_only()` and a `BEFORE UPDATE OR DELETE` row trigger on every audit table
     (`AuditTrailGuardSql.AuditTables`), plus a `BEFORE TRUNCATE` statement trigger that refuses table wipes.
     `payroll_audit_logs` keeps its own function (sealing rule), which also honours the erasure flag.
   - **Fork precheck.** If `audit_logs` already holds chain forks and no guard index exists, the migration
     stops with `RAISE EXCEPTION` carrying the group and row counts. It changes nothing. See "Accepting legacy forks".
2. `20261026000200_AddAuditChainForkGuardIndex` (non-transactional, `suppressTransaction`)
   - Drops a leftover INVALID index from an earlier failed attempt, then runs
     `CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ux_audit_logs_tenant_previous_hash` on
     `audit_logs (coalesce(tenant_id, zero-uuid), previous_hash) WHERE previous_hash <> ''`. Rows with an empty
     `previous_hash` are unsealed pre-chain legacy rows (the column defaulted to `''`), so a tenant legitimately holds
     many; they are excluded from the guard and from the precheck. Platform (NULL tenant) rows form one chain and are
     guarded as tenant zero-uuid. Audit inserts are not blocked while it builds.
   - Estimate the build from `SELECT pg_size_pretty(pg_relation_size('audit_logs'))` before deploying.

**Deploy window.** Migrations run before the Production approval of the new build. Until the new build is live,
the OLD build can still fork on a race (unlocked tail read), and the unique index then rejects the second insert
(23505) after its business change committed. Ship migrations and application as one gated step, or accept the
window, which lasts as long as the old build serves traffic. Old-build tenant erasure also hits the new triggers.

## Accepting legacy forks (only when the precheck refuses)

Forks cannot be repaired (rows are hashed and now trigger-protected) and must not be deleted. If compliance
accepts them as a documented exception, create an explicit partial guard by hand, then re-run the deploy; the
precheck passes because the index exists and migration 2 is a no-op:

```sql
-- Exclude exactly the accepted legacy rows. Pick a predicate that selects only them
-- (for example by id range or by a created_at_utc cutoff taken from the precheck query below).
CREATE UNIQUE INDEX CONCURRENTLY ux_audit_logs_tenant_previous_hash
  ON audit_logs ((coalesce(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)), previous_hash)
  WHERE previous_hash <> '' AND created_at_utc > '<cutoff after the last accepted legacy fork>';
```

`GET /api/audit-logs/integrity` keeps reporting the legacy break positions. A partial guard shows in
`SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_audit_logs_tenant_previous_hash'` (it has a WHERE clause).
Never use a cutoff earlier than the newest accepted fork, and prefer a cutoff comfortably after any app/DB clock skew.

## Precheck (run before deploying; read-only)

```sql
-- Historical forks: groups of rows chained off the same tail. Expect zero rows.
SELECT coalesce(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid) AS tenant,
       previous_hash, count(*) AS rows_sharing_tail,
       min(created_at_utc) AS first_seen, max(created_at_utc) AS last_seen
FROM audit_logs
WHERE previous_hash <> ''
GROUP BY 1, 2
HAVING count(*) > 1
ORDER BY rows_sharing_tail DESC;

-- Is the guard present, and is it full or partial?
SELECT indexname, indexdef FROM pg_indexes WHERE indexname = 'ux_audit_logs_tenant_previous_hash';

-- Is every audit table protected?
SELECT c.relname FROM pg_class c
WHERE c.relname ~ '(_audit_logs|_audits|_payslip_access_logs)$' OR c.relname = 'audit_logs'
EXCEPT
SELECT c.relname FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
WHERE t.tgname LIKE 'trg\_%\_append\_only' AND NOT t.tgisinternal;
```

## Verification

- `GET /api/audit-logs/integrity` central chain, per tenant, read-only, streamed in keyset pages of 1000 (bounded
  memory). Optional `fromUtc`, `toUtc`, `maxRows`. One verification at a time per tenant (409) with a 5 s cool-down
  (429). A window that starts mid-chain skips only its first link check.
- `GET /api/audit-logs/integrity/payroll` same shape, by `Seq` (`fromSeq`, `toSeq`, `maxRows`); needs the
  `payroll.read` permission on top of the tenant Admin role.
- **Known limit: tail truncation.** Deleting the newest rows leaves a valid prefix; the chain cannot prove how long
  it should be. Closing this needs an external anchor (periodic per-tenant chain-head checkpoint to separate storage).
  Not built; track as a follow-up.

## Retention and erasure (the controlled path)

No retention rule deletes audit rows on a schedule. The only sanctioned DELETE is whole-tenant erasure (manual
`DELETE /api/platform/tenants/{id}/purge?confirm=PURGE` and the `SoftDeletedTenantRule` sweep). Both set the
**transaction-local, tenant-scoped** flag `set_config('app.audit_erasure', '<tenant id>', true)` inside the erasing
transaction; the triggers then permit DELETE of that tenant's rows only (never UPDATE, never another tenant, never a
table wipe). The flag cannot leak across pooled connections. A flag stolen through SQL injection erases one tenant
at most. Longer term: a SECURITY DEFINER erasure function and `REVOKE` on the app role when roles move into migrations.

**Owner decision pending (payroll and finance trails).** `payroll_audit_logs`, `loan_/advance_/bonus_/compliance_`
audit tables are removed by a manual tenant purge, while `audit_logs`, `admin_audit_logs` and
`retention_purge_audits` are kept by both erasure paths (and the two paths' retained sets differ). Statutory payroll
record retention may require keeping the payroll trail after a tenant purge. Decide, then align the two retained sets.

## Locking and ordering

A save that carries audit rows is two phases in ONE transaction: business changes first, then the per-tenant audit
lock and the audit INSERTs last. The audit lock is a bounded try-lock (5 s); a stuck holder fails the write with
`AuditSealFailedException` instead of queueing the tenant's writes. Rule for new code: do not take another advisory
lock or `FOR UPDATE` after an audit-bearing save inside one transaction; stage audit rows in the final save of a long
batch. Failed saves detach their staged audit rows; code that catches a failed save and keeps using the same context
should also call `IAuditService.DiscardStaged()`.

Prefer `IAuditService.Stage(...)` before the business `SaveChangesAsync` so the change and its audit row commit
atomically. `WriteAsync` after an earlier committed save is a separate transaction. Remaining post-save callers
are counted by `UnstagedAuditWriteCallers_NeverGrow` (the ceiling may only go down).

Table wipes are refused by a statement trigger. A table owner can still drop the trigger; when database roles are
managed in migrations, `REVOKE TRUNCATE, TRIGGER` from the application role.
