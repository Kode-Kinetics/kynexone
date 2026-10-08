using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Review 2 of selfie attendance v2: the parts that are new surface (HR's selfie view and <c>hasSelfie</c>, the bucket
/// region and storage-move checks, the mock-unsupported report, the purge cadence), so they cannot compile on the
/// reviewed backend at all. The refusals that can are in <see cref="SelfieReview2RefusalTests"/>.
/// </summary>
public sealed class SelfieReview2Tests
{
    // ── Item 5: HR's review view ────────────────────────────────────────────────────────────────────────────

    /// <summary>The colleague consents, uploads and punches with the selfie; returns that punch's raw event id.</summary>
    private static async Task<Guid> ColleaguePunchesWithASelfieAsync(SelfieWorld w)
    {
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig());
        await SelfieAttendanceTests.ConsentAsync(w, w.Colleague, w.ColleagueUserId);
        var evidenceId = await SelfieAttendanceTests.UploadAsync(w, w.Colleague, w.ColleagueUserId);
        var punch = await w.Attendance(await w.EmployeeAsync(w.Colleague, w.ColleagueUserId))
            .MobilePunch(new WebPunchRequest(0, "In", null, null, null, EvidenceId: evidenceId), default);
        return Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(punch.Result).Value).Id;
    }

    [Fact]
    public async Task Item5_AnInScopeHrUser_SeesTheJpeg_NoStore_NoKey_AndTheViewIsAudited()
    {
        var w = await SelfieWorld.CreateAsync();
        var rawId = await ColleaguePunchesWithASelfieAsync(w);
        var hr = w.Evidence(await w.RoleAsync("HR Manager", w.Caller, w.CallerUserId));

        var result = await hr.ViewSelfie(rawId, default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.True(AttendanceEvidenceController.IsJpeg(file.FileContents));
        Assert.Equal("no-store", hr.Response.Headers.CacheControl.ToString());
        var evidence = await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync();
        Assert.DoesNotContain(evidence.StorageKey, string.Join(";", hr.Response.Headers.Select(h => h.Value.ToString())));
        var audit = await w.Db.AttendanceAuditLogs.AsNoTracking().SingleAsync(a => a.Action == "attendance.selfie.viewed");
        Assert.Equal(w.CallerUserId, audit.UserId);
        Assert.Contains($"\"employeeId\":{w.Colleague.Id}", audit.MetadataJson);
        Assert.Contains(rawId.ToString(), audit.MetadataJson);
        Assert.Contains(w.CallerUserId.ToString(), audit.MetadataJson);
        Assert.Contains("viewedAtUtc", audit.MetadataJson);
    }

    [Fact]
    public async Task Item5_TheEndpointRequiresTheEvidenceViewPermission_WhichIsCataloguedPrivilegedAndHeldByHr()
    {
        var method = typeof(AttendanceEvidenceController).GetMethod(nameof(AttendanceEvidenceController.ViewSelfie))!;
        var attribute = method.GetCustomAttribute<HasPermissionAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal("perm:attendance.evidence.view", attribute!.Policy);
        Assert.True(PrivilegedMfaPolicy.IsPrivilegedPermission("attendance.evidence.view"));
        Assert.Contains("attendance.evidence.view", PrivilegedMfaPolicy.PrivilegedPermissions);
        Assert.Contains("attendance.evidence.view", await SelfieWorld.SeededAsync("HR Manager"));
        Assert.Contains("attendance.evidence.view", await SelfieWorld.SeededAsync("HR Director"));
        Assert.DoesNotContain("attendance.evidence.view", await SelfieWorld.SeededAsync("Employee"));
        Assert.DoesNotContain("attendance.evidence.view", await SelfieWorld.SeededAsync("Manager"));
    }

    [Fact]
    public async Task Item5_AnEmployeeOutsideTheCallersScope_AnswersNotFound_AndNothingIsAudited()
    {
        var w = await SelfieWorld.CreateAsync();
        var rawId = await ColleaguePunchesWithASelfieAsync(w);
        // A line manager given the key, whose team does not include the colleague.
        var manager = w.Principal("Manager", w.CallerUserId, w.Caller.Id,
            (await SelfieWorld.SeededAsync("Manager")).Append("attendance.evidence.view"), null);

        var result = await w.Evidence(manager).ViewSelfie(rawId, default);

        Assert.Equal("evidence_not_found", SelfieWorld.CodeOf(Assert.IsType<NotFoundObjectResult>(result)));
        Assert.False(await w.Db.AttendanceAuditLogs.AnyAsync(a => a.Action == "attendance.selfie.viewed"));
    }

    [Fact]
    public async Task Item5_HrCannotOpenTheSelfieOnTheirOwnPunch()
    {
        var w = await SelfieWorld.CreateAsync();
        var rawId = await ColleaguePunchesWithASelfieAsync(w);
        // The colleague is an HR Manager too: org-wide scope, the key — and the subject of the punch.
        var self = w.Evidence(await w.RoleAsync("HR Manager", w.Colleague, w.ColleagueUserId));

        var result = await self.ViewSelfie(rawId, default);

        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal("evidence_own_record", SelfieWorld.CodeOf(result));
        Assert.False(await w.Db.AttendanceAuditLogs.AnyAsync(a => a.Action == "attendance.selfie.viewed"));
    }

    [Fact]
    public async Task Item5_APurgedSelfie_AnswersNotFound()
    {
        var w = await SelfieWorld.CreateAsync();
        var rawId = await ColleaguePunchesWithASelfieAsync(w);
        var evidence = await w.Db.AttendanceEvidence.SingleAsync();
        await new SelfieEvidencePurger(w.Db, w.Storage).PurgeNowAsync(evidence, DateTime.UtcNow, "test: retention reached", default);
        await w.Db.SaveChangesAsync();

        var result = await w.Evidence(await w.RoleAsync("HR Manager", w.Caller, w.CallerUserId)).ViewSelfie(rawId, default);

        Assert.IsType<NotFoundObjectResult>(result);
        // And a punch with no selfie at all answers the same.
        var plain = await w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId)).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        var plainId = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(plain.Result).Value).Id;
        Assert.IsType<NotFoundObjectResult>(await w.Evidence(await w.RoleAsync("HR Manager", w.Colleague, w.ColleagueUserId)).ViewSelfie(plainId, default));
    }

    [Fact]
    public async Task Item5_TheRawPunchLog_SaysWhichPunchesHaveAStoredSelfie_AndNeverTheKey()
    {
        var w = await SelfieWorld.CreateAsync();
        var withSelfie = await ColleaguePunchesWithASelfieAsync(w);
        await w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId)).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var hr = w.Attendance(await w.RoleAsync("HR Manager", w.Caller, w.CallerUserId));

        var page = await hr.Raw(day, day, null, null, 1, 50, default);

        Assert.Equal(2, page.Items.Count);
        Assert.True(page.Items.Single(r => r.Id == withSelfie).HasSelfie);
        Assert.False(page.Items.Single(r => r.Id != withSelfie).HasSelfie);
        var json = JsonSerializer.Serialize(page);
        Assert.Contains("\"HasSelfie\":true", json);
        Assert.DoesNotContain((await w.Db.AttendanceEvidence.SingleAsync()).StorageKey, json);

        // Purged: the flag goes false with it.
        var evidence = await w.Db.AttendanceEvidence.SingleAsync();
        await new SelfieEvidencePurger(w.Db, w.Storage).PurgeNowAsync(evidence, DateTime.UtcNow, "test", default);
        await w.Db.SaveChangesAsync();
        Assert.All((await hr.Raw(day, day, null, null, 1, 50, default)).Items, r => Assert.False(r.HasSelfie));
    }

    // ── Item 4: residency is read from storage, and a move switches the feature off ─────────────────────────

    private static StorageResidency Residency(string? bucketRegion, bool probeThrows = false, params string[] allowed) => new(new StorageOptions
    {
        Provider = "s3", Bucket = "b", Endpoint = SelfieWorld.KsaEndpoint, Region = "auto",
        ResidencyAllowList = new(StringComparer.OrdinalIgnoreCase) { ["KSA"] = allowed },
    }, _ => probeThrows ? throw new HttpRequestException("storage unreachable") : Task.FromResult(bucketRegion));

    [Fact]
    public async Task Item4_TheBucketsReportedRegion_MustBeListedToo_NotJustTheConfiguredEndpoint()
    {
        // The endpoint is listed, but the bucket says it lives elsewhere.
        var elsewhere = await Residency("eu-central-003", false, "s3.ksa-region.example.test", SelfieWorld.KsaBucketRegion).CheckAsync("KSA");
        Assert.False(elsewhere.Resident);
        Assert.Contains("reports region eu-central-003", elsewhere.Reason);

        var unreachable = await Residency(null, true, "s3.ksa-region.example.test", SelfieWorld.KsaBucketRegion).CheckAsync("KSA");
        Assert.False(unreachable.Resident);
        Assert.Contains("could not be read", unreachable.Reason);

        var resident = await Residency(SelfieWorld.KsaBucketRegion, false, "s3.ksa-region.example.test", SelfieWorld.KsaBucketRegion).CheckAsync("KSA");
        Assert.True(resident.Resident);
        Assert.Equal(SelfieWorld.ResidentKsaLocation, resident.Location);
    }

    [Fact]
    public async Task Item4_TheBucketRegion_IsReadOnceAndCached()
    {
        var calls = 0;
        var residency = new StorageResidency(new StorageOptions
        {
            Provider = "s3", Bucket = "b", Endpoint = SelfieWorld.KsaEndpoint,
            ResidencyAllowList = new(StringComparer.OrdinalIgnoreCase) { ["KSA"] = ["s3.ksa-region.example.test", SelfieWorld.KsaBucketRegion] },
        }, _ => { Interlocked.Increment(ref calls); return Task.FromResult<string?>(SelfieWorld.KsaBucketRegion); });

        for (var i = 0; i < 5; i++) Assert.True((await residency.CheckAsync("KSA")).Resident);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Item4_WhenStorageHasMovedSinceTheFlagWasEnabled_SelfieAttendanceIsOff()
    {
        var w = await SelfieWorld.CreateAsync();
        // Stamped when an Owner enabled it on a different bucket; storage now (still on the allow-list) is elsewhere.
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig(
            storageLocation: StorageResidency.Canonical("s3.ksa-region.example.test", "old-bucket", SelfieWorld.KsaBucketRegion)));

        var policy = await w.Verification.GetPolicyAsync(w.TenantId, default);

        Assert.False(policy.SelfieEnabled);
        Assert.Contains("storage has moved", policy.SelfieOffReason);
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig());
        Assert.True((await w.Verification.GetPolicyAsync(w.TenantId, default)).SelfieEnabled); // positive control
    }

    // ── Item 11: the platform rule, the audit and HR's report ───────────────────────────────────────────────

    [Fact]
    public async Task Item11_WithNoPlatformHeader_UnsupportedIsAcceptedAsBefore_AndTheAuditSaysThePlatformIsUnknown()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));
        c.Request.Headers.UserAgent = "KynexOne/1.0";

        var ok = await c.MobilePunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8, MockDetection: "Unsupported"), default);

        Assert.IsType<OkObjectResult>(ok.Result);
        var audit = await w.Db.AttendanceAuditLogs.SingleAsync(a => a.Action == AttendanceService.MockDetectionUnavailableAction);
        Assert.Contains("\"clientPlatform\":\"unknown\"", audit.MetadataJson);
    }

    [Fact]
    public async Task Item11_TheMockUnsupportedReport_CountsPerEmployee_WithinScope()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        var iphone = w.Attendance(user);
        iphone.Request.Headers["X-Client-Platform"] = "ios";
        Assert.IsType<OkObjectResult>((await iphone.MobilePunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8, MockDetection: "Unsupported"), default)).Result);
        var oldApp = w.Attendance(user);
        Assert.IsType<OkObjectResult>((await oldApp.MobilePunch(new WebPunchRequest(0, "Out", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8, MockDetection: "Unsupported"), default)).Result);
        var android = w.Attendance(await w.EmployeeAsync(w.Colleague, w.ColleagueUserId));
        android.Request.Headers["X-Client-Platform"] = "android";
        Assert.IsType<OkObjectResult>((await android.MobilePunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8, MockDetection: "Supported", LocationMocked: false), default)).Result);

        var hr = w.Attendance(await w.RoleAsync("HR Manager", w.Colleague, w.ColleagueUserId));
        var report = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await hr.GeofenceMockUnsupportedReport(null, null, default)).Value);

        Assert.Equal(1, report.GetProperty("count").GetInt32());
        Assert.Equal(2, report.GetProperty("totalPunches").GetInt32());
        var row = report.GetProperty("employees")[0];
        Assert.Equal(w.Caller.Id, row.GetProperty("employeeId").GetInt32());
        Assert.Equal(2, row.GetProperty("punches").GetInt32());
        Assert.Equal(1, row.GetProperty("iosPunches").GetInt32());
        Assert.Equal(1, row.GetProperty("unknownPlatformPunches").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(report.GetProperty("definition").GetString()));

        // An employee sees only their own scope: the colleague (plain Employee, own record only) sees nothing of the caller.
        var own = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
            await w.Attendance(await w.EmployeeAsync(w.Colleague, w.ColleagueUserId)).GeofenceMockUnsupportedReport(null, null, default)).Value);
        Assert.Equal(0, own.GetProperty("count").GetInt32());
    }

    // ── Items 2 and 3: cadence and capacity defaults ────────────────────────────────────────────────────────

    [Fact]
    public void Item2_ThePurgeRunsEvery15Minutes_OneJobPerTenantPerSlot()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), new SelfieEvidencePurgeOptions().ScheduleInterval);
        var t = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(SelfieEvidencePurgeJobHandler.IdempotencyKey(t), SelfieEvidencePurgeJobHandler.IdempotencyKey(t.AddMinutes(14)));
        Assert.NotEqual(SelfieEvidencePurgeJobHandler.IdempotencyKey(t), SelfieEvidencePurgeJobHandler.IdempotencyKey(t.AddMinutes(15)));
        Assert.NotEqual(SelfieEvidencePurgeJobHandler.IdempotencyKey(t.AddMinutes(30)), SelfieEvidencePurgeJobHandler.IdempotencyKey(t.AddMinutes(45)));
    }

    [Fact]
    public async Task Item3_OneImageSlotByDefault_AndABusyUploadWaitsBrieflyForIt()
    {
        Assert.Equal(1, SelfieImageGate.DefaultConcurrency);
        Assert.Equal("Selfie:ImageConcurrency", SelfieImageGate.ConfigKey);
        var gate = new SelfieImageGate();
        Assert.True(gate.TryEnter());
        var waiting = gate.EnterAsync(TimeSpan.FromSeconds(3), default);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        gate.Exit(); // the other decode finishes within the wait: this upload proceeds instead of answering busy
        Assert.True(await waiting);
    }
}
