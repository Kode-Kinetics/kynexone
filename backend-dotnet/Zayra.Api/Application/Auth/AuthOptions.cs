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

public class SeedAdminOptions
{
    public string TenantName { get; set; } = "Zayra HQ";
    public string TenantSlug { get; set; } = "zayra";
    public string Email { get; set; } = "admin@zayra.local";
    public string FullName { get; set; } = "Zayra Admin";
    // No hard-coded default: the bootstrap admin password MUST be supplied via config/env
    // (SeedAdmin:Password / env SeedAdmin__Password). Program.cs fails fast in Production/dedicated
    // deployments when it is missing or weak, and substitutes a dev default in local development.
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// When true, seeds sample/demo business data (company, branches, departments,
    /// grades, sample policies and approval workflows). Defaults to FALSE so production
    /// tenants start clean — the admin configures their own organisation via Setup.
    /// </summary>
    public bool SeedDemoData { get; set; } = false;
}
