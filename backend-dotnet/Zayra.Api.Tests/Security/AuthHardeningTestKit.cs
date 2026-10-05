using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// A real <see cref="AuthService"/> + <see cref="MfaService"/> over a private SQLite database, so the
/// relational paths (guarded UPDATEs, execution strategy, transactions) run as they do on Postgres.
/// </summary>
internal sealed class AuthHardeningTestKit : IAsyncDisposable
{
    public const string TenantSlug = "hardening";
    public static readonly RequestContext Ctx = new("127.0.0.1", "tests");

    private readonly SqliteConnection _anchor;
    private readonly DbContextOptions<ZayraDbContext> _options;
    private readonly IDataProtectionProvider _protection = DataProtectionProvider.Create("ZayraTests");

    public Guid TenantId { get; private set; }

    private AuthHardeningTestKit(SqliteConnection anchor, DbContextOptions<ZayraDbContext> options)
    {
        _anchor = anchor;
        _options = options;
    }

    public static async Task<AuthHardeningTestKit> CreateAsync()
    {
        var anchor = new SqliteConnection($"Data Source=file:auth-hardening-{Guid.NewGuid():N}?mode=memory&cache=shared");
        await anchor.OpenAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(anchor).Options;
        var kit = new AuthHardeningTestKit(anchor, options);
        await using var db = kit.NewDb();
        await db.Database.EnsureCreatedAsync();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Hardening", Slug = TenantSlug, IsActive = true };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        kit.TenantId = tenant.Id;
        return kit;
    }

    public ZayraDbContext NewDb() => new(_options);

    public TotpService Totp => new(_protection);

    public static IOptions<JwtOptions> Jwt { get; } = Options.Create(new JwtOptions
    {
        Issuer = "Zayra.Tests",
        TenantAudience = "kynexone-tenant-test",
        PlatformAudience = "kynexone-platform-test",
        SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_AUTH_TESTS",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7,
    });

    public AuthService Auth(ZayraDbContext db, IPasswordHasher? hasher = null, IConfiguration? config = null)
    {
        var tokens = new JwtTokenService(Jwt);
        var audit = new AuditService(db);
        return new AuthService(
            db,
            hasher ?? new Pbkdf2PasswordHasher(),
            tokens,
            audit,
            new NoEmail(),
            Jwt,
            new MfaService(db, Totp, tokens, audit),
            Totp,
            NullLogger<AuthService>.Instance,
            config ?? new ConfigurationBuilder().Build());
    }

    public MfaService Mfa(ZayraDbContext db)
    {
        var tokens = new JwtTokenService(Jwt);
        return new MfaService(db, Totp, tokens, new AuditService(db));
    }

    /// <summary>Seeds an active, confirmed, group-scope tenant user holding <paramref name="roleName"/>.</summary>
    public async Task<Guid> SeedUserAsync(string email, string passwordHash, string? roleName)
    {
        await using var db = NewDb();
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FullName = email,
            PasswordHash = passwordHash,
            Status = "Active",
            IsActive = true,
            IsEmailConfirmed = true,
            IsGroupScope = true,
        };
        db.Users.Add(user);
        if (roleName is not null)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.TenantId == TenantId && r.Name == roleName);
            if (role is null)
            {
                role = new Role
                {
                    Id = Guid.NewGuid(), TenantId = TenantId, Name = roleName,
                    NormalizedName = roleName.ToUpperInvariant(), Description = roleName,
                };
                db.Roles.Add(role);
            }
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        }
        await db.SaveChangesAsync();
        return user.Id;
    }

    public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();
}

file sealed class NoEmail : IEmailService
{
    public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}
