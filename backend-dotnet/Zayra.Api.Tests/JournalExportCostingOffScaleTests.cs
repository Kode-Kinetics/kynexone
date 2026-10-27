using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Finance;
using static Zayra.Api.Tests.CostExportHarness;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests;

/// <summary>
/// The byte-identical promise: a company that has not switched cost accounting on gets exactly the files it got before the currency-scale change
/// (two decimals, even for KWD/BHD/OMR). The currency scale applies once costing is on. Frozen exports regenerate at the scale they were written at.
/// </summary>
public class JournalExportCostingOffScaleTests
{
    private static async Task<string> ExportKwd(bool costing)
    {
        var (db, tenantId) = await TenantAsync("KWD", costAccounting: costing);
        SeedChart(db, tenantId);
        db.Add(Row(tenantId, "Accrual", "5000 - Salaries", "", 10.005m, currency: "KWD"));
        db.Add(Row(tenantId, "Accrual", "", "2100 - Salaries Payable", 10.005m, currency: "KWD"));
        db.SaveChanges();
        var maker = await CallerAsync(db, tenantId, "Payroll Manager");
        var created = await CreateAsync(db, maker, "generic-csv", "KWD");
        StatusOf(created).Should().Be(201, System.Text.Json.JsonSerializer.Serialize(((Microsoft.AspNetCore.Mvc.ObjectResult)created).Value));
        var id = IdOf(created);
        var stored = await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == id);
        _scale = stored.AmountScale;
        return Utf8(await DownloadAsync(db, tenantId, id));
    }

    private static short? _scale;

    [Fact]
    public async Task AKuwaitiCompanyWithCostingOff_ExportsTwoDecimals_AsBefore()
    {
        var csv = await ExportKwd(costing: false);
        _scale.Should().Be((short)2);
        csv.Should().Contain("10.01").And.NotContain("10.005").And.NotContain("10.010");
    }

    [Fact]
    public async Task AKuwaitiCompanyWithCostingOn_KeepsThreeDecimals()
    {
        var csv = await ExportKwd(costing: true);
        _scale.Should().Be((short)3);
        csv.Should().Contain("10.005");
    }

    [Fact]
    public void Expand_WithAScaleResolver_StampsTheResolvedScale()
    {
        var entry = Row(Guid.NewGuid(), "Accrual", "5000 - Salaries", "", 10m, currency: "KWD");
        JournalExportBuilder.Expand(new[] { entry }, _ => "SA01", null, null, _ => 2).Should().OnlyContain(l => l.Scale == 2);
        JournalExportBuilder.Expand(new[] { entry }, _ => "SA01", null, null).Should().OnlyContain(l => l.Scale == 3, "no resolver keeps the currency scale");
    }
}
