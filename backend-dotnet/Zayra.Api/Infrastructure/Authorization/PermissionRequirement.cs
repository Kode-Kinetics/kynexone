using Microsoft.AspNetCore.Authorization;

namespace Zayra.Api.Infrastructure.Authorization;

/// <summary>
/// Authorization requirement carrying the ANY-of permission keys a request must satisfy.
/// Handled by <see cref="PermissionAuthorizationHandler"/>.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(IEnumerable<string> anyOf)
    {
        Any = (anyOf ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToArray();
    }

    /// <summary>Caller must hold AT LEAST ONE of these permission keys.</summary>
    public IReadOnlyCollection<string> Any { get; }
}
