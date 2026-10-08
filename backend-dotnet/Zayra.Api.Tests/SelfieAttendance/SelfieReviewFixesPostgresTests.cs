using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Retention;
using Zayra.Api.Infrastructure.Retention.Rules;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Review 1 of selfie attendance v2 on real PostgreSQL: tenant erasure (both paths) deletes the selfie files before the
/// rows, the purge scheduler cannot be starved by one tenant's backlog, and the due scan loads its payroll facts in bulk.
/// Written against types the reviewed backend (d43b3943) already had, so it also runs — and fails — there.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SelfieReviewFixesPostgresTests : PlatformTestBase
{
    private readonly PostgresFixture _fx;
    public SelfieReviewFixesPostgresTests(PostgresFixture fx) => _fx = fx;

    // ── Item 4: erasure ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item4_TheRetentionSweepsTenantErasure_DeletesEverySelfieFile_ThenTheRows()
    {
        var storage = new MemoryDocumentStorage();
        var (tenantId, employeeId) = await SeedEmployeeAsync();
        var keys = new List<string>();
        await using (var db = _fx.CreateDb())
        {
            keys.Add(await AddEvidenceAsync(db, storage, tenantId, employeeId, "Active"));
            keys.Add(await AddEvidenceAsync(db, storage, tenantId, employeeId, "Active"));
        }
        await SoftDeleteTenantAsync(tenantId, DateTime.UtcNow.AddDays(-200));

        await SweepAsync(tenantId, storage);

        await using var verify = _fx.CreateDb();
        Assert.NotNull((await verify.Tenants.SingleAsync(t => t.Id == tenantId)).PurgedAtUtc);
        Assert.Equal(0, await verify.AttendanceEvidence.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId));
        Assert.All(keys, k => Assert.False(storage.Objects.ContainsKey(k), $"selfie file {k} was orphaned by the erasure"));
    }

    [Fact]
    public async Task Item4_WhenAFileCannotBeConfirmedDeleted_TheErasureErasesNothing()
    {
        var storage = new MemoryDocumentStorage();
        var (tenantId, employeeId) = await SeedEmployeeAsync();
        string key;
        await using (var db = _fx.CreateDb()) key = await AddEvidenceAsync(db, storage, tenantId, employeeId, "Active");
        await SoftDeleteTenantAsync(tenantId, DateTime.UtcNow.AddDays(-200));
        storage.FailDeletes = true;

        await SweepAsync(tenantId, storage, requireSuccess: false);

        await using var verify = _fx.CreateDb();
        Assert.Null((await verify.Tenants.SingleAsync(t => t.Id == tenantId)).PurgedAtUtc);
        Assert.Equal(1, await verify.AttendanceEvidence.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId));
        Assert.Equal(1, await verify.Employees.IgnoreQueryFilters().CountAsync(e => e.Id == employeeId));
        Assert.True(storage.Objects.ContainsKey(key));
    }

    [Fact]
    public async Task Item4_TheOwnersManualPurge_DeletesEverySelfieFile_AndStopsBeforeErasingWhenItCannot()
    {
        var storage = new MemoryDocumentStorage();
        var (tenantId, employeeId) = await SeedEmployeeAsync();
        string key;
        await using (var db = _fx.CreateDb()) key = await AddEvidenceAsync(db, storage, tenantId, employeeId, "Active");
        await SoftDeleteTenantAsync(tenantId, DateTime.UtcNow.AddDays(-1));

        // Storage down: refused, and nothing is erased.
        storage.FailDeletes = true;
        await using (var db = _fx.CreateDb())
        {
            var blocked = await Owner(db, storage).PurgeTenant(tenantId, "PURGE", default);
            Assert.IsNotType<OkObjectResult>(blocked);
        }
        await using (var verify = _fx.CreateDb())
            Assert.Equal(1, await verify.AttendanceEvidence.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId));
        Assert.True(storage.Objects.ContainsKey(key));

        storage.FailDeletes = false;
        await using (var db = _fx.CreateDb())
            Assert.IsType<OkObjectResult>(await Owner(db, storage).PurgeTenant(tenantId, "PURGE", default));
        Assert.False(storage.Objects.ContainsKey(key), "the manual purge orphaned the selfie file");
        await using var after = _fx.CreateDb();
        Assert.Equal(0, await after.AttendanceEvidence.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId));
    }

    // ── Item 11: one tenant's backlog cannot delay another's purge ──────────────────────────────────────────

    [Fact]
    public async Task Item11_TheScheduler_EnqueuesEveryTenantWithDueRows_HoweverLargeOneTenantsBacklog()
    {
        var (busy, busyEmployee) = await SeedEmployeeAsync();
        var (quiet, quietEmployee) = await SeedEmployeeAsync();
        await using (var db = _fx.CreateDb())
        {
            // 1,200 unused selfies, all older than the quiet tenant's single due one.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO attendance_evidence (id,tenant_id,employee_id,storage_key,sha256,content_type,byte_size,created_at_utc,expires_at_utc,purge_state) "
                + "SELECT gen_random_uuid(), {0}, {1}, 'storage/documents/' || replace({0}::text, '-', '') || '/k' || g, repeat('e', 64), 'image/jpeg', 3, "
                + "now() - interval '400 days' - (g || ' seconds')::interval, now() - interval '400 days', 'Active' FROM generate_series(1, 1200) g",
                busy, busyEmployee);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO attendance_evidence (id,tenant_id,employee_id,storage_key,sha256,content_type,byte_size,created_at_utc,expires_at_utc,purge_state) "
                + "VALUES (gen_random_uuid(), {0}, {1}, 'storage/documents/' || replace({0}::text, '-', '') || '/q', repeat('e', 64), 'image/jpeg', 3, now() - interval '2 days', now() - interval '2 days' + interval '10 minutes', 'Active')",
                quiet, quietEmployee);
        }

        await using var sp = BuildInstance(new MemoryDocumentStorage());
        var now = DateTime.UtcNow;
        var scheduler = new SelfieEvidencePurgeScheduler(sp.GetRequiredService<IServiceScopeFactory>(), new SelfieEvidencePurgeOptions(),
            NullLogger<SelfieEvidencePurgeScheduler>.Instance);
        await scheduler.EnqueueDueAsync(now, default);

        await using var verify = _fx.CreateDb();
        var jobs = await verify.BackgroundJobs.IgnoreQueryFilters()
            .Where(j => j.JobType == SelfieEvidencePurgeJobHandler.JobType && (j.TenantId == busy || j.TenantId == quiet))
            .Select(j => j.TenantId).ToListAsync();
        // Leave nothing for another test's runner to pick up: these two tenants' jobs and rows go.
        await verify.BackgroundJobs.IgnoreQueryFilters().Where(j => j.TenantId == busy || j.TenantId == quiet).ExecuteDeleteAsync();
        await verify.AttendanceEvidence.IgnoreQueryFilters().Where(e => e.TenantId == busy || e.TenantId == quiet).ExecuteDeleteAsync();
        Assert.Contains(busy, jobs);
        Assert.Contains(quiet, jobs);
    }

    [Fact]
    public async Task Item11_TheDueScan_LoadsPayrollFactsInBulk_NotPerRow()
    {
        var storage = new MemoryDocumentStorage();
        var (tenantId, employeeId) = await SeedEmployeeAsync();
        await using (var db = _fx.CreateDb())
        {
            for (var i = 0; i < 30; i++)
            {
                var usedAt = new DateTime(2025, 1 + i % 12, 10, 6, 0, 0, DateTimeKind.Utc).AddMinutes(i);
                var raw = new AttendanceRawEvent { TenantId = tenantId, EmployeeId = employeeId, PunchTimestampUtc = usedAt, PunchDirection = "In" };
                db.AttendanceRawEvents.Add(raw);
                db.AttendanceEvidence.Add(new AttendanceEvidence
                {
                    TenantId = tenantId, EmployeeId = employeeId, StorageKey = $"storage/documents/{tenantId:N}/u{i}", Sha256 = new string('f', 64),
                    ByteSize = 3, CreatedAtUtc = usedAt.AddMinutes(-1), ExpiresAtUtc = usedAt.AddMinutes(9), UsedAtUtc = usedAt,
                    UsedByRawEventId = raw.Id, PurgeState = "Active",
                });
            }
            await db.SaveChangesAsync();
        }

        var counter = new CommandCounter();
        await using var counted = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString).AddInterceptors(counter).Options);
        var due = await new SelfieEvidencePurger(counted, storage).FindDueAsync(tenantId, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), 1000, default);

        Assert.Equal(30, due.Count);
        Assert.InRange(counter.Count, 1, 5); // candidates, timezone, companies, locks — never two per row
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private PlatformController Owner(ZayraDbContext db, IDocumentStorage storage)
    {
        var controller = CreateController(db);
        var services = new ServiceCollection();
        services.AddSingleton(storage);
        controller.ControllerContext.HttpContext.RequestServices = services.BuildServiceProvider();
        return controller;
    }

    private async Task<(Guid TenantId, int EmployeeId)> SeedEmployeeAsync()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"SR-{Guid.NewGuid():N}"[..20], FullName = "Selfie Review",
            Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (tenantId, employee.Id);
    }

    private static async Task<string> AddEvidenceAsync(ZayraDbContext db, MemoryDocumentStorage storage, Guid tenantId, int employeeId, string state)
    {
        var key = (await storage.SaveAsync(tenantId, new Microsoft.AspNetCore.Http.FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "s.jpg")
        {
            Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(), ContentType = "image/jpeg",
        }, default)).StorageUrl;
        var created = DateTime.UtcNow.AddMinutes(-3);
        db.AttendanceEvidence.Add(new AttendanceEvidence
        {
            TenantId = tenantId, EmployeeId = employeeId, StorageKey = key, Sha256 = new string('b', 64),
            ByteSize = 3, CreatedAtUtc = created, ExpiresAtUtc = created.AddMinutes(10), PurgeState = state,
        });
        await db.SaveChangesAsync();
        return key;
    }

    private async Task SoftDeleteTenantAsync(Guid tenantId, DateTime deletedAtUtc)
    {
        await using var db = _fx.CreateDb();
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.IsActive = false;
        tenant.Slug = $"{tenant.Slug}{SoftDeletedTenantRule.DeletedSlugMarker}{tenantId.ToString("N")[..8]}";
        tenant.SoftDeletedAtUtc = deletedAtUtc;
        await db.SaveChangesAsync();
    }

    /// <summary>Runs one data-retention sweep (tenant erasure on) through the real F3 runner.</summary>
    private async Task SweepAsync(Guid tenantId, IDocumentStorage storage, bool requireSuccess = true)
    {
        var now = DateTime.UtcNow;
        await using var sp = BuildInstance(storage);
        Guid jobId;
        await using (var db = _fx.CreateDb())
        {
            var store = new BackgroundJobStore(db, sp.GetRequiredService<BackgroundJobTypeRegistry>());
            jobId = (await store.EnqueueAsync(tenantId, DataRetentionSweepJobHandler.JobType,
                DataRetentionSweepJobHandler.DefaultIdempotencyKey(now), new DataRetentionSweepPayload(now), null, default)).Job.Id;
        }
        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        for (var i = 0; i < 40; i++)
        {
            await using (var db = _fx.CreateDb())
            {
                var job = await db.BackgroundJobs.IgnoreQueryFilters().Where(j => j.Id == jobId).Select(j => new { j.Status, j.LastError }).SingleAsync();
                if (BackgroundJobStatuses.IsTerminal(job.Status))
                {
                    if (requireSuccess) Assert.True(job.Status == BackgroundJobStatuses.Succeeded, job.LastError ?? "no error recorded");
                    return;
                }
            }
            if (!await runner.RunNextAsync("w", default, default, [DataRetentionSweepJobHandler.JobType]))
            {
                if (!requireSuccess && i > 3) return; // a failed item waits for its retry backoff; nothing was erased meanwhile
                await Task.Delay(50);
            }
        }
        if (requireSuccess) throw new TimeoutException($"Retention sweep {jobId} did not finish.");
    }

    private ServiceProvider BuildInstance(IDocumentStorage storage)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _fx.CreateDb());
        services.AddSingleton(storage);
        services.AddSingleton(new DataRetentionOptions { ScheduleEnabled = true, ApplyDeletions = true, AllowTenantErasure = true });
        services.AddSingleton(new BackgroundJobOptions { LeaseDuration = TimeSpan.FromMinutes(2), HeartbeatInterval = TimeSpan.FromSeconds(30) });
        services.AddScoped<IRetentionRule, SoftDeletedTenantRule>();
        services.AddSingleton(DataRetentionSweepJobHandler.Descriptor);
        services.AddScoped<DataRetentionSweepJobHandler>();
        services.AddSingleton(SelfieEvidencePurgeJobHandler.Descriptor);
        services.AddScoped<SelfieEvidencePurgeJobHandler>();
        services.AddSingleton(new SelfieEvidencePurgeOptions());
        services.AddSingleton<BackgroundJobTypeRegistry>();
        services.AddScoped<BackgroundJobStore>();
        services.AddSingleton<BackgroundJobRunner>();
        return services.BuildServiceProvider();
    }
}
