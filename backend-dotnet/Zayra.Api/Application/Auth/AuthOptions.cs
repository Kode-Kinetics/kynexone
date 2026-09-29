namespace Zayra.Api.Application.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "Zayra.Api";

    // Separate audiences enforce that a tenant token is cryptographically rejected on
    // platform routes even if its claims were somehow tampered with.
    // TenantAudience  — issued to regular user logins and impersonation sessions.
    // PlatformAudience — issued only by /api/platform/auth/login and /mfa/challenge/verify.
    // The PlatformAdmin authorization policy requires BOTH is_platform_admin:"true" AND
    // aud = PlatformAudience, so a tenant token (wrong aud) is rejected even if it
    // somehow carried the is_platform_admin claim.
    public string TenantAudience   { get; set; } = "kynexone-tenant";
    public string PlatformAudience { get; set; } = "kynexone-platform";

    public string SigningKey { get; set; } = "CHANGE_ME_TO_A_64_CHARACTER_PRODUCTION_SECRET_KEY";
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 14;
}
