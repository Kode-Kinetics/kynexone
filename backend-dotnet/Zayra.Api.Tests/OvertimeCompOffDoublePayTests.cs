using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;
using static Zayra.Api.Tests.OvertimeFixKit;

namespace Zayra.Api.Tests;

/// <summary>
/// OVT-01 (P0): an approved overtime request that still has a PendingPayroll impact could be converted to
/// comp-off WITHOUT voiding the cash, and the day count was unbounded - the same overtime paid twice, once
/// as money and once as leave. Conversion must (1) cap the days at the approved hours over the working day,
/// (2) withdraw the unpaid cash atomically (refusing when payroll already paid it), (3) never leave a
/// comp-off credit and a payable impact for one request, and (4) never be done by the subject.
/// </summary>
public class OvertimeCompOffDoublePayTests
{
    private static readonly string[] Adm = { "Admin" };

    private static async Task<(World W, OvertimeRequest R)> ApprovedWithPendingImpactAsync(int approvedMinutes = 480, string impactStatus = "PendingPayroll", Guid? runId = null)
    {
        var w = await SeedAsync();
        var r = await AddRequestAsync(w, status: "Approved", minutes: approvedMinutes, approvedMinutes: approvedMinutes);
        w.Db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact
        {
            TenantId = w.TenantId, OvertimeRequestId = r.Id, EmployeeId = r.EmployeeId, Minutes = approvedMinutes, Amount = 1250m,
            ApprovedMultiplier = 1.5m, Status = impactStatus, PayrollRunId = runId,
        });
        await w.Db.SaveChangesAsync();
        return (w, r);
    }

    [Fact]
    public async Task Conversion_WithdrawsThePendingCash_SoTheRequestIsNeverPaidTwice()
    {
        var (w, r) = await ApprovedWithPendingImpactAsync();

        var result = await Controller(w, Guid.NewGuid(), Adm).CreateCompOffConversion(new CompOffConversionRequest(r.Id, 1m), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedResult>();
        (await w.Db.CompOffCredits.CountAsync(x => x.EmployeeId == r.EmployeeId)).Should().Be(1);
        (await w.Db.OvertimePayrollImpacts.CountAsync(x => x.OvertimeRequestId == r.Id && x.Status == "PendingPayroll"))
            .Should().Be(0, "a request converted to time off must not also be payable");
        (await w.Db.OvertimeAuditLogs.AnyAsync(x => x.Action == "overtime.compoff.created")).Should().BeTrue();
    }

    [Fact]
    public async Task Conversion_IsRefused_WhenPayrollAlreadyPaidTheOvertime()
    {
        var runId = Guid.NewGuid();
        var (w, r) = await ApprovedWithPendingImpactAsync(impactStatus: "Processed", runId: runId);

        var result = await Controller(w, Guid.NewGuid(), Adm).CreateCompOffConversion(new CompOffConversionRequest(r.Id, 1m), CancellationToken.None);

        result.Result.Should().Match<IActionResult>(x => x is BadRequestObjectResult || x is ConflictObjectResult);
        (await w.Db.CompOffCredits.CountAsync()).Should().Be(0);
        (await w.Db.OvertimeCompOffConversions.CountAsync()).Should().Be(0);
        (await w.Db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(x => x.OvertimeRequestId == r.Id)).Status.Should().Be("Processed");
    }

    [Theory]
    [InlineData(480, 1.01)]   // 8 h approved = 1 working day
    [InlineData(480, 30)]
    [InlineData(120, 30)]     // 2 h of overtime never buys a month off
    [InlineData(120, 1.01)]   // part-day rounds up to ONE day, no more
    [InlineData(600, 2.01)]   // 10 h = 1.25 days -> 2
    public async Task Conversion_IsRefused_WhenTheDaysExceedTheApprovedHoursOverTheWorkingDay(int approvedMinutes, double days)
    {
        var (w, r) = await ApprovedWithPendingImpactAsync(approvedMinutes);

        var result = await Controller(w, Guid.NewGuid(), Adm).CreateCompOffConversion(new CompOffConversionRequest(r.Id, (decimal)days), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.CompOffCredits.CountAsync()).Should().Be(0);
        (await w.Db.OvertimeCompOffConversions.CountAsync()).Should().Be(0);
        (await w.Db.OvertimePayrollImpacts.CountAsync(x => x.OvertimeRequestId == r.Id && x.Status == "PendingPayroll"))
            .Should().Be(1, "a refused conversion must leave the cash exactly as it was");
    }

    [Theory]
    [InlineData(480, 1.0)]
    [InlineData(120, 1.0)]
    [InlineData(120, 0.25)]
    [InlineData(600, 2.0)]
    public async Task Conversion_AtTheBound_IsAccepted(int approvedMinutes, double days)
    {
        var (w, r) = await ApprovedWithPendingImpactAsync(approvedMinutes);

        var result = await Controller(w, Guid.NewGuid(), Adm).CreateCompOffConversion(new CompOffConversionRequest(r.Id, (decimal)days), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task TheSubject_CannotConvertTheirOwnOvertime_AndTheCashIsUntouched()
    {
        var (w, r) = await ApprovedWithPendingImpactAsync();

        var result = await Controller(w, w.SubjectUserId, Adm).CreateCompOffConversion(new CompOffConversionRequest(r.Id, 1m), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await w.Db.CompOffCredits.CountAsync()).Should().Be(0);
        (await w.Db.OvertimePayrollImpacts.CountAsync(x => x.OvertimeRequestId == r.Id && x.Status == "PendingPayroll")).Should().Be(1);
    }

    [Fact]
    public async Task Conversion_OfARequestWithNoImpact_StillWorks()
    {
        var w = await SeedAsync();
        var r = await AddRequestAsync(w, status: "Approved", minutes: 480, approvedMinutes: 480);

        var result = await Controller(w, Guid.NewGuid(), Adm).CreateCompOffConversion(new CompOffConversionRequest(r.Id, 1m), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedResult>();
    }
}
