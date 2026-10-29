using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Infrastructure.Http;

namespace Zayra.Api.Controllers;

/// <summary>Every custody action carries a fresh authenticator code (<c>totpCode</c>); the passphrase / key are the CLIENT's, never stored or logged.</summary>
public sealed record RecoveryExportRequest(string? Passphrase, string? PublicKeyPem, string? TotpCode);
public sealed record RecoveryRestoreRequest(string RecoveryCopy, string? Passphrase, string? PrivateKeyPem, string? TotpCode);
public sealed record RevokeRequest(string Confirm, string? TotpCode);
public sealed record KeyCustodyStepUpRequest(string? TotpCode);
public sealed record SliceRequest(int MaxRows = 2000, Dictionary<string, int>? Cursor = null);

internal static class CustodyResults
{
    /// <summary>Maps the custody exceptions to stable status codes; anything else is rethrown to the global handler.</summary>
    public static async Task<IActionResult> Run(ControllerBase c, Func<Task<IActionResult>> body)
    {
        try { return await body(); }
        catch (StepUpRequiredException ex) { return new ObjectResult(new { code = "mfa_step_up_required", error = ex.Message }) { StatusCode = StatusCodes.Status403Forbidden }; }
        catch (CustodyForbiddenException ex) { return new ObjectResult(new { code = "forbidden", error = ex.Message }) { StatusCode = StatusCodes.Status403Forbidden }; }
        catch (ArgumentException ex) { return c.BadRequest(new { error = ex.Message }); }
        catch (CryptographicException) { return c.BadRequest(new { error = "The recovery copy could not be opened with that passphrase or key." }); }
        catch (FieldEncryptionUnavailableException ex) { return new ObjectResult(new { code = ex.Code, error = ex.SafeMessage }) { StatusCode = ex.StatusCode }; }
        catch (InvalidOperationException ex) { return c.Conflict(new { error = ex.Message }); }
    }
}

/// <summary>
/// The client's own custody controls over its field-encryption keys: download a recovery copy (with a passphrase or public key
/// the client supplies), request / cancel a revoke of the platform's ability to decrypt, restore from the copy. Only the tenant's
/// top administrator (Admin role holding security.manage), each with a fresh authenticator code.
/// </summary>
[ApiController]
[Route("api/security/field-encryption")]
[HasPermission("security.manage")]
[NoStore]
public class TenantFieldEncryptionController(FieldEncryptionAdminService admin) : ControllerBase
{
    private Guid TenantId => Guid.Parse(User.FindFirstValue("tenant_id") ?? throw new UnauthorizedAccessException());
    private RequestContext Ctx => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(),
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var u) ? u : null, TenantId);
    private bool TopAdmin => User.IsInRole("Admin");

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct) => TopAdmin ? Ok(await admin.StatusAsync(TenantId, ct)) : Forbid();

    [HttpPost("recovery-copy")]
    public Task<IActionResult> Export([FromBody] RecoveryExportRequest req, CancellationToken ct) =>
        !TopAdmin ? Task.FromResult<IActionResult>(Forbid())
            : CustodyResults.Run(this, async () => Ok(await admin.ExportRecoveryAsync(TenantId, req.Passphrase, req.PublicKeyPem, req.TotpCode, ActorKind.TenantAdmin, Ctx, ct)));

    /// <summary>Requests a revoke. 202: it cools off (see <c>effectiveAtUtc</c>) and another administrator can cancel it.</summary>
    [HttpPost("revoke")]
    public Task<IActionResult> Revoke([FromBody] RevokeRequest req, CancellationToken ct)
    {
        if (!TopAdmin) return Task.FromResult<IActionResult>(Forbid());
        if (req.Confirm != "REVOKE") return Task.FromResult<IActionResult>(BadRequest(new { error = "Send {\"confirm\":\"REVOKE\"} to confirm." }));
        return CustodyResults.Run(this, async () => Accepted(await admin.RequestRevokeAsync(TenantId, req.TotpCode, ActorKind.TenantAdmin, Ctx, ct)));
    }

    [HttpPost("revoke/cancel")]
    public Task<IActionResult> CancelRevoke([FromBody] KeyCustodyStepUpRequest req, CancellationToken ct) =>
        !TopAdmin ? Task.FromResult<IActionResult>(Forbid())
            : CustodyResults.Run(this, async () => { await admin.CancelRevokeAsync(TenantId, req.TotpCode, ActorKind.TenantAdmin, Ctx, ct); return NoContent(); });

    [HttpPost("restore")]
    public Task<IActionResult> Restore([FromBody] RecoveryRestoreRequest req, CancellationToken ct) =>
        !TopAdmin ? Task.FromResult<IActionResult>(Forbid())
            : CustodyResults.Run(this, async () => { await admin.RestoreAsync(TenantId, req.RecoveryCopy, req.Passphrase, req.PrivateKeyPem, req.TotpCode, ActorKind.TenantAdmin, Ctx, ct); return NoContent(); });
}

