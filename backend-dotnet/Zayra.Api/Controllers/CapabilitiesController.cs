using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Controllers;

/// <summary>
/// What this build can actually do, feature by feature. Screens read it to hide a form whose save the server refuses
/// (answers 501), and to say so in a short note, instead of offering a form that can never save.
/// <para>The map is one static table: the day a feature is built, its entry flips here and every screen that asked
/// follows, with no client release. Entries are build facts, not permissions: who may use a feature is still decided
/// by the endpoint that serves it.</para>
/// </summary>
[ApiController]
[Route("api/capabilities")]
[Authorize]
public class CapabilitiesController : ControllerBase
{
    /// <summary>Feature key to whether this build supports it. Keys are the contract with the frontend (api/capabilities.ts).</summary>
    public static readonly IReadOnlyDictionary<string, bool> Features = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        // Stored and never consulted by any approval path, so every write answers 501 (AccessController).
        ["approvalDelegations.write"] = false,
        ["approvalAuthorities.write"] = false,
        // Federation adapters are not installed in this build (EnterpriseIdentityController).
        ["samlFederation"] = false,
        ["oidcFederation"] = false,
    };

    /// <summary>The permission that makes a flag relevant. A signed-in person without it is told nothing about that feature: the
    /// flags describe which security features this build lacks, which only the people who administer them need to know.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> RelevantTo = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["approvalDelegations.write"] = ["security.manage", "approvals.manage"],
        ["approvalAuthorities.write"] = ["security.manage", "approvals.manage"],
        ["samlFederation"] = ["security.manage"],
        ["oidcFederation"] = ["security.manage"],
    };

    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        features = Features.Where(f => RelevantTo.TryGetValue(f.Key, out var permissions) && permissions.Any(p => User.HasPermission(p)))
            .ToDictionary(f => f.Key, f => f.Value),
    });
}
