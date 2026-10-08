using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Review 2 of selfie attendance v2 on real PostgreSQL: the purge's separate queries under a real backlog, the
/// scheduler's consent subquery inside the system-wide bypass, and the withdrawal waiting on the upload's advisory lock.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SelfieReview2PostgresTests
{
    private readonly PostgresFixture _fx;
    public SelfieReview2PostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Item2_ABacklogOf1200UsedSelfiesNotYetDue_DoesNotBlockTheUnusedOrPendingOnes()
    {
        var (tenantId, employeeId) = await SeedEmployeeAsync(consent: true);
        await using (var db = _fx.CreateDb())
        {
            // 1,200 selfies used 100 days ago (no locked payroll month: due only at 120 days), created before anything else.
            var start = DateTime.UtcNow.AddDays(-100);
            for (var i = 0; i < 1200; i++)
            {
                var usedAt = start.AddSeconds(-i);
                var raw = new AttendanceRawEvent { TenantId = tenantId, EmployeeId = employeeId, PunchTimestampUtc = usedAt, PunchDirection = "In", VerificationMethod = "Selfie" };
                db.AttendanceRawEvents.Add(raw);
                db.AttendanceEvidence.Add(new AttendanceEvidence
                {
                    TenantId = tenantId, EmployeeId = employeeId, StorageKey = $"storage/documents/{tenantId:N}/used{i}.jpg", Sha256 = new string('e', 64),
                    ByteSize = 3, CreatedAtUtc = usedAt.AddMinutes(-1), ExpiresAtUtc = usedAt.AddMinutes(9), UsedAtUtc = usedAt, UsedByRawEventId = raw.Id,
                    PurgeState = AttendanceEvidencePurgeStates.Active,
                });
            }
            await db.SaveChangesAsync();
        }
        Guid unused, pending;
        await using (var db = _fx.CreateDb())
        {
            unused = await AddEvidenceAsync(db, tenantId, employeeId, DateTime.UtcNow.AddHours(-25), AttendanceEvidencePurgeStates.Active);
            pending = await AddEvidenceAsync(db, tenantId, employeeId, DateTime.UtcNow.AddHours(-2), AttendanceEvidencePurgeStates.Pending);
        }

        await using var scan = _fx.CreateDb();
        var due = await new SelfieEvidencePurger(scan, new MemoryDocumentStorage()).FindDueAsync(tenantId, DateTime.UtcNow, 1000, default);

        Assert.Contains(unused, due);
        Assert.Contains(pending, due);
        Assert.Equal(2, due.Count);
    }

    [Fact]
    public async Task Item2_TheScheduler_FindsATenantWhoseOnlyDueSelfieIsOneWithoutConsent()
    {
        var (withoutConsent, orphanEmployee) = await SeedEmployeeAsync(consent: false);
        var (withConsent, keptEmployee) = await SeedEmployeeAsync(consent: true);
        await using (var db = _fx.CreateDb())
        {
            await AddEvidenceAsync(db, withoutConsent, orphanEmployee, DateTime.UtcNow.AddMinutes(-5), AttendanceEvidencePurgeStates.Active);
            await AddEvidenceAsync(db, withConsent, keptEmployee, DateTime.UtcNow.AddMinutes(-5), AttendanceEvidencePurgeStates.Active);
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _fx.CreateDb());
        services.AddSingleton(new BackgroundJobOptions { LeaseDuration = TimeSpan.FromMinutes(2), HeartbeatInterval = TimeSpan.FromSeconds(30) });
        services.AddSingleton(SelfieEvidencePurgeJobHandler.Descriptor);
        services.AddSingleton<BackgroundJobTypeRegistry>();
        services.AddScoped<BackgroundJobStore>();
        await using var sp = services.BuildServiceProvider();
        var now = DateTime.UtcNow;

        await new SelfieEvidencePurgeScheduler(sp.GetRequiredService<IServiceScopeFactory>(), new SelfieEvidencePurgeOptions(),
            NullLogger<SelfieEvidencePurgeScheduler>.Instance).EnqueueDueAsync(now, default);

        await using var verify = _fx.CreateDb();
        var key = SelfieEvidencePurgeJobHandler.IdempotencyKey(now);
        Assert.True(await verify.BackgroundJobs.IgnoreQueryFilters().AnyAsync(j => j.TenantId == withoutConsent && j.IdempotencyKey == key));
        Assert.False(await verify.BackgroundJobs.IgnoreQueryFilters().AnyAsync(j => j.TenantId == withConsent && j.IdempotencyKey == key));
    }

    [Fact]
    public async Task Item6_AWithdrawal_WaitsForTheUploadsAdvisoryLock()
    {
        var (tenantId, employeeId) = await SeedEmployeeAsync(consent: true);
        Guid userId;
        await using (var db = _fx.CreateDb())
        {
            var email = $"selfie2-{Guid.NewGuid():N}@lock.test";
            var user = new User
            {
                TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = "Selfie Person", PasswordHash = "no-login",
                Status = "Active", AccessMode = "Mobile", IsActive = true, IsEmailConfirmed = true, IsGroupScope = true,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount
            {
                TenantId = tenantId, EmployeeId = employeeId, UserId = userId, AccessMode = AccessModes.Mobile, Status = "Active", RequiresPasswordSetup = false,
            });
            await db.SaveChangesAsync();
        }

        // An upload holds the lock (between its consent check and its write).
        await using var holder = new NpgsqlConnection(_fx.ConnectionString);
        await holder.OpenAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", holder, tx))
        {
            take.Parameters.AddWithValue("k", SelfieConsentLock.Key(tenantId, employeeId));
            await take.ExecuteNonQueryAsync();
        }

        await using var db2 = _fx.CreateDb();
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim("tenant_id", tenantId.ToString()),
            new System.Security.Claims.Claim("sub", userId.ToString()),
            new System.Security.Claims.Claim("employee_id", employeeId.ToString()),
        ], "Test"));
        var controller = new EssAttendanceVerificationController(db2, new AttendanceVerificationService(db2, SelfieWorld.ResidentKsa), new MemoryDocumentStorage())
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = principal } },
        };
        var withdrawal = controller.WithdrawConsent(null, default);

        await Task.Delay(700);
        Assert.False(withdrawal.IsCompleted, "the withdrawal did not wait for the upload holding the lock");
        await using (var check = _fx.CreateDb())
            Assert.True(await check.BiometricConsents.IgnoreQueryFilters().AnyAsync(c => c.EmployeeId == employeeId && c.WithdrawnAtUtc == null));

        await tx.CommitAsync(); // the upload finishes
        await withdrawal.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var check = _fx.CreateDb())
            Assert.False(await check.BiometricConsents.IgnoreQueryFilters().AnyAsync(c => c.EmployeeId == employeeId && c.WithdrawnAtUtc == null));
    }

    private async Task<(Guid TenantId, int EmployeeId)> SeedEmployeeAsync(bool consent)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"SELFIE2-{Guid.NewGuid():N}"[..20], FullName = "Selfie Person",
            Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        if (consent)
        {
            db.BiometricConsents.Add(new BiometricConsent { TenantId = tenantId, EmployeeId = employee.Id, PolicyVersion = "1", GivenAtUtc = DateTime.UtcNow.AddDays(-200), Channel = "Mobile" });
            await db.SaveChangesAsync();
        }
        return (tenantId, employee.Id);
    }

    private static async Task<Guid> AddEvidenceAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateTime createdAt, string state)
    {
        var row = new AttendanceEvidence
        {
            TenantId = tenantId, EmployeeId = employeeId, StorageKey = $"storage/documents/{tenantId:N}/{Guid.NewGuid():N}.jpg",
            Sha256 = state == AttendanceEvidencePurgeStates.Active ? new string('b', 64) : null,
            ByteSize = state == AttendanceEvidencePurgeStates.Active ? 3 : null,
            CreatedAtUtc = createdAt, ExpiresAtUtc = createdAt.AddMinutes(10), PurgeState = state,
        };
        db.AttendanceEvidence.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }
}

