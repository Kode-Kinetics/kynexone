using FluentAssertions;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;   // PlatformLoginRequest, CreateTenantRequest, etc. (declared in PlatformController.cs)
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;        // PlatformUser, PlatformRoles

namespace Zayra.Api.Tests.Platform;

public class PlatformAuthTests : PlatformTestBase
{
    // ── Login ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_WithValidEnvVarCredentials_Returns200WithToken()
    {
        await using var db         = CreateDb();
        var controller             = CreateController(db);
        var req                    = new PlatformLoginRequest(AdminEmail, AdminPassword);

        var result = await controller.Login(req, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value!.ToString()!;
        body.Should().Contain("token");
    }

    [Fact]
    public async Task BootstrapLogin_MaterializesRevocablePlatformUserAndStampedToken()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);

        var result = await controller.Login(
            new PlatformLoginRequest(AdminEmail, AdminPassword), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = JsonSerializer.SerializeToElement(ok.Value);
        var principal = PrincipalFrom(body.GetProperty("token").GetString()!);
        var stored = await db.PlatformUsers.SingleAsync();

        principal.FindFirstValue(JwtRegisteredClaimNames.Sub).Should().Be(stored.Id.ToString());
        principal.FindFirstValue(PlatformSessionSecurity.SessionStampClaim).Should().NotBeNullOrWhiteSpace();
        (await PlatformSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task PlatformLogout_RevokesPreviouslyIssuedTokenServerSide()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);
        var login = (OkObjectResult)await controller.Login(
            new PlatformLoginRequest(AdminEmail, AdminPassword), CancellationToken.None);
        var token = JsonSerializer.SerializeToElement(login.Value).GetProperty("token").GetString()!;
        var principal = PrincipalFrom(token);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        (await controller.PlatformLogout(CancellationToken.None)).Should().BeOfType<NoContentResult>();

        (await PlatformSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None))
            .Should().BeFalse("logout rotates the server-side session stamp");
    }

    [Fact]
    public async Task FailedLogin_DoesNotRevokeAnExistingPlatformSession()
    {
        await using var db = CreateDb();
        var controller = CreateController(db);
        var login = (OkObjectResult)await controller.Login(
            new PlatformLoginRequest(AdminEmail, AdminPassword), CancellationToken.None);
        var token = JsonSerializer.SerializeToElement(login.Value).GetProperty("token").GetString()!;
        var principal = PrincipalFrom(token);

        var rejected = await controller.Login(
            new PlatformLoginRequest(AdminEmail, "WRONG_PASSWORD"), CancellationToken.None);

        rejected.Should().BeOfType<UnauthorizedObjectResult>();
        (await PlatformSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None))
            .Should().BeTrue("login telemetry is not an administrative security-stamp change");
        (await db.PlatformUsers.SingleAsync()).FailedLoginCount.Should().Be(1);
    }

    [Theory]
    [InlineData(false, PlatformRoles.Owner)]
    [InlineData(true, PlatformRoles.Auditor)]
    public async Task PlatformToken_IsRejectedAfterDeactivationOrRoleChange(bool active, string currentRole)
    {
        await using var db = CreateDb();
        var controller = CreateController(db);
        var login = (OkObjectResult)await controller.Login(
            new PlatformLoginRequest(AdminEmail, AdminPassword), CancellationToken.None);
        var token = JsonSerializer.SerializeToElement(login.Value).GetProperty("token").GetString()!;
        var principal = PrincipalFrom(token);
        var stored = await db.PlatformUsers.SingleAsync();
        stored.IsActive = active;
        stored.Role = currentRole;
        await db.SaveChangesAsync();

        (await PlatformSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None))
            .Should().BeFalse("active state and current role are checked on every platform request");
    }

    private static ClaimsPrincipal PrincipalFrom(string token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        return new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims, "Bearer"));
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);
        var req             = new PlatformLoginRequest(AdminEmail, "WRONG_PASSWORD");

        var result = await controller.Login(req, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Login_WithWrongEmail_Returns401()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);
        var req             = new PlatformLoginRequest("nobody@example.com", AdminPassword);

        var result = await controller.Login(req, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Login_WithEmptyEmail_Returns400()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);
        var req             = new PlatformLoginRequest("", AdminPassword);

        var result = await controller.Login(req, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Login_WithDbPlatformUser_Returns200()
    {
        // Arrange: insert a DB-based platform user
        await using var db  = CreateDb();
        var controller      = CreateController(db);
        var hasher          = new Zayra.Api.Infrastructure.Auth.Pbkdf2PasswordHasher();
        var dbUser = new Zayra.Api.Models.PlatformUser
        {
            Email        = "dbuser@platform.local",
            FullName     = "DB User",
            PasswordHash = hasher.Hash("DbPassword123!"),
            Role         = Zayra.Api.Models.PlatformRoles.Finance,
            IsActive     = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.PlatformUsers.Add(dbUser);
        await db.SaveChangesAsync();

        var req = new PlatformLoginRequest("dbuser@platform.local", "DbPassword123!");

        var result = await controller.Login(req, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value!.ToString().Should().Contain("token");
    }

    [Fact]
    public async Task Login_WithDbUserAndWrongPassword_Returns401()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);
        var hasher          = new Zayra.Api.Infrastructure.Auth.Pbkdf2PasswordHasher();
        db.PlatformUsers.Add(new Zayra.Api.Models.PlatformUser
        {
            Email        = "dbuser2@platform.local",
            FullName     = "DB User 2",
            PasswordHash = hasher.Hash("RealPassword123!"),
            Role         = Zayra.Api.Models.PlatformRoles.Admin,
            IsActive     = true,
        });
        await db.SaveChangesAsync();

        var req = new PlatformLoginRequest("dbuser2@platform.local", "WRONG_PASSWORD");

        var result = await controller.Login(req, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    // ── Stats (auth guard) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Stats_WithPlatformAdminClaims_Returns200WithTotalTenants()
    {
        await using var db  = CreateDb();
        var controller      = CreateController(db);

        var result = await controller.Stats(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        // The response should contain totalTenants — serialise via anonymous type
        var body = ok.Value!;
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        json.Should().Contain("totalTenants");
    }

    [Fact]
    public async Task Stats_TotalTenantsReflectsSeededData()
    {
        await using var db  = CreateDb();
        // Seed two tenants
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Name = "Alpha", Slug = "alpha" });
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Name = "Beta",  Slug = "beta"  });
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var result = await controller.Stats(CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(ok.Value));
        body.GetProperty("totalTenants").GetInt32().Should().Be(2);
    }
}
