using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Phase 1A P0: platform-admin impersonation tokens previously carried ONLY role claims —
/// no permission claims (HasPermission() always false) and no entity_access claims, which
/// the company query filter interpreted as group scope: an impersonated session saw EVERY
/// company in the tenant regardless of the user's actual grants.
///
/// The token now mirrors a real login (permissions + entity_access + is_group_scope) and
/// carries entity_scope_strict, so a user with no explicit grants fails CLOSED for
/// company-assigned data instead of falling open to tenant-wide access.
/// </summary>
public class ImpersonationScopeTests : PlatformTestBase
{
    // ── C1: selected-company user — token claims + sibling-company denial ──────

    [Fact]
    public async Task Impersonate_UserGrantedCompanyA_CannotSeeCompanyB()
    {
        var (dbName, tenantId, companyA, companyB, _) = await SeedTenantWithTwoCompanies();

        await using var db = NewDb(dbName);
        var user = await SeedTenantUser(db, tenantId, grants: new[] { companyA });
        var controller = CreateController(db);

        var jwt = await ImpersonateAndParse(controller, tenantId, user.Id);

        // Token claims: permissions restored, entity_access restricted to Company A only.
        jwt.Claims.Should().Contain(c => c.Type == "permission" && c.Value == "employees.read");
        var entityAccess = jwt.Claims.Where(c => c.Type == "entity_access").ToList();
        entityAccess.Should().HaveCount(1, "only the explicitly granted company may appear");
        entityAccess[0].Value.Should().Contain(companyA.ToString());
        entityAccess[0].Value.Should().NotContain(companyB.ToString());
        jwt.Claims.Should().Contain(c => c.Type == EntityScopeContext.StrictScopeClaim && c.Value == "true");
        jwt.Claims.Should().Contain(c => c.Type == "impersonated_by");
        jwt.Claims.Should().NotContain(c => c.Type == "is_group_scope");

        // Scope resolution: A allowed, sibling B denied.
        var scope = EntityScopeContext.FromClaims(ToPrincipal(jwt));
        scope.IsGroupLevel.Should().BeFalse();
        scope.CanAccessCompany(companyA).Should().BeTrue();
        scope.CanAccessCompany(companyB).Should().BeFalse("cross-company access requires an explicit grant");

        // Data layer: the company query filter must hide Company B's employees.
        var visible = await QueryEmployeeCodesAs(dbName, jwt);
        visible.Should().Contain("EMP-A");
        visible.Should().NotContain("EMP-B", "impersonated Company A admin must not read Company B data");
    }

    // ── C2: no grants — fail closed, never tenant-wide ─────────────────────────

    [Fact]
    public async Task Impersonate_UserWithNoEntityGrants_FailsClosedForCompanyScopedData()
    {
        var (dbName, tenantId, companyA, companyB, _) = await SeedTenantWithTwoCompanies();

        await using var db = NewDb(dbName);
        var user = await SeedTenantUser(db, tenantId, grants: Array.Empty<Guid>());
        var controller = CreateController(db);

        var jwt = await ImpersonateAndParse(controller, tenantId, user.Id);

        jwt.Claims.Should().NotContain(c => c.Type == "entity_access");
        jwt.Claims.Should().Contain(c => c.Type == EntityScopeContext.StrictScopeClaim && c.Value == "true");

        // Absence of grants must resolve to default-deny, NOT group level.
        var scope = EntityScopeContext.FromClaims(ToPrincipal(jwt));
        scope.IsGroupLevel.Should().BeFalse("missing claims on an impersonation token must never mean 'all companies'");
        scope.CanAccessCompany(companyA).Should().BeFalse();
        scope.CanAccessCompany(companyB).Should().BeFalse();

        var visible = await QueryEmployeeCodesAs(dbName, jwt);
        visible.Should().NotContain("EMP-A");
        visible.Should().NotContain("EMP-B");
        // Phase 1B hardening: Employee is ICompanyScopedOperational — company-unassigned
        // rows are visible to GROUP scope only, never to a fail-closed scoped session.
        visible.Should().NotContain("EMP-NULL",
            "operational null-CompanyId rows must not leak to scoped users (poison-default prevention)");
        visible.Should().BeEmpty();
    }

    // ── C3: explicit group scope — represented, not inferred ───────────────────

