using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// Shared helpers for all platform controller tests.
/// Uses InMemory EF so no MySQL/Docker is needed.
/// </summary>
public abstract class PlatformTestBase
{
    // Credentials matching the .env file
    protected const string AdminEmail    = "admin@platform.local";
    protected const string AdminPassword = "YourPassword123!";

    protected const string JwtSigningKey       = "TEST_PLATFORM_SIGNING_KEY_MUST_BE_64_CHARS_FOR_TESTS___PADDED";
    protected const string JwtIssuer           = "Zayra.Tests";
    protected const string JwtTenantAudience   = "kynexone-tenant-test";
    protected const string JwtPlatformAudience = "kynexone-platform-test";

    // ── DB ─────────────────────────────────────────────────────────────────────

    protected static ZayraDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(opts);
    }

    // ── JWT ────────────────────────────────────────────────────────────────────

    protected static JwtOptions GetJwtOptions() => new()
    {
        Issuer           = JwtIssuer,
        TenantAudience   = JwtTenantAudience,
        PlatformAudience = JwtPlatformAudience,
        SigningKey        = JwtSigningKey,
        AccessTokenMinutes = 30,
        RefreshTokenDays   = 7
    };

    /// <summary>
    /// Builds a platform-admin JWT that passes the [Authorize(Policy="PlatformAdmin")] check
    /// AND the RequirePlatformRole filter for Owner.
    /// </summary>
    protected static string BuildPlatformToken(string role = PlatformRoles.Owner)
    {
        var key         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub,   "platform-admin"),
            new Claim(JwtRegisteredClaimNames.Email, AdminEmail),
            new Claim(ClaimTypes.Role,               "PlatformAdmin"),
            new Claim("is_platform_admin",           "true"),
            new Claim("platform_role",               role),
            new Claim(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString()),
        };
        var token = new JwtSecurityToken(JwtIssuer, JwtPlatformAudience, claims,
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ── Controller factory ─────────────────────────────────────────────────────

    protected static PlatformController CreateController(
        ZayraDbContext db,
        string platformRole = PlatformRoles.Owner,
        IAuthSeeder? authSeeder = null)
    {
        var jwt    = Options.Create(GetJwtOptions());
        var hasher = new Pbkdf2PasswordHasher();
        var tokenSvc = new JwtTokenService(jwt);
        var config = new ConfigurationBuilder().Build();
        var email  = new FakePlatformEmailService();
        var seeder = authSeeder ?? new FakeAuthSeeder(db, hasher);

        var controller = new PlatformController(
            db, jwt, hasher, seeder, tokenSvc, email, config,
            new NullPlatformMfaService(),
            NullLogger<PlatformController>.Instance,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        // Wire up a ClaimsPrincipal that passes the PlatformAdmin policy
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub,   "platform-admin"),
            new Claim(JwtRegisteredClaimNames.Email, AdminEmail),
            new Claim(ClaimTypes.Role,               "PlatformAdmin"),
            new Claim("is_platform_admin",           "true"),
            new Claim("platform_role",               platformRole),
        };
        var identity  = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // Set env-vars so login fallback works
        Environment.SetEnvironmentVariable("PLATFORM_ADMIN_EMAIL",    AdminEmail);
        Environment.SetEnvironmentVariable("PLATFORM_ADMIN_PASSWORD", AdminPassword);

        return controller;
    }
}

// ── Fakes ──────────────────────────────────────────────────────────────────────

internal sealed class FakePlatformEmailService : IEmailService
{
    public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

internal sealed class NullPlatformMfaService : IMfaService
{
    public Task<MfaSetupInitDto> InitiateSetupAsync(Guid userId, Guid tenantId, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> VerifySetupAsync(Guid userId, Guid tenantId, MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<string> CreateEnrollmentChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken ct) => throw new NotImplementedException();
    public Task<MfaSetupInitDto?> InitiateEnrollmentSetupAsync(string token, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> VerifyEnrollmentSetupAsync(string token, MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<string> CreateChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken ct) => throw new NotImplementedException();
    public Task<Zayra.Api.Domain.Entities.User?> VerifyChallengeAsync(string token, string code, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> DisableAsync(Guid userId, Guid tenantId, string code, CancellationToken ct) => throw new NotImplementedException();
    public Task<MfaSetupInitDto> InitiatePlatformSetupAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> VerifyPlatformSetupAsync(Guid id, MfaVerifySetupRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<string> CreatePlatformChallengeAsync(Guid id, string ip, CancellationToken ct) => throw new NotImplementedException();
    public Task<Zayra.Api.Models.PlatformUser?> VerifyPlatformChallengeAsync(string token, string code, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> DisablePlatformAsync(Guid id, string code, CancellationToken ct) => throw new NotImplementedException();
}

internal sealed class FakeAuthSeeder : IAuthSeeder
{
    private readonly ZayraDbContext _db;
    private readonly IPasswordHasher _hasher;

    public FakeAuthSeeder(ZayraDbContext db, IPasswordHasher hasher)
    {
        _db     = db;
        _hasher = hasher;
    }

    public Task SeedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<Role> EnsureTenantRolesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var existing = await _db.Roles
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == "Admin", cancellationToken);
        if (existing is not null) return existing;

        var role = new Role
        {
            Id             = Guid.NewGuid(),
            TenantId       = tenantId,
            Name           = "Admin",
            NormalizedName = "ADMIN",
            Description    = "Admin"
        };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync(cancellationToken);
        return role;
    }
}
