using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// A tenant provisioned exactly as production provisions one (<see cref="AuthSeeder.EnsureTenantRolesAsync"/>),
/// and callers whose claims are a seeded role's REAL bundle — so a test proves what a live Admin, HR Director
/// or Finance Approver can do, not what a hand-picked claim list can do.
/// </summary>
internal static class SeededRoleBundles
{
    public static async Task<(ZayraDbContext Db, Guid TenantId)> NewTenantAsync(string label)
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase($"{label}-{Guid.NewGuid():N}").Options;
        var db = new ZayraDbContext(options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{Guid.NewGuid():N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, CancellationToken.None);
        return (db, tenantId);
    }

    /// <summary>The permission keys the tenant's stored role holds, read back from role_permissions.</summary>
    public static async Task<string[]> PermissionsOfAsync(ZayraDbContext db, Guid tenantId, string role) =>
        (await db.Roles.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Name == role)
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key))
            .ToListAsync()).ToArray();

    /// <summary>What a tenant administrator does in the Roles &amp; Permissions matrix: add one catalog key to a stored role.</summary>
    public static async Task GrantAsync(ZayraDbContext db, Guid tenantId, string role, string permissionKey)
    {
        var roleRow = await db.Roles.SingleAsync(r => r.TenantId == tenantId && r.Name == role);
        var permission = await db.Permissions.SingleAsync(p => p.Key == permissionKey);
        db.RolePermissions.Add(new RolePermission { RoleId = roleRow.Id, PermissionId = permission.Id });
        await db.SaveChangesAsync();
    }

    /// <summary>A group-scoped caller carrying the role name and exactly the role's stored permissions.</summary>
    public static async Task<ClaimsPrincipal> CallerAsync(ZayraDbContext db, Guid tenantId, string role)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, $"{role} caller"),
            new("FullName", $"{role} caller"),
            new(ClaimTypes.Role, role),
            new("is_group_scope", "true"),
        };
        claims.AddRange((await PermissionsOfAsync(db, tenantId, role)).Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    public static T Bind<T>(T controller, ClaimsPrincipal caller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = caller } };
        return controller;
    }

    /// <summary>The HTTP status an action result would produce (a bare ForbidResult is the 403).</summary>
    public static int StatusOf(IActionResult result) => result switch
    {
        ForbidResult => StatusCodes.Status403Forbidden,
        ObjectResult o => o.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult s => s.StatusCode,
        _ => throw new InvalidOperationException($"Unmapped action result {result.GetType().Name}"),
    };
}
