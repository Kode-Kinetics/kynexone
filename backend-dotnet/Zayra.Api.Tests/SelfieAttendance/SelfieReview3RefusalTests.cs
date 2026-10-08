using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Selfie attendance v2, third review: the refusals and guarantees that must hold, through the real controllers.
/// Written ONLY against types and members the reviewed head (45ed1c9e) already had — the review-3 columns and helpers
/// are never named — so this file compiles against that head's production code and its tests fail there.
/// </summary>
public sealed class SelfieReview3RefusalTests
{
    // ── Items 1–3: the server-failure waiver ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item1_AnUploadWhileAnotherIsInFlight_Is409InProgress_AndWaivesNothing()
    {
        var w = await RequiredSelfieWorldAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        // Another upload of the same employee reserved its attempt 5 seconds ago and is still writing.
        w.Db.AttendanceEvidence.Add(new AttendanceEvidence
        {
            TenantId = w.TenantId, EmployeeId = w.Caller.Id, StorageKey = $"storage/documents/{w.TenantId:N}/attendance-evidence/{Guid.NewGuid():N}.jpg",
            CreatedAtUtc = DateTime.UtcNow.AddSeconds(-5), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10), PurgeState = AttendanceEvidencePurgeStates.Pending,
        });
        await w.Db.SaveChangesAsync();

