using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Controllers.Compliance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// M4: a contract's basic salary is as sensitive as the employee's salary. HR without employees.sensitive gets the contract
/// with the pay withheld on EVERY response that returns it: list, get, create and status change, not only the first two.
/// </summary>
public class ContractSalaryMaskingTests
{
    private static ContractsController Ctl(ScopeSodWorld w, bool sensitive)
    {
        var permissions = sensitive ? new[] { "employees.sensitive" } : Array.Empty<string>();
        var principal = w.GroupAdmin(Guid.NewGuid(), permissions);
        return ScopeSodWorld.Wire(new ContractsController(w.DbFor(principal)), principal);
    }

    private static decimal? SalaryOf(IActionResult r) => r switch
    {
        OkObjectResult { Value: EmployeeContract c } => c.BasicSalary,
        CreatedAtActionResult { Value: EmployeeContract c } => c.BasicSalary,
        _ => null,
    };

    private static async Task<(ScopeSodWorld W, Guid Employee, Guid ContractId)> SeedAsync()
    {
        var w = await ScopeSodWorld.CreateAsync();
        await using var db = w.Plain();
        var employee = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.EmpA);
        var contract = new EmployeeContract
        {
            TenantId = w.Tenant, CompanyId = w.CompanyA, EmployeeId = employee.PublicId, EmployeeName = employee.FullName, ContractNumber = "CON-1",
            StartDate = new DateOnly(2026, 1, 1), BasicSalary = 9_000m, CurrencyCode = "SAR", Status = "Draft",
        };
        db.EmployeeContracts.Add(contract);
        await db.SaveChangesAsync();
        return (w, employee.PublicId, contract.Id);
    }

    [Fact]
    public async Task Get_WithholdsPay_WithoutSensitiveAccess_AndShowsItWith()
    {
        var (w, _, id) = await SeedAsync();
        SalaryOf(await Ctl(w, sensitive: false).Get(id, default)).Should().Be(0m);
        SalaryOf(await Ctl(w, sensitive: true).Get(id, default)).Should().Be(9_000m);
        await using var check = w.Plain();
        (await check.EmployeeContracts.AsNoTracking().SingleAsync(c => c.Id == id)).BasicSalary.Should().Be(9_000m, "only the response copy is masked");
    }

    [Fact]
    public async Task Create_ResponseWithholdsPay_ButTheStoredContractKeepsIt()
    {
        var (w, employeePublicId, _) = await SeedAsync();
        var r = await Ctl(w, sensitive: false).Create(new CreateContractRequest(employeePublicId, null, null, "Employment", new DateOnly(2027, 1, 1), null, 12_345m, "SAR", null, null, null), default);
        SalaryOf(r).Should().Be(0m);
        await using var check = w.Plain();
        (await check.EmployeeContracts.AsNoTracking().Where(c => c.StartDate == new DateOnly(2027, 1, 1)).SingleAsync()).BasicSalary.Should().Be(12_345m);
    }

    [Fact]
    public async Task StatusChange_ResponseWithholdsPay_WithoutSensitiveAccess()
    {
        var (w, _, id) = await SeedAsync();
        var r = await Ctl(w, sensitive: false).UpdateStatus(id, new UpdateContractStatusRequest("PendingApproval", null), default);
        r.Should().BeOfType<OkObjectResult>();
        SalaryOf(r).Should().Be(0m);
    }

    [Fact]
    public async Task List_WithholdsPayOnEveryItem()
    {
        var (w, _, _) = await SeedAsync();
        var r = await Ctl(w, sensitive: false).List(null, null, 1, 20, default);
        var body = System.Text.Json.JsonSerializer.Serialize(r.Should().BeOfType<OkObjectResult>().Subject.Value);
        body.Should().NotContain("9000");
    }
}
