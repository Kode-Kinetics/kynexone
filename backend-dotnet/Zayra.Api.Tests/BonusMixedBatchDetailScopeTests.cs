using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The detail of a bonus batch that spans two companies: a caller who sees one company's lines receives that company's journal entries
/// and the audit entries of those lines only, and the batch-level audit entries keep no amounts. A group-level caller sees everything.
/// </summary>
public class BonusMixedBatchDetailScopeTests
{
    private sealed record World(DbContextOptions<ZayraDbContext> Options, Guid Tenant, Guid CoA, Guid CoB, Guid Batch, Guid LineA, Guid LineB);

    private static DefaultHttpContext Http(Guid tenant, string[] permissions, string[] roles, Guid? scopedToCompany)
    {
        var claims = new List<Claim> { new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Name, "tester") };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (scopedToCompany is Guid cid)
            claims.Add(new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = EntityScopeModes.Companies, c = new[] { cid } })));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
    }

    private static async Task<(SqliteConnection Conn, World W)> SeedAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options;
        await using var db = new ZayraDbContext(options);
        db.Database.EnsureCreated();
        var tenant = Guid.NewGuid();
        Company Co(string name) => new() { TenantId = tenant, LegalNameEn = name, CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR" };
        var coA = Co("Co A"); var coB = Co("Co B");
        db.Companies.AddRange(coA, coB);
        await db.SaveChangesAsync();
        Employee Emp(string code, Guid company) => new()
        {
            TenantId = tenant, CompanyId = company, EmployeeCode = code, FullName = $"Emp {code}", Status = "Active",
            JoiningDate = new DateTime(2023, 1, 1), WorkEmail = $"{code}@t.test", Nationality = "SAU", ContractType = "Indefinite",
        };
        var empA = Emp("A001", coA.Id); var empB = Emp("B001", coB.Id);
        db.Employees.AddRange(empA, empB);
        var type = new BonusType { TenantId = tenant, Code = "PERF", NameEn = "Performance", TaxRegion = "GCC", IsActive = true };
        db.BonusTypes.Add(type);
        await db.SaveChangesAsync();
        var batch = new BonusBatch
        {
            TenantId = tenant, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BatchNumber = "BON-1", BatchName = "Mixed",
            PaymentPeriod = "2026-06", PaymentDate = new DateOnly(2026, 6, 25), Status = "Draft", EmployeeCount = 2, TotalAmount = 1_200m,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();
        EmployeeBonus Line(Employee e) => new()
        {
            TenantId = tenant, CompanyId = e.CompanyId, BonusBatchId = batch.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = e.Id, EmployeeName = e.FullName,
            BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 10_000m, CalculationMethod = "Fixed", CalculationValue = 600m,
            GrossBonusAmount = 600m, BonusAmount = 600m, PaymentPeriod = "2026-06", Status = "Draft", TaxRegion = "GCC",
        };
        var lineA = Line(empA); var lineB = Line(empB);
        db.EmployeeBonuses.AddRange(lineA, lineB);
        await db.SaveChangesAsync();

        FinanceGlEntry Gl(Guid? company, string description) => new()
        {
            TenantId = tenant, CompanyId = company, SourceModule = "Bonus", SourceEntityId = batch.Id, SourceEntityRef = batch.BatchNumber, EventType = "BonusApproval",
            DebitAccount = "6100 - Employee Bonus Expense", CreditAccount = "2300 - Bonus Payable", Amount = 600m, Currency = "SAR",
            EntryDate = new DateOnly(2026, 6, 1), Period = "2026-06", Description = description,
        };
        db.FinanceGlEntries.AddRange(Gl(coA.Id, "GL-A"), Gl(coB.Id, "GL-B"), Gl(null, "GL-GROUP"));
        BonusAuditLog Audit(Guid? line, string action) => new()
        {
            TenantId = tenant, BonusBatchId = batch.Id, EmployeeBonusId = line, EntityType = line is null ? "BonusBatch" : "EmployeeBonus", Action = action,
            OldValuesJson = "{\"total\":0}", NewValuesJson = "{\"total\":1200}",
        };
        db.BonusAuditLogs.AddRange(Audit(null, "BATCH-LEVEL"), Audit(lineA.Id, "LINE-A"), Audit(lineB.Id, "LINE-B"));
        await db.SaveChangesAsync();
        return (conn, new World(options, tenant, coA.Id, coB.Id, batch.Id, lineA.Id, lineB.Id));
    }

    private static async Task<JsonElement> DetailAsync(World w, Guid? scopedToCompany)
    {
        var http = Http(w.Tenant, ["payroll.read", "finance.gl.read"], ["Admin"], scopedToCompany);
        await using var db = new ZayraDbContext(w.Options, new Accessor(http));
        var controller = new BonusesController(db, new Scope()) { ControllerContext = new ControllerContext { HttpContext = http } };
        var ok = (await controller.GetBatch(w.Batch, default)).Should().BeOfType<OkObjectResult>().Subject;
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    private static List<string> Strings(JsonElement body, string array, string property) =>
        body.GetProperty(array).EnumerateArray().Select(x => x.GetProperty(property).GetString()!).ToList();

    [Fact]
    public async Task ACompanyCaller_SeesOnlyTheirCompanysJournalEntries_OfAMixedBatch()
    {
        var (conn, w) = await SeedAsync();
        await using var _ = conn;

        var body = await DetailAsync(w, w.CoA);

        Strings(body, "glEntries", "Description").Should().BeEquivalentTo(["GL-A"]);
    }

    [Fact]
    public async Task ACompanyCaller_SeesOnlyTheirLinesAuditEntries_AndBatchLevelEntriesWithoutAmounts()
    {
        var (conn, w) = await SeedAsync();
        await using var _ = conn;

        var body = await DetailAsync(w, w.CoA);

        Strings(body, "auditLogs", "Action").Should().BeEquivalentTo(["BATCH-LEVEL", "LINE-A"]);
        var batchLevel = body.GetProperty("auditLogs").EnumerateArray().Single(x => x.GetProperty("Action").GetString() == "BATCH-LEVEL");
        batchLevel.GetProperty("NewValuesJson").GetString().Should().BeEmpty();
        var line = body.GetProperty("auditLogs").EnumerateArray().Single(x => x.GetProperty("Action").GetString() == "LINE-A");
        line.GetProperty("NewValuesJson").GetString().Should().Contain("1200");
    }

    [Fact]
    public async Task AGroupLevelCaller_StillSeesEveryJournalAndAuditEntry()
    {
        var (conn, w) = await SeedAsync();
        await using var _ = conn;

        var body = await DetailAsync(w, scopedToCompany: null);

        Strings(body, "glEntries", "Description").Should().BeEquivalentTo(["GL-A", "GL-B", "GL-GROUP"]);
        Strings(body, "auditLogs", "Action").Should().BeEquivalentTo(["BATCH-LEVEL", "LINE-A", "LINE-B"]);
    }

    [Fact]
    public async Task ACompanyCaller_StillSeesOnlyTheirCompanysEntries_WhenTheBatchCountHasDrifted()
    {
        var (conn, w) = await SeedAsync();
        await using var _ = conn;
        await using (var db = new ZayraDbContext(w.Options))
        {
            var batch = await db.BonusBatches.SingleAsync(b => b.Id == w.Batch);
            batch.EmployeeCount = 1;   // a stale count: it no longer exceeds the lines this caller sees
            await db.SaveChangesAsync();
        }

        var body = await DetailAsync(w, w.CoA);

        Strings(body, "glEntries", "Description").Should().BeEquivalentTo(["GL-A"]);
        Strings(body, "auditLogs", "Action").Should().BeEquivalentTo(["BATCH-LEVEL", "LINE-A"]);
    }

    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }

    private sealed class Accessor(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = ctx;
    }
}
