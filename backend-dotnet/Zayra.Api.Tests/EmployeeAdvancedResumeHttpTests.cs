using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

public class EmployeeAdvancedResumeHttpTests : IClassFixture<EmployeeDraftCreateHttpFixture>
{
    private readonly EmployeeDraftCreateHttpFixture _fx;
    public EmployeeAdvancedResumeHttpTests(EmployeeDraftCreateHttpFixture fixture) => _fx = fixture;
    private static object Salary(decimal basic = 4000m, string effectiveDate = "2026-01-01") => new
    {
        basicSalary = basic, housingAllowance = 1000m, transportAllowance = 400m, foodAllowance = 100m,
        mobileAllowance = 50m, otherAllowance = 200m, fixedDeduction = 25m,
        salaryStructureCode = "RESUME", effectiveDate, currency = "SAR",
    };

    private async Task<int> CreateAsync()
    {
        using var response = await _fx.SendAsync(HttpMethod.Post, "/api/employees", new
        {
            englishName = $"Resume {Guid.NewGuid():N}", salaryBreakdown = Salary(),
            payrollProfile = new { salaryCurrency = "SAR", paymentMethod = "Cash", payrollGroup = "Old", molId = "OLD" },
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("salaryBreakdown").GetProperty("housingAllowance").GetDecimal().Should().Be(1000m);
        return body.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task AdvancedFieldsResumeThroughTheSameApprovalAndReloadContract()
    {
        var id = await CreateAsync();
        using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new
        {
            effectiveDate = "2026-01-01",
            changes = new { salaryBreakdown = Salary(5000), salaryCurrency = "USD", paymentMethod = "BankTransfer",
                payrollGroup = "Monthly", salaryStructureReference = "Reviewed", molId = "MOL-42" },
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var changeId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changeRequestId").GetGuid();
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            (await db.EmployeeSalaryStructures.SingleAsync(s => s.EmployeeId == id && s.IsActive)).BasicSalary.Should().Be(4000);
            (await db.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == id)).PaymentMethod.Should().Be("Cash");
            (await ApprovalCenterDriver.ApproveChangeAsync(db, _fx.TenantId, changeId)).Status.Should().Be("Approved");
        }
        using var detail = await _fx.SendAsync(HttpMethod.Get, $"/api/employees/{id}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK, await detail.Content.ReadAsStringAsync());
        var saved = await detail.Content.ReadFromJsonAsync<JsonElement>();
        var salary = saved.GetProperty("salaryBreakdown");
        salary.GetProperty("basicSalary").GetDecimal().Should().Be(5000);
        salary.GetProperty("housingAllowance").GetDecimal().Should().Be(1000);
        salary.GetProperty("fixedDeduction").GetDecimal().Should().Be(25);
        salary.GetProperty("salaryStructureCode").GetString().Should().Be("RESUME");
        salary.GetProperty("effectiveDate").GetString().Should().Be("2026-01-01");
        var payroll = saved.GetProperty("payrollProfile");
        payroll.GetProperty("salaryCurrency").GetString().Should().Be("USD");
        payroll.GetProperty("paymentMethod").GetString().Should().Be("BankTransfer");
        payroll.GetProperty("payrollGroup").GetString().Should().Be("Monthly");
        payroll.GetProperty("salaryStructureReference").GetString().Should().Be("Reviewed");
        payroll.GetProperty("molId").GetString().Should().Be("MOL-42");
        saved.GetProperty("wpsBankDetails").GetString().Should().Be("BankTransfer");
        saved.GetProperty("salary").GetDecimal().Should().Be(6750);
    }

    [Fact]
    public async Task SalaryAndPayrollRequireSensitivePermissionAndSalaryProjectionIsMasked()
    {
        var id = await CreateAsync();
        using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new
        {
            effectiveDate = "2026-01-01", changes = new { salaryBreakdown = Salary(5000), paymentMethod = "BankTransfer" },
        }, _fx.EditorToken, useWriter: false);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        using var detail = await _fx.SendAsync(HttpMethod.Get, $"/api/employees/{id}", token: _fx.EditorToken, useWriter: false);
        detail.StatusCode.Should().Be(HttpStatusCode.OK, await detail.Content.ReadAsStringAsync());
        var json = await detail.Content.ReadFromJsonAsync<JsonElement>();
        (!json.TryGetProperty("salaryBreakdown", out var salary) || salary.ValueKind == JsonValueKind.Null).Should().BeTrue();
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("negative")]
    [InlineData("both")]
    [InlineData("overflow")]
    [InlineData("gross-limit")]
    [InlineData("fractional-cent")]
    [InlineData("date-mismatch")]
    [InlineData("future-mixed")]
    public async Task InvalidSalaryCannotQueueApprovalOrPartiallyApplyOrdinaryFields(string mode)
    {
        var id = await CreateAsync();
        var changes = new Dictionary<string, object?> { ["englishName"] = "Must not be applied", ["salaryBreakdown"] = Salary() };
        if (mode == "incomplete") changes["salaryBreakdown"] = new { basicSalary = 5000 };
        if (mode == "negative") changes["salaryBreakdown"] = Salary(-1);
        if (mode == "both") changes["salary"] = 9000;
        if (mode == "overflow") changes["salaryBreakdown"] = Salary(decimal.MaxValue);
        if (mode == "gross-limit") changes["salaryBreakdown"] = Salary(9999999999m);
        if (mode == "fractional-cent") changes["salaryBreakdown"] = Salary(4000.001m);
        var effectiveDate = "2026-01-01";
        if (mode == "date-mismatch") changes["salaryBreakdown"] = Salary(5000, "2026-02-01");
        if (mode == "future-mixed")
        {
            effectiveDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10).ToString("yyyy-MM-dd");
            changes["salaryBreakdown"] = Salary(5000, effectiveDate);
            changes["paymentMethod"] = "BankTransfer";
        }
        using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new { effectiveDate, changes });
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync());
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.Employees.SingleAsync(e => e.Id == id)).EnglishName.Should().NotBe("Must not be applied");
        (await db.EmployeeChangeRequests.CountAsync(c => c.EmployeeId == id)).Should().Be(0);
    }

    [Fact]
    public async Task FutureSalaryApprovalSchedulesWithoutChangingCurrentSalaryOrAssignment()
    {
        var id = await CreateAsync();
        var effective = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new
        {
            effectiveDate = effective.ToString("yyyy-MM-dd"), changes = new { salaryBreakdown = Salary(5000, effective.ToString("yyyy-MM-dd")) },
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var changeId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changeRequestId").GetGuid();
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await ApprovalCenterDriver.ApproveChangeAsync(db, _fx.TenantId, changeId)).Status.Should().Be("Approved");
        (await db.EmployeeChangeRequests.SingleAsync(x => x.Id == changeId)).EffectiveDate.Should().Be(effective);
        (await db.Employees.SingleAsync(x => x.Id == id)).Salary.Should().Be(5750);
        (await db.EmployeeSalaryStructures.SingleAsync(x => x.EmployeeId == id && x.IsActive)).BasicSalary.Should().Be(4000);
        (await db.EmployeeHistories.SingleAsync(x => x.EmployeeId == id && x.EventType == EmployeeChangeBaseline.ScheduledEventType)).EffectiveDate.Should().Be(effective);
    }

    [Fact]
    public async Task ScheduledSalaryBaselineDetectsComponentAndPayrollDrift()
    {
        var id = await CreateAsync();
        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var employee = await db.Employees.SingleAsync(e => e.Id == id);
        var profile = await db.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == id);
        var keys = new[] { "salaryBreakdown", "salaryCurrency", "paymentMethod", "molId", "payrollGroup", "salaryStructureReference" };
        var baseline = await EmployeeChangeBaseline.CaptureAsync(db, employee, profile, keys, default);
        baseline.Should().NotBeNull();
        EmployeeChangeBaseline.Drifted(baseline!, await EmployeeChangeBaseline.CaptureAsync(db, employee, profile, keys, default)).Should().BeEmpty();
        var previousMirror = employee.WpsBankDetails;
        employee.WpsBankDetails = "Changed outside the profile";
        await db.SaveChangesAsync();
        EmployeeChangeBaseline.Drifted(baseline!, await EmployeeChangeBaseline.CaptureAsync(db, employee, profile, keys, default))
            .Should().BeEquivalentTo(["paymentMethod"]);
        employee.WpsBankDetails = previousMirror;
        (await db.EmployeeSalaryStructures.SingleAsync(s => s.EmployeeId == id && s.IsActive)).HousingAllowance = 1100;
        profile.PaymentMethod = "WPS";
        await db.SaveChangesAsync();
        EmployeeChangeBaseline.Drifted(baseline!, await EmployeeChangeBaseline.CaptureAsync(db, employee, profile, keys, default))
            .Should().BeEquivalentTo(["salaryBreakdown", "paymentMethod"]);
    }
}
