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
    /// purged (with any file) after an hour. Busy and storage failures are recorded so a REQUIRED selfie is waived for
    /// the employee's next punch: the server failing never blocks attendance.
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

        byte[] jpeg;
        try
        {
            var (prepared, inputRefusal) = await ReadAndPrepareAsync(tenantId, employeeId, ct);
            if (inputRefusal is not null)
            {
                // Nothing reached storage: the attempt does not count (review 2, item 7).
                await ReleaseAttemptAsync(attempt!);
                return inputRefusal;
            }
            jpeg = prepared!;
        }
        catch (OperationCanceledException)
        {
            await ReleaseAttemptAsync(attempt!);
            throw;
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant();
        try { await _storage.PutAtAsync(tenantId, attempt!.StorageKey, jpeg, EssUploadPolicy.Jpeg, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row stays Pending (it reached storage, so it counts, and is unusable); the purge deletes any partial
            // file within the hour. Try now too. The failure waives a required selfie for the next punch.
            try { await _storage.DeleteStrictAsync(tenantId, attempt!.StorageKey, CancellationToken.None); } catch { /* the purge retries */ }
            await RecordServerFailureAsync(tenantId, employeeId, AttendanceVerificationService.ServerFailureStorage, attempt!.Id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, AttendanceRefusals.SelfieStorageUnavailable.BodyWith(punchWithoutSelfie: true));
        }

        return await ActivateAsync(tenantId, employeeId, policy.ConsentPolicyVersion, attempt, sha256, jpeg.Length, ct);
    }

    /// <summary>
    /// Reads the body and turns it into the sanitised JPEG, or the refusal to return. JPEG only, by magic bytes, BEFORE
    /// any decode: a PNG decodes at full size whatever its file size, which is how a small instance runs out of memory.
    /// </summary>
    private async Task<(byte[]? Jpeg, IActionResult? Refusal)> ReadAndPrepareAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        IFormFile? file;
        try
        {
            var form = await Request.ReadFormAsync(ct);
            file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { return (null, TooLarge()); }
        catch (InvalidDataException ex) when (ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)) { return (null, TooLarge()); }
        catch (InvalidDataException) { return (null, Invalid()); }
        if (file is null || file.Length <= 0)
            return (null, BadRequest(Refusal("selfie_missing", "Take a selfie first, then try again.", "التقط صورة ذاتية أولًا ثم حاول مرة أخرى.")));
        if (file.Length > MaxRequestBytes) return (null, TooLarge());

        byte[] source;
        await using (var input = file.OpenReadStream())
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await input.CopyToAsync(buffer, ct);
            source = buffer.ToArray();
        }
        if (!IsJpeg(source)) return (null, Invalid());

        if (!await _gate.EnterAsync(SelfieImageGate.DefaultWait, ct))
        {
            await RecordServerFailureAsync(tenantId, employeeId, AttendanceVerificationService.ServerFailureBusy, null);
            Response.Headers.RetryAfter = "5";
            return (null, StatusCode(StatusCodes.Status429TooManyRequests, AttendanceRefusals.SelfieBusy.BodyWith(punchWithoutSelfie: true)));
        }
        try { return (ProfilePhotoProcessor.ToSanitisedJpeg(source, MaxSourcePixels, DecodeLongEdge), null); }
        catch (ImageTooLargeException)
        {
            return (null, BadRequest(Refusal("selfie_too_large",
                "The photo is larger than 16 megapixels. Take it again with the app's camera.",
                "الصورة أكبر من 16 ميغابكسل. التقطها مرة أخرى بكاميرا التطبيق.")));
        }
        catch (InvalidDataException) { return (null, Invalid()); }
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

    /// <summary>
    /// Reserves one upload attempt: under the per-employee advisory lock (<see cref="SelfieConsentLock"/>), re-checks
    /// consent (a withdrawal takes the same lock), counts the employee's attempts in the last hour and, below the limit,
    /// inserts the Pending row with its storage key derived from its id. Commits before the body is read.
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
            _ => (null, StatusCode(StatusCodes.Status429TooManyRequests, refusal.Body)),
        };
    }

    private async Task<(AttendanceEvidence?, AttendanceRefusal?)> ReserveCoreAsync(Guid tenantId, int employeeId, string policyVersion, CancellationToken ct)
    {
        if (await Verification.ActiveConsentAsync(tenantId, employeeId, policyVersion, ct) is null)
            return (null, AttendanceRefusals.ConsentRequired);
        var now = DateTime.UtcNow;
        var since = now.AddHours(-1);
        var attempts = await Db.AttendanceEvidence.CountAsync(e => e.TenantId == tenantId && e.EmployeeId == employeeId && e.CreatedAtUtc > since, ct);
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
    /// Records a server-side failure (busy, or storage) for the employee. A punch that REQUIRES a selfie is then
    /// allowed once without one, recorded None and audited with this reason (AttendanceVerificationService).
    /// </summary>
    private async Task RecordServerFailureAsync(Guid tenantId, int employeeId, string reason, Guid? evidenceId)
    {
        Db.ChangeTracker.Clear();
        Audit(tenantId, AttendanceVerificationService.SelfieServerFailureAction, AttendanceVerificationService.EmployeeEntity, employeeId.ToString(),
            new { employeeId, reason, evidenceId });
        await Db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Flips the stored attempt to Active under the advisory lock, once consent is confirmed still open and the row is
    /// still Pending. Otherwise (consent withdrawn during the upload, or the row purged meanwhile) the file just written
    /// is deleted strictly; if storage cannot confirm that delete, the row is left — or put back — Pending, never
    /// Purged, so the 1-hour sweeper retries it.
    /// </summary>
    private async Task<IActionResult> ActivateAsync(Guid tenantId, int employeeId, string policyVersion, AttendanceEvidence attempt,
        string sha256, int byteSize, CancellationToken ct)
    {
        var (activated, refusal) = await SelfieConsentLock.RunAsync(Db, tenantId, employeeId, async () =>
        {
            var row = await Db.AttendanceEvidence.FirstAsync(e => e.TenantId == tenantId && e.Id == attempt.Id, ct);
            var consentOpen = await Verification.ActiveConsentAsync(tenantId, employeeId, policyVersion, ct) is not null;
            if (row.PurgeState != AttendanceEvidencePurgeStates.Pending || !consentOpen)
                return ((AttendanceEvidence?)null, consentOpen ? AttendanceRefusals.UploadInterrupted : AttendanceRefusals.ConsentRequired);
            var now = DateTime.UtcNow;
            row.PurgeState = AttendanceEvidencePurgeStates.Active;
            row.Sha256 = sha256;
            row.ByteSize = byteSize;
            row.ExpiresAtUtc = now + AttendanceVerificationService.EvidenceLifetime;
            Audit(tenantId, "attendance.selfie.uploaded", "AttendanceEvidence", row.Id.ToString(),
                new { employeeId, sha256, byteSize, expiresAtUtc = row.ExpiresAtUtc });
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
    /// <see cref="ViewPermission"/> (privileged, so MFA); the punch's employee inside the caller's data scope; never the
    /// caller's own record (HR does not review themselves); and the evidence Active and not purged. Every view is
    /// audited (who, whose, which punch, when). <c>Cache-Control: no-store</c>; the storage key is never returned.
    /// </summary>
    [HttpGet("{rawEventId:guid}/selfie")]
    [HasPermission(ViewPermission)]
    public async Task<IActionResult> ViewSelfie(Guid rawEventId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        var raw = await Db.AttendanceRawEvents.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Id == rawEventId)
            .Select(r => new { r.Id, r.EmployeeId })
            .FirstOrDefaultAsync(ct);
        if (raw?.EmployeeId is not int employeeId) return NotFound(NoSelfie);
        var scope = await _scope.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(employeeId)) return NotFound(NoSelfie); // outside scope answers like a missing punch
        var callerUserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var uid) ? uid : (Guid?)null;
        if (scope.CallerEmployeeId == employeeId
            || await Zayra.Api.Infrastructure.Approvals.SubjectDecisionBar.CallerIsSubjectAsync(Db, tenantId, callerUserId, employeeId, ct))
            return StatusCode(StatusCodes.Status403Forbidden, Refusal("evidence_own_record",
                "You cannot review the selfie on your own punch. Another HR reviewer must open it.",
                "لا يمكنك مراجعة الصورة الذاتية المرفقة بتسجيلك أنت. يجب أن يفتحها مراجع آخر من الموارد البشرية."));

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

    public static async Task<T> RunAsync<T>(ZayraDbContext db, Guid tenantId, int employeeId, Func<Task<T>> work, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await work();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retried attempt starts clean.
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if ((db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                var lockKey = Key(tenantId, employeeId);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
            }
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
    /// <para>Review item 8: the caller's UNUSED selfies (Active or Pending) are then deleted at once, strictly (every
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
        var unused = await Db.AttendanceEvidence
            .Where(e => e.TenantId == tenantId && e.EmployeeId == employeeId && e.UsedAtUtc == null
                        && (e.PurgeState == AttendanceEvidencePurgeStates.Active || e.PurgeState == AttendanceEvidencePurgeStates.Pending))
            .ToListAsync(ct);
        if (unused.Count == 0) return (0, 0);
        if (_storage is null) return (0, unused.Count);

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
        return (deleted, unused.Count - deleted);
    }

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
