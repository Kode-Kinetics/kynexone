using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Policy;

namespace Zayra.Api.Controllers.Admin;

public sealed record PolicySettingDto(string Key, string Type, string Category, string Description,
    IReadOnlyList<string> AllowedValues, string DefaultValue, string Value, bool IsDefault);

public sealed record PolicySettingUpdateRequest(string? Value);

/// <summary>Tenant-wide, client-configurable business policies. Defaults are strict; every change is audited.</summary>
[Authorize]
[ApiController]
public class PolicySettingsController : ControllerBase
{
    private readonly IPolicySettingsAdmin _policies;
    public PolicySettingsController(IPolicySettingsAdmin policies) => _policies = policies;

    private Guid GetTenantId() => Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;

    private static PolicySettingDto ToDto(PolicySettingView v) => new(
        v.Definition.Key, v.Definition.Type.ToString().ToLowerInvariant(), v.Definition.Category, v.Definition.Description,
        v.Definition.AllowedValues, v.Definition.Default, v.Value, v.IsDefault);

    [HttpGet("api/policy-settings")]
    [HasPermission("security.manage")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid == Guid.Empty) return Forbid();
        var items = await _policies.ListAsync(tid, ct);
        return Ok(items.Select(ToDto).ToList());
    }

    [HttpPut("api/policy-settings/{key}")]
    [HasPermission("security.manage")]
    public async Task<IActionResult> Update(string key, [FromBody] PolicySettingUpdateRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        if (tid == Guid.Empty) return Forbid();
        Guid? uid = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value, out var id) ? id : null;
        var actor = new PolicyActor(uid, User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst("name")?.Value ?? string.Empty,
            HttpContext?.Connection.RemoteIpAddress?.ToString() ?? string.Empty);

        var result = await _policies.SetAsync(tid, key, req?.Value, actor, ct);
        return result.Status switch
        {
            PolicySetStatus.UnknownKey => NotFound(new { code = "policy_key_unknown", message = result.Error }),
            PolicySetStatus.Invalid => BadRequest(new { code = "policy_value_invalid", message = result.Error }),
            _ => Ok(ToDto(result.Setting!)),
        };
    }
}
