using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostExportHarness;

namespace Zayra.Api.Tests;

/// <summary>Cost-centre names are plain single-line text: direction marks are dropped, tab/CR/LF become a space, other control characters are refused, and exports never carry direction marks.</summary>
public class CostCentreNameTextTests
{
    private static readonly char[] DirectionMarks =
    {
        '‪', '‫', '‬', '‭', '‮', '⁦', '⁧', '⁨', '⁩', '‎', '‏',
    };

    private static async Task<(ZayraDbContext Db, Guid Tenant, Guid Company, OrganizationSetupService Svc)> SeedAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        db.Add(a); await db.SaveChangesAsync();
        return (db, tenant, a.Id, new OrganizationSetupService(db, new AuditService(db)));
    }

    private static RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant);

    [Theory]
    [InlineData("‮Ops‬", "Ops")]
    [InlineData("A⁦B⁩C‎‏", "ABC")]
    [InlineData("North\tWing\r\nStores", "North Wing  Stores")]
    public async Task ACreatedName_IsNormalised(string sent, string stored)
    {
        var (db, tenant, company, svc) = await SeedAsync();

        var created = await svc.CreateCostCenterAsync(tenant, new CostCenterRequest(company, "N1", sent), Ctx(tenant), default);

        (await db.CostCenters.AsNoTracking().SingleAsync(c => c.Id == created.Id)).Name.Should().Be(stored);
    }

    [Fact]
    public async Task AnUpdatedName_IsNormalisedToo()
    {
        var (db, tenant, company, svc) = await SeedAsync();
        var created = await svc.CreateCostCenterAsync(tenant, new CostCenterRequest(company, "N1", "Ops"), Ctx(tenant), default);

        await svc.UpdateCostCenterAsync(tenant, created.Id, new CostCenterRequest(company, "N1", "‮Ops2"), Ctx(tenant), default);

        (await db.CostCenters.AsNoTracking().SingleAsync(c => c.Id == created.Id)).Name.Should().Be("Ops2");
    }

    [Fact]
    public async Task AnotherControlCharacter_IsARowError()
    {
        var (db, tenant, company, svc) = await SeedAsync();

        var act = () => svc.CreateCostCenterAsync(tenant, new CostCenterRequest(company, "N1", "Ops\u0007"), Ctx(tenant), default);

        var ex = (await act.Should().ThrowAsync<CostCentreRuleException>()).Which;
        (ex.Code, ex.Status).Should().Be(("cost_centre_name_invalid", 422));
        ex.MessageAr.Should().NotBeNullOrWhiteSpace();
        (await db.CostCenters.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void EveryFormatter_DropsDirectionMarks_FromTheCostCentreName()
    {
        var rows = new List<FinanceGlEntry>
        {
            Row(Guid.NewGuid(), "Accrual", "5000 - Salaries", "", 100m, ccCode: "CC100", ccName: "‮=1+1‬ Ops‏", ccSegment: "6100-OPS"),
            Row(Guid.NewGuid(), "Accrual", "", "2100 - Salaries Payable", 100m),
        };
        var doc = Doc(rows);

        foreach (var formatter in JournalExportFormatterGoldenTests.AllFormatters())
        {
            var text = System.Text.Encoding.UTF8.GetString(formatter.Format(doc));
            text.IndexOfAny(DirectionMarks).Should().Be(-1, $"{formatter.Key} must not emit direction marks");
        }
    }
}
