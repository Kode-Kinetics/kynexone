using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A Saudi company gets the ANB Connect bank-instruction export with nothing configured: no environment variable, no
/// redeploy, no tenant/company GUID list. The platform team keeps a per-tenant switch (<c>saudi_bank_export</c>) that can
/// only turn it OFF, and an operator can still kill it everywhere with <c>SaudiBankExportActivation:Enabled=false</c>.
///
/// <para>Boundary: these use EF InMemory and call the controller directly, so they prove the decision and the controller's
/// gating order, not ASP.NET routing. The route-level permission pipeline is covered by the HTTP tests that already exist.</para>
/// </summary>
public class SaudiBankExportDefaultOnTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> CompanyAsync(ZayraDbContext db, Guid tenant, string country, bool deleted = false)
    {
        var company = new Company
        {
            TenantId = tenant, LegalNameEn = $"Establishment {Guid.NewGuid():N}"[..28],
            CountryCode = country, DefaultCurrency = "SAR", IsDeleted = deleted,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static async Task SwitchAsync(ZayraDbContext db, Guid tenant, bool enabled)
    {
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenant, FeatureKey = FeatureKeys.SaudiBankExport, IsEnabled = enabled });
        await db.SaveChangesAsync();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static Task<bool> EnabledAsync(ZayraDbContext db, IConfiguration? cfg, Guid tenant, Guid company) =>
        SaudiBankExportActivation.IsEnabledAsync(db, cfg, tenant, company, default);

    // ── The decision ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SA")]
    [InlineData("sa")]
    [InlineData("SAU")]
    public async Task SaudiCompany_WithNothingConfigured_IsEnabled(string country)
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await CompanyAsync(db, tenant, country);

        (await EnabledAsync(db, null, tenant, company)).Should().BeTrue();
    }

    [Theory]
    [InlineData("AE")]
    [InlineData("US")]
    [InlineData("")]
    public async Task NonSaudiCompany_WithNothingConfigured_StaysOff(string country)
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await CompanyAsync(db, tenant, country);

        (await EnabledAsync(db, null, tenant, company)).Should().BeFalse("only a Saudi legal entity is on by default");
    }

    [Fact]
    public async Task PlatformSwitchOff_DisablesTheTenant_AndWinsOverTheOperatorAllowList()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await CompanyAsync(db, tenant, "SA");
        await SwitchAsync(db, tenant, enabled: false);
        var allowList = Config(
            ("SaudiBankExportActivation:Enabled", "true"),
            ("SaudiBankExportActivation:Companies:0:TenantId", tenant.ToString()),
            ("SaudiBankExportActivation:Companies:0:CompanyId", company.ToString()));

        (await EnabledAsync(db, null, tenant, company)).Should().BeFalse();
        (await EnabledAsync(db, allowList, tenant, company)).Should().BeFalse("the platform kill switch is final");
    }

    [Fact]
    public async Task PlatformSwitchOn_RowIsTheSameAsNoRow()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await CompanyAsync(db, tenant, "SA");
        await SwitchAsync(db, tenant, enabled: true);

        (await EnabledAsync(db, null, tenant, company)).Should().BeTrue();
    }

    [Fact]
    public async Task SwitchingOneTenantOff_NeverTouchesAnotherTenant()
    {
        var db = NewDb();
        var off = Guid.NewGuid();
        var other = Guid.NewGuid();
        var offCompany = await CompanyAsync(db, off, "SA");
        var otherCompany = await CompanyAsync(db, other, "SA");
        await SwitchAsync(db, off, enabled: false);

        (await EnabledAsync(db, null, off, offCompany)).Should().BeFalse();
        (await EnabledAsync(db, null, other, otherCompany)).Should().BeTrue();
    }

    [Fact]
    public async Task ACompanyOfAnotherTenant_IsNeverEnabled_EvenIfSaudi()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var foreign = await CompanyAsync(db, Guid.NewGuid(), "SA");

        (await EnabledAsync(db, null, tenant, foreign)).Should().BeFalse("the company must belong to the calling tenant");
    }

    [Fact]
    public async Task ADeletedOrMissingOrEmptyCompany_IsNeverEnabled()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var deleted = await CompanyAsync(db, tenant, "SA", deleted: true);

        (await EnabledAsync(db, null, tenant, deleted)).Should().BeFalse();
        (await EnabledAsync(db, null, tenant, Guid.NewGuid())).Should().BeFalse();
        (await EnabledAsync(db, null, tenant, Guid.Empty)).Should().BeFalse();
        (await EnabledAsync(db, null, Guid.Empty, deleted)).Should().BeFalse();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("0")]
    [InlineData("no")]
    public async Task OperatorKillSwitch_AnythingButTrue_DisablesEveryTenant(string value)
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await CompanyAsync(db, tenant, "SA");

        (await EnabledAsync(db, Config(("SaudiBankExportActivation:Enabled", value)), tenant, company)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("true")]
    public async Task OperatorSwitch_UnsetBlankOrTrue_DoesNotDisableTheDefault(string? value)
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await CompanyAsync(db, tenant, "SA");
        var cfg = value is null ? Config() : Config(("SaudiBankExportActivation:Enabled", value));

        (await EnabledAsync(db, cfg, tenant, company)).Should().BeTrue();
    }

    [Fact]
    public async Task TheOperatorAllowList_StillEnablesACompanyThatIsNotSaudi()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await CompanyAsync(db, tenant, "AE");
        var cfg = Config(
            ("SaudiBankExportActivation:Enabled", "true"),
            ("SaudiBankExportActivation:Companies:0:TenantId", tenant.ToString()),
            ("SaudiBankExportActivation:Companies:0:CompanyId", company.ToString()));

        (await EnabledAsync(db, cfg, tenant, company)).Should().BeTrue("the pre-existing allow-list keeps working");
    }

    [Fact]
    public async Task EnabledCompanyIds_AreTheTenantsSaudiCompanies_PlusTheAllowList_MinusDeleted()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var saudi = await CompanyAsync(db, tenant, "SA");
        var uae = await CompanyAsync(db, tenant, "AE");
        var listed = await CompanyAsync(db, tenant, "AE");
        await CompanyAsync(db, tenant, "SA", deleted: true);
        await CompanyAsync(db, Guid.NewGuid(), "SA");
        var cfg = Config(
            ("SaudiBankExportActivation:Enabled", "true"),
            ("SaudiBankExportActivation:Companies:0:TenantId", tenant.ToString()),
            ("SaudiBankExportActivation:Companies:0:CompanyId", listed.ToString()));

        var ids = await SaudiBankExportActivation.EnabledCompanyIdsAsync(db, cfg, tenant, default);

        ids.Should().BeEquivalentTo(new[] { saudi, listed });
        ids.Should().NotContain(uae);

        await SwitchAsync(db, tenant, enabled: false);
        (await SaudiBankExportActivation.EnabledCompanyIdsAsync(db, cfg, tenant, default)).Should().BeEmpty();
    }

    // ── The controller ──────────────────────────────────────────────────────────────────────────

    private static async Task<(ZayraDbContext Db, Guid Tenant, Guid Company, Guid Batch)> SeedAsync()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);   // CountryCode = "SA"
        var batch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 9);
        var svc = new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses());
        (await svc.SaveSettingsAsync(tenant, company, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default))
            .Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        return (db, tenant, company, batch);
    }

    private static void AssertDisabled(IActionResult result)
    {
        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        obj.Value.Should().BeEquivalentTo(new
        {
            error = "bank_export_disabled",
            message = "Bank instruction export is not activated for this legal entity.",
        });
    }

    private static bool Available(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<SaudiBankExportAvailabilityDto>().Subject.Enabled;

    [Fact]
    public async Task Controller_SaudiBatch_IsAvailable_WithNoConfiguration()
    {
        var (db, tenant, company, batch) = await SeedAsync();
        var ctrl = SaudiBankExportTests.Ctrl(db, tenant, company, activationConfiguration: null, "payroll.export", "payroll.structure_manage");

        Available(await ctrl.Availability(batch, default)).Should().BeTrue();
        AssertNotDisabled(await ctrl.Context(batch, default));
    }

    private static void AssertNotDisabled(IActionResult result) =>
        (result is ObjectResult { Value: { } v } && v.ToString()!.Contains("bank_export_disabled")).Should().BeFalse();

    [Fact]
    public async Task Controller_PlatformSwitchedOff_RejectsEveryBatchAndTemplateAction_AndWritesNothing()
    {
        var (db, tenant, company, batch) = await SeedAsync();
        await SwitchAsync(db, tenant, enabled: false);
        var ctrl = SaudiBankExportTests.Ctrl(db, tenant, company, activationConfiguration: null, "payroll.export", "payroll.structure_manage");
        var auditBefore = db.PayrollAuditLogs.Count();

        Available(await ctrl.Availability(batch, default)).Should().BeFalse();
        AssertDisabled(await ctrl.Context(batch, default));
        AssertDisabled(await ctrl.Validate(batch, SaudiBankExportTestData.Request(), default));
        AssertDisabled(await ctrl.Generate(batch, SaudiBankExportTestData.Request(), default));
        AssertDisabled(await ctrl.Download(batch, default));
        AssertDisabled(await ctrl.ListTemplates(company, default));
        AssertDisabled(await ctrl.TemplateCatalog());

        db.BankTransferFiles.Count().Should().Be(0);
        db.PayrollAuditLogs.Count().Should().Be(auditBefore);
    }

    [Fact]
    public async Task Controller_TemplateCatalog_IsOpenForASaudiCompanyTheCallerCanAccess_AndClosedOtherwise()
    {
        var (db, tenant, company, _) = await SeedAsync();

        (await SaudiBankExportTests.Ctrl(db, tenant, company, activationConfiguration: null, "payroll.export", "payroll.structure_manage")
            .TemplateCatalog()).Should().BeOfType<OkObjectResult>();

        // Scoped to a different company of the same tenant: the Saudi company is not theirs, so nothing is open to them.
        var elsewhere = await CompanyAsync(db, tenant, "AE");
        AssertDisabled(await SaudiBankExportTests.Ctrl(db, tenant, elsewhere, activationConfiguration: null, "payroll.export", "payroll.structure_manage")
            .TemplateCatalog());
    }

    [Fact]
    public async Task Controller_OperatorKillSwitch_ClosesASaudiBatch()
    {
        var (db, tenant, company, batch) = await SeedAsync();
        var ctrl = SaudiBankExportTests.Ctrl(db, tenant, company, Config(("SaudiBankExportActivation:Enabled", "false")),
            "payroll.export", "payroll.structure_manage");

        Available(await ctrl.Availability(batch, default)).Should().BeFalse();
        AssertDisabled(await ctrl.Generate(batch, SaudiBankExportTestData.Request(), default));
    }

    // ── The platform switch ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheConsoleOffersTheSwitch_ShownOnByDefault()
    {
        var entry = PlatformController.FeatureFlagCatalog.Single(f => f.Key == FeatureKeys.SaudiBankExport);
        entry.DefaultEnabled.Should().BeTrue("with no row a Saudi tenant has the export");
        PlatformDefaultOnFeatures.IsDefaultOn(FeatureKeys.SaudiBankExport).Should().BeTrue();
        PlatformDefaultOnFeatures.IsDefaultOn(FeatureKeys.Learning).Should().BeFalse();
    }

    [Fact]
    public void TenantAdmin_CannotSwitchItOff_OnlyThePlatformCan() =>
        ModuleCatalog.NonModuleKeys.Should().ContainKey(FeatureKeys.SaudiBankExport);
}

