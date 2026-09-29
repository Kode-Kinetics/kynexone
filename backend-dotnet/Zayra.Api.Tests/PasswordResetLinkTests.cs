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
/// The administrator-initiated password reset, end to end.
///
/// <para>Before this suite, <c>POST /api/access/users/{id}/admin-reset-password</c> returned a
/// hard-coded 409 with no condition attached to it at all — the temporary-password flow had been
/// deliberately retired in favour of "the controlled password-reset link", but for a tenant
/// administrator that link did not exist anywhere. The product's only Reset Password control could
/// therefore never succeed, for any user, in any state.</para>
///
/// <para>The second half of this suite covers the delivery half of the same defect: production has
/// no SMTP configured, so an endpoint that reported success would be reporting an email that was
/// never sent. Every path here asserts on what the response CLAIMS, not only on what it stored.</para>
/// </summary>
public sealed class PasswordResetLinkTests
{
    // ── The fix: a legitimate reset now succeeds ──────────────────────────────

    [Fact]
    public async Task IssuePasswordResetLink_Succeeds_AndHandsBackACopyableLink_WhenNoMailTransportExists()
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedActiveUserAsync(db);

        var result = await Controller(db, tenant.Id, new FakeEmailService(configured: false))
            .IssuePasswordResetLink(user.Id, default);

        var body = Body(result.Should().BeOfType<OkObjectResult>().Subject);
        body.GetProperty("emailDeliveryConfigured").GetBoolean().Should().BeFalse();
        body.GetProperty("emailSent").GetBoolean().Should().BeFalse();

        // The whole point: a live link the administrator can pass on by hand.
        var resetUrl = body.GetProperty("resetUrl").GetString();
        resetUrl.Should().NotBeNullOrWhiteSpace();
        resetUrl.Should().Contain("/reset-password").And.Contain($"workspace={tenant.Slug}");

        // And the message must say plainly that nothing was emailed.
        body.GetProperty("message").GetString().Should().Contain("No email delivery is configured");

        (await db.PasswordResetTokens.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IssuePasswordResetLink_EmailsTheUserAndWithholdsTheLink_WhenAMailTransportExists()
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedActiveUserAsync(db);
        var email = new FakeEmailService(configured: true);

        var result = await Controller(db, tenant.Id, email).IssuePasswordResetLink(user.Id, default);

        var body = Body(result.Should().BeOfType<OkObjectResult>().Subject);
        body.GetProperty("emailDeliveryConfigured").GetBoolean().Should().BeTrue();
        body.GetProperty("emailSent").GetBoolean().Should().BeTrue();

        // The user has it in their inbox; a second copy in an admin's browser is pure extra exposure.
        body.GetProperty("resetUrl").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("message").GetString().Should().Contain("Reset link emailed to");

        email.Sent.Should().ContainSingle();
        email.Sent[0].To.Should().Be(user.Email);
        email.Sent[0].Html.Should().Contain("/reset-password");
    }

    [Fact]
    public async Task IssuePasswordResetLink_SaysDeliveryFailed_RatherThanClaimingSuccess_WhenTheRelayThrows()
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedActiveUserAsync(db);

        var result = await Controller(db, tenant.Id, new FakeEmailService(configured: true, throwOnSend: true))
            .IssuePasswordResetLink(user.Id, default);