    [Fact]
    public async Task Impersonate_GroupScopeUser_TokenCarriesExplicitGroupClaim_SeesAllCompanies()
    {
        var (dbName, tenantId, companyA, companyB, _) = await SeedTenantWithTwoCompanies();

        await using var db = NewDb(dbName);
        var user = await SeedTenantUser(db, tenantId, grants: Array.Empty<Guid>(), isGroupScope: true);
        var controller = CreateController(db);

        var jwt = await ImpersonateAndParse(controller, tenantId, user.Id);

        jwt.Claims.Should().Contain(c => c.Type == "is_group_scope" && c.Value == "true",
            "group access must be an explicit claim, never inferred from claim absence");

        var scope = EntityScopeContext.FromClaims(ToPrincipal(jwt));
        scope.IsGroupLevel.Should().BeTrue();
        scope.CanAccessCompany(companyA).Should().BeTrue();
        scope.CanAccessCompany(companyB).Should().BeTrue();

        var visible = await QueryEmployeeCodesAs(dbName, jwt);
        visible.Should().Contain(new[] { "EMP-A", "EMP-B" });
        visible.Should().Contain("EMP-NULL", "group scope still sees unassigned rows so they stay repairable");
    }

    // ── C4: selected-companies subset — sees exactly the granted set ───────────

    [Fact]
    public async Task Impersonate_SelectedCompaniesGrant_SeesOnlySelectedCompanies()
    {
        var (dbName, tenantId, companyA, companyB, companyC) = await SeedTenantWithTwoCompanies(withThirdCompany: true);

        await using var db = NewDb(dbName);
        var user = await SeedTenantUser(db, tenantId, grants: new[] { companyA, companyB });
        var controller = CreateController(db);

        var jwt = await ImpersonateAndParse(controller, tenantId, user.Id);
        var scope = EntityScopeContext.FromClaims(ToPrincipal(jwt));

        scope.CanAccessCompany(companyA).Should().BeTrue();
        scope.CanAccessCompany(companyB).Should().BeTrue();
        scope.CanAccessCompany(companyC).Should().BeFalse();

        var visible = await QueryEmployeeCodesAs(dbName, jwt);
        visible.Should().Contain(new[] { "EMP-A", "EMP-B" });
        visible.Should().NotContain("EMP-C");
    }

    // ── C5: break-glass support access — same rules as Impersonate ─────────────

    [Fact]
    public async Task SupportAccess_UserGrantedCompanyA_TokenScopedAndCannotSeeCompanyB()
    {
        var (dbName, tenantId, companyA, companyB, _) = await SeedTenantWithTwoCompanies();

        await using var db = NewDb(dbName);
        var user = await SeedTenantUser(db, tenantId, grants: new[] { companyA });
        var controller = CreateController(db);

        var result = await controller.StartSupportAccess(
            new StartSupportAccessRequest(tenantId.ToString(), user.Id.ToString(), "Customer ticket #123"),
            CancellationToken.None);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var tokenString = (string)ok.Value!.GetType().GetProperty("token")!.GetValue(ok.Value)!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokenString);

        jwt.Claims.Should().Contain(c => c.Type == "permission" && c.Value == "employees.read");
        jwt.Claims.Should().Contain(c => c.Type == EntityScopeContext.StrictScopeClaim && c.Value == "true");

        var scope = EntityScopeContext.FromClaims(ToPrincipal(jwt));
        scope.CanAccessCompany(companyA).Should().BeTrue();
        scope.CanAccessCompany(companyB).Should().BeFalse("break-glass sessions must honour company grants");

