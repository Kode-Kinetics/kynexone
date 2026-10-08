using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Selfie attendance v2, the employee's side, shared by <see cref="AttendanceEvidenceController"/> (the upload) and
/// <see cref="EssAttendanceVerificationController"/> (consent and the read). Every endpoint acts on the CALLER's own
/// linked employee (the <c>employee_id</c> claim, <see cref="CallerEmployeeResolver"/>); no endpoint takes an employee id.
/// </summary>
public abstract class SelfieAttendanceControllerBase : ControllerBase
{
    protected readonly ZayraDbContext Db;
    protected readonly AttendanceVerificationService Verification;

    protected SelfieAttendanceControllerBase(ZayraDbContext db, AttendanceVerificationService? verification)
    {
        Db = db;
        Verification = verification ?? new AttendanceVerificationService(db);
    }

    /// <summary>The caller's linked employee in their tenant, or the refusal to return.</summary>
    protected async Task<(bool Ok, Guid TenantId, int EmployeeId, IActionResult? Error)> CallerAsync(CancellationToken ct, bool requireActive = false)
    {
        if (User.FindFirstValue("access_mode") is "NoLogin" or "KioskOnly")
            return (false, default, default, StatusCode(StatusCodes.Status403Forbidden,
                Refusal("access_mode_not_allowed", "This sign-in cannot use selfie attendance.", "لا يمكن لهذا الحساب استخدام الحضور بالصورة.")));
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId))
            return (false, default, default, Unauthorized());
        if (await CallerEmployeeResolver.ResolveAsync(Db, User, tenantId, ct, requireActive) is not int employeeId)
            return (false, default, default, BadRequest(new { code = "employee_not_linked", message = EssLinkGuidance.En, messageAr = EssLinkGuidance.Ar }));
        return (true, tenantId, employeeId, null);
    }

    /// <summary>Stages an attendance audit row (committed with the caller's save). The metadata always names the employee.</summary>
    protected void Audit(Guid tenantId, string action, string entity, string entityId, object metadata) =>
        Db.AttendanceAuditLogs.Add(new AttendanceAuditLog
        {
            TenantId = tenantId,
            UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null,
            Action = action,
            EntityName = entity,
            EntityId = entityId,
            MetadataJson = JsonSerializer.Serialize(metadata),
        });

    protected static object Refusal(string code, string message, string messageAr) => new { code, message, messageAr };
}

/// <summary>
/// POST /api/attendance/evidence/selfie (the upload) and GET /api/attendance/evidence/{rawEventId}/selfie (HR's review
/// view). Only the upload sits under the <c>selfie_attendance</c> opt-in prefix, so the global FeatureFlagGuardFilter
/// closes it while the flag is off (the action checks the flag itself too). The review view stays reachable with the
/// flag off: a selfie that backs a pay record is kept for its retention window whatever the flag says now.
/// </summary>
[ApiController]
[Route("api/attendance/evidence")]
[Authorize]
public sealed class AttendanceEvidenceController : SelfieAttendanceControllerBase
{
    /// <summary>The whole request, file and multipart framing: anything larger is refused before it is read.</summary>
    public const long MaxRequestBytes = 8L * 1024 * 1024;
    /// <summary>The largest photo accepted, by its declared canvas (checked from the header, before any decode).</summary>
    public const long MaxSourcePixels = 16_000_000;
    /// <summary>JPEGs are decoded downsampled toward this long edge, then re-encoded at ≤ <see cref="ProfilePhotoProcessor.MaxEdge"/> px.</summary>
    public const int DecodeLongEdge = 1080;
    /// <summary>The permission that lets HR open a stored selfie (privileged: needs MFA).</summary>
    public const string ViewPermission = "attendance.evidence.view";

    /// <summary>
    /// The hard server deadline on one upload's body read, decode and storage write together. Past it the attempt is
    /// released (it does not count) and the answer is 408 <c>selfie_upload_timeout</c>, which never waives anything.
    /// <para>It is tied to two other constants, and a test asserts both: it must stay below
    /// <see cref="SelfieWaivers.InFlight"/> (60 s), so a drip-fed body cannot outlive the window in which its reservation
    /// blocks a second upload; and below <see cref="EssAttendanceVerificationController.InFlightPendingGrace"/>
    /// (2 minutes), so a withdrawal never marks Purged a Pending row whose upload can still write its file.</para>
    /// </summary>
    public static readonly TimeSpan UploadDeadline = TimeSpan.FromSeconds(45);

    /// <summary>This controller's deadline: <see cref="UploadDeadline"/>, shortened only by tests.</summary>
    internal TimeSpan Deadline { get; init; } = UploadDeadline;

    private readonly IDocumentStorage _storage;
    private readonly SelfieImageGate _gate;
    private readonly IDataScopeService _scope;

    public AttendanceEvidenceController(ZayraDbContext db, IDocumentStorage storage, AttendanceVerificationService? verification = null,
        SelfieImageGate? gate = null, IDataScopeService? scope = null)
        : base(db, verification)
    {
        _storage = storage;
        // DI supplies the process-wide singleton; a hand-built controller (tests) gets its own.
        _gate = gate ?? new SelfieImageGate();
        _scope = scope ?? new DataScopeService(db);
    }