/// <summary>
/// Platform-operator side. There is deliberately NO export and NO revoke here: the client's keys are the client's to copy and to
/// revoke. A platform operator can restore (with the client's own copy and passphrase / key), rotate a DEK, and run the
/// backfill / re-encrypt / verify data movement. Restore and rotate need the operator's fresh authenticator code.
/// </summary>
[ApiController]
[Route("api/platform/field-encryption/{tenantId:guid}")]
[Authorize(Policy = "PlatformAdmin")]
[NoStore]
public class PlatformFieldEncryptionController(FieldEncryptionAdminService admin, FieldEncryptionBackfill backfill, Zayra.Api.Application.Auth.IAuditService audit) : ControllerBase
{
    private RequestContext Ctx(Guid tenantId) => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(),
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var u) ? u : null, tenantId);

    private Task BulkAudit(string action, Guid tenantId, string detail, CancellationToken ct) =>
        audit.RecordEventAsync($"FieldEncryption.{action}", "FieldEncryption", tenantId.ToString(), Ctx(tenantId), $"actor=platform-operator; {detail}", ct);

    [HttpGet]
    public async Task<IActionResult> Status(Guid tenantId, CancellationToken ct) => Ok(new { status = await admin.StatusAsync(tenantId, ct), progress = await backfill.ProgressAsync(tenantId, ct) });

    [HttpPost("restore")]
    [RequirePlatformRole(PlatformRoles.Admin)]
    public Task<IActionResult> Restore(Guid tenantId, [FromBody] RecoveryRestoreRequest req, CancellationToken ct) =>
        CustodyResults.Run(this, async () => { await admin.RestoreAsync(tenantId, req.RecoveryCopy, req.Passphrase, req.PrivateKeyPem, req.TotpCode, ActorKind.PlatformOperator, Ctx(tenantId), ct); return NoContent(); });

    [HttpPost("rotate-dek")]
    [RequirePlatformRole(PlatformRoles.Admin)]
    public Task<IActionResult> Rotate(Guid tenantId, [FromBody] KeyCustodyStepUpRequest req, CancellationToken ct) =>
        CustodyResults.Run(this, async () => Ok(new { activeVersion = await admin.RotateDekAsync(tenantId, req.TotpCode, ActorKind.PlatformOperator, Ctx(tenantId), ct) }));

    /// <summary>KEK rotation step: rewrap this tenant's key rows under the active KEK version (run for every tenant before removing the old KEK).</summary>
    [HttpPost("rewrap-kek")]
    [RequirePlatformRole(PlatformRoles.Admin)]
    public Task<IActionResult> RewrapKek(Guid tenantId, [FromBody] KeyCustodyStepUpRequest req, CancellationToken ct) =>
        CustodyResults.Run(this, async () => Ok(new { rowsRewrapped = await admin.RewrapKekAsync(tenantId, req.TotpCode, ActorKind.PlatformOperator, Ctx(tenantId), ct) }));

    [HttpPost("backfill")]
    [RequirePlatformRole(PlatformRoles.Admin)]
    public Task<IActionResult> Backfill(Guid tenantId, [FromBody] SliceRequest req, CancellationToken ct) =>
        CustodyResults.Run(this, async () =>
        {
            var result = await backfill.EncryptMissingAsync(tenantId, Math.Clamp(req.MaxRows, 1, 20000), ct);
            await BulkAudit("BackfillRun", tenantId, $"encrypted={result.Encrypted}; complete={result.Complete}", ct);
            return Ok(result);
        });

    [HttpPost("reencrypt")]
    [RequirePlatformRole(PlatformRoles.Admin)]
    public Task<IActionResult> Reencrypt(Guid tenantId, [FromBody] SliceRequest req, CancellationToken ct) =>
        CustodyResults.Run(this, async () =>
        {
            var result = await backfill.ReencryptToActiveAsync(tenantId, Math.Clamp(req.MaxRows, 1, 20000), ct);
            await BulkAudit("ReencryptRun", tenantId, $"reencrypted={result.Encrypted}; complete={result.Complete}", ct);
            return Ok(result);
        });

    /// <summary>Rebuilds every ciphertext / blind index that disagrees with its (authoritative) plaintext - the fix for what verify reports.</summary>
    [HttpPost("repair")]
    [RequirePlatformRole(PlatformRoles.Admin)]
    public Task<IActionResult> Repair(Guid tenantId, [FromBody] SliceRequest req, CancellationToken ct) =>
        CustodyResults.Run(this, async () =>
        {
            var report = await backfill.RepairAsync(tenantId, Math.Clamp(req.MaxRows, 1, 20000), req.Cursor, ct);
            await BulkAudit("RepairRun", tenantId, $"checked={report.RowsChecked}; repaired={report.Repaired}; complete={report.Complete}", ct);
            return Ok(report);
        });

    [HttpPost("verify")]
    [RequirePlatformRole(PlatformRoles.Admin)]
    public Task<IActionResult> Verify(Guid tenantId, [FromBody] SliceRequest req, CancellationToken ct) =>
        CustodyResults.Run(this, async () =>
        {
            var report = await backfill.VerifyAsync(tenantId, Math.Clamp(req.MaxRows, 1, 50000), req.Cursor, ct);
            await BulkAudit("VerifyRun", tenantId, $"checked={report.RowsChecked}; mismatches={report.MismatchTotal}; complete={report.Complete}", ct);
            return Ok(report);
        });
}

