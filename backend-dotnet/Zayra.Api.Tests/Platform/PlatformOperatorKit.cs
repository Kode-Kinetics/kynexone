using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// Controller-level helpers for platform tests that need a REAL operator identity (a Guid subject and an email),
/// which <see cref="PlatformTestBase.CreateController"/> does not give (its subject is the literal "platform-admin").
/// Runs over the SQLite kit so relational behaviour matches production.
/// </summary>
internal static class PlatformOperatorKit
{
    public static async Task<PlatformUser> SeedOperatorAsync(
        AuthHardeningTestKit kit, string email, string role = PlatformRoles.Owner, bool active = true)
    {
        await using var db = kit.NewDb();
        var user = new PlatformUser
        {
            Email = email, FullName = email, Role = role, IsActive = active,
            PasswordHash = new Pbkdf2PasswordHasher().Hash("Correct-Horse-Battery-9!"),
        };
        db.PlatformUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>A controller acting as <paramref name="actor"/> (id, role claim and email claim all taken from it).</summary>
    public static PlatformController As(AuthHardeningTestKit kit, ZayraDbContext db, PlatformUser actor)
        => Create(kit, db, actor.Id, actor.Role, actor.Email);

    public static PlatformController Create(
        AuthHardeningTestKit kit, ZayraDbContext db, Guid? actingAs, string role, string? email = null)
    {
        var jwt = AuthHardeningTestKit.Jwt;
        var hasher = new Pbkdf2PasswordHasher();
        var tokens = new JwtTokenService(jwt);
        var config = new ConfigurationBuilder().Build();
        var controller = new PlatformController(
            db, jwt, hasher, new FakeAuthSeeder(db, hasher), tokens, new FakePlatformEmailService(), config,
            kit.Mfa(db),
            new AccessManagementService(db, hasher, new AuditService(db), tokens, config),
            NullLogger<PlatformController>.Instance,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var claims = new List<Claim>
        {
            new("is_platform_admin", "true"), new("platform_role", role),
        };
        if (actingAs is { } id) claims.Add(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
        if (email is not null) claims.Add(new Claim(JwtRegisteredClaimNames.Email, email));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
        return controller;
    }

    public static int? Status(IActionResult result)
        => (result as ObjectResult)?.StatusCode ?? (result as StatusCodeResult)?.StatusCode;

    public static JsonElement Body(IActionResult result)
        => JsonSerializer.SerializeToElement(((ObjectResult)result).Value);

    public static async Task<List<AdminAuditLog>> AuditRowsAsync(AuthHardeningTestKit kit, string action)
    {
        await using var db = kit.NewDb();
        return await db.AdminAuditLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.Action == action).ToListAsync();
    }
}