    /// <summary>
    /// Stores one selfie for the caller and returns an opaque evidence id: single-use, bound to the caller, valid for
    /// 10 minutes. In this order, so a hostile upload costs as little as possible:
    /// <list type="number">
    ///   <item>the caller, the flag (with sign-offs and storage residency) and consent — nothing is read yet;</item>
    ///   <item>the declared size (≤ 8 MB) and the hourly limit: the attempt is RESERVED as a <c>Pending</c> row under the
    ///     per-employee advisory lock (consent re-checked under it), so parallel uploads cannot overshoot;</item>
    ///   <item>only then the body is read. It must be a JPEG by its magic bytes (the camera always produces JPEG; the
    ///     Content-Type header is not trusted); its canvas is checked from the header (≤ 16 MP), and it is decoded
    ///     downsampled inside the process-wide gate (waiting up to 3 s for a slot, then 429 busy);</item>
    ///   <item>the re-encoded JPEG (EXIF and GPS stripped, ≤ 512 px) is stored at the key derived from the row's id; the
    ///     row flips to <c>Active</c> under the same lock, after consent is checked once more. The original bytes are
    ///     never stored.</item>
    /// </list>
    /// An attempt refused before anything reached storage (bad input, decode failure, busy) deletes its Pending row, so
    /// only attempts that reached storage count toward the hourly limit. A Pending row whose upload never completed is
    /// purged (with any file) after an hour. Only one attempt per employee may be in flight (review 3): another Pending
    /// attempt younger than 60 s answers 409 <c>selfie_upload_in_progress</c>, which never waives anything. A busy or
    /// storage failure with nothing else in flight is kept on the attempt's own row as a waiver, so a REQUIRED selfie is
    /// waived for one punch (at most two a day): the server failing never blocks attendance.
    /// </summary>
    [HttpPost("selfie")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    // NO parameters, deliberately: an action with any parameter makes MVC build its value providers, and the form value
    // provider reads the entire multipart body before the action runs — which would undo the order below. The
    // cancellation token comes from the request instead.
    public async Task<IActionResult> UploadSelfie()
    {
        var ct = HttpContext.RequestAborted;
        var (ok, tenantId, employeeId, error) = await CallerAsync(ct, requireActive: true);
        if (!ok) return error!;

        var policy = await Verification.GetPolicyAsync(tenantId, ct);
        if (!policy.SelfieEnabled) return StatusCode(StatusCodes.Status403Forbidden, AttendanceRefusals.SelfieNotEnabled.Body);
        if (await Verification.ActiveConsentAsync(tenantId, employeeId, policy.ConsentPolicyVersion, ct) is null)
            return StatusCode(StatusCodes.Status403Forbidden, AttendanceRefusals.ConsentRequired.Body);
        if (Request.ContentLength > MaxRequestBytes) return TooLarge();

        var (attempt, refusal) = await ReserveAttemptAsync(tenantId, employeeId, policy.ConsentPolicyVersion, ct);
        if (refusal is not null) return refusal;

        // The hard deadline (UploadDeadline) on the body read, the decode and the storage write together: a drip-fed body
        // must not outlive the 60 s in which its reservation blocks a second upload, nor the waiver logic built on it.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Deadline);
        var work = deadline.Token;
        bool TimedOut() => deadline.IsCancellationRequested && !ct.IsCancellationRequested;

        byte[] jpeg;
        try
        {
            var (prepared, inputRefusal, busy) = await ReadAndPrepareAsync(work);
            if (busy)
            {
                // The server's failure: kept as this attempt's waiver when nothing else was in flight (review 3).
                var punchWithoutSelfie = await RecordServerFailureAsync(tenantId, employeeId, SelfieUploadFailureReasons.Busy, attempt!, policy);
                Response.Headers.RetryAfter = "5";
                return StatusCode(StatusCodes.Status429TooManyRequests, AttendanceRefusals.SelfieBusy.BodyWith(punchWithoutSelfie));
            }
            if (inputRefusal is not null)
            {
                // Nothing reached storage: the attempt does not count (review 2, item 7). This includes a body cut off by
                // the network (selfie_upload_incomplete), which is never recorded as a server failure.
                await ReleaseAttemptAsync(attempt!);
                return inputRefusal;
            }
            jpeg = prepared!;
        }
        catch (OperationCanceledException) when (TimedOut())
        {
            // Nothing reached storage: released, so it does not count; a timeout is never a waivable server failure.
            await ReleaseAttemptAsync(attempt!);
            return UploadTimeout();
        }
        catch (OperationCanceledException)
        {
            await ReleaseAttemptAsync(attempt!);
            throw;
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant();
        try { await _storage.PutAtAsync(tenantId, attempt!.StorageKey, jpeg, EssUploadPolicy.Jpeg, work); }
        catch (OperationCanceledException) when (TimedOut())
        {
            // The write was given up, but it may still land LATE — after any delete tried now. So the row is never
            // removed: it is closed as Timeout (non-waivable; out of the in-flight and hourly counts) and stays Pending, so
            // the 1-hour sweeper strictly deletes the file once any late write has landed.
            await CloseFailedAttemptAsync(attempt!, SelfieUploadFailureReasons.Timeout);
            return UploadTimeout();
        }
        catch (OperationCanceledException)
        {
            // The client went away mid-write: the same — closed as Aborted, Pending for the sweeper — so the abandoned
            // attempt stops counting as "in flight" and blocks no later waiver.
            await CloseFailedAttemptAsync(attempt!, SelfieUploadFailureReasons.Aborted);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row stays Pending (it reached storage, so it counts, and is unusable); the purge deletes any partial
            // file within the hour. Try now too. The failure is this attempt's waiver when nothing else was in flight.
            try { await _storage.DeleteStrictAsync(tenantId, attempt!.StorageKey, CancellationToken.None); } catch { /* the purge retries */ }
            var punchWithoutSelfie = await RecordServerFailureAsync(tenantId, employeeId, SelfieUploadFailureReasons.Storage, attempt!, policy);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, AttendanceRefusals.SelfieStorageUnavailable.BodyWith(punchWithoutSelfie));
        }

        return await ActivateAsync(tenantId, employeeId, policy.ConsentPolicyVersion, attempt, sha256, jpeg.Length, ct);
    }

    /// <summary>
    /// Reads the body and turns it into the sanitised JPEG, or the refusal to return. JPEG only, by magic bytes, BEFORE
    /// any decode: a PNG decodes at full size whatever its file size, which is how a small instance runs out of memory.
    /// </summary>
    private async Task<(byte[]? Jpeg, IActionResult? Refusal, bool Busy)> ReadAndPrepareAsync(CancellationToken ct)
    {
        IFormFile? file;
        try
        {
            var form = await Request.ReadFormAsync(ct);
            file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { return (null, TooLarge(), false); }
        // The connection dropped or the body ended early (Kestrel's BadHttpRequestException, an IOException such as
        // "Unexpected end of Stream"): the network's failure, not the server's — released by the caller, never waived.
        catch (BadHttpRequestException) { return (null, Incomplete(), false); }
        catch (IOException) { return (null, Incomplete(), false); }
        catch (InvalidDataException ex) when (ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)) { return (null, TooLarge(), false); }
        catch (InvalidDataException) { return (null, Invalid(), false); }
        if (file is null || file.Length <= 0)
            return (null, BadRequest(Refusal("selfie_missing", "Take a selfie first, then try again.", "التقط صورة ذاتية أولًا ثم حاول مرة أخرى.")), false);
        if (file.Length > MaxRequestBytes) return (null, TooLarge(), false);

        byte[] source;
        try
        {
            await using var input = file.OpenReadStream();
            using var buffer = new MemoryStream((int)file.Length);
            await input.CopyToAsync(buffer, ct);
            source = buffer.ToArray();
        }
        catch (BadHttpRequestException) { return (null, Incomplete(), false); }
        catch (IOException) { return (null, Incomplete(), false); }
        if (!IsJpeg(source)) return (null, Invalid(), false);

        if (!await _gate.EnterAsync(SelfieImageGate.DefaultWait, ct)) return (null, null, true);
        try { return (ProfilePhotoProcessor.ToSanitisedJpeg(source, MaxSourcePixels, DecodeLongEdge), null, false); }
        catch (ImageTooLargeException)
        {
            return (null, BadRequest(Refusal("selfie_too_large",
                "The photo is larger than 16 megapixels. Take it again with the app's camera.",
                "الصورة أكبر من 16 ميغابكسل. التقطها مرة أخرى بكاميرا التطبيق.")), false);
        }
        catch (InvalidDataException) { return (null, Invalid(), false); }
        finally { _gate.Exit(); }
    }

    /// <summary>A JPEG starts with the SOI marker and a marker byte: FF D8 FF. Nothing else is accepted.</summary>
    public static bool IsJpeg(ReadOnlySpan<byte> bytes) => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private ObjectResult Invalid() => BadRequest(Refusal("selfie_invalid",
        "The selfie must be a JPEG photo taken with the app's camera. Take it again.",
        "يجب أن تكون الصورة الذاتية بصيغة JPEG ملتقطة بكاميرا التطبيق. التقطها مرة أخرى."));

    private ObjectResult TooLarge() => StatusCode(StatusCodes.Status413PayloadTooLarge, Refusal("selfie_too_large",
        "The selfie is larger than 8 MB. Take it again at a lower resolution.",
        "حجم الصورة أكبر من 8 ميغابايت. التقطها مرة أخرى بدقة أقل."));

    private ObjectResult Incomplete() => BadRequest(AttendanceRefusals.SelfieUploadIncomplete.Body);

    private ObjectResult UploadTimeout() => StatusCode(StatusCodes.Status408RequestTimeout, AttendanceRefusals.SelfieUploadTimeout.Body);

    /// <summary>
    /// Closes an attempt whose storage write was given up (<see cref="SelfieUploadFailureReasons.Timeout"/>,
    /// <see cref="SelfieUploadFailureReasons.Aborted"/>): the row keeps its Pending state and storage key, so the 1-hour
    /// sweeper strictly deletes whatever file a late write leaves; the non-waivable reason takes it out of the in-flight
    /// checks and the hourly count. Never removes the row (a file could land after it), never waives anything.
    /// </summary>
    /// <summary>Audit action of an upload whose storage write was given up (timeout, or the client went away). Never a waiver.</summary>
    public const string UploadAbandonedAction = "attendance.selfie.upload_abandoned";

    private async Task CloseFailedAttemptAsync(AttendanceEvidence attempt, string reason)
    {
        var none = CancellationToken.None;
        Db.ChangeTracker.Clear();
        var row = await Db.AttendanceEvidence.FirstOrDefaultAsync(e => e.TenantId == attempt.TenantId && e.Id == attempt.Id
            && e.PurgeState == AttendanceEvidencePurgeStates.Pending && e.FailedReason == null, none);
        if (row is null) return;
        row.FailedReason = reason;
        Audit(attempt.TenantId, UploadAbandonedAction, AttendanceVerificationService.EmployeeEntity,
            attempt.EmployeeId.ToString(), new { employeeId = attempt.EmployeeId, reason, evidenceId = attempt.Id, waiverGranted = false });
        await Db.SaveChangesAsync(none);
    }

    /// <summary>
    /// Reserves one upload attempt: under the per-employee advisory lock (<see cref="SelfieConsentLock"/>), re-checks
    /// consent (a withdrawal takes the same lock), refuses while another attempt of the employee is in flight (a Pending
    /// row younger than 60 s that has not failed: review 3, one upload in flight per employee), counts the employee's
    /// attempts that reached storage in the last hour and, below the limit, inserts the Pending row with its storage key
    /// derived from its id. Commits before the body is read.
    /// </summary>
    private async Task<(AttendanceEvidence? Attempt, IActionResult? Refusal)> ReserveAttemptAsync(
        Guid tenantId, int employeeId, string policyVersion, CancellationToken ct)
    {
        var (attempt, refusal) = await SelfieConsentLock.RunAsync(Db, tenantId, employeeId,
            () => ReserveCoreAsync(tenantId, employeeId, policyVersion, ct), ct);
        return refusal switch
        {
            null => (attempt, null),
            { Code: "consent_required" } => (null, StatusCode(StatusCodes.Status403Forbidden, refusal.Body)),
            // Never carries punchWithoutSelfie: a second upload in flight must not manufacture a waiver (review 3).
            { Code: "selfie_upload_in_progress" } => (null, Conflict(refusal.Body)),
            _ => (null, StatusCode(StatusCodes.Status429TooManyRequests, refusal.Body)),
        };
    }

    private async Task<(AttendanceEvidence?, AttendanceRefusal?)> ReserveCoreAsync(Guid tenantId, int employeeId, string policyVersion, CancellationToken ct)
    {
        if (await Verification.ActiveConsentAsync(tenantId, employeeId, policyVersion, ct) is null)
            return (null, AttendanceRefusals.ConsentRequired);
        var now = DateTime.UtcNow;
        var inFlightSince = now - SelfieWaivers.InFlight;
        if (await Db.AttendanceEvidence.AnyAsync(e => e.TenantId == tenantId && e.EmployeeId == employeeId
                && e.PurgeState == AttendanceEvidencePurgeStates.Pending && e.FailedReason == null && e.CreatedAtUtc > inFlightSince, ct))
            return (null, AttendanceRefusals.SelfieUploadInProgress);
        var since = now.AddHours(-1);
        // Busy (no file was written), Timeout and Aborted (the transfer never completed) do not count (review 2, item 7;
        // hardening); a stored selfie and any storage failure, waived or denied, do.
        var attempts = await Db.AttendanceEvidence.CountAsync(e => e.TenantId == tenantId && e.EmployeeId == employeeId && e.CreatedAtUtc > since
                                                                   && (e.FailedReason == null
                                                                       || (e.FailedReason != SelfieUploadFailureReasons.Busy
                                                                           && e.FailedReason != SelfieUploadFailureReasons.Timeout
                                                                           && e.FailedReason != SelfieUploadFailureReasons.Aborted)), ct);
        if (attempts >= AttendanceVerificationService.MaxUploadsPerHour) return (null, AttendanceRefusals.RateLimited);

        var id = Guid.NewGuid();
        var evidence = new AttendanceEvidence
        {
            Id = id,
            TenantId = tenantId,
            EmployeeId = employeeId,
            StorageKey = _storage.TenantKey(tenantId, $"attendance-evidence/{id:N}.jpg"),
            ContentType = EssUploadPolicy.Jpeg,
            PurgeState = AttendanceEvidencePurgeStates.Pending,
            CreatedAtUtc = now,
            ExpiresAtUtc = now + AttendanceVerificationService.EvidenceLifetime,
        };
        Db.AttendanceEvidence.Add(evidence);
        await Db.SaveChangesAsync(ct);
        return (evidence, null);
    }

    /// <summary>Removes a Pending attempt that never reached storage, so it does not count toward the hourly limit.</summary>
    private async Task ReleaseAttemptAsync(AttendanceEvidence attempt)
    {
        Db.ChangeTracker.Clear();
        var row = await Db.AttendanceEvidence.FirstOrDefaultAsync(e => e.TenantId == attempt.TenantId && e.Id == attempt.Id
            && e.PurgeState == AttendanceEvidencePurgeStates.Pending, CancellationToken.None);
        if (row is null) return;
        Db.AttendanceEvidence.Remove(row);
        await Db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Records a server-side failure (busy, or storage) of <paramref name="attempt"/>, under the per-employee advisory
    /// lock (review 3). When the employee had NO other attempt in flight, the attempt becomes a waiver
    /// (<see cref="AttendanceEvidence.FailedReason"/>): one later punch of theirs may go through without the required
    /// selfie, within 10 minutes, at most twice a day (<see cref="SelfieWaivers"/>). A busy attempt wrote no file, so it is
    /// closed Purged at once; a storage failure stays Pending for the sweeper (a partial file may exist). A failure while
    /// another attempt was in flight waives nothing: a busy one is deleted (it never counted), a storage one stays Pending
    /// closed as <c>DeniedFailure</c> (non-waivable, no longer "in flight").
    /// Returns whether the app may offer to punch without a selfie (<c>punchWithoutSelfie</c>): always where the selfie is
    /// optional; where it is required, only when this failure is a waiver and today's cap is not used up.
    /// </summary>
    private async Task<bool> RecordServerFailureAsync(Guid tenantId, int employeeId, string reason, AttendanceEvidence attempt,
        AttendanceVerificationPolicy policy)
    {
        var none = CancellationToken.None;
        var waiver = await SelfieConsentLock.RunAsync(Db, tenantId, employeeId, async () =>
        {
            var row = await Db.AttendanceEvidence.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == attempt.Id
                && e.PurgeState == AttendanceEvidencePurgeStates.Pending, none);
            var now = DateTime.UtcNow;
            // ANY not-failed Pending attempt of the employee from the last hour counts as in flight here, not just the
            // last 60 s: an attempt still Pending after 60 s has not finished, and a failure beside it waives nothing.
            var inFlightSince = now - SelfieWaivers.FailureInFlightLookback;
            var otherInFlight = await Db.AttendanceEvidence.AnyAsync(e => e.TenantId == tenantId && e.EmployeeId == employeeId && e.Id != attempt.Id
                && e.PurgeState == AttendanceEvidencePurgeStates.Pending && e.FailedReason == null && e.CreatedAtUtc > inFlightSince, none);
            var granted = row is not null && !otherInFlight;
            if (row is not null)
            {
                // A storage failure refused a waiver is still closed as failed (DeniedFailure, non-waivable), so it stops
                // counting as "in flight" and cannot block a later attempt's waiver for the rest of the hour.
                row.FailedReason = granted ? reason : SelfieUploadFailureReasons.DeniedFailure;
                if (reason == SelfieUploadFailureReasons.Busy)
                {
                    if (granted)
                    {
                        // Nothing was written to storage: nothing to sweep.
                        row.PurgeState = AttendanceEvidencePurgeStates.Purged;
                        row.PurgedAtUtc = now;
                    }
                    else Db.AttendanceEvidence.Remove(row);
                }
            }
            Audit(tenantId, AttendanceVerificationService.SelfieServerFailureAction, AttendanceVerificationService.EmployeeEntity, employeeId.ToString(),
                new { employeeId, reason, evidenceId = attempt.Id, waiverGranted = granted, otherAttemptInFlight = otherInFlight });
            await Db.SaveChangesAsync(none);
            return granted;
        }, none);
        if (!policy.RequireSelfieForConsented) return true;
        return waiver && !await SelfieWaivers.CapReachedAsync(Db, tenantId, employeeId, DateTime.UtcNow, none);
    }

    /// <summary>
    /// Flips the stored attempt to Active under the advisory lock, once consent is confirmed still open and the row is
    /// still Pending. Otherwise (consent withdrawn during the upload — also when it was then given again, since the open
    /// consent must date from at or before the attempt — or the row purged meanwhile) the file just written
    /// is deleted strictly; if storage cannot confirm that delete, the row is left — or put back — Pending, never
    /// Purged, so the 1-hour sweeper retries it.
    /// </summary>
    private async Task<IActionResult> ActivateAsync(Guid tenantId, int employeeId, string policyVersion, AttendanceEvidence attempt,
        string sha256, int byteSize, CancellationToken ct)
    {
        var (activated, refusal) = await SelfieConsentLock.RunAsync(Db, tenantId, employeeId, async () =>
        {
            var row = await Db.AttendanceEvidence.FirstAsync(e => e.TenantId == tenantId && e.Id == attempt.Id, ct);
            // The open consent must be the one the photo was taken under: given at or before the attempt was reserved. A
            // withdrawal and a NEW consent while the photo uploaded leave consent open, but not for this photo — discard it.
            var consent = await Verification.ActiveConsentAsync(tenantId, employeeId, policyVersion, ct);
            var consentOpen = consent is not null && consent.GivenAtUtc <= row.CreatedAtUtc;
            if (row.PurgeState != AttendanceEvidencePurgeStates.Pending || !consentOpen)
                return ((AttendanceEvidence?)null, consentOpen ? AttendanceRefusals.UploadInterrupted : AttendanceRefusals.ConsentRequired);
            var now = DateTime.UtcNow;
            row.PurgeState = AttendanceEvidencePurgeStates.Active;
            row.Sha256 = sha256;
            row.ByteSize = byteSize;
            row.ExpiresAtUtc = now + AttendanceVerificationService.EvidenceLifetime;
            // Review 3: the employee could take a selfie after all, so any open server-failure waiver is cancelled.
            var open = await Db.AttendanceEvidence
                .Where(e => e.TenantId == tenantId && e.EmployeeId == employeeId
                            && (e.FailedReason == SelfieUploadFailureReasons.Busy || e.FailedReason == SelfieUploadFailureReasons.Storage)
                            && e.WaiverConsumedAtUtc == null && e.WaiverCancelledAtUtc == null)
                .ToListAsync(ct);
            foreach (var waiver in open) waiver.WaiverCancelledAtUtc = now;
            Audit(tenantId, "attendance.selfie.uploaded", "AttendanceEvidence", row.Id.ToString(),
                new { employeeId, sha256, byteSize, expiresAtUtc = row.ExpiresAtUtc, waiversCancelled = open.Select(w => w.Id) });
            await Db.SaveChangesAsync(ct);
            return (row, (AttendanceRefusal?)null);
        }, ct);
        if (activated is not null)
            return StatusCode(StatusCodes.Status201Created, new { evidenceId = activated.Id, expiresAtUtc = activated.ExpiresAtUtc });

        await DiscardStoredAttemptAsync(tenantId, attempt.Id, refusal!);
        return refusal!.Code == AttendanceRefusals.ConsentRequired.Code
            ? StatusCode(StatusCodes.Status403Forbidden, refusal.Body)
            : Conflict(refusal.Body);
    }

    private async Task DiscardStoredAttemptAsync(Guid tenantId, Guid evidenceId, AttendanceRefusal why)
    {
        var none = CancellationToken.None;
        Db.ChangeTracker.Clear();
        var row = await Db.AttendanceEvidence.FirstAsync(e => e.TenantId == tenantId && e.Id == evidenceId, none);
        try
        {
            if (row.PurgeState == AttendanceEvidencePurgeStates.Pending)
                // Strict: throws BEFORE touching the row when the delete is not confirmed.
                await new SelfieEvidencePurger(Db, _storage).PurgeNowAsync(row, DateTime.UtcNow,
                    $"Selfie upload not activated ({why.Code}); the stored file is deleted at once.", none);
            else
                // Already Purged (a withdrawal purged the Pending row before this upload wrote its file): delete the file again.
                await _storage.DeleteStrictAsync(tenantId, row.StorageKey, none);
            await Db.SaveChangesAsync(none);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Leave the row Pending — or put it back to Pending — never Purged while the file may still exist: the purge
            // job deletes stale Pending rows (and their files) after an hour.
            Db.ChangeTracker.Clear();
            var again = await Db.AttendanceEvidence.FirstAsync(e => e.TenantId == tenantId && e.Id == evidenceId, none);
            if (again.PurgeState != AttendanceEvidencePurgeStates.Pending)
            {
                again.PurgeState = AttendanceEvidencePurgeStates.Pending;
                again.PurgedAtUtc = null;
                Audit(tenantId, "attendance.selfie.purge_reopened", "AttendanceEvidence", again.Id.ToString(),
                    new { employeeId = again.EmployeeId, reason = "the file written by an interrupted upload could not be confirmed deleted; the purge job retries it", error = ex.GetType().Name });
            }
            await Db.SaveChangesAsync(none);
        }
    }

    /// <summary>
    /// HR's review view: the stored selfie behind one punch, as a JPEG stream. Requires
    /// <see cref="ViewPermission"/> (privileged, so MFA; NOT held by Admin by default, review 3); the punch's employee
    /// inside the caller's data scope; never the caller's own record (HR does not review themselves); and the evidence
    /// Active and not purged. Every view is audited (who, whose, which punch, when), and so is every REFUSED attempt
    /// (review 3: missing permission, outside scope, own record — who, which punch, why). <c>Cache-Control: no-store</c>;
    /// the storage key is never returned. The permission is checked here rather than by an attribute so a refusal for
    /// it can be audited too.
    /// </summary>
    [HttpGet("{rawEventId:guid}/selfie")]
    public async Task<IActionResult> ViewSelfie(Guid rawEventId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        var callerUserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var uid) ? uid : (Guid?)null;

        if (!User.HasPermission(ViewPermission))
        {
            await AuditRefusedViewAsync(tenantId, callerUserId, rawEventId, null, ViewRefusalReasons.MissingPermission);
            return StatusCode(StatusCodes.Status403Forbidden, Refusal("evidence_permission_required",
                "Opening a stored selfie needs the selfie-review permission. Ask your HR administrator.",
                "يتطلب فتح الصورة الذاتية المحفوظة صلاحية مراجعة الصور الذاتية. تواصل مع مسؤول الموارد البشرية."));
        }

        var raw = await Db.AttendanceRawEvents.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Id == rawEventId)
            .Select(r => new { r.Id, r.EmployeeId })
            .FirstOrDefaultAsync(ct);
        if (raw?.EmployeeId is not int employeeId) return NotFound(NoSelfie);
        var scope = await _scope.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(employeeId))
        {
            await AuditRefusedViewAsync(tenantId, callerUserId, rawEventId, employeeId, ViewRefusalReasons.OutsideScope);
            return NotFound(NoSelfie); // outside scope answers like a missing punch
        }
        if (scope.CallerEmployeeId == employeeId
            || await Zayra.Api.Infrastructure.Approvals.SubjectDecisionBar.CallerIsSubjectAsync(Db, tenantId, callerUserId, employeeId, ct))
        {
            await AuditRefusedViewAsync(tenantId, callerUserId, rawEventId, employeeId, ViewRefusalReasons.OwnRecord);
            return StatusCode(StatusCodes.Status403Forbidden, Refusal("evidence_own_record",
                "You cannot review the selfie on your own punch. Another HR reviewer must open it.",
                "لا يمكنك مراجعة الصورة الذاتية المرفقة بتسجيلك أنت. يجب أن يفتحها مراجع آخر من الموارد البشرية."));
        }

        var evidence = await Db.AttendanceEvidence.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.UsedByRawEventId == rawEventId && e.EmployeeId == employeeId
                        && e.PurgeState == AttendanceEvidencePurgeStates.Active && e.PurgedAtUtc == null)
            .Select(e => new { e.Id, e.StorageKey })
            .FirstOrDefaultAsync(ct);
        if (evidence is null) return NotFound(NoSelfie);

        byte[] bytes;
        try { bytes = await _storage.GetBytesAsync(tenantId, evidence.StorageKey, ct); }
        catch (FileNotFoundException) { return NotFound(NoSelfie); }

        Audit(tenantId, "attendance.selfie.viewed", "AttendanceEvidence", evidence.Id.ToString(),
            new { viewerUserId = callerUserId, employeeId, rawEventId, evidenceId = evidence.Id, viewedAtUtc = DateTime.UtcNow });
        await Db.SaveChangesAsync(ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, EssUploadPolicy.Jpeg);
    }

    /// <summary>Audit action of a refused attempt to open a stored selfie (review 3).</summary>
    public const string ViewRefusedAction = "attendance.selfie.view_refused";

    /// <summary>Why a selfie view was refused, as the audit records it.</summary>
    public static class ViewRefusalReasons
    {
        public const string MissingPermission = "missing_permission";
        public const string OutsideScope = "outside_scope";
        public const string OwnRecord = "own_record";
    }

    /// <summary>Writes the refused-view audit row (who, which punch, whose when known, why). Never blocks the refusal.</summary>
    private async Task AuditRefusedViewAsync(Guid tenantId, Guid? viewerUserId, Guid rawEventId, int? employeeId, string reason)
    {
        Audit(tenantId, ViewRefusedAction, "AttendanceRawEvent", rawEventId.ToString(),
            new { viewerUserId, rawEventId, employeeId, reason, refusedAtUtc = DateTime.UtcNow });
        await Db.SaveChangesAsync(CancellationToken.None);
    }

    private static readonly object NoSelfie = new
    {
        code = "evidence_not_found",
        message = "There is no stored selfie for this punch. It may never have had one, or it was deleted under the retention rule.",
        messageAr = "لا توجد صورة ذاتية محفوظة لهذا التسجيل. ربما لم تُرفق صورة، أو حُذفت وفق قاعدة الاحتفاظ.",
    };
}

