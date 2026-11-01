using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CompanySetup;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Every readiness item opens the Setup section that fixes it, with the company selected. The statutory leave policies item
/// used to point at /leave?companyId=, the operational Leave page, after the policy editor moved into Setup.
/// </summary>
public class CompanySetupReadinessHrefTests
{
    private sealed class NoEmail : IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    [Fact]
    public async Task TheLeavePoliciesItemOpensSetupLeavePoliciesWithThatCompanySelected()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Acme", CountryCode = "AE", DefaultCurrency = "AED" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var items = await CompanySetupReadiness.ComputeAsync(db, new NoEmail(), tenantId, company, CancellationToken.None);

        items.Single(i => i.Key == "leavePolicies").Href.Should().Be($"/setup?tab=leavePolicies&companyId={company.Id}");
    }

    [Theory]
    [InlineData("SA", "bankWps")]
    [InlineData("SAU", "bankWps")]
    [InlineData("AE", "companies")]
    public async Task TheBankAndWpsItemOpensTheSetupSectionThatCanFillItIn(string country, string tab)
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Acme", CountryCode = country, DefaultCurrency = country == "SA" ? "SAR" : "AED" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var items = await CompanySetupReadiness.ComputeAsync(db, new NoEmail(), tenantId, company, CancellationToken.None);

        // Not /payroll: its Bank / WPS tab only creates payment batches from a locked run, so before the first payment the
        // employer settings this item asks for could not be entered there.
        items.Single(i => i.Key == "bankWps").Href.Should().Be($"/setup?tab={tab}&companyId={company.Id}");
    }

    [Fact]
    public async Task TheBankAndWpsItemIsDoneOnlyOnceTheEmployerSettingsAreSaved()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var companyId = await SaudiBankExportTestData.SeedCompanyAsync(db, tenantId);
        var company = await db.Companies.FirstAsync(c => c.Id == companyId);

        var before = await CompanySetupReadiness.ComputeAsync(db, new NoEmail(), tenantId, company, CancellationToken.None);
        before.Single(i => i.Key == "bankWps").Status.Should().NotBe("done");

        (await new SaudiBankExportService(db).SaveSettingsAsync(tenantId, companyId, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default))
            .Outcome.Should().Be(SaudiBankExportOutcome.Ok);

        var after = await CompanySetupReadiness.ComputeAsync(db, new NoEmail(), tenantId, company, CancellationToken.None);
        after.Single(i => i.Key == "bankWps").Status.Should().Be("done");
    }
}
