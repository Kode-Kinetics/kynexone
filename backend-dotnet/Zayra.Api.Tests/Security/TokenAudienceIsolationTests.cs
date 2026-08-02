using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Http;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Proves the REAL audience/route boundary the pipeline actually enforces (rewritten during the
/// tenant-isolation hardening pod).
///
/// The old <c>PlatformToken_IsRejectedByTenantAudienceValidator</c> test asserted a single-audience
/// validator that DOES NOT EXIST: Program.cs configures <c>ValidAudiences = { TenantAudience,
/// PlatformAudience }</c>, so the JWT middleware accepts a platform token's signature on EVERY route.
/// Containment is delivered by TWO real mechanisms, both asserted here:
///   1. The PlatformAdmin authorization policy (RequireClaim is_platform_admin + aud=platform) —
///      keeps a tenant/forged token OFF platform routes.
///   2. AudienceRouteGuard (per-route audience segregation) — keeps a platform token OFF tenant
///      /api/* routes, the only enforcement on that vector because #1a still (by design) grants
///      platform tokens the cross-tenant data-layer bypass.
/// </summary>
public class TokenAudienceIsolationTests
{
    private const string SigningKey       = "TEST_AUDIENCE_ISOLATION_SIGNING_KEY_MUST_BE_64_CHARS__PADDED00";
    private const string Issuer           = "Zayra.Tests";
    private const string TenantAudience   = "kynexone-tenant";
    private const string PlatformAudience = "kynexone-platform";

    // ── Token factory helpers ─────────────────────────────────────────────────

    private static string BuildToken(string audience, IEnumerable<Claim> claims, int expiryHours = 1)
    {
        var key         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer:             Issuer,
            audience:           audience,
            claims:             claims,
            expires:            DateTime.UtcNow.AddHours(expiryHours),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Validates against the REAL Program.cs config: BOTH audiences are accepted.</summary>
    private static ClaimsPrincipal? ParseTokenAgainstRealPipeline(string tokenString)
    {
        var handler = new JwtSecurityTokenHandler();
        var key     = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        try
        {
            return handler.ValidateToken(tokenString, new TokenValidationParameters
            {
                ValidateIssuer           = true,
                ValidateAudience         = true,
                ValidateLifetime         = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer              = Issuer,
                // The actual pipeline (Program.cs:223) accepts BOTH audiences.
                ValidAudiences           = new[] { TenantAudience, PlatformAudience },
                IssuerSigningKey         = key,
                ClockSkew                = TimeSpan.Zero
            }, out _);
        }
        catch (SecurityTokenException)
        {
            return null;
        }
    }

    /// <summary>Mirrors the RequireClaim calls of the PlatformAdmin policy in Program.cs.</summary>
    private static bool SatisfiesPlatformAdminPolicy(ClaimsPrincipal principal) =>
        principal.HasClaim("is_platform_admin", "true") &&
        principal.HasClaim("aud", PlatformAudience);

    private static ClaimsPrincipal AuthenticatedPrincipal(string audience, params Claim[] claims)
    {
        var identity = new ClaimsIdentity(
            claims.Append(new Claim("aud", audience)),
            authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    // ── PlatformAdmin policy (real gate #1): platform routes ──────────────────

    [Fact]
    public void PlatformToken_WithCorrectAudienceAndClaims_PassesPlatformAdminPolicy()
    {
        var tokenString = BuildToken(PlatformAudience, new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "platform-admin"),
            new Claim("is_platform_admin", "true"),
            new Claim("platform_role", "Owner"),
        });

        var principal = ParseTokenAgainstRealPipeline(tokenString);

        principal.Should().NotBeNull("platform token must validate with the shared signing key");
        SatisfiesPlatformAdminPolicy(principal!).Should().BeTrue(
            "platform token carries both is_platform_admin and the correct aud claim");
    }

    [Fact]
    public void TenantToken_IsRejectedByPlatformAdminPolicy_DueToWrongAudience()
    {
        var tokenString = BuildToken(TenantAudience, new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "user-guid"),
            new Claim("tenant_id", Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "HR Manager"),
        });

        var principal = ParseTokenAgainstRealPipeline(tokenString);

        principal.Should().NotBeNull("JWT signature is valid so validation should succeed");
        SatisfiesPlatformAdminPolicy(principal!).Should().BeFalse(
            "tenant token lacks both is_platform_admin and the platform audience claim");
    }

    [Fact]
    public void TenantToken_CarryingForgedPlatformClaim_IsStillRejectedByAudienceCheck()
    {
        var tokenString = BuildToken(TenantAudience, new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "attacker"),
            new Claim("tenant_id", Guid.NewGuid().ToString()),
            new Claim("is_platform_admin", "true"),  // injected claim — wrong audience token
        });

        var principal = ParseTokenAgainstRealPipeline(tokenString);

        principal.Should().NotBeNull("signature is still valid — we're proving the claim-based gate");
        principal!.HasClaim("is_platform_admin", "true").Should().BeTrue(
            "claim is present — this is what the OLD single-gate check would have passed");
        SatisfiesPlatformAdminPolicy(principal).Should().BeFalse(
            "PlatformAdmin policy now requires aud=kynexone-platform — tenant aud fails the second gate");
    }

    // ── Kills the old fiction: platform token DOES validate on the real pipeline ──

    [Fact]
    public void PlatformToken_ValidatesAgainstRealPipeline_BecauseBothAudiencesAreAccepted()
    {
        // The removed test asserted a platform token is rejected by a "tenant-only" validator.
        // The real pipeline has no such validator — it accepts both audiences. This documents that
        // truth, which is precisely WHY AudienceRouteGuard (below) is the load-bearing gate on the
        // platform-token-on-tenant-route vector.
        var tokenString = BuildToken(PlatformAudience, new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "platform-admin"),
            new Claim("is_platform_admin", "true"),
        });

        var principal = ParseTokenAgainstRealPipeline(tokenString);

        principal.Should().NotBeNull(
            "the real JWT middleware accepts BOTH audiences, so a platform token's signature validates "
            + "on tenant routes too — route-level enforcement (AudienceRouteGuard) is what contains it");
    }

    // ── AudienceRouteGuard (real gate #2): tenant routes — the closed hole (#1b) ──

    [Fact]
    public void Guard_PlatformToken_OnTenantApiRoute_IsBlocked()
    {
        var user = AuthenticatedPrincipal(PlatformAudience,
            new Claim("is_platform_admin", "true"));

        AudienceRouteGuard.IsPlatformTokenOnTenantRoute("/api/employees", user, PlatformAudience)
            .Should().BeTrue("a platform-audience token must never be honoured on a tenant /api/* route");
    }

    [Fact]
    public void Guard_PlatformToken_OnPlatformRoute_IsAllowed()
    {
        var user = AuthenticatedPrincipal(PlatformAudience,
            new Claim("is_platform_admin", "true"));

        AudienceRouteGuard.IsPlatformTokenOnTenantRoute("/api/platform/tenants", user, PlatformAudience)
            .Should().BeFalse("platform-admin cross-tenant endpoints live under the allowlisted /api/platform prefix");
    }

    [Fact]
    public void Guard_TenantToken_OnTenantApiRoute_IsAllowed()
    {
        var user = AuthenticatedPrincipal(TenantAudience,
            new Claim("tenant_id", Guid.NewGuid().ToString()));

        AudienceRouteGuard.IsPlatformTokenOnTenantRoute("/api/employees", user, PlatformAudience)
            .Should().BeFalse("a normal tenant token is exactly what tenant /api/* routes expect");
    }

    [Fact]
    public void Guard_ImpersonationToken_TenantAudience_OnTenantApiRoute_IsAllowed()
    {
        // Impersonation / break-glass mint TENANT-audience tokens carrying tenant_id — cross-tenant
        // platform work happens through these, so the guard must never match them.
        var user = AuthenticatedPrincipal(TenantAudience,
            new Claim("tenant_id", Guid.NewGuid().ToString()),
            new Claim("is_platform_admin", "true")); // impersonator provenance, still tenant aud

        AudienceRouteGuard.IsPlatformTokenOnTenantRoute("/api/payroll", user, PlatformAudience)
            .Should().BeFalse("impersonation uses a tenant-audience token, the intended cross-tenant path");
    }

    [Fact]
    public void Guard_Unauthenticated_OnAnonymousRoute_IsNotMatched()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); // not authenticated

        AudienceRouteGuard.IsPlatformTokenOnTenantRoute("/api/auth/login", anonymous, PlatformAudience)
            .Should().BeFalse("anonymous requests carry no platform audience and must pass through");
    }

    [Fact]
    public void Guard_IsCaseInsensitive_AndSegmentAware()
    {
        var user = AuthenticatedPrincipal(PlatformAudience,
            new Claim("is_platform_admin", "true"));

        // Case-insensitive: /API/... still caught.
        AudienceRouteGuard.IsPlatformTokenOnTenantRoute("/API/employees", user, PlatformAudience)
            .Should().BeTrue("routing is case-insensitive; a cased path must not slip the guard");

        // Segment-aware: /api/platformx is NOT the /api/platform prefix, so it is a tenant route.
        AudienceRouteGuard.IsPlatformTokenOnTenantRoute("/api/platformx/data", user, PlatformAudience)
            .Should().BeTrue("/api/platformx is a tenant route, not the allowlisted /api/platform segment");
    }

    // ── Middleware body: proves wiring (short-circuit + 403 shape), not just the predicate ──

    [Fact]
    public async Task Guard_Middleware_PlatformTokenOnTenantRoute_Writes403_AndShortCircuits()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/employees";
        context.Response.Body = new MemoryStream();
        context.User = AuthenticatedPrincipal(PlatformAudience, new Claim("is_platform_admin", "true"));

        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        await AudienceRouteGuard.InvokeAsync(context, next, PlatformAudience);

        nextCalled.Should().BeFalse("the middleware must short-circuit and never reach the endpoint");
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain(AudienceRouteGuard.ViolationCode,
            "the 403 body must carry the platform_token_on_tenant_route code");
    }

    [Fact]
    public async Task Guard_Middleware_PlatformTokenOnPlatformRoute_CallsNext()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/platform/tenants";
        context.User = AuthenticatedPrincipal(PlatformAudience, new Claim("is_platform_admin", "true"));

        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        await AudienceRouteGuard.InvokeAsync(context, next, PlatformAudience);

        nextCalled.Should().BeTrue("platform routes are allowlisted; the request must proceed");
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK, "no error status was set");
    }

    // ── Structural: JwtOptions defaults are well-formed ───────────────────────

    [Fact]
    public void JwtOptions_DefaultAudienceValues_AreDistinctAndNonEmpty()
    {
        var opts = new JwtOptions();

        opts.TenantAudience.Should().NotBeNullOrWhiteSpace();
        opts.PlatformAudience.Should().NotBeNullOrWhiteSpace();
        opts.TenantAudience.Should().NotBe(opts.PlatformAudience,
            "each token type must have a distinct audience so one cannot be used in place of the other");
    }
}