/// <summary>
/// The per-employee advisory lock that orders selfie uploads against consent changes (review 2, item 6): the upload's
/// reservation and activation and a consent withdrawal all take it, so a withdrawal cannot slip between a consent check
/// and the write it guards. Transaction-scoped (<c>pg_advisory_xact_lock</c>); on a non-relational provider (tests)
/// the work simply runs.
/// </summary>
public static class SelfieConsentLock
{
    public static string Key(Guid tenantId, int employeeId) => $"attendance-evidence-upload:{tenantId:N}:{employeeId}";

    /// <summary>
    /// Takes the same lock inside the caller's ALREADY OPEN transaction (the punch write, review 3: a waiver is used
    /// under it). Released when that transaction ends. A no-op on a non-relational provider (tests).
    /// </summary>
    public static async Task AcquireInCurrentTransactionAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("The selfie advisory lock must be taken inside the punch transaction.");
        if (!(db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return;
        var lockKey = Key(tenantId, employeeId);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
    }

    public static async Task<T> RunAsync<T>(ZayraDbContext db, Guid tenantId, int employeeId, Func<Task<T>> work, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await work();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retried attempt starts clean.
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await AcquireInCurrentTransactionAsync(db, tenantId, employeeId, ct);
            var result = await work();
            await tx.CommitAsync(ct);
            return result;
        });
    }
}

