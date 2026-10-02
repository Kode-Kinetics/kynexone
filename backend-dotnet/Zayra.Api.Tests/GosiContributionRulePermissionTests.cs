using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// DELETE /api/gosi/contribution-rules/{id} required <c>payroll.manage</c>, a key that has never been in
/// the permission catalog — so no role bundle, no Admin backfill and no per-user override could ever grant
/// it, and every caller got 403. A tenant that created a GOSI rate override could never retire it.
///
/// <para>The caller's permissions here are the REAL seeded role bundles (AuthSeeder.EnsureTenantRolesAsync),
/// not hand-picked claims, so the test proves what a live Admin and a live Payroll Manager can do.</para>
/// </summary>
public class GosiContributionRulePermissionTests
{
    [Fact]
    public async Task Admin_CanDeactivateATenantGosiRateOverride()
    {
        var (db, tenantId) = await SeedTenantAsync();
        var ruleId = await AddTenantOverrideAsync(db, tenantId);

        var result = await Controller(db, tenantId, await RolePermissionsAsync(db, tenantId, "Admin"))
            .DeactivateContributionRule(ruleId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await db.GosiContributionRules.AsNoTracking().SingleAsync(r => r.Id == ruleId)).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task PayrollManager_WithoutTheStatutoryOverridePermission_IsForbidden()
    {
        var (db, tenantId) = await SeedTenantAsync();
        var ruleId = await AddTenantOverrideAsync(db, tenantId);
        var perms = await RolePermissionsAsync(db, tenantId, "Payroll Manager");
        perms.Should().Contain("payroll.write").And.NotContain("payroll.rates.statutory_override",
            "the bundle deliberately withholds the higher-trust statutory key (AuthSeeder, Level 4)");

        var result = await Controller(db, tenantId, perms).DeactivateContributionRule(ruleId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await db.GosiContributionRules.AsNoTracking().SingleAsync(r => r.Id == ruleId)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAndDeactivate_RequireTheSamePermission()
    {
        // Symmetry: whoever may put a statutory override in force may take it out again, and no one else.
        var (db, tenantId) = await SeedTenantAsync();
        var ruleId = await AddTenantOverrideAsync(db, tenantId);

        var onlyOverride = new[] { "payroll.rates.statutory_override" };
        (await Controller(db, tenantId, onlyOverride).DeactivateContributionRule(ruleId, CancellationToken.None))
            .Should().BeOfType<NoContentResult>();
    }

    private static async Task<(ZayraDbContext Db, Guid TenantId)> SeedTenantAsync()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase("gosi-rule-perm-" + Guid.NewGuid().ToString("N")).Options;
        var db = new ZayraDbContext(options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "Gosi Perm", Slug = $"gp-{Guid.NewGuid():N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, CancellationToken.None);
        return (db, tenantId);
    }

    private static async Task<Guid> AddTenantOverrideAsync(ZayraDbContext db, Guid tenantId)
    {
        var rule = new GosiContributionRule
        {
            TenantId = tenantId, CountryCode = "SA", Classification = "Saudi", Branch = "Annuities", Payer = "Employee",
            Rate = 0.09m, EffectiveFrom = new DateOnly(2024, 1, 1), IsActive = true, SourceReference = "test override",
        };
        db.GosiContributionRules.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    }

    private static async Task<string[]> RolePermissionsAsync(ZayraDbContext db, Guid tenantId, string role) =>
        (await db.Roles.Where(r => r.TenantId == tenantId && r.Name == role)
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key))
            .ToListAsync()).ToArray();

    private static GosiController Controller(ZayraDbContext db, Guid tenantId, IEnumerable<string> permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new GosiController(db, new GosiReconciliationService(db, null!), null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }
}
