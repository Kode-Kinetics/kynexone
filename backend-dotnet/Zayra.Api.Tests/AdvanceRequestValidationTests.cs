using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.AdvanceTestKit;
using static Zayra.Api.Tests.AdvancePayoutLifecycleTests;

namespace Zayra.Api.Tests;

/// <summary>
/// Audit P3 and decision G1: the repayment start date is bounded (not before yesterday, at most twelve months out); the employee
/// must be serving (Active and not on notice, the loan module's default eligibility) at request, approval and payout; and the
/// advance number is allocated after the highest number already used in the tenant, imported and deleted ones included.
/// </summary>
public class AdvanceRequestValidationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly string[] Hr = { "HR Manager" };

    private static CreateAdvanceRequest Request(Fx f, decimal amount, int installments = 2, DateOnly? start = null) =>
        new(f.EmployeePublicId, "Huda Saleh", amount, "Installments", installments, start, "family", f.EmployeeId);

    // ── Item 8: the repayment start date ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-2)]     // before yesterday
    [InlineData(-40)]
    public async Task Create_WithARepaymentStartInThePast_IsRefused(int days)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 1_000m, start: Today.AddDays(days)), Ct),
            "advance_repayment_start_invalid");
        Assert.Empty(await db.SalaryAdvances.ToListAsync());
    }

    [Fact]
    public async Task Create_WithARepaymentStartMoreThanTwelveMonthsOut_IsRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 1_000m, start: Today.AddMonths(12).AddDays(1)), Ct),
            "advance_repayment_start_invalid");
    }

    [Theory]
    [InlineData(-1)]   // yesterday: a request typed late in the day in another timezone
    [InlineData(0)]
    [InlineData(365)]
    public async Task Create_WithARepaymentStartInsideTheWindow_IsAccepted(int days)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var start = days == 365 ? Today.AddMonths(12) : Today.AddDays(days);

        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 1_000m, start: start), Ct));
    }

    [Fact]
    public async Task Approve_WithARepaymentStartOutsideTheWindow_IsRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var id = await SeedPendingAsync(db, f, 1_000m);

        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Approve(id, new AdvanceApproveRequest(1_000m, 2, Today.AddDays(-5)), Ct),
            "advance_repayment_start_invalid");
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Approve(id, new AdvanceApproveRequest(1_000m, 2, Today.AddMonths(13)), Ct),
            "advance_repayment_start_invalid");
        Assert.Equal("Pending", (await db.SalaryAdvances.AsNoTracking().SingleAsync()).Status);
    }

    // ── Item 8 / G1: the employee must be serving ───────────────────────────────────────────────────

    [Theory]
    [InlineData(EmployeeStatuses.Terminated)]
    [InlineData(EmployeeStatuses.Offboarded)]
    [InlineData("Inactive")]
    public async Task Create_ForAnEmployeeWhoIsNotServing_IsRefused(string status)
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        (await db.Employees.SingleAsync()).Status = status;
        await db.SaveChangesAsync();

        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 1_000m), Ct), "advance_employee_not_eligible");
        Assert.Empty(await db.SalaryAdvances.ToListAsync());
    }

    [Fact]
    public async Task Create_ForAnActiveEmployeeServingNotice_IsRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = f.EmployeeId, EmployeeCode = "ADV-001", EmployeeName = "Huda Saleh",
            SeparationType = "Resignation", Status = "InProgress", NoticeDate = Today, NoticePeriodDays = 30, LastWorkingDay = Today.AddDays(30),
        });
        await db.SaveChangesAsync();

        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 1_000m), Ct), "advance_employee_not_eligible");
    }

    [Fact]
    public async Task Approve_AndPayout_ForAnEmployeeWhoStoppedServing_AreRefused()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var pending = await SeedPendingAsync(db, f, 1_000m);
        var approved = await SeedPendingAsync(db, f, 1_000m);
        Assert.IsType<OkObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Approve(approved, new AdvanceApproveRequest(1_000m, 2, null), Ct));
        db.ChangeTracker.Clear();
        (await db.Employees.SingleAsync()).Status = EmployeeStatuses.Terminated;
        await db.SaveChangesAsync();

        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), Hr).Approve(pending, new AdvanceApproveRequest(1_000m, 2, null), Ct),
            "advance_employee_not_eligible");
        AssertRefusal<BadRequestObjectResult>(await Advances(db, f, Guid.NewGuid(), new[] { "Finance" })
            .Payout(approved, new AdvancePayoutRequest(null, "BANK-1", null), Ct), "advance_employee_not_eligible");
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
    }

    // ── Item 8: the advance number ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_AllocatesTheNumberAfterTheHighestAlreadyUsed_IncludingImportedAndDeletedOnes()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var year = DateTime.UtcNow.Year;
        foreach (var (number, deleted) in new[] { ($"ADV-{year}-00007", false), ($"ADV-{year}-00012", true), ($"ADV-{year - 1}-00090", false), ("IMPORTED-55", false) })
        {
            var id = await SeedPendingAsync(db, f, 100m);
            var adv = await db.SalaryAdvances.SingleAsync(x => x.Id == id);
            adv.AdvanceNumber = number; adv.IsDeleted = deleted; adv.Status = "Rejected";
        }
        await db.SaveChangesAsync();

        var result = await Advances(db, f, Guid.NewGuid(), Hr).Create(Request(f, 1_000m), Ct);

        var dto = Assert.IsType<Zayra.Api.Application.Finance.SalaryAdvanceDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal($"ADV-{year}-00013", dto.AdvanceNumber);
    }
}
