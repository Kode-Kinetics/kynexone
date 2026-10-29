using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;

namespace Zayra.Api.Tests;

/// <summary>
/// Real-Postgres proof of the audit-trail guards. The shared <see cref="PostgresFixture"/> builds its
/// schema with EnsureCreated (no migrations), so each test installs the EXACT SQL the migration
/// <c>HardenAuditTrailIntegrity</c> runs (shared via <see cref="AuditTrailGuardSql"/>) and removes it in
/// a finally block so the container stays clean for the sequential Integration collection.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class AuditTrailIntegrityPostgresTests
{
    private readonly PostgresFixture _fx;
    public AuditTrailIntegrityPostgresTests(PostgresFixture fx) => _fx = fx;

    private static async Task InstallTriggersAsync(ZayraDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.GenericFunctionSql);
        await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.StatementGuardFunctionSql);
        await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.CreateTriggersSql);
    }

    private static async Task RemoveTriggersAsync(ZayraDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.DropTriggersSql);
        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS audit_trail_no_wipe();");
        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS audit_trail_append_only();");
    }

    /// <summary>The context uses a retrying execution strategy, which forbids a bare BeginTransaction:
    /// run the unit inside strategy.ExecuteAsync, exactly as the production erasure path does.</summary>
    private static Task InTransactionAsync(ZayraDbContext db, Func<Task<bool>> work) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            if (await work()) await tx.CommitAsync(); else await tx.RollbackAsync();
        });

    private static RequestContext Ctx(Guid tenant) => new("10.0.0.1", "agent", Guid.NewGuid(), tenant);

    // ── 1. N parallel audit writes → one linear chain ────────────────────────────────────────────

    [Fact]
    public async Task ParallelWrites_ForOneTenant_ProduceASingleLinearChain()
    {
        await using var seed = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);
        const int writers = 24;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(async i =>
        {
            await using var db = _fx.CreateDb(); // one context (and connection) per concurrent writer
            await new AuditService(db).WriteAsync("race." + i, "Thing", i.ToString(), Ctx(tenant), null, CancellationToken.None);
        }));

        await using var read = _fx.CreateDb();
        var chain = await read.AuditLogs.AsNoTracking().Where(x => x.TenantId == tenant)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync();
        chain.Should().HaveCount(writers);
        chain.Select(x => x.PreviousHash).Distinct().Should().HaveCount(writers, "a shared previous_hash is a fork");
        AuditService.VerifyChain(chain).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ParallelStagedWrites_WithABusinessRow_CommitTogetherAndChainLinearly()
    {
        await using var seed = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            await using var db = _fx.CreateDb();
            var audit = new AuditService(db);
            audit.Stage("staged." + i, "Thing", i.ToString(), Ctx(tenant), null);
            audit.Stage("staged.second." + i, "Thing", i.ToString(), Ctx(tenant), null);
            await db.SaveChangesAsync();
        }));

        await using var read = _fx.CreateDb();
        var chain = await read.AuditLogs.AsNoTracking().Where(x => x.TenantId == tenant)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync();
        chain.Should().HaveCount(24);
        AuditService.VerifyChain(chain).IsValid.Should().BeTrue();
    }

    // ── 2. DB-level fork guard ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ForkGuardIndex_RejectsTwoRowsChainedOffTheSameTail()
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        await new AuditService(db).WriteAsync("genesis", "Thing", "1", Ctx(tenant), null, CancellationToken.None);
        var tail = await db.AuditLogs.AsNoTracking().Where(x => x.TenantId == tenant).Select(x => x.EntryHash).SingleAsync();

        await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.CreateForkGuardIndexSql);
        try
        {
            const string insert = "INSERT INTO audit_logs (id, tenant_id, action, entity_name, previous_hash, entry_hash, hash_algorithm, created_at_utc) " +
                                  "VALUES ({0}, {1}, 'fork', 'Thing', {2}, {3}, 'SHA-256', now() + interval '1 hour')";
            await db.Database.ExecuteSqlRawAsync(insert, Guid.NewGuid(), tenant, tail, "aaaa");
            var second = async () => await db.Database.ExecuteSqlRawAsync(insert, Guid.NewGuid(), tenant, tail, "bbbb");
            (await second.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        }
        finally { await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.DropForkGuardIndexSql); }
    }

    [Fact]
    public async Task ForkPrecheck_WithHistoricalForks_RefusesWithCounts_AndChangesNothing()
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        const string insert = "INSERT INTO audit_logs (id, tenant_id, action, entity_name, previous_hash, entry_hash, hash_algorithm, created_at_utc) " +
                              "VALUES ({0}, {1}, 'legacy', 'Thing', 'samehash', {2}, 'SHA-256', now() - interval '1 day')";
        await db.Database.ExecuteSqlRawAsync(insert, Guid.NewGuid(), tenant, "e1");
        await db.Database.ExecuteSqlRawAsync(insert, Guid.NewGuid(), tenant, "e2"); // a legacy fork
        await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.DropForkGuardIndexSql);
        var precheck = async () => await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.ForkPrecheckSql);
        (await precheck.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("chain fork group");
        (await db.AuditLogs.AsNoTracking().CountAsync(x => x.TenantId == tenant)).Should().Be(2, "no legacy row is deleted or rewritten");
        await DeleteTenantAuditRowsAsync(db, tenant); // the shared database must not keep a fork
    }

    [Fact]
    public async Task ConcurrentIndexBuild_RunsOutsideATransaction_AndAnAcceptedPartialGuardStillBlocksNewForks()
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        const string legacy = "INSERT INTO audit_logs (id, tenant_id, action, entity_name, previous_hash, entry_hash, hash_algorithm, created_at_utc) " +
                              "VALUES ({0}, {1}, 'legacy', 'Thing', 'samehash', {2}, 'SHA-256', now() - interval '1 day')";
        await db.Database.ExecuteSqlRawAsync(legacy, Guid.NewGuid(), tenant, "e1");
        await db.Database.ExecuteSqlRawAsync(legacy, Guid.NewGuid(), tenant, "e2");
        await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.DropForkGuardIndexSql);
        try
        {
            // the operator-accepted path from the runbook: a partial guard that excludes the accepted legacy rows.
            // The database is SHARED by the whole suite, and other tests leave audit rows of their own (some with equal
            // previous_hash values), which would make the build fail with 23505 for reasons unrelated to this test: the
            // predicate is therefore also pinned to this test's tenant, so the guard sees only the rows this test wrote.
            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX CONCURRENTLY ux_audit_logs_tenant_previous_hash ON audit_logs ((coalesce(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)), previous_hash) " +
                $"WHERE action <> 'legacy' AND tenant_id = '{tenant:D}'");
            await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.ForkPrecheckSql); // now passes: the guard exists
            const string fresh = "INSERT INTO audit_logs (id, tenant_id, action, entity_name, previous_hash, entry_hash, hash_algorithm, created_at_utc) " +
                                 "VALUES ({0}, {1}, 'new', 'Thing', 'newtail', {2}, 'SHA-256', now() + interval '1 hour')";
            await db.Database.ExecuteSqlRawAsync(fresh, Guid.NewGuid(), tenant, "n1");
            var dup = async () => await db.Database.ExecuteSqlRawAsync(fresh, Guid.NewGuid(), tenant, "n2");
            (await dup.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.DropForkGuardIndexSql);
            await DeleteTenantAuditRowsAsync(db, tenant);
        }
    }

    [Fact]
    public async Task RealisticLegacyShapes_PassThePrecheck_AndTheGuardIndexBuilds()
    {
        await using var db = _fx.CreateDb();
        var t1 = await PostgresFixture.SeedMinimalTenant(db);
        var t2 = await PostgresFixture.SeedMinimalTenant(db);
        const string insert = "INSERT INTO audit_logs (id, tenant_id, action, entity_name, previous_hash, entry_hash, hash_algorithm, created_at_utc) " +
                              "VALUES ({0}, {1}, 'legacy', 'Thing', {2}, {3}, 'SHA-256', now() - interval '2 day')";
        const string insertPlatform = "INSERT INTO audit_logs (id, tenant_id, action, entity_name, previous_hash, entry_hash, hash_algorithm, created_at_utc) " +
                                      "VALUES ({0}, NULL, 'legacy', 'Thing', {1}, {2}, 'SHA-256', now() - interval '2 day')";
        // unsealed pre-chain rows: empty previous_hash, several per tenant and several on the platform (NULL tenant)
        for (var i = 0; i < 3; i++)
        {
            await db.Database.ExecuteSqlRawAsync(insert, Guid.NewGuid(), t1, "", "");
            await db.Database.ExecuteSqlRawAsync(insert, Guid.NewGuid(), t2, "", "");
            await db.Database.ExecuteSqlRawAsync(insertPlatform, Guid.NewGuid(), "", "");
        }
        // sealed chains: a linked second row per tenant and on the platform chain
        await db.Database.ExecuteSqlRawAsync(insert, Guid.NewGuid(), t1, "h1a", "h1b");
        await db.Database.ExecuteSqlRawAsync(insert, Guid.NewGuid(), t2, "h2a", "h2b");
        await db.Database.ExecuteSqlRawAsync(insertPlatform, Guid.NewGuid(), "hpa", "hpb");
        await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.DropForkGuardIndexSql);
        try
        {
            await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.ForkPrecheckSql);
            await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.CreateForkGuardIndexSql);
            (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname = 'ux_audit_logs_tenant_previous_hash'").SingleAsync())
                .Should().Be(1);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.DropForkGuardIndexSql);
            await DeleteTenantAuditRowsAsync(db, t1);
            await DeleteTenantAuditRowsAsync(db, t2);
            await db.Database.ExecuteSqlRawAsync("DELETE " + "FROM audit_logs WHERE tenant_id IS NULL AND action = 'legacy'");
        }
    }

    private static Task DeleteTenantAuditRowsAsync(ZayraDbContext db, Guid tenant) =>
        db.Database.ExecuteSqlRawAsync("DELETE " + "FROM audit_logs WHERE tenant_id = {0}", tenant);

    // ── 3. append-only triggers on every audit table ─────────────────────────────────────────────

    [Fact]
    public async Task EveryAuditTable_HasAnAppendOnlyTrigger_AfterTheGuardSqlRuns()
    {
        await using var db = _fx.CreateDb();
        await InstallTriggersAsync(db);
        try
        {
            foreach (var t in AuditTrailGuardSql.AuditTables)
            {
                var n = await db.Database
                    .SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM pg_trigger WHERE tgname = 'trg_{t}_append_only' AND NOT tgisinternal")
                    .SingleAsync();
                n.Should().Be(1, $"{t} must be append-only");
            }
        }
        finally { await RemoveTriggersAsync(db); }
    }

    [Fact]
    public async Task AuditLogs_RejectRawUpdateAndDelete()
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        await new AuditService(db).WriteAsync("keep.me", "Thing", "1", Ctx(tenant), null, CancellationToken.None);
        await InstallTriggersAsync(db);
        try
        {
            var upd = async () => await db.Database.ExecuteSqlRawAsync("UPDATE audit_logs SET action = 'tampered' WHERE tenant_id = {0}", tenant);
            (await upd.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23001"); // restrict_violation
            var del = async () => await db.Database.ExecuteSqlRawAsync("DELETE FROM audit_logs WHERE tenant_id = {0}", tenant);
            (await del.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
            (await db.AuditLogs.AsNoTracking().CountAsync(x => x.TenantId == tenant)).Should().Be(1);
        }
        finally { await RemoveTriggersAsync(db); }
    }

    // ── 4. controlled erasure path ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantErasureFlag_AllowsDelete_InThatTransactionOnly_AndNeverUpdate()
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        await new AuditService(db).WriteAsync("erase.me", "Thing", "1", Ctx(tenant), null, CancellationToken.None);
        await InstallTriggersAsync(db);
        try
        {
            // inside a flagged transaction: UPDATE still refused, DELETE allowed
            await InTransactionAsync(db, async () =>
            {
                await db.Database.SqlQuery<string>(AuditTrailGuardSql.ErasureFlagFor(tenant)).ToListAsync();
                var upd = async () => await db.Database.ExecuteSqlRawAsync("UPDATE audit_logs SET action = 'x' WHERE tenant_id = {0}", tenant);
                await upd.Should().ThrowAsync<PostgresException>();
                return false; // the failed statement poisoned the transaction: roll back
            });

            await InTransactionAsync(db, async () =>
            {
                await db.Database.SqlQuery<string>(AuditTrailGuardSql.ErasureFlagFor(tenant)).ToListAsync();
                (await db.Database.ExecuteSqlRawAsync("DELETE FROM audit_logs WHERE tenant_id = {0}", tenant)).Should().Be(1);
                return true;
            });

            // the flag was transaction-local: a later, unflagged delete on the same connection is refused
            await new AuditService(db).WriteAsync("after.erasure", "Thing", "2", Ctx(tenant), null, CancellationToken.None);
            var del = async () => await db.Database.ExecuteSqlRawAsync("DELETE FROM audit_logs WHERE tenant_id = {0}", tenant);
            await del.Should().ThrowAsync<PostgresException>();
        }
        finally { await RemoveTriggersAsync(db); }
    }

    [Fact]
    public async Task PayrollAuditTrigger_AfterGuardSql_AllowsFlaggedDeleteOnly()
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        db.PayrollAuditLogs.Add(new Zayra.Api.Models.PayrollAuditLog { TenantId = tenant, Action = "p", EntityName = "PayrollRun", EntityId = "r", MetadataJson = "{}" });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.PayrollFunctionSql);
        await db.Database.ExecuteSqlRawAsync(@"DROP TRIGGER IF EXISTS trg_payroll_audit_logs_append_only ON payroll_audit_logs;
CREATE TRIGGER trg_payroll_audit_logs_append_only BEFORE UPDATE OR DELETE ON payroll_audit_logs
FOR EACH ROW EXECUTE FUNCTION payroll_audit_logs_append_only();");
        try
        {
            var del = async () => await db.Database.ExecuteSqlRawAsync("DELETE FROM payroll_audit_logs WHERE tenant_id = {0}", tenant);
            await del.Should().ThrowAsync<PostgresException>();

            await InTransactionAsync(db, async () =>
            {
                await db.Database.SqlQuery<string>(AuditTrailGuardSql.ErasureFlagFor(tenant)).ToListAsync();
                (await db.Database.ExecuteSqlRawAsync("DELETE FROM payroll_audit_logs WHERE tenant_id = {0}", tenant)).Should().Be(1);
                return true;
            });
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS trg_payroll_audit_logs_append_only ON payroll_audit_logs;");
            await db.Database.ExecuteSqlRawAsync(AuditTrailGuardSql.PayrollFunctionOriginalSql);
        }
    }

    // ── H3: the erasure flag is scoped to one tenant ─────────────────────────────────────────────

    [Fact]
    public async Task ErasureFlag_ForOneTenant_CannotDeleteAnotherTenantsAuditRows()
    {
        await using var db = _fx.CreateDb();
        var a = await PostgresFixture.SeedMinimalTenant(db);
        var b = await PostgresFixture.SeedMinimalTenant(db);
        await new AuditService(db).WriteAsync("a.row", "Thing", "1", Ctx(a), null, CancellationToken.None);
        await new AuditService(db).WriteAsync("b.row", "Thing", "1", Ctx(b), null, CancellationToken.None);
        await InstallTriggersAsync(db);
        try
        {
            await InTransactionAsync(db, async () =>
            {
                await db.Database.SqlQuery<string>(AuditTrailGuardSql.ErasureFlagFor(a)).ToListAsync();
                (await db.Database.ExecuteSqlRawAsync("DELETE FROM audit_logs WHERE tenant_id = {0}", a)).Should().Be(1);
                var other = async () => await db.Database.ExecuteSqlRawAsync("DELETE FROM audit_logs WHERE tenant_id = {0}", b);
                await other.Should().ThrowAsync<PostgresException>("a flag naming tenant A must not erase tenant B");
                return false;
            });
            (await db.AuditLogs.AsNoTracking().CountAsync(x => x.TenantId == b)).Should().Be(1);
        }
        finally { await RemoveTriggersAsync(db); }
    }

    [Fact]
    public async Task AuditTables_RefuseAWholeTableWipe()
    {
        await using var db = _fx.CreateDb();
        await InstallTriggersAsync(db);
        try
        {
            var wipe = async () => await db.Database.ExecuteSqlRawAsync("TRUNC" + "ATE TABLE audit_logs");
            (await wipe.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
        }
        finally { await RemoveTriggersAsync(db); }
    }

    // ── H2: audit lock last, bounded wait ────────────────────────────────────────────────────────

    [Fact]
    public async Task RowLockThenAuditedSave_VersusAuditedSaveThenUpdateOfThatRow_NeitherDeadlocks()
    {
        await using var seed = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);

        // T1: takes a row lock on the tenant row (FOR UPDATE) first, then performs an audited save.
        // T2: performs an audited save in the same tenant, and updates the same row in the same save.
        // With the audit lock taken LAST (after the business statements), both orders serialise on the row
        // lock first, so no lock-order inversion (40P01) is possible.
        var t1 = Task.Run(async () =>
        {
            for (var i = 0; i < 15; i++)
            {
                await using var db = _fx.CreateDb();
                await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM tenants WHERE id = {tenant} FOR UPDATE").ToListAsync();
                    new AuditService(db).Stage("t1." + i, "Thing", i.ToString(), Ctx(tenant), null);
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                });
            }
        });
        var t2 = Task.Run(async () =>
        {
            for (var i = 0; i < 15; i++)
            {
                await using var db = _fx.CreateDb();
                var row = await db.Tenants.SingleAsync(t => t.Id == tenant);
                row.Name = "n" + i;
                new AuditService(db).Stage("t2." + i, "Thing", i.ToString(), Ctx(tenant), null);
                await db.SaveChangesAsync();
            }
        });
        await Task.WhenAll(t1, t2); // any 40P01 would surface here

        await using var read = _fx.CreateDb();
        var chain = await read.AuditLogs.AsNoTracking().Where(x => x.TenantId == tenant)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync();
        chain.Should().HaveCount(30);
        AuditService.VerifyChain(chain).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task AuditLockHeldByAnotherTransaction_FailsTheWriteFast_InsteadOfQueueing()
    {
        await using var seed = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);
        var key = ZayraDbContext.ComputeCentralAuditChainLockKey(tenant);

        // a separate connection holds the tenant audit lock for longer than the bounded wait
        await using var holder = _fx.CreateDb();
        await holder.Database.OpenConnectionAsync();
        await InTransactionAsync(holder, async () =>
        {
            await holder.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({key}) AS \"Value\"").ToListAsync();

            await using var writer = _fx.CreateDb();
            var started = DateTime.UtcNow;
            var write = async () => await new AuditService(writer).WriteAsync("blocked", "Thing", "1", Ctx(tenant), null, CancellationToken.None);
            var ex = await write.Should().ThrowAsync<AuditSealFailedException>();
            ex.Which.InnerException.Should().BeOfType<TimeoutException>();
            (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30));
            return false;
        });
    }
}