        var body = Body(result.Should().BeOfType<OkObjectResult>().Subject);
        body.GetProperty("emailDeliveryConfigured").GetBoolean().Should().BeTrue();
        body.GetProperty("emailSent").GetBoolean().Should().BeFalse();
        body.GetProperty("resetUrl").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("message").GetString().Should().Contain("could not be sent");
    }

    [Fact]
    public async Task IssuedLink_ActuallyResetsThePassword_AndIsThenSpent()
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedActiveUserAsync(db);
        var originalHash = user.PasswordHash;

        var body = Body((await Controller(db, tenant.Id, new FakeEmailService(configured: false))
            .IssuePasswordResetLink(user.Id, default)).Should().BeOfType<OkObjectResult>().Subject);
        var token = TokenFromUrl(body.GetProperty("resetUrl").GetString()!);

        await BuildAuthService(db).ResetPasswordAsync(
            new ResetPasswordRequest(token, "BrandNewPassword1!", tenant.Slug), Ctx, default);

        (await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id))
            .PasswordHash.Should().NotBe(originalHash);

        // Single use: the same link must not work a second time.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            BuildAuthService(db).ResetPasswordAsync(
                new ResetPasswordRequest(token, "YetAnotherPassword1!", tenant.Slug), Ctx, default));
    }

    [Fact]
    public async Task IssuedLink_IsRefusedOnceItHasExpired()
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedActiveUserAsync(db);

        var body = Body((await Controller(db, tenant.Id, new FakeEmailService(configured: false))
            .IssuePasswordResetLink(user.Id, default)).Should().BeOfType<OkObjectResult>().Subject);
        var token = TokenFromUrl(body.GetProperty("resetUrl").GetString()!);

        // One hour, matching the self-service link — an admin-initiated reset is not the longer door.
        var issued = await db.PasswordResetTokens.SingleAsync();
        (issued.ExpiresAtUtc - issued.CreatedAtUtc).Should().BeCloseTo(TimeSpan.FromHours(1), TimeSpan.FromSeconds(5));

        issued.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            BuildAuthService(db).ResetPasswordAsync(
                new ResetPasswordRequest(token, "BrandNewPassword1!", tenant.Slug), Ctx, default));
    }

    [Fact]
    public async Task IssuingASecondLink_KillsTheFirstOne()
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedActiveUserAsync(db);
        var controller = Controller(db, tenant.Id, new FakeEmailService(configured: false));

        var first = TokenFromUrl(Body((await controller.IssuePasswordResetLink(user.Id, default))
            .Should().BeOfType<OkObjectResult>().Subject).GetProperty("resetUrl").GetString()!);
        var second = TokenFromUrl(Body((await controller.IssuePasswordResetLink(user.Id, default))
            .Should().BeOfType<OkObjectResult>().Subject).GetProperty("resetUrl").GetString()!);

        first.Should().NotBe(second);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            BuildAuthService(db).ResetPasswordAsync(
                new ResetPasswordRequest(first, "BrandNewPassword1!", tenant.Slug), Ctx, default));

        await BuildAuthService(db).ResetPasswordAsync(
            new ResetPasswordRequest(second, "BrandNewPassword1!", tenant.Slug), Ctx, default);
    }

    [Fact]
    public async Task IssuePasswordResetLink_IsAudited_AndRecordsTheDisclosureSeparately()
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedActiveUserAsync(db);

        await Controller(db, tenant.Id, new FakeEmailService(configured: false))
            .IssuePasswordResetLink(user.Id, default);

        (await db.AuditLogs.AsNoTracking().ToListAsync())
            .Should().ContainSingle(x => x.Action == "access.password_reset_link_issued");

        var disclosure = (await db.AdminAuditLogs.AsNoTracking().ToListAsync())
            .Should().ContainSingle(x => x.Action == "PasswordResetLinkDisclosedToAdmin").Subject;
        JsonDocument.Parse(disclosure.NewValuesJson).RootElement
            .GetProperty("linkShownToAdmin").GetBoolean().Should().BeTrue();
    }

    // ── Still refused: the fix must not have opened anything ──────────────────

    [Fact]
    public async Task IssuePasswordResetLink_RefusesAUserBelongingToAnotherTenant()
    {
        await using var db = CreateDb();
        var (_, user) = await SeedActiveUserAsync(db);
        var otherTenant = Guid.NewGuid();

        var result = await Controller(db, otherTenant, new FakeEmailService(configured: false))
            .IssuePasswordResetLink(user.Id, default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.PasswordResetTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task IssuePasswordResetLink_RefusesAnUnknownUser()
    {
        await using var db = CreateDb();
        var (tenant, _) = await SeedActiveUserAsync(db);

        var result = await Controller(db, tenant.Id, new FakeEmailService(configured: false))
            .IssuePasswordResetLink(Guid.NewGuid(), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.PasswordResetTokens.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("Suspended", false)]
    [InlineData("Deactivated", false)]
    [InlineData("Invited", false)]
    public async Task IssuePasswordResetLink_RefusesAnAccountThatCouldNotRedeemTheLink(string status, bool isActive)
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedActiveUserAsync(db);
        user.Status = status;
        user.IsActive = isActive;
        await db.SaveChangesAsync();

        var result = await Controller(db, tenant.Id, new FakeEmailService(configured: false))
            .IssuePasswordResetLink(user.Id, default);

        // Refused with a reason, not a dead 409 and not a link that silently fails in the user's hands.
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        Body(bad).GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        (await db.PasswordResetTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AdminChosenPasswords_RemainDisabled_ButNowNameTheWorkingAlternative()
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedActiveUserAsync(db);

        var result = await Controller(db, tenant.Id, new FakeEmailService(configured: false))
            .AdminResetPassword(user.Id, new AdminResetPasswordRequest("AdminChosenPass1!"), default);

        var body = Body(result.Should().BeOfType<ConflictObjectResult>().Subject);
        body.GetProperty("error").GetString().Should().Be("temporary_password_flow_disabled");
        body.GetProperty("useEndpoint").GetString().Should().Contain("password-reset-link");
    }

    // ── Invitations must not claim an email that never left ───────────────────

    [Fact]
    public async Task InviteEmployeeLogin_StatesThatNothingWasSent_WhenNoMailTransportExists()
    {
        await using var db = CreateDb();
        var (tenant, _) = await SeedActiveUserAsync(db);
        var employee = new Employee
        {
            TenantId = tenant.Id,
            CompanyId = Guid.NewGuid(),
            EmployeeCode = "EMP-INVITE",
            FullName = "Invited Person",
            EnglishName = "Invited Person",
            WorkEmail = "invited.person@example.test",
            Status = EmployeeStatuses.Active,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await Controller(db, tenant.Id, new FakeEmailService(configured: false))
            .InviteEmployeeLogin(
                new InviteEmployeeLoginRequest(employee.Id, null, AccessModes.FullPortal, null),
                default);

        var invite = (result.Result.Should().BeOfType<CreatedResult>().Subject.Value)
            .Should().BeOfType<EmployeeLoginInvitationDto>().Subject;

        invite.EmailDeliveryConfigured.Should().BeFalse();
        invite.EmailSent.Should().BeFalse();
        invite.DeliveryMessage.Should().Contain("No email delivery is configured");
        // The link is still returned, so the administrator has something to pass on.
        invite.InvitationUrl.Should().Contain("/accept-invitation");
    }

    [Fact]
    public async Task InviteEmployeeLogin_ActuallyEmailsTheInvitation_WhenAMailTransportExists()
    {
        await using var db = CreateDb();
        var (tenant, _) = await SeedActiveUserAsync(db);
        var employee = new Employee
        {
            TenantId = tenant.Id,
            CompanyId = Guid.NewGuid(),
            EmployeeCode = "EMP-INVITE-2",
            FullName = "Second Person",
            EnglishName = "Second Person",
            WorkEmail = "second.person@example.test",
            Status = EmployeeStatuses.Active,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var email = new FakeEmailService(configured: true);

        var result = await Controller(db, tenant.Id, email).InviteEmployeeLogin(
            new InviteEmployeeLoginRequest(employee.Id, null, AccessModes.FullPortal, null), default);

        var invite = (result.Result.Should().BeOfType<CreatedResult>().Subject.Value)
            .Should().BeOfType<EmployeeLoginInvitationDto>().Subject;

        invite.EmailSent.Should().BeTrue();
        invite.DeliveryMessage.Should().Contain("Invitation emailed to");
        email.Sent.Should().ContainSingle();
        email.Sent[0].Html.Should().Contain("/accept-invitation");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private const string AppUrl = "https://app.kynexone.test";
    private static readonly RequestContext Ctx = new("127.0.0.1", "tests");

    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "Zayra.Tests",
        TenantAudience = "kynexone-tenant-test",
        PlatformAudience = "kynexone-platform-test",
        SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_RESET_LINK_TESTS",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7,
    });

    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["APP_URL"] = AppUrl })
        .Build();

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(Tenant tenant, User user)> SeedActiveUserAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Kynex HQ", Slug = "kynex-hq", IsActive = true };
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Tenant = tenant,
            Email = "reset.target@example.test",
            NormalizedEmail = "RESET.TARGET@EXAMPLE.TEST",
            FullName = "Reset Target",
            PasswordHash = new Pbkdf2PasswordHasher().Hash("OriginalPassword1!"),
            Status = "Active",
            AccessMode = AccessModes.FullPortal,
            IsActive = true,
        };
        db.Tenants.Add(tenant);
        db.SecuritySettings.Add(new SecuritySetting { Id = Guid.NewGuid(), TenantId = tenant.Id });
        db.Users.Add(user);
        // The default role an invitation is issued with (AccessManagementService.DefaultRoles).
        db.Roles.Add(new Role
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Employee",
            NormalizedName = "EMPLOYEE",
            Description = "Employee",
            IsActive = true,
        });
        await db.SaveChangesAsync();
        return (tenant, user);
    }

    private static AccessController Controller(ZayraDbContext db, Guid tenantId, IEmailService email) =>
        new(new AccessManagementService(db, new Pbkdf2PasswordHasher(), new AuditService(db), new JwtTokenService(Jwt), Configuration),
            db,
            email)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "Admin"),
                        // Group-level scope, stated explicitly so the assertion does not depend on
                        // whichever way the strict-mode default happens to be set.
                        new Claim("is_group_scope", "true"),
                    }, "test")),
                },
            },
        };

    private static AuthService BuildAuthService(ZayraDbContext db)
    {
        var tokenService = new JwtTokenService(Jwt);
        var audit = new AuditService(db);
        var totp = new TotpService(DataProtectionProvider.Create("ZayraResetLinkTests"));
        return new AuthService(
            db,
            new Pbkdf2PasswordHasher(),
            tokenService,
            audit,
            new FakeEmailService(configured: false),
            Jwt,
            new MfaService(db, totp, tokenService, audit),
            totp,
            NullLogger<AuthService>.Instance,
            Configuration);
    }

    private static JsonElement Body(ObjectResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Value)).RootElement;

    /// <summary>The secret rides in the URL fragment, by design (AuthLinkBuilder).</summary>
    private static string TokenFromUrl(string url) =>
        Uri.UnescapeDataString(url[(url.IndexOf("#token=", StringComparison.Ordinal) + "#token=".Length)..]);

    private sealed class FakeEmailService : IEmailService
    {
        private readonly bool _configured;
        private readonly bool _throwOnSend;

        public FakeEmailService(bool configured, bool throwOnSend = false)
        {
            _configured = configured;
            _throwOnSend = throwOnSend;
        }

        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            if (_throwOnSend) throw new InvalidOperationException("relay refused: 550 at smtp.internal with user hunter2");
            Sent.Add((toAddress, subject, htmlBody));
            return Task.CompletedTask;
        }

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_configured);
    }
}
