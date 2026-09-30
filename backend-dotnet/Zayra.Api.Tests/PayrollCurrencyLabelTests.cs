using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// R03 follow-ups — a payroll amount is labelled with its legal entity's currency, which the client
/// resolves from the run's company (frontend/src/lib/payrollCurrency.ts). The server's part is to say
/// which company each amount belongs to, and never to hand over a single figure that silently adds
/// two currencies together without the per-company breakdown beside it.
/// </summary>
public class PayrollCurrencyLabelTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task ReportSummary_BreaksYearToDateDownByCompany()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var saudi = Guid.NewGuid();
        var emirati = Guid.NewGuid();
        var year = DateTime.UtcNow.Year;
        db.PayrollRuns.AddRange(
            new PayrollRun { TenantId = tenantId, CompanyId = saudi, Year = year, Month = 1, Status = "Locked", TotalGrossSalary = 100m, TotalNetSalary = 90m },
            new PayrollRun { TenantId = tenantId, CompanyId = saudi, Year = year, Month = 2, Status = "Locked", TotalGrossSalary = 100m, TotalNetSalary = 90m },
            new PayrollRun { TenantId = tenantId, CompanyId = emirati, Year = year, Month = 1, Status = "Locked", TotalGrossSalary = 50m, TotalNetSalary = 40m },
            new PayrollRun { TenantId = tenantId, CompanyId = emirati, Year = year, Month = 2, Status = "Draft", TotalGrossSalary = 999m, TotalNetSalary = 999m });
        await db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await PayComponentNetPayDefectTests.Build(db, tenantId, "payroll.read")
            .ReportSummary(CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var byCompany = json.RootElement.GetProperty("ytdByCompany").EnumerateArray()
            .ToDictionary(e => e.GetProperty("companyId").GetGuid(), e => e);

        byCompany.Should().HaveCount(2);
        byCompany[saudi].GetProperty("totalGrossYtd").GetDecimal().Should().Be(200m);
        byCompany[saudi].GetProperty("totalNetYtd").GetDecimal().Should().Be(180m);
        byCompany[emirati].GetProperty("totalGrossYtd").GetDecimal().Should().Be(50m, "a Draft run is not year-to-date pay");
        byCompany[emirati].GetProperty("lockedRuns").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task DashboardPayroll_NamesTheRunsCompany_OnTheSummaryAndEveryTrendPoint()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Currency", Slug = $"currency-{tenantId:N}" });
        db.PayrollRuns.Add(new PayrollRun
        {
            TenantId = tenantId, CompanyId = companyId, Year = today.Year, Month = today.Month, Status = "Locked",
            TotalNetSalary = 5_000m, EmployeeCount = 5,
        });
        await db.SaveChangesAsync();

        var ctrl = new DashboardController(db,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new OrgScope())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()), new Claim("permission", "payroll.read"),
                    }, "test")),
                },
            },
        };
        var full = Assert.IsType<DashboardFullDto>(Assert.IsType<OkObjectResult>(await ctrl.Full(6)).Value);

        full.Overview.PayrollSummary!.CompanyId.Should().Be(companyId);
        full.PayrollTrends.Single(p => p.TotalNet == 5_000m).CompanyId.Should().Be(companyId);
        full.PayrollTrends.Where(p => p.TotalNet == 0m).Should().OnlyContain(p => p.CompanyId == null,
            "a month with no run has no company, and no currency");
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
