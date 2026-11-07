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
/// Security review M3 and L2 for the bonus batch endpoints (SQLite, no container).
///
/// <para>M3: UpdateBatch and DeleteBatch used ScopedBatchAsync, which shows a batch to a caller who sees ANY one of its
/// lines. A company-A caller could therefore edit a mixed-company batch, or delete it, soft-deleting only the lines
/// they could see and orphaning the other company's. Approve and Process already refuse a partial view with
/// <c>company_scope_partial</c>; Update and Delete now do too.</para>
///
/// <para>L2: the "is this a Finance actor" test matched only the role named exactly "Finance", so a Finance Approver
/// or a custom Finance Controller role did not see the batch's journal entries or the audit snapshots.</para>
/// </summary>
public class BonusBatchScopeAndFinanceActorTests
{
    private sealed record Mixed(DbContextOptions<ZayraDbContext> Options, Guid Tenant, Company CoA, Company CoB, BonusBatch Batch);

    private static DefaultHttpContext Http(Guid tenant, string[] permissions, string[]? roles = null, Guid? scopedToCompany = null)
    {
        var claims = new List<Claim> { new("tenant_id", tenant.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Name, "tester") };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        claims.AddRange((roles ?? []).Select(r => new Claim(ClaimTypes.Role, r)));
        if (scopedToCompany is Guid cid)
            claims.Add(new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = EntityScopeModes.Companies, c = new[] { cid } })));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
    }

    private static BonusesController Ctrl(ZayraDbContext db, HttpContext http) =>
        new(db, new Scope()) { ControllerContext = new ControllerContext { HttpContext = http } };

    private static async Task<Company> CoAsync(ZayraDbContext db, Guid tenant, string name)
    {
        var c = new Company { TenantId = tenant, LegalNameEn = name, CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    private static async Task<Employee> EmpAsync(ZayraDbContext db, Guid tenant, Guid companyId, string code)
    {
        var e = new Employee
        {
            TenantId = tenant, CompanyId = companyId, EmployeeCode = code, FullName = $"Emp {code}", Status = "Active",
            JoiningDate = new DateTime(2023, 1, 1), WorkEmail = $"{code}@t.test", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    private static async Task<(SqliteConnection Conn, Mixed World)> SeedMixedAsync(string status = "Draft")
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options;
        await using var db = new ZayraDbContext(options);     // no HttpContext => group scope
        db.Database.EnsureCreated();
        var tenant = Guid.NewGuid();
        var coA = await CoAsync(db, tenant, "Co A");
        var coB = await CoAsync(db, tenant, "Co B");
        var empA = await EmpAsync(db, tenant, coA.Id, "A001");
        var empB = await EmpAsync(db, tenant, coB.Id, "B001");
        var type = new BonusType { TenantId = tenant, Code = "PERF", NameEn = "Performance", TaxRegion = "GCC", IsActive = true };
        db.BonusTypes.Add(type);
        await db.SaveChangesAsync();
        var batch = new BonusBatch
        {
            TenantId = tenant, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BatchNumber = "BON-1", BatchName = "Original",
            PaymentPeriod = "2026-06", PaymentDate = new DateOnly(2026, 6, 25), Status = status, EmployeeCount = 2, TotalAmount = 1_200m,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();
        foreach (var emp in new[] { empA, empB })
            db.EmployeeBonuses.Add(new EmployeeBonus
            {
                TenantId = tenant, CompanyId = emp.CompanyId, BonusBatchId = batch.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
                EmployeeName = emp.FullName, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 10_000m, CalculationMethod = "Fixed",
                CalculationValue = 600m, GrossBonusAmount = 600m, BonusAmount = 600m, PaymentPeriod = "2026-06", Status = "Draft", TaxRegion = "GCC",
            });
        await db.SaveChangesAsync();
        return (conn, new Mixed(options, tenant, coA, coB, batch));
    }

    private static readonly string[] Writer = ["payroll.read", "payroll.write"];

    private static void ShouldBePartialScopeRefusal(IActionResult result)
    {
        var refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(403);
        JsonSerializer.Serialize(refusal.Value).Should().Contain("company_scope_partial");
    }

    [Fact]
    public async Task A_company_A_caller_cannot_edit_a_batch_that_spans_company_B()
    {
        var (conn, w) = await SeedMixedAsync();
        await using var _ = conn;
        var http = Http(w.Tenant, Writer, scopedToCompany: w.CoA.Id);
        await using var scopedDb = new ZayraDbContext(w.Options, new Accessor(http));
        scopedDb.EmployeeBonuses.Count(b => b.BonusBatchId == w.Batch.Id).Should().Be(1, "sanity: company B's line is hidden from this caller");

        var result = await Ctrl(scopedDb, http).UpdateBatch(w.Batch.Id, new UpdateBatchRequest("Hijacked", null, null, null), default);

        ShouldBePartialScopeRefusal(result);
        await using var group = new ZayraDbContext(w.Options);
        (await group.BonusBatches.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == w.Batch.Id)).BatchName.Should().Be("Original");
    }

    [Fact]
    public async Task A_company_A_caller_cannot_delete_a_batch_that_spans_company_B_and_nothing_is_orphaned()
    {
        var (conn, w) = await SeedMixedAsync();
        await using var _ = conn;
        var http = Http(w.Tenant, Writer, scopedToCompany: w.CoA.Id);
        await using var scopedDb = new ZayraDbContext(w.Options, new Accessor(http));

        var result = await Ctrl(scopedDb, http).DeleteBatch(w.Batch.Id, default);

        ShouldBePartialScopeRefusal(result);
        await using var group = new ZayraDbContext(w.Options);
        (await group.BonusBatches.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == w.Batch.Id)).IsDeleted.Should().BeFalse();
        var lines = await group.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking().Where(b => b.BonusBatchId == w.Batch.Id).ToListAsync();
        lines.Should().HaveCount(2);
        lines.Should().OnlyContain(b => !b.IsDeleted, "no line of the batch may be soft-deleted by a partial view");
    }

    [Fact]
    public async Task A_group_scope_caller_can_still_edit_and_delete_the_mixed_batch()
    {
        var (conn, w) = await SeedMixedAsync();
        await using var _ = conn;
        var http = Http(w.Tenant, Writer);
        await using var db = new ZayraDbContext(w.Options, new Accessor(http));

        (await Ctrl(db, http).UpdateBatch(w.Batch.Id, new UpdateBatchRequest("Renamed", null, null, null), default)).Should().BeOfType<OkObjectResult>();
        (await Ctrl(db, http).DeleteBatch(w.Batch.Id, default)).Should().BeOfType<OkObjectResult>();

        await using var group = new ZayraDbContext(w.Options);
        var lines = await group.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking().Where(b => b.BonusBatchId == w.Batch.Id).ToListAsync();
        lines.Should().HaveCount(2);
        lines.Should().OnlyContain(b => b.IsDeleted);
    }

    [Fact]
    public async Task A_company_A_caller_can_still_edit_a_batch_that_is_wholly_company_A()
    {
        var (conn, w) = await SeedMixedAsync();
        await using var _ = conn;
        await using (var setup = new ZayraDbContext(w.Options))
        {
            await setup.EmployeeBonuses.Where(b => b.BonusBatchId == w.Batch.Id && b.CompanyId == w.CoB.Id).ExecuteDeleteAsync();
            await setup.BonusBatches.Where(b => b.Id == w.Batch.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.EmployeeCount, 1));
        }
        var http = Http(w.Tenant, Writer, scopedToCompany: w.CoA.Id);
        await using var scopedDb = new ZayraDbContext(w.Options, new Accessor(http));

        (await Ctrl(scopedDb, http).UpdateBatch(w.Batch.Id, new UpdateBatchRequest("Renamed", null, null, null), default)).Should().BeOfType<OkObjectResult>();
    }

    // ── L2: who counts as a Finance actor ───────────────────────────────────────────────────────

    private static async Task SeedJournalAndAuditAsync(Mixed w)
    {
        await using var db = new ZayraDbContext(w.Options);
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = w.Tenant, SourceModule = "Bonus", SourceEntityId = w.Batch.Id, SourceEntityRef = w.Batch.BatchNumber, EventType = "BonusApproval",
            DebitAccount = "6100 - Employee Bonus Expense", CreditAccount = "2300 - Bonus Payable", Amount = 1_200m, Currency = "SAR",
            EntryDate = new DateOnly(2026, 6, 1), Period = "2026-06", Description = "Bonus accrual",
        });
        db.BonusAuditLogs.Add(new BonusAuditLog
        {
            TenantId = w.Tenant, BonusBatchId = w.Batch.Id, EntityType = "BonusBatch", Action = "BatchSubmitted",
            OldValuesJson = "{\"total\":0}", NewValuesJson = "{\"total\":1200}",
        });
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> GetBatchAsync(Mixed w, string[] permissions, string[]? roles)
    {
        var http = Http(w.Tenant, permissions, roles);
        await using var db = new ZayraDbContext(w.Options, new Accessor(http));
        var ok = (await Ctrl(db, http).GetBatch(w.Batch.Id, default)).Should().BeOfType<OkObjectResult>().Subject;
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    [Theory]
    [InlineData("Finance Approver")]
    [InlineData("Finance Controller")]
    public async Task A_finance_role_holding_finance_gl_read_sees_the_journal_and_the_audit_snapshots(string role)
    {
        var (conn, w) = await SeedMixedAsync();
        await using var _ = conn;
        await SeedJournalAndAuditAsync(w);

        var body = await GetBatchAsync(w, ["payroll.read", "finance.gl.read"], [role]);

        body.GetProperty("glEntries").GetArrayLength().Should().Be(1);
        body.GetProperty("auditLogs")[0].GetProperty("NewValuesJson").GetString().Should().Contain("1200");
    }

    [Fact]
    public async Task A_payroll_reader_without_finance_or_hr_standing_still_gets_neither()
    {
        var (conn, w) = await SeedMixedAsync();
        await using var _ = conn;
        await SeedJournalAndAuditAsync(w);

        var body = await GetBatchAsync(w, ["payroll.read"], ["Manager"]);

        body.GetProperty("glEntries").GetArrayLength().Should().Be(0);
        body.GetProperty("auditLogs")[0].GetProperty("NewValuesJson").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task The_Finance_and_Admin_roles_keep_the_journal_view_they_always_had()
    {
        var (conn, w) = await SeedMixedAsync();
        await using var _ = conn;
        await SeedJournalAndAuditAsync(w);

        foreach (var role in new[] { "Finance", "Admin" })
            (await GetBatchAsync(w, ["payroll.read"], [role])).GetProperty("glEntries").GetArrayLength().Should().Be(1, role);
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
