using System.Diagnostics;
using System.Data.Common;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Postgres behind PgBouncer in TRANSACTION mode — the shape production runs in (Render talks to
/// Neon's <c>-pooler</c> endpoint). Every other Postgres test talks to the server directly, where one
/// client connection is one server session, so a session-scoped advisory lock looks correct there
/// and is wrong in production. The schema is built over the direct connection; the code under test
/// runs through the pooler.
///
/// <para>PgBouncer's default <c>server_round_robin = 0</c> reuses the most recently released server
/// session first, which makes the interleavings below deterministic.</para>
/// </summary>
public sealed class PgBouncerTransactionPoolFixture : IAsyncLifetime
{
    private readonly INetwork _network = new NetworkBuilder().Build();
    private PostgreSqlContainer? _postgres;
    private IContainer? _pgbouncer;

    public string DirectConnectionString { get; private set; } = string.Empty;
    public string PooledConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Through the pooler, but with no Npgsql client-side pool. An Npgsql connector reused from its
    /// pool prepends <c>DISCARD ALL</c> (which includes <c>pg_advisory_unlock_all</c>) to its first
    /// command, and under transaction pooling that lands on an arbitrary server session. The probes
    /// use fresh connections so what they observe is the lock itself, not that side effect.
    /// </summary>
    public string UnpooledClientConnectionString => PooledConnectionString + ";Pooling=false";

    public async Task InitializeAsync()
    {
        await _network.CreateAsync();
        _postgres = new PostgreSqlBuilder()
            // Pinned by tag, like every other Postgres test here. A bare repo@digest reference pulls
            // fine from a warm local cache but fails on a clean CI runner ("invalid reference
            // format"): Testcontainers 3.10 appends ":latest" to a reference that has no tag.
            .WithImage("postgres:16-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases("pg")
            .Build();
        await _postgres.StartAsync();
        DirectConnectionString = _postgres.GetConnectionString();
        var direct = new NpgsqlConnectionStringBuilder(DirectConnectionString);

        _pgbouncer = new ContainerBuilder()
            // PgBouncer 1.26.0, pinned by its exact version tag (see the note on the Postgres image).
            .WithImage("edoburu/pgbouncer:v1.26.0-p0")
            .WithNetwork(_network)
            .WithEnvironment("DB_HOST", "pg")
            .WithEnvironment("DB_PORT", "5432")
            .WithEnvironment("DB_USER", direct.Username!)
            .WithEnvironment("DB_PASSWORD", direct.Password!)
            .WithEnvironment("AUTH_TYPE", "scram-sha-256")
            .WithEnvironment("POOL_MODE", "transaction")
            .WithEnvironment("DEFAULT_POOL_SIZE", "10")
            .WithEnvironment("MAX_CLIENT_CONN", "200")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
            .Build();
        await _pgbouncer.StartAsync();
        PooledConnectionString = new NpgsqlConnectionStringBuilder(DirectConnectionString)
        {
            Host = _pgbouncer.Hostname,
            Port = _pgbouncer.GetMappedPublicPort(5432),
        }.ConnectionString;

        await using (var db = CreateDirectDb())
            await db.Database.EnsureCreatedAsync();

        // The pooler accepts TCP before it can reach the server; wait until a query goes through.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(PooledConnectionString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync();
                break;
            }
            catch (NpgsqlException) when (attempt < 30)
            {
                await Task.Delay(500);
            }
        }
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_pgbouncer is not null) await _pgbouncer.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
        await _network.DisposeAsync();
    }

    public ZayraDbContext CreateDirectDb() => Create(DirectConnectionString);

    public ZayraDbContext CreatePooledDb(params IInterceptor[] extra) => Create(PooledConnectionString, extra);

    private static ZayraDbContext Create(string connectionString, params IInterceptor[] extra) => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(connectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(new IInterceptor[] { RowLockingInterceptor.Instance, AdvisoryXactLockGuardInterceptor.Instance }.Concat(extra))
            .Options);

    /// <summary>backend_xid / backend_xmin of the session holding the advisory lock on <paramref name="key"/>.</summary>
    public async Task<(string? Xid, string? Xmin)> LockHolderXidAndXminAsync(long key)
    {
        await using var conn = new NpgsqlConnection(DirectConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT a.backend_xid::text, a.backend_xmin::text
            FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
            WHERE l.locktype = 'advisory' AND l.granted AND l.objsubid = 1
              AND l.classid::bigint = @hi AND l.objid::bigint = @lo
            """, conn);
        cmd.Parameters.AddWithValue("hi", (long)((ulong)key >> 32));
        cmd.Parameters.AddWithValue("lo", (long)((ulong)key & 0xFFFF_FFFF));
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("No session holds the lock.");
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>Granted advisory locks on <paramref name="key"/>, counted server-wide over the direct connection.</summary>
    public async Task<int> GrantedAdvisoryLocksAsync(long key)
    {
        await using var conn = new NpgsqlConnection(DirectConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*)::int FROM pg_locks
            WHERE locktype = 'advisory' AND granted AND objsubid = 1
              AND classid::bigint = @hi AND objid::bigint = @lo
            """, conn);
        cmd.Parameters.AddWithValue("hi", (long)((ulong)key >> 32));
        cmd.Parameters.AddWithValue("lo", (long)((ulong)key & 0xFFFF_FFFF));
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}

