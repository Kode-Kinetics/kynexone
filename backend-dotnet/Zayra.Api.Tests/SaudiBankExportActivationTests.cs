using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Tests;

/// <summary>
/// Regression coverage for the default-OFF <see cref="SaudiBankExportActivation"/> release switch.
///
/// <para>TEST-BOUNDARY LABELS, read literally:</para>
/// <list type="bullet">
///   <item><b>Pure logic</b> tests below call <see cref="SaudiBankExportActivation"/> directly with a
///   hand-built <see cref="IConfiguration"/> — no DB, no controller, no HTTP pipeline.</item>
///   <item><b>Controller / EF InMemory</b> tests build <see cref="SaudiBankExportsController"/> the same
///   way <c>SaudiBankExportTests.Ctrl</c> does (that helper is reused via its now-internal visibility) —
///   this proves the controller's own gating order, not ASP.NET routing, model binding or the real
///   authorization/JWT pipeline. Those are covered elsewhere by <c>SaudiBankExportAuthorizationHttpTests</c>
///   against the shared <c>AuthorizationPipelineFixture</c>, which this file deliberately does not touch
///   or extend: that fixture has no token carrying <c>payroll.export</c>, so a real-HTTP activation test
///   would need a fixture change, which is out of scope for this change.</item>
/// </list>
/// </summary>
public class SaudiBankExportActivationTests
{
    // ── Pure logic: SaudiBankExportActivation ───────────────────────────────────────────────────

