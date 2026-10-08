using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Selfie attendance v2, third review: what the redesigned waiver stores and reports, the platform-header switch, the
/// kiosk channel matrix, and Admin's default. These name the review-3 columns and members, so (unlike
/// <see cref="SelfieReview3RefusalTests"/>) they cannot compile against the reviewed head.
/// </summary>
public sealed class SelfieReview3Tests
{
    // ── Items 1–3: the waiver is one specific failed attempt ─────────────────────────────────────────────

    [Fact]
    public async Task AStorageFailure_IsRecordedOnItsOwnAttempt_AndThePunchUsingItIsMarkedOnTheRawEvent()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        w.Storage.FailPuts = true;
        Assert.Equal(503, Status(await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg())));
        w.Storage.FailPuts = false;
        var attempt = await w.Db.AttendanceEvidence.SingleAsync();
        Assert.Equal(SelfieUploadFailureReasons.Storage, attempt.FailedReason);
        Assert.Equal(AttendanceEvidencePurgeStates.Pending, attempt.PurgeState); // a partial file may exist: the sweeper's

        // The audit log is not load-bearing: wipe it, the waiver still works.
        w.Db.AttendanceAuditLogs.RemoveRange(w.Db.AttendanceAuditLogs);
        await w.Db.SaveChangesAsync();
        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);

        var raw = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(punch.Result).Value);
        Assert.Equal(AttendanceVerificationMethods.None, raw.VerificationMethod);
        Assert.Equal($"waiver:{attempt.Id}", raw.PhotoReference);
        var used = await w.Db.AttendanceEvidence.SingleAsync(e => e.Id == attempt.Id);
        Assert.Equal(raw.Id, used.WaiverRawEventId);
        Assert.NotNull(used.WaiverConsumedAtUtc);
        Assert.Null(used.WaiverCancelledAtUtc);
        var audit = await w.Db.AttendanceAuditLogs.SingleAsync(a => a.Action == AttendanceVerificationService.SelfieRequirementWaivedAction);
        Assert.Contains(attempt.Id.ToString(), audit.MetadataJson);
    }

    [Fact]
    public async Task ABusyAttempt_IsClosedPurgedWithItsReason_AndALaterSuccessCancelsTheWaiver()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        Assert.True(w.Gate.TryEnter());
        Assert.Equal(429, Status(await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg())));
        w.Gate.Exit();
        var busy = await w.Db.AttendanceEvidence.SingleAsync();
        Assert.Equal(SelfieUploadFailureReasons.Busy, busy.FailedReason);
        Assert.Equal(AttendanceEvidencePurgeStates.Purged, busy.PurgeState);

        Assert.Equal(201, Status(await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg())));

        var cancelled = await w.Db.AttendanceEvidence.SingleAsync(e => e.Id == busy.Id);
        Assert.NotNull(cancelled.WaiverCancelledAtUtc);
        Assert.Null(cancelled.WaiverConsumedAtUtc);
    }

    [Fact]
    public async Task AServerFailureWhileAnotherAttemptIsInFlight_WaivesNothing()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        // While this upload writes, another attempt of the employee reserves (as if this one had been slow for > 60 s),
        // and then this write fails.
        var storage = new FailingWhileAnotherReservesStorage(w);
        var controller = w.With(new AttendanceEvidenceController(w.Db, storage, w.Verification, w.Gate), user);
        controller.Request.ContentType = "multipart/form-data; boundary=x";
        var bytes = SelfieAttendanceTests.SelfieJpeg();
        controller.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(),
            new FormFileCollection { new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "s.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" } });

        var failed = await controller.UploadSelfie();

        Assert.Equal(503, Status(failed));
        Assert.False(JsonSerializer.SerializeToElement(((ObjectResult)failed).Value).GetProperty("punchWithoutSelfie").GetBoolean());
        Assert.All(await w.Db.AttendanceEvidence.ToListAsync(), e => Assert.Null(e.FailedReason));
        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        Assert.Equal("selfie_required", SelfieWorld.CodeOf(punch.Result));
    }

    [Fact]
    public async Task AWaiverOlderThanTenMinutes_IsNotUsed()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        w.Db.AttendanceEvidence.Add(new AttendanceEvidence
        {
            TenantId = w.TenantId, EmployeeId = w.Caller.Id, StorageKey = $"storage/documents/{w.TenantId:N}/attendance-evidence/x.jpg",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-11), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1),
            PurgeState = AttendanceEvidencePurgeStates.Purged, PurgedAtUtc = DateTime.UtcNow.AddMinutes(-11), FailedReason = SelfieUploadFailureReasons.Busy,
        });
        await w.Db.SaveChangesAsync();

        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);

        Assert.Equal("selfie_required", SelfieWorld.CodeOf(punch.Result));
    }

    [Fact]
    public async Task TheLegacyMobileRoute_NeverUsesAWaiver()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        w.Storage.FailPuts = true;
        Assert.Equal(503, Status(await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg())));

        var legacy = await w.Mobile(user).Punch(new Zayra.Api.Controllers.MobilePunchRequest(0, "In", null), default);

        Assert.Equal("selfie_required", SelfieWorld.CodeOf(legacy));
        Assert.Null((await w.Db.AttendanceEvidence.SingleAsync()).WaiverConsumedAtUtc);
    }

    [Fact]
    public async Task AnIntegration_CannotWriteAWaiverReference()
    {
        Assert.True(ReservedVerificationLabels.Violates("Device", "waiver:00000000-0000-0000-0000-000000000001"));
        Assert.False(ReservedVerificationLabels.Violates("Device", "photo-123"));
        await Task.CompletedTask;
    }

    // ── The HR report ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheWaivedPunchesReport_ListsEachWaivedPunch_WithItsFailedAttempt_InScopeOnly()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        w.Storage.FailPuts = true;
        Assert.Equal(503, Status(await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg())));
        w.Storage.FailPuts = false;
        var raw = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(
            (await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default)).Result).Value);
        var attemptId = (await w.Db.AttendanceEvidence.SingleAsync()).Id;

        var hr = await w.RoleAsync("HR Manager", w.Colleague, w.ColleagueUserId);
        var report = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await w.Attendance(hr).SelfieWaivedPunches(null, null, default)).Value);

        Assert.Equal(1, report.GetProperty("count").GetInt32());
        var row = report.GetProperty("punches")[0];
        Assert.Equal(raw.Id, row.GetProperty("rawEventId").GetGuid());
        Assert.Equal(w.Caller.Id, row.GetProperty("employeeId").GetInt32());
        Assert.Equal("CALLER", row.GetProperty("employeeCode").GetString());
        Assert.Equal(attemptId, row.GetProperty("failedAttemptId").GetGuid());
        Assert.Equal("Storage", row.GetProperty("failedReason").GetString());
        Assert.Equal("None", row.GetProperty("verificationMethod").GetString());
        Assert.False(string.IsNullOrWhiteSpace(report.GetProperty("definition").GetString()));

        // A line manager whose team does not include the employee sees nothing.
        var manager = w.Principal("Manager", w.ColleagueUserId, w.Colleague.Id, (await SelfieWorld.SeededAsync("Manager")).Append("attendance.read"), null);
        var none = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await w.Attendance(manager).SelfieWaivedPunches(null, null, default)).Value);
        Assert.Equal(0, none.GetProperty("count").GetInt32());
    }

    // ── Item 4: the kiosk channel ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, AccessModes.KioskOnly, true, PunchChannel.Kiosk)]
    [InlineData(true, null, true, PunchChannel.SelfMobile)]          // HR Manager / Admin / Kiosk Operator on their own record
    [InlineData(true, AccessModes.Mobile, false, PunchChannel.SelfMobile)]
    [InlineData(false, null, true, PunchChannel.Kiosk)]              // an on-behalf kiosk punch by a holder of attendance.kiosk
    [InlineData(false, null, false, PunchChannel.OnBehalf)]          // attendance.write only
    public void Item4_TheKioskChannel_IsForAKioskOnlySelfPunch_OrAnOnBehalfKioskPunch(bool own, string? accessMode, bool holdsKiosk, PunchChannel expected) =>
        Assert.Equal(expected, AttendanceController.ResolvePunchChannel(PunchChannel.Kiosk, own, accessMode, holdsKiosk));

    // ── Item 8: the platform-header switch ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, true)]
    [InlineData("false", true)]
    [InlineData("true", false)]
    public async Task Item8_UnsupportedWithoutAPlatformHeader_IsRefusedOnlyWhenTheSwitchIsOn(string? setting, bool accepted)
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(setting is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { [AttendanceVerificationService.RequirePlatformHeaderForUnsupportedKey] = setting }).Build();
        var verification = new AttendanceVerificationService(w.Db, w.Residency, config);
        var controller = w.With(new AttendanceController(new AttendanceService(w.Db, new NullNotifications(), new NullHttpClients()),
            new DataScopeService(w.Db), new HrmHierarchyService(w.Db, new NullAudit()), w.Db, verification), await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var punch = await controller.MobilePunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8, MockDetection: "Unsupported"), default);

        if (accepted) Assert.IsType<OkObjectResult>(punch.Result);
        else Assert.Equal("app_update_required", SelfieWorld.CodeOf(punch.Result));
        // An iPhone that sends the header is accepted whatever the switch says.
        controller.Request.Headers[ClientPlatform.HeaderName] = "ios";
        Assert.IsType<OkObjectResult>((await controller.MobilePunch(new WebPunchRequest(0, "Out", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8, MockDetection: "Unsupported"), default)).Result);
    }

    // ── Item 10: Admin's default, and Admin still administers ───────────────────────────────────────────

    [Fact]
    public void Item10_BothAdminGrants_WithholdExactlyTheListedKeys()
    {
        Assert.Equal(["attendance.evidence.view"], AuthSeeder.AdminWithheldPermissions);
        var source = File.ReadAllText(Path.Combine(ApiRoot(), "Infrastructure", "Seed", "AuthSeeder.cs"));
        var notIn = System.Text.RegularExpressions.Regex.Match(source, @"p\.permission_key NOT IN \(([^)]*)\)");
        Assert.True(notIn.Success, "the Admin backfill no longer withholds anything");
        Assert.Equal(AuthSeeder.AdminWithheldPermissions, notIn.Groups[1].Value.Split(',').Select(x => x.Trim().Trim('\'')).ToArray());
        var isNot = System.Text.RegularExpressions.Regex.Match(source, "\"Admin\", \"Tenant system administrator with full access\",\\s*permissions\\.Where\\(x => x\\.Key is not (\"[^)]*)\\)");
        Assert.True(isNot.Success, "the Admin role bundle no longer withholds anything");
        Assert.Equal(AuthSeeder.AdminWithheldPermissions,
            System.Text.RegularExpressions.Regex.Matches(isNot.Groups[1].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray());
    }

    [Fact]
    public async Task Item10_AnAdminWithoutTheKey_CanStillManageAnHrManager_AndGrantTheKey_WhileANonAdminCannot()
    {
        var admin = PrivilegeCeiling.ForCaller(Guid.NewGuid(), isAdmin: true, await SelfieWorld.SeededAsync("Admin"), []);
        Assert.DoesNotContain("attendance.evidence.view", admin.Held);
        var hrManager = await SelfieWorld.SeededAsync("HR Manager");
        Assert.Null(PrivilegeCeiling.AboveCallerRefusal(admin, targetIsAdmin: false, hrManager));
        Assert.Null(PrivilegeCeiling.GrantRefusal(admin, ["attendance.evidence.view"]));

        var officer = PrivilegeCeiling.ForCaller(Guid.NewGuid(), isAdmin: false, await SelfieWorld.SeededAsync("HR Officer"), []);
        Assert.NotNull(PrivilegeCeiling.GrantRefusal(officer, ["attendance.evidence.view"]));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<SelfieWorld> RequiredAsync()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig(requireSelfieForConsented: true));
        await SelfieAttendanceTests.ConsentAsync(w, w.Caller, w.CallerUserId);
        return w;
    }

    private static int? Status(IActionResult result) => (result as ObjectResult)?.StatusCode;

    private static string ApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Zayra.Api"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Zayra.Api");
    }

    /// <summary>A store whose write fails after another attempt of the same employee has reserved meanwhile.</summary>
    private sealed class FailingWhileAnotherReservesStorage(SelfieWorld w) : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) => w.Storage.SaveAsync(tenantId, file, cancellationToken);
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => w.Storage.GetBytesAsync(tenantId, storageUrl, ct);
        public string ResolvePath(string storageUrl) => storageUrl;
        public Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => w.Storage.TryDeleteAsync(tenantId, storageUrl, ct);
        public string TenantKey(Guid tenantId, string relativeName) => w.Storage.TenantKey(tenantId, relativeName);
        public Task DeleteStrictAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => w.Storage.DeleteStrictAsync(tenantId, storageUrl, ct);

        public async Task PutAtAsync(Guid tenantId, string storageKey, byte[] content, string contentType, CancellationToken ct = default)
        {
            w.Db.AttendanceEvidence.Add(new AttendanceEvidence
            {
                TenantId = tenantId, EmployeeId = w.Caller.Id, StorageKey = w.Storage.TenantKey(tenantId, $"attendance-evidence/{Guid.NewGuid():N}.jpg"),
                CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10), PurgeState = AttendanceEvidencePurgeStates.Pending,
            });
            await w.Db.SaveChangesAsync(ct);
            throw new IOException("storage unavailable");
        }
    }
}

