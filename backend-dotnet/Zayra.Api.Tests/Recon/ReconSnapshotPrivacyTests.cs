using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

/// <summary>What is stored with a sign-off names no employee: the live reading still shows who, the kept record holds ids.</summary>
public class ReconSnapshotPrivacyTests
{
    private static async Task<ManpowerReconServiceTests.Kit> SeedLeaverWithoutSettlementAsync()
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync();
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000); k.AddSlip(k.Current, 1, 1000);
        k.AddEmployee(2, "Offboarded"); k.AddSlip(k.Prior, 2, 700, finalWage: true);
        await k.Db.SaveChangesAsync();
        return k;
    }

    [Fact]
    public async Task TheStoredSnapshot_KeepsTheIdOfALeaverWithoutASettlement_ButNotTheName()
    {
        var k = await SeedLeaverWithoutSettlementAsync();
        var ctrl = ReconHarness.Recon(k.Db, k.TenantId, "Finance Approver", ReconHarness.SignPermissions);
        var live = ReconHarness.Body<ReconDto>(await ctrl.Get(k.Current.Id, null, default));
        live.FinalSettlement.LeaversWithoutFs.Should().ContainSingle(l => l.EmployeeId == 2 && l.EmployeeName == "Employee 2", "the live reading still shows who");

        PayrollReconAccessTests.Status(await ctrl.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id), default)).Should().Be(201);

        var stored = await k.Db.PayrollRunReconSignoffs.AsNoTracking().SingleAsync();
        stored.SnapshotJson.Should().Contain("leaversWithoutFs").And.Contain("\"employeeId\":2")
            .And.NotContain("Employee 2").And.NotContain("employeeName");
    }

    [Fact]
    public void TheHash_DoesNotDependOnAName()
    {
        var withName = new ReconLeaverWithoutFsDto(2, "E2", "Employee 2", "2026-09-30");
        var renamed = withName with { EmployeeName = "Somebody Else" };
        var dto = JsonFixture();
        var a = dto with { FinalSettlement = dto.FinalSettlement with { LeaversWithoutFs = new[] { withName } } };
        var b = dto with { FinalSettlement = dto.FinalSettlement with { LeaversWithoutFs = new[] { renamed } } };
        ReconSnapshotHash.Compute(a, 1, 1).Should().Be(ReconSnapshotHash.Compute(b, 1, 1));
        ReconSnapshotHash.SummaryJson(a).Should().Be(ReconSnapshotHash.SummaryJson(b));
    }

    private static ReconDto JsonFixture() =>
        System.Text.Json.JsonSerializer.Deserialize<ReconDto>(ReconContractShapeTests.FixtureText("recon-full"),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
}
