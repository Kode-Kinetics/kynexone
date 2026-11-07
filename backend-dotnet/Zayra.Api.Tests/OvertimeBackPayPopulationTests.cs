using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Defence in depth for late overtime back pay carried into a Draft run at approval. PayrollRunProcessor loads adjustments only for the employees the run
/// pays, so an OvertimeBackPay adjustment on a run whose population has since dropped the employee (HR excluded them after the approval) would be left
/// attached, never paid, and unrecoverable (the prior-period finder reads PendingPayroll impacts only). Process returns such a request to PendingPayroll
/// and detaches the adjustment, so a later run that does pay the employee settles it.
/// </summary>
public class OvertimeBackPayPopulationTests
{
    private static async Task<(OvertimeRequest Request, OvertimePayrollImpact Impact, PayrollAdjustment Adjustment)> CarryAsync(
        Zayra.Api.Data.ZayraDbContext db, Guid tenantId, Guid companyId, Guid runId, Employee employee)
    {
        var day = new DateOnly(2026, 6, 10);
        var request = new OvertimeRequest
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeId = employee.Id, EmployeeName = employee.FullName, WorkDate = day,
            StartTimeUtc = day.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc), EndTimeUtc = day.ToDateTime(new TimeOnly(19, 0), DateTimeKind.Utc),
            RequestedMinutes = 120, ApprovedMinutes = 120, Status = "Approved",
        };
        var impact = new OvertimePayrollImpact
        {
            TenantId = tenantId, OvertimeRequestId = request.Id, EmployeeId = employee.Id, Minutes = 120, Amount = 150m,
            ApprovedMultiplier = 1.5m, Status = "CarriedForward",
        };
        var adjustment = new PayrollAdjustment
        {
            TenantId = tenantId, PayrollRunId = runId, EmployeeId = employee.Id, AdjustmentType = "Overtime Back Pay", Amount = 150m, Status = "Approved",
            Reason = "Overtime back pay", SourceType = PayrollAdjustmentSources.OvertimeBackPay, SourceId = request.Id, SourceSnapshotJson = "{}",
        };
        db.OvertimeRequests.Add(request); db.OvertimePayrollImpacts.Add(impact); db.PayrollAdjustments.Add(adjustment);
        await db.SaveChangesAsync();
        return (request, impact, adjustment);
    }

    [Fact]
    public async Task ACarriedBackPayAdjustment_OfAnEmployeeTheRunDoesNotPay_IsDetached_AndTheImpactReturnsToPendingPayroll()
    {
        var (db, conn) = PayrollProrationArrearsTests.CreateDb();
        await using var _ = db; await using var __ = conn;
        var tenantId = Guid.NewGuid();
        var (companyId, a, b) = await PayrollProrationArrearsTests.SeedAsync(db, tenantId);
        var run = PayrollProrationArrearsTests.AddRun(db, tenantId, companyId, 2026, 7);
        await db.SaveChangesAsync();
        var kept = await CarryAsync(db, tenantId, companyId, run.Id, a);
        var orphan = await CarryAsync(db, tenantId, companyId, run.Id, b);
        // HR excluded B from the run after the approval carried B's overtime into it.
        db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
        {
            TenantId = tenantId, CompanyId = companyId, PayrollRunId = run.Id, EmployeeId = b.Id, Mode = PayrollRunSelectionModes.Exclude, Reason = "on unpaid leave",
        });
        await db.SaveChangesAsync();

        (await PayrollProrationArrearsTests.Ctrl(db, tenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.PayrollAdjustments.AsNoTracking().AnyAsync(x => x.SourceId == orphan.Request.Id)).Should().BeFalse("detached from the run that does not pay B");
        var released = await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(x => x.Id == orphan.Impact.Id);
        released.Status.Should().Be("PendingPayroll");
        released.PayrollRunId.Should().BeNull();
        (await db.AuditLogs.AsNoTracking().AnyAsync(x => x.EntityId == orphan.Request.Id.ToString() && x.Action == "overtime.request.backpay_released_by_run")).Should().BeTrue();

        (await db.PayrollAdjustments.AsNoTracking().SingleAsync(x => x.SourceId == kept.Request.Id)).Status.Should().Be("Processed", "A is paid by the run");
        (await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(x => x.Id == kept.Impact.Id)).Status.Should().Be("CarriedForward");
        (await db.PayrollEarnings.AsNoTracking().AnyAsync(e => e.PayrollRunId == run.Id && e.EmployeeId == b.Id)).Should().BeFalse();
    }
}