[Trait("Category", "Integration")]
public sealed partial class AdvisoryLockTransactionPoolingPostgresTests : IClassFixture<PgBouncerTransactionPoolFixture>
{
    private readonly PgBouncerTransactionPoolFixture _fx;
    public AdvisoryLockTransactionPoolingPostgresTests(PgBouncerTransactionPoolFixture fx) => _fx = fx;

    /// <summary>
    /// Proves the fixture really reproduces production's pooler: a session lock taken by one client
    /// is NOT exclusive against the next client, because the next client's transaction is handed the
    /// very server session that holds it. If this ever fails, the fixture has stopped being a
    /// transaction-mode pooler and the tests below no longer prove anything.
    /// </summary>
    [Fact]
    public async Task Fixture_SessionLockThroughTransactionPooler_IsNotExclusive()
    {
        var key = Random.Shared.NextInt64();
        await using (var holder = new NpgsqlConnection(_fx.UnpooledClientConnectionString))
        {
            await holder.OpenAsync();
            await using var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", holder);
            lockCmd.Parameters.AddWithValue("k", key);
            await lockCmd.ExecuteNonQueryAsync();

            (await TryTakeInOtherPooledTransactionAsync(key)).Should().BeTrue(
                "under transaction pooling another client draws the server session that holds the session lock");
        }

        (await _fx.GrantedAdvisoryLocksAsync(key)).Should().Be(1,
            "the session lock outlives its client: it stays on the pooled server session");
    }

    [Theory]
    [InlineData("create-admin")]
    [InlineData("assign-admin")]
    public async Task AdminSeatLock_ThroughTransactionPooler_IsExclusiveWhileHeld_AndNeverLeaks(string operation)
    {
        Guid tenantId;
        Guid existingUserId;
        await using (var seed = _fx.CreateDirectDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            seed.Roles.Add(new Role
            {
                TenantId = tenantId, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin",
                IsActive = true, IsEditable = true
            });
            var user = new User
            {
                TenantId = tenantId,
                Email = $"u-{Guid.NewGuid():N}@example.test",
                NormalizedEmail = string.Empty,
                FullName = "Promoted User",
                PasswordHash = "hash",
                AccessMode = AccessModes.FullPortal,
                Status = "Active",
                IsActive = true,
                IsEmailConfirmed = true
            };
            user.NormalizedEmail = AuthService.Normalize(user.Email);
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            existingUserId = user.Id;
        }

        var key = AccessManagementService.AdminSeatLockKey(tenantId);
        bool? otherClientCouldTakeLock = null;
        NpgsqlConnection? other = null;
        NpgsqlTransaction? otherTx = null;
        var hook = new AfterAdvisoryLockHook(async () =>
        {
            // While the operation believes it holds the admin-seat lock, a different pooled client
            // opens a transaction (drawing the most recently released server session) and asks for it.
            other = new NpgsqlConnection(_fx.UnpooledClientConnectionString);
            await other.OpenAsync();
            otherTx = await other.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@k)", other, otherTx);
            cmd.Parameters.AddWithValue("k", key);
            otherClientCouldTakeLock = (bool)(await cmd.ExecuteScalarAsync())!;
        });

