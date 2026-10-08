using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// The owner's selfie DEMO EXCEPTION (2026-10-08) on real PostgreSQL, through the real callers: the ESS discovery and
/// consent endpoints, the upload, <c>punch/mobile</c> and the purge job on the F3 queue. The exception is configured the
/// way the deploy does it (the <c>SelfieDemoException</c> configuration section handed to
/// <see cref="AttendanceVerificationService"/>), and nothing here names a type the exception added, so this file compiles
/// against the code before it — where every test below fails.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SelfieDemoExceptionPostgresTests
{
    private const string Approval = "owner 2026-10-08 (demo, test data only)";
    private const string DemoActiveAction = "attendance.selfie.demo_exception_active";
    private readonly PostgresFixture _fx;
    public SelfieDemoExceptionPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ListedSlug_BeforeExpiry_ConsentUploadAndPunch_WorkWithoutAnySignOff_AndAreAudited()
    {
        var (tenantId, slug, employeeId) = await SeedAsync();
        var config = Demo(slug, DateTime.UtcNow.AddDays(14));

        // Discovery: on, consent needed, and the demo notice in English and Arabic.
        var view = await ViewAsync(config, tenantId, employeeId);
        var selfie = view.GetProperty("selfie");
        Assert.True(selfie.GetProperty("enabled").GetBoolean());
        Assert.Equal("consent_needed", selfie.GetProperty("step").GetString());
        Assert.False(selfie.GetProperty("requiredForConsented").GetBoolean());
        var notice = selfie.GetProperty("demoNotice");
        Assert.Equal("Demo: photos are stored outside Saudi Arabia and deleted automatically 7 days after they are taken.",
            notice.GetProperty("message").GetString());
        Assert.Contains("السعودية", notice.GetProperty("messageAr").GetString());
        Assert.False(view.GetProperty("geofence").GetProperty("enforced").GetBoolean()); // geofence untouched

        await ConsentAsync(config, tenantId, employeeId);
        var evidenceId = await UploadAsync(config, tenantId, employeeId);
        var raw = await PunchAsync(config, tenantId, employeeId, evidenceId);
        Assert.Equal(AttendanceVerificationMethods.Selfie, raw.VerificationMethod);

        await using var verify = _fx.CreateDb();
        // No flag row and no sign-off anywhere: the exception alone switched it on.
        Assert.False(await verify.TenantFeatureFlags.IgnoreQueryFilters().AnyAsync(f => f.TenantId == tenantId && f.FeatureKey == FeatureKeys.SelfieAttendance));
        var used = await verify.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.Id == evidenceId);
        Assert.Equal(raw.Id, used.UsedByRawEventId);
        // One "active" row however many policy reads this process made (view, consent, upload, punch).
        var announced = await verify.AttendanceAuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == tenantId && a.Action == DemoActiveAction).ToListAsync();
        var active = Assert.Single(announced);
        Assert.Contains(slug, active.MetadataJson);
        Assert.Contains(Approval, active.MetadataJson);
        Assert.Contains("2026-", active.MetadataJson);
        var uploaded = await verify.AttendanceAuditLogs.IgnoreQueryFilters().SingleAsync(a => a.TenantId == tenantId && a.Action == "attendance.selfie.uploaded");
        Assert.Contains("\"underDemoException\":true", uploaded.MetadataJson);
        Assert.Contains(Approval, uploaded.MetadataJson);
    }

    [Fact]
    public async Task AfterExpiry_OrForAnUnlistedTenant_SelfiesAreRefusedAsBefore_EvenWithEvidenceTakenDuringTheDemo()
    {
        var (tenantId, slug, employeeId) = await SeedAsync();
        var during = Demo(slug, DateTime.UtcNow.AddDays(14));
        await ConsentAsync(during, tenantId, employeeId);
        var evidenceId = await UploadAsync(during, tenantId, employeeId);

        // The same deploy after the expiry: off again, no notice, no new upload, and the id taken during the demo is refused.
        var after = Demo(slug, DateTime.UtcNow.AddMinutes(-1));
        var view = (await ViewAsync(after, tenantId, employeeId)).GetProperty("selfie");
        Assert.False(view.GetProperty("enabled").GetBoolean());
        Assert.Equal("off", view.GetProperty("step").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("demoNotice").ValueKind);
        var upload = await UploadResultAsync(after, tenantId, employeeId);
        Assert.Equal(403, upload.StatusCode);
        Assert.Equal("selfie_not_enabled", SelfieWorld.CodeOf(upload));
        var punch = await PunchResultAsync(after, tenantId, employeeId, evidenceId);
        Assert.Equal("selfie_not_enabled", SelfieWorld.CodeOf(punch));

        // A tenant the exception does not list, while it is active for another: exactly as before.
        var (otherTenant, _, otherEmployee) = await SeedAsync();
        var other = Demo(slug, DateTime.UtcNow.AddDays(14));
        var otherView = (await ViewAsync(other, otherTenant, otherEmployee)).GetProperty("selfie");
        Assert.False(otherView.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, otherView.GetProperty("demoNotice").ValueKind);
        await using (var db = _fx.CreateDb())
        {
            var consent = await Ess(db, other, otherTenant, otherEmployee).GiveConsent(new GiveBiometricConsentRequest("1", "Mobile"), default);
            Assert.Equal("selfie_not_enabled", SelfieWorld.CodeOf(consent));
        }
        var otherUpload = await UploadResultAsync(other, otherTenant, otherEmployee);
        Assert.Equal("selfie_not_enabled", SelfieWorld.CodeOf(otherUpload));

        // An expiry missing altogether means off, even for a listed slug.
        var noExpiry = Demo(slug, null);
        Assert.False((await ViewAsync(noExpiry, tenantId, employeeId)).GetProperty("selfie").GetProperty("enabled").GetBoolean());

        await using var verify = _fx.CreateDb();
        Assert.Equal(1, await verify.AttendanceEvidence.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId));
        Assert.Equal(0, await verify.AttendanceEvidence.IgnoreQueryFilters().CountAsync(e => e.TenantId == otherTenant));
        Assert.Equal(0, await verify.AttendanceAuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == otherTenant && a.Action == DemoActiveAction));
    }

    [Fact]
    public async Task Purge_DeletesADemoSelfie_SevenDaysAfterCapture_EvenThoughAPunchUsedIt_AndAfterTheExceptionExpired()
    {
        var (tenantId, slug, employeeId) = await SeedAsync();
        // The exception ends in an hour; the selfie taken now must still go on day 7.
        var config = Demo(slug, DateTime.UtcNow.AddHours(1));
        await ConsentAsync(config, tenantId, employeeId);
        var evidenceId = await UploadAsync(config, tenantId, employeeId);
        await PunchAsync(config, tenantId, employeeId, evidenceId);
        DateTime capturedAt;
        string key;
        await using (var db = _fx.CreateDb())
        {
            var row = await db.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.Id == evidenceId);
            Assert.NotNull(row.UsedAtUtc);
            capturedAt = row.CreatedAtUtc;
            key = row.StorageKey;
        }
        Assert.True(_storage.Objects.ContainsKey(key));

        await using var sp = BuildInstance();
        // Day 6, 23 hours: a used selfie would normally wait for payroll lock + 90 days or 120 days. Not yet due.
        Assert.Equal("Succeeded", await RunJobAsync(sp, tenantId, capturedAt.AddDays(7).AddHours(-1)));
        await using (var db = _fx.CreateDb())
            Assert.Equal(AttendanceEvidencePurgeStates.Active, (await db.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.Id == evidenceId)).PurgeState);

        // The scheduler's tenant scan (its predicate, scoped here to this tenant so no other test's tenant is enqueued)
        // finds it once due, and not before — the exception long expired by then.
        var dueAt = capturedAt.AddDays(7).AddMinutes(1);
        await using (var db = _fx.CreateDb())
        {
            var mine = db.AttendanceEvidence.IgnoreQueryFilters().Where(e => e.TenantId == tenantId);
            Assert.False(await mine.Where(SelfieEvidenceRetention.MaybeDue(capturedAt.AddDays(7).AddHours(-1), db.BiometricConsents.IgnoreQueryFilters())).AnyAsync());
            Assert.True(await mine.Where(SelfieEvidenceRetention.MaybeDue(dueAt, db.BiometricConsents.IgnoreQueryFilters())).AnyAsync());
        }

        Assert.Equal("Succeeded", await RunJobAsync(sp, tenantId, dueAt));
        await using var verify = _fx.CreateDb();
        var purged = await verify.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.Id == evidenceId);
        Assert.Equal(AttendanceEvidencePurgeStates.Purged, purged.PurgeState);
        Assert.NotNull(purged.Sha256); // the row and its hash are kept
        Assert.False(_storage.Objects.ContainsKey(key));
        var audit = await verify.RetentionPurgeAudits.IgnoreQueryFilters().SingleAsync(a => a.EntityId == evidenceId.ToString());
        Assert.Contains("demo exception", audit.Reason);
        Assert.Equal(SelfieEvidenceRetention.RuleKey, audit.RuleKey);
    }

    [Fact]
    public async Task ADeliberatePlatformOff_WinsOverTheException_SoConsentIsNeverCollectedForUploadsThatWouldBeRefused()
    {
        var (tenantId, slug, employeeId) = await SeedAsync();
        await using (var db = _fx.CreateDb())
        {
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.SelfieAttendance, IsEnabled = false });
            await db.SaveChangesAsync();
        }
        var config = Demo(slug, DateTime.UtcNow.AddDays(14));

        var selfie = (await ViewAsync(config, tenantId, employeeId)).GetProperty("selfie");
        Assert.False(selfie.GetProperty("enabled").GetBoolean());
        Assert.False(selfie.TryGetProperty("demoNotice", out var notice) && notice.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task NoOtherTenantChanges_ASignedOffTenant_KeepsItsOwnPolicyAndNormalRetention_EvenIfListed()
    {
        // A fully signed-off tenant on KSA storage that requires the selfie from consenting employees. The exception is
        // active, and even LISTS this tenant: its own policy wins, no demo notice, and its selfies keep the normal rule.
        var (tenantId, slug, employeeId) = await SeedAsync();
        await using (var db = _fx.CreateDb())
        {
            db.TenantFeatureFlags.Add(new TenantFeatureFlag
            {
                TenantId = tenantId, FeatureKey = FeatureKeys.SelfieAttendance, IsEnabled = true,
                ConfigJson = SelfieWorld.SignedOffConfig(requireSelfieForConsented: true),
            });
            await db.SaveChangesAsync();
        }
        var config = Demo(slug, DateTime.UtcNow.AddDays(14));
        await ConsentAsync(config, tenantId, employeeId, SelfieWorld.ResidentKsa);

        var view = (await ViewAsync(config, tenantId, employeeId, SelfieWorld.ResidentKsa)).GetProperty("selfie");
        Assert.Equal("required", view.GetProperty("step").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("demoNotice").ValueKind);

        var evidenceId = await UploadAsync(config, tenantId, employeeId, SelfieWorld.ResidentKsa);
        await PunchAsync(config, tenantId, employeeId, evidenceId, SelfieWorld.ResidentKsa);

        await using var sp = BuildInstance();
        Assert.Equal("Succeeded", await RunJobAsync(sp, tenantId, DateTime.UtcNow.AddDays(8)));
        await using var verify = _fx.CreateDb();
        Assert.Equal(AttendanceEvidencePurgeStates.Active, (await verify.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.Id == evidenceId)).PurgeState);
        Assert.Equal(0, await verify.AttendanceAuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenantId && a.Action == DemoActiveAction));
        var uploaded = await verify.AttendanceAuditLogs.IgnoreQueryFilters().SingleAsync(a => a.TenantId == tenantId && a.Action == "attendance.selfie.uploaded");
        Assert.DoesNotContain("\"underDemoException\":true", uploaded.MetadataJson);
    }

    // ── the world ──────────────────────────────────────────────────────────────────

    private readonly MemoryDocumentStorage _storage = new();

    /// <summary>The <c>SelfieDemoException</c> section exactly as render.yaml sets it (env vars <c>SelfieDemoException__*</c>).</summary>
    private static IConfiguration Demo(string slug, DateTime? expiresUtc) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SelfieDemoException:TenantSlugs:0"] = slug,
            ["SelfieDemoException:ExpiresUtc"] = expiresUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["SelfieDemoException:EvidenceRetentionDays"] = "7",
            ["SelfieDemoException:ApprovedBy"] = Approval,
        })
        .Build();

    private static AttendanceVerificationService Verification(ZayraDbContext db, IConfiguration config, StorageResidency? residency) =>
        new(db, residency, config);

    private static ControllerContext Context(Guid tenantId, int employeeId) => AttendanceDailyRecordRacePostgresTests.Context(tenantId, employeeId);

    private static EssAttendanceVerificationController Ess(ZayraDbContext db, IConfiguration config, Guid tenantId, int employeeId,
        StorageResidency? residency = null, IDocumentStorage? storage = null) =>
        new(db, Verification(db, config, residency), storage) { ControllerContext = Context(tenantId, employeeId) };

    private async Task<JsonElement> ViewAsync(IConfiguration config, Guid tenantId, int employeeId, StorageResidency? residency = null)
    {
        await using var db = _fx.CreateDb();
        var result = await Ess(db, config, tenantId, employeeId, residency, _storage).GetAttendanceVerification(default);
        return JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
    }

    private async Task ConsentAsync(IConfiguration config, Guid tenantId, int employeeId, StorageResidency? residency = null)
    {
        await using var db = _fx.CreateDb();
        var result = await Ess(db, config, tenantId, employeeId, residency, _storage).GiveConsent(new GiveBiometricConsentRequest("1", "Mobile"), default);
        Assert.True(result is ObjectResult { StatusCode: 200 or 201 }, $"consent was not recorded: {SelfieWorld.CodeOf(result)} {SelfieWorld.MessageOf(result)}");
    }

    private async Task<ObjectResult> UploadResultAsync(IConfiguration config, Guid tenantId, int employeeId, StorageResidency? residency = null)
    {
        await using var db = _fx.CreateDb();
        var controller = new AttendanceEvidenceController(db, _storage, Verification(db, config, residency), new SelfieImageGate())
        {
            ControllerContext = Context(tenantId, employeeId),
        };
        var bytes = SelfieAttendanceTests.SelfieJpeg();
        controller.Request.ContentType = "multipart/form-data; boundary=x";
        controller.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(),
            new FormFileCollection { new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "s.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" } });
        return Assert.IsAssignableFrom<ObjectResult>(await controller.UploadSelfie());
    }

    private async Task<Guid> UploadAsync(IConfiguration config, Guid tenantId, int employeeId, StorageResidency? residency = null)
    {
        var result = await UploadResultAsync(config, tenantId, employeeId, residency);
        Assert.True(result.StatusCode == 201, $"upload refused: {result.StatusCode} {SelfieWorld.CodeOf(result)} {SelfieWorld.MessageOf(result)}");
        return JsonSerializer.SerializeToElement(result.Value).GetProperty("evidenceId").GetGuid();
    }

    private async Task<IActionResult?> PunchResultAsync(IConfiguration config, Guid tenantId, int employeeId, Guid evidenceId, StorageResidency? residency = null)
    {
        await using var db = _fx.CreateDb();
        var controller = new AttendanceController(new AttendanceService(db, new NullNotifications(), new NullHttpClients()),
            new DataScopeService(db), new HrmHierarchyService(db, new NullAudit()), db, Verification(db, config, residency))
        { ControllerContext = Context(tenantId, employeeId) };
        return (await controller.MobilePunch(new WebPunchRequest(0, "In", null, null, null, EvidenceId: evidenceId), default)).Result;
    }

    private async Task<AttendanceRawEvent> PunchAsync(IConfiguration config, Guid tenantId, int employeeId, Guid evidenceId, StorageResidency? residency = null)
    {
        var result = await PunchResultAsync(config, tenantId, employeeId, evidenceId, residency);
        return Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(result).Value);
    }

    private async Task<(Guid TenantId, string Slug, int EmployeeId)> SeedAsync()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var slug = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Slug).SingleAsync();
        db.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true });
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"DEMO-{Guid.NewGuid():N}"[..20], FullName = "Demo Person",
            Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (tenantId, slug, employee.Id);
    }

    private ServiceProvider BuildInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _fx.CreateDb());
        services.AddSingleton<IDocumentStorage>(_storage);
        services.AddSingleton(new SelfieEvidencePurgeOptions());
        services.AddSingleton(new BackgroundJobOptions { LeaseDuration = TimeSpan.FromMinutes(2), HeartbeatInterval = TimeSpan.FromSeconds(30) });
        services.AddSingleton(SelfieEvidencePurgeJobHandler.Descriptor);
        services.AddScoped<SelfieEvidencePurgeJobHandler>();
        services.AddSingleton<BackgroundJobTypeRegistry>();
        services.AddScoped<BackgroundJobStore>();
        services.AddSingleton<BackgroundJobRunner>();
        return services.BuildServiceProvider();
    }

    private async Task<string> RunJobAsync(ServiceProvider sp, Guid tenantId, DateTime asOf)
    {
        Guid jobId;
        await using (var db = _fx.CreateDb())
        {
            var store = new BackgroundJobStore(db, sp.GetRequiredService<BackgroundJobTypeRegistry>());
            jobId = (await store.EnqueueAsync(tenantId, SelfieEvidencePurgeJobHandler.JobType,
                SelfieEvidencePurgeJobHandler.IdempotencyKey(asOf), new SelfieEvidencePurgePayload(asOf), null, default)).Job.Id;
        }
        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        for (var i = 0; i < 60; i++)
        {
            await using (var db = _fx.CreateDb())
            {
                var job = await db.BackgroundJobs.IgnoreQueryFilters().Where(j => j.Id == jobId).Select(j => new { j.Status, j.LastError }).SingleAsync();
                if (BackgroundJobStatuses.IsTerminal(job.Status))
                {
                    Assert.True(job.Status == BackgroundJobStatuses.Succeeded, job.LastError ?? "no error recorded");
                    return job.Status;
                }
            }
            if (!await runner.RunNextAsync("w", default, default, [SelfieEvidencePurgeJobHandler.JobType]))
                await Task.Delay(50);
        }
        throw new TimeoutException($"Selfie purge job {jobId} did not finish.");
    }
}

