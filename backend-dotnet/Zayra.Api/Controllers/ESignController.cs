using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Application.Esign;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

public sealed record SignatureRequestBody(Guid? DocumentId);
public sealed record EmployeeDeviceStepUpBody(int? EmployeeId, string? Password, string? MfaCode);

/// <summary>
/// Built-in click-to-sign ACKNOWLEDGEMENT (P4). The employee reads the stored PDF, types their name, re-enters their password and
/// confirms; the server records the evidence, a hash-chained append-only row and a bilingual certificate. It is an electronic
/// acknowledgement, not a qualified electronic signature. No external service is called anywhere on this path.
/// <para>Two ways in: the employee's own session (/api/ess/signatures), or HR's device where the EMPLOYEE authenticates with their
/// own credentials (/api/employees/{id}/signatures/.../sign-on-device). An HR session alone can never produce a signature.</para>
/// </summary>
[ApiController]
[Authorize]
[NoStore]
public sealed class ESignController(ZayraDbContext db, PolicySigningService signing, IStepUpService stepUp, IDocumentStorage storage, IConfiguration config) : ControllerBase
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    // ── Employee (self-service) ───────────────────────────────────────────────────────────────────

    [HttpGet("api/ess/signatures")]
    [HasPermission("ess.read")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var me = await ResolveSelfAsync(ct);
        if (me is null) return Err(409, EsignProblemCodes.EssNotLinked);
        return Ok(await ListForAsync(me.Value.TenantId, me.Value.EmployeeId, ct));
    }

    [HttpGet("api/ess/signatures/{documentId:guid}/document")]
    [HasPermission("ess.read")]
    public async Task<IActionResult> Document(Guid documentId, CancellationToken ct)
    {
        var me = await ResolveSelfAsync(ct);
        if (me is null) return Err(409, EsignProblemCodes.EssNotLinked);
        var doc = await OwnSignableAsync(me.Value.TenantId, me.Value.EmployeeId, documentId, ct);
        if (doc is null) return Err(404, EsignProblemCodes.NotFound);
        var bytes = await ReadAsync(me.Value.TenantId, doc, ct);
        if (bytes is null) return Err(404, EsignProblemCodes.NotFound);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, "application/pdf", doc.FileName);
    }

    [HttpGet("api/ess/signatures/{documentId:guid}/consent")]
    [HasPermission("ess.read")]
    public async Task<IActionResult> Consent(Guid documentId, [FromQuery] string? lang, CancellationToken ct)
    {
        var me = await ResolveSelfAsync(ct);
        if (me is null) return Err(409, EsignProblemCodes.EssNotLinked);
        var doc = await OwnSignableAsync(me.Value.TenantId, me.Value.EmployeeId, documentId, ct);
        if (doc is null) return Err(404, EsignProblemCodes.NotFound);
        var bytes = await ReadAsync(me.Value.TenantId, doc, ct);
        if (bytes is null) return Err(404, EsignProblemCodes.NotFound);
        var version = ConsentCatalog.Current;
        return Ok(new
        {
            consentVersion = version, text = ConsentCatalog.Text(version, lang ?? "en"), textAr = ConsentCatalog.Text(version, "ar"), textEn = ConsentCatalog.Text(version, "en"),
            textSha256 = ConsentCatalog.TextSha256(version), documentSha256 = SignatureChain.Sha256Hex(bytes), documentVersion = doc.VersionNumber,
            title = doc.DocumentType, fileName = doc.FileName, integrityFlagged = doc.IntegrityFlaggedAtUtc is not null,
        });
    }

    [HttpPost("api/ess/signatures/{documentId:guid}/sign")]
    [HasPermission("ess.write")]
    public async Task<IActionResult> Sign(Guid documentId, [FromBody] EsignSignBody? body, CancellationToken ct)
    {
        var me = await ResolveSelfAsync(ct);
        if (me is null) return Err(409, EsignProblemCodes.EssNotLinked);
        if (body is null) return Err(400, EsignProblemCodes.ValidationFailed);
        var userId = me.Value.UserId;
        if (userId is null) return Err(409, EsignProblemCodes.EssNotLinked);
        var outcome = await signing.SignAsync(new SignCommand(me.Value.TenantId, me.Value.EmployeeId, userId.Value, userId.Value, null, documentId, body,
            Request.Headers[EsignConstants.StepUpHeader].FirstOrDefault(), ClientIp(), Request.Headers.UserAgent.ToString()), ct);
        return Reply(outcome);
    }

    [HttpGet("api/ess/signatures/{ackId:guid}/verify")]
    [HasPermission("ess.read")]
    public async Task<IActionResult> VerifyMine(Guid ackId, CancellationToken ct)
    {
        var me = await ResolveSelfAsync(ct);
        if (me is null) return Err(409, EsignProblemCodes.EssNotLinked);
        return VerifyReply(await signing.VerifyAsync(me.Value.TenantId, ackId, me.Value.EmployeeId, ct));
    }

    [HttpGet("api/ess/signatures/{ackId:guid}/certificate")]
    [HasPermission("ess.read")]
    public async Task<IActionResult> Certificate(Guid ackId, CancellationToken ct)
    {
        var me = await ResolveSelfAsync(ct);
        if (me is null) return Err(409, EsignProblemCodes.EssNotLinked);
        var cert = await (from a in db.EmployeePolicyAcknowledgements.AsNoTracking()
                          join d in db.EmployeeDocuments.AsNoTracking() on a.CertificateDocumentId equals d.Id
                          where a.TenantId == me.Value.TenantId && a.Id == ackId && a.EmployeeId == me.Value.EmployeeId && !d.IsDeleted
                          select d).FirstOrDefaultAsync(ct);
        if (cert is null) return Err(404, EsignProblemCodes.NotFound);
        var bytes = await ReadAsync(me.Value.TenantId, cert, ct);
        if (bytes is null) return Err(404, EsignProblemCodes.NotFound);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, "application/pdf", cert.FileName);
    }

    // ── HR ────────────────────────────────────────────────────────────────────────────────────────

    [HttpPost("api/employees/{id:int}/signature-requests")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> RequestSignature(int id, [FromBody] SignatureRequestBody? body, CancellationToken ct)
    {
        if (!HrMayManageDocuments()) return Forbid();
        var tenantId = TenantId();
        if (body?.DocumentId is not { } documentId) return Err(400, EsignProblemCodes.ValidationFailed);
        if (!await db.Employees.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Id == id && !e.IsDeleted, ct)) return Err(404, EsignProblemCodes.NotFound);
        var doc = await db.EmployeeDocuments.FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == documentId && d.EmployeeId == id && !d.IsDeleted, ct);
        if (doc is null) return Err(404, EsignProblemCodes.NotFound);
        if (await db.EmployeePolicyAcknowledgements.AsNoTracking().AnyAsync(a => a.TenantId == tenantId && a.EmployeeId == id && a.PolicyId == documentId, ct)) return Err(409, EsignProblemCodes.AlreadySigned);
        if (RestrictedEmployeeDocumentTypes.IsRestricted(doc.DocumentType) || string.Equals(doc.DocumentType, EsignConstants.DocumentType, StringComparison.OrdinalIgnoreCase)
            || EmployeeDocumentDto.IsSensitiveDocument(doc.DocumentType, doc.DocumentCategory) || doc.ScanStatus != "Clean"
            || !string.Equals(doc.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            return Err(422, EsignProblemCodes.DocumentNotSignable);
        var bytes = await ReadAsync(tenantId, doc, ct);
        if (bytes is null || bytes.Length < PdfMagic.Length || !bytes.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic)) return Err(422, EsignProblemCodes.DocumentNotSignable);
        var now = DateTime.UtcNow;
        doc.SignatureRequiredAtUtc ??= now;
        doc.SignatureRequestedBy ??= CallerId();
        db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog { TenantId = tenantId, EmployeeId = id, UserId = CallerId(), Action = "esign.requested", EntityName = "EmployeeDocument", EntityId = documentId.ToString() });
        await db.SaveChangesAsync(ct);
        return Created($"/api/employees/{id}/signatures", new { documentId, requestedAtUtc = doc.SignatureRequiredAtUtc });
    }

    [HttpGet("api/employees/{id:int}/signatures")]
    [HasPermission("employees.read")]
    public async Task<IActionResult> ListForEmployee(int id, CancellationToken ct)
    {
        if (!HrMayManageDocuments(requireWrite: false)) return Forbid();
        var tenantId = TenantId();
        if (!await db.Employees.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Id == id && !e.IsDeleted, ct)) return Err(404, EsignProblemCodes.NotFound);
        return Ok(await ListForAsync(tenantId, id, ct));
    }

    [HttpGet("api/employees/{id:int}/signatures/{ackId:guid}/verify")]
    [HasPermission("employees.read")]
    public async Task<IActionResult> VerifyForEmployee(int id, Guid ackId, CancellationToken ct)
    {
        if (!HrMayManageDocuments(requireWrite: false)) return Forbid();
        var tenantId = TenantId();
        if (!await db.Employees.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Id == id && !e.IsDeleted, ct)) return Err(404, EsignProblemCodes.NotFound);
        return VerifyReply(await signing.VerifyAsync(tenantId, ackId, id, ct));
    }

    /// <summary>The employee types their OWN password on HR's device. HR's password is never accepted here, and HR sees only success or failure.</summary>
    [HttpPost("api/auth/step-up/employee-device")]
    [HasPermission("employees.write")]
    [EnableRateLimiting("esign_device")]
    public async Task<IActionResult> EmployeeDeviceStepUp([FromBody] EmployeeDeviceStepUpBody? body, CancellationToken ct)
    {
        if (!HrMayManageDocuments()) return Forbid();
        if (body?.EmployeeId is not { } employeeId) return Err(400, EsignProblemCodes.ValidationFailed);
        var tenantId = TenantId();
        var employeeUser = await EmployeeUserAsync(tenantId, employeeId, ct);
        if (employeeUser is null) return Err(404, EsignProblemCodes.NotFound);
        if (employeeUser == CallerId()) return Err(403, EsignProblemCodes.SubjectCannotSignForEmployee);
        var result = await stepUp.VerifyAsync(employeeUser.Value, StepUpPurposes.Esign, new StepUpCredential(body.Password, body.MfaCode), ct);
        db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog
        {
            TenantId = tenantId, EmployeeId = employeeId, UserId = CallerId(), Action = result.Ok ? "esign.device_auth_ok" : "esign.device_auth_failed", EntityName = "Employee", EntityId = employeeId.ToString(),
        });
        await db.SaveChangesAsync(ct);
        if (!result.Ok) return Err(401, EsignProblemCodes.EmployeeDeviceAuthFailed, detail: result.Error == EssProblemCodes.MfaRequired ? EssProblemCodes.MfaRequired : null);
        return Ok(new { stepUpToken = result.Token, expiresAtUtc = result.ExpiresAtUtc });
    }

    [HttpPost("api/employees/{id:int}/signatures/{documentId:guid}/sign-on-device")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> SignOnDevice(int id, Guid documentId, [FromBody] EsignSignBody? body, CancellationToken ct)
    {
        if (!HrMayManageDocuments()) return Forbid();
        if (body is null) return Err(400, EsignProblemCodes.ValidationFailed);
        var tenantId = TenantId();
        var employeeUser = await EmployeeUserAsync(tenantId, id, ct);
        if (employeeUser is null) return Err(404, EsignProblemCodes.NotFound);
        var hr = CallerId();
        if (hr is null || employeeUser == hr) return Err(403, EsignProblemCodes.SubjectCannotSignForEmployee);
        var outcome = await signing.SignAsync(new SignCommand(tenantId, id, employeeUser.Value, employeeUser.Value, hr, documentId, body,
            Request.Headers[EsignConstants.StepUpHeader].FirstOrDefault(), ClientIp(), Request.Headers.UserAgent.ToString()), ct);
        return Reply(outcome);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    private ObjectResult Err(int status, string code, Guid? ackId = null, string? detail = null)
    {
        var problem = new Dictionary<string, object?>
        {
            ["type"] = "about:blank", ["title"] = code, ["status"] = status, ["code"] = code, ["titleKey"] = EsignConstants.ProblemKeyPrefix + code, ["traceId"] = HttpContext.TraceIdentifier,
        };
        if (ackId is not null) problem["ackId"] = ackId;
        if (detail is not null) problem["detail"] = detail;
        if (status == 401 && code == EsignProblemCodes.StepUpRequired) Response.Headers.WWWAuthenticate = $"StepUp purpose=\"{StepUpPurposes.Esign}\"";
        if (status == 429) Response.Headers.RetryAfter = "3600";
        var result = new ObjectResult(problem) { StatusCode = status };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    private IActionResult Reply(SignOutcome o) => o.Ok
        ? Created($"/api/ess/signatures/{o.AckId}/verify", new { ackId = o.AckId, signedAtUtc = o.SignedAtUtc, recordHash = o.RecordHash, certificateDocumentId = o.CertificateDocumentId, verificationCode = o.VerificationCode })
        : Err(o.Status, o.Code!, o.AckId);

    private IActionResult VerifyReply(VerifyOutcome v) => !v.Found
        ? Err(404, EsignProblemCodes.NotFound)
        : Ok(new { valid = v.Valid, documentIntact = v.DocumentIntact, chainIntact = v.ChainIntact, recordHash = v.RecordHash, checkedAtUtc = v.CheckedAtUtc });

    private Guid TenantId() => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid? CallerId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
    private string ClientIp() => ClientIpResolver.Resolve(HttpContext, config[ClientIpResolver.SecretConfigKey]);

    /// <summary>HasPermission is any-of; HR's side needs the pair, as the document download does.</summary>
    private bool HrMayManageDocuments(bool requireWrite = true) =>
        User.HasPermission("employees.documents") && (!requireWrite || User.HasPermission("employees.write"));

    private async Task<(Guid TenantId, int EmployeeId, Guid? UserId)?> ResolveSelfAsync(CancellationToken ct)
    {
        if (User.FindFirstValue("access_mode") is "NoLogin" or "KioskOnly" || !Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)) return null;
        var employeeId = await CallerEmployeeResolver.ResolveAsync(db, User, tenantId, ct);
        return employeeId is null ? null : (tenantId, employeeId.Value, CallerId());
    }

    private async Task<Guid?> EmployeeUserAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        if (!await db.Employees.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted, ct)) return null;   // out-of-scope employees do not resolve
        return await db.EmployeeUserAccounts.AsNoTracking().Where(a => a.TenantId == tenantId && a.EmployeeId == employeeId && a.Status == "Active" && a.UserId != null)
            .OrderByDescending(a => a.IsPrimary).Select(a => a.UserId).FirstOrDefaultAsync(ct);
    }

    private Task<EmployeeDocument?> OwnSignableAsync(Guid tenantId, int employeeId, Guid documentId, CancellationToken ct) =>
        db.EmployeeDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == documentId && d.EmployeeId == employeeId && !d.IsDeleted
            && d.SignatureRequiredAtUtc != null && d.ScanStatus == "Clean", ct);

    private async Task<byte[]?> ReadAsync(Guid tenantId, EmployeeDocument doc, CancellationToken ct)
    {
        try { return await storage.GetBytesAsync(tenantId, doc.StorageUrl, ct); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException) { return null; }
    }

    private async Task<List<object>> ListForAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var docs = await db.EmployeeDocuments.AsNoTracking().Where(d => d.TenantId == tenantId && d.EmployeeId == employeeId && !d.IsDeleted && d.SignatureRequiredAtUtc != null && d.ScanStatus == "Clean")
            .OrderByDescending(d => d.SignatureRequiredAtUtc).ToListAsync(ct);
        var ids = docs.Select(d => d.Id).ToList();
        var acks = await db.EmployeePolicyAcknowledgements.AsNoTracking().Where(a => a.TenantId == tenantId && a.EmployeeId == employeeId && ids.Contains(a.PolicyId)).ToListAsync(ct);
        return docs.Select(d =>
        {
            var ack = acks.FirstOrDefault(a => a.PolicyId == d.Id);
            var superseded = ack is null && docs.Any(o => o.Id != d.Id && o.DocumentType == d.DocumentType && o.VersionNumber > d.VersionNumber);
            return (object)new
            {
                documentId = d.Id, title = d.DocumentType, fileName = d.FileName, status = ack is not null ? "Signed" : superseded ? "Superseded" : "Awaiting", requestedAtUtc = d.SignatureRequiredAtUtc,
                ackId = ack?.Id, signedAtUtc = ack?.AcknowledgedAtUtc, sha256Prefix = ack?.DocumentSha256?[..12], method = ack?.Method, typedName = ack?.TypedName,
                verificationCode = ack?.RecordHash is { } h ? VerificationCode.From(h) : null, certificateDocumentId = ack?.CertificateDocumentId, integrityFlagged = d.IntegrityFlaggedAtUtc is not null,
            };
        }).ToList();
    }
}