/// <summary>
/// The explicit, one-off DataProtection key-ring operations. Nothing here runs at boot. <c>encrypt</c> is one-way until every image
/// can read an encrypted ring and is refused unless <c>FieldEncryption:EncryptDataProtectionRing</c> is true; <c>rewrap</c> moves
/// entries to the active KEK during a KEK rotation. Each needs the operator's fresh authenticator code.
/// </summary>
[ApiController]
[Route("api/platform/field-encryption/dp-ring")]
[Authorize(Policy = "PlatformAdmin")]
[NoStore]
public class PlatformDataProtectionRingController(DataProtectionRingJob job, IStepUpVerifier stepUp, Zayra.Api.Application.Auth.IAuditService audit) : ControllerBase
{
    private RequestContext Ctx => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(),
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var u) ? u : null, null);

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(await job.StatusAsync(ct));

    [HttpPost("encrypt")]
    [RequirePlatformRole(PlatformRoles.Admin)]
    public Task<IActionResult> Encrypt([FromBody] KeyCustodyStepUpRequest req, CancellationToken ct) => Op("DpRingEncrypt", req, job.EncryptAsync, ct);

    [HttpPost("rewrap")]
    [RequirePlatformRole(PlatformRoles.Admin)]
    public Task<IActionResult> Rewrap([FromBody] KeyCustodyStepUpRequest req, CancellationToken ct) => Op("DpRingRewrap", req, job.RewrapAsync, ct);

    private Task<IActionResult> Op(string action, KeyCustodyStepUpRequest req, Func<CancellationToken, Task<RingJobResult>> run, CancellationToken ct) =>
        CustodyResults.Run(this, async () =>
        {
            if (!await stepUp.VerifyAsync(ActorKind.PlatformOperator, Ctx.UserId, Guid.Empty, req.TotpCode, ct))
            {
                await audit.RecordEventAsync($"FieldEncryption.{action}StepUpFailed", "FieldEncryption", null, Ctx, "actor=platform-operator; a current authenticator code was missing or wrong", ct);
                throw new StepUpRequiredException();
            }
            try
            {
                var result = await run(ct);
                await audit.RecordEventAsync($"FieldEncryption.{action}", "FieldEncryption", null, Ctx, $"actor=platform-operator; rowsChanged={result.RowsChanged}; kekVersionsInUse={string.Join(",", result.After.KekVersionsInUse)}", ct);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                await audit.RecordEventAsync($"FieldEncryption.{action}Refused", "FieldEncryption", null, Ctx, $"actor=platform-operator; reason={ex.Message}", ct);
                throw;
            }
        });
}
