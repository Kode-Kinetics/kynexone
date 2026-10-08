using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Review 2 of selfie attendance v2: the refusals and guarantees, through the real controllers with realistic callers
/// (an <c>employee_id</c> claim backed by an <c>EmployeeUserAccounts</c> row, the real DataScopeService, the seeded
/// permissions). Written only against types the reviewed backend (3ef5a2d4) already had, so this file also compiles
/// there — and each test FAILS there.
/// </summary>
public sealed class SelfieReview2RefusalTests
{
    // ── Item 1: the kiosk route is the kiosk channel only for a caller holding attendance.kiosk ─────────────────

    [Fact]
    public async Task Item1_APlainEssUserOnTheKioskRoute_GetsTheMobileRules_AndIsRefusedWithoutMockDetection()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var ess = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        // At the site, accuracy fine, but no mockDetection and no locationMocked: the kiosk rules let this through;
        // the mobile rules (which now apply) say the app must be updated.
        var refused = await ess.KioskPunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8), default);
        Assert.Equal("app_update_required", SelfieWorld.CodeOf(refused.Result));

        // No accuracy either: refused (location_inaccurate) rather than recorded.
        var noAccuracy = await ess.KioskPunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, MockDetection: "Supported", LocationMocked: false), default);
        Assert.Equal("location_inaccurate", SelfieWorld.CodeOf(noAccuracy.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    [Fact]
    public async Task Item1_APlainEssUserOnTheKioskRoute_IsAskedForTheRequiredSelfie_NotRecordedAsAKioskPunch()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig(requireSelfieForConsented: true));
        await SelfieAttendanceTests.ConsentAsync(w, w.Caller, w.CallerUserId);
        var ess = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));

        var refused = await ess.KioskPunch(new WebPunchRequest(0, "In", null, null, null), default);

        Assert.Equal("selfie_required", SelfieWorld.CodeOf(refused.Result));
        // And their own selfie IS accepted on that route now (old app builds call it for a self punch).
        var evidenceId = await SelfieAttendanceTests.UploadAsync(w);
        var ok = await ess.KioskPunch(new WebPunchRequest(0, "In", null, null, null, EvidenceId: evidenceId), default);
        Assert.Equal(AttendanceVerificationMethods.Selfie, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(ok.Result).Value).VerificationMethod);
    }

    [Fact]
    public async Task Item1_AKioskOnlyLogin_StillGetsTheKioskChannel()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var kiosk = w.Attendance(w.Principal("Kiosk Operator", w.CallerUserId, w.Caller.Id, ["attendance.kiosk"], "KioskOnly"));

        var ok = await kiosk.KioskPunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8), default);

        Assert.Equal(AttendanceVerificationMethods.None, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(ok.Result).Value).VerificationMethod);
    }

    // ── Item 2: the purge's queries cannot starve each other ─────────────────────────────────────────────────

    [Fact]
    public async Task Item2_ABacklogOf1200UsedSelfiesNotYetDue_DoesNotHoldBackAnUnusedOneOrAPendingOne()
    {
        var w = await SelfieWorld.CreateAsync();
        await SelfieAttendanceTests.EnableSelfieAsync(w);
        var now = DateTime.UtcNow;
        // 1,200 selfies used 100 days ago: no payroll run locked their month, so they are due only at 120 days.
        for (var i = 0; i < 1200; i++)
        {
            var usedAt = now.AddDays(-100).AddSeconds(-i);
            w.Db.AttendanceEvidence.Add(new AttendanceEvidence
            {
                TenantId = w.TenantId, EmployeeId = w.Caller.Id, StorageKey = $"storage/documents/{w.TenantId:N}/used{i}.jpg",
                Sha256 = new string('a', 64), ByteSize = 3, CreatedAtUtc = usedAt.AddMinutes(-1), ExpiresAtUtc = usedAt.AddMinutes(9),
                UsedAtUtc = usedAt, UsedByRawEventId = Guid.NewGuid(), PurgeState = AttendanceEvidencePurgeStates.Active,
            });
        }
        var unused = Evidence(w, w.Caller.Id, now.AddHours(-25), AttendanceEvidencePurgeStates.Active);
        var pending = Evidence(w, w.Caller.Id, now.AddHours(-2), AttendanceEvidencePurgeStates.Pending);
        await w.Db.SaveChangesAsync();

        var due = await new SelfieEvidencePurger(w.Db, w.Storage).FindDueAsync(w.TenantId, now, 1000, default);

        Assert.Contains(unused.Id, due);
        Assert.Contains(pending.Id, due);
        Assert.Equal(2, due.Count);
    }

    [Fact]
    public async Task Item2_AnUnusedSelfieOfAnEmployeeWithNoOpenConsent_IsDueAtOnce()
    {
        var w = await SelfieWorld.CreateAsync();
        await SelfieAttendanceTests.EnableSelfieAsync(w);
        var now = DateTime.UtcNow;
        // The colleague holds no open consent (a withdrawal whose own delete storage did not confirm).
        var orphan = Evidence(w, w.Colleague.Id, now.AddMinutes(-20), AttendanceEvidencePurgeStates.Active);
        // The caller still consents: their 20-minute-old unused selfie keeps its 24 hours.
        var kept = Evidence(w, w.Caller.Id, now.AddMinutes(-20), AttendanceEvidencePurgeStates.Active);
        await w.Db.SaveChangesAsync();

        var due = await new SelfieEvidencePurger(w.Db, w.Storage).FindDueAsync(w.TenantId, now, 1000, default);

        Assert.Equal([orphan.Id], due);
        Assert.DoesNotContain(kept.Id, due);
    }

    // ── Item 3: JPEG only, refused before any decode ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Item3_A16MegapixelPng_IsRefusedBeforeDecoding_AndTheAttemptDoesNotCount()
    {
        var w = await SelfieWorld.CreateAsync();
        var world = WithGate(w, new SelfieImageGate(concurrency: 1));
        await SelfieAttendanceTests.EnableSelfieAsync(world);
        var user = await world.EmployeeAsync(w.Caller, w.CallerUserId);
        // Every image slot is taken: an upload that reached the decode stage could only answer "busy". A refusal of
        // the PNG itself proves it never got that far.
        Assert.True(world.Gate.TryEnter());

        var png = SolidPng(4000, 4000); // exactly 16 MP: within the canvas cap, 64 MB of pixels if decoded
        var refused = await world.UploadAsync(user, png, "image/png", "s.png");

        Assert.Equal("selfie_invalid", SelfieWorld.CodeOf(refused));
        // Labelled as a JPEG it is still a PNG: the magic bytes decide, not the Content-Type.
        Assert.Equal("selfie_invalid", SelfieWorld.CodeOf(await world.UploadAsync(user, png, "image/jpeg", "s.jpg")));
        Assert.Empty(w.Db.AttendanceEvidence);
        Assert.Empty(w.Storage.Objects);
        world.Gate.Exit();
    }

    // ── Item 6: consent re-checked before activation; a failed delete leaves the row Pending ──────────────────

    [Fact]
    public async Task Item6_ConsentWithdrawnWhileTheFileUploads_TheSelfieIsNotActivated_AndTheFileIsDeleted()
    {
        var w = await SelfieWorld.CreateAsync();
        await SelfieAttendanceTests.EnableSelfieAsync(w);
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        var storage = new HookedStorage(w.Storage)
        {
            // The withdrawal lands between the reservation's consent check and the activation.
            OnPut = async () =>
            {
                foreach (var c in await w.Db.BiometricConsents.Where(c => c.EmployeeId == w.Caller.Id && c.WithdrawnAtUtc == null).ToListAsync())
                    c.WithdrawnAtUtc = DateTime.UtcNow;
                await w.Db.SaveChangesAsync();
            },
        };

        var result = await UploadThroughAsync(w, storage, user, SelfieAttendanceTests.SelfieJpeg());

        Assert.Equal("consent_required", SelfieWorld.CodeOf(result));
        var row = await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync();
        Assert.Equal(AttendanceEvidencePurgeStates.Purged, row.PurgeState);
        Assert.Empty(w.Storage.Objects);
    }

    [Fact]
    public async Task Item6_AWithdrawalDuringTheUpload_WhoseDeleteFails_LeavesTheRowPending_NeverActive()
    {
        var w = await SelfieWorld.CreateAsync();
        await SelfieAttendanceTests.EnableSelfieAsync(w);
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        var storage = new HookedStorage(w.Storage)
        {
            OnPut = async () =>
            {
                // Storage stops confirming deletes, then the employee withdraws through Self-Service.
                w.Storage.FailDeletes = true;
                await w.Ess(user).WithdrawConsent(null, default);
            },
        };

        var result = await UploadThroughAsync(w, storage, user, SelfieAttendanceTests.SelfieJpeg());

        Assert.Equal("consent_required", SelfieWorld.CodeOf(result));
        var row = await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync();
        // Pending, so the 1-hour sweeper retries the delete; never Active (usable without consent), never Purged (a lie).
        Assert.Equal(AttendanceEvidencePurgeStates.Pending, row.PurgeState);
        Assert.True(w.Storage.Objects.ContainsKey(row.StorageKey), "the file is still there, and the Pending row is what gets it deleted");
        w.Storage.FailDeletes = false;
        Assert.Equal([row.Id], await new SelfieEvidencePurger(w.Db, w.Storage).FindDueAsync(w.TenantId, row.CreatedAtUtc.AddMinutes(61), 100, default));
    }

    // ── Item 7: busy and failed attempts do not burn the quota; the server failing never blocks attendance ─────

    [Fact]
    public async Task Item7_ABusyAnswer_LeavesNoCountableAttemptBehind()
    {
        var w = await SelfieWorld.CreateAsync();
        var world = WithGate(w, new SelfieImageGate(concurrency: 1));
        await SelfieAttendanceTests.EnableSelfieAsync(world);
        var user = await world.EmployeeAsync(w.Caller, w.CallerUserId);
        Assert.True(world.Gate.TryEnter());

        var busy = await world.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg());

        Assert.Equal("selfie_busy", SelfieWorld.CodeOf(busy));
        // Review 3: the attempt is kept as its own server-failure record (the waiver), closed Purged with no file, so
        // it is neither usable nor in flight, and it does not count toward the hourly limit.
        var row = Assert.Single(w.Db.AttendanceEvidence);
        Assert.Equal(AttendanceEvidencePurgeStates.Purged, row.PurgeState);
        Assert.Equal(SelfieUploadFailureReasons.Busy, row.FailedReason);
        Assert.Empty(w.Storage.Objects);
        world.Gate.Exit();
    }

    [Fact]
    public async Task Item7_WhenTheServerWasBusy_ARequiredSelfieIsWaivedForOnePunch_RecordedNoneAndAudited()
    {
        var w = await SelfieWorld.CreateAsync();
        var world = WithGate(w, new SelfieImageGate(concurrency: 1));
        await world.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig(requireSelfieForConsented: true));
        await SelfieAttendanceTests.ConsentAsync(world, w.Caller, w.CallerUserId);
        var user = await world.EmployeeAsync(w.Caller, w.CallerUserId);
        Assert.True(world.Gate.TryEnter());
        Assert.Equal("selfie_busy", SelfieWorld.CodeOf(await world.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg())));
        world.Gate.Exit();

        var punch = await world.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);

        var raw = Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(punch.Result).Value);
        Assert.Equal(AttendanceVerificationMethods.None, raw.VerificationMethod);
        var waiver = await w.Db.AttendanceAuditLogs.SingleAsync(a => a.Action == "attendance.selfie.requirement_waived");
        Assert.Contains("busy", waiver.MetadataJson);
        Assert.Contains(raw.Id.ToString(), waiver.MetadataJson);
        // One failure waives one punch.
        var next = await world.Attendance(user).MobilePunch(new WebPunchRequest(0, "Out", null, null, null), default);
        Assert.Equal("selfie_required", SelfieWorld.CodeOf(next.Result));
    }

    [Fact]
    public async Task Item7_WhenStorageFailed_TheUploadSays503_AndARequiredSelfieIsWaivedForThePunch()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig(requireSelfieForConsented: true));
        await SelfieAttendanceTests.ConsentAsync(w, w.Caller, w.CallerUserId);
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        w.Storage.FailPuts = true;

        var failed = await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg());

        Assert.Equal(503, Assert.IsAssignableFrom<ObjectResult>(failed).StatusCode);
        Assert.Equal("selfie_storage_unavailable", SelfieWorld.CodeOf(failed));
        Assert.True(JsonSerializer.SerializeToElement(((ObjectResult)failed).Value).GetProperty("punchWithoutSelfie").GetBoolean());
        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        Assert.Equal(AttendanceVerificationMethods.None, Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(punch.Result).Value).VerificationMethod);
    }

    // ── Item 11: "Unsupported" from an Android phone is refused ───────────────────────────────────────────────

    [Theory]
    [InlineData("android", "KynexOne/2.1")]
    [InlineData("", "okhttp/4.12.0")]
    [InlineData("ios", "Mozilla/5.0 (Linux; Android 14; Pixel 8)")]
    public async Task Item11_MockDetectionUnsupported_FromAnAndroidPhone_IsRefused(string platformHeader, string userAgent)
    {
        var w = await SelfieWorld.CreateAsync();
        await w.AddSiteAsync();
        await w.EnforceGeofenceAsync();
        var c = w.Attendance(await w.EmployeeAsync(w.Caller, w.CallerUserId));
        if (platformHeader.Length > 0) c.Request.Headers["X-Client-Platform"] = platformHeader;
        c.Request.Headers.UserAgent = userAgent;

        var refused = await c.MobilePunch(new WebPunchRequest(0, "In", null, SelfieWorld.SiteLat, SelfieWorld.SiteLon, AccuracyMeters: 8, MockDetection: "Unsupported"), default);

        Assert.Equal("mock_detection_required", SelfieWorld.CodeOf(refused.Result));
        Assert.Empty(w.Db.AttendanceRawEvents);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private static AttendanceEvidence Evidence(SelfieWorld w, int employeeId, DateTime createdAt, string state)
    {
        var row = new AttendanceEvidence
        {
            TenantId = w.TenantId, EmployeeId = employeeId, StorageKey = $"storage/documents/{w.TenantId:N}/{Guid.NewGuid():N}.jpg",
            Sha256 = state == AttendanceEvidencePurgeStates.Active ? new string('b', 64) : null,
            ByteSize = state == AttendanceEvidencePurgeStates.Active ? 3 : null,
            CreatedAtUtc = createdAt, ExpiresAtUtc = createdAt.AddMinutes(10), PurgeState = state,
        };
        w.Db.AttendanceEvidence.Add(row);
        return row;
    }

    private static SelfieWorld WithGate(SelfieWorld w, SelfieImageGate gate) => new()
    {
        Db = w.Db, TenantId = w.TenantId, CompanyId = w.CompanyId, CallerUserId = w.CallerUserId, ColleagueUserId = w.ColleagueUserId,
        Caller = w.Caller, Colleague = w.Colleague, Storage = w.Storage, Gate = gate,
    };

    private static Task<IActionResult> UploadThroughAsync(SelfieWorld w, IDocumentStorage storage, System.Security.Claims.ClaimsPrincipal user, byte[] bytes)
    {
        var controller = w.With(new AttendanceEvidenceController(w.Db, storage, w.Verification, w.Gate), user);
        controller.Request.ContentType = "multipart/form-data; boundary=x";
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "selfie.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };
        controller.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(), new FormFileCollection { file });
        return controller.UploadSelfie();
    }

    internal static byte[] SolidPng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(new SKColor(200, 160, 140));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>The memory store with a hook that runs while the file is being written (between reservation and activation).</summary>
    private sealed class HookedStorage(MemoryDocumentStorage inner) : IDocumentStorage
    {
        public Func<Task>? OnPut { get; init; }
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) => inner.SaveAsync(tenantId, file, cancellationToken);
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => inner.GetBytesAsync(tenantId, storageUrl, ct);
        public string ResolvePath(string storageUrl) => inner.ResolvePath(storageUrl);
        public Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => inner.TryDeleteAsync(tenantId, storageUrl, ct);
        public string TenantKey(Guid tenantId, string relativeName) => inner.TenantKey(tenantId, relativeName);
        public Task DeleteStrictAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => inner.DeleteStrictAsync(tenantId, storageUrl, ct);

        public async Task PutAtAsync(Guid tenantId, string storageKey, byte[] content, string contentType, CancellationToken ct = default)
        {
            if (OnPut is not null) await OnPut();
            await inner.PutAtAsync(tenantId, storageKey, content, contentType, ct);
        }
    }
}