        var second = await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg());

        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(second).StatusCode);
        Assert.Equal("selfie_upload_in_progress", SelfieWorld.CodeOf(second));
        Assert.False(PunchWithoutSelfie(second));
        Assert.Empty(w.Storage.Objects);
        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        Assert.Equal("selfie_required", SelfieWorld.CodeOf(punch.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("storage")]
    public async Task Item2_AServerFailureThenASuccessfulUpload_LeavesNoWaiver(string failure)
    {
        var w = await RequiredSelfieWorldAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        if (failure == "busy") Assert.True(w.Gate.TryEnter()); else w.Storage.FailPuts = true;
        var failed = await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg());
        Assert.True(PunchWithoutSelfie(failed), "the failure itself does offer a punch without a selfie");
        if (failure == "busy") w.Gate.Exit(); else w.Storage.FailPuts = false;

        // The employee could take a selfie after all.
        var stored = await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg());
        Assert.Equal(201, Assert.IsAssignableFrom<ObjectResult>(stored).StatusCode);

        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);

        Assert.Equal("selfie_required", SelfieWorld.CodeOf(punch.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Item3_TheDailyCapHolds_AThirdWaiverInADayIsRefused_WithThePlainMessage()
    {
        var w = await RequiredSelfieWorldAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        string[] directions = ["In", "Out", "In"];
        var outcomes = new List<ActionResult<AttendanceRawEvent>>();
        var offers = new List<bool>();
        foreach (var direction in directions)
        {
            w.Storage.FailPuts = true;
            var failed = await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg());
            Assert.Equal(503, Assert.IsAssignableFrom<ObjectResult>(failed).StatusCode);
            offers.Add(PunchWithoutSelfie(failed));
            w.Storage.FailPuts = false;
            outcomes.Add(await w.Attendance(user).MobilePunch(new WebPunchRequest(0, direction, null, null, null), default));
        }

        Assert.All(outcomes.Take(2), o => Assert.Equal(AttendanceVerificationMethods.None,
            Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(o.Result).Value).VerificationMethod));
        Assert.Equal("selfie_required", SelfieWorld.CodeOf(outcomes[2].Result));
        Assert.Equal("Please try the selfie again in a moment.", SelfieWorld.MessageOf(outcomes[2].Result));
        Assert.Equal(2, await w.Db.AttendanceRawEvents.CountAsync());
        // The app is told not to offer "Clock without a selfie" once the cap is used up.
        Assert.Equal([true, true, false], offers);
    }

    // ── Item 4: HR and Admins self-punching on the kiosk route get the mobile rules ─────────────────────────

    [Theory]
    [InlineData("HR Manager")]
    [InlineData("Admin")]
    public async Task Item4_AnHrManagerOrAdmin_SelfPunchingOnTheKioskRoute_GetsTheMobileGeofenceRules(string role)
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var staff = await w.RoleAsync(role, w.Caller, w.CallerUserId);
        Assert.Contains(staff.Claims, c => c.Type == "permission" && c.Value == "attendance.kiosk");

        // Inside the site, accurate — but without mockDetection: on the kiosk channel that passed; on mobile it may not.
        var punch = await w.Attendance(staff).KioskPunch(
            new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8), default);

        Assert.Equal("app_update_required", SelfieWorld.CodeOf(punch.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Theory]
    [InlineData("HR Manager")]
    [InlineData("Admin")]
    public async Task Item4_AnHrManagerOrAdmin_SelfPunchingOnTheKioskRoute_IsAskedForTheRequiredSelfie(string role)
    {
        var w = await RequiredSelfieWorldAsync();
        var staff = await w.RoleAsync(role, w.Caller, w.CallerUserId);

        var punch = await w.Attendance(staff).KioskPunch(new WebPunchRequest(0, "In", null, null, null), default);

        Assert.Equal("selfie_required", SelfieWorld.CodeOf(punch.Result));
    }

    [Fact]
    public async Task Item4_AKioskOnlySignIn_StillGetsTheKioskChannel_AndAnHrOnBehalfKioskPunchToo()
    {
        var w = await RequiredSelfieWorldAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var kiosk = w.Principal("Employee", w.CallerUserId, w.Caller.Id, ["attendance.kiosk"], AccessModes.KioskOnly);

        var own = await w.Attendance(kiosk).KioskPunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8), default);
        Assert.Equal(AttendanceVerificationMethods.None, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(own.Result).Value).VerificationMethod);

        var hr = await w.RoleAsync("HR Manager", w.Colleague, w.ColleagueUserId);
        var onBehalf = await w.Attendance(hr).KioskPunch(
            new WebPunchRequest(w.Caller.Id, "Out", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8), default);
        Assert.Equal(AttendanceVerificationMethods.None, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(onBehalf.Result).Value).VerificationMethod);
    }

    // ── Item 6: the residency probe cannot hang ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Item6_ABucketRegionProbeThatNeverAnswers_TimesOut_AndResidencyIsNotConfirmed()
    {
        var never = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var residency = new StorageResidency(new StorageOptions
        {
            Provider = "s3", Bucket = "b", Endpoint = SelfieWorld.KsaEndpoint, Region = "auto",
            ResidencyAllowList = new(StringComparer.OrdinalIgnoreCase) { ["KSA"] = ["s3.ksa-region.example.test", SelfieWorld.KsaBucketRegion] },
        }, _ => never.Task); // ignores its token too: only a timeout of our own can end it

        var started = DateTime.UtcNow;
        var verdict = await residency.CheckAsync("KSA").WaitAsync(TimeSpan.FromSeconds(15));

        Assert.False(verdict.Resident);
        Assert.Contains("did not answer within 3 seconds", verdict.Reason);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(10));
    }

    // ── Item 9: a withdrawal does not mark an upload in flight Purged ─────────────────────────────────────

    [Fact]
    public async Task Item9_AWithdrawal_LeavesAPendingRowYoungerThanTwoMinutesPending_AndPurgesAnOlderOne()
    {
        var w = await SelfieWorld.CreateAsync();
        await SelfieAttendanceTests.EnableSelfieAsync(w);
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        var inFlight = Pending(w, DateTime.UtcNow.AddSeconds(-20));
        var stale = Pending(w, DateTime.UtcNow.AddMinutes(-3));
        await w.Db.SaveChangesAsync();

        var result = await w.Ess(user).WithdrawConsent(null, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(AttendanceEvidencePurgeStates.Pending, (await w.Db.AttendanceEvidence.SingleAsync(e => e.Id == inFlight.Id)).PurgeState);
        Assert.DoesNotContain(inFlight.StorageKey, w.Storage.Deleted);
        Assert.Equal(AttendanceEvidencePurgeStates.Purged, (await w.Db.AttendanceEvidence.SingleAsync(e => e.Id == stale.Id)).PurgeState);
        var withdrawal = JsonSerializer.SerializeToElement(((OkObjectResult)result).Value).GetProperty("withdrawal");
        Assert.Equal(1, withdrawal.GetProperty("unusedSelfiesDeleted").GetInt32());
        Assert.Equal(1, withdrawal.GetProperty("unusedSelfiesAwaitingDeletion").GetInt32());
    }

    // ── Item 10: Admin does not open selfies by default; refused views are audited ─────────────────────────

    [Fact]
    public async Task Item10_TheSeededAdminRole_DoesNotHoldTheSelfieViewPermission_WhileHrDoes()
    {
        Assert.DoesNotContain("attendance.evidence.view", await SelfieWorld.SeededAsync("Admin"));
        Assert.Contains("attendance.evidence.view", await SelfieWorld.SeededAsync("HR Manager"));
        Assert.Contains("attendance.evidence.view", await SelfieWorld.SeededAsync("HR Director"));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR Officer")]
    public async Task Item10_ACallerWithoutThePermission_IsRefused_AndTheRefusalIsAudited(string role)
    {
        var w = await SelfieWorld.CreateAsync();
        var rawId = await ColleaguePunchesWithASelfieAsync(w);
        var caller = await w.RoleAsync(role, w.Caller, w.CallerUserId);

        var result = await w.Evidence(caller).ViewSelfie(rawId, default);

        Assert.Equal(403, Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult>(result).StatusCode);
        Assert.IsNotType<FileContentResult>(result);
        AssertRefusedAudit(w, rawId, "missing_permission");
        Assert.False(await w.Db.AttendanceAuditLogs.AnyAsync(a => a.Action == "attendance.selfie.viewed"));
    }

    [Fact]
    public async Task Item10_ARefusalForScopeOrOwnRecord_IsAuditedWithTheReason()
    {
        var w = await SelfieWorld.CreateAsync();
        var rawId = await ColleaguePunchesWithASelfieAsync(w);
        var outside = w.Principal("Manager", w.CallerUserId, w.Caller.Id,
            (await SelfieWorld.SeededAsync("Manager")).Append("attendance.evidence.view"), null);
        Assert.IsType<NotFoundObjectResult>(await w.Evidence(outside).ViewSelfie(rawId, default));
        AssertRefusedAudit(w, rawId, "outside_scope");

        var self = await w.RoleAsync("HR Manager", w.Colleague, w.ColleagueUserId);
        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(await w.Evidence(self).ViewSelfie(rawId, default)).StatusCode);
        AssertRefusedAudit(w, rawId, "own_record");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Selfie attendance on, required for consenting employees, and the caller consents.</summary>
    private static async Task<SelfieWorld> RequiredSelfieWorldAsync()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig(requireSelfieForConsented: true));
        await SelfieAttendanceTests.ConsentAsync(w, w.Caller, w.CallerUserId);
        return w;
    }

    private static bool PunchWithoutSelfie(IActionResult result) =>
        result is ObjectResult { Value: { } body }
        && JsonSerializer.SerializeToElement(body).TryGetProperty("punchWithoutSelfie", out var p) && p.ValueKind == JsonValueKind.True;

    private static AttendanceEvidence Pending(SelfieWorld w, DateTime createdAt)
    {
        var row = new AttendanceEvidence
        {
            TenantId = w.TenantId, EmployeeId = w.Caller.Id, StorageKey = $"storage/documents/{w.TenantId:N}/attendance-evidence/{Guid.NewGuid():N}.jpg",
            CreatedAtUtc = createdAt, ExpiresAtUtc = createdAt.AddMinutes(10), PurgeState = AttendanceEvidencePurgeStates.Pending,
        };
        w.Db.AttendanceEvidence.Add(row);
        return row;
    }

    /// <summary>The colleague consents, uploads a selfie and punches with it; returns the punch id.</summary>
    internal static async Task<Guid> ColleaguePunchesWithASelfieAsync(SelfieWorld w)
    {
        await SelfieAttendanceTests.EnableSelfieAsync(w);
        await SelfieAttendanceTests.ConsentAsync(w, w.Colleague, w.ColleagueUserId);
        var evidenceId = await SelfieAttendanceTests.UploadAsync(w, w.Colleague, w.ColleagueUserId);
        var punch = await w.Attendance(await w.EmployeeAsync(w.Colleague, w.ColleagueUserId))
            .MobilePunch(new WebPunchRequest(0, "In", null, null, null, EvidenceId: evidenceId), default);
        return Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(punch.Result).Value).Id;
    }

    private static void AssertRefusedAudit(SelfieWorld w, Guid rawId, string reason)
    {
        var rows = w.Db.AttendanceAuditLogs.Where(a => a.Action == "attendance.selfie.view_refused" && a.EntityId == rawId.ToString()).ToList();
        var row = Assert.Single(rows, r => JsonSerializer.Deserialize<JsonElement>(r.MetadataJson!).GetProperty("reason").GetString() == reason);
        var metadata = JsonSerializer.Deserialize<JsonElement>(row.MetadataJson!);
        Assert.NotNull(row.UserId); // who
        Assert.Equal(rawId.ToString(), metadata.GetProperty("rawEventId").GetString());
    }
}