/// <summary>
/// The employee's selfie-attendance settings in Self-Service: read whether the selfie step and the geofence apply, and
/// give or withdraw consent. All reachable with the feature off — withdrawing is always possible.
/// </summary>
[ApiController]
[Route("api/ess")]
[Authorize]
public sealed class EssAttendanceVerificationController : SelfieAttendanceControllerBase
{
    private readonly IDocumentStorage? _storage;
    private readonly ILogger<EssAttendanceVerificationController>? _log;

    public EssAttendanceVerificationController(ZayraDbContext db, AttendanceVerificationService? verification = null,
        IDocumentStorage? storage = null, ILogger<EssAttendanceVerificationController>? log = null)
        : base(db, verification)
    {
        _storage = storage;
        _log = log;
    }

    /// <summary>
    /// What the app needs before a punch: whether the selfie step applies (flag on, the caller's consent, whether the
    /// tenant requires it for consenting employees, the current policy version) and whether the geofence is enforced
    /// with its limits and the caller's sites. Works with every flag off.
    /// </summary>
    [HttpGet("attendance-verification")]
    public async Task<IActionResult> GetAttendanceVerification(CancellationToken ct)
    {
        var (ok, tenantId, employeeId, error) = await CallerAsync(ct);
        if (!ok) return error!;
        return Ok(await BuildViewAsync(tenantId, employeeId, ct));
    }

