using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Tests.Compensation;
using static Zayra.Api.Tests.Compensation.MeritTestKit;

namespace Zayra.Api.Tests;

/// <summary>
/// The salary import's row validation, on real PostgreSQL (moved from the EF InMemory provider: the import is one
/// transaction under the retrying execution strategy, which InMemory cannot run, so production keeps no special path
/// for it). Nothing invalid is written.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PayrollSalaryImportPostgresTests(PostgresFixture fx)
{
    private const string Header = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance,FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate";

    private async Task<(int Created, int Skipped, List<string> Errors)> ImportAsync(Guid tenant, string rows)
    {
        await using var db = fx.CreateDb();
        var result = await Payroll(db, tenant).ImportEmployeeSalaries(new ImportSalaryStructuresRequest($"{Header}\n{rows}\n"), default);
        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        return (body.GetProperty("created").GetInt32(), body.GetProperty("skipped").GetInt32(),
            body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList());
    }

    private async Task<string> CodeAsync(MeritWorld w, string key)
    {
        await using var db = fx.CreateDb();
        return await db.Employees.AsNoTracking().Where(e => e.Id == w.E[key]).Select(e => e.EmployeeCode).SingleAsync();
    }

    private async Task<int> RowsOnAsync(Guid tenant, DateOnly date)
    {
        await using var db = fx.CreateDb();
        return await db.EmployeeSalaryStructures.IgnoreQueryFilters().CountAsync(s => s.TenantId == tenant && s.EffectiveDate == date);
    }

    [Fact]
    public async Task UnknownEmployeeCode_IsReportedAndNothingIsWritten()
    {
        var w = await SeedAsync(fx);
        var (created, skipped, errors) = await ImportAsync(w.Tenant, "GHOST001,GRADE-G1,5000,0,0,0,0,0,0,SAR,2027-05-01");
        (created, skipped).Should().Be((0, 1));
        errors.Should().ContainSingle().Which.Should().Contain("GHOST001");
        (await RowsOnAsync(w.Tenant, new DateOnly(2027, 5, 1))).Should().Be(0);
    }

    [Fact]
    public async Task NegativeBasicSalary_IsReportedAndNothingIsWritten()
    {
        var w = await SeedAsync(fx);
        var (created, skipped, errors) = await ImportAsync(w.Tenant, $"{await CodeAsync(w, "B5")},GRADE-G1,-500,0,0,0,0,0,0,SAR,2027-05-01");
        (created, skipped).Should().Be((0, 1));
        errors.Should().Contain(e => e.Contains("BasicSalary"));
        (await RowsOnAsync(w.Tenant, new DateOnly(2027, 5, 1))).Should().Be(0);
    }

    [Fact]
    public async Task MissingStructureCode_IsReported_AndNoRowWithAnEmptyStructureIsPersisted()
    {
        var w = await SeedAsync(fx);
        var (created, skipped, _) = await ImportAsync(w.Tenant, $"{await CodeAsync(w, "B5")},,5000,0,0,0,0,0,0,SAR,2027-05-01");
        (created, skipped).Should().Be((0, 1));
        await using var db = fx.CreateDb();
        (await db.EmployeeSalaryStructures.IgnoreQueryFilters().AnyAsync(s => s.TenantId == w.Tenant && s.SalaryStructureId == Guid.Empty)).Should().BeFalse();
    }

    [Fact]
    public async Task AnotherTenantsEmployeeCode_IsNotResolved()
    {
        var a = await SeedAsync(fx);
        var b = await SeedAsync(fx);
        var foreignCode = await CodeAsync(b, "B5");
        await using (var db = fx.CreateDb())
            (await db.Employees.IgnoreQueryFilters().AnyAsync(e => e.TenantId == a.Tenant && e.EmployeeCode == foreignCode)).Should().BeFalse("the code must exist only in tenant B for this test to mean anything");
        var (created, skipped, _) = await ImportAsync(a.Tenant, $"{foreignCode},GRADE-G1,5000,0,0,0,0,0,0,SAR,2027-05-01");
        (created, skipped).Should().Be((0, 1), "tenant A cannot resolve tenant B employee codes");
        (await RowsOnAsync(b.Tenant, new DateOnly(2027, 5, 1))).Should().Be(0);
    }
}
