using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
/// The hardening follow-up to PR #212 (selfie attendance v2): the upload's server deadline, a network drop during the
/// upload, the waiver's in-flight lookback, re-consent during an upload, the residency probe under load, and the legacy
/// route. Written only against types the #212 head (e835077f) already has — the shortened test deadline is set by
/// reflection, because the property is new — so this file compiles there and its refusal tests fail there.
/// </summary>
public sealed class SelfieHardeningRefusalTests
{
    /// <summary>The whole test's limit: the old code has no deadline, so a stalled upload would hang forever.</summary>
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

    // ── Item 1: the server deadline ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item1_ADripFedBodyThatStalls_IsCutOffAtTheDeadline_408_AndTheAttemptIsReleased()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        var controller = Shortened(w.Evidence(user));
        controller.Request.ContentType = "multipart/form-data; boundary=x";
        controller.Request.Body = new ScriptedBody(MultipartPrefix(SelfieAttendanceTests.SelfieJpeg().Take(40).ToArray()), stallAtEnd: true);

        var result = await controller.UploadSelfie().WaitAsync(Watchdog);

        Assert.Equal(408, (result as ObjectResult)?.StatusCode);
        Assert.Equal("selfie_upload_timeout", SelfieWorld.CodeOf(result));
        Assert.False(PunchWithoutSelfie(result));
        Assert.Empty(await w.Db.AttendanceEvidence.ToListAsync()); // released: it does not count, and it is not a waiver
        Assert.False(await w.Db.AttendanceAuditLogs.AnyAsync(a => a.Action == AttendanceVerificationService.SelfieServerFailureAction));
        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        Assert.Equal("selfie_required", SelfieWorld.CodeOf(punch.Result));
        // And the next upload is not blocked as "in flight".
        Assert.Equal(201, (await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg()) as ObjectResult)?.StatusCode);
    }

    [Theory]
    [InlineData(SelfieUploadFailureReasons.Busy)]
    [InlineData(SelfieUploadFailureReasons.Storage)]
    public async Task Item1_AServerFailure_WaivesNothing_WhileAnOlderPendingAttemptOfTheEmployeeExists(string reason)
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        // An earlier attempt reserved 5 minutes ago and never finished: past the 60 s in which it blocks a new upload, but
        // not closed by the 1-hour sweeper. A failure beside it is no evidence the server alone failed the employee.
        var stalled = new AttendanceEvidence
        {
            TenantId = w.TenantId, EmployeeId = w.Caller.Id, StorageKey = w.Storage.TenantKey(w.TenantId, $"attendance-evidence/{Guid.NewGuid():N}.jpg"),
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5), PurgeState = AttendanceEvidencePurgeStates.Pending,
        };
        w.Db.AttendanceEvidence.Add(stalled);
        await w.Db.SaveChangesAsync();

        IActionResult failed;
        if (reason == SelfieUploadFailureReasons.Busy)
        {
            Assert.True(w.Gate.TryEnter());
            try { failed = await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg()); }
            finally { w.Gate.Exit(); }
            Assert.Equal(429, (failed as ObjectResult)?.StatusCode);
        }
        else
        {
            w.Storage.FailPuts = true;
            failed = await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg());
            w.Storage.FailPuts = false;
            Assert.Equal(503, (failed as ObjectResult)?.StatusCode);
        }

        Assert.False(PunchWithoutSelfie(failed));
        Assert.All(await w.Db.AttendanceEvidence.ToListAsync(), e => Assert.DoesNotContain(e.FailedReason, SelfieUploadFailureReasons.Waivable));
        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        Assert.Equal("selfie_required", SelfieWorld.CodeOf(punch.Result));
    }

    // ── Item 4: a network drop during the upload ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Item4_ATruncatedBody_Answers400Incomplete_LeavesNoPendingRow_AndDoesNotCountTowardTheHourlyLimit()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        // Nine attempts that reached storage in the last hour: one more is allowed.
        for (var i = 0; i < 9; i++)
            w.Db.AttendanceEvidence.Add(new AttendanceEvidence
            {
                TenantId = w.TenantId, EmployeeId = w.Caller.Id, StorageKey = w.Storage.TenantKey(w.TenantId, $"attendance-evidence/{Guid.NewGuid():N}.jpg"),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10), ExpiresAtUtc = DateTime.UtcNow, PurgeState = AttendanceEvidencePurgeStates.Purged,
                PurgedAtUtc = DateTime.UtcNow.AddMinutes(-5), Sha256 = new string('a', 64), ByteSize = 10,
            });
        await w.Db.SaveChangesAsync();
        var controller = w.Evidence(user);
        controller.Request.ContentType = "multipart/form-data; boundary=x";
        controller.Request.Body = new ScriptedBody(MultipartPrefix(SelfieAttendanceTests.SelfieJpeg().Take(40).ToArray()), stallAtEnd: false);

        var result = await controller.UploadSelfie().WaitAsync(Watchdog);

        Assert.Equal(400, (result as ObjectResult)?.StatusCode);
        Assert.Equal("selfie_upload_incomplete", SelfieWorld.CodeOf(result));
        Assert.False(PunchWithoutSelfie(result));
        Assert.Equal(9, await w.Db.AttendanceEvidence.CountAsync());
        Assert.False(await w.Db.AttendanceEvidence.AnyAsync(e => e.PurgeState == AttendanceEvidencePurgeStates.Pending));
        Assert.False(await w.Db.AttendanceAuditLogs.AnyAsync(a => a.Action == AttendanceVerificationService.SelfieServerFailureAction));
        // The dropped attempt did not count: the tenth upload of the hour still goes through.
        Assert.Equal(201, (await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg()) as ObjectResult)?.StatusCode);
    }

    // ── Item 6: re-consent during an upload ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item6_WithdrawAndConsentAgainWhileThePhotoUploads_DiscardsThePhoto()
    {
        var w = await OptionalAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        var storage = new ReconsentDuringPutStorage(w, user);
        var controller = w.With(new AttendanceEvidenceController(w.Db, storage, w.Verification, w.Gate), user);
        SetForm(controller, SelfieAttendanceTests.SelfieJpeg());

        var result = await controller.UploadSelfie();

        Assert.Equal(403, (result as ObjectResult)?.StatusCode);
        Assert.Equal("consent_required", SelfieWorld.CodeOf(result));
        Assert.True(storage.Reconsented);
        Assert.False(await w.Db.AttendanceEvidence.AnyAsync(e => e.PurgeState == AttendanceEvidencePurgeStates.Active));
        Assert.Empty(w.Storage.Objects); // the photo taken under the withdrawn consent is gone
        // A photo taken under the NEW consent is accepted.
        Assert.Equal(201, (await w.UploadAsync(user, SelfieAttendanceTests.SelfieJpeg()) as ObjectResult)?.StatusCode);
    }

    // ── Item 3: the residency probe under load ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Item3_TwentyConcurrentPolicyReadsDuringAHangingProbe_MakeOneProbeCall_AndFinishInAboutOneTimeout()
    {
        var calls = 0;
        var residency = new StorageResidency(new StorageOptions
        {
            Provider = "s3", Bucket = "b", Endpoint = SelfieWorld.KsaEndpoint, Region = "auto",
            ResidencyAllowList = new(StringComparer.OrdinalIgnoreCase) { ["KSA"] = ["s3.ksa-region.example.test", SelfieWorld.KsaBucketRegion] },
        }, async ct =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(Timeout.Infinite, ct); // storage hangs
            return null;
        });

        var clock = Stopwatch.StartNew();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = Enumerable.Range(0, 20).Select(_ => Task.Run(async () => { await start.Task; return await residency.CheckAsync(StorageResidency.Ksa); })).ToList();
        start.SetResult();
        var verdicts = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(90));
        clock.Stop();

        Assert.Equal(1, calls);
        Assert.All(verdicts, v => Assert.False(v.Resident)); // fail closed
        Assert.True(clock.Elapsed < StorageResidency.ProbeTimeout * 2, $"20 reads took {clock.Elapsed}, expected about one {StorageResidency.ProbeTimeout}");
        // Within ProbeFailureRetry the failure is answered without another probe.
        Assert.False((await residency.CheckAsync(StorageResidency.Ksa)).Resident);
        Assert.Equal(1, calls);
    }

    // ── Item 7: the legacy route never uses a waiver ─────────────────────────────────────────────────────

    [Fact]
    public async Task Item7_TheLegacyMobilePunch_UnderARequiredSelfieWithAnOpenWaiver_RefusesSelfieRequired_AndLeavesTheWaiverUnconsumed()
    {
        var w = await RequiredAsync();
        var user = await w.EmployeeAsync(w.Caller, w.CallerUserId);
        var waiver = new AttendanceEvidence
        {
            TenantId = w.TenantId, EmployeeId = w.Caller.Id, StorageKey = w.Storage.TenantKey(w.TenantId, $"attendance-evidence/{Guid.NewGuid():N}.jpg"),
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(9), PurgeState = AttendanceEvidencePurgeStates.Purged,
            PurgedAtUtc = DateTime.UtcNow.AddMinutes(-1), FailedReason = SelfieUploadFailureReasons.Busy,
        };
        w.Db.AttendanceEvidence.Add(waiver);
        await w.Db.SaveChangesAsync();

        var legacy = await w.Mobile(user).Punch(new MobilePunchRequest(0, "In", null), default);

        Assert.Equal(400, (legacy as ObjectResult)?.StatusCode);
        Assert.Equal("selfie_required", SelfieWorld.CodeOf(legacy));
        var after = await w.Db.AttendanceEvidence.AsNoTracking().SingleAsync(e => e.Id == waiver.Id);
        Assert.Null(after.WaiverConsumedAtUtc);
        Assert.Null(after.WaiverRawEventId);
        Assert.Empty(await w.Db.AttendanceRawEvents.ToListAsync());
        Assert.Empty(await w.Db.AttendanceDailyRecords.ToListAsync());
        // The waiver really was open: the app's own punch route uses it.
        var punch = await w.Attendance(user).MobilePunch(new WebPunchRequest(0, "In", null, null, null), default);
        Assert.Equal($"waiver:{waiver.Id}", Assert.IsType<AttendanceRawEvent>(Assert.IsType<OkObjectResult>(punch.Result).Value).PhotoReference);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<SelfieWorld> RequiredAsync()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig(requireSelfieForConsented: true));
        await SelfieAttendanceTests.ConsentAsync(w, w.Caller, w.CallerUserId);
        return w;
    }

    private static async Task<SelfieWorld> OptionalAsync()
    {
        var w = await SelfieWorld.CreateAsync();
        await w.SetFlagAsync(SelfieWorld.SelfieKey, true, SelfieWorld.SignedOffConfig());
        await SelfieAttendanceTests.ConsentAsync(w, w.Caller, w.CallerUserId);
        return w;
    }

    /// <summary>Shortens the upload deadline to one second (the production value is 45 s). By reflection: see the class note.</summary>
    private static AttendanceEvidenceController Shortened(AttendanceEvidenceController controller)
    {
        typeof(AttendanceEvidenceController)
            .GetProperty("Deadline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            ?.SetValue(controller, TimeSpan.FromSeconds(1));
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

    /// <summary>The start of a multipart body with one <c>file</c> part whose content is <paramref name="partial"/> — no closing boundary.</summary>
    private static byte[] MultipartPrefix(byte[] partial) =>
        Encoding.ASCII.GetBytes("--x\r\nContent-Disposition: form-data; name=\"file\"; filename=\"s.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n")
            .Concat(partial).ToArray();

    /// <summary>
    /// A request body that sends <c>prefix</c>, then either stalls until its read is cancelled (a drip-feed that stops) or
    /// ends (the connection dropped mid-part).
    /// </summary>
    private sealed class ScriptedBody(byte[] prefix, bool stallAtEnd) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position < prefix.Length)
            {
                var n = Math.Min(buffer.Length, Math.Min(16, prefix.Length - _position)); // dripped, 16 bytes at a time
                prefix.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }
            if (!stallAtEnd) return 0;
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    /// <summary>Storage whose write lets the employee withdraw consent and give it again (a NEW consent) mid-upload.</summary>
    private sealed class ReconsentDuringPutStorage(SelfieWorld w, System.Security.Claims.ClaimsPrincipal user) : IDocumentStorage
    {
        public bool Reconsented { get; private set; }
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) => w.Storage.SaveAsync(tenantId, file, cancellationToken);
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => w.Storage.GetBytesAsync(tenantId, storageUrl, ct);
        public string ResolvePath(string storageUrl) => storageUrl;
        public Task<bool> TryDeleteAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => w.Storage.TryDeleteAsync(tenantId, storageUrl, ct);
        public string TenantKey(Guid tenantId, string relativeName) => w.Storage.TenantKey(tenantId, relativeName);
        public Task DeleteStrictAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => w.Storage.DeleteStrictAsync(tenantId, storageUrl, ct);

        public async Task PutAtAsync(Guid tenantId, string storageKey, byte[] content, string contentType, CancellationToken ct = default)
        {
            await w.Storage.PutAtAsync(tenantId, storageKey, content, contentType, ct);
            Assert.IsType<OkObjectResult>(await w.Ess(user).WithdrawConsent(new WithdrawBiometricConsentRequest("Mobile"), default));
            await Task.Delay(20); // the new consent is strictly later than the attempt
            await SelfieAttendanceTests.ConsentAsync(w, w.Caller, w.CallerUserId);
            Reconsented = true;
        }
    }
}
