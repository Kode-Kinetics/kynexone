using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The Leave screens' company / branch selector, honoured by every list and report it is sent to.
///
/// <para>THE DEFECT. All of these endpoints are reached from one screen with one group selector, and the API
/// client sends <c>companyId</c>/<c>branchId</c> on each. Several actions never declared the two parameters.
/// An undeclared query parameter is not an error in ASP.NET Core — it is dropped — so the encashment,
/// comp-off and absence registers, the balance summary, liability, usage, pending approvals, "on leave
/// today" and the calendar all answered a question about ONE company with EVERY company's rows, while the
/// selector on screen said otherwise. A group-scope user reading a single subsidiary's absence register got
/// the whole group's, with nothing to say so.</para>
///
/// <para>Each test seeds two companies and asks for one. Before the fix every one of them returned both.</para>
/// </summary>
public class LeaveGroupFilterTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Encashment_ReturnsOnlyTheSelectedCompany()
    {
        var w = await SeedAsync();
        w.Db.LeaveEncashmentRequests.AddRange(
            Encashment(w, w.AlphaEmployeeId), Encashment(w, w.BetaEmployeeId));
        await w.Db.SaveChangesAsync();

        var all = Items<LeaveEncashmentRequest>(await Encashments(w).List(null, null, null, null, null, 1, 25, Ct));
        all.Should().HaveCount(2, "with nothing selected the register is the whole group");

        var alpha = Items<LeaveEncashmentRequest>(
            await Encashments(w).List(null, null, null, w.AlphaCompanyId, null, 1, 25, Ct));
        alpha.Should().ContainSingle().Which.EmployeeId.Should().Be(w.AlphaEmployeeId);
    }

    [Fact]
    public async Task CompOff_ReturnsOnlyTheSelectedCompany()
    {
        var w = await SeedAsync();
        w.Db.CompOffCredits.AddRange(CompOff(w, w.AlphaEmployeeId), CompOff(w, w.BetaEmployeeId));
        await w.Db.SaveChangesAsync();

        Items<CompOffCredit>(await CompOffs(w).List(null, null, null, null, 1, 25, Ct)).Should().HaveCount(2);

        var alpha = Items<CompOffCredit>(await CompOffs(w).List(null, null, w.AlphaCompanyId, null, 1, 25, Ct));
        alpha.Should().ContainSingle().Which.EmployeeId.Should().Be(w.AlphaEmployeeId);
    }

    [Fact]
    public async Task Absences_ReturnOnlyTheSelectedCompany()
    {
        var w = await SeedAsync();
        await SeedAbsencesAsync(w);

        Items<AbsenceRecord>(await Absences(w).List(null, null, null, null, null, null, null, null, null, 1, 25, Ct))
            .Should().HaveCount(2);

        var alpha = Items<AbsenceRecord>(
            await Absences(w).List(null, null, null, null, null, null, null, w.AlphaCompanyId, null, 1, 25, Ct));
        alpha.Should().ContainSingle().Which.EmployeeId.Should().Be(w.AlphaEmployeeId);
    }

    [Fact]
    public async Task ABranchNarrowsFurtherThanItsCompany()
    {
        var w = await SeedAsync();
        await SeedAbsencesAsync(w);

        var branch = Items<AbsenceRecord>(
            await Absences(w).List(null, null, null, null, null, null, null, null, w.AlphaBranchId, 1, 25, Ct));
        branch.Should().ContainSingle().Which.EmployeeId.Should().Be(w.AlphaEmployeeId);
    }

    [Fact]
    public async Task ACompanyWithNobodyInIt_ReturnsNothing_NotEverything()
    {
        // The dangerous failure mode: an empty match read as "no filter". A selection that names nobody must
        // produce an empty register, never the whole group's.
        var w = await SeedAsync();
        await SeedAbsencesAsync(w);

        var empty = Items<AbsenceRecord>(
            await Absences(w).List(null, null, null, null, null, null, null, Guid.NewGuid(), null, 1, 25, Ct));
        empty.Should().BeEmpty();
    }

    [Fact]
    public async Task BalanceSummaryAndLiability_CountOnlyTheSelectedCompany()
    {
        var w = await SeedAsync();
        var leaveTypeId = Guid.NewGuid();
        foreach (var (employeeId, name) in new[] { (w.AlphaEmployeeId, "Alpha One"), (w.BetaEmployeeId, "Beta One") })
            w.Db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = w.TenantId, EmployeeId = employeeId, EmployeeName = name, LeaveTypeId = leaveTypeId,
                LeaveTypeName = "Annual", Year = DateTime.UtcNow.Year, Entitled = 30, Accrued = 30,
            });
        await w.Db.SaveChangesAsync();

        var summary = (System.Collections.IEnumerable)((OkObjectResult)await Reports(w)
            .BalanceSummary(null, w.AlphaCompanyId, null, Ct)).Value!;
        summary.Cast<object>().Should().ContainSingle("the balance summary is the selected company's");

        var liability = (OkObjectResult)await Reports(w).Liability(null, w.AlphaCompanyId, null, Ct);
        var details = (System.Collections.IEnumerable)liability.Value!.GetType()
            .GetProperty("details")!.GetValue(liability.Value)!;
        details.Cast<object>().Should().ContainSingle("a liability figure for one company must not carry the other's");
    }

    [Fact]
    public async Task OnLeaveToday_CountsOnlyTheSelectedCompany()
    {
        var w = await SeedAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var employeeId in new[] { w.AlphaEmployeeId, w.BetaEmployeeId })
            w.Db.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = w.TenantId, EmployeeId = employeeId, EmployeeName = $"E{employeeId}",
                Status = "Approved", StartDate = today, EndDate = today, TotalDays = 1,
            });
        await w.Db.SaveChangesAsync();

        var result = (OkObjectResult)await Reports(w).OnLeaveToday(w.AlphaCompanyId, null, Ct);
        result.Value!.GetType().GetProperty("count")!.GetValue(result.Value).Should().Be(1);
    }

    [Fact]
    public async Task TheCalendar_ShowsOnlyTheSelectedCompany()
    {
        var w = await SeedAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var employeeId in new[] { w.AlphaEmployeeId, w.BetaEmployeeId })
            w.Db.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = w.TenantId, EmployeeId = employeeId, EmployeeName = $"E{employeeId}",
                Status = "Approved", StartDate = today, EndDate = today, TotalDays = 1,
            });
        await w.Db.SaveChangesAsync();

        var entries = (System.Collections.IEnumerable)((OkObjectResult)await Calendar(w)
            .List(today, today, null, null, null, null, w.AlphaCompanyId, null, Ct)).Value!;
        entries.Cast<object>().Should().ContainSingle();
    }

    // ── The absence filter spellings ───────────────────────────────────────────────────────────

    /// <summary>
    /// The Absences screen has always sent <c>from</c>/<c>to</c>/<c>type</c>; the action declared
    /// <c>fromDate</c>/<c>toDate</c>/<c>absenceType</c>. Undeclared parameters are dropped, so those three
    /// filters had never narrowed anything: picking "Unauthorized" still listed every sick day.
    /// </summary>
    [Fact]
    public async Task TheAbsenceFilters_WorkUnderBothSpellings()
    {
        var w = await SeedAsync();
        var day = new DateOnly(2026, 3, 10);
        w.Db.AbsenceRecords.AddRange(
            new AbsenceRecord { TenantId = w.TenantId, EmployeeId = w.AlphaEmployeeId, AbsenceDate = day, AbsenceType = "Unauthorized" },
            new AbsenceRecord { TenantId = w.TenantId, EmployeeId = w.AlphaEmployeeId, AbsenceDate = day.AddDays(20), AbsenceType = "Sick" });
        await w.Db.SaveChangesAsync();

        // Canonical spelling.
        Items<AbsenceRecord>(await Absences(w).List(null, day, day, "Unauthorized", null, null, null, null, null, 1, 25, Ct))
            .Should().ContainSingle().Which.AbsenceType.Should().Be("Unauthorized");

        // What the screen actually sends.
        Items<AbsenceRecord>(await Absences(w).List(null, null, null, null, day, day, "Unauthorized", null, null, 1, 25, Ct))
            .Should().ContainSingle().Which.AbsenceType.Should().Be("Unauthorized");

        // The date window alone, in the alias spelling: the second absence is outside it.
        Items<AbsenceRecord>(await Absences(w).List(null, null, null, null, day, day.AddDays(1), null, null, null, 1, 25, Ct))
            .Should().ContainSingle();

        // Both sent: the canonical value wins, and nothing throws.
        Items<AbsenceRecord>(await Absences(w).List(null, null, null, "Sick", null, null, "Unauthorized", null, null, 1, 25, Ct))
            .Should().ContainSingle().Which.AbsenceType.Should().Be("Sick");
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private sealed record World(
        ZayraDbContext Db, Guid TenantId, Guid AlphaCompanyId, Guid BetaCompanyId, Guid AlphaBranchId,
        int AlphaEmployeeId, int BetaEmployeeId);

    private static async Task<World> SeedAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase($"leave-group-{Guid.NewGuid():N}").Options);
        var tenantId = Guid.NewGuid();
        var alpha = new Company { TenantId = tenantId, LegalNameEn = "Alpha Co", CountryCode = CountryCodes.Saudi, IsActive = true };
        var beta = new Company { TenantId = tenantId, LegalNameEn = "Beta Co", CountryCode = CountryCodes.Saudi, IsActive = true };
        db.AddRange(alpha, beta);
        await db.SaveChangesAsync();
        var alphaBranch = new Branch { TenantId = tenantId, CompanyId = alpha.Id, NameEn = "Alpha HQ", Code = "AHQ" };
        var betaBranch = new Branch { TenantId = tenantId, CompanyId = beta.Id, NameEn = "Beta HQ", Code = "BHQ" };
        db.AddRange(alphaBranch, betaBranch);
        await db.SaveChangesAsync();

        var alphaEmployee = new Employee
        {
            TenantId = tenantId, CompanyId = alpha.Id, BranchId = alphaBranch.Id, EmployeeCode = "A-1",
            FullName = "Alpha One", Status = "Active", Salary = 6_000m, JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        var betaEmployee = new Employee
        {
            TenantId = tenantId, CompanyId = beta.Id, BranchId = betaBranch.Id, EmployeeCode = "B-1",
            FullName = "Beta One", Status = "Active", Salary = 6_000m, JoiningDate = DateTime.UtcNow.AddYears(-2),
        };
        db.AddRange(alphaEmployee, betaEmployee);
        await db.SaveChangesAsync();

        return new World(db, tenantId, alpha.Id, beta.Id, alphaBranch.Id, alphaEmployee.Id, betaEmployee.Id);
    }

    private static async Task SeedAbsencesAsync(World w)
    {
        w.Db.AbsenceRecords.AddRange(
            new AbsenceRecord { TenantId = w.TenantId, EmployeeId = w.AlphaEmployeeId, AbsenceDate = new DateOnly(2026, 3, 2), AbsenceType = "Unauthorized" },
            new AbsenceRecord { TenantId = w.TenantId, EmployeeId = w.BetaEmployeeId, AbsenceDate = new DateOnly(2026, 3, 2), AbsenceType = "Unauthorized" });
        await w.Db.SaveChangesAsync();
    }

    private static LeaveEncashmentRequest Encashment(World w, int employeeId) => new()
    {
        TenantId = w.TenantId, EmployeeId = employeeId, EmployeeName = $"E{employeeId}",
        Year = DateTime.UtcNow.Year, DaysToEncash = 3, Status = "Pending",
    };

    private static CompOffCredit CompOff(World w, int employeeId) => new()
    {
        TenantId = w.TenantId, EmployeeId = employeeId, EmployeeName = $"E{employeeId}",
        WorkedDate = new DateOnly(2026, 3, 1), DaysEarned = 1, Status = "Available",
    };

    /// <summary>An organisation-wide caller: the group-scope user the selector exists for.</summary>
    private static T Bind<T>(T controller, World w) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", w.TenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim("permission", "employees.write"),
                    new Claim("is_group_scope", "true"),
                }, "Test")),
            },
        };
        return controller;
    }

    private static EncashmentController Encashments(World w) =>
        Bind(new EncashmentController(w.Db, new DataScopeService(w.Db), new NoRules()), w);

    private static CompOffController CompOffs(World w) =>
        Bind(new CompOffController(w.Db, new DataScopeService(w.Db)), w);

    private static AbsenceController Absences(World w) =>
        Bind(new AbsenceController(w.Db, new DataScopeService(w.Db)), w);

    private static LeaveReportsController Reports(World w) =>
        Bind(new LeaveReportsController(w.Db, new DataScopeService(w.Db)), w);

    private static LeaveCalendarController Calendar(World w) =>
        Bind(new LeaveCalendarController(w.Db, new DataScopeService(w.Db)), w);

    private static IReadOnlyList<T> Items<T>(IActionResult result) =>
        ((PagedResult<T>)((OkObjectResult)result).Value!).Items.ToList();

    private sealed class NoRules : IStatutoryRuleReader
    {
        public Task<decimal?> GetDecimalAsync(string countryCode, string jurisdiction, string ruleKey, DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<decimal?>(null);
        public Task<string?> GetStringAsync(string countryCode, string jurisdiction, string ruleKey, DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }
}
