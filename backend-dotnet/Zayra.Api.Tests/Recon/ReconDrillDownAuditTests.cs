using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Tests.Recon;

/// <summary>Looking up the people behind a figure is a read of personal pay data, so the first page of each lookup is audited.</summary>
public class ReconDrillDownAuditTests
{
    private static async Task<ManpowerReconServiceTests.Kit> SeedAsync()
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync();
        for (var i = 1; i <= 3; i++) { k.AddEmployee(i); k.AddSlip(k.Prior, i, 1000); k.AddSlip(k.Current, i, 1000); }
        await k.Db.SaveChangesAsync();
        return k;
    }

    private static Zayra.Api.Controllers.PayrollReconController Ctrl(ManpowerReconServiceTests.Kit k) =>
        ReconHarness.Recon(k.Db, k.TenantId, "Auditor", ReconHarness.ReadPermissions);

    private static Task<List<Zayra.Api.Models.PayrollAuditLog>> ViewedAsync(ManpowerReconServiceTests.Kit k) =>
        k.Db.PayrollAuditLogs.AsNoTracking().Where(a => a.Action == "payroll.recon.viewed").ToListAsync();

    [Fact]
    public async Task TheFirstPageOfALookup_IsAudited_WithWhatWasAsked()
    {
        var k = await SeedAsync();
        (await Ctrl(k).Employees(k.Current.Id, "volume", "CONTINUING", null, null, null, 1, 2, default)).Should().BeOfType<OkObjectResult>();

        var rows = await ViewedAsync(k);
        rows.Should().ContainSingle();
        rows[0].EntityId.Should().Be(k.Current.Id.ToString());
        rows[0].MetadataJson.Should().Contain("volume").And.Contain("CONTINUING").And.Contain("\"total\":3");
    }

    [Fact]
    public async Task LaterPagesOfTheSameLookup_AreNotAuditedAgain_ButADifferentLookupIs()
    {
        var k = await SeedAsync();
        var ctrl = Ctrl(k);
        await ctrl.Employees(k.Current.Id, "volume", "CONTINUING", null, null, null, 1, 2, default);
        await ctrl.Employees(k.Current.Id, "volume", "CONTINUING", null, null, null, 2, 2, default);
        (await ViewedAsync(k)).Should().HaveCount(1);

        await ctrl.Employees(k.Current.Id, "volume", "OPENING", null, null, null, 1, 2, default);
        (await ViewedAsync(k)).Should().HaveCount(2);
    }

    [Fact]
    public async Task ARefusedLookup_IsNotAudited()
    {
        var k = await SeedAsync();
        (await Ctrl(k).Employees(k.Current.Id, "nope", null, null, null, null, 1, 50, default)).Should().BeOfType<BadRequestObjectResult>();
        (await ViewedAsync(k)).Should().BeEmpty();
    }
}
