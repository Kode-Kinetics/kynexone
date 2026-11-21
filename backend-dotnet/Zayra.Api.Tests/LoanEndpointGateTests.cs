using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Tests;

/// <summary>
/// Loan creation and the eligibility check are gated on the existing loans.* keys, as every other loan and advance endpoint is
/// (an advance request already needs loans.self or loans.write), so turning loans.self off stops an employee creating a loan.
/// The two retired endpoints (settle, mark an instalment paid) answer honestly in English and Arabic instead of promising a
/// write-off workflow that does not exist.
/// </summary>
public class LoanEndpointGateTests
{
    private static string[] Keys(string method) =>
        typeof(LoansController).GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttributes<HasPermissionAttribute>().Single().Permissions.OrderBy(x => x).ToArray();

    [Fact]
    public void CreateLoan_NeedsLoansSelfOrLoansWrite_AsAnAdvanceRequestDoes()
    {
        Assert.Equal(new[] { "loans.self", "loans.write" }, Keys(nameof(LoansController.CreateLoan)));
        var advance = typeof(AdvancesController).GetMethod(nameof(AdvancesController.Create))!
            .GetCustomAttributes<HasPermissionAttribute>().Single().Permissions.OrderBy(x => x).ToArray();
        Assert.Equal(advance, Keys(nameof(LoansController.CreateLoan)));
    }

    [Fact]
    public void Eligibility_NeedsALoansReadKey_AsTheOtherLoanReadsDo()
    {
        Assert.Equal(new[] { "loans.read", "loans.self", "loans.write" }, Keys(nameof(LoansController.GetLoanEligibility)));
        Assert.Equal(Keys(nameof(LoansController.ListLoans)), Keys(nameof(LoansController.GetLoanEligibility)));
    }

    [Fact]
    public async Task RetiredSettleEndpoint_DoesNotPromiseAWriteOffWorkflow_AndAnswersInArabic()
    {
        var body = Json(Assert.IsType<ConflictObjectResult>(await Controller().SettleLoan(Guid.NewGuid(), new LoanSettlementRequest("Full", 1m, new DateOnly(2026, 1, 1), null), default)).Value);
        AssertHonest(body);
    }

    [Fact]
    public async Task RetiredMarkInstalmentPaidEndpoint_AnswersInArabicToo()
    {
        var body = Json(Assert.IsType<ConflictObjectResult>(await Controller().MarkInstallmentPaid(Guid.NewGuid(), Guid.NewGuid(), new PayInstallmentRequest(1m, new DateOnly(2026, 1, 1), null), default)).Value);
        AssertHonest(body);
    }

    private static void AssertHonest(JsonElement body)
    {
        Assert.Equal("loan_endpoint_retired", body.GetProperty("error").GetString());
        var message = body.GetProperty("message").GetString()!;
        Assert.DoesNotContain("write-off workflow", message);
        Assert.Contains("repayment", message);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("messageAr").GetString()));
    }

    private static LoansController Controller()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return new LoansController(db, new Scope())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Finance") }, "test")),
                },
            },
        };
    }

    private static JsonElement Json(object? value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return doc.RootElement.Clone();
    }

    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
