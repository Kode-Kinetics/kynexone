using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.PayNext;

namespace Zayra.Api.Tests;

/// <summary>
/// Spec M1-M3, report only: existing single-row unpaid-leave deductions that span more than one month are listed with how
/// the slice model would split them (the original amount shared by days, the last month taking the cent remainder), and
/// any that do not reconcile are listed apart. Processed rows and sick-leave scale rows are listed, never split. The report
/// changes no row.
/// </summary>
public class LeaveSliceSplitReportTests
{
    private static async Task<LeaveRequest> SingleRow(Zayra.Api.Data.ZayraDbContext db, Co co, int employeeId, DateOnly start, DateOnly end,
        decimal days, decimal amount, string status, string impactType = CompanyPayrollRules.UnpaidLeaveImpactType, string requestStatus = "Approved")
    {
        var leave = new LeaveRequest
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeId = employeeId, StartDate = start, EndDate = end,
            TotalDays = days, Status = requestStatus, DayType = "Full",
        };
        db.LeaveRequests.Add(leave);
        db.LeavePayrollImpacts.Add(new LeavePayrollImpact
        {
            TenantId = co.TenantId, LeaveRequestId = leave.Id, EmployeeId = employeeId, PayPeriod = $"{start.Year}-{start.Month:00}",
            ImpactType = impactType, Days = days, Amount = amount, Status = status,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return leave;
    }

    private static async Task<System.Text.Json.JsonElement> Report(Zayra.Api.Data.ZayraDbContext db, Co co)
    {
        var leaves = await TestApprovalConfig.LeaveServiceAsync(db, co.TenantId, "HR");
        var http = Ctx(co.TenantId, Checker);
        var controller = new LeaveSliceSplitReportController(leaves, new PnScope())
        { ControllerContext = new ControllerContext { HttpContext = http } };
        var result = await controller.Get(CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        return Json(result);
    }

    [Fact]
    public async Task M1_APendingMultiMonthRow_IsListedWithItsSplit_SummingToTheOriginalAmount()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        var leave = await SingleRow(db, co, e.Id, new DateOnly(2026, 1, 25), new DateOnly(2026, 2, 5), 10m, 2_000.01m, "Pending");

        var root = await Report(db, co);

        root.GetProperty("readOnly").GetBoolean().Should().BeTrue();
        var row = root.GetProperty("wouldSplit").EnumerateArray().Should().ContainSingle().Subject;
        row.GetProperty("leaveRequestId").GetGuid().Should().Be(leave.Id);
        var parts = row.GetProperty("parts").EnumerateArray()
            .Select(p => (p.GetProperty("payPeriod").GetString(), p.GetProperty("days").GetDecimal(), p.GetProperty("amount").GetDecimal())).ToList();
        parts.Should().Equal(("2026-01", 5m, 1_000.00m), ("2026-02", 5m, 1_000.01m));
        root.GetProperty("doNotReconcile").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task M2_M3_ProcessedRowsAndSickScaleRows_AreListedApart_AndADayMismatchIsListedAsNotReconciling()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        await SingleRow(db, co, e.Id, new DateOnly(2026, 1, 25), new DateOnly(2026, 2, 5), 10m, 2_000m, "Processed");
        await SingleRow(db, co, e.Id, new DateOnly(2026, 3, 25), new DateOnly(2026, 4, 5), 20m, 4_000m, "Pending");
        await SingleRow(db, co, e.Id, new DateOnly(2026, 5, 25), new DateOnly(2026, 6, 5), 4m, 800m, "Pending",
            impactType: "Leave Deduction (KSA Art.117 sick-leave scale: 4 day(s) at 0%)");
        await SingleRow(db, co, e.Id, new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 9), 5m, 1_000m, "Pending");

        var root = await Report(db, co);

        root.GetProperty("alreadyDeducted").GetArrayLength().Should().Be(1);
        root.GetProperty("doNotReconcile").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("days").GetDecimal().Should().Be(20m, "25 March - 5 April holds at most 12 days, whatever the week");
        root.GetProperty("sickScaleRows").GetArrayLength().Should().Be(1);
        root.GetProperty("wouldSplit").GetArrayLength().Should().Be(0, "a leave inside one month is not a split candidate");
    }

    [Fact]
    public async Task TheReport_ChangesNoRow_AndWritesNoAuditEntry()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        await SingleRow(db, co, e.Id, new DateOnly(2026, 1, 25), new DateOnly(2026, 2, 5), 10m, 2_000m, "Pending");
        await SingleRow(db, co, e.Id, new DateOnly(2026, 3, 25), new DateOnly(2026, 4, 5), 20m, 4_000m, "Pending");
        var before = await db.LeavePayrollImpacts.AsNoTracking().OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.PayPeriod, i.Days, i.Amount, i.Status, i.SliceFrom }).ToListAsync();
        var auditsBefore = await db.LeaveAuditLogs.CountAsync();

        await Report(db, co);

        (await db.LeavePayrollImpacts.AsNoTracking().OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.PayPeriod, i.Days, i.Amount, i.Status, i.SliceFrom }).ToListAsync()).Should().BeEquivalentTo(before);
        (await db.LeaveAuditLogs.CountAsync()).Should().Be(auditsBefore);
    }

    [Fact]
    public void TheReport_IsAGetBehindPayrollRead()
    {
        var method = typeof(LeaveSliceSplitReportController).GetMethod(nameof(LeaveSliceSplitReportController.Get))!;
        method.GetCustomAttributes<HasPermissionAttribute>().Single().Permissions.Should().Equal("payroll.read");
        method.GetCustomAttributes<HttpGetAttribute>().Should().ContainSingle();
        typeof(LeaveSliceSplitReportController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().Any(a => a is HttpPostAttribute or HttpPutAttribute or HttpDeleteAttribute or HttpPatchAttribute))
            .Should().BeEmpty("the report changes nothing");
    }
}