    /// <summary>Records the caller's consent to selfie attendance for the current policy version.</summary>
    [HttpPost("biometric-consent")]
    public async Task<IActionResult> GiveConsent([FromBody] GiveBiometricConsentRequest request, CancellationToken ct)
    {
        var (ok, tenantId, employeeId, error) = await CallerAsync(ct);
        if (!ok) return error!;

        var policy = await Verification.GetPolicyAsync(tenantId, ct);
        if (!policy.SelfieEnabled) return Conflict(AttendanceRefusals.SelfieNotEnabled.Body);
        if (!string.Equals(request.PolicyVersion?.Trim(), policy.ConsentPolicyVersion, StringComparison.Ordinal))
            return Conflict(new
            {
                code = "consent_version_mismatch",
                message = "The consent text has changed. Read the current version and agree to it again.",
                messageAr = "تغيّر نص الموافقة. اقرأ النسخة الحالية ووافق عليها مرة أخرى.",
                currentPolicyVersion = policy.ConsentPolicyVersion,
            });
        if (BiometricConsentChannels.Normalize(request.Channel) is not { } channel)
            return BadRequest(Refusal("consent_channel_invalid", "Channel must be Mobile or Web.", "يجب أن تكون القناة Mobile أو Web."));

        var open = await Db.BiometricConsents
            .Where(c => c.TenantId == tenantId && c.EmployeeId == employeeId && c.WithdrawnAtUtc == null)
            .ToListAsync(ct);
        if (open.Any(c => c.PolicyVersion == policy.ConsentPolicyVersion))
            return Ok(await BuildViewAsync(tenantId, employeeId, ct));

        var now = DateTime.UtcNow;
        // Consent to an older text does not carry over: close it, then record agreement to the current one.
        foreach (var stale in open) stale.WithdrawnAtUtc = now;
        var consent = new BiometricConsent
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            PolicyVersion = policy.ConsentPolicyVersion,
            GivenAtUtc = now,
            Channel = channel,
        };
        Db.BiometricConsents.Add(consent);
        Audit(tenantId, "attendance.biometric_consent.given", "BiometricConsent", consent.Id.ToString(),
            new { employeeId, policyVersion = consent.PolicyVersion, channel, supersededConsentIds = open.Select(c => c.Id) });
        try { await Db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // A concurrent request recorded it first (one open row per employee is a unique index): same outcome.
            Db.ChangeTracker.Clear();
        }
        return StatusCode(StatusCodes.Status201Created, await BuildViewAsync(tenantId, employeeId, ct));
    }

    /// <summary>
    /// Withdraws the caller's consent. Always possible — with the feature off, with no write permission, and when there
    /// is nothing open (a no-op that still answers 200). Afterwards the caller punches without a selfie.
    /// <para>Review item 8: the caller's UNUSED selfies (Active, or Pending for 2 minutes or more — a younger Pending row
    /// may be an upload in flight and is left to it and the 1-hour sweeper, review 3) are then deleted at once, strictly (every
    /// version, confirmed). A selfie a punch already used keeps its retention window, because it backs a pay record
    /// (docs/schema/OWNERSHIP_AND_RETENTION.md). A delete storage cannot confirm leaves that row for the purge job,
    /// which runs every 15 minutes and deletes an unused selfie of an employee with no open consent at once, so it is
    /// retried within about 15 minutes; the withdrawal itself never fails because of it.</para>
    /// </summary>
    [HttpPost("biometric-consent/withdraw")]
    [HttpDelete("biometric-consent")]
    public async Task<IActionResult> WithdrawConsent([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] WithdrawBiometricConsentRequest? request, CancellationToken ct)
    {
        var (ok, tenantId, employeeId, error) = await CallerAsync(ct);
        if (!ok) return error!;

        // Under the same advisory lock as the upload's reservation and activation (review 2, item 6): an upload in
        // flight either activates before this withdrawal, and is then purged below as unused, or re-checks consent
        // after it, finds none, and deletes its own file.
        await SelfieConsentLock.RunAsync(Db, tenantId, employeeId, async () =>
        {
            var open = await Db.BiometricConsents
                .Where(c => c.TenantId == tenantId && c.EmployeeId == employeeId && c.WithdrawnAtUtc == null)
                .ToListAsync(ct);
            if (open.Count == 0) return 0;
            var now = DateTime.UtcNow;
            foreach (var consent in open)
            {
                consent.WithdrawnAtUtc = now;
                Audit(tenantId, "attendance.biometric_consent.withdrawn", "BiometricConsent", consent.Id.ToString(),
                    new { employeeId, policyVersion = consent.PolicyVersion, channel = BiometricConsentChannels.Normalize(request?.Channel) });
            }
            await Db.SaveChangesAsync(ct);
            return open.Count;
        }, ct);
        Db.ChangeTracker.Clear();
        var (deleted, awaiting) = await PurgeUnusedSelfiesAsync(tenantId, employeeId, ct);
        var view = await BuildViewAsync(tenantId, employeeId, ct);
        view["withdrawal"] = new { unusedSelfiesDeleted = deleted, unusedSelfiesAwaitingDeletion = awaiting };
        return Ok(view);
    }

    private async Task<(int Deleted, int Awaiting)> PurgeUnusedSelfiesAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        // Review 3, item 9: a Pending row younger than 2 minutes may be an upload still in flight. It is NOT marked Purged
        // here (that would let the upload write its file afterwards and, if it then crashed, leave a face image behind a
        // Purged row that nothing sweeps). It stays Pending: the upload's own consent re-check under the advisory lock
        // discards it, and if the upload died the 1-hour sweeper deletes its file. It counts as awaiting deletion.
        var inFlightBefore = DateTime.UtcNow - InFlightPendingGrace;
        var unused = await Db.AttendanceEvidence
            .Where(e => e.TenantId == tenantId && e.EmployeeId == employeeId && e.UsedAtUtc == null
                        && (e.PurgeState == AttendanceEvidencePurgeStates.Active || e.PurgeState == AttendanceEvidencePurgeStates.Pending))
            .ToListAsync(ct);
        var inFlight = unused.Count(e => e.PurgeState == AttendanceEvidencePurgeStates.Pending && e.CreatedAtUtc > inFlightBefore);
        unused = unused.Where(e => !(e.PurgeState == AttendanceEvidencePurgeStates.Pending && e.CreatedAtUtc > inFlightBefore)).ToList();
        if (unused.Count == 0) return (0, inFlight);
        if (_storage is null) return (0, unused.Count + inFlight);

        var purger = new SelfieEvidencePurger(Db, _storage);
        var deleted = 0;
        foreach (var evidence in unused)
        {
            try
            {
                // Strict: throws before touching the row when the delete is not confirmed.
                await purger.PurgeNowAsync(evidence, DateTime.UtcNow, "Consent withdrawn: an unused selfie is deleted at once.", ct);
                await Db.SaveChangesAsync(ct);
                deleted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning(ex, "Selfie {EvidenceId} could not be confirmed deleted on consent withdrawal; the purge job will retry.", evidence.Id);
            }
        }
        return (deleted, unused.Count - deleted + inFlight);
    }

    /// <summary>
    /// A Pending row younger than this may belong to an upload still in flight; a withdrawal leaves it Pending. It must
    /// stay safely above <see cref="AttendanceEvidenceController.UploadDeadline"/> (45 s): an upload can write its file
    /// for at most that long after it reserved, so a row older than this grace can no longer gain a file, and marking it
    /// Purged cannot orphan one. A test asserts deadline &lt; grace.
    /// </summary>
    public static readonly TimeSpan InFlightPendingGrace = TimeSpan.FromMinutes(2);

    private async Task<Dictionary<string, object?>> BuildViewAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var policy = await Verification.GetPolicyAsync(tenantId, ct);
        var consent = await Verification.ActiveConsentAsync(tenantId, employeeId, policy.ConsentPolicyVersion, ct);
        var sites = policy.GeofenceEnforced ? await Verification.SitesForAsync(tenantId, employeeId, ct) : [];
        return new Dictionary<string, object?>
        {
            ["selfie"] = new
            {
                enabled = policy.SelfieEnabled,
                // Why a tenant that switched selfie attendance on still sees it off (sign-offs or storage residency); null otherwise.
                offReason = policy.SelfieOffReason,
                // The step is offered only to a consenting employee; required only if the tenant chose so for them.
                step = !policy.SelfieEnabled ? "off" : consent is null ? "consent_needed" : policy.RequireSelfieForConsented ? "required" : "optional",
                requiredForConsented = policy.RequireSelfieForConsented,
                currentPolicyVersion = policy.ConsentPolicyVersion,
                consent = consent is null ? null : new { consent.PolicyVersion, consent.GivenAtUtc, consent.Channel },
                evidenceLifetimeSeconds = (int)AttendanceVerificationService.EvidenceLifetime.TotalSeconds,
                maxUploadsPerHour = AttendanceVerificationService.MaxUploadsPerHour,
            },
            ["geofence"] = new
            {
                enforced = policy.GeofenceEnforced,
                maxAccuracyMeters = policy.GeofenceEnforced ? policy.MaxAccuracyMeters : (int?)null,
                allowMockedLocation = policy.GeofenceEnforced ? policy.AllowMockedLocation : (bool?)null,
                sites = sites.Select(s => new { s.Name, s.Latitude, s.Longitude, s.RadiusMeters }),
                // The app is required for self punches while the geofence is enforced (a browser cannot attest to a mock).
                webPunchAllowed = !policy.GeofenceEnforced,
            },
        };
    }

}

public sealed record GiveBiometricConsentRequest(string? PolicyVersion, string? Channel);

public sealed record WithdrawBiometricConsentRequest(string? Channel);
