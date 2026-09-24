using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// GET /api/tenant-admin/localization must never invent a US timezone for a tenant that has not
/// stated one.
///
/// <para>THE DEFECT. The HR Command Center header — the FIRST screen a customer sees — renders its
/// clock in the zone this endpoint returns. For a tenant with no <c>TenantLocalizationSetting</c>
/// row (every tenant created before provisioning started writing one) the endpoint answered with a
/// bare <c>new TenantLocalizationSetting()</c>, whose <c>DefaultTimezone</c> property default is
/// <b>America/New_York</b> (Models/SaasPlatform.cs). Observed live at 06:28 Riyadh on Thu 24 Sept
/// 2026, the header read "Wed, 23 Sept 2026, 23:28" — seven hours behind and the WRONG DAY — while
/// the Saudi Compliance panel on the same page, which formats in the viewer's own zone, read
/// 24/09/2026 06:29. Two clocks on one screen, disagreeing by seven hours.</para>
///
/// <para>The lesson is the one <see cref="TenantTimeZoneFromCountryTests"/> already records, at the
/// other end: a property default is a decision, and a READ path that materialises the entity ships
/// that decision to the customer just as surely as a write does.</para>
/// </summary>
public class LocalizationFallbackZoneTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static TenantAdminController Ctrl(ZayraDbContext db, Guid? tenantId)
    {
        var claims = tenantId is { } id
            ? new[] { new Claim("tenant_id", id.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }
            : Array.Empty<Claim>();
        return new TenantAdminController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, tenantId is null ? null : "test")),
                },
            },
        };
    }

    private static async Task<TenantLocalizationSetting> Get(ZayraDbContext db, Guid? tenantId, string? slug = null)
    {
        var result = await Ctrl(db, tenantId).GetLocalization(slug, CancellationToken.None);
        return result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<TenantLocalizationSetting>().Subject;
    }

    private static Guid SeedTenant(ZayraDbContext db, string? companyCountry)
    {
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Evostel", Slug = $"evostel-{tid:N}" });
        if (companyCountry is not null)
            db.Companies.Add(new Company { TenantId = tid, LegalNameEn = "Evostel Arabia", CountryCode = companyCountry });
        db.SaveChanges();
        return tid;
    }

    /// <summary>The regression itself: no row, a Saudi company → Riyadh, and never US Eastern.</summary>
    [Theory]
    [InlineData("SA", "Asia/Riyadh")]
    [InlineData("SAU", "Asia/Riyadh")]
    [InlineData("AE", "Asia/Dubai")]
    [InlineData("QA", "Asia/Qatar")]
    public async Task WithNoLocalizationRow_TheZoneFollowsTheTenantsOwnCompany(string country, string expected)
    {
        using var db = CreateDb();
        var tid = SeedTenant(db, country);

        var loc = await Get(db, tid);

        loc.DefaultTimezone.Should().Be(expected);
        loc.DefaultTimezone.Should().NotBe("America/New_York", "a GCC tenant's first screen must not be told it is in New York");
    }

    /// <summary>
    /// Nothing to resolve from → EMPTY, not a guess and not the entity's US default. Empty is the
    /// only honest answer, and it is the one the client can act on: it renders in the VIEWER's own
    /// browser zone, which is never silently wrong by a day.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Atlantis")]
    public async Task WithNoCountryStatedAnywhere_TheZoneIsEmpty_NotAGuessAndNotUsEastern(string? country)
    {
        using var db = CreateDb();
        var tid = SeedTenant(db, country);

        var loc = await Get(db, tid);

        loc.DefaultTimezone.Should().BeEmpty();
        loc.DefaultTimezone.Should().NotBe("America/New_York");
    }

    /// <summary>The pre-auth load (no tenant resolvable at all) must not seed a US zone either.</summary>
    [Fact]
    public async Task WithNoTenantAtAll_TheZoneIsEmpty()
    {
        using var db = CreateDb();

        var loc = await Get(db, tenantId: null);

        loc.DefaultTimezone.Should().BeEmpty();
    }

    /// <summary>A tenant that HAS stated a zone still wins — the fallback must not override it.</summary>
    [Fact]
    public async Task AStatedZoneIsReturnedUnchanged()
    {
        using var db = CreateDb();
        var tid = SeedTenant(db, "SA");
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tid, DefaultTimezone = "Asia/Dubai" });
        db.SaveChanges();

        var loc = await Get(db, tid);

        loc.DefaultTimezone.Should().Be("Asia/Dubai", "the stated setting outranks anything inferred from the company");
    }

    /// <summary>
    /// The fallback is a READ. It must not persist a row, or the inferred value would harden into a
    /// stated setting that nobody chose and Setup would show as though an administrator had.
    /// </summary>
    [Fact]
    public async Task TheFallbackIsNotPersisted()
    {
        using var db = CreateDb();
        var tid = SeedTenant(db, "SA");

        await Get(db, tid);

        db.TenantLocalizationSettings.IgnoreQueryFilters().Should().BeEmpty();
    }
}
