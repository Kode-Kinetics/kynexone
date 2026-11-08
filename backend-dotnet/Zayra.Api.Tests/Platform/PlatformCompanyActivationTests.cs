using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// Platform console, activating a company that is stuck in Draft: the email domain is re-checked at activation (a Draft can be
/// older than another organisation's claim to the domain), the record names the acting platform admin, and the tenant's cached
/// sign-in verdicts are dropped so the new company set takes effect on the next request.
/// </summary>
public sealed class PlatformCompanyActivationTests : PlatformTestBase
{
    private static (ZayraDbContext Db, MemorySessionVerdictCache Cache) NewDb()
    {
        var cache = new MemorySessionVerdictCache(new MemoryCache(new MemoryCacheOptions()), Options.Create(new SessionVerdictCacheOptions { TtlSeconds = 30 }));
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new SessionVerdictInvalidationInterceptor(cache))
            .Options);
        return (db, cache);
    }

    private static Tenant NewTenant(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = $"t-{Guid.NewGuid():N}"[..14] };
        db.Tenants.Add(tenant);
        return tenant;
    }

    private static Company NewCompany(Guid tenantId, string name, string domain, bool draft) => new()
    {
        TenantId = tenantId, LegalNameEn = name, RegistrationNumber = $"R-{Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR",
        EmailDomain = domain, IsActive = !draft, ApprovalStatus = draft ? CompanyApprovalStatuses.Draft : CompanyApprovalStatuses.Active,
    };

    [Fact]
    public async Task Activation_IsRefused_WhenAnotherOrganisationNowUsesTheDraftsEmailDomain_AndTheCompanyStaysDraft()
    {
        var (db, _) = NewDb();
        var tenant = NewTenant(db);
        var other = NewTenant(db);
        var draft = NewCompany(tenant.Id, "Old Draft", "acme.sa", draft: true);
        db.Companies.AddRange(draft, NewCompany(other.Id, "Acme Holdings", "acme.sa", draft: false));
        await db.SaveChangesAsync();

        var result = await CreateController(db).ApproveCompany(tenant.Id, draft.Id, CancellationToken.None);

        var refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(409);
        System.Text.Json.JsonSerializer.Serialize(refused.Value).Should().Contain("email_domain_claimed");
        db.ChangeTracker.Clear();
        var company = await db.Companies.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == draft.Id);
        company.IsActive.Should().BeFalse();
        company.ApprovalStatus.Should().Be(CompanyApprovalStatuses.Draft);
        (await db.AdminAuditLogs.IgnoreQueryFilters().AnyAsync(a => a.Action == "CompanyActivatedByPlatform")).Should().BeFalse();
    }

    [Fact]
    public async Task Activation_IsAllowed_WhenTheDomainIsOnlyHeldByTheSameTenant_OrNoOneElse()
    {
        var (db, _) = NewDb();
        var tenant = NewTenant(db);
        var ownSister = NewCompany(tenant.Id, "Sister", "acme.sa", draft: false);
        var draft = NewCompany(tenant.Id, "Old Draft", "acme.sa", draft: true);
        var noDomain = NewCompany(tenant.Id, "No Domain", "", draft: true);
        db.Companies.AddRange(ownSister, draft, noDomain);
        await db.SaveChangesAsync();
        var platform = CreateController(db);

        (await platform.ApproveCompany(tenant.Id, draft.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await platform.ApproveCompany(tenant.Id, noDomain.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Activation_RecordsWhichPlatformAdminDidIt()
    {
        var (db, _) = NewDb();
        var tenant = NewTenant(db);
        var draft = NewCompany(tenant.Id, "Old Draft", "", draft: true);
        db.Companies.Add(draft);
        await db.SaveChangesAsync();
        var adminId = Guid.NewGuid();
        var platform = CreateController(db);
        platform.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [.. platform.ControllerContext.HttpContext.User.Claims, new Claim(ClaimTypes.NameIdentifier, adminId.ToString())], "Test"));

        (await platform.ApproveCompany(tenant.Id, draft.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        db.ChangeTracker.Clear();
        var row = await db.AdminAuditLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Action == "CompanyActivatedByPlatform");
        row.TenantId.Should().Be(tenant.Id);
        row.EntityType.Should().Be(nameof(Company));
        row.EntityId.Should().Be(draft.Id.ToString());
        row.PerformedByName.Should().Be(AdminEmail);
        (row.OldValuesJson + row.NewValuesJson).Should().Contain(adminId.ToString(), "the record names the acting platform admin by id");
        row.NewValuesJson.Should().Contain(draft.Id.ToString());
    }

    [Fact]
    public async Task Activation_DropsTheTenantsCachedSignInVerdicts_ButNotAnotherTenants()
    {
        var (db, cache) = NewDb();
        var tenant = NewTenant(db);
        var elsewhere = NewTenant(db);
        var draft = NewCompany(tenant.Id, "Old Draft", "", draft: true);
        db.Companies.Add(draft);
        await db.SaveChangesAsync();
        var tenantUser = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var mine = cache.KeyFor(tenant.Id, tenantUser, "fingerprint");
        var theirs = cache.KeyFor(elsewhere.Id, otherUser, "fingerprint");
        cache.RememberCurrent(mine);
        cache.RememberCurrent(theirs);
        cache.IsKnownCurrent(cache.KeyFor(tenant.Id, tenantUser, "fingerprint")).Should().BeTrue("a verdict cached before the activation is served until something changes");

        (await CreateController(db).ApproveCompany(tenant.Id, draft.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        cache.IsKnownCurrent(cache.KeyFor(tenant.Id, tenantUser, "fingerprint")).Should().BeFalse("a session checked before the company set changed must be checked again");
        cache.IsKnownCurrent(cache.KeyFor(elsewhere.Id, otherUser, "fingerprint")).Should().BeTrue("another tenant's sessions are untouched");
    }
}
