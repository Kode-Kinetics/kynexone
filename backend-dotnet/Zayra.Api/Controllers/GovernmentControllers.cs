using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// GET /api/government/capabilities: per body, whether this tenant can exchange data with it right now (disabled, file only,
/// simulated or live) and why. Any signed-in tenant user may read it; it carries no customer data. The frontend feature-detects
/// on it and hides every government control on 404 or <c>disabled</c>, so a frontend deployed ahead of the API stays honest.
/// </summary>
[ApiController]
[Authorize]
[NoStore]
[Route("api/government/capabilities")]
public sealed class GovernmentCapabilitiesController(GovernmentCapabilitiesService capabilities) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GovernmentCapabilityDto>>> Get(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)) return Unauthorized();
        return Ok(await capabilities.GetAsync(tenantId, ct));
    }
}

/// <summary>
/// PUT body for a government or bank credential. The secret travels in this request only and is never returned; the record's
/// <see cref="ToString"/> never prints it (a logged action argument cannot leak it).
/// </summary>
public sealed record GovernmentCredentialSaveRequest(string? Environment, string? Kind, string? Secret, Guid? CompanyId, DateTime? ExpiresAtUtc,
    string? ClientIdentifier = null, string? BaseUrlHost = null, string? AuthorizationHost = null)
{
    public override string ToString() =>
        $"GovernmentCredentialSaveRequest {{ Environment = {Environment}, Kind = {Kind}, CompanyId = {CompanyId}, Secret = (withheld) }}";
}