        try
        {
            await using var db = _fx.CreatePooledDb(hook);
            var service = new AccessManagementService(db, new Pbkdf2PasswordHasher(), new NullAuditService(), new FakeTokenService());
            var context = new RequestContext("127.0.0.1", "tests", Guid.NewGuid(), tenantId);
            if (operation == "create-admin")
            {
                var email = $"admin-{Guid.NewGuid():N}@example.test";
                await service.CreateUserAsync(tenantId,
                    new CreateUserRequest(email, "Pooled Admin", "StrongPassword!123", new[] { "Admin" }),
                    context, CancellationToken.None);
            }
            else
            {
                await service.AssignRolesAsync(tenantId, existingUserId,
                    new AssignRolesRequest(new[] { "Admin" }),
                    EntityScopeContext.GroupLevel, context, CancellationToken.None);
            }
        }
        finally
        {
            if (otherTx is not null) await otherTx.RollbackAsync();
            if (other is not null) await other.DisposeAsync();
        }

        otherClientCouldTakeLock.Should().NotBeNull("the operation must take the admin-seat lock");
        otherClientCouldTakeLock.Should().BeFalse(
            "while one request holds a tenant's admin-seat lock no other request may take it");
        (await _fx.GrantedAdvisoryLocksAsync(key)).Should().Be(0,
            "the lock must be released with the operation, not left on a pooled server session");
        (await TryTakeInOtherPooledTransactionAsync(key)).Should().BeTrue(
            "after the operation the next request must be able to take the lock");
    }

    [Fact]
    public async Task MigrationImportLease_ThroughTransactionPooler_IsExclusiveWhileHeld_AndReleasedOnDispose()
    {
        var key = MigrationImportController.MigrationImportLockKey(Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        await using (var db = _fx.CreatePooledDb())
        {
            var lease = (await TransactionHeldAdvisoryLease.TryAcquireAsync(db, key, CancellationToken.None))!;
            await using (lease)
            {
                lease.IsHeld.Should().BeTrue();
                (await _fx.GrantedAdvisoryLocksAsync(key)).Should().Be(1);

                // The import does its own work through the DbContext in many short transactions
                // while the lease is held; none of that may let another client in.
                for (var i = 0; i < 5; i++)
                {
                    await db.Tenants.AsNoTracking().CountAsync();
                    (await TryTakeInOtherPooledTransactionAsync(key)).Should().BeFalse(
                        $"round {i}: a second import of the same package must not get the lock");
                    await lease.EnsureHeldAsync(CancellationToken.None);
                }

                // A second lease for the same key is refused immediately (non-blocking), not queued.
                await using (var db2 = _fx.CreatePooledDb())
                    (await TransactionHeldAdvisoryLease.TryAcquireAsync(db2, key, CancellationToken.None)).Should().BeNull();
            }
        }

        (await _fx.GrantedAdvisoryLocksAsync(key)).Should().Be(0, "a disposed lease leaves nothing behind");
        await using (var db3 = _fx.CreatePooledDb())
        await using (var again = await TransactionHeldAdvisoryLease.TryAcquireAsync(db3, key, CancellationToken.None))
            again.Should().NotBeNull("once released, the next import takes the lock");
    }

    [Fact]
    public async Task MigrationImportLease_KeepaliveOutlivesAShortIdleCeiling_AndHoldsNoXminHorizon()
    {
        // The property under test is "outlives the ceiling", so the wait only needs to pass it.
        // The ceiling is wide against the ping interval (25x) because the pings ride the shared
        // thread pool: on a loaded runner a 2 s ceiling with 300 ms pings left only 1.7 s of slack
        // and failed on a harness stall, not on the lease. Production runs 30 min / 30 s.
        var ceiling = TimeSpan.FromSeconds(5);
        var key = Random.Shared.NextInt64();
        // Monotonic stamps of each renewal; the diagnosis below reads them, the wall clock can jump.
        var pings = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var start = Stopwatch.GetTimestamp();
        await using var db = _fx.CreatePooledDb();
        var lease = (await TransactionHeldAdvisoryLease.TryAcquireAsync(
            db, key,
            new TransactionHeldAdvisoryLease.LeaseOptions(ceiling, TimeSpan.FromMilliseconds(200))
            {
                BeforeKeepalive = () => { pings.Enqueue(Stopwatch.GetTimestamp()); return Task.CompletedTask; },
            },
            CancellationToken.None))!;
        await using (lease)
        {
            // A "section" longer than the idle ceiling, doing no work on the lease connection.
            await Task.Delay(ceiling + TimeSpan.FromSeconds(2));
            var stamps = pings.ToArray();
            var because = $"pings: {stamps.Length}, lease lost: {lease.IsLost}, largest gap between renewals: "
                + $"{LargestGapMs(start, stamps):N0} ms against a {ceiling.TotalMilliseconds:N0} ms ceiling. "
                + "No pings or a lost lease means the keepalive is broken; one gap near the ceiling with "
                + "steady pings either side means the test harness stalled";

            // The renewals themselves, not only the end state: the lease must still be renewing
            // after the first ceiling window has passed, and no gap may reach the ceiling.
            stamps.Should().Contain(t => Stopwatch.GetElapsedTime(start, t) > ceiling, because);
            LargestGapMs(start, stamps).Should().BeLessThan(ceiling.TotalMilliseconds, because);
            (await _fx.GrantedAdvisoryLocksAsync(key)).Should().Be(1, because);
            await lease.EnsureHeldAsync(CancellationToken.None);

            var (xid, xmin) = await _fx.LockHolderXidAndXminAsync(key);
            xid.Should().BeNull("the lease never writes, so it is never assigned a transaction id");
            xmin.Should().BeNull("between keepalives a READ COMMITTED lease holds no snapshot, so vacuum is not held back");
        }
        (await _fx.GrantedAdvisoryLocksAsync(key)).Should().Be(0);
    }

    [Fact]
    public async Task MigrationImportLease_WithoutKeepalive_TheIdleCeilingEndsIt_AndReleasesTheLock()
    {
        var key = Random.Shared.NextInt64();
        await using var db = _fx.CreatePooledDb();
        var lease = (await TransactionHeldAdvisoryLease.TryAcquireAsync(
            db, key, new TransactionHeldAdvisoryLease.LeaseOptions(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan), CancellationToken.None))!;
        await using (lease)
        {
            for (var i = 0; i < 50 && await _fx.GrantedAdvisoryLocksAsync(key) > 0; i++) await Task.Delay(100);
            (await _fx.GrantedAdvisoryLocksAsync(key)).Should().Be(0, "the SET LOCAL ceiling is enforced: an idle lease is ended, never leaked");
            var lost = async () => await lease.EnsureHeldAsync(CancellationToken.None);
            await lost.Should().ThrowAsync<InvalidOperationException>();
            lease.IsLost.Should().BeTrue();
        }
    }

    [Fact]
    public async Task MigrationImportLease_WhenTheKeepaliveFaultsUnexpectedly_DisposeStillReleasesTheLock()
    {
        var key = Random.Shared.NextInt64();
        await using var db = _fx.CreatePooledDb();
        var options = new TransactionHeldAdvisoryLease.LeaseOptions(TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(100))
        {
            // An exception type PingAsync does not classify.
            BeforeKeepalive = () => throw new NotSupportedException("injected keepalive fault"),
        };
        var lease = (await TransactionHeldAdvisoryLease.TryAcquireAsync(db, key, options, CancellationToken.None))!;
        for (var i = 0; i < 50 && !lease.IsLost; i++) await Task.Delay(50);
        lease.IsLost.Should().BeTrue("a faulted keepalive means the lease can no longer be trusted");
        (await _fx.GrantedAdvisoryLocksAsync(key)).Should().Be(1);

        var dispose = async () => await lease.DisposeAsync();
        await dispose.Should().NotThrowAsync();
        (await _fx.GrantedAdvisoryLocksAsync(key)).Should().Be(0, "dispose must roll back even after the keepalive faulted");
    }

    [Fact]
    public async Task MigrationImportLease_PastItsMaxAge_IsEndedAndReleased_SoTheBatchCanBeResumed()
    {
        var key = Random.Shared.NextInt64();
        await using var db = _fx.CreatePooledDb();
        var options = new TransactionHeldAdvisoryLease.LeaseOptions(TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(100))
        {
            MaxAge = TimeSpan.FromSeconds(1),
        };
        var lease = (await TransactionHeldAdvisoryLease.TryAcquireAsync(db, key, options, CancellationToken.None))!;
        await using (lease)
        {
            await lease.EnsureHeldAsync(CancellationToken.None);
            for (var i = 0; i < 60 && await _fx.GrantedAdvisoryLocksAsync(key) > 0; i++) await Task.Delay(100);
            (await _fx.GrantedAdvisoryLocksAsync(key)).Should().Be(0, "a lease past its absolute age is not renewed and lets go");
            var expired = async () => await lease.EnsureHeldAsync(CancellationToken.None);
            await expired.Should().ThrowAsync<InvalidOperationException>("the import must stop and leave the batch for resume");
            (await TryTakeInOtherPooledTransactionAsync(key)).Should().BeTrue("a resume can now take the package lock");
        }
    }

    [Fact]
    public async Task MigrationImportLease_RefusesACallerThatAlreadyHasATransactionOpen()
    {
        await using var db = _fx.CreatePooledDb();
        var strategy = db.Database.CreateExecutionStrategy();
        var act = async () => await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await TransactionHeldAdvisoryLease.TryAcquireAsync(db, 42, CancellationToken.None);
        });
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("before opening a transaction");
    }

    [Fact]
    public async Task XactLockGuard_RefusesALockOutsideATransaction_AndAllowsOneInside()
    {
        await using var db = _fx.CreatePooledDb();
        var outside = async () => await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({42L})");
        (await outside.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("outside a transaction");

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({42L})");
            await tx.CommitAsync();
        });
        (await _fx.GrantedAdvisoryLocksAsync(42)).Should().Be(0);
    }

    [Fact]
    public async Task MigrationImportCommit_ThroughTransactionPooler_HoldsLeaseForTheImport_AndReleasesIt()
    {
        var tenantId = await SeedTenantAsync();
        var request = RolesPackage($"pooler-{Guid.NewGuid():N}");
        var key = MigrationImportController.MigrationImportLockKey(tenantId, MigrationImportController.PackageChecksum(request));

        bool? otherClientCouldTakeLock = null;
        var probe = new FirstSaveProbe(async () => otherClientCouldTakeLock = await TryTakeInOtherPooledTransactionAsync(key));
        await using var db = _fx.CreatePooledDb(probe);
        var result = await CreateController(db, tenantId).Commit(request, CancellationToken.None);

        OkDto(result).Status.Should().Be("Completed");
        otherClientCouldTakeLock.Should().BeFalse("a second import of the same package must not run while the first does");
        (await _fx.GrantedAdvisoryLocksAsync(key)).Should().Be(0, "the import's lease is released when the request ends");
    }

    [Fact]
    public async Task MigrationImportCommit_WhenThePackageIsAlreadyBeingImported_Returns409_WithoutWaiting()
    {
        var tenantId = await SeedTenantAsync();
        var request = RolesPackage(null);
        var key = MigrationImportController.MigrationImportLockKey(tenantId, MigrationImportController.PackageChecksum(request));
        await using var holderDb = _fx.CreatePooledDb();
        await using var held = await TransactionHeldAdvisoryLease.TryAcquireAsync(holderDb, key, CancellationToken.None);
        held.Should().NotBeNull();

        await using var db = _fx.CreatePooledDb();
        var result = await CreateController(db, tenantId).Commit(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(conflict.Value).Should().Contain(MigrationImportController.AlreadyProcessingMessage);
        await using var verify = _fx.CreateDirectDb();
        (await verify.MigrationImportBatches.CountAsync(x => x.TenantId == tenantId)).Should().Be(0, "the refused caller wrote nothing");
    }

    [Fact]
    public async Task MigrationImportResume_OfACommitWithoutExternalBatchId_ContendsForTheSameLock_AndNeverDuplicatesTheBatch()
    {
        var tenantId = await SeedTenantAsync();
        var request = RolesPackage(null);

        // While the commit is running (after it has written its Processing batch), a resume of
        // that batch arrives on another connection.
        ActionResult<MigrationReconciliationDto>? concurrentResume = null;
        var probe = new FirstSaveProbe(async () =>
        {
            await using var lookup = _fx.CreateDirectDb();
            var batchId = await lookup.MigrationImportBatches.Where(x => x.TenantId == tenantId).Select(x => x.Id).SingleAsync();
            await using var resumeDb = _fx.CreatePooledDb();
            concurrentResume = await CreateController(resumeDb, tenantId).Resume(batchId, request, CancellationToken.None);
        });
        await using (var db = _fx.CreatePooledDb(probe))
            OkDto(await CreateController(db, tenantId).Commit(request, CancellationToken.None)).Status.Should().Be("Completed");

        concurrentResume.Should().NotBeNull();
        concurrentResume!.Result.Should().BeOfType<ConflictObjectResult>("the resume must not run concurrently with the commit");
        await using var verify = _fx.CreateDirectDb();
        (await verify.MigrationImportBatches.CountAsync(x => x.TenantId == tenantId)).Should().Be(1);
    }

    [Fact]
    public async Task MigrationImportResume_OfAnInterruptedBatchWithoutExternalBatchId_ContinuesThatBatch()
    {
        var tenantId = await SeedTenantAsync();
        var request = RolesPackage(null);
        Guid batchId;
        await using (var seed = _fx.CreateDirectDb())
        {
            // What a crash mid-import leaves behind: a Processing batch with no ExternalBatchId.
            var crashed = new MigrationImportBatch
            {
                TenantId = tenantId,
                ExternalBatchId = null,
                PackageChecksum = MigrationImportController.PackageChecksum(request),
                PackageType = "MigrationPackage",
                Status = "Processing",
                PayloadJson = "{}",
                CreatedBy = Guid.NewGuid(),
            };
            seed.MigrationImportBatches.Add(crashed);
            await seed.SaveChangesAsync();
            batchId = crashed.Id;
        }

        await using (var db = _fx.CreatePooledDb())
        {
            var dto = OkDto(await CreateController(db, tenantId).Resume(batchId, request, CancellationToken.None));
            dto.Status.Should().Be("Completed");
            dto.BatchId.Should().Be(batchId, "resume continues the original batch");
        }

        await using var verify = _fx.CreateDirectDb();
        (await verify.MigrationImportBatches.CountAsync(x => x.TenantId == tenantId)).Should().Be(1,
            "resuming must not create a second batch for the same package");
    }

    [Fact]
    public async Task MigrationImportLease_WhenLeaseBackendIsKilled_ReleasesLock_AndEnsureHeldFailsLoudly()
    {
        var key = Random.Shared.NextInt64();
        await using var db = _fx.CreatePooledDb();
        var lease = (await TransactionHeldAdvisoryLease.TryAcquireAsync(db, key, CancellationToken.None))!;
        try
        {
            await using (var admin = new NpgsqlConnection(_fx.DirectConnectionString))
            {
                await admin.OpenAsync();
                await using var kill = new NpgsqlCommand(
                    """
                    SELECT pg_terminate_backend(pid) FROM pg_locks
                    WHERE locktype = 'advisory' AND granted AND objsubid = 1
                      AND classid::bigint = @hi AND objid::bigint = @lo
                    """, admin);
                kill.Parameters.AddWithValue("hi", (long)((ulong)key >> 32));
                kill.Parameters.AddWithValue("lo", (long)((ulong)key & 0xFFFF_FFFF));
                await kill.ExecuteNonQueryAsync();
            }

            for (var i = 0; i < 50 && await _fx.GrantedAdvisoryLocksAsync(key) > 0; i++) await Task.Delay(100);
            (await _fx.GrantedAdvisoryLocksAsync(key)).Should().Be(0, "a dead lease releases its lock rather than leaking it");

            var lost = async () => await lease.EnsureHeldAsync(CancellationToken.None);
            await lost.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    private static double LargestGapMs(long start, IEnumerable<long> pings)
    {
        var largest = 0.0;
        var previous = start;
        foreach (var ping in pings.Append(Stopwatch.GetTimestamp()))
        {
            largest = Math.Max(largest, Stopwatch.GetElapsedTime(previous, ping).TotalMilliseconds);
            previous = ping;
        }
        return largest;
    }

    private async Task<Guid> SeedTenantAsync()
    {
        await using var seed = _fx.CreateDirectDb();
        return await PostgresFixture.SeedMinimalTenant(seed);
    }

    private static MigrationPackageRequest RolesPackage(string? externalBatchId) => new(externalBatchId, new Dictionary<string, string>
    {
        ["roles"] = $"Name,Description,AuthorityLevel,IsActive\nImported HR {Guid.NewGuid():N},Imported role,50,true\n",
    }, false);

    private static MigrationReconciliationDto OkDto(ActionResult<MigrationReconciliationDto> result) =>
        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<MigrationReconciliationDto>().Which;

    private static MigrationImportController CreateController(ZayraDbContext db, Guid tenantId) =>
        new(db, new Pbkdf2PasswordHasher(), new Zayra.Api.Infrastructure.Audit.AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
                    {
                        new System.Security.Claims.Claim("tenant_id", tenantId.ToString()),
                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin"),
                    }, "test"))
                }
            }
        };

    private async Task<bool> TryTakeInOtherPooledTransactionAsync(long key)
    {
        await using var conn = new NpgsqlConnection(_fx.UnpooledClientConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@k)", conn, tx);
        cmd.Parameters.AddWithValue("k", key);
        var taken = (bool)(await cmd.ExecuteScalarAsync())!;
        await tx.RollbackAsync();
        return taken;
    }

    /// <summary>Runs <paramref name="onLocked"/> once, right after the first advisory-lock statement returns.</summary>
    private sealed partial class AfterAdvisoryLockHook(Func<Task> onLocked) : DbCommandInterceptor
    {
        private int _fired;

        [GeneratedRegex(@"pg_advisory_(xact_)?lock\(")]
        private static partial Regex LockStatement();

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (LockStatement().IsMatch(command.CommandText) && Interlocked.Exchange(ref _fired, 1) == 0)
                await onLocked();
            return result;
        }
    }

    /// <summary>Runs <paramref name="onSaved"/> once, after the first SaveChanges completes.</summary>
    private sealed class FirstSaveProbe(Func<Task> onSaved) : SaveChangesInterceptor
    {
        private int _fired;

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0) await onSaved();
            return result;
        }
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string CreateAccessToken(User user, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions, Tenant tenant, IReadOnlyCollection<EntityAccessGrant> entityAccess, EntityScopeDescriptor entityScope, out DateTime expiresAtUtc)
        {
            expiresAtUtc = DateTime.UtcNow.AddHours(1);
            return $"fake-access-{user.Id}";
        }

        public string CreateSecureToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        public string HashToken(string token) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    private sealed class NullAuditService : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
