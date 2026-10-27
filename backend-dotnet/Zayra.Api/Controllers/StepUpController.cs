using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Infrastructure.Http;

namespace Zayra.Api.Controllers;

public sealed record StepUpRequest(string? Purpose, string? Password, string? MfaCode);

/// <summary>
/// POST /api/auth/step-up — the signed-in person re-enters their password (and authenticator code when enrolled) and gets a
/// short-lived token for one purpose (C-2). Wrong attempts use the same counter and lockout as sign-in.
/// </summary>
[ApiController]
[Authorize]
[NoStore]
[Route("api/auth/step-up")]
public sealed class StepUpController(IStepUpService stepUp) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Verify(StepUpRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId))
            return Unauthorized();
        if (!StepUpPurposes.IsKnown(request.Purpose))
            return EssProblem.Create(HttpContext, 400, EssProblemCodes.ValidationFailed);
        var result = await stepUp.VerifyAsync(userId, request.Purpose!, new StepUpCredential(request.Password, request.MfaCode), ct);
        if (result.Ok) return Ok(new { stepUpToken = result.Token, expiresAtUtc = result.ExpiresAtUtc });
        return EssProblem.Create(HttpContext, 401, result.Error ?? EssProblemCodes.InvalidCredentials);
    }
}
