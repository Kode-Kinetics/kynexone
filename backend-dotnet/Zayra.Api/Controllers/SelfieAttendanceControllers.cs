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
    private const long MaxSelfieBytes = EssUploadPolicy.MaxPhotoBytes;

    private readonly IDocumentStorage _storage;

    public AttendanceEvidenceController(ZayraDbContext db, IDocumentStorage storage, AttendanceVerificationService? verification = null)
        : base(db, verification)
    {
        _storage = storage;
    }

    /// <summary>
    /// Stores one selfie for the caller and returns an opaque evidence id: single-use, bound to the caller, valid for
    /// 10 minutes. The image is decoded and re-encoded server-side (EXIF and GPS stripped, ≤512 px); the original bytes
    /// are never stored. At most 10 uploads per employee per hour.
    /// </summary>
    [HttpPost("selfie")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxSelfieBytes + EssUploadPolicy.MultipartOverheadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxSelfieBytes + EssUploadPolicy.MultipartOverheadBytes)]
    public async Task<IActionResult> UploadSelfie([FromForm] SelfieUploadForm form, CancellationToken ct)
    {
        var (ok, tenantId, employeeId, error) = await CallerAsync(ct, requireActive: true);
        if (!ok) return error!;

        var policy = await Verification.GetPolicyAsync(tenantId, ct);
        if (!policy.SelfieEnabled) return StatusCode(StatusCodes.Status403Forbidden, AttendanceRefusals.SelfieNotEnabled.Body);
        if (await Verification.ActiveConsentAsync(tenantId, employeeId, policy.ConsentPolicyVersion, ct) is null)
            return StatusCode(StatusCodes.Status403Forbidden, AttendanceRefusals.ConsentRequired.Body);

        if (form.File is null || form.File.Length <= 0)
            return BadRequest(Refusal("selfie_missing", "Take a selfie first, then try again.", "التقط صورة ذاتية أولًا ثم حاول مرة أخرى."));
        if (form.File.Length > MaxSelfieBytes)
            return BadRequest(Refusal("selfie_too_large", "The selfie is larger than 5 MB. Take it again at a lower resolution.", "حجم الصورة أكبر من 5 ميغابايت. التقطها مرة أخرى بدقة أقل."));

        byte[] source;
        await using (var input = form.File.OpenReadStream())
        using (var buffer = new MemoryStream((int)form.File.Length))
        {
            await input.CopyToAsync(buffer, ct);
            source = buffer.ToArray();
        }
        var verdict = EssUploadPolicy.Check(form.File.ContentType, form.File.FileName, source, MaxSelfieBytes, EssUploadPolicy.PhotoTypes);
        if (!verdict.Ok) return BadRequest(Refusal("selfie_invalid", verdict.Error ?? "The selfie must be a JPEG or PNG photo.", "يجب أن تكون الصورة بصيغة JPEG أو PNG."));

        byte[] jpeg;
        try { jpeg = ProfilePhotoProcessor.ToSanitisedJpeg(source); }
        catch (InvalidDataException) { return BadRequest(Refusal("selfie_invalid", "The selfie could not be read as a photo. Take it again.", "تعذّرت قراءة الصورة. التقطها مرة أخرى.")); }
        var sha256 = Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant();

        AttendanceEvidence? created = null;
        var storedKeys = new List<string>();
        try
        {
            if (Db.Database.IsRelational())
            {
                var strategy = Db.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    // A retried attempt starts clean: nothing else is tracked on this request's context.
                    Db.ChangeTracker.Clear();
                    await using var tx = await Db.Database.BeginTransactionAsync(ct);
                    created = await StoreAsync(tenantId, employeeId, jpeg, sha256, storedKeys.Add, ct);
                    if (created is not null) await tx.CommitAsync(ct);
                });
            }
            else
            {
                created = await StoreAsync(tenantId, employeeId, jpeg, sha256, storedKeys.Add, ct);
            }
        }
        catch
        {
            // The row did not commit: remove every blob this request stored so a failed upload leaves no image behind.
            foreach (var key in storedKeys) await _storage.TryDeleteAsync(tenantId, key, CancellationToken.None);
            throw;
        }
        // A transient retry may have stored a blob for an attempt that then rolled back; only the committed one stays.
        foreach (var key in storedKeys.Where(k => k != created?.StorageKey))
            await _storage.TryDeleteAsync(tenantId, key, CancellationToken.None);

        if (created is null) return StatusCode(StatusCodes.Status429TooManyRequests, AttendanceRefusals.RateLimited.Body);
        return StatusCode(StatusCodes.Status201Created, new { evidenceId = created.Id, expiresAtUtc = created.ExpiresAtUtc });
    }

    /// <summary>
    /// Inside the caller's transaction: locks the employee row (so concurrent uploads for one employee serialise on the
    /// rate limit), counts the last hour, stores the blob and stages the row and its audit. Null when rate-limited.
    /// </summary>
    private async Task<AttendanceEvidence?> StoreAsync(Guid tenantId, int employeeId, byte[] jpeg, string sha256, Action<string> onStored, CancellationToken ct)
    {
        await Db.Employees.Where(e => e.TenantId == tenantId && e.Id == employeeId)
            .Select(e => e.Id)
            .TagWith(RowLockingInterceptor.ForUpdateTag)
            .FirstOrDefaultAsync(ct);

        var now = DateTime.UtcNow;
        var since = now.AddHours(-1);
        var recent = await Db.AttendanceEvidence.CountAsync(e => e.TenantId == tenantId && e.EmployeeId == employeeId && e.CreatedAtUtc > since, ct);
        if (recent >= AttendanceVerificationService.MaxUploadsPerHour) return null;

        var stored = await _storage.SaveAsync(tenantId, new FormFile(new MemoryStream(jpeg), 0, jpeg.Length, "file", "attendance-selfie.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = EssUploadPolicy.Jpeg,
        }, ct);
        onStored(stored.StorageUrl);

        var evidence = new AttendanceEvidence
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            StorageKey = stored.StorageUrl,
            Sha256 = sha256,
            ContentType = EssUploadPolicy.Jpeg,
            ByteSize = jpeg.Length,
            CreatedAtUtc = now,
            ExpiresAtUtc = now + AttendanceVerificationService.EvidenceLifetime,
        };
        Db.AttendanceEvidence.Add(evidence);
        Audit(tenantId, "attendance.selfie.uploaded", "AttendanceEvidence", evidence.Id.ToString(),
            new { employeeId, sha256, byteSize = jpeg.Length, expiresAtUtc = evidence.ExpiresAtUtc });
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
    public EssAttendanceVerificationController(ZayraDbContext db, AttendanceVerificationService? verification = null)
        : base(db, verification) { }

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
        return Ok(await BuildViewAsync(tenantId, employeeId, ct));
    }

    private async Task<object> BuildViewAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var policy = await Verification.GetPolicyAsync(tenantId, ct);
        var consent = await Verification.ActiveConsentAsync(tenantId, employeeId, policy.ConsentPolicyVersion, ct);
        var sites = policy.GeofenceEnforced ? await Verification.SitesForAsync(tenantId, employeeId, ct) : [];
        return new
        {
            selfie = new
            {
                enabled = policy.SelfieEnabled,
                // The step is offered only to a consenting employee; required only if the tenant chose so for them.
                step = !policy.SelfieEnabled ? "off" : consent is null ? "consent_needed" : policy.RequireSelfieForConsented ? "required" : "optional",
                requiredForConsented = policy.RequireSelfieForConsented,
                currentPolicyVersion = policy.ConsentPolicyVersion,
                consent = consent is null ? null : new { consent.PolicyVersion, consent.GivenAtUtc, consent.Channel },
                evidenceLifetimeSeconds = (int)AttendanceVerificationService.EvidenceLifetime.TotalSeconds,
                maxUploadsPerHour = AttendanceVerificationService.MaxUploadsPerHour,
            },
            geofence = new
            {
                enforced = policy.GeofenceEnforced,
                maxAccuracyMeters = policy.GeofenceEnforced ? policy.MaxAccuracyMeters : (int?)null,
                allowMockedLocation = policy.GeofenceEnforced ? policy.AllowMockedLocation : (bool?)null,
                sites = sites.Select(s => new { s.Name, s.Latitude, s.Longitude, s.RadiusMeters }),
            },
        };
    }

}

public sealed class SelfieUploadForm
{
    public IFormFile? File { get; set; }
}

public sealed record GiveBiometricConsentRequest(string? PolicyVersion, string? Channel);

public sealed record WithdrawBiometricConsentRequest(string? Channel);
