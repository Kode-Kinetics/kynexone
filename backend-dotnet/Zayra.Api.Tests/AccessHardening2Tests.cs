using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Forgot-password answers in the same time and at most once a minute (five an hour) per account, a new link kills the
/// old one; an administrator's lock is not bypassed by a reset link, a wrong-password lockout ends with a reset; and a
/// company-scoped administrator changes only people who belong entirely to their companies.
/// </summary>
public sealed class AccessHardening2Tests
{
    private const string Password = "Correct-Horse-9!";

    // ── Forgot password ───────────────────────────────────────────────────────

    [Fact]
    public async Task ForgotPassword_IsHeldBackInsideTheCooldown_AndANewLinkKillsTheOldOne()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var auth = Auth(db);

        await auth.ForgotPasswordAsync(new ForgotPasswordRequest(EmployeeAEmail, w.Slug), Ctx, default);
        await auth.ForgotPasswordAsync(new ForgotPasswordRequest(EmployeeAEmail, w.Slug), Ctx, default);
        (await db.PasswordResetTokens.CountAsync()).Should().Be(1, "the second request came inside the cooldown");

        // A minute later a new link is issued, and the first one stops working.
        var first = await db.PasswordResetTokens.SingleAsync();
        first.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2);
        await db.SaveChangesAsync();
        await auth.ForgotPasswordAsync(new ForgotPasswordRequest(EmployeeAEmail, w.Slug), Ctx, default);
        var tokens = await db.PasswordResetTokens.AsNoTracking().OrderBy(x => x.CreatedAtUtc).ToListAsync();
        tokens.Should().HaveCount(2);
        tokens[0].ExpiresAtUtc.Should().BeOnOrBefore(DateTime.UtcNow, "superseded");
        tokens[1].ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task ForgotPassword_AtMostFiveAnHour()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        for (var i = 0; i < 5; i++)
            db.PasswordResetTokens.Add(new PasswordResetToken
            {
                UserId = w.EmployeeAId, TokenHash = $"old-{i}", CreatedAtUtc = DateTime.UtcNow.AddMinutes(-50 + i * 5),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1),
            });
        await db.SaveChangesAsync();

        await Auth(db).ForgotPasswordAsync(new ForgotPasswordRequest(EmployeeAEmail, w.Slug), Ctx, default);

        (await db.PasswordResetTokens.CountAsync()).Should().Be(5);
    }

    [Fact]
    public async Task ForgotPassword_TakesTheSameMinimumTime_ForAnAccountThatExists_AndOneThatDoesNot()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var auth = Auth(db, minimumMs: 400);

        var known = Stopwatch.StartNew();
        await auth.ForgotPasswordAsync(new ForgotPasswordRequest(EmployeeAEmail, w.Slug), Ctx, default);
        known.Stop();
        var unknown = Stopwatch.StartNew();
        await auth.ForgotPasswordAsync(new ForgotPasswordRequest("nobody@example.test", w.Slug), Ctx, default);
        unknown.Stop();

        known.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(380);
        unknown.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(380);
    }

    // ── Locks and reset links ────────────────────────────────────────────────

    [Fact]
    public async Task NoResetLink_ForAnAccountAnAdministratorLocked()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var target = await db.Users.SingleAsync(x => x.Id == w.EmployeeAId);
        target.Status = "Locked";
        target.IsLocked = true;
        target.LockoutEnd = DateTime.UtcNow.AddDays(1);
        await db.SaveChangesAsync();

        var result = await Controller(db, w, w.GroupAdminId, groupScope: true).IssuePasswordResetLink(w.EmployeeAId, default);

        Json(result.Should().BeOfType<BadRequestObjectResult>().Subject).GetProperty("message").GetString().Should().Contain("Unlock it first");
        (await db.PasswordResetTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AReset_EndsAWrongPasswordLockout()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var target = await db.Users.SingleAsync(x => x.Id == w.EmployeeAId);
        target.FailedLoginCount = 5;
        target.IsLocked = true;
        target.LockoutEnd = DateTime.UtcNow.AddMinutes(15);
        await db.SaveChangesAsync();
        var body = Json((await Controller(db, w, w.GroupAdminId, groupScope: true).IssuePasswordResetLink(w.EmployeeAId, default))
            .Should().BeOfType<OkObjectResult>().Subject);
        var url = body.GetProperty("resetUrl").GetString()!;
        var token = Uri.UnescapeDataString(url[(url.IndexOf("#token=", StringComparison.Ordinal) + "#token=".Length)..]);

        await Auth(db).ResetPasswordAsync(new ResetPasswordRequest(token, "BrandNew-Pass9!", w.Slug), Ctx, default);

        var after = await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.EmployeeAId);
        after.IsLocked.Should().BeFalse();
        after.LockoutEnd.Should().BeNull();
        after.FailedLoginCount.Should().Be(0);
    }

    // ── Company-scoped administrators ────────────────────────────────────────

    [Fact]
    public async Task ACompanyAdmin_ChangesSomeoneWhollyInTheirCompany()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);

        var result = await Controller(db, w, w.CompanyAdminId, groupScope: false).LockUser(w.EmployeeAId, new ReasonRequest("x"), default);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task ACompanyAdmin_CannotChangeSomeoneWhoAlsoWorksInAnotherCompany()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        db.UserEntityAccesses.Add(new UserEntityAccess
        {
            TenantId = w.TenantId, UserId = w.EmployeeAId, CompanyId = w.CompanyB, GrantMode = EntityGrantModes.SelectedCompanies,
            Role = "Employee", IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await Controller(db, w, w.CompanyAdminId, groupScope: false).LockUser(w.EmployeeAId, new ReasonRequest("x"), default);

        var refused = result.Should().BeAssignableTo<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        Json(refused).GetProperty("code").GetString().Should().Be(PrivilegeCeiling.Codes.TargetOtherCompanies);
        (await db.Users.AsNoTracking().SingleAsync(x => x.Id == w.EmployeeAId)).Status.Should().Be("Active");

        // A group administrator still can.
        (await Controller(db, w, w.GroupAdminId, groupScope: true).LockUser(w.EmployeeAId, new ReasonRequest("x"), default))
            .Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task ACompanyAdmin_CannotChangeAGroupWideUser()
    {
        await using var db = CreateDb();
        var w = await SeedAsync(db);
        var target = await db.Users.SingleAsync(x => x.Id == w.EmployeeAId);
        target.IsGroupScope = true;
        await db.SaveChangesAsync();

        var result = await Controller(db, w, w.CompanyAdminId, groupScope: false).SuspendUser(w.EmployeeAId, new ReasonRequest("x"), default);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private const string EmployeeAEmail = "employee.a@example.test";
    private static readonly RequestContext Ctx = new("127.0.0.1", "tests");

    private static readonly IOptions<JwtOptions> Jwt = Options.Create(new JwtOptions
    {
        Issuer = "Zayra.Tests",
        TenantAudience = "kynexone-tenant-test",
        PlatformAudience = "kynexone-platform-test",
        SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_ACCESS_HARDENING_TWO_TESTS",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7,
    });

    private sealed record World(Guid TenantId, string Slug, Guid CompanyA, Guid CompanyB, Guid GroupAdminId, Guid CompanyAdminId, Guid EmployeeAId);

    private static ZayraDbContext CreateDb() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<World> SeedAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Kynex HQ", Slug = "kynex-hq", IsActive = true };
        var hash = new Pbkdf2PasswordHasher().Hash(Password);
        var companyA = new Company { Id = Guid.NewGuid(), TenantId = tenant.Id, LegalNameEn = "Company A" };
        var companyB = new Company { Id = Guid.NewGuid(), TenantId = tenant.Id, LegalNameEn = "Company B" };
        User NewUser(string email, bool groupScope = false) => new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Tenant = tenant, Email = email, NormalizedEmail = email.ToUpperInvariant(),
            FullName = email, PasswordHash = hash, Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true, IsGroupScope = groupScope,
        };
        var groupAdmin = NewUser("group.admin@example.test", groupScope: true);
        var companyAdmin = NewUser("company.admin@example.test");
        var employee = NewUser(EmployeeAEmail);
        var adminRole = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin", IsActive = true };
        var employeeRole = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee", IsActive = true };
        var employeeRecord = new Employee { TenantId = tenant.Id, CompanyId = companyA.Id, EmployeeCode = "EMP-A-1", FullName = "Employee A" };
        db.Tenants.Add(tenant);
        db.Companies.AddRange(companyA, companyB);
        db.SecuritySettings.Add(new SecuritySetting { Id = Guid.NewGuid(), TenantId = tenant.Id });
        db.Users.AddRange(groupAdmin, companyAdmin, employee);
        db.Roles.AddRange(adminRole, employeeRole);
        db.UserRoles.AddRange(
            new UserRole { UserId = groupAdmin.Id, RoleId = adminRole.Id },
            new UserRole { UserId = companyAdmin.Id, RoleId = adminRole.Id },
            new UserRole { UserId = employee.Id, RoleId = employeeRole.Id });
        db.UserEntityAccesses.Add(new UserEntityAccess
        {
            TenantId = tenant.Id, UserId = companyAdmin.Id, CompanyId = companyA.Id, GrantMode = EntityGrantModes.SelectedCompanies,
            Role = "Company Administrator", IsActive = true,
        });
        db.Employees.Add(employeeRecord);
        await db.SaveChangesAsync();
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = tenant.Id, EmployeeId = employeeRecord.Id, UserId = employee.Id, AccessMode = AccessModes.FullPortal, Status = "Active", IsPrimary = true });
        await db.SaveChangesAsync();
        return new World(tenant.Id, tenant.Slug, companyA.Id, companyB.Id, groupAdmin.Id, companyAdmin.Id, employee.Id);
    }

    private static IConfiguration Config(int minimumMs) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["APP_URL"] = "https://app.kynexone.test",
        ["Auth:ForgotPasswordMinimumMs"] = minimumMs.ToString(),
    }).Build();

    private static AccessController Controller(ZayraDbContext db, World w, Guid callerId, bool groupScope)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, callerId.ToString()),
            new("is_group_scope", groupScope ? "true" : "false"),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(groupScope
                ? new { v = 2, m = "group", c = Array.Empty<Guid>() }
                : new { v = 2, m = "companies", c = new[] { w.CompanyA } })),
        };
        return new AccessController(
            new AccessManagementService(db, new Pbkdf2PasswordHasher(), new AuditService(db), new JwtTokenService(Jwt), Config(0)),
            db, new NoEmail(), Auth(db), stepUp: StepUpTestKit.AlwaysConfirmed(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }

    private static AuthService Auth(ZayraDbContext db, int minimumMs = 0)
    {
        var tokens = new JwtTokenService(Jwt);
        var audit = new AuditService(db);
        var totp = new TotpService(StepUpTestKit.Protection);
        return new AuthService(db, new Pbkdf2PasswordHasher(), tokens, audit, new NoEmail(), Jwt, new MfaService(db, totp, tokens, audit), totp,
            NullLogger<AuthService>.Instance, Config(minimumMs));
    }

    private static JsonElement Json(ObjectResult result) => JsonDocument.Parse(JsonSerializer.Serialize(result.Value)).RootElement;

    private sealed class NoEmail : IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