/// <summary>
/// PUT / GET /api/government/credentials/{body} and POST .../{body}/revoke: a customer's credential for a government or bank body. Metadata only
/// in every response (body, environment, kind, fingerprint, version, expiry, last used, rotation): never the secret.
/// <list type="bullet">
/// <item>Permission <see cref="Permission"/>, which no built-in role holds (not even Admin by default).</item>
/// <item>Save, rotate and revoke need a fresh "confirm it's you" step-up (<see cref="StepUpService"/>, header <c>X-Step-Up</c>);
/// without it the answer is 403 <c>step_up_required</c> and nothing changes.</item>
/// <item>The store fails closed: with no field-encryption KEK on this server every call answers 503
/// <c>government_credentials_unavailable</c> and nothing is stored.</item>
/// </list>
/// </summary>
[ApiController]
[Authorize]
[NoStore]
[Route("api/government/credentials")]
public sealed class GovernmentCredentialsController(GovernmentCredentialStore store, StepUpService? stepUp = null,
    Microsoft.Extensions.Caching.Memory.IMemoryCache? usedProofs = null) : ControllerBase
{
    public const string Permission = "gov.credentials.manage";
    public const string StepUpReusedCode = "step_up_already_used";

    /// <summary>Fallback when no cache is registered, so a proof is still single-use within this process.</summary>
    private static readonly Microsoft.Extensions.Caching.Memory.MemoryCache FallbackProofs = new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

    [HttpGet("{body}")]
    [HasPermission(Permission)]
    public async Task<IActionResult> Get(string body, [FromQuery] string? environment, [FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (Scope() is not { } scope) return Unauthorized();
        if (Invalid(body, environment ?? GovernmentEnvironments.Sandbox) is { } bad) return bad;
        return await Guarded(async () =>
        {
            var meta = await store.GetMetadataAsync(scope.TenantId, companyId, body, environment ?? GovernmentEnvironments.Sandbox, ct);
            return meta is null ? NotFound(new { code = "government_credential_not_found" }) : (IActionResult)Ok(meta);
        });
    }

    [HttpPut("{body}")]
    [HasPermission(Permission)]
    public async Task<IActionResult> Put(string body, [FromBody] GovernmentCredentialSaveRequest request, CancellationToken ct)
    {
        if (Scope() is not { } scope) return Unauthorized();
        var environment = request.Environment ?? GovernmentEnvironments.Sandbox;
        if (Invalid(body, environment) is { } bad) return bad;
        if (!GovernmentCredentialKinds.IsKnown(request.Kind))
            return BadRequest(new { code = "government_credential_invalid", field = "kind", allowed = GovernmentCredentialKinds.All });
        if (string.IsNullOrWhiteSpace(request.Secret) || request.Secret.Length > GovernmentCredentialStore.MaxSecretLength)
            return BadRequest(new { code = "government_credential_invalid", field = "secret" });
        if (request.ExpiresAtUtc is { } expiry && expiry <= DateTime.UtcNow)
            return BadRequest(new { code = "government_credential_invalid", field = "expiresAtUtc" });
        if (await StepUpRefusalAsync(scope, ct) is { } refusal) return refusal;
        return await Guarded(async () => Ok(await store.SaveAsync(scope.TenantId, request.CompanyId, body, environment, request.Kind!,
            request.Secret!, request.ExpiresAtUtc, Context(scope), ct,
            new GovernmentCredentialBinding(request.ClientIdentifier, request.BaseUrlHost, request.AuthorizationHost))));
    }

    /// <summary>Revokes the credential: the secret is wiped, the record stays (marked revoked, with who and when) and never opens
    /// again; saving a new one is a new version. Same permission, step-up and company scope as saving.</summary>
    [HttpPost("{body}/revoke")]
    [HasPermission(Permission)]
    public async Task<IActionResult> Revoke(string body, [FromQuery] string? environment, [FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (Scope() is not { } scope) return Unauthorized();
        var env = environment ?? GovernmentEnvironments.Sandbox;
        if (Invalid(body, env) is { } bad) return bad;
        if (await StepUpRefusalAsync(scope, ct) is { } refusal) return refusal;
        return await Guarded(async () => await store.RevokeAsync(scope.TenantId, companyId, body, env, Context(scope), ct) is { } revoked
            ? Ok(revoked)
            : (IActionResult)NotFound(new { code = "government_credential_not_found" }));
    }

    private sealed record CallerScope(Guid TenantId, Guid UserId);

    private CallerScope? Scope() =>
        Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenant)
        && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var user)
            ? new CallerScope(tenant, user)
            : null;

    private RequestContext Context(CallerScope scope) =>
        new(HttpContext?.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), scope.UserId, scope.TenantId);

    private IActionResult? Invalid(string body, string environment)
    {
        if (!GovernmentBodies.IsKnown(body)) return NotFound(new { code = "government_body_unknown" });
        if (!GovernmentEnvironments.IsKnown(environment))
            return BadRequest(new { code = "government_credential_invalid", field = "environment", allowed = GovernmentEnvironments.All });
        return null;
    }

    /// <summary>
    /// Fails closed: no step-up service, or no valid X-Step-Up token for this user in this tenant, is a refusal. A proof opens ONE
    /// credential change: it is spent here (keyed by its hash, for its lifetime), so a token minted for another action, or replayed,
    /// cannot open a second save, rotation or removal.
    /// </summary>
    private async Task<IActionResult?> StepUpRefusalAsync(CallerScope scope, CancellationToken ct)
    {
        var proof = Request.Headers[StepUpService.HeaderName].ToString();
        if (stepUp is not null && await stepUp.IsConfirmedAsync(scope.UserId, scope.TenantId, proof, ct))
        {
            var spent = usedProofs ?? FallbackProofs;
            var key = "gov-cred-step-up:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{scope.TenantId:N}|{scope.UserId:N}|{proof}")));
            lock (spent)
            {
                if (!spent.TryGetValue(key, out _))
                {
                    spent.Set(key, true, StepUpService.Lifetime + TimeSpan.FromMinutes(1));
                    return null;
                }
            }
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = StepUpService.RequiredCode,
                reason = StepUpReusedCode,
                error = StepUpService.RequiredCode,
                message = "Confirm it's you again: each credential change needs its own confirmation.",
                messageAr = "أكّد هويتك مرة أخرى: يحتاج كل تغيير في بيانات الاعتماد إلى تأكيد خاص به.",
            });
        }
        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = StepUpService.RequiredCode,
            error = StepUpService.RequiredCode,
            message = "Confirm it's you to continue: enter your password, and your two-step code if it is on.",
            messageAr = "أكّد هويتك للمتابعة: أدخل كلمة المرور، ورمز التحقق بخطوتين إن كان مفعّلًا.",
        });
    }

    private async Task<IActionResult> Guarded(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (GovernmentCredentialRefusedException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message, messageAr = ex.MessageAr });
        }
        catch (ArgumentException ex) when (ex.ParamName is "baseUrlHost" or "tokenUrlHost" or "binding")
        {
            return BadRequest(new { code = GovernmentCredentialStore.InvalidCode, field = ex.ParamName });
        }
        catch (FieldEncryptionUnavailableException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.SafeMessage, messageAr = ArabicSafeMessage(ex) });
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("company_scope_required", StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                code = "company_scope_required",
                message = "Choose the company this credential belongs to.",
                messageAr = "اختر الشركة التي تخص بيانات الاعتماد هذه.",
            });
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "company_scope_denied",
                message = "You can manage credentials only for the companies you have access to.",
                messageAr = "يمكنك إدارة بيانات الاعتماد للشركات التي لديك صلاحية الوصول إليها فقط.",
            });
        }
    }

    private static string ArabicSafeMessage(FieldEncryptionUnavailableException ex) => ex is GovernmentCredentialsUnavailableException
        ? "لا يمكن حفظ بيانات اعتماد الخدمات الحكومية والمصرفية أو استخدامها على هذا الخادم بعد، لأن مفاتيح التشفير غير مهيأة. لم يُحفظ شيء. تواصل مع مشغّل المنصة."
        : "لا يمكن حفظ الحقول المحمية الآن لأن مفاتيح التشفير غير متاحة مؤقتًا. لم يُفقد شيء؛ حاول مرة أخرى لاحقًا.";
}
