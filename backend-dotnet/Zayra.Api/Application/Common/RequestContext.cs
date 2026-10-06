namespace Zayra.Api.Application.Auth;

public record RequestContext(
    string? IpAddress,
    string? UserAgent,
    Guid? UserId = null,
    Guid? TenantId = null,
    IReadOnlyCollection<string>? Roles = null,
    IReadOnlyCollection<string>? Permissions = null)
{
    /// <summary>The known-device cookie presented with a sign-in, validated against the account by AuthService.</summary>
    public string? KnownDeviceToken { get; init; }

    /// <summary>Set by an endpoint that already validated the caller's known-device cookie for the
    /// principal being authenticated (platform MFA step): an active failed-password lockout is bypassed.</summary>
    public bool KnownDeviceVerified { get; init; }
}