/// <summary>
/// Review 2, item 4, against a real S3 implementation (MinIO in a container): a strict delete on a VERSIONED bucket
/// leaves no version and no delete marker behind, and the bucket's region and versioning are read from storage itself.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SelfieMinioStrictDeleteTests : IAsyncLifetime
{
    private const string AccessKey = "selfie-minio";
    private const string SecretKey = "selfie-minio-secret-key";
    private readonly IContainer _minio = new ContainerBuilder()
        .WithImage("minio/minio:latest")
        .WithCommand("server", "/data")
        .WithEnvironment("MINIO_ROOT_USER", AccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
        .WithPortBinding(9000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/minio/health/live")))
        .Build();

    private string Endpoint => $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";

    public Task InitializeAsync() => _minio.StartAsync();
    public async Task DisposeAsync() => await _minio.DisposeAsync();

    private AmazonS3Client Client() => new(AccessKey, SecretKey, new AmazonS3Config { ServiceURL = Endpoint, ForcePathStyle = true, UseHttp = true });

    private StorageOptions Options(string bucket) => new()
    {
        Provider = "s3", Bucket = bucket, Endpoint = Endpoint, AccessKey = AccessKey, SecretKey = SecretKey, Region = "auto",
    };

    private async Task<string> VersionedBucketAsync(bool versioned)
    {
        var bucket = $"selfie-{Guid.NewGuid():N}"[..30];
        using var s3 = Client();
        await s3.PutBucketAsync(bucket);
        if (versioned)
            await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
            });
        return bucket;
    }

    private async Task<int> VersionsAndMarkersAsync(string bucket, string key)
    {
        using var s3 = Client();
        var page = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        return (page.Versions ?? []).Count(v => v.Key == key);
    }

    [Fact]
    public async Task Item4_StrictDeleteOnAVersionedBucket_LeavesNoVersionAndNoDeleteMarker()
    {
        var bucket = await VersionedBucketAsync(versioned: true);
        var tenant = Guid.NewGuid();
        var opts = Options(bucket);
        var storage = new S3DocumentStorage(S3DocumentStorage.CreatePrimitives(opts), opts, NullLogger<S3DocumentStorage>.Instance);
        var key = storage.TenantKey(tenant, $"attendance-evidence/{Guid.NewGuid():N}.jpg");
        for (var i = 0; i < 3; i++) await storage.PutAtAsync(tenant, key, [0xFF, 0xD8, 0xFF, (byte)i], "image/jpeg");
        Assert.Equal(3, await VersionsAndMarkersAsync(bucket, key));
        // A plain delete would only add a marker on top: four entries, three faces still in storage.

        await storage.DeleteStrictAsync(tenant, key);

        Assert.Equal(0, await VersionsAndMarkersAsync(bucket, key));
        await storage.DeleteStrictAsync(tenant, key); // a re-run on an absent object is success
    }

    [Fact]
    public async Task Item4_TheBucketsOwnRegionAndVersioning_AreReadFromStorage_AndDecideResidency()
    {
        var bucket = await VersionedBucketAsync(versioned: true);
        var opts = Options(bucket);
        var primitives = S3DocumentStorage.CreatePrimitives(opts);

        Assert.Equal("Enabled", await primitives.GetBucketVersioningAsync(bucket, default));
        var region = await primitives.GetBucketRegionAsync(bucket, default);
        Assert.False(string.IsNullOrWhiteSpace(region));

        var host = new Uri(Endpoint).Host;
        opts.ResidencyAllowList = new(StringComparer.OrdinalIgnoreCase) { ["KSA"] = [host] };
        Assert.False((await new StorageResidency(opts, ct => primitives.GetBucketRegionAsync(bucket, ct)).CheckAsync("KSA")).Resident,
            "the endpoint alone is not enough: the bucket's reported region must be listed too");
        opts.ResidencyAllowList = new(StringComparer.OrdinalIgnoreCase) { ["KSA"] = [host, region!] };
        Assert.True((await new StorageResidency(opts, ct => primitives.GetBucketRegionAsync(bucket, ct)).CheckAsync("KSA")).Resident);
    }
}
