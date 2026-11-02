using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Controllers;
using Zayra.Api.Models;
using static Zayra.Api.Tests.OvertimeFixKit;

namespace Zayra.Api.Tests;

/// <summary>
/// OVT-14 (P2): GET /api/overtime/calculations returned every in-scope row's HourlyRate - a figure derived
/// from the employee's salary - to any caller with a data scope (a line manager sees the team's overtime
/// hours, which is not the same as seeing what each person earns). Only Admin / HR / Payroll roles and the
/// employee the calculation belongs to may see the rate (and the rate breakdown in CalculationJson).
/// </summary>
public class OvertimeCalculationRateVisibilityTests
{
    private static async Task<(World W, OvertimeCalculation Calc)> SeedCalcAsync()
    {
        var w = await SeedAsync();
        var calc = new OvertimeCalculation
        {
            TenantId = w.TenantId, OvertimeRequestId = Guid.NewGuid(), EmployeeId = w.Subject.Id, ApprovedMinutes = 120,
            HourlyRate = 125m, Multiplier = 1.5m, Amount = 325m, Currency = "SAR",
            CalculationJson = "{\"basicHourly\":75.0000,\"baseHourly\":125.0000,\"effectiveMultiplier\":1.5}",
        };
        w.Db.OvertimeCalculations.Add(calc);
        await w.Db.SaveChangesAsync();
        return (w, calc);
    }

    private static async Task<OvertimeCalculationView> ReadAsync(OvertimeController c)
    {
        var ok = (await c.Calculations(null, 100, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>().Subject;
        return ((IEnumerable<OvertimeCalculationView>)ok.Value!).Should().ContainSingle().Subject;
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR Manager")]
    [InlineData("Payroll Officer")]
    [InlineData("Payroll Manager")]
    public async Task PrivilegedRoles_SeeTheHourlyRate(string role)
    {
        var (w, _) = await SeedCalcAsync();
        var row = await ReadAsync(Controller(w, Guid.NewGuid(), new[] { role }));

        row.HourlyRate.Should().Be(125m);
        row.CalculationJson.Should().Contain("baseHourly");
        row.Amount.Should().Be(325m);
    }

    [Fact]
    public async Task TheOwner_SeesTheirOwnRate()
    {
        var (w, _) = await SeedCalcAsync();
        var row = await ReadAsync(Controller(w, w.SubjectUserId, new[] { "Employee" }, callerEmployeeId: w.Subject.Id));

        row.HourlyRate.Should().Be(125m);
    }

    [Theory]
    [InlineData("Manager")]
    [InlineData("Supervisor")]
    [InlineData("Employee")]
    [InlineData("Auditor")]
    public async Task EveryoneElse_GetsNoRate_ButStillSeesHoursAndAmount(string role)
    {
        var (w, _) = await SeedCalcAsync();
        var row = await ReadAsync(Controller(w, Guid.NewGuid(), new[] { role }, callerEmployeeId: w.Subject.Id + 1000));

        row.HourlyRate.Should().BeNull("the rate is salary-derived");
        row.CalculationJson.Should().NotContain("Hourly", "the breakdown carries the same salary-derived rates");
        row.ApprovedMinutes.Should().Be(120);
        row.Amount.Should().Be(325m);
        row.Multiplier.Should().Be(1.5m);
    }
}
