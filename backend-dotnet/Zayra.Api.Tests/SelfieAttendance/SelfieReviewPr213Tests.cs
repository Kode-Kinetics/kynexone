using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// PR #213 review, selfie items 3 and 7: an attempt whose storage write was given up is never removed (a late write
/// could land after it), and every closed-as-failed attempt carries a non-waivable reason, so it stops counting as "in
/// flight" and as an hourly attempt where it never completed. The reasons are written as strings, not the new constants,
/// so this file compiles against the reviewed head (e83c1673) and its tests fail there.
/// </summary>
public sealed class SelfieReviewPr213Tests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

    // ── Item 3: a timed-out write keeps its row, and the sweeper deletes the file once the late write has landed ───

    [Fact]
    public async Task Item3_ATimedOutWrite_KeepsItsRowPendingAsTimeout_SoTheSweeperDeletesTheFileALateWriteLands()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        var storage = new LateLandingStorage(w.Storage);
        var controller = Shortened(w.With(new AttendanceEvidenceController(w.Db, storage, w.Verification, w.Gate), user));
        SetForm(controller, SelfieAttendanceTests.SelfieJpeg());

        var result = await controller.UploadSelfie().WaitAsync(Watchdog);

        Assert.Equal(408, (result as ObjectResult)?.StatusCode);
        Assert.Equal("selfie_upload_timeout", SelfieWorld.CodeOf(result));
        Assert.False(PunchWithoutSelfie(result));
        var row = await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync();      // never removed
        Assert.Equal(AttendanceEvidencePurgeStates.Pending, row.PurgeState);
        Assert.Equal("Timeout", row.FailedReason);                                  // non-waivable

        await storage.Landed.WaitAsync(Watchdog);                                   // the given-up write lands late
        Assert.True(w.Storage.Objects.ContainsKey(row.StorageKey));

        // It is no longer "in flight", and it is not a waiver.
        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        Assert.Equal("selfie_required", SelfieWorld.CodeOf(punch.Result));
        Assert.Equal(201, (await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg()) as ObjectResult)?.StatusCode);

        // An hour on, the sweeper strictly deletes the late file and keeps the row.
        var later = row.CreatedAtUtc.AddMinutes(61);
        var purger = new SelfieEvidencePurger(w.Db, w.Storage);
        Assert.Contains(row.Id, await purger.FindDueAsync(w.TenantId, later, 100, default));
        await purger.PurgeOneAsync(w.TenantId, row.Id, later, null, default);
        await w.Db.SaveChangesAsync();
        Assert.False(w.Storage.Objects.ContainsKey(row.StorageKey));
        Assert.Equal(AttendanceEvidencePurgeStates.Purged, (await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync(e => e.Id == row.Id)).PurgeState);
    }

    [Fact]
    public async Task Item3_ATimeoutRow_DoesNotCountTowardTheHourlyLimit()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        for (var i = 0; i < 9; i++) w.Db.AttendanceEvidence.Add(Stored(w));
        w.Db.AttendanceEvidence.Add(Pending(w, minutesAgo: 5, failedReason: "Timeout"));
        await w.Db.SaveChangesAsync();

        Assert.Equal(201, (await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg()) as ObjectResult)?.StatusCode);
    }

    /// <summary>Narrow verification of #213, item 4: an Aborted attempt (the client went away) DOES count toward the hourly limit.</summary>
    [Fact]
    public async Task Item4_AnAbortedRow_CountsTowardTheHourlyLimit()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        for (var i = 0; i < 9; i++) w.Db.AttendanceEvidence.Add(Stored(w));
        w.Db.AttendanceEvidence.Add(Pending(w, minutesAgo: 5, failedReason: "Aborted"));
        await w.Db.SaveChangesAsync();

        var result = await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg());

        Assert.Equal(429, (result as ObjectResult)?.StatusCode);
        Assert.Equal("selfie_rate_limited", SelfieWorld.CodeOf(result));
    }

    // ── Item 7: closed-as-failed attempts stop counting as in flight ────────────────────────────────────────

    [Fact]
    public async Task Item7_AClientAbortDuringTheWrite_ClosesTheAttemptAsAborted_SoTheNextUploadIsNotBlocked()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        var storage = new BlockingDocumentStorage();
        var controller = w.With(new AttendanceEvidenceController(w.Db, storage, w.Verification, w.Gate), user);
        using var client = new CancellationTokenSource();
        controller.HttpContext.RequestAborted = client.Token;
        SetForm(controller, SelfieAttendanceTests.SelfieJpeg());

        var upload = controller.UploadSelfie();
        await storage.WriteStarted.WaitAsync(Watchdog);
        client.Cancel();                                                           // the phone goes away mid-write
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload.WaitAsync(Watchdog));

        var row = await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync();
        Assert.Equal(AttendanceEvidencePurgeStates.Pending, row.PurgeState);       // the sweeper's
        Assert.Equal("Aborted", row.FailedReason);
        // Not "in flight": a retry straight away is accepted, not 409 selfie_upload_in_progress.
        Assert.Equal(201, (await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg()) as ObjectResult)?.StatusCode);
    }

    [Fact]
    public async Task Item7_AStorageFailureDeniedAWaiver_IsClosedAsDeniedFailure_AndDoesNotBlockALaterWaiver()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        // An unfinished attempt from 5 minutes ago: the first storage failure is refused a waiver because of it.
        var stalled = Pending(w, minutesAgo: 5, failedReason: null);
        w.Db.AttendanceEvidence.Add(stalled);
        await w.Db.SaveChangesAsync();
        w.Storage.FailPuts = true;
        var denied = await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg());
        Assert.Equal(503, (denied as ObjectResult)?.StatusCode);
        Assert.False(PunchWithoutSelfie(denied));
        Assert.Equal("DeniedFailure", (await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync(e => e.Id != stalled.Id)).FailedReason);

        // The stalled attempt is swept; now a storage failure with nothing else unfinished IS a waiver — the denied
        // failure no longer counts as in flight.
        w.Db.AttendanceEvidence.Remove(await w.Db.AttendanceEvidence.SingleAsync(e => e.Id == stalled.Id));
        await w.Db.SaveChangesAsync();
        var waived = await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg());
        w.Storage.FailPuts = false;

        Assert.Equal(503, (waived as ObjectResult)?.StatusCode);
        Assert.True(PunchWithoutSelfie(waived));
        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        Assert.IsType<OkObjectResult>(punch.Result);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<SelfieWorld> RequiredAsync()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig(requireSelfieForConsented: true));
        await SelfieAttendanceTests.ConsentAsync(w, w.Caller, w.CallerUserId);
        return w;
    }

    private static AttendanceEvidence Stored(SelfieWorld w) => new()
    {
        TenantId = w.TenantId, EmployeeId = w.Caller.Id, StorageKey = w.Storage.TenantKey(w.TenantId, $"attendance-evidence/{Guid.NewGuid():N}.jpg"),
        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-10), PurgeState = AttendanceEvidencePurgeStates.Purged,
        PurgedAtUtc = DateTime.UtcNow.AddMinutes(-5), Sha256 = new string('a', 64), ByteSize = 10,
    };

    private static AttendanceEvidence Pending(SelfieWorld w, int minutesAgo, string? failedReason) => new()
    {
        TenantId = w.TenantId, EmployeeId = w.Caller.Id, StorageKey = w.Storage.TenantKey(w.TenantId, $"attendance-evidence/{Guid.NewGuid():N}.jpg"),
        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10 - minutesAgo),
        PurgeState = AttendanceEvidencePurgeStates.Pending, FailedReason = failedReason,
    };

    private static AttendanceEvidenceController Shortened(AttendanceEvidenceController controller)
    {
        typeof(AttendanceEvidenceController)
            .GetProperty("Deadline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .SetValue(controller, TimeSpan.FromSeconds(1));
        return controller;
    }

    private static void SetForm(AttendanceEvidenceController controller, byte[] bytes)
    {
        controller.Request.ContentType = "multipart/form-data; boundary=x";
        controller.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(),
            new FormFileCollection { new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "s.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" } });
    }

    private static bool PunchWithoutSelfie(IActionResult result) =>
        result is ObjectResult { Value: { } v } && JsonSerializer.SerializeToElement(v) is { ValueKind: JsonValueKind.Object } body
        && body.TryGetProperty("punchWithoutSelfie", out var p) && p.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Storage whose write hangs until its token is cancelled — and then LANDS anyway, a moment later (the bytes had
    /// already reached the store), the way an S3 PUT can complete after the client stopped waiting.
    /// </summary>
    private sealed class LateLandingStorage(MemoryDocumentStorage inner) : IDocumentStorage
    {
        private readonly TaskCompletionSource _landed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Landed => _landed.Task;
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) => inner.SaveAsync(tenantId, file, cancellationToken);
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => inner.GetBytesAsync(tenantId, storageUrl, ct);
        public string ResolvePath(string storageUrl) => storageUrl;
        public Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => inner.TryDeleteAsync(tenantId, storageUrl, ct);
        public string TenantKey(Guid tenantId, string relativeName) => inner.TenantKey(tenantId, relativeName);
        public Task DeleteStrictAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => inner.DeleteStrictAsync(tenantId, storageUrl, ct);

        public async Task PutAtAsync(Guid tenantId, string storageKey, byte[] content, string contentType, CancellationToken ct = default)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200);
                    await inner.PutAtAsync(tenantId, storageKey, content, contentType);
                    _landed.TrySetResult();
                });
            }
        }
    }
}
