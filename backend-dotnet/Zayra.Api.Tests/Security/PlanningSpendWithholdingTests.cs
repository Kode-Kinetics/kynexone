using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// F10 — the establishment and planning views show headcount to planners, but not salary spend.
///
/// <para>"Current monthly spend" is the sum of the department's salaries. The three views that show it
/// were open to organization.read or reports.read, so a Compliance Officer, a Recruiter or an HR
/// Assistant read it — and in a one-person department it IS that person's salary. It is now withheld
/// without payroll.read (or employees.sensitive, which already shows each salary on the employee
/// record): the field stays in the response, null, and a <c>withheld</c> note says why.</para>
/// </summary>
public sealed class PlanningSpendWithholdingTests
{
    private static readonly string[] Planner = ["organization.read"];
    private static readonly string[] PayrollPlanner = ["organization.read", "payroll.read"];

    [Fact]
    public async Task EstablishmentRows_WithholdSpend_WithoutPayrollRead()
    {
        await using var db = Db();
        var tid = await SeedOnePersonDepartmentAsync(db);

        var withheld = Rows(await Planning(db, tid, Planner).Establishment(default));
        Assert.Equal(JsonValueKind.Null, withheld[0].GetProperty("CurrentMonthlySpend").ValueKind);
        Assert.Equal(1, withheld[0].GetProperty("CurrentHeadcount").GetInt32());   // headcount is still shown

        var shown = Rows(await Planning(db, tid, PayrollPlanner).Establishment(default));
        Assert.Equal(12345m, shown[0].GetProperty("CurrentMonthlySpend").GetDecimal());
    }

    [Fact]
    public async Task WorkforceSummary_WithholdsSpendAndEverythingDerivedFromIt_WithoutPayrollRead()
    {
        await using var db = Db();
        var tid = await SeedOnePersonDepartmentAsync(db);

        var json = Json(await Planning(db, tid, Planner).WorkforceSummary(default));

        foreach (var field in new[] { "currentMonthlySpend", "budgetVariance", "overBudgetDepartments" })
            Assert.Equal(JsonValueKind.Null, json.GetProperty(field).ValueKind);
        Assert.Contains("payroll.read", json.GetProperty("withheld").GetRawText());
        Assert.Equal(1, json.GetProperty("totalCurrentHeadcount").GetInt32());

        var shown = Json(await Planning(db, tid, PayrollPlanner).WorkforceSummary(default));
        Assert.Equal(12345m, shown.GetProperty("currentMonthlySpend").GetDecimal());
        Assert.Empty(shown.GetProperty("withheld").EnumerateArray());
    }

    [Fact]
    public async Task EstablishmentMatrix_WithholdsSpend_WithoutPayrollRead()
    {
        await using var db = Db();
        var tid = await SeedOnePersonDepartmentAsync(db);

        var json = Json(await EstablishmentApiTests.CreateController(db, tid, Planner).Matrix(default));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("departments")[0].GetProperty("currentMonthlySpend").ValueKind);
        Assert.Contains("payroll.read", json.GetProperty("withheld").GetRawText());

        var shown = Json(await EstablishmentApiTests.CreateController(db, tid, PayrollPlanner).Matrix(default));
        Assert.Equal(12345m, shown.GetProperty("departments")[0].GetProperty("currentMonthlySpend").GetDecimal());
    }

    [Fact]
    public async Task Spend_IsShown_ToAHolderOfEmployeesSensitive()
    {
        // employees.sensitive already shows every salary on the employee record.
        await using var db = Db();
        var tid = await SeedOnePersonDepartmentAsync(db);

        var rows = Rows(await Planning(db, tid, "organization.read", "employees.sensitive").Establishment(default));

        Assert.Equal(12345m, rows[0].GetProperty("CurrentMonthlySpend").GetDecimal());
    }

    private static JsonElement Json(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private static JsonElement[] Rows(IActionResult result) => Json(result).EnumerateArray().ToArray();

    private static ZayraDbContext Db() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedOnePersonDepartmentAsync(ZayraDbContext db)
    {
        var tid = Guid.NewGuid();
        var dept = new Department { TenantId = tid, Code = "LEG", NameEn = "Legal", IsActive = true, ApprovedHeadcount = 1, MonthlyBudgetAmount = 10000 };
        db.Departments.Add(dept);
        db.Employees.Add(new Employee
        {
            TenantId = tid, EmployeeCode = "L-1", FullName = "Only Lawyer", Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-1), DepartmentId = dept.Id, Department = dept.NameEn, Salary = 12345m,
        });
        await db.SaveChangesAsync();
        return tid;
    }

    private static PlanningController Planning(ZayraDbContext db, Guid tid, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tid.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("is_group_scope", "true"),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new PlanningController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }
}
