using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// One edit that moves an employee to a new grade AND states a salary package: the package's range check is made against the grade the employee is moving
/// TO, not the grade they are leaving. Judged against the old grade, an employee promoted G1 → G5 on a G5-sized salary was refused as "outside grade G1's
/// salary range", nothing was saved, and the grade never changed.
/// </summary>
public class EmployeeGradeChangeSalaryRangeHttpTests : IClassFixture<EmployeeDraftCreateHttpFixture>
{
    private readonly EmployeeDraftCreateHttpFixture _fx;
    public EmployeeGradeChangeSalaryRangeHttpTests(EmployeeDraftCreateHttpFixture fixture) => _fx = fixture;

    // Whole pay = 14000 + 1000 + 400 + 0 + 0 + 0 = 15400.
    private static object Salary(string structure) => new
    {
        basicSalary = 14000m, housingAllowance = 1000m, transportAllowance = 400m, foodAllowance = 0m,
        mobileAllowance = 0m, otherAllowance = 0m, fixedDeduction = 0m,
        salaryStructureCode = structure, effectiveDate = "2026-01-01", currency = "SAR",
    };

    private async Task<(int EmployeeId, Grade Low, Grade High, string Structure)> SeedAsync(bool startOnHigh = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var structure = $"GRADE-RANGE-{suffix}";
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var low = new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = $"LO{suffix}", Name = $"Low {suffix}", Currency = "SAR", MinSalary = 1000m, MaxSalary = 3000m, IsActive = true };
        var high = new Grade { TenantId = _fx.TenantId, CompanyId = _fx.CompanyId, Code = $"HI{suffix}", Name = $"High {suffix}", Currency = "SAR", MinSalary = 10000m, MaxSalary = 20000m, IsActive = true };
        db.Grades.AddRange(low, high);
        db.SalaryStructures.Add(new SalaryStructure { TenantId = _fx.TenantId, Code = structure, Name = structure, Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1) });
        await db.SaveChangesAsync();

        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", new { englishName = $"Promoted {suffix}" });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var employee = await db.Employees.SingleAsync(e => e.Id == id);
        var start = startOnHigh ? high : low;
        employee.Grade = start.Code; employee.GradeId = start.Id;
        await db.SaveChangesAsync();
        return (id, low, high, structure);
    }

    [Fact]
    public async Task AGradeChangeInTheSameEdit_JudgesTheSalaryAgainstTheNewGrade_AndMovesTheGrade()
    {
        var (id, _, high, structure) = await SeedAsync();

        using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new
        {
            effectiveDate = "2026-01-01", changes = new { grade = high.Code, salaryBreakdown = Salary(structure) },
        });

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().NotBe(HttpStatusCode.UnprocessableEntity, text);
        response.IsSuccessStatusCode.Should().BeTrue(text);
        using var verify = _fx.Host.Services.CreateScope();
        var employee = await verify.ServiceProvider.GetRequiredService<ZayraDbContext>().Employees.AsNoTracking().SingleAsync(e => e.Id == id);
        employee.Grade.Should().Be(high.Code);
        employee.GradeId.Should().Be(high.Id, "the grade the user picked is the grade saved, not only its label");
    }

    [Fact]
    public async Task ASalaryOutsideTheNewGradesRange_IsStillRefused_EvenWhenItFitsTheOldGrade()
    {
        var (id, low, _, structure) = await SeedAsync(startOnHigh: true);

        using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new
        {
            effectiveDate = "2026-01-01", changes = new { grade = low.Code, salaryBreakdown = Salary(structure) },
        });

        // 15,400 is inside the grade the employee is leaving (10,000-20,000) but outside the one they are moving to (1,000-3,000).
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, text);
        text.Should().Contain($"outside grade {low.Code}");
    }
}