/// <summary>The waived-punches report's query on real PostgreSQL (the join on the nullable waiver FK must translate).</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SelfieReview3ReportPostgresTests
{
    private readonly PostgresFixture _fx;
    public SelfieReview3ReportPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task TheWaivedPunchesReport_RunsOnPostgres_AndListsTheWaivedPunch()
    {
        Guid tenantId;
        int employeeId;
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            var employee = new Employee
            {
                TenantId = tenantId, EmployeeCode = $"WAIVE-{Guid.NewGuid():N}"[..20], FullName = "Waived Person",
                Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
            employeeId = employee.Id;
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = SelfieWorld.SelfieKey, IsEnabled = true, ConfigJson = SelfieWorld.SignedOffConfig(requireSelfieForConsented: true) });
            db.BiometricConsents.Add(new BiometricConsent { TenantId = tenantId, EmployeeId = employeeId, PolicyVersion = "1", Channel = BiometricConsentChannels.Mobile });
            await db.SaveChangesAsync();
        }
        var storage = new MemoryDocumentStorage { FailPuts = true };
        var employeeUser = Principal(tenantId, employeeId, AccessModes.Mobile);
        await using (var db = _fx.CreateDb())
        {
            var upload = new AttendanceEvidenceController(db, storage, new AttendanceVerificationService(db, SelfieWorld.ResidentKsa), new SelfieImageGate())
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = employeeUser } } };
            var bytes = SelfieAttendanceTests.SelfieJpeg();
            upload.Request.ContentType = "multipart/form-data; boundary=x";
            upload.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(),
                new FormFileCollection { new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "s.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" } });
            Assert.Equal(503, ((ObjectResult)await upload.UploadSelfie()).StatusCode);
        }
        Guid rawId;
        await using (var db = _fx.CreateDb())
        {
            var punch = await Attendance(db, employeeUser).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
            rawId = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(punch.Result).Value).Id;
        }

        await using (var db = _fx.CreateDb())
        {
            var hr = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", tenantId.ToString()), new Claim("sub", Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "HR Manager"),
                new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = EntityScopeModes.Group })),
                new Claim("permission", "attendance.read"), new Claim("permission", "employees.read"), new Claim("permission", "employees.write"),
            ], "Test"));
            var report = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await Attendance(db, hr).SelfieWaivedPunches(null, null, default)).Value);
            Assert.Equal(1, report.GetProperty("count").GetInt32());
            Assert.Equal(rawId, report.GetProperty("punches")[0].GetProperty("rawEventId").GetGuid());
            Assert.Equal("Storage", report.GetProperty("punches")[0].GetProperty("failedReason").GetString());
        }
    }

    private static ClaimsPrincipal Principal(Guid tenantId, int employeeId, string accessMode) => new(new ClaimsIdentity(
    [
        new Claim("tenant_id", tenantId.ToString()), new Claim("sub", Guid.NewGuid().ToString()),
        new Claim("employee_id", employeeId.ToString()), new Claim("access_mode", accessMode),
    ], "Test"));

    private static AttendanceController Attendance(ZayraDbContext db, ClaimsPrincipal user)
    {
        var controller = new AttendanceController(new AttendanceService(db, new NullNotifications(), new NullHttpClients()),
            new DataScopeService(db), new HrmHierarchyService(db, new NullAudit()), db, new AttendanceVerificationService(db, SelfieWorld.ResidentKsa))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } } };
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        return controller;
    }
}