/// <summary>The platform side of the switch: its audit trail, and that "enable for everyone" cannot silently re-open closed tenants.</summary>
public class SaudiBankExportPlatformSwitchTests : Zayra.Api.Tests.Platform.PlatformTestBase
{
    private static async Task<Guid> TenantAsync(ZayraDbContext db, string name)
    {
        var id = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = id, Name = name, Slug = $"{name}-{Guid.NewGuid():N}", IsActive = true });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task SwitchingItOff_WithNoRowYet_AuditsThatItWasOn()
    {
        await using var db = CreateDb();
        var tenant = await TenantAsync(db, "ksa");

        await CreateController(db).SetFeatureFlag(tenant, FeatureKeys.SaudiBankExport, new SetFeatureFlagRequest(false, null), default);

        db.TenantFeatureFlags.Should().ContainSingle(f => f.TenantId == tenant && f.FeatureKey == FeatureKeys.SaudiBankExport && !f.IsEnabled);
        var audit = db.AdminAuditLogs.Single(a => a.TenantId == tenant && a.EntityType == "FeatureFlag");
        audit.Action.Should().Be("FeatureDisabled");
        audit.OldValuesJson.Should().Contain("\"isEnabled\":true", "with no row the export was on for a Saudi company");
        audit.NewValuesJson.Should().Contain("\"isEnabled\":false");
    }

    [Fact]
    public async Task BulkDisable_WithNoRows_AuditsThatItWasOn_AndClosesEveryTenant()
    {
        await using var db = CreateDb();
        var a = await TenantAsync(db, "a");
        var b = await TenantAsync(db, "b");

        await CreateController(db).BulkSetFeatureFlag(new BulkFeatureFlagRequest(null, true, FeatureKeys.SaudiBankExport, false, null), default);

        db.TenantFeatureFlags.Where(f => f.FeatureKey == FeatureKeys.SaudiBankExport).Select(f => f.TenantId)
            .Should().BeEquivalentTo(new[] { a, b });
        db.AdminAuditLogs.Where(x => x.EntityType == "FeatureFlag").Select(x => x.OldValuesJson)
            .Should().OnlyContain(j => j!.Contains("\"isEnabled\":true"));
    }

    [Fact]
    public async Task BulkEnable_ForAllTenants_IsRefused_SoItNeverReopensATenantThePlatformClosed()
    {
        await using var db = CreateDb();
        var closed = await TenantAsync(db, "closed");
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = closed, FeatureKey = FeatureKeys.SaudiBankExport, IsEnabled = false });
        await db.SaveChangesAsync();

        var result = await CreateController(db).BulkSetFeatureFlag(new BulkFeatureFlagRequest(null, true, FeatureKeys.SaudiBankExport, true, null), default);

        var refused = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(refused.Value).Should().Contain("default_on_bulk_enable_refused");
        db.TenantFeatureFlags.Single(f => f.TenantId == closed).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task BulkEnable_ForNamedTenants_StaysPossible_ADeliberateChoice()
    {
        await using var db = CreateDb();
        var closed = await TenantAsync(db, "closed");
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = closed, FeatureKey = FeatureKeys.SaudiBankExport, IsEnabled = false });
        await db.SaveChangesAsync();

        var result = await CreateController(db).BulkSetFeatureFlag(new BulkFeatureFlagRequest(new List<Guid> { closed }, false, FeatureKeys.SaudiBankExport, true, null), default);

        result.Should().NotBeOfType<UnprocessableEntityObjectResult>();
        db.TenantFeatureFlags.Single(f => f.TenantId == closed).IsEnabled.Should().BeTrue();
    }
}
