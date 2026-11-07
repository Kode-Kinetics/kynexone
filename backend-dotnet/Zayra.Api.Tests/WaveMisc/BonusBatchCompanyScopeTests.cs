using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;
using Xunit;

namespace Zayra.Api.Tests.WaveMisc;

/// <summary>Bonus batches follow the company scope of their lines, and the detail view keeps pay journals and
/// audit values to Finance and HR.</summary>
public class BonusBatchCompanyScopeTests
{
    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }

    private sealed record World(ScopeSodWorld W, Guid Mixed, Guid OnlyB, Guid Empty);

    private static async Task<World> SeedAsync()
    {
        var w = await ScopeSodWorld.CreateAsync();
        await using var db = w.Plain();
        var type = new BonusType { TenantId = w.Tenant, NameEn = "Perf", Code = "PERF" };
        db.BonusTypes.Add(type);
        BonusBatch Batch(string n, int count, decimal total) => new()
        {
            TenantId = w.Tenant, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BatchNumber = n, BatchName = n,
            PaymentPeriod = "2026-10", PaymentDate = new DateOnly(2026, 10, 25), Status = "Draft", EmployeeCount = count, TotalAmount = total,
        };
        var mixed = Batch("BON-MIX", 2, 300m); var onlyB = Batch("BON-B", 1, 200m); var empty = Batch("BON-EMPTY", 0, 0m);
        db.BonusBatches.AddRange(mixed, onlyB, empty);
        await db.SaveChangesAsync();
        EmployeeBonus Line(BonusBatch b, Guid company, int emp, decimal net) => new()
        {
            TenantId = w.Tenant, CompanyId = company, BonusBatchId = b.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp,
            EmployeeName = "E" + emp, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 5000m, CalculationMethod = "Fixed",
            CalculationValue = net, GrossBonusAmount = net, BonusAmount = net, PaymentPeriod = "2026-10", Status = "Draft",
        };
        db.EmployeeBonuses.AddRange(Line(mixed, w.CompanyA, w.EmpA, 100m), Line(mixed, w.CompanyB, w.EmpB, 200m), Line(onlyB, w.CompanyB, w.EmpB, 200m));
        db.BonusAuditLogs.Add(new BonusAuditLog { TenantId = w.Tenant, BonusBatchId = mixed.Id, EntityType = "BonusBatch", Action = "BatchCreated", NewValuesJson = "{\"secret\":1}" });
        db.FinanceGlEntries.Add(new FinanceGlEntry { TenantId = w.Tenant, SourceEntityId = mixed.Id, EntryDate = new DateOnly(2026, 10, 25) });
        await db.SaveChangesAsync();
        return new World(w, mixed.Id, onlyB.Id, empty.Id);
    }

    private static BonusesController As(ScopeSodWorld w, ClaimsPrincipal p) =>
        ScopeSodWorld.Wire(new BonusesController(w.DbFor(p), new OrgScope()), p);

    private static ClaimsPrincipal WithoutRole(ClaimsPrincipal p)
    {
        var claims = p.Claims.Where(c => c.Type != ClaimTypes.Role).Append(new Claim(ClaimTypes.Role, "Manager"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static JsonElement Json(IActionResult r) =>
        JsonSerializer.SerializeToElement(r.Should().BeOfType<OkObjectResult>().Subject.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task ListShowsOnlyBatchesWithLinesInTheCallersCompanies_AndOnlyTheirTotals()
    {
        var s = await SeedAsync();
        var p = s.W.CompanyAdmin(s.W.CompanyA, null, "payroll.read");
        var json = Json(await As(s.W, p).ListBatches(null, 1, 20, default));
        var items = json.GetProperty("items").EnumerateArray().ToList();
        json.GetProperty("total").GetInt32().Should().Be(2);
        items.Select(i => i.GetProperty("id").GetGuid()).Should().BeEquivalentTo(new[] { s.Mixed, s.Empty });
        var mixed = items.Single(i => i.GetProperty("id").GetGuid() == s.Mixed);
        mixed.GetProperty("employeeCount").GetInt32().Should().Be(1);
        mixed.GetProperty("totalAmount").GetDecimal().Should().Be(100m);
    }

    [Fact]
    public async Task GroupScopeSeesEveryBatchWithItsFullTotals()
    {
        var s = await SeedAsync();
        var json = Json(await As(s.W, s.W.GroupAdmin(null, "payroll.read")).ListBatches(null, 1, 20, default));
        json.GetProperty("total").GetInt32().Should().Be(3);
        json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == s.Mixed)
            .GetProperty("totalAmount").GetDecimal().Should().Be(300m);
    }

    [Fact]
    public async Task ABatchOfOtherCompaniesIsNotFoundByIdOrByAction()
    {
        var s = await SeedAsync();
        var p = s.W.CompanyAdmin(s.W.CompanyA, null, "payroll.read", "payroll.write");
        var c = As(s.W, p);
        (await c.GetBatch(s.OnlyB, default)).Should().BeOfType<NotFoundResult>();
        (await c.UpdateBatch(s.OnlyB, new UpdateBatchRequest("x", null, null, null), default)).Should().BeOfType<NotFoundResult>();
        (await c.DeleteBatch(s.OnlyB, default)).Should().BeOfType<NotFoundResult>();
        (await c.SubmitBatch(s.OnlyB, default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task DetailOfAMixedBatchShowsOnlyTheCallersLinesAndTotals()
    {
        var s = await SeedAsync();
        var json = Json(await As(s.W, s.W.CompanyAdmin(s.W.CompanyA, null, "payroll.read")).GetBatch(s.Mixed, default));
        json.GetProperty("bonuses").GetArrayLength().Should().Be(1);
        json.GetProperty("batch").GetProperty("totalAmount").GetDecimal().Should().Be(100m);
    }

    [Fact]
    public async Task AuditValuesAndTheJournalAreWithheldFromReadersWhoAreNeitherHrNorFinance()
    {
        var s = await SeedAsync();
        var reader = WithoutRole(s.W.GroupAdmin(null, "payroll.read"));
        var json = Json(await As(s.W, reader).GetBatch(s.Mixed, default));
        json.GetProperty("glEntries").GetArrayLength().Should().Be(0);
        json.GetProperty("auditLogs")[0].GetProperty("newValuesJson").GetString().Should().BeEmpty();

        var finance = Json(await As(s.W, s.W.GroupAdmin(null, "payroll.read")).GetBatch(s.Mixed, default));
        finance.GetProperty("glEntries").GetArrayLength().Should().Be(1);
        finance.GetProperty("auditLogs")[0].GetProperty("newValuesJson").GetString().Should().Contain("secret");
    }
}
