using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

/// <summary>
/// The employee update (PUT /api/employees/{id}) answers a salary reduction that carries no written-agreement reference with the SAME structured refusal
/// the salary endpoints give — <c>reduction_consent_required</c>, the sentence naming what goes down, and the items with what they were and what they become —
/// not a generic <c>invalid_payroll_change</c>. The edit form recovers from that code (opens the reference field), and cannot recover from a generic one.
/// </summary>
public class EmployeeUpdateReductionRefusalHttpTests : IClassFixture<EmployeeDraftCreateHttpFixture>
{
    private readonly EmployeeDraftCreateHttpFixture _fx;
    public EmployeeUpdateReductionRefusalHttpTests(EmployeeDraftCreateHttpFixture fixture) => _fx = fixture;

    private static object Salary(decimal basic, string? reference = null) => new
    {
        basicSalary = basic, housingAllowance = 1000m, transportAllowance = 400m, foodAllowance = 100m,
        mobileAllowance = 50m, otherAllowance = 200m, fixedDeduction = 25m,
        salaryStructureCode = "REDUCTION-REFUSAL", effectiveDate = "2026-01-01", currency = "SAR",
        reductionConsentReference = reference,
    };

    private async Task<int> CreateEmployeeWithApprovedSalaryAsync()
    {
        using (var scope = _fx.Host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            if (!await db.SalaryStructures.AnyAsync(s => s.TenantId == _fx.TenantId && s.Code == "REDUCTION-REFUSAL" && !s.IsDeleted))
            {
                db.SalaryStructures.Add(new Zayra.Api.Models.SalaryStructure
                {
                    TenantId = _fx.TenantId, Code = "REDUCTION-REFUSAL", Name = "Reduction refusal", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1),
                });
                await db.SaveChangesAsync();
            }
        }
        using var created = await _fx.SendAsync(HttpMethod.Post, "/api/employees", new
        {
            englishName = $"Reduction {Guid.NewGuid():N}", salaryBreakdown = Salary(4000m),
            payrollProfile = new { salaryCurrency = "SAR", paymentMethod = "Cash" },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        using var approve = _fx.Host.Services.CreateScope();
        await SalaryApprovalKit.ApprovePendingAsync(approve.ServiceProvider.GetRequiredService<ZayraDbContext>(), _fx.TenantId);
        return id;
    }

    [Fact]
    public async Task ALowerSalaryWithoutAReference_IsRefusedWithTheStructuredReductionConsentRefusal()
    {
        var id = await CreateEmployeeWithApprovedSalaryAsync();

        using var response = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new
        {
            effectiveDate = "2026-01-01", changes = new { salaryBreakdown = Salary(3500m) },
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("reduction_consent_required");
        body.GetProperty("code").GetString().Should().Be("reduction_consent_required");
        body.GetProperty("message").GetString().Should().Contain("written agreement").And.Contain("BASIC 4,000.00 to 3,500.00").And.Contain("No change was applied");
        var items = body.GetProperty("reducedItems").EnumerateArray().ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("item").GetString().Should().Be("BASIC");
        items[0].GetProperty("from").GetDecimal().Should().Be(4000m);
        items[0].GetProperty("to").GetDecimal().Should().Be(3500m);

        using var scope = _fx.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        (await db.EmployeeChangeRequests.CountAsync(c => c.EmployeeId == id)).Should().Be(0, "a refused change queues nothing");
    }

    [Fact]
    public async Task TheSameReductionWithAReference_IsAccepted_AndAnotherInvalidPayrollChangeKeepsItsOwnCode()
    {
        var id = await CreateEmployeeWithApprovedSalaryAsync();

        using var accepted = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new
        {
            effectiveDate = "2026-01-01", changes = new { salaryBreakdown = Salary(3500m, "AMEND-2026-1") },
        });
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());

        using var other = await _fx.SendAsync(HttpMethod.Put, $"/api/employees/{id}", new
        {
            effectiveDate = "2026-01-01", changes = new { salaryBreakdown = Salary(-1m) },
        });
        other.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("invalid_payroll_change");
    }
}

/// <summary>A reduction refusal that reaches the global handler keeps its stable code (not a bare bad_request), like the company write guard's.</summary>
public class SalaryReductionConsentRefusalMappingTests
{
    [Fact]
    public void TheTypedReductionRefusal_MapsToItsStableCode_AndStaysAnInvalidOperationForExistingCatches()
    {
        var problem = Zayra.Api.Infrastructure.Payroll.SalaryReductions.RequireConsent(
            Zayra.Api.Infrastructure.Payroll.SalaryReductions.Compare(
                new Zayra.Api.Infrastructure.Payroll.PaySlots(4000m, 0m, 0m, 0m, 0m, 0m, 0m), [],
                new Zayra.Api.Infrastructure.Payroll.PaySlots(3500m, 0m, 0m, 0m, 0m, 0m, 0m), []), null)!;
        var items = new[] { new Zayra.Api.Infrastructure.Payroll.ReducedItem("BASIC", 4000m, 3500m) };
        var ex = new Zayra.Api.Infrastructure.Payroll.SalaryReductionConsentRequiredException(problem, items);

        ex.Should().BeAssignableTo<InvalidOperationException>();
        ex.Code.Should().Be("reduction_consent_required");
        ex.Items.Should().BeEquivalentTo(items);
        var (status, code, message) = Zayra.Api.Infrastructure.Http.ExceptionResponseMapper.Map(ex);
        (status, code).Should().Be((400, "reduction_consent_required"));
        message.Should().Contain("written agreement").And.Contain("BASIC 4,000.00 to 3,500.00");
    }
}
