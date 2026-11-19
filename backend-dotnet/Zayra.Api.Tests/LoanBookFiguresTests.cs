using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The loan book's figures count every open loan, Overdue included (an overdue loan is still owed, and still sits in 1400), and
/// never add amounts of different currencies into one number: the Loan Balance report, the analytics KPI and the loans audit
/// report each carry a per-currency breakdown, and their single total is given only when the book is in one currency.
/// </summary>
public class LoanBookFiguresTests
{
    [Fact]
    public async Task LoanBalanceReport_IncludesOverdueLoans_WithTheirStatusAndCurrency()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.EmployeeLoans.AddRange(Loan(tid, "LN-ACT", "Active", 1_000m, "AED"), Loan(tid, "LN-OVD", "Overdue", 500m, "AED"), Loan(tid, "LN-CLS", "Closed", 0m, "AED"));
        await db.SaveChangesAsync();
        var ctrl = Reports(db, tid, "reports.read", "employees.read", "loans.read");

        var body = Json(Assert.IsType<OkObjectResult>(await ctrl.RunReport(new RunReportRequest("finance.loan-balance", null), default)).Value);

        var rows = body.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(new[] { "LN-ACT", "LN-OVD" }, rows.Select(r => r.GetProperty("loanNumber").GetString()).OrderBy(x => x).ToArray());
        var overdue = rows.Single(r => r.GetProperty("loanNumber").GetString() == "LN-OVD");
        Assert.Equal(("Overdue", "AED"), (overdue.GetProperty("status").GetString(), overdue.GetProperty("currency").GetString()));
    }

    [Fact]
    public async Task AnalyticsKpis_CountOverdueLoans_AndGroupTheBalanceByCurrency()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.EmployeeLoans.AddRange(Loan(tid, "LN-1", "Active", 1_000m, "AED"), Loan(tid, "LN-2", "Overdue", 500m, "AED"), Loan(tid, "LN-3", "Active", 2_000m, "SAR"));
        await db.SaveChangesAsync();

        var financial = Json(Assert.IsType<OkObjectResult>(await Analytics(db, tid).GetKPIs(default)).Value).GetProperty("financial");

        Assert.Equal(3, financial.GetProperty("activeLoans").GetInt32());
        // Two currencies: no single figure, which would add dirhams to riyals.
        Assert.Equal(JsonValueKind.Null, financial.GetProperty("outstandingLoanBalance").ValueKind);
        var byCurrency = financial.GetProperty("outstandingLoanBalanceByCurrency").EnumerateArray()
            .ToDictionary(x => x.GetProperty("currency").GetString()!, x => (x.GetProperty("amount").GetDecimal(), x.GetProperty("count").GetInt32()));
        Assert.Equal((1_500m, 2), byCurrency["AED"]);
        Assert.Equal((2_000m, 1), byCurrency["SAR"]);
    }

    [Fact]
    public async Task AnalyticsKpis_InOneCurrency_KeepTheSingleTotal_OverdueIncluded()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.EmployeeLoans.AddRange(Loan(tid, "LN-1", "Active", 1_000m, "AED"), Loan(tid, "LN-2", "Overdue", 500m, "AED"));
        await db.SaveChangesAsync();

        var financial = Json(Assert.IsType<OkObjectResult>(await Analytics(db, tid).GetKPIs(default)).Value).GetProperty("financial");

        Assert.Equal(2, financial.GetProperty("activeLoans").GetInt32());
        Assert.Equal(1_500m, financial.GetProperty("outstandingLoanBalance").GetDecimal());
        Assert.Equal("AED", financial.GetProperty("loanCurrencyCode").GetString());
    }

    [Fact]
    public async Task LoansAuditReport_CountsOverdue_AndDoesNotAddCurrenciesTogether()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.EmployeeLoans.AddRange(Loan(tid, "LN-1", "Active", 1_000m, "AED"), Loan(tid, "LN-2", "Overdue", 500m, "AED"), Loan(tid, "LN-3", "Active", 2_000m, "SAR"));
        await db.SaveChangesAsync();

        var body = Json(Assert.IsType<OkObjectResult>(await LoansAudit(db, tid).AuditReport(null, null, default)).Value);

        Assert.Equal(1, body.GetProperty("overdueLoans").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("totalOutstanding").ValueKind);
        var byCurrency = body.GetProperty("byCurrency").EnumerateArray()
            .ToDictionary(x => x.GetProperty("currency").GetString()!, x => x.GetProperty("outstanding").GetDecimal());
        Assert.Equal(1_500m, byCurrency["AED"]);
        Assert.Equal(2_000m, byCurrency["SAR"]);
    }

    [Fact]
    public async Task LoansAuditReport_InOneCurrency_KeepsItsTotals()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.EmployeeLoans.AddRange(Loan(tid, "LN-1", "Active", 1_000m, "AED"), Loan(tid, "LN-2", "Overdue", 500m, "AED"));
        await db.SaveChangesAsync();

        var body = Json(Assert.IsType<OkObjectResult>(await LoansAudit(db, tid).AuditReport(null, null, default)).Value);

        Assert.Equal(1_500m, body.GetProperty("totalOutstanding").GetDecimal());
        Assert.Equal("AED", body.GetProperty("currencyCode").GetString());
    }

    private static EmployeeLoan Loan(Guid tid, string number, string status, decimal balance, string currency) => new()
    {
        TenantId = tid, EmployeeId = Guid.NewGuid(), EmployeeIntId = 1, EmployeeName = number, LoanNumber = number, LoanTypeName = "Personal",
        Status = status, Currency = currency, ApprovedAmount = Math.Max(balance, 100m), OutstandingBalance = balance,
        TotalRepaid = Math.Max(balance, 100m) - balance, DisbursementDate = new DateOnly(2026, 1, 1),
    };

    private static JsonElement Json(object? value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return doc.RootElement.Clone();
    }

    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db)
    {
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Loan book", Slug = $"loan-book-{tid:N}" });
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tid, DefaultTimezone = "UTC" });
        await db.SaveChangesAsync();
        return tid;
    }

    private static ClaimsPrincipal User(Guid tid, string? role, params string[] permissions) => new(new ClaimsIdentity(
        new List<Claim> { new("tenant_id", tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Name, "Reader") }
            .Concat(role is null ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.Role, role) })
            .Concat(permissions.Select(p => new Claim("permission", p))), "test"));

    private static ReportsController Reports(ZayraDbContext db, Guid tid, params string[] permissions) =>
        new(db, new DataScopeService(db)) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = User(tid, null, permissions) } } };

    private static AnalyticsController Analytics(ZayraDbContext db, Guid tid) =>
        new(db) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = User(tid, null, "reports.read", "employees.read", "loans.read") } } };

    private static LoansController LoansAudit(ZayraDbContext db, Guid tid) =>
        new(db, new Scope()) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = User(tid, "Finance", "loans.read") } } };

    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
