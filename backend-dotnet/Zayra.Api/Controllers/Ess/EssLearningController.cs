using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Learning;

namespace Zayra.Api.Controllers.Ess;

public sealed record EssLaunchInput(string? ReturnUrl);

/// <summary>
/// "My learning" (LRN-04/05/06/08): the caller's OWN assignments, content, assessments, transcript and certificates.
/// There is no employee id in any route — the employee is the one linked to the signed-in user (CallerEmployeeResolver,
/// the same lookup ESS uses), so another person's enrollment, attempt or certificate is simply "not found" (LRN-12).
/// Closed unless the tenant has the learning opt-in flag on (/api/ess/learning in OptInFeatures).
/// </summary>
[ApiController]
[Authorize]
[LearningErrors]
[Route("api/ess/learning")]
public sealed class EssLearningController(ZayraDbContext db, LearningProgressService progress, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    [HasPermission("ess.read")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var (tenantId, employeeId) = await SelfAsync(ct);
        return Ok(await progress.MyLearningAsync(tenantId, employeeId, ct));
    }

    [HttpGet("enrollments/{id:guid}")]
    [HasPermission("ess.read")]
    public async Task<IActionResult> Enrollment(Guid id, CancellationToken ct)
    {
        var (tenantId, employeeId) = await SelfAsync(ct);
        return Ok(await progress.EnrollmentDetailAsync(tenantId, employeeId, id, ct));
    }

    /// <summary>POST …/launch — opens the content and records launch evidence (cmi5: a session and a one-time fetch URL).</summary>
    [HttpPost("enrollments/{id:guid}/launch")]
    [HasPermission("ess.write")]
    public async Task<IActionResult> Launch(Guid id, [FromBody] EssLaunchInput? input, CancellationToken ct)
    {
        var (tenantId, employeeId) = await SelfAsync(ct);
        return Ok(await progress.LaunchAsync(Actor(tenantId, employeeId), employeeId, id, $"{Request.Scheme}://{Request.Host}",
            SafeReturnUrl(input?.ReturnUrl), ct));
    }

    /// <summary>POST …/acknowledge — only for an acknowledgement-rule course that was opened first (409 otherwise).</summary>
    [HttpPost("enrollments/{id:guid}/acknowledge")]
    [HasPermission("ess.write")]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
    {
        var (tenantId, employeeId) = await SelfAsync(ct);
        return Ok(await progress.AcknowledgeAsync(Actor(tenantId, employeeId), employeeId, id, ct));
    }

    /// <summary>POST …/attempts — start an assessment attempt, or resume the open one. Questions come without answers.</summary>
    [HttpPost("enrollments/{id:guid}/attempts")]
    [HasPermission("ess.write")]
    public async Task<IActionResult> StartAttempt(Guid id, CancellationToken ct)
    {
        var (tenantId, employeeId) = await SelfAsync(ct);
        return Ok(await progress.StartAttemptAsync(Actor(tenantId, employeeId), employeeId, id, ct));
    }

    /// <summary>POST /api/ess/learning/attempts/{id}/submit — scored on the server; a repeat returns the stored result.</summary>
    [HttpPost("attempts/{id:guid}/submit")]
    [HasPermission("ess.write")]
    public async Task<IActionResult> Submit(Guid id, [FromBody] SubmitAttemptInput input, CancellationToken ct)
    {
        var (tenantId, employeeId) = await SelfAsync(ct);
        return Ok(await progress.SubmitAttemptAsync(Actor(tenantId, employeeId), employeeId, id, input, ct));
    }

    [HttpGet("credentials/{id:guid}")]
    [HasPermission("ess.read")]
    public async Task<IActionResult> Credential(Guid id, CancellationToken ct)
    {
        var (tenantId, employeeId) = await SelfAsync(ct);
        return Ok(await progress.MyCredentialAsync(tenantId, employeeId, id, ct));
    }

    private async Task<(Guid TenantId, int EmployeeId)> SelfAsync(CancellationToken ct)
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly")
            throw new LearningException(403, "access_mode", "This access mode cannot use self-service.");
        if (this.GetTenantId() is not Guid tenantId) throw new LearningException(401, "unauthenticated", "Sign in again.");
        if (await CallerEmployeeResolver.ResolveAsync(db, User, tenantId, ct) is not int employeeId)
            throw new LearningException(409, "no_employee_record", EssLinkGuidance.En);
        return (tenantId, employeeId);
    }

    private LearningActor Actor(Guid tenantId, int employeeId) =>
        new(tenantId, this.GetUserId(), HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(),
            new DataScope { Level = DataScopeLevel.Own, CallerEmployeeId = employeeId, AllowedEmployeeIds = [employeeId] }, employeeId);

    /// <summary>
    /// The cmi5 returnURL must be an http(s) URL on one of this app's own origins, so a launch can never carry a script
    /// URL or an open redirect to the unit. Anything else is dropped (the unit then simply shows no return link).
    /// </summary>
    internal string? SafeReturnUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2000 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        var origin = uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        var allowed = (configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"])
            .Concat((configuration["CORS_EXTRA_ORIGINS"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(o => o.TrimEnd('/'));
        return allowed.Contains(origin, StringComparer.OrdinalIgnoreCase) ? uri.AbsoluteUri : null;
    }
}
