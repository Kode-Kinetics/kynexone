using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/enterprise-identity")]
public class EnterpriseIdentityController : ControllerBase
{
    private readonly IEnterpriseIdentityService _identity;

    public EnterpriseIdentityController(IEnterpriseIdentityService identity)
    {
        _identity = identity;
    }

    [HttpGet("settings")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<EnterpriseIdentitySettingsDto>> GetSettings(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var gate = RequireGroupEntityScope();
        if (gate is not null) return gate;
        return Ok(await _identity.GetSettingsAsync(tenantId.Value, ct));
    }

    [HttpPut("settings")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<EnterpriseIdentitySettingsDto>> UpdateSettings(UpdateEnterpriseIdentitySettingsRequest request, CancellationToken ct)
    {
        try
        {
            var tenantId = this.GetTenantId();
            if (tenantId is null) return Unauthorized();
            var gate = RequireGroupEntityScope();
            if (gate is not null) return gate;
            return Ok(await _identity.UpdateSettingsAsync(tenantId.Value, request, GetContext(), ct));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("scim/token")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<RotateScimTokenResponse>> RotateScimToken(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var gate = RequireGroupEntityScope();
        if (gate is not null) return gate;
        return Ok(await _identity.RotateScimTokenAsync(tenantId.Value, GetContext(), ct));
    }

    [HttpGet("validate")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<EnterpriseIdentityValidationResult>> Validate(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var gate = RequireGroupEntityScope();
        if (gate is not null) return gate;
        return Ok(await _identity.ValidateSettingsAsync(tenantId.Value, ct));
    }

    [HttpGet("saml/{tenantSlug}/metadata")]
    [AllowAnonymous]
    public async Task<IActionResult> SamlMetadata(string tenantSlug, CancellationToken ct)
    {
        var metadata = await _identity.GetSamlMetadataAsync(tenantSlug, BaseUrl(), ct);
        if (metadata is null) return NotFound();
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <EntityDescriptor entityID="{System.Security.SecurityElement.Escape(metadata.EntityId)}" xmlns="urn:oasis:names:tc:SAML:2.0:metadata">
              <SPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                <AssertionConsumerService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="{System.Security.SecurityElement.Escape(metadata.AssertionConsumerServiceUrl)}" index="0" isDefault="true" />
                <SingleLogoutService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect" Location="{System.Security.SecurityElement.Escape(metadata.SingleLogoutServiceUrl)}" />
              </SPSSODescriptor>
            </EntityDescriptor>
            """;
        return Content(xml, "application/samlmetadata+xml", Encoding.UTF8);
    }

    [HttpGet("oidc/{tenantSlug}/.well-known/openid-configuration")]
    [AllowAnonymous]
    public async Task<ActionResult<OidcTenantMetadataDto>> OidcMetadata(string tenantSlug, CancellationToken ct)
    {
        var metadata = await _identity.GetOidcMetadataAsync(tenantSlug, BaseUrl(), ct);
        return metadata is null ? NotFound() : Ok(metadata);
    }

    [HttpGet("saml/{tenantSlug}/acs")]
    [AllowAnonymous]
    public IActionResult SamlAcsBoundary() => StatusCode(StatusCodes.Status501NotImplemented, new { message = "SAML assertion consumption is not enabled in this build. Configure an IdP only after a federation adapter is installed." });

    [HttpGet("oidc/{tenantSlug}/authorize")]
    [AllowAnonymous]
    public IActionResult OidcAuthorizeBoundary() => StatusCode(StatusCodes.Status501NotImplemented, new { message = "OIDC federation is not enabled in this build. This endpoint is metadata-only." });

    [HttpGet("scim/v2/Users")]
    [AllowAnonymous]
    public async Task<ActionResult<ScimListResponse>> ListScimUsers([FromQuery] int startIndex = 1, [FromQuery] int count = 100, CancellationToken ct = default)
    {
        var tenantId = await AuthenticateScimAsync(ct);
        if (tenantId is null) return Unauthorized();
        return Ok(await _identity.ListScimUsersAsync(tenantId.Value, startIndex, count, ct));
    }

    [HttpPost("scim/v2/Users")]
    [AllowAnonymous]
    public async Task<ActionResult<ScimUserResource>> CreateScimUser(ScimUserUpsertRequest request, CancellationToken ct)
    {
        try
        {
            var tenantId = await AuthenticateScimAsync(ct);
            if (tenantId is null) return Unauthorized();
            var user = await _identity.UpsertScimUserAsync(tenantId.Value, request, GetContext() with { TenantId = tenantId }, ct);
            return Created($"/api/enterprise-identity/scim/v2/Users/{user.Id}", user);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
    }

    [HttpPatch("scim/v2/Users/{userId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<ScimUserResource>> PatchScimUser(Guid userId, ScimPatchRequest request, CancellationToken ct)
    {
        try
        {
            var tenantId = await AuthenticateScimAsync(ct);
            if (tenantId is null) return Unauthorized();
            var user = await _identity.PatchScimUserAsync(tenantId.Value, userId, request, GetContext() with { TenantId = tenantId }, ct);
            return user is null ? NotFound() : Ok(user);
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
    }

    [HttpDelete("scim/v2/Users/{userId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> DeleteScimUser(Guid userId, CancellationToken ct)
    {
        try
        {
            var tenantId = await AuthenticateScimAsync(ct);
            if (tenantId is null) return Unauthorized();
            return await _identity.DeactivateScimUserAsync(tenantId.Value, userId, GetContext() with { TenantId = tenantId }, ct) ? NoContent() : NotFound();
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(new { message = ex.Message }); }
    }

    private async Task<Guid?> AuthenticateScimAsync(CancellationToken ct)
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return await _identity.ValidateScimTokenAsync(header[prefix.Length..].Trim(), ct);
    }

    private string BaseUrl() => $"{Request.Scheme}://{Request.Host}";
    private RequestContext GetContext() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());
    private ActionResult? RequireGroupEntityScope() => this.GetEntityScope().IsGroupLevel ? null : Forbid();
}
