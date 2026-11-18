using FluentAssertions;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Payroll;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>A salary change, hire or approval reads an allowance as payroll does: one whose change waits for Finance is still paid on its last confirmed
/// terms, so it may stay on (or go on) a salary meanwhile. One that was never confirmed may not.</summary>
public partial class CashAllowanceRependKeepsPayingTests
{
    [Fact]
    public async Task ASalaryMayCarryAnAllowanceWhoseChangeWaitsForFinance_AsPayrollPaysIt()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        Component(await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = false }, default));

        (await SalaryAllowanceLines.DefinitionFaultsAsync(w.Db, w.Tid, [Car], default, w.CompanyId)).Should().BeEmpty();
    }

    [Fact]
    public async Task ASalaryMayNotCarryAnAllowanceThatWasNeverConfirmed()
    {
        await using var w = await World.Create();
        Component(await w.As(w.Hr).AddBenefit(w.Service, CashCar(), default));

        (await SalaryAllowanceLines.DefinitionFaultsAsync(w.Db, w.Tid, [Car], default, w.CompanyId))
            .Should().ContainKey(Car).WhoseValue.Should().Be(AllowanceDefinitionFault.NotConfirmed);
    }
}
