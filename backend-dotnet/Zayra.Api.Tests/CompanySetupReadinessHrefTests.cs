using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CompanySetup;
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
    [Fact]
    public async Task New_company_checklist_requires_distinct_usable_pay_entry_and_review_users()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = new Zayra.Api.Domain.Entities.Tenant { Name = "Fresh company", Slug = Guid.NewGuid().ToString("N"), IsActive = true };
        db.Tenants.Add(tenant);
        var company = new Company { TenantId = tenant.Id, LegalNameEn = "Fresh company", CountryCode = "AE", IsActive = true };
        db.Companies.Add(company);
        var maker = new Zayra.Api.Domain.Entities.User { TenantId = tenant.Id, Tenant = tenant, Email = "maker@company.test", IsGroupScope = true };
        db.Users.Add(maker);
        db.UserPermissionOverrides.AddRange(
            new UserPermissionOverride { TenantId = tenant.Id, UserId = maker.Id, PermissionKey = "employees.write", Effect = "Allow", IsActive = true },
            new UserPermissionOverride { TenantId = tenant.Id, UserId = maker.Id, PermissionKey = "payroll.approve", Effect = "Allow", IsActive = true });
        await db.SaveChangesAsync();
        async Task<string> Status() => (await CompanySetupReadiness.ComputeAsync(db, new NoEmail(), tenant.Id, company, default))
            .Single(i => i.Key == "independentPayReview").Status;
        (await Status()).Should().Be("todo", "one administrator must never approve their own packages");
        var reviewer = new Zayra.Api.Domain.Entities.User { TenantId = tenant.Id, Tenant = tenant, Email = "reviewer@company.test", IsGroupScope = true };
        db.Users.Add(reviewer);
        db.UserPermissionOverrides.Add(new UserPermissionOverride { TenantId = tenant.Id, UserId = reviewer.Id,
            PermissionKey = "payroll.approve", Effect = "Allow", IsActive = true });
        await db.SaveChangesAsync();
        (await Status()).Should().Be("done", "direct reviewers do not depend on employee activation");
        reviewer.MustChangePassword = true;
        await db.SaveChangesAsync();
        (await Status()).Should().Be("todo", "a permission alone is not a usable reviewer");
    }

    [Fact]
    public async Task A_switching_company_lists_the_history_to_bring_over_and_derives_done_from_the_carried_rows()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Acme", CountryCode = "SA", DefaultCurrency = "SAR" };
        var other = new Company { TenantId = tenantId, LegalNameEn = "Other", CountryCode = "SA", DefaultCurrency = "SAR" };
        db.Companies.AddRange(company, other);
        var cutover = new CompanyCutover { TenantId = tenantId, CompanyId = company.Id, CutoverDate = new DateOnly(2026, 3, 1), Status = CutoverStatuses.Planned };
        db.CompanyCutovers.Add(cutover);
        await db.SaveChangesAsync();
        string[] keys = ["migrateHistoryPayrollYtd", "migrateHistoryLeave", "migrateHistoryLoans", "migrateHistoryEosb"];
        async Task<IReadOnlyList<ReadinessItem>> Items() => await CompanySetupReadiness.ComputeAsync(db, new NoEmail(), tenantId, company, default);

        (await Items()).Select(i => i.Key).Should().NotContain(k => k.StartsWith("migrateHistory"), "a brand-new company has no history to bring");

        cutover.SourceSystem = "SAP";
        await db.SaveChangesAsync();
        (await Items()).Select(i => i.Key).Should().NotContain(k => k.StartsWith("migrateHistory"), "a source system alone does not make a Planned cutover switching");
        cutover.Status = CutoverStatuses.Active;
        await db.SaveChangesAsync();
        var items = await Items();
        foreach (var key in keys)
        {
            var item = items.Single(i => i.Key == key);
            item.Status.Should().Be("todo");
            item.Href.Should().Be($"/setup?tab=openingBalances&companyId={company.Id}");
            item.Label.Should().NotBeNullOrWhiteSpace();
            item.LabelAr.Should().NotBeNullOrWhiteSpace();
        }
        items.Single(i => i.Key == "migrateHistoryEosb").Label.Should().Contain("hire date");

        // Rows carried for ANOTHER company do not count; this company's rows do.
        db.OpeningBalanceOrigins.Add(new OpeningBalanceOrigin { TenantId = tenantId, CompanyId = other.Id, EntityType = OpeningBalanceEntityTypes.LeaveBalance, EntityId = Guid.NewGuid() });
        db.OpeningBalanceOrigins.Add(new OpeningBalanceOrigin { TenantId = tenantId, CompanyId = company.Id, EntityType = OpeningBalanceEntityTypes.Advance, EntityId = Guid.NewGuid() });
        db.PayrollOpeningBalances.Add(new PayrollOpeningBalance { TenantId = tenantId, CompanyId = company.Id, Year = 2026, BalanceType = OpeningBalanceTypes.YtdGross, Amount = 1 });
        await db.SaveChangesAsync();
        items = await Items();
        items.Single(i => i.Key == "migrateHistoryLeave").Status.Should().Be("todo");
        items.Single(i => i.Key == "migrateHistoryLoans").Status.Should().Be("done", "an advance counts as loans/advances");
        items.Single(i => i.Key == "migrateHistoryPayrollYtd").Status.Should().Be("done");
        items.Single(i => i.Key == "migrateHistoryEosb").Status.Should().Be("todo");

        // An Active cutover with no named source is a switching company too.
        cutover.SourceSystem = string.Empty;
        await db.SaveChangesAsync();
        (await Items()).Select(i => i.Key).Should().Contain(keys);
    }

    [Fact]
    public async Task ApprovedPositionsAndBudgetIsDoneOnceADepartmentHasAnApprovedHeadcountOrBudget()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Acme", CountryCode = "SA", DefaultCurrency = "SAR" };
        var other = new Company { TenantId = tenantId, LegalNameEn = "Other", CountryCode = "SA", DefaultCurrency = "SAR" };
        db.Companies.AddRange(company, other);
        var dept = new Department { TenantId = tenantId, CompanyId = company.Id, NameEn = "Ops", IsActive = true };
        db.Departments.Add(dept);
        await db.SaveChangesAsync();
        async Task<string> Status() => (await CompanySetupReadiness.ComputeAsync(db, new NoEmail(), tenantId, company, default))
            .Single(i => i.Key == "establishment").Status;

        (await Status()).Should().Be("recommended", "nothing is planned yet");
        dept.ApprovedHeadcount = 5;
        await db.SaveChangesAsync();
        (await Status()).Should().Be("done", "the Headcount & budget screen saves on the department, not on a position");
        dept.ApprovedHeadcount = 0;
        dept.MonthlyBudgetAmount = 10000;
        await db.SaveChangesAsync();
        (await Status()).Should().Be("done", "a budget alone is a plan");
        dept.MonthlyBudgetAmount = 0;
        dept.CompanyId = other.Id;
        dept.ApprovedHeadcount = 5;
        await db.SaveChangesAsync();
        (await Status()).Should().Be("recommended", "another company's plan does not complete this one");
    }

    [Fact]
    public async Task InactiveOrDeletedDepartmentPlans_DoNotCompleteTheEstablishmentItem()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Acme", CountryCode = "SA", DefaultCurrency = "SAR" };
        db.Companies.Add(company);
        var inactive = new Department { TenantId = tenantId, CompanyId = company.Id, NameEn = "Old", IsActive = false, ApprovedHeadcount = 5, MonthlyBudgetAmount = 9000 };
        var deleted = new Department { TenantId = tenantId, CompanyId = company.Id, NameEn = "Gone", IsActive = true, IsDeleted = true, ApprovedHeadcount = 5, MonthlyBudgetAmount = 9000 };
        db.Departments.AddRange(inactive, deleted);
        await db.SaveChangesAsync();
        async Task<string> Status() => (await CompanySetupReadiness.ComputeAsync(db, new NoEmail(), tenantId, company, default))
            .Single(i => i.Key == "establishment").Status;
        (await Status()).Should().Be("recommended", "a retired department's plan is not a plan");
    }

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
