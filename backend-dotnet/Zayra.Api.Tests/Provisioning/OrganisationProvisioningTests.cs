using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Boot;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Provisioning;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Provisioning;

/// <summary>
/// WP2: the installation's organisation is created once, locally, by <c>--setup-organisation</c>, through
/// <see cref="OrganisationProvisioningService"/> — the same steps, in the same order, that the retired
/// platform console ran for a new tenant. These tests pin what an organisation is born with.
/// </summary>
public class OrganisationProvisioningTests
{
    private static ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase($"org-provisioning-{Guid.NewGuid():N}")
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
        .Options);

    private static OrganisationProvisioningService Service(ZayraDbContext db) =>
        new(db, new AuthSeeder(db), new Pbkdf2PasswordHasher());

    private static OrganisationProvisioningRequest Request(string? homeCountry, string password = "A-Real-Admin-Pass-42") => new()
    {
        OrganisationName = "Gulf Trading",
        LegalName = "Gulf Trading Company LLC",
        HomeCountry = homeCountry,
        AdminEmail = "Admin@GulfTrading.Example",
        AdminFullName = "First Administrator",
        AdminPassword = password,
    };

    private static async Task<ProvisionedOrganisation> ProvisionAsync(ZayraDbContext db, string homeCountry)
    {
        var result = await Service(db).ProvisionAsync(Request(homeCountry), OrganisationProvisioningService.SetupActor, CancellationToken.None);
        result.Refusal.Should().BeNull();
        return result.Organisation!;
    }

    // ── Everything the organisation is born with ─────────────────────────────────────────────────

    [Theory]
    [InlineData("SA", "SAR", "Asia/Riyadh")]
    [InlineData("AE", "AED", "Asia/Dubai")]
    public async Task Setup_CreatesTheOrganisation_WithEveryDefault_AndTheCountrysTimeZoneAndCurrency(
        string country, string currency, string timeZone)
    {
        await using var db = CreateDb();

        var org = await ProvisionAsync(db, country);

        var tenant = await db.Tenants.SingleAsync();
        tenant.Id.Should().Be(org.OrganisationId);
        tenant.Name.Should().Be("Gulf Trading");
        tenant.CompanyCreationMode.Should().Be(CompanyCreationModes.GroupSelfServiceWithinLimit);

        var localization = await db.TenantLocalizationSettings.IgnoreQueryFilters().SingleAsync();
        localization.CountryCode.Should().Be(country);
        localization.CurrencyCode.Should().Be(currency);
        localization.DefaultTimezone.Should().Be(timeZone,
            "attendance work dates, leave and payroll periods are computed in the organisation's zone");
        HomeJurisdiction.TimeZoneFor(country).Should().Be(timeZone);

        var company = await db.Companies.IgnoreQueryFilters().SingleAsync();
        company.CountryCode.Should().Be(country, "the first company inherits the home jurisdiction");
        company.DefaultCurrency.Should().Be(currency);
        company.LegalNameEn.Should().Be("Gulf Trading Company LLC");
        company.TradeName.Should().Be("Gulf Trading");

        var admin = await db.Users.IgnoreQueryFilters().Include(u => u.UserRoles).ThenInclude(r => r.Role).SingleAsync();
        admin.Id.Should().Be(org.AdminUserId);
        admin.Email.Should().Be("admin@gulftrading.example");
        admin.AccessMode.Should().Be("FullPortal");
        admin.Status.Should().Be("Active");
        admin.IsEmailConfirmed.Should().BeTrue();
        admin.IsGroupScope.Should().BeTrue();
        admin.UserRoles.Select(r => r.Role!.Name).Should().Contain("Admin");
        new Pbkdf2PasswordHasher().Verify("A-Real-Admin-Pass-42", admin.PasswordHash).Should().BeTrue();

        (await db.Roles.IgnoreQueryFilters().CountAsync()).Should().BeGreaterThan(1, "the full standard role set is seeded");
        (await db.GlAccounts.IgnoreQueryFilters().AnyAsync()).Should().BeTrue("the chart of accounts is seeded");
        (await db.PayComponents.IgnoreQueryFilters().AnyAsync()).Should().BeTrue("the pay-component catalog is seeded");
        (await db.LeaveTypes.IgnoreQueryFilters().AnyAsync()).Should().BeTrue("leave types come from the bundle");
        (await db.LeavePolicies.IgnoreQueryFilters().AnyAsync()).Should().BeTrue();
        (await db.AttendancePolicies.IgnoreQueryFilters().AnyAsync()).Should().BeTrue();
        (await db.ApprovalWorkflows.IgnoreQueryFilters().AnyAsync()).Should().BeTrue();
        (await db.NotificationTemplates.IgnoreQueryFilters().AnyAsync()).Should().BeTrue();
        (await db.MasterDataTypes.IgnoreQueryFilters().AnyAsync()).Should().BeTrue("reference lists come from the bundle");
        (await db.CountryPayrollRules.IgnoreQueryFilters().AnyAsync(r => r.CountryCode == country)).Should().BeTrue();

        var audit = await db.AdminAuditLogs.IgnoreQueryFilters().ToListAsync();
        audit.Select(a => a.Action).Should().BeEquivalentTo(["OrganisationCreated", "AdminCreated"]);
        audit.Should().OnlyContain(a => a.PerformedByName == OrganisationProvisioningService.SetupActor);
    }

    [Fact]
    public async Task Setup_ProducesTheSameDefaults_AsTheBundleSeedsForTheCountry()
    {
        await using var viaSetup = CreateDb();
        var org = await ProvisionAsync(viaSetup, "SA");

        await using var direct = CreateDb();
        var tenant = new Tenant { Name = "Direct", Slug = "direct" };
        direct.Tenants.Add(tenant);
        await direct.SaveChangesAsync();
        await TenantProvisioningBundle.ProvisionAsync(direct, tenant.Id, "SA", CancellationToken.None);
        await direct.SaveChangesAsync();

        (await viaSetup.LeaveTypes.IgnoreQueryFilters().Select(l => l.Code).OrderBy(c => c).ToListAsync())
            .Should().Equal(await direct.LeaveTypes.IgnoreQueryFilters().Select(l => l.Code).OrderBy(c => c).ToListAsync(),
                "setup runs the provisioning bundle itself, not a copy of it");
        org.OrganisationId.Should().NotBeEmpty();
    }

    // ── One organisation per installation ────────────────────────────────────────────────────────

    [Fact]
    public async Task ASecondSetup_IsRefused_AndChangesNothing()
    {
        await using var db = CreateDb();
        await ProvisionAsync(db, "SA");
        var users = await db.Users.IgnoreQueryFilters().CountAsync();

        var second = await Service(db).ProvisionAsync(Request("AE") with { OrganisationName = "Another" },
            OrganisationProvisioningService.SetupActor, CancellationToken.None);

        second.Succeeded.Should().BeFalse();
        second.Refusal.Should().Contain("already set up");
        (await db.Tenants.CountAsync()).Should().Be(1);
        (await db.Users.IgnoreQueryFilters().CountAsync()).Should().Be(users);
    }

    [Fact]
    public async Task ADeactivatedOrganisation_StillBlocksASecondSetup()
    {
        await using var db = CreateDb();
        db.Tenants.Add(new Tenant { Name = "Old", Slug = "old", IsActive = false });
        await db.SaveChangesAsync();

        var result = await Service(db).ProvisionAsync(Request("SA"), OrganisationProvisioningService.SetupActor, CancellationToken.None);

        result.Succeeded.Should().BeFalse("a database that has ever held an organisation is not an empty installation");
    }

    // ── Input rules ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "home country is required")]
    [InlineData("", "home country is required")]
    [InlineData("   ", "home country is required")]
    [InlineData("Atlantis", "Unrecognized home country")]
    [InlineData("ZZ", "Unrecognized home country")]
    [InlineData("GB", "no country pack")]            // a real country the product has no statutory pack for
    public async Task AnUnusableHomeCountry_IsRefused_BeforeAnythingIsWritten(string? country, string because)
    {
        await using var db = CreateDb();

        var result = await Service(db).ProvisionAsync(Request(country), OrganisationProvisioningService.SetupActor, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Refusal.Should().Contain(because);
        (await db.Tenants.CountAsync()).Should().Be(0, "statutory seeding needs the jurisdiction, so nothing is provisioned without it");
        (await db.Companies.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("qa", "QA")]     // casing normalised
    [InlineData("ARE", "AE")]    // ISO-3 accepted and mapped, per CountryCodeStandard
    public async Task TheHomeCountry_IsNormalised(string stated, string expected)
    {
        await using var db = CreateDb();

        await ProvisionAsync(db, stated);

        (await db.Companies.IgnoreQueryFilters().SingleAsync()).CountryCode.Should().Be(expected);
        (await db.TenantLocalizationSettings.IgnoreQueryFilters().SingleAsync()).CountryCode.Should().Be(expected);
    }

    [Theory]
    [InlineData("short", "at least 10 characters")]
    [InlineData("ChangeMe123!", "known default")]
    [InlineData("YourPassword123!", "known default")]
    [InlineData("PlatformAdmin123!", "known default")]
    public async Task AWeakAdminPassword_IsRefused(string password, string because)
    {
        await using var db = CreateDb();

        var result = await Service(db).ProvisionAsync(Request("SA", password), OrganisationProvisioningService.SetupActor, CancellationToken.None);

        result.Refusal.Should().Contain(because);
        (await db.Tenants.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-email")]
    public async Task AnInvalidAdminEmail_IsRefused(string? email)
    {
        await using var db = CreateDb();

        var result = await Service(db).ProvisionAsync(Request("SA") with { AdminEmail = email },
            OrganisationProvisioningService.SetupActor, CancellationToken.None);

        result.Refusal.Should().Contain("admin email");
    }

    [Fact]
    public async Task TheOrganisationName_IsRequired()
    {
        await using var db = CreateDb();

        var result = await Service(db).ProvisionAsync(Request("SA") with { OrganisationName = " " },
            OrganisationProvisioningService.SetupActor, CancellationToken.None);

        result.Refusal.Should().Contain("organisation name is required");
    }

    // ── A blank-country company is surfaced, never guessed ───────────────────────────────────────

    [Fact]
    public async Task TheMissingCountryAudit_IsSilentAboutAnOrganisationCreatedBySetup()
    {
        await using var db = CreateDb();
        await ProvisionAsync(db, "AE");

        var summary = await MissingCountryAudit.RunAsync(db, NullLogger.Instance, CancellationToken.None);

        summary.Findings.Should().BeEmpty("an organisation created by setup cannot reach the blank-country state");
    }

    /// <summary>
    /// A group may hold one entity in Saudi Arabia and another in Qatar. The country is resolved PER
    /// COMPANY, so the two entities produce DIFFERENT statutory requirements for the same nationality.
    /// </summary>
    [Fact]
    public async Task AKsaOrganisation_WithAQatarCompany_ResolvesDifferentStatutoryRequirements()
    {
        await using var db = CreateDb();
        var org = await ProvisionAsync(db, "SA");
        var ksa = await db.Companies.IgnoreQueryFilters().SingleAsync();
        var qatar = new Company
        {
            TenantId = org.OrganisationId, LegalNameEn = "Gulf Trading Qatar WLL", TradeName = "Gulf Trading Qatar",
            CountryCode = "QA", IsActive = true,
        };
        db.Companies.Add(qatar);
        await db.SaveChangesAsync();

        var resolver = new EmployeeReadinessPolicyResolver(db);
        var ksaKeys = (await resolver.ResolveAsync(org.OrganisationId, ksa.Id, ksa.CountryCode, "IN")).Items.Select(i => i.Key).ToList();
        var qatarKeys = (await resolver.ResolveAsync(org.OrganisationId, qatar.Id, qatar.CountryCode, "IN")).Items.Select(i => i.Key).ToList();

        ksaKeys.Should().Contain("IqamaNumber").And.Contain("GosiReference").And.NotContain("Qid");
        qatarKeys.Should().Contain("Qid").And.NotContain("IqamaNumber").And.NotContain("GosiReference");
    }

    // ── The setup command's input ────────────────────────────────────────────────────────────────

    [Fact]
    public void SetupInput_ReadsTheFileFirst_AndFallsBackToConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OrganisationSetup:OrganisationName"] = "From environment",
            ["OrganisationSetup:HomeCountry"] = "AE",
            ["OrganisationSetup:AdminPassword"] = "env-password-123",
        }).Build();

        var input = OrganisationSetupCommand.Read(configuration,
            """{ "organisationName": "From file", "adminEmail": "admin@example.com" }""");

        input.OrganisationName.Should().Be("From file");
        input.HomeCountry.Should().Be("AE");
        input.AdminEmail.Should().Be("admin@example.com");
        input.AdminPassword.Should().Be("env-password-123");
    }

    [Fact]
    public void TheSetupFilePath_IsTheArgumentAfterTheCommand()
    {
        OrganisationSetupCommand.ShouldHandle(["--setup-organisation"]).Should().BeTrue();
        OrganisationSetupCommand.FilePathFrom(["--setup-organisation", "/etc/kynexone/setup.json"]).Should().Be("/etc/kynexone/setup.json");
        OrganisationSetupCommand.FilePathFrom(["--setup-organisation"]).Should().BeNull();
        OrganisationSetupCommand.FilePathFrom(["--setup-organisation", "--other"]).Should().BeNull();
        OrganisationSetupCommand.ShouldHandle(["--migrate"]).Should().BeFalse();
    }
}
