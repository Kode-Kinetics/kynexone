using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// Shared helpers: an isolated InMemory database per call and the test JWT options. Uses InMemory EF so
/// no Docker is needed. (It carried the platform controller factory until the platform was removed.)
/// </summary>
public abstract class PlatformTestBase
{
    protected const string JwtSigningKey = "TEST_PLATFORM_SIGNING_KEY_MUST_BE_64_CHARS_FOR_TESTS___PADDED";
    protected const string JwtIssuer     = "Zayra.Tests";
    protected const string JwtAudience   = "kynexone-test";

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
        Issuer             = JwtIssuer,
        Audience           = JwtAudience,
        SigningKey         = JwtSigningKey,
        AccessTokenMinutes = 30,
        RefreshTokenDays   = 7
    };
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
    public Task<bool> AdminDisableAsync(Guid userId, Guid tenantId, RequestContext context, CancellationToken ct) => throw new NotImplementedException();
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