        var visible = await QueryEmployeeCodesAs(dbName, jwt);
        visible.Should().Contain("EMP-A");
        visible.Should().NotContain("EMP-B");
    }

    // ── Strict-marker unit behavior (targeted fail-closed, D) ──────────────────

    [Fact]
    public void EntityScopeContext_StrictMarker_NoClaims_DeniesInsteadOfGroupFallback()
    {
        var principal = Principal(("tenant_id", Guid.NewGuid().ToString()),
                                  (EntityScopeContext.StrictScopeClaim, "true"));

        var scope = EntityScopeContext.FromClaims(principal, strictMode: false);

        scope.IsGroupLevel.Should().BeFalse();
        scope.AccessibleCompanyIds.Should().BeEmpty();
        scope.CanAccessCompany(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void EntityScopeContext_StrictMarker_MalformedEntityAccess_FailsClosed()
    {
        var principal = Principal(("tenant_id", Guid.NewGuid().ToString()),
                                  (EntityScopeContext.StrictScopeClaim, "true"),
                                  ("entity_access", "{not-valid-json"));

        var scope = EntityScopeContext.FromClaims(principal, strictMode: false);

        scope.IsGroupLevel.Should().BeFalse("malformed grants must never widen access");
        scope.AccessibleCompanyIds.Should().BeEmpty();
    }

    [Fact]
    public void EntityScopeContext_LegacyTokenWithoutStrictMarker_KeepsBackwardCompatGroupFallback()
    {
        // Regression guard: normal (non-impersonation) sessions are unchanged until the
        // global StrictMode cutover — absence of claims still resolves to group level.
        var principal = Principal(("tenant_id", Guid.NewGuid().ToString()));

        EntityScopeContext.FromClaims(principal, strictMode: false)
            .IsGroupLevel.Should().BeTrue();
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private static ZayraDbContext NewDb(string dbName, IHttpContextAccessor? accessor = null)
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new ZayraDbContext(options, accessor);
    }

    private static async Task<(string DbName, Guid TenantId, Guid CompanyA, Guid CompanyB, Guid CompanyC)>
        SeedTenantWithTwoCompanies(bool withThirdCompany = false)
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = NewDb(dbName);

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Group Tenant", Slug = $"grp-{Guid.NewGuid():N}" });

        var companyA = new Company { TenantId = tenantId, LegalNameEn = "Alpha Trading LLC" };
        var companyB = new Company { TenantId = tenantId, LegalNameEn = "Beta Industries LLC" };
        db.Companies.AddRange(companyA, companyB);
        db.Employees.Add(MakeEmployee(tenantId, "EMP-A", companyA.Id));
        db.Employees.Add(MakeEmployee(tenantId, "EMP-B", companyB.Id));
        db.Employees.Add(MakeEmployee(tenantId, "EMP-NULL", null));

        var companyCId = Guid.Empty;
        if (withThirdCompany)
        {
            var companyC = new Company { TenantId = tenantId, LegalNameEn = "Gamma Holdings LLC" };
            db.Companies.Add(companyC);
            db.Employees.Add(MakeEmployee(tenantId, "EMP-C", companyC.Id));
            companyCId = companyC.Id;
        }

        await db.SaveChangesAsync();
        return (dbName, tenantId, companyA.Id, companyB.Id, companyCId);
    }

    private static Employee MakeEmployee(Guid tenantId, string code, Guid? companyId) => new()
    {
        TenantId = tenantId,
        CompanyId = companyId,
        EmployeeCode = code,
        FullName = $"Employee {code}",
        Status = "Active",
        JoiningDate = DateTime.UtcNow.AddYears(-1)
    };

    private static async Task<User> SeedTenantUser(ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> grants, bool isGroupScope = false)
    {
        var permission = new Permission { Key = "employees.read", Module = "Employees" };
        var role = new Role { TenantId = tenantId, Name = "Company Admin", NormalizedName = "COMPANY ADMIN" };
        db.Permissions.Add(permission);
        db.Roles.Add(role);
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });

        var user = new User
        {
            TenantId = tenantId,
            Email = $"admin-{Guid.NewGuid():N}@corp.local",
            NormalizedEmail = $"ADMIN-{Guid.NewGuid():N}@CORP.LOCAL",
            FullName = "Scoped Admin",
            IsGroupScope = isGroupScope
        };
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        foreach (var companyId in grants)
            db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = tenantId, UserId = user.Id, CompanyId = companyId, Role = "CompanyAdmin", IsActive = true });

        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<JwtSecurityToken> ImpersonateAndParse(PlatformController controller, Guid tenantId, Guid userId)
    {
        var result = await controller.Impersonate(tenantId, new ImpersonateRequest(userId), CancellationToken.None);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var tokenString = (string)ok.Value!.GetType().GetProperty("token")!.GetValue(ok.Value)!;
        return new JwtSecurityTokenHandler().ReadJwtToken(tokenString);
    }

    private static ClaimsPrincipal ToPrincipal(JwtSecurityToken jwt) =>
        new(new ClaimsIdentity(jwt.Claims, "Test"));

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Test"));

    /// <summary>Queries Employees through the real EF global filters with the impersonated principal.</summary>
    private static async Task<List<string>> QueryEmployeeCodesAs(string dbName, JwtSecurityToken jwt)
    {
        var accessor = new FixedHttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = ToPrincipal(jwt) }
        };
        await using var db = NewDb(dbName, accessor);
        return await db.Employees.Select(e => e.EmployeeCode).ToListAsync();
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