    private static IConfiguration Config(string? enabled, params (string TenantId, string CompanyId)[] companies)
    {
        var dict = new Dictionary<string, string?>();
        if (enabled is not null) dict[$"{SaudiBankExportActivation.Section}:Enabled"] = enabled;
        for (var i = 0; i < companies.Length; i++)
        {
            dict[$"{SaudiBankExportActivation.Section}:Companies:{i}:TenantId"] = companies[i].TenantId;
            dict[$"{SaudiBankExportActivation.Section}:Companies:{i}:CompanyId"] = companies[i].CompanyId;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void EnabledCompanies_NullConfiguration_ReturnsEmpty() =>
        SaudiBankExportActivation.EnabledCompanies(null, Guid.NewGuid()).Should().BeEmpty();

    [Fact]
    public void IsEnabled_NullConfiguration_ReturnsFalse() =>
        SaudiBankExportActivation.IsEnabled(null, Guid.NewGuid(), Guid.NewGuid()).Should().BeFalse();

    [Fact]
    public void EnabledCompanies_EmptyTenantId_ReturnsEmpty() =>
        SaudiBankExportActivation.EnabledCompanies(Config("true"), Guid.Empty).Should().BeEmpty();

    [Fact]
    public void EnabledCompanies_MissingEnabledKey_ReturnsEmpty()
    {
        var tenant = Guid.NewGuid();
        var cfg = Config(enabled: null, (tenant.ToString(), Guid.NewGuid().ToString()));
        SaudiBankExportActivation.EnabledCompanies(cfg, tenant).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("enabled")]
    public void EnabledCompanies_MalformedEnabledValue_ReturnsEmpty(string malformed)
    {
        var tenant = Guid.NewGuid();
        var cfg = Config(malformed, (tenant.ToString(), Guid.NewGuid().ToString()));
        SaudiBankExportActivation.EnabledCompanies(cfg, tenant).Should().BeEmpty();
    }

    [Fact]
    public void EnabledCompanies_GlobalSwitchAlone_WithoutCompanyEntry_ReturnsEmpty() =>
        SaudiBankExportActivation.EnabledCompanies(Config("true"), Guid.NewGuid()).Should().BeEmpty();

    [Fact]
    public void EnabledCompanies_RequiresExactTenantAndCompanyPair_NoPartialMatch()
    {
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var company = Guid.NewGuid();
        // Same company id, but the entry belongs to a DIFFERENT tenant — must not leak across tenants.
        var cfg = Config("true", (otherTenant.ToString(), company.ToString()));

        SaudiBankExportActivation.IsEnabled(cfg, tenant, company).Should().BeFalse();
        SaudiBankExportActivation.IsEnabled(cfg, otherTenant, company).Should().BeTrue();
    }

    [Fact]
    public void EnabledCompanies_MalformedGuidEntries_AreSkipped_NeverTreatedAsWildcard()
    {
        var tenant = Guid.NewGuid();
        var cfg = Config("true", ("*", "*"), ("not-a-guid", Guid.NewGuid().ToString()));
        SaudiBankExportActivation.EnabledCompanies(cfg, tenant).Should().BeEmpty();
    }

    [Fact]
    public void EnabledCompanies_GlobalDisable_DominatesOverPresentCompanyList()
    {
        var tenant = Guid.NewGuid();
        var company = Guid.NewGuid();
        var cfg = Config("false", (tenant.ToString(), company.ToString()));

        SaudiBankExportActivation.EnabledCompanies(cfg, tenant).Should().BeEmpty();
        SaudiBankExportActivation.IsEnabled(cfg, tenant, company).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_EmptyCompanyId_IsNeverEnabled()
    {
        var tenant = Guid.NewGuid();
        var cfg = Config("true", (tenant.ToString(), Guid.Empty.ToString()));
        SaudiBankExportActivation.IsEnabled(cfg, tenant, Guid.Empty).Should().BeFalse();
    }

    // ── Controller / EF InMemory boundary ───────────────────────────────────────────────────────

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(ZayraDbContext Db, Guid Tenant, Guid Company, Guid Batch)> SeedAsync()
    {
        // Deliberately NOT calling SaudiBankExportTests.Arrange (private to that file): duplicated here,
        // minimally, because this file may only add tests, not widen that file's internals further.
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
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

    [Fact]
    public async Task Controller_DefaultOff_RejectsEveryGatedAction_EvenWithFullPermissions_AndWritesNothing()
    {
        var (db, tenant, company, batch) = await SeedAsync();
        var ctrl = SaudiBankExportTests.Ctrl(db, tenant, company, activationConfiguration: null,
            "payroll.export", "payroll.structure_manage");

        var auditCountBefore = db.PayrollAuditLogs.Count();

        AssertDisabled(ctrl.Formats());
        AssertDisabled(await ctrl.GetSettings(company, default));
        AssertDisabled(await ctrl.PutSettings(company, SaudiBankExportTestData.Settings(), default));
        AssertDisabled(await ctrl.Context(batch, default));
        AssertDisabled(await ctrl.Validate(batch, SaudiBankExportTestData.Request(), default));
        AssertDisabled(await ctrl.Generate(batch, SaudiBankExportTestData.Request(), default));
        AssertDisabled(await ctrl.Download(batch, default));

        db.BankTransferFiles.Count().Should().Be(0);
        db.PayrollAuditLogs.Count().Should().Be(auditCountBefore, "disabled calls must add no audit or business writes beyond fixture setup");
    }

    [Fact]
    public async Task Availability_DefaultOff_AuthorizedOwnBatch_ReturnsEnabledFalse_NotForbid()
    {
        var (db, tenant, company, batch) = await SeedAsync();
        var ctrl = SaudiBankExportTests.Ctrl(db, tenant, company, activationConfiguration: null, "payroll.export");

        var result = await ctrl.Availability(batch, default);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<SaudiBankExportAvailabilityDto>().Subject;
        dto.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Availability_CrossCompanyScope_IsForbidden_RegardlessOfActivation()
    {
        var (db, tenant, _, batch) = await SeedAsync();
        var ctrl = SaudiBankExportTests.Ctrl(db, tenant, Guid.NewGuid(), activationConfiguration: null, "payroll.export");
        (await ctrl.Availability(batch, default)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Availability_OtherTenant_IsNotFound_RegardlessOfActivation()
    {
        var (db, _, company, batch) = await SeedAsync();
        var ctrl = SaudiBankExportTests.Ctrl(db, Guid.NewGuid(), company, activationConfiguration: null, "payroll.export");
        (await ctrl.Availability(batch, default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Controller_EnabledPair_Succeeds_ButOtherCompanyInSameTenantDoesNot()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var companyA = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var companyB = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var svc = new SaudiBankExportService(db, new SaudiBankExportTestData.FakeAddresses());
        (await svc.SaveSettingsAsync(tenant, companyA, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default))
            .Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        (await svc.SaveSettingsAsync(tenant, companyB, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default))
            .Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        var enabledOnlyForA = SaudiBankExportTests.EnabledActivationConfig(tenant, companyA);

        (await SaudiBankExportTests.Ctrl(db, tenant, companyA, enabledOnlyForA, "payroll.export")
            .GetSettings(companyA, default)).Should().BeOfType<OkObjectResult>();
        AssertDisabled(await SaudiBankExportTests.Ctrl(db, tenant, companyB, enabledOnlyForA, "payroll.export")
            .GetSettings(companyB, default));
    }

    [Fact]
    public async Task Controller_RuntimeSwitchOff_BlocksDownload_AfterArtifactWasGenerated_ButDoesNotDeleteIt()
    {
        var (db, tenant, company, batch) = await SeedAsync();
        var enabled = SaudiBankExportTests.EnabledActivationConfig(tenant, company);
        var enabledCtrl = SaudiBankExportTests.Ctrl(db, tenant, company, enabled, "payroll.export");

        (await enabledCtrl.Generate(batch, SaudiBankExportTestData.Request(), default)).Should().BeOfType<OkObjectResult>();
        db.BankTransferFiles.Count(f => f.PaymentBatchId == batch).Should().Be(1);

        // IConfiguration is resolved per-request in production, never cached on the controller; a fresh
        // controller instance with the switch OFF is this test's stand-in for "the very next request".
        var disabledCtrl = SaudiBankExportTests.Ctrl(db, tenant, company, activationConfiguration: null, "payroll.export");
        AssertDisabled(await disabledCtrl.Download(batch, default));

        // Blocked retrieval, not erased history: the artifact persists untouched.
        db.BankTransferFiles.Count(f => f.PaymentBatchId == batch).Should().Be(1);
    }
}
