using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// DEFECT (P0): a user created through the platform admin could see nothing.
///
/// <para><c>POST /api/platform/tenants/{id}/users</c> had no scope parameter at all and set
/// <c>IsGroupScope</c> only for the Admin role, so an HR Manager or a payroll approver it created
/// resolved to ZERO accessible companies. Every company-owned row was then filtered out of that
/// account's queries and the symptom surfaced as a flat 404 from
/// <c>/api/payroll/runs/{id}/approve</c> for a run that plainly existed — which reads as "no such
/// run", not as "this account has no company access". The user was invisible to itself.</para>
///
/// <para>Every test asserts the scope the RUNTIME resolves, through the same
/// <see cref="EntityScopeClaims"/> resolve-and-emit pair that normal login, impersonation and
/// break-glass tokens all use, and then reads it back through
/// <see cref="EntityScopeContext.FromClaims"/> in STRICT mode. Asserting that a grant row was
/// written would prove nothing: the question is whether the account can see a company.</para>
///
/// <para>Real Postgres: which companies an <c>AllCurrentCompanies</c> grant snapshots is a database
/// query over the tenant's active legal entities, which is the half of this that is data-shaped.</para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class PlatformCreatedUserEntityScopeTests : PlatformTestBase
{
    private readonly PostgresFixture _fx;
    public PlatformCreatedUserEntityScopeTests(PostgresFixture fx) => _fx = fx;

    private sealed record Fixture(Guid TenantId, Guid CompanyA, Guid CompanyB);

    private static async Task<Fixture> SeedTenantWithTwoCompanies(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Scope Test", Slug = $"scope-{Guid.NewGuid():N}" });
        foreach (var (name, normalized) in new[]
                 {
                     ("Admin", "ADMIN"), ("HR Manager", "HR MANAGER"), ("Payroll Approver", "PAYROLL APPROVER"),
                 })
        {
            db.Roles.Add(new Role
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Name = name, NormalizedName = normalized, Description = name,
            });
        }
        var a = new Company { TenantId = tenantId, LegalNameEn = "Dairy Co", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        var b = new Company { TenantId = tenantId, LegalNameEn = "Bakery Co", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        return new Fixture(tenantId, a.Id, b.Id);
    }

    private static CreateTenantUserRequest Request(
        string role, string? entityScope = null, IReadOnlyCollection<Guid>? companyIds = null) =>
        new($"{Guid.NewGuid():N}@example.test", "Provisioned User", "correct-horse-battery", role, null, entityScope, companyIds);

    /// <summary>The scope the runtime will actually see, resolved and emitted exactly as login does
    /// and parsed back in STRICT (fail-closed) mode.</summary>
    private static async Task<EntityScopeContext> RuntimeScopeOf(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.EntityAccesses)
            .SingleAsync(u => u.Id == userId);
        var grants = user.EntityAccesses.Where(e => e.IsActive)
            .Select(e => new EntityAccessGrant(e.CompanyId, e.Role, e.GrantMode)).ToList();
        IReadOnlyCollection<Guid> activeCompanyIds = await db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted).Select(c => c.Id).ToListAsync();
        var claims = EntityScopeClaims.Build(
            EntityScopeClaims.Resolve(user.IsGroupScope, grants, activeCompanyIds), grants);
        return EntityScopeContext.FromClaims(
            new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), strictMode: true);
    }

    private static Guid CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var idProperty = ok.Value!.GetType().GetProperty("Id");
        idProperty.Should().NotBeNull();
        return (Guid)idProperty!.GetValue(ok.Value)!;
    }

    // ── The defect ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("HR Manager")]
    [InlineData("Payroll Approver")]
    public async Task NonAdminRole_WithNoScopeAsked_CanStillSeeTheTenantsCompanies(string role)
    {
        await using var db = _fx.CreateDb();
        var fx = await SeedTenantWithTwoCompanies(db);

        var userId = CreatedId(await CreateController(db).CreateTenantUser(fx.TenantId, Request(role), default));

        var scope = await RuntimeScopeOf(db, fx.TenantId, userId);
        scope.CanAccessCompany(fx.CompanyA).Should().BeTrue(
            "before the fix this account resolved to zero companies and every company-owned row "
            + "vanished from its queries — the payroll approver's 404 on a run that existed");
        scope.CanAccessCompany(fx.CompanyB).Should().BeTrue();
    }

    [Fact]
    public async Task AdminRole_IsStillGroupScoped()
    {
        await using var db = _fx.CreateDb();
        var fx = await SeedTenantWithTwoCompanies(db);

        var userId = CreatedId(await CreateController(db).CreateTenantUser(fx.TenantId, Request("Admin"), default));

        (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).IsGroupScope.Should().BeTrue();
        var scope = await RuntimeScopeOf(db, fx.TenantId, userId);
        scope.IsGroupLevel.Should().BeTrue("the Admin default is unchanged — group scope follows companies added later");
    }

    // ── The explicit scope parameter ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExplicitCompanyScope_ConfinesTheAccountToThoseCompaniesOnly()
    {
        await using var db = _fx.CreateDb();
        var fx = await SeedTenantWithTwoCompanies(db);

        var userId = CreatedId(await CreateController(db).CreateTenantUser(
            fx.TenantId, Request("HR Manager", "companies", new[] { fx.CompanyA }), default));

        var scope = await RuntimeScopeOf(db, fx.TenantId, userId);
        scope.IsGroupLevel.Should().BeFalse();
        scope.CanAccessCompany(fx.CompanyA).Should().BeTrue();
        scope.CanAccessCompany(fx.CompanyB).Should().BeFalse(
            "a confined account must stay confined — otherwise every cross-company isolation assertion is vacuous");
    }

    [Fact]
    public async Task ExplicitGroupScope_FollowsCompaniesAddedLater()
    {
        await using var db = _fx.CreateDb();
        var fx = await SeedTenantWithTwoCompanies(db);

        var userId = CreatedId(await CreateController(db).CreateTenantUser(
            fx.TenantId, Request("HR Manager", "group"), default));

        var later = new Company { TenantId = fx.TenantId, LegalNameEn = "Later Co", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.Add(later);
        await db.SaveChangesAsync();

        (await RuntimeScopeOf(db, fx.TenantId, userId)).CanAccessCompany(later.Id).Should().BeTrue();
    }

    // ── Fail closed, loudly, at the operator ──────────────────────────────────────────────────

    [Fact]
    public async Task TenantWithNoCompanies_RefusesACompanyScopedAccount_RatherThanCreatingABlindOne()
    {
        await using var db = _fx.CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Empty", Slug = $"empty-{Guid.NewGuid():N}" });
        db.Roles.Add(new Role { Id = Guid.NewGuid(), TenantId = tenantId, Name = "HR Manager", NormalizedName = "HR MANAGER", Description = "HR" });
        await db.SaveChangesAsync();

        var result = await CreateController(db).CreateTenantUser(tenantId, Request("HR Manager"), default);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        System.Text.Json.JsonSerializer.Serialize(conflict.Value).Should().Contain("tenant_has_no_companies");
        (await db.Users.CountAsync(u => u.TenantId == tenantId)).Should().Be(0, "the refusal must not half-apply");
    }

    [Fact]
    public async Task CompanyScopeWithNoCompanyIds_IsRefused()
    {
        await using var db = _fx.CreateDb();
        var fx = await SeedTenantWithTwoCompanies(db);

        var result = await CreateController(db).CreateTenantUser(
            fx.TenantId, Request("HR Manager", "companies"), default);

        System.Text.Json.JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(result).Value)
            .Should().Contain("company_ids_required");
    }

    [Fact]
    public async Task CompanyIdFromAnotherTenant_IsRefused()
    {
        await using var db = _fx.CreateDb();
        var fx = await SeedTenantWithTwoCompanies(db);
        var other = await SeedTenantWithTwoCompanies(db);

        var result = await CreateController(db).CreateTenantUser(
            fx.TenantId, Request("HR Manager", "companies", new[] { other.CompanyA }), default);

        System.Text.Json.JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(result).Value)
            .Should().Contain("unknown_company");
        (await db.UserEntityAccesses.CountAsync(g => g.CompanyId == other.CompanyA)).Should().Be(0);
    }

    [Fact]
    public async Task UnrecognisedEntityScope_IsRefusedWithTheAcceptedValues()
    {
        await using var db = _fx.CreateDb();
        var fx = await SeedTenantWithTwoCompanies(db);

        var result = await CreateController(db).CreateTenantUser(
            fx.TenantId, Request("HR Manager", "everything"), default);

        System.Text.Json.JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(result).Value)
            .Should().Contain("invalid_entity_scope").And.Contain("allCurrentCompanies");
    }
}
