using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>Leave balance adjustments stay inside the caller's company, never touch the caller's own balance,
/// and need a reason and a sane amount.</summary>
public class LeaveBalanceAdjustScopeTests
{
    private static async Task<(ScopeSodWorld W, Guid LeaveType)> SeedAsync()
    {
        var w = await ScopeSodWorld.CreateAsync();
        await using var db = w.Plain();
        var lt = new LeaveType { TenantId = w.Tenant, Code = "AL", NameEn = "Annual", IsActive = true };
        db.LeaveTypes.Add(lt);
        await db.SaveChangesAsync();
        return (w, lt.Id);
    }

    /// <summary>Adjustments recorded for the world's employees, read through an unscoped context.</summary>
    private sealed class Probe(ScopeSodWorld w)
    {
        public List<int> Applied
        {
            get
            {
                using var db = w.Plain();
                return db.EmployeeLeaveBalances.AsNoTracking().Where(b => b.ManualAdjustment != 0).Select(b => b.EmployeeId).ToList();
            }
        }
    }

    private static (LeaveBalancesController C, Probe Svc) Ctl(ScopeSodWorld w, System.Security.Claims.ClaimsPrincipal p)
    {
        var db = w.DbFor(p);
        var svc = new Zayra.Api.Infrastructure.Leave.LeaveService(db, new Zayra.Api.Infrastructure.Approvals.ApprovalRouter(db));
        var c = ScopeSodWorld.Wire(new LeaveBalancesController(db, svc, new DataScopeService(db)), p);
        return (c, new Probe(w));
    }

    [Fact]
    public async Task CompanyAdmin_CannotAdjustAnotherCompanysEmployee()
    {
        var (w, lt) = await SeedAsync();
        var (c, svc) = Ctl(w, w.CompanyAdmin(w.CompanyA, permissions: "employees.read"));
        var r = await c.Adjust(new BalanceAdjustmentRequest(w.EmpB, lt, 5, "gift", null), default);
        r.Should().BeOfType<NotFoundObjectResult>();
        svc.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task CompanyAdmin_CanAdjustOwnCompanyEmployee()
    {
        var (w, lt) = await SeedAsync();
        var (c, svc) = Ctl(w, w.CompanyAdmin(w.CompanyA));
        (await c.Adjust(new BalanceAdjustmentRequest(w.EmpA, lt, 5, "carry over", null), default)).Should().BeOfType<OkObjectResult>();
        svc.Applied.Should().ContainSingle();
    }

    [Fact]
    public async Task GroupAdmin_CanAdjustEitherCompany()
    {
        var (w, lt) = await SeedAsync();
        var (c, svc) = Ctl(w, w.GroupAdmin());
        (await c.Adjust(new BalanceAdjustmentRequest(w.EmpB, lt, 5, "carry over", null), default)).Should().BeOfType<OkObjectResult>();
        svc.Applied.Should().ContainSingle();
    }

    [Fact]
    public async Task ForeignTenantAdmin_GetsNotFound()
    {
        var (w, lt) = await SeedAsync();
        var (c, svc) = Ctl(w, w.ForeignTenantAdmin());
        (await c.Adjust(new BalanceAdjustmentRequest(w.EmpA, lt, 5, "x", null), default)).Should().BeOfType<NotFoundObjectResult>();
        svc.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task Nobody_CanAdjustTheirOwnBalance()
    {
        var (w, lt) = await SeedAsync();
        var (c, svc) = Ctl(w, w.GroupAdmin(userId: w.UserA));
        var r = await c.Adjust(new BalanceAdjustmentRequest(w.EmpA, lt, 5, "top up myself", null), default);
        var bad = r.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("subject_cannot_decide");
        svc.Applied.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("  ", 5)]
    [InlineData("ok", 0)]
    [InlineData("ok", 999)]
    public async Task ReasonAndAmountAreValidated(string reason, double amount)
    {
        var (w, lt) = await SeedAsync();
        var (c, svc) = Ctl(w, w.GroupAdmin());
        (await c.Adjust(new BalanceAdjustmentRequest(w.EmpA, lt, (decimal)amount, reason, null), default)).Should().BeOfType<BadRequestObjectResult>();
        svc.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task Adjust_RefusesAnOldYear_AndAGrantThatPushesTheYearsTotalPastTheCap()
    {
        var (w, lt) = await SeedAsync();
        var (c, svc) = Ctl(w, w.GroupAdmin(permissions: "leave.write"));
        var thisYear = DateTime.UtcNow.Year;

        var old = await c.Adjust(new BalanceAdjustmentRequest(w.EmpA, lt, 5m, "late", thisYear - 5), default);
        old.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("year_out_of_range");

        // Grants up to the approval threshold apply at once; twelve of five days reach the 60-day yearly total.
        for (var i = 0; i < 12; i++)
            (await c.Adjust(new BalanceAdjustmentRequest(w.EmpA, lt, 5m, $"grant {i}", thisYear), default)).Should().BeOfType<OkObjectResult>();
        var over = await c.Adjust(new BalanceAdjustmentRequest(w.EmpA, lt, 5m, "grant 13", thisYear), default);
        over.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("cumulative_adjustment_exceeded");
    }
}
