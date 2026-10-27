using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Users &amp; Access hardening: a reset link is never handed to yourself, an issued link and its use carry one id in
/// the audit trail, and free text from a
/// request lands in audit metadata as data (valid JSON), never as structure.
/// </summary>
public sealed class AccessHardeningTests
{
    [Fact]
    public async Task NoResetLinkForYourself()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var result = await Controller(db, w, w.AdminId).IssuePasswordResetLink(w.AdminId, default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.PasswordResetTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AnIssuedLink_AndItsUse_CarryTheSameIdInTheAuditTrail()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var body = Body((await Controller(db, w, w.AdminId).IssuePasswordResetLink(w.TargetId, default)).Should().BeOfType<OkObjectResult>().Subject);
        var url = body.GetProperty("resetUrl").GetString()!;
        var token = Uri.UnescapeDataString(url[(url.IndexOf("#token=", StringComparison.Ordinal) + "#token=".Length)..]);
        await Auth(db).ResetPasswordAsync(new ResetPasswordRequest(token, "BrandNewPassword1!", w.Slug), Ctx, default);

        var issued = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "access.password_reset_link_issued");
        var used = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "auth.password_reset");
        var tokenId = (await db.PasswordResetTokens.AsNoTracking().SingleAsync()).Id.ToString();
        JsonDocument.Parse(issued.Metadata!).RootElement.GetProperty("resetTokenId").GetString().Should().Be(tokenId);
        JsonDocument.Parse(used.Metadata!).RootElement.GetProperty("resetTokenId").GetString().Should().Be(tokenId);
        issued.UserId.Should().Be(w.AdminId);
    }

    // A password typed at Create User is only temporary: AccessManagementScopeTests (Postgres; user creation needs a
    // relational transaction) asserts MustChangePassword, and AdminSetTemporaryPasswordTests covers the sign-in step.

    [Fact]
    public async Task ALockReasonWithQuotes_IsStoredAsData()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        const string reason = "left \"urgently\", see ticket\\42\",\"actor\":\"someone-else";

        var result = await Controller(db, w, w.AdminId).LockUser(w.TargetId, new ReasonRequest(reason), default);

        result.Should().BeOfType<NoContentResult>();
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "access.user_locked");
        var root = JsonDocument.Parse(audit.Metadata!).RootElement;
        root.GetProperty("reason").GetString().Should().Be(reason);
        root.TryGetProperty("actor", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AFailedSignIn_WithQuotesInTheEmail_WritesValidAuditMetadata()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        const string email = "x\",\"failedCount\":0,\"reason\":\"ok@example.test";

        var attempt = () => Auth(db).LoginAsync(new LoginRequest(email, "Whatever-Pass1!", w.Slug), Ctx, default);
        await attempt.Should().ThrowAsync<UnauthorizedAccessException>();

        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "auth.login_failed");
        var root = JsonDocument.Parse(audit.Metadata!).RootElement;
        root.GetProperty("email").GetString().Should().Be(email);
        root.TryGetProperty("failedCount", out _).Should().BeFalse();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private const string TargetEmail = "temp.target@example.test";
    private static readonly RequestContext Ctx = new("127.0.0.1", "tests");

    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "Zayra.Tests",
        TenantAudience = "kynexone-tenant-test",
        PlatformAudience = "kynexone-platform-test",
        SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_ACCESS_HARDENING_TESTS",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7,
    });

    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["APP_URL"] = "https://app.kynexone.test" })
        .Build();

    private sealed record World(Guid TenantId, string Slug, Guid AdminId, Guid TargetId);

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<World> SeedAsync(ZayraDbContext db, string slug = "kynex-hq")
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = slug, Slug = slug, IsActive = true };
        var hasher = new Pbkdf2PasswordHasher();
        var target = new User
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Tenant = tenant,
            Email = TargetEmail, NormalizedEmail = TargetEmail.ToUpperInvariant(), FullName = "Temp Target",
            PasswordHash = hasher.Hash("OriginalPassword1!"), Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true,
        };
        var adminEmail = $"admin@{slug}.test";
        var admin = new User
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Tenant = tenant,
            Email = adminEmail, NormalizedEmail = adminEmail.ToUpperInvariant(), FullName = "Acting Admin",
            PasswordHash = hasher.Hash("AdminPassword1!"), Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true,
        };
        var adminRole = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin", IsActive = true };
        db.Tenants.Add(tenant);
        db.SecuritySettings.Add(new SecuritySetting { Id = Guid.NewGuid(), TenantId = tenant.Id });
        db.Users.AddRange(target, admin);
        db.Roles.Add(adminRole);
        db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
        await db.SaveChangesAsync();
        return new World(tenant.Id, slug, admin.Id, target.Id);
    }

    private static AccessController Controller(ZayraDbContext db, World w, Guid callerId) =>
        new(new AccessManagementService(db, new Pbkdf2PasswordHasher(), new AuditService(db), new JwtTokenService(Jwt), Configuration),
            db,
            new NoEmail(),
            Auth(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", w.TenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, callerId.ToString()),
                        new Claim("is_group_scope", "true"),
                    }, "test")),
                },
            },
        };

    private static AuthService Auth(ZayraDbContext db)
    {
        var tokenService = new JwtTokenService(Jwt);
        var audit = new AuditService(db);
        var totp = new TotpService(DataProtectionProvider.Create("ZayraAccessHardeningTests"));
        return new AuthService(db, new Pbkdf2PasswordHasher(), tokenService, audit, new NoEmail(), Jwt,
            new MfaService(db, totp, tokenService, audit), totp, NullLogger<AuthService>.Instance, Configuration);
    }

    private static JsonElement Body(ObjectResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Value)).RootElement;

    private sealed class NoEmail : IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
