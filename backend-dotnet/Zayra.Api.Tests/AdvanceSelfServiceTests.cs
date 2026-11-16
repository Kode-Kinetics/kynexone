using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;
using static Zayra.Api.Tests.AdvanceTestKit;
using static Zayra.Api.Tests.AdvancePayoutLifecycleTests;

namespace Zayra.Api.Tests;

/// <summary>
/// Self-service: an employee who holds only <c>loans.self</c> lists and reads their own salary advances with <c>mine=true</c>,
/// and nothing else: another employee's advance is refused by id, and without <c>mine=true</c> the loan book stays closed (403
/// with a reason). Cancelling one's own Pending advance is covered by <see cref="AdvanceCancelTests"/>. Holders of loans.read or
/// loans.write keep the existing behaviour.
/// </summary>
public class AdvanceSelfServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task ASelfServiceEmployee_ListsOnlyTheirOwnAdvances_WithMine()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (_, colleagueAdvance) = await SeedColleagueAdvanceAsync(db, f);
        var own = await SeedPendingAsync(db, f, 1_000m);

        var result = await Self(db, f).List(null, null, 1, 30, Ct, mine: true);

        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        var ids = body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("Id").GetGuid()).ToList();
        Assert.Equal(new[] { own }, ids);
        Assert.DoesNotContain(colleagueAdvance, ids);
        Assert.Equal(1, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ASelfServiceEmployee_WithoutMine_IsRefusedTheBook_WithAReason()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        await SeedPendingAsync(db, f, 1_000m);

        var list = await Self(db, f).List(null, null, 1, 30, Ct);
        var get = await Self(db, f).Get((await db.SalaryAdvances.AsNoTracking().SingleAsync()).Id, Ct);

        ReportDataAuthorizationTests.AssertForbiddenWithReason(list, "permission");
        ReportDataAuthorizationTests.AssertForbiddenWithReason(get, "permission");
    }

    [Fact]
    public async Task ASelfServiceEmployee_ReadsTheirOwnAdvanceById_ButNotAColleagues()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var (_, colleagueAdvance) = await SeedColleagueAdvanceAsync(db, f);
        var own = await SeedPendingAsync(db, f, 1_000m);

        var mine = await Self(db, f).Get(own, Ct, mine: true);
        var theirs = await Self(db, f).Get(colleagueAdvance, Ct, mine: true);

        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(mine).Value);
        Assert.Equal(own, body.GetProperty("advance").GetProperty("Id").GetGuid());
        Assert.Equal(0, body.GetProperty("glEntries").GetArrayLength());
        Assert.IsType<ForbidResult>(theirs);
    }

    [Fact]
    public async Task ABookReader_KeepsTheExistingList_AndMineNarrowsItToTheirOwnRecord()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        await SeedColleagueAdvanceAsync(db, f);
        await SeedPendingAsync(db, f, 1_000m);

        var book = await Advances(db, f, Guid.NewGuid(), new[] { "HR Manager" }, perms: new[] { "loans.read" }).List(null, null, 1, 30, Ct);
        var hrMine = await Advances(db, f, Guid.NewGuid(), new[] { "HR Manager" }, perms: new[] { "loans.read" }).List(null, null, 1, 30, Ct, mine: true);

        Assert.Equal(2, JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(book).Value).GetProperty("total").GetInt32());
        Assert.Equal(0, JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(hrMine).Value).GetProperty("total").GetInt32());
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The employee's own login: loans.self only, the employee_id claim the token service issues, and a data scope that
    /// is the employee's own record (or the whole organisation, to prove the body does not rely on the scope alone).</summary>
    private static AdvancesController Self(ZayraDbContext db, Fx f, bool scopeToSelf = true)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", f.TenantId.ToString()), new(ClaimTypes.NameIdentifier, f.EmployeeUserId.ToString()),
            new(ClaimTypes.Name, "Huda Saleh"), new(ClaimTypes.Role, "Employee"), new("permission", "loans.self"),
            new("employee_id", f.EmployeeId.ToString()),
        };
        return new AdvancesController(db, new FixedScope(scopeToSelf ? new[] { f.EmployeeId } : null))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }

    private static async Task<(int ColleagueId, Guid AdvanceId)> SeedColleagueAdvanceAsync(ZayraDbContext db, Fx f)
    {
        var colleague = new Employee
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeCode = "ADV-002", FullName = "Omar Haddad",
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-3),
        };
        db.Employees.Add(colleague);
        await db.SaveChangesAsync();
        var adv = new SalaryAdvance
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = colleague.PublicId, EmployeeIntId = colleague.Id,
            EmployeeName = colleague.FullName, AdvanceNumber = $"ADV-C-{Guid.NewGuid():N}"[..14], RequestedAmount = 700m,
            RepaymentType = "Installments", Installments = 1, Status = "Pending", CreatedBy = Guid.NewGuid(),
        };
        db.SalaryAdvances.Add(adv);
        await db.SaveChangesAsync();
        return (colleague.Id, adv.Id);
    }
}