/// <summary>
/// 20261008000800_AddSelfieDemoExceptionPurgeDue on a fresh PostgreSQL 16 built by the real migration chain: applies,
/// re-runs as a no-op, enforces its CHECK, names its index's query, refuses to roll back while a stamped selfie is still
/// stored, then rolls back and re-applies.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SelfieDemoExceptionMigrationPostgresTests
{
    private const string ThisMigration = "20261008000800_AddSelfieDemoExceptionPurgeDue";

    [Fact]
    public async Task Migration_Applies_ReRuns_EnforcesItsCheck_RefusesRollbackOverStoredDemoSelfies_AndReapplies()
    {
        await using var container = new Testcontainers.PostgreSql.PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
        var all = db.Database.GetMigrations().ToList();
        Assert.Contains(ThisMigration, all);
        var before = all[all.IndexOf(ThisMigration) - 1];

        await migrator.MigrateAsync();
        await migrator.MigrateAsync(); // a re-run is a no-op
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(1L, await Scalar<long>(db, "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_attendance_evidence__purge_due_after_capture'"));
        var index = await Scalar<string>(db, "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_attendance_evidence__purge_due_override'");
        Assert.Contains("(tenant_id, purge_due_at_utc)", index);
        Assert.Contains("purge_due_at_utc IS NOT NULL", index);
        Assert.Contains("query d", await Scalar<string>(db, "SELECT obj_description('ix_attendance_evidence__purge_due_override'::regclass, 'pg_class')"));

        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "M", Slug = $"m-{tenant:N}" });
        var employee = new Employee { TenantId = tenant, EmployeeCode = "M-1", FullName = "M", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        string Row(string due) =>
            "INSERT INTO attendance_evidence (id,tenant_id,employee_id,storage_key,sha256,content_type,byte_size,created_at_utc,expires_at_utc,purge_state,purge_due_at_utc) " +
            $"VALUES (gen_random_uuid(),'{tenant}',{employee.Id},'k','{new string('c', 64)}','image/jpeg',10,now(),now() + interval '10 minutes','Active',{due})";
        await Sql(db, Row("now() + interval '7 days'"));
        await Sql(db, Row("NULL"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Sql(db, Row("now() - interval '1 minute'"))); // due before capture

        // A stamped selfie still stored: Down refuses (dropping the stamp would drop the promise to delete it).
        await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(before));
        Assert.Equal(1L, await Scalar<long>(db, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'attendance_evidence' AND column_name = 'purge_due_at_utc'"));
        await Sql(db, "UPDATE attendance_evidence SET purge_state = 'Purged', purged_at_utc = now() WHERE purge_due_at_utc IS NOT NULL");
        await migrator.MigrateAsync(before);
        Assert.Equal(0L, await Scalar<long>(db, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'attendance_evidence' AND column_name = 'purge_due_at_utc'"));
        await migrator.MigrateAsync();
        Assert.Equal(1L, await Scalar<long>(db, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'attendance_evidence' AND column_name = 'purge_due_at_utc'"));
    }

    private static async Task Sql(ZayraDbContext db, string sql)
    {
        var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(ZayraDbContext db, string sql)
    {
        var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
