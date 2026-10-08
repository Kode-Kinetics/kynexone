using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Selfie attendance v2 on real PostgreSQL, where InMemory cannot speak: the purge job through the F3 queue (leases,
/// per-item transactions, a second run), and the single-use evidence id under two concurrent punches.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SelfieAttendancePostgresTests
{
    private readonly PostgresFixture _fx;
    public SelfieAttendancePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task PurgeJob_DeletesDueBlobs_KeepsRowsAndSha_WritesRetentionAudits_AndASecondRunIsANoOp()
    {
        var storage = new MemoryDocumentStorage();
        var (tenantId, employeeId) = await SeedEmployeeAsync();
        var now = DateTime.UtcNow;
        Guid dueId, freshId;
        await using (var db = _fx.CreateDb())
        {
            // The uploader still consents, so an unused selfie keeps its 24 hours (without consent it is due at once).
            db.BiometricConsents.Add(new BiometricConsent { TenantId = tenantId, EmployeeId = employeeId, PolicyVersion = "1", GivenAtUtc = now.AddDays(-2), Channel = "Mobile" });
            await db.SaveChangesAsync();
            dueId = await AddEvidenceAsync(db, storage, tenantId, employeeId, now.AddHours(-30));
            freshId = await AddEvidenceAsync(db, storage, tenantId, employeeId, now.AddHours(-2));
        }

        await using var sp = BuildInstance(storage);
        Assert.Equal("Succeeded", await RunJobAsync(sp, tenantId, now));

        await using (var db = _fx.CreateDb())
        {
            var due = await db.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.Id == dueId);
            Assert.Equal(AttendanceEvidencePurgeStates.Purged, due.PurgeState);
            Assert.NotNull(due.PurgedAtUtc);
            Assert.Equal(new string('b', 64), due.Sha256);
            Assert.False(storage.Objects.ContainsKey(due.StorageKey));
            var fresh = await db.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.Id == freshId);
            Assert.Equal(AttendanceEvidencePurgeStates.Active, fresh.PurgeState);
            Assert.True(storage.Objects.ContainsKey(fresh.StorageKey));
            var audits = await db.RetentionPurgeAudits.IgnoreQueryFilters().Where(a => a.TenantId == tenantId).ToListAsync();
            Assert.Equal(dueId.ToString(), Assert.Single(audits).EntityId);
            Assert.Equal(SelfieEvidenceRetention.RuleKey, audits[0].RuleKey);
            Assert.NotNull(audits[0].JobId);
        }

        // An hour later (a new job key): nothing new is due, nothing is deleted again, no second audit row.
        Assert.Equal("Succeeded", await RunJobAsync(sp, tenantId, now.AddHours(1)));
        await using (var db = _fx.CreateDb())
            Assert.Equal(1, await db.RetentionPurgeAudits.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenantId));
        // Only this tenant's files: the runner also picks up purge jobs other tests in the collection left queued.
        Assert.Single(storage.Deleted, k => k.Contains(tenantId.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evidence_TwoConcurrentPunchesWithTheSameId_ExactlyOneWins()
    {
        var storage = new MemoryDocumentStorage();
        var (tenantId, employeeId) = await SeedEmployeeAsync();
        Guid evidenceId;
        await using (var db = _fx.CreateDb())
        {
            evidenceId = await AddEvidenceAsync(db, storage, tenantId, employeeId, DateTime.UtcNow);
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> AttemptAsync(string direction)
        {
            await using var db = _fx.CreateDb();
            var service = new AttendanceService(db, new NullNotifications(), new NullHttpClients());
            await gate.Task;
            try
            {
                await service.PunchAsync(tenantId, new WebPunchRequest(employeeId, direction, null, null, null, EvidenceId: evidenceId), "Mobile app punch",
                    new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenantId), default,
                    new PunchVerification(AttendanceVerificationMethods.Selfie, evidenceId, null, null));
                return true;
            }
            catch (AttendanceRefusalException ex) when (ex.Refusal.Code == "evidence_used") { return false; }
        }

        var first = AttemptAsync("In");
        var second = AttemptAsync("Out");
        gate.SetResult();
        var outcomes = await Task.WhenAll(first, second);

        Assert.Equal(1, outcomes.Count(x => x));
        await using var verify = _fx.CreateDb();
        Assert.Equal(1, await verify.AttendanceRawEvents.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId));
        var used = await verify.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.Id == evidenceId);
        Assert.NotNull(used.UsedByRawEventId);
    }

    [Fact]
    public async Task Upload_OnPostgres_ReservesTheAttempt_StoresTheRow_AndEnforcesTheHourlyLimit()
    {
        var storage = new MemoryDocumentStorage();
        var (tenantId, employeeId) = await SeedEmployeeAsync();
        await using (var db = _fx.CreateDb())
        {
            await EnableAsync(db, tenantId, employeeId);
            for (var i = 0; i < 9; i++) await AddEvidenceAsync(db, storage, tenantId, employeeId, DateTime.UtcNow.AddMinutes(-5));
        }

        Assert.Equal(201, await UploadAsync(storage, tenantId, employeeId));   // the tenth this hour
        Assert.Equal(429, await UploadAsync(storage, tenantId, employeeId));   // the eleventh
        await using var verify = _fx.CreateDb();
        Assert.Equal(10, await verify.AttendanceEvidence.IgnoreQueryFilters().CountAsync(e => e.EmployeeId == employeeId));
        var stored = await verify.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.EmployeeId == employeeId && e.Sha256 != new string('b', 64));
        Assert.Equal(AttendanceEvidencePurgeStates.Active, stored.PurgeState);
        Assert.EndsWith($"attendance-evidence/{stored.Id:N}.jpg", stored.StorageKey);
        Assert.True(storage.Objects.ContainsKey(stored.StorageKey));
        Assert.Equal(1, await verify.AttendanceAuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenantId && a.Action == "attendance.selfie.uploaded"));
    }

    /// <summary>
    /// Review item 12, narrowed by review 3 (one upload in flight per employee): twelve uploads at once. The per-employee
    /// advisory lock serialises the in-flight check and the reservation, so exactly one is admitted and the other eleven
    /// are refused 409 <c>selfie_upload_in_progress</c> — never 429 busy, so they can never become waivers.
    /// </summary>
    [Fact]
    public async Task Upload_TwelveInParallel_OnlyOneIsInFlight_TheOthersAre409InProgress()
    {
        // The admitted upload is held inside its storage write until every other attempt has answered, so all twelve
        // overlap for real.
        var storage = new BlockingDocumentStorage();
        var (tenantId, employeeId) = await SeedEmployeeAsync();
        await using (var db = _fx.CreateDb())
        {
            await EnableAsync(db, tenantId, employeeId);
            for (var i = 0; i < 7; i++) await AddEvidenceAsync(db, storage.Inner, tenantId, employeeId, DateTime.UtcNow.AddMinutes(-5));
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var imageGate = new SelfieImageGate(concurrency: 16);
        async Task<int?> Attempt()
        {
            await gate.Task;
            return await UploadAsync(storage, tenantId, employeeId, imageGate);
        }
        var attempts = Enumerable.Range(0, 12).Select(_ => Task.Run(Attempt)).ToList();
        gate.SetResult();
        await storage.WriteStarted.WaitAsync(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 400 && attempts.Count(t => t.IsCompleted) < 11; i++) await Task.Delay(50);
        // Eleven answered while one is still writing; then let it finish.
        Assert.Equal(11, attempts.Count(t => t.IsCompleted));
        storage.Release();
        var codes = await Task.WhenAll(attempts);

        Assert.Equal(1, codes.Count(c => c == 201));
        Assert.Equal(11, codes.Count(c => c == 409));
        await using var verify = _fx.CreateDb();
        Assert.Equal(8, await verify.AttendanceEvidence.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId && e.EmployeeId == employeeId));
        Assert.Equal(0, await verify.AttendanceEvidence.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId && e.FailedReason != null));
    }

    /// <summary>An attempt whose body turns out to be junk still counts (it reserved a Pending row), and is swept within the hour.</summary>
    [Fact]
    public async Task Upload_AStorageFailedAttempt_Counts_AndItsPendingRowIsPurgedAfterAnHour_WhileBadInputLeavesNoRow()
    {
        var storage = new MemoryDocumentStorage();
        var (tenantId, employeeId) = await SeedEmployeeAsync();
        await using (var db = _fx.CreateDb()) await EnableAsync(db, tenantId, employeeId);

        // Bad input never reached storage: its reserved row is deleted, so it does not count (review 2, item 7).
        Assert.Equal(400, await UploadAsync(storage, tenantId, employeeId, bytes: "not a photo"u8.ToArray()));
        await using (var db = _fx.CreateDb())
            Assert.Equal(0, await db.AttendanceEvidence.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId));

        // A storage failure did reach storage: the row stays Pending (it counts) and is swept after an hour.
        storage.FailPuts = true;
        Assert.Equal(503, await UploadAsync(storage, tenantId, employeeId));
        storage.FailPuts = false;
        Guid pendingId;
        await using (var db = _fx.CreateDb())
        {
            var pending = await db.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.TenantId == tenantId);
            Assert.Equal(AttendanceEvidencePurgeStates.Pending, pending.PurgeState);
            Assert.Null(pending.Sha256);
            pendingId = pending.Id;
        }

        await using var sp = BuildInstance(storage);
        Assert.Equal("Succeeded", await RunJobAsync(sp, tenantId, DateTime.UtcNow.AddMinutes(61)));
        await using var verify = _fx.CreateDb();
        var swept = await verify.AttendanceEvidence.IgnoreQueryFilters().SingleAsync(e => e.Id == pendingId);
        Assert.Equal(AttendanceEvidencePurgeStates.Purged, swept.PurgeState);
        Assert.Contains("never completed", (await verify.RetentionPurgeAudits.IgnoreQueryFilters().SingleAsync(a => a.EntityId == pendingId.ToString())).Reason);
    }

    private static async Task EnableAsync(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.SelfieAttendance, IsEnabled = true, ConfigJson = SelfieWorld.SignedOffConfig() });
        db.BiometricConsents.Add(new BiometricConsent { TenantId = tenantId, EmployeeId = employeeId, PolicyVersion = "1", Channel = BiometricConsentChannels.Mobile });
        await db.SaveChangesAsync();
    }

    private async Task<int?> UploadAsync(IDocumentStorage storage, Guid tenantId, int employeeId, SelfieImageGate? gate = null, byte[]? bytes = null)
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim("tenant_id", tenantId.ToString()),
            new System.Security.Claims.Claim("sub", Guid.NewGuid().ToString()),
            new System.Security.Claims.Claim("employee_id", employeeId.ToString()),
        ], "Test"));
        await using var db = _fx.CreateDb();
        var controller = new Zayra.Api.Controllers.AttendanceEvidenceController(db, storage,
            new AttendanceVerificationService(db, SelfieWorld.ResidentKsa), gate ?? new SelfieImageGate())
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = principal } },
        };
        bytes ??= SelfieAttendanceTests.SelfieJpeg();
        controller.Request.ContentType = "multipart/form-data; boundary=x";
        controller.Request.Form = new Microsoft.AspNetCore.Http.FormCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(),
            new Microsoft.AspNetCore.Http.FormFileCollection
            {
                new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "s.jpg")
                {
                    Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(), ContentType = "image/jpeg",
                },
            });
        var result = await controller.UploadSelfie();
        return (result as Microsoft.AspNetCore.Mvc.ObjectResult)?.StatusCode;
    }

    private async Task<(Guid TenantId, int EmployeeId)> SeedEmployeeAsync()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"SELFIE-{Guid.NewGuid():N}"[..20], FullName = "Selfie Person",
            Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (tenantId, employee.Id);
    }

    private static async Task<Guid> AddEvidenceAsync(ZayraDbContext db, MemoryDocumentStorage storage, Guid tenantId, int employeeId, DateTime createdAt)
    {
        var key = (await storage.SaveAsync(tenantId, new Microsoft.AspNetCore.Http.FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "s.jpg")
        {
            Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(), ContentType = "image/jpeg",
        }, default)).StorageUrl;
        var row = new AttendanceEvidence
        {
            TenantId = tenantId, EmployeeId = employeeId, StorageKey = key, Sha256 = new string('b', 64), ByteSize = 3,
            CreatedAtUtc = createdAt, ExpiresAtUtc = createdAt.AddMinutes(10), PurgeState = AttendanceEvidencePurgeStates.Active,
        };
        db.AttendanceEvidence.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
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
        for (var i = 0; i < 40; i++)
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

    private ServiceProvider BuildInstance(IDocumentStorage storage)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _fx.CreateDb());
        services.AddSingleton(storage);
        services.AddSingleton(new SelfieEvidencePurgeOptions());
        services.AddSingleton(new BackgroundJobOptions { LeaseDuration = TimeSpan.FromMinutes(2), HeartbeatInterval = TimeSpan.FromSeconds(30) });
        services.AddSingleton(SelfieEvidencePurgeJobHandler.Descriptor);
        services.AddScoped<SelfieEvidencePurgeJobHandler>();
        services.AddSingleton<BackgroundJobTypeRegistry>();
        services.AddScoped<BackgroundJobStore>();
        services.AddSingleton<BackgroundJobRunner>();
        return services.BuildServiceProvider();
    }
}

