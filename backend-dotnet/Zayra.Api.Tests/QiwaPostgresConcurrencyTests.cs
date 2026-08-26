using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class QiwaPostgresConcurrencyTests
{
    private readonly PostgresFixture _fixture;

    public QiwaPostgresConcurrencyTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TwoWorkers_ClaimSamePendingLog_PostsExactlyOnce_WithStableIdempotencyKey()
    {
        var protection = DataProtectionProvider.Create($"qiwa-race-{Guid.NewGuid():N}");
        Guid tenantId;
        var syncLogId = Guid.NewGuid();
        await using (var seed = _fixture.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
            seed.Employees.Add(new Employee
            {
                Id = Random.Shared.Next(1_000_000, 2_000_000),
                TenantId = tenantId,
                EmployeeCode = $"PG-{Guid.NewGuid():N}"[..20],
                FullName = "Qiwa Race Employee",
                Status = "Active",
                SaudiOrNonSaudi = "Saudi",
                IdType = "NationalId",
                IdNumber = $"{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}",
                Nationality = "Saudi",
                OccupationCode = "2421",
                EstablishmentId = "7000123456",
                WorkLocationId = "WL-1",
                ContractReference = $"C-{Guid.NewGuid():N}"[..20],
            });
            var employeeId = seed.Employees.Local.Single().Id;
            seed.QiwaTenantConnections.Add(new QiwaTenantConnection
            {
                TenantId = tenantId,
                EstablishmentId = "7000123456",
                Environment = "sandbox",
            });
            seed.QiwaApiCredentials.Add(new QiwaApiCredential
            {
                TenantId = tenantId,
                ClientId = "client",
                EncryptedClientSecret = protection
                    .CreateProtector(QiwaIntegrationService.SecretPurpose)
                    .Protect("secret"),
                Environment = "sandbox",
            });
            seed.QiwaSyncLogs.Add(new QiwaSyncLog
            {
                Id = syncLogId,
                TenantId = tenantId,
                EmployeeId = employeeId,
                Direction = "Push",
                Status = QiwaSyncLogStatuses.Pending,
            });
            await seed.SaveChangesAsync();
        }

        var adapter = new BlockingSpyAdapter();
        var factory = new FreshDbScopeFactory(_fixture.ConnectionString);
        var workerA = CreateWorker(factory, adapter, protection);
        var workerB = CreateWorker(factory, adapter, protection);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunAsync(QiwaSyncWorker worker)
        {
            await gate.Task;
            await worker.ProcessOnceAsync(CancellationToken.None);
        }

        var runA = RunAsync(workerA);
        var runB = RunAsync(workerB);
        gate.SetResult();
        await Task.WhenAll(runA, runB);

        Assert.Equal(1, adapter.PushCount);
        Assert.Equal(new[] { syncLogId }, adapter.IdempotencyKeys);
        await using var verify = _fixture.CreateDb();
        var log = await verify.QiwaSyncLogs.IgnoreQueryFilters().SingleAsync(x => x.Id == syncLogId);
        Assert.Equal(QiwaSyncLogStatuses.Success, log.Status);
        Assert.NotNull(log.CompletedAtUtc);
    }

    private static QiwaSyncWorker CreateWorker(
        IServiceScopeFactory factory, IQiwaApiAdapter adapter, IDataProtectionProvider protection) =>
        new(factory, adapter, new QiwaOAuthTokenCache(), protection, NullLogger<QiwaSyncWorker>.Instance);

    private sealed class BlockingSpyAdapter : IQiwaApiAdapter
    {
        private int _pushCount;
        public int PushCount => Volatile.Read(ref _pushCount);
        public ConcurrentBag<Guid> IdempotencyKeys { get; } = [];
        public string AdapterName => "race-spy";

        public Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct) =>
            Task.FromResult<string?>("token");

        public async Task<QiwaApiResult> PushEmployeeAsync(
            string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct)
        {
            Interlocked.Increment(ref _pushCount);
            IdempotencyKeys.Add(idempotencyKey);
            await Task.Delay(150, ct);
            return new QiwaApiResult(true, null, null, "{\"status\":\"synced\"}");
        }

        public Task<QiwaApiResult> GetEmployeeStatusAsync(
            string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct) =>
            Task.FromResult(new QiwaApiResult(true, null, null, "{\"status\":\"active\"}"));
    }

    private sealed class FreshDbScopeFactory(string connectionString) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new DbScope(connectionString);

        private sealed class DbScope : IServiceScope
        {
            private readonly ZayraDbContext _db;
            public DbScope(string connectionString)
            {
                _db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
                    .UseNpgsql(connectionString).Options);
                ServiceProvider = new DbProvider(_db);
            }
            public IServiceProvider ServiceProvider { get; }
            public void Dispose() => _db.Dispose();
        }

        private sealed class DbProvider(ZayraDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(ZayraDbContext) ? db : null;
        }
    }
}
