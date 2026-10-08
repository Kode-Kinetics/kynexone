using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
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
/// POST /api/attendance/evidence/selfie. The route sits under the <c>selfie_attendance</c> opt-in prefix, so the global
/// FeatureFlagGuardFilter closes it while the flag is off; the action checks the flag itself too.
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

    private readonly IDocumentStorage _storage;
    private readonly SelfieImageGate _gate;

    public AttendanceEvidenceController(ZayraDbContext db, IDocumentStorage storage, AttendanceVerificationService? verification = null,
        SelfieImageGate? gate = null)
        : base(db, verification)
    {
        _storage = storage;
        // DI supplies the process-wide singleton; a hand-built controller (tests) gets its own.
        _gate = gate ?? new SelfieImageGate();
    }

    /// <summary>
    /// Stores one selfie for the caller and returns an opaque evidence id: single-use, bound to the caller, valid for
    /// 10 minutes. In this order, so a hostile upload costs as little as possible:
    /// <list type="number">
    ///   <item>the caller, the flag (with sign-offs and storage residency) and consent — nothing is read yet;</item>
    ///   <item>the declared size (≤ 8 MB) and the hourly limit: the attempt is RESERVED as a <c>Pending</c> row under a
    ///     per-employee advisory lock, so every attempt counts, finished or not, and parallel uploads cannot overshoot;</item>
    ///   <item>only then the body is read; the photo's canvas is checked from its header (≤ 16 MP) and it is decoded
    ///     downsampled inside a process-wide gate of two (busy: 429 at once);</item>
    ///   <item>the re-encoded JPEG (EXIF and GPS stripped, ≤ 512 px) is stored at the key derived from the row's id, and
    ///     the row flips to <c>Active</c>. The original bytes are never stored.</item>
    /// </list>
    /// A Pending row whose upload never completed is purged (with any file) after an hour.
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

        var attempt = await ReserveAttemptAsync(tenantId, employeeId, ct);
        if (attempt is null) return StatusCode(StatusCodes.Status429TooManyRequests, AttendanceRefusals.RateLimited.Body);

        // ── Only now is the body read. ──
        IFormFile? file;
        try
        {
            var form = await Request.ReadFormAsync(ct);
            file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { return TooLarge(); }
        catch (InvalidDataException ex) when (ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)) { return TooLarge(); }
        catch (InvalidDataException) { return BadRequest(Refusal("selfie_invalid", "The selfie could not be read. Take it again.", "تعذّرت قراءة الصورة. التقطها مرة أخرى.")); }
        if (file is null || file.Length <= 0)
            return BadRequest(Refusal("selfie_missing", "Take a selfie first, then try again.", "التقط صورة ذاتية أولًا ثم حاول مرة أخرى."));
        if (file.Length > MaxRequestBytes) return TooLarge();

        byte[] source;
        await using (var input = file.OpenReadStream())
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await input.CopyToAsync(buffer, ct);
            source = buffer.ToArray();
        }
        var verdict = EssUploadPolicy.Check(file.ContentType, file.FileName, source, MaxRequestBytes, EssUploadPolicy.PhotoTypes);
        if (!verdict.Ok) return BadRequest(Refusal("selfie_invalid", verdict.Error ?? "The selfie must be a JPEG or PNG photo.", "يجب أن تكون الصورة بصيغة JPEG أو PNG."));

        if (!_gate.TryEnter())
        {
            Response.Headers.RetryAfter = "5";
            return StatusCode(StatusCodes.Status429TooManyRequests, AttendanceRefusals.SelfieBusy.Body);
        }
        byte[] jpeg;
        try { jpeg = ProfilePhotoProcessor.ToSanitisedJpeg(source, MaxSourcePixels, DecodeLongEdge); }
        catch (ImageTooLargeException)
        {
            return BadRequest(Refusal("selfie_too_large",
                "The photo is larger than 16 megapixels. Take it again with the app's camera.",
                "الصورة أكبر من 16 ميغابكسل. التقطها مرة أخرى بكاميرا التطبيق."));
        }
        catch (InvalidDataException) { return BadRequest(Refusal("selfie_invalid", "The selfie could not be read as a photo. Take it again.", "تعذّرت قراءة الصورة. التقطها مرة أخرى.")); }
        finally { _gate.Exit(); }
        source = [];
        var sha256 = Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant();

        try { await _storage.PutAtAsync(tenantId, attempt.StorageKey, jpeg, EssUploadPolicy.Jpeg, ct); }
        catch
        {
            // The row stays Pending (unusable) and the purge deletes any partial file within the hour; try now too.
            try { await _storage.DeleteStrictAsync(tenantId, attempt.StorageKey, CancellationToken.None); } catch { /* the purge retries */ }
            throw;
        }

        Db.ChangeTracker.Clear();
        var row = await Db.AttendanceEvidence.FirstAsync(e => e.TenantId == tenantId && e.Id == attempt.Id, ct);
        if (row.PurgeState != AttendanceEvidencePurgeStates.Pending)
        {
            await _storage.DeleteStrictAsync(tenantId, attempt.StorageKey, CancellationToken.None);
            throw new InvalidOperationException($"Selfie evidence {row.Id} left Pending before its upload finished.");
        }
        var now = DateTime.UtcNow;
        row.PurgeState = AttendanceEvidencePurgeStates.Active;
        row.Sha256 = sha256;
        row.ByteSize = jpeg.Length;
        row.ExpiresAtUtc = now + AttendanceVerificationService.EvidenceLifetime;
        Audit(tenantId, "attendance.selfie.uploaded", "AttendanceEvidence", row.Id.ToString(),
            new { employeeId, sha256, byteSize = jpeg.Length, expiresAtUtc = row.ExpiresAtUtc });
        await Db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, new { evidenceId = row.Id, expiresAtUtc = row.ExpiresAtUtc });
    }

    private ObjectResult TooLarge() => StatusCode(StatusCodes.Status413PayloadTooLarge, Refusal("selfie_too_large",
        "The selfie is larger than 8 MB. Take it again at a lower resolution.",
        "حجم الصورة أكبر من 8 ميغابايت. التقطها مرة أخرى بدقة أقل."));

    /// <summary>
    /// Reserves one upload attempt: under a transaction-scoped advisory lock on (tenant, employee), counts the
    /// employee's attempts in the last hour (every row, whatever its state) and, below the limit, inserts the Pending
    /// row with its storage key derived from its id. Null when rate-limited. Commits before the body is read, so the
    /// attempt counts even if the upload then fails.
    /// </summary>
    private async Task<AttendanceEvidence?> ReserveAttemptAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        if (!Db.Database.IsRelational()) return await ReserveCoreAsync(tenantId, employeeId, ct);
        var strategy = Db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retried attempt starts clean: nothing else is tracked on this request's context yet.
            Db.ChangeTracker.Clear();
            await using var tx = await Db.Database.BeginTransactionAsync(ct);
            if ((Db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                var lockKey = $"attendance-evidence-upload:{tenantId:N}:{employeeId}";
                await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
            }
            var reserved = await ReserveCoreAsync(tenantId, employeeId, ct);
            await tx.CommitAsync(ct);
            return reserved;
        });
    }

    private async Task<AttendanceEvidence?> ReserveCoreAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = now.AddHours(-1);
        var attempts = await Db.AttendanceEvidence.CountAsync(e => e.TenantId == tenantId && e.EmployeeId == employeeId && e.CreatedAtUtc > since, ct);
        if (attempts >= AttendanceVerificationService.MaxUploadsPerHour) return null;

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
        return evidence;
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
    /// which retries it within the hour; the withdrawal itself never fails because of it.</para>
    /// </summary>
    [HttpPost("biometric-consent/withdraw")]
    [HttpDelete("biometric-consent")]
    public async Task<IActionResult> WithdrawConsent([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] WithdrawBiometricConsentRequest? request, CancellationToken ct)
    {
        var (ok, tenantId, employeeId, error) = await CallerAsync(ct);
        if (!ok) return error!;

        var open = await Db.BiometricConsents
            .Where(c => c.TenantId == tenantId && c.EmployeeId == employeeId && c.WithdrawnAtUtc == null)
            .ToListAsync(ct);
        if (open.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var consent in open)
            {
                consent.WithdrawnAtUtc = now;
                Audit(tenantId, "attendance.biometric_consent.withdrawn", "BiometricConsent", consent.Id.ToString(),
                    new { employeeId, policyVersion = consent.PolicyVersion, channel = BiometricConsentChannels.Normalize(request?.Channel) });
            }
            await Db.SaveChangesAsync(ct);
        }
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