/// <summary>
/// The migration itself (20261008000500_AddSelfieEvidenceAndBiometricConsent) on a fresh PostgreSQL 16 built by the real
/// migration chain — the shared fixture uses EnsureCreated, which cannot prove a migration. Applies, re-runs as a no-op,
/// enforces every CHECK and the one-open-consent index, refuses to roll back over rows, then rolls back and re-applies.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SelfieAttendanceMigrationPostgresTests
{
    private const string ThisMigration = "20261008000500_AddSelfieEvidenceAndBiometricConsent";

    [Fact]
    public async Task Migration_Applies_ReRuns_EnforcesItsConstraints_RefusesRollbackOverRows_AndReapplies()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new ZayraDbContext(options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.IndexOf(ThisMigration) - 1];

        await migrator.MigrateAsync();
        await migrator.MigrateAsync(); // re-run is a no-op
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        foreach (var name in new[] { "ck_attendance_evidence__purge_state", "ck_attendance_evidence__purged_pair", "ck_attendance_evidence__used_pair",
                     "ck_attendance_evidence__byte_size", "ck_attendance_evidence__expiry", "ck_attendance_evidence__active_payload",
                     "ck_attendance_evidence__used_was_active", "ck_attendance_evidence__failed_reason",
                     "ck_attendance_evidence__failed_never_active", "ck_attendance_evidence__waiver_pair",
                     "ck_attendance_evidence__waiver_needs_failure", "ck_attendance_evidence__waiver_once", "ck_biometric_consents__channel",
                     "ck_biometric_consents__withdrawn_after_given", "ck_biometric_consents__policy_version" })
            Assert.Equal(1L, await Scalar<long>(db, $"SELECT count(*) FROM pg_constraint WHERE conname = '{name}'"));
        Assert.Equal("r", await Scalar<string>(db, "SELECT confdeltype::text FROM pg_constraint WHERE conname = 'FK_attendance_evidence_employees_employee_id'"));
        Assert.Contains("@retention:E", await Scalar<string>(db, "SELECT obj_description('attendance_evidence'::regclass, 'pg_class')"));
        Assert.Contains("rate limit", await Scalar<string>(db, "SELECT obj_description('ix_attendance_evidence__employee_created'::regclass, 'pg_class')"));
        // Review 3: the waiver indexes name their queries, and the waiver's punch is a RESTRICT FK.
        Assert.Contains("waived-punches", await Scalar<string>(db, "SELECT obj_description('ix_attendance_evidence__waived_punches'::regclass, 'pg_class')"));
        Assert.Contains("One waiver per punch", await Scalar<string>(db, "SELECT obj_description('ux_attendance_evidence__waiver_raw_event'::regclass, 'pg_class')"));
        Assert.Equal("r", await Scalar<string>(db, "SELECT confdeltype::text FROM pg_constraint WHERE conname = 'FK_attendance_evidence_attendance_raw_events_waiver_raw_event_~'"));
        // The purge index: no constant leading purge_state, partial on the two live states, used_at_utc included.
        var purgeIndex = await Scalar<string>(db, "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_attendance_evidence__purge_due'");
        Assert.Contains("(tenant_id, created_at_utc) INCLUDE (used_at_utc)", purgeIndex);
        Assert.Contains("'Pending'", purgeIndex);
        Assert.DoesNotContain("SelfieAttendanceController.StoreAsync", await Scalar<string>(db,
            "SELECT obj_description('ix_attendance_evidence__employee_created'::regclass, 'pg_class')"));

        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "M", Slug = $"m-{tenant:N}" });
        var employee = new Employee { TenantId = tenant, EmployeeCode = "M-1", FullName = "M", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var e = employee.Id;
        var hash = "'" + new string('c', 64) + "'";
        string Evidence(string purgeState = "Active", string purgedAt = "NULL", string usedAt = "NULL", string? sha = null, string size = "10") =>
            $"INSERT INTO attendance_evidence (id,tenant_id,employee_id,storage_key,sha256,content_type,byte_size,created_at_utc,expires_at_utc,used_at_utc,used_by_raw_event_id,purge_state,purged_at_utc) " +
            $"VALUES (gen_random_uuid(),'{tenant}',{e},'k',{sha ?? hash},'image/jpeg',{size},now(),now() + interval '10 minutes',{usedAt},NULL,'{purgeState}',{purgedAt})";

        await Sql(db, Evidence());
        await Sql(db, Evidence(purgeState: "Pending", sha: "NULL", size: "NULL"));                      // an attempt before its upload
        await Assert.ThrowsAsync<PostgresException>(() => Sql(db, Evidence(sha: "NULL", size: "NULL")));    // Active without its hash
        await Assert.ThrowsAsync<PostgresException>(() => Sql(db, Evidence(purgeState: "Gone")));
        await Assert.ThrowsAsync<PostgresException>(() => Sql(db, Evidence(purgeState: "Purged")));          // purged without purged_at
        await Assert.ThrowsAsync<PostgresException>(() => Sql(db, Evidence(usedAt: "now()")));               // used without a raw event
        // Review 3: the waiver columns.
        string Failed(string reason = "'Busy'", string consumed = "NULL", string cancelled = "NULL", string state = "Purged") =>
            $"INSERT INTO attendance_evidence (id,tenant_id,employee_id,storage_key,content_type,created_at_utc,expires_at_utc,purge_state,purged_at_utc,failed_reason,waiver_consumed_at_utc,waiver_raw_event_id,waiver_cancelled_at_utc) " +
            $"VALUES (gen_random_uuid(),'{tenant}',{e},'k','image/jpeg',now(),now() + interval '10 minutes','{state}',{(state == "Purged" ? "now()" : "NULL")},{reason},{consumed},NULL,{cancelled})";
        await Sql(db, Failed());
        await Sql(db, Failed(reason: "'Storage'", state: "Pending"));
        await Sql(db, Failed(cancelled: "now()"));
        await Assert.ThrowsAsync<PostgresException>(() => Sql(db, Failed(reason: "'Crash'")));                // not a server failure
        await Assert.ThrowsAsync<PostgresException>(() => Sql(db, Failed(reason: "NULL", cancelled: "now()"))); // a waiver without a failure
        await Assert.ThrowsAsync<PostgresException>(() => Sql(db, Failed(consumed: "now()")));                  // used without its punch
        await Assert.ThrowsAsync<PostgresException>(() => Sql(db,                                              // a failed attempt is never Active
            $"INSERT INTO attendance_evidence (id,tenant_id,employee_id,storage_key,sha256,content_type,byte_size,created_at_utc,expires_at_utc,purge_state,failed_reason) " +
            $"VALUES (gen_random_uuid(),'{tenant}',{e},'k',{hash},'image/jpeg',10,now(),now() + interval '10 minutes','Active','Busy')"));
        await Sql(db, "DELETE FROM attendance_evidence WHERE failed_reason IS NOT NULL");
        await Sql(db, $"INSERT INTO biometric_consents (id,tenant_id,employee_id,policy_version,given_at_utc,channel) VALUES (gen_random_uuid(),'{tenant}',{e},'1',now(),'Mobile')");
        await Assert.ThrowsAsync<PostgresException>(() => Sql(db,                                             // a second open consent
            $"INSERT INTO biometric_consents (id,tenant_id,employee_id,policy_version,given_at_utc,channel) VALUES (gen_random_uuid(),'{tenant}',{e},'1',now(),'Web')"));
        await Assert.ThrowsAsync<PostgresException>(() => Sql(db,
            $"INSERT INTO biometric_consents (id,tenant_id,employee_id,policy_version,given_at_utc,channel) VALUES (gen_random_uuid(),'{tenant}',{e},'1',now(),'Fax')"));

        // Down refuses while rows exist; with them gone it rolls back cleanly and the migration re-applies.
        await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(before));
        Assert.Equal(2L, await Scalar<long>(db, "SELECT count(*) FROM attendance_evidence"));
        await Sql(db, "DELETE FROM attendance_evidence; DELETE FROM biometric_consents;");
        await migrator.MigrateAsync(before);
        Assert.Equal(0L, await Scalar<long>(db, "SELECT count(*) FROM pg_tables WHERE tablename IN ('attendance_evidence','biometric_consents')"));
        await migrator.MigrateAsync();
        Assert.Equal(2L, await Scalar<long>(db, "SELECT count(*) FROM pg_tables WHERE tablename IN ('attendance_evidence','biometric_consents')"));
    }

    private static async Task Sql(ZayraDbContext db, string sql)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(ZayraDbContext db, string sql)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
