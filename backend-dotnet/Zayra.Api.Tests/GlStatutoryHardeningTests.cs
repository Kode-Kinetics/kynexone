using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Audit fixes for the rates and GL configuration surfaces (fix/gl-statutory). SQLite in-memory +
/// EnsureCreated, so conditional updates and transactions are real relational operations.
/// The proof that every company-overridable statutory key changes PAY lives in
/// <see cref="CompanyStatutoryOverrideAppliedTests"/> (Postgres, full payroll run).
/// </summary>
public class GlStatutoryHardeningTests
{
    private static readonly DateTime Since2020 = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static T Attach<T>(T c, Guid tenantId, IEnumerable<string> perms, Guid? userId = null, string? scopeJson = null, string? companyHeader = null)
        where T : ControllerBase
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
        };
        claims.AddRange(perms.Select(p => new Claim("permission", p)));
        if (scopeJson is not null) claims.Add(new Claim(EntityScopeContext.V2ClaimType, scopeJson));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        if (companyHeader is not null) http.Request.Headers[ZayraDbContext.CompanySelectionHeader] = companyHeader;
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return c;
    }

    private static readonly string[] GlPerms =
        { "finance.gl.read", "finance.gl.manage", "finance.gl.drivers.manage", FinanceGlController.PredicateAuthorPermission };

    private static FinanceGlController Gl(ZayraDbContext db, Guid tid, string[]? perms = null, Guid? uid = null, string? scope = null, string? header = null)
        => Attach(new FinanceGlController(db), tid, perms ?? GlPerms, uid, scope, header);

    private static RatesController Rates(ZayraDbContext db, Guid tid, string[] perms, Guid? uid = null)
    {
        var reader = new StatutoryRuleReader(db);
        return Attach(new RatesController(db, reader, new StatutoryRateResolver(db, reader)), tid, perms, uid);
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);
    private static List<object> Items(object value) => ((System.Collections.IEnumerable)value).Cast<object>().ToList();

    private static async Task<GlAccount> Account(ZayraDbContext db, Guid tid, string code, string name, string type, Guid? companyId = null, bool active = true)
    {
        var a = new GlAccount { TenantId = tid, CompanyId = companyId, Code = code, Name = name, AccountType = type, IsActive = active };
        db.GlAccounts.Add(a);
        await db.SaveChangesAsync();
        return a;
    }

    private static async Task PostJournal(ZayraDbContext db, Guid tid, string debit, string credit)
    {
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, SourceModule = "Payroll", SourceEntityId = Guid.NewGuid(), SourceEntityRef = "PR-1",
            EventType = "Accrual", DebitAccount = debit, CreditAccount = credit, Amount = 100m, Currency = "SAR",
            EntryDate = new DateOnly(2026, 6, 30), Period = "2026-06",
        });
        await db.SaveChangesAsync();
    }

    // ═══ FinanceGlController ════════════════════════════════════════════════════

    [Fact]
    public async Task Account_TypeAndCodeFormat_AreValidated()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var gl = Gl(db, tid);
        (await gl.CreateAccount(null, new GlAccountRequest("6001", "Thing", "Revenue"), default)).Should().BeOfType<BadRequestObjectResult>();
        (await gl.CreateAccount(null, new GlAccountRequest("60 01", "Thing", "Expense"), default)).Should().BeOfType<BadRequestObjectResult>();
        (await gl.CreateAccount(null, new GlAccountRequest(new string('1', 31), "Thing", "Expense"), default)).Should().BeOfType<BadRequestObjectResult>();
        var ok = (await gl.CreateAccount(null, new GlAccountRequest("6001.A-1", "Thing", "income"), default)).Should().BeOfType<OkObjectResult>().Subject;
        Prop(ok.Value!, "AccountType").Should().Be("Income", "the type is stored in its canonical spelling");
    }

    [Fact]
    public async Task Account_CodeAndName_AreImmutable_OnceAJournalNamesIt()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var posted = await Account(db, tid, "5001", "Basic", "Expense");
        var fresh = await Account(db, tid, "5050", "Unused", "Expense");
        await PostJournal(db, tid, "5001 - Basic", "2100 - Salaries Payable");
        var gl = Gl(db, tid);

        var rename = (await gl.UpdateAccount(posted.Id, false, new GlAccountRequest("5001", "Basic Pay", "Expense"), default)).Should().BeOfType<ConflictObjectResult>().Subject;
        Prop(rename.Value!, "code").Should().Be("GL_ACCOUNT_POSTED");
        (await gl.UpdateAccount(posted.Id, false, new GlAccountRequest("5009", "Basic", "Expense"), default)).Should().BeOfType<ConflictObjectResult>();
        (await gl.DeleteAccount(posted.Id, default)).Should().BeOfType<ConflictObjectResult>();
        (await db.GlAccounts.AsNoTracking().SingleAsync(a => a.Id == posted.Id)).Name.Should().Be("Basic");

        // An account no journal names may still be renamed and recoded.
        (await gl.UpdateAccount(fresh.Id, false, new GlAccountRequest("5051", "Renamed", "Expense"), default)).Should().BeOfType<OkObjectResult>();
        var audit = await db.AuditLogs.AsNoTracking().Where(a => a.Action == "finance.gl.account.updated").ToListAsync();
        audit.Should().ContainSingle().Which.Metadata.Should().Contain("5050").And.Contain("5051");
    }

    [Fact]
    public async Task Account_Deactivation_ChecksMappingsInEveryCompany_AndForceNoLongerBypasses()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var companyA = Guid.NewGuid();
        var tenantDefault = await Account(db, tid, "5001", "Basic", "Expense");
        // Company A maps a TENANT-DEFAULT account — the old same-scope check could not see this.
        db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tid, CompanyId = companyA, DriverKey = "EARN:BASIC", AccountId = tenantDefault.Id, IsActive = true });
        await db.SaveChangesAsync();

        var refused = (await Gl(db, tid).UpdateAccount(tenantDefault.Id, true, new GlAccountRequest("5001", "Basic", "Expense", IsActive: false), default))
            .Should().BeOfType<ConflictObjectResult>().Subject;
        Prop(refused.Value!, "code").Should().Be("GL_ACCOUNT_IN_USE");
        (await Gl(db, tid).DeleteAccount(tenantDefault.Id, default)).Should().BeOfType<ConflictObjectResult>();
        (await db.GlAccounts.AsNoTracking().SingleAsync(a => a.Id == tenantDefault.Id)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SystemDriver_CannotBeDeactivated_And_DriverKey_IsImmutable()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, default); await db.SaveChangesAsync();
        var sys = await db.GlDrivers.IgnoreQueryFilters().FirstAsync(d => d.TenantId == tid && d.Key == "NET_PAYABLE");
        var gl = Gl(db, tid);
        (await gl.UpdateDriver(sys.Id, new GlDriverRequest(sys.Key, sys.Label, sys.Category, sys.PostingSide, sys.DefaultCode, sys.DefaultName,
                sys.AccountType, sys.MatchSource, sys.MatchMode, sys.MatchComponentCode, sys.EmitsEmployerExpensePair, sys.PairedExpenseDriverKey, sys.SortOrder, IsActive: false), default))
            .Should().BeOfType<BadRequestObjectResult>();
        (await gl.UpdateDriver(sys.Id, new GlDriverRequest("NET_PAYABLE_2", sys.Label, sys.Category, sys.PostingSide, sys.DefaultCode, sys.DefaultName,
                sys.AccountType, sys.MatchSource, sys.MatchMode, sys.MatchComponentCode, sys.EmitsEmployerExpensePair, sys.PairedExpenseDriverKey, sys.SortOrder), default))
            .Should().BeOfType<BadRequestObjectResult>();
        (await db.GlDrivers.AsNoTracking().IgnoreQueryFilters().SingleAsync(d => d.Id == sys.Id)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Driver_Default_MustBeAnActiveVisibleAccount_NeedsManage_AndIsAuditedBeforeAfter()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, default); await db.SaveChangesAsync();
        await Account(db, tid, "5090", "Wellness Expense", "Expense");
        await Account(db, tid, "5091", "Old Wellness", "Expense", active: false);
        var gl = Gl(db, tid);
        GlDriverRequest Req(string code, string side = "DR") => new("EARN:WELLNESS", "Wellness", GlDriverCategories.Earning, side, code, "ignored", MatchMode: "Exact", MatchComponentCode: "WELLNESS");

        (await gl.CreateDriver(null, Req("9999"), default)).Should().BeOfType<BadRequestObjectResult>("no such account");
        (await gl.CreateDriver(null, Req("5091"), default)).Should().BeOfType<BadRequestObjectResult>("inactive account");
        (await gl.CreateDriver(null, Req("5090", side: "CR"), default)).Should().BeOfType<BadRequestObjectResult>("an earning debits");
        var driversOnly = Gl(db, tid, new[] { "finance.gl.read", "finance.gl.drivers.manage" });
        (await driversOnly.CreateDriver(null, Req("5090"), default)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);

        var created = (await gl.CreateDriver(null, Req("5090"), default)).Should().BeOfType<OkObjectResult>().Subject;
        var id = (Guid)Prop(Prop(created.Value!, "driver")!, "Id")!;
        (await db.GlDrivers.AsNoTracking().IgnoreQueryFilters().SingleAsync(d => d.Id == id)).DefaultName.Should().Be("Wellness Expense", "the label comes from the chart, not free text");

        await Account(db, tid, "5092", "Staff Wellness", "Expense");
        (await gl.UpdateDriver(id, Req("5092") with { Label = "Staff wellness" }, default)).Should().BeOfType<OkObjectResult>();
        var audit = (await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "finance.gl.driver.updated")).Metadata;
        using var doc = JsonDocument.Parse(audit);
        doc.RootElement.GetProperty("before").GetProperty("DefaultCode").GetString().Should().Be("5090");
        doc.RootElement.GetProperty("after").GetProperty("DefaultCode").GetString().Should().Be("5092");
        doc.RootElement.GetProperty("before").GetProperty("Label").GetString().Should().Be("Wellness");
        doc.RootElement.GetProperty("after").GetProperty("PostingSide").GetString().Should().Be("DR");
    }

    [Fact]
    public async Task Driver_Delete_IsRefused_WhileAnyCompanyMapsIt()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, default); await db.SaveChangesAsync();
        var acct = await Account(db, tid, "5090", "Wellness Expense", "Expense");
        var gl = Gl(db, tid);
        var created = (OkObjectResult)await gl.CreateDriver(null, new GlDriverRequest("EARN:WELLNESS", "Wellness", GlDriverCategories.Earning, "DR", "5090", "", MatchMode: "Exact", MatchComponentCode: "WELLNESS"), default);
        var id = (Guid)Prop(Prop(created.Value!, "driver")!, "Id")!;
        var company = Guid.NewGuid();
        (await gl.SetMappings(company, new() { new GlMappingRequest("EARN:WELLNESS", acct.Id) }, default)).Should().BeOfType<OkObjectResult>();
        (await gl.DeleteDriver(id, default)).Should().BeOfType<ConflictObjectResult>();
        (await gl.SetMappings(company, new(), default)).Should().BeOfType<OkObjectResult>();
        (await gl.DeleteDriver(id, default)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SetMappings_RefusesDuplicates_TypeMismatch_InactiveAccounts_AndAuditsCostCentres()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, default); await db.SaveChangesAsync();
        var expense = await Account(db, tid, "5201", "Basic B", "Expense");
        var liability = await Account(db, tid, "2201", "Payable B", "Liability");
        var inactive = await Account(db, tid, "5202", "Old", "Expense", active: false);
        var cc = new CostCenter { TenantId = tid, Code = "CC1", Name = "Riyadh" };
        db.CostCenters.Add(cc); await db.SaveChangesAsync();
        var gl = Gl(db, tid);

        (await gl.SetMappings(null, new() { new("EARN:BASIC", expense.Id), new("EARN:BASIC", expense.Id) }, default))
            .Should().BeOfType<BadRequestObjectResult>("a duplicate driver is a 400, not a unique-index 500");
        var mismatch = (await gl.SetMappings(null, new() { new("EARN:BASIC", liability.Id) }, default)).Should().BeOfType<BadRequestObjectResult>().Subject;
        Prop(mismatch.Value!, "code").Should().Be("GL_MAPPING_TYPE_MISMATCH");
        (await gl.SetMappings(null, new() { new("NET_PAYABLE", expense.Id) }, default)).Should().BeOfType<BadRequestObjectResult>("net pay is a liability");
        (await gl.SetMappings(null, new() { new("EARN:BASIC", inactive.Id) }, default)).Should().BeOfType<BadRequestObjectResult>();

        (await gl.SetMappings(null, new() { new("EARN:BASIC", expense.Id), new("NET_PAYABLE", liability.Id) }, default)).Should().BeOfType<OkObjectResult>();
        (await gl.SetMappings(null, new() { new("EARN:BASIC", expense.Id, cc.Id), new("NET_PAYABLE", liability.Id) }, default)).Should().BeOfType<OkObjectResult>();
        var last = await db.AuditLogs.AsNoTracking().Where(a => a.Action == "finance.gl.mapping.saved").OrderByDescending(a => a.CreatedAtUtc).FirstAsync();
        last.Metadata.Should().Contain(cc.Id.ToString(), "a cost-centre-only change is a recorded change");
    }

    [Fact]
    public async Task GroupAdmin_WithACompanySelected_CanStillWriteTenantDefaults()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var selected = Guid.NewGuid();
        var group = JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() });
        var gl = Gl(db, tid, scope: group, header: selected.ToString());
        (await gl.CreateAccount(null, new GlAccountRequest("7100", "Group default", "Expense"), default)).Should().BeOfType<OkObjectResult>();

        var scoped = JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { selected } });
        (await Gl(db, tid, scope: scoped).CreateAccount(null, new GlAccountRequest("7101", "Sneaky", "Expense"), default)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task PeriodReopen_NeedsItsOwnPermission_AndNeverTheCloser()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var closer = Guid.NewGuid();
        var withReopen = GlPerms.Append(FinanceGlController.PeriodReopenPermission).ToArray();
        (await Gl(db, tid, withReopen, closer).ClosePeriod("2026-06", null, new GlPeriodActionRequest("month end"), default)).Should().BeOfType<OkObjectResult>();

        (await Gl(db, tid).ReopenPeriod("2026-06", null, new GlPeriodActionRequest("fix"), default)).Should().BeOfType<ForbidResult>("finance.gl.manage alone cannot reopen");
        (await Gl(db, tid, withReopen, closer).ReopenPeriod("2026-06", null, new GlPeriodActionRequest("fix"), default))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        (await Gl(db, tid, withReopen).ReopenPeriod("2026-06", null, new GlPeriodActionRequest("fix"), default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public void PeriodReopenPermission_IsSeededToAdminOnly()
    {
        var src = File.ReadAllText(Path.Combine(ApiRoot(), "Infrastructure", "Seed", "AuthSeeder.cs"));
        src.Should().Contain("(\"finance.gl.period.reopen\", \"Finance\"");
        // Every named role bundle lists its keys explicitly or by a prefix that excludes "finance.".
        src.Split('\n').Where(l => l.Contains("\"finance.gl.period.reopen\"") && !l.TrimStart().StartsWith("(")).Should().BeEmpty();
    }

    private static string ApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("Zayra.Api source root not found.");
    }

    // ── Chart-of-accounts CSV ──────────────────────────────────────────────────

    private static IFormFile CsvFile(string content) =>
        new FormFile(new MemoryStream(Encoding.UTF8.GetBytes(content)), 0, Encoding.UTF8.GetByteCount(content), "file", "coa.csv");

    [Fact]
    public async Task ChartCsv_ExportsTheScope_AndImportIsAllOrNothing()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await Account(db, tid, "5001", "Basic", "Expense");
        await Account(db, tid, "2100", "Salaries Payable", "Liability");
        await PostJournal(db, tid, "5001 - Basic", "2100 - Salaries Payable");
        var gl = Gl(db, tid);

        var file = (await gl.ExportAccounts(null, default)).Should().BeOfType<FileContentResult>().Subject;
        var csv = Encoding.UTF8.GetString(file.FileContents);
        csv.Should().StartWith("Code,Name,Type,IsActive").And.Contain("5001,Basic,Expense,true").And.Contain("2100,Salaries Payable,Liability,true");

        // Dry run (the default): counts, no writes.
        var good = "Code,Name,Type,IsActive\n5001,Basic,Expense,true\n2100,Salaries Payable,Liability,false\n6100,Bonus,Expense,true\n";
        var preview = (await gl.ImportAccounts(null, null, CsvFile(good), default)).Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop(preview, "dryRun").Should().Be(true);
        Prop(preview, "created").Should().Be(1);
        Prop(preview, "updated").Should().Be(1);
        Prop(preview, "unchanged").Should().Be(1);
        (await db.GlAccounts.CountAsync(a => a.TenantId == tid)).Should().Be(2);

        // One bad row (a renamed POSTED account) → nothing written, every error named with its row.
        var bad = "Code,Name,Type,IsActive\n6100,Bonus,Expense,true\n5001,Basic Salary,Expense,true\n7000,Bad,Revenue,true\n";
        var refused = (await gl.ImportAccounts(null, false, CsvFile(bad), default)).Should().BeOfType<UnprocessableEntityObjectResult>().Subject.Value!;
        var errors = Items(Prop(refused, "errors")!);
        errors.Select(e => Prop(e, "row")).Should().BeEquivalentTo(new object[] { 3, 4 });
        (await db.GlAccounts.CountAsync(a => a.TenantId == tid)).Should().Be(2, "all or nothing");

        // Commit the good file.
        var applied = (await gl.ImportAccounts(null, false, CsvFile(good), default)).Should().BeOfType<OkObjectResult>().Subject.Value!;
        Prop(applied, "created").Should().Be(1);
        (await db.GlAccounts.AsNoTracking().SingleAsync(a => a.TenantId == tid && a.Code == "2100")).IsActive.Should().BeFalse();
        (await db.AuditLogs.CountAsync(a => a.Action == "finance.gl.account.imported")).Should().Be(1);

        // Same permission as create.
        (await Gl(db, tid, new[] { "finance.gl.read" }).ImportAccounts(null, true, CsvFile(good), default)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ChartCsv_Import_RefusesDeactivatingAMappedAccount_AndDuplicateCodes()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var basic = await Account(db, tid, "5001", "Basic", "Expense");
        db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tid, CompanyId = Guid.NewGuid(), DriverKey = "EARN:BASIC", AccountId = basic.Id, IsActive = true });
        await db.SaveChangesAsync();
        var body = (await Gl(db, tid).ImportAccounts(null, true, CsvFile("Code,Name,Type,IsActive\n5001,Basic,Expense,false\n6000,A,Expense,true\n6000,B,Expense,true\n"), default))
            .Should().BeOfType<OkObjectResult>().Subject.Value!;
        Items(Prop(body, "errors")!).Should().HaveCount(2);
    }

    // ═══ Statutory overrides ════════════════════════════════════════════════════

    private static void PlatformRule(ZayraDbContext db, string key, string value, string description = "KSA Labour Law")
        => db.StatutoryRules.Add(new StatutoryRule { TenantId = null, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = key, RuleValue = value, DataType = "decimal", Description = description, EffectiveFrom = Since2020 });

    private static readonly string[] Override = { "payroll.rates.statutory_override" };

    [Fact]
    public async Task ApprovedOverride_IsWhatTheCompanyAwareReaderReturns_ForThatCompanyOnly()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        PlatformRule(db, "ot.standard_multiplier", "1.5"); await db.SaveChangesAsync();
        var maker = Guid.NewGuid();
        var created = (OkObjectResult)await Rates(db, tid, Override, maker).CreateStatutoryOverride(new StatutoryOverrideRequest(a, "SAU", "KSA-mainland", "ot.standard_multiplier", "2", new DateOnly(2026, 1, 1), "CBA clause 7", new DateOnly(2027, 1, 1)), default);
        var id = (Guid)Prop(created.Value!, "Id")!;
        (await Rates(db, tid, Override).ApproveStatutoryOverride(id, default)).Should().BeOfType<OkObjectResult>();

        var reader = new StatutoryRuleReader(db);
        var june = new DateOnly(2026, 6, 1);
        (await reader.GetDecimalAsync("SAU", "KSA-mainland", "ot.standard_multiplier", june, tid, default, a)).Should().Be(2m);
        (await reader.GetDecimalAsync("SAU", "KSA-mainland", "ot.standard_multiplier", june, tid, default, b)).Should().Be(1.5m, "the memo is keyed per company");
        (await reader.GetDecimalAsync("SAU", "KSA-mainland", "ot.standard_multiplier", june, tid)).Should().Be(1.5m, "a company-less read never sees an override");
        var ctx = await reader.ResolveAsync("SAU", "KSA-mainland", june, tid, default, companyId: a);
        ctx.StandardMultiplier.Should().Be(2m);
    }

    [Fact]
    public async Task LegacyRows_ThatPayrollWouldIgnoreOrThatBreakAFloor_AreRefusedAtApprove_AndIgnoredAtRead()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var company = Guid.NewGuid();
        PlatformRule(db, "ot.standard_multiplier", "1.5");
        PlatformRule(db, "eosb.days_per_year_1to5", "15");
        var maker = Guid.NewGuid();
        const string eosbTier1Rule = "eosb.days_per_year_1to5";
        var notApplied = new CompanyStatutoryOverride { TenantId = tid, CompanyId = company, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = eosbTier1Rule, OverrideValue = "20", EffectiveFrom = new DateOnly(2026, 1, 1), ReviewBy = new DateOnly(2027, 1, 1), Reason = "legacy", Status = "PendingApproval", CreatedBy = maker };
        var belowFloor = new CompanyStatutoryOverride { TenantId = tid, CompanyId = company, CountryCode = "SAU", Jurisdiction = "KSA-mainland", RuleKey = "ot.standard_multiplier", OverrideValue = "1.25", EffectiveFrom = new DateOnly(2026, 1, 1), ReviewBy = new DateOnly(2027, 1, 1), Reason = "legacy", Status = "PendingApproval", CreatedBy = maker };
        db.AddRange(notApplied, belowFloor); await db.SaveChangesAsync();

        var checker = Rates(db, tid, Override);
        var r1 = (await checker.ApproveStatutoryOverride(notApplied.Id, default)).Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        Prop(r1.Value!, "code").Should().Be(StatutoryOverridePolicy.NotAppliedCode);
        var r2 = (await checker.ApproveStatutoryOverride(belowFloor.Id, default)).Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        Prop(r2.Value!, "code").Should().Be(StatutoryOverridePolicy.FloorCode);

        // A below-floor row made Active before this check existed is never paid.
        await db.CompanyStatutoryOverrides.IgnoreQueryFilters().Where(o => o.Id == belowFloor.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "Active"));
        (await new StatutoryRuleReader(db).GetDecimalAsync("SAU", "KSA-mainland", "ot.standard_multiplier", new DateOnly(2026, 6, 1), tid, default, company)).Should().Be(1.5m);
    }

    [Fact]
    public async Task Ceiling_StandardMonthlyHours_MayOnlyGoDown()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        PlatformRule(db, "ot.standard_monthly_hours", "240"); await db.SaveChangesAsync();
        var r = Rates(db, tid, Override);
        (await r.CreateStatutoryOverride(new StatutoryOverrideRequest(Guid.NewGuid(), "SAU", "KSA-mainland", "ot.standard_monthly_hours", "260", new DateOnly(2026, 1, 1), "x", new DateOnly(2027, 1, 1)), default))
            .Should().BeOfType<UnprocessableEntityObjectResult>();
        (await r.CreateStatutoryOverride(new StatutoryOverrideRequest(Guid.NewGuid(), "SAU", "KSA-mainland", "ot.standard_monthly_hours", "200", new DateOnly(2026, 1, 1), "x", new DateOnly(2027, 1, 1)), default))
            .Should().BeOfType<OkObjectResult>();
        (await r.CreateStatutoryOverride(new StatutoryOverrideRequest(Guid.NewGuid(), "SAU", "KSA-mainland", "ot.standard_monthly_hours", "200", new DateOnly(2026, 1, 1), "x", new DateOnly(2025, 1, 1)), default))
            .Should().BeOfType<BadRequestObjectResult>("review date before the start");
        (await r.CreateStatutoryOverride(new StatutoryOverrideRequest(Guid.NewGuid(), "SAU", "KSA-mainland", "ot.standard_monthly_hours", "200", new DateOnly(2026, 1, 1), "x", new DateOnly(2027, 1, 1), EffectiveTo: new DateOnly(2025, 12, 31)), default))
            .Should().BeOfType<BadRequestObjectResult>("end before the start");
    }

    [Fact]
    public async Task Approve_ClaimsTheRequestOnce()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var company = Guid.NewGuid();
        PlatformRule(db, "ot.standard_multiplier", "1.5"); await db.SaveChangesAsync();
        var created = (OkObjectResult)await Rates(db, tid, Override).CreateStatutoryOverride(new StatutoryOverrideRequest(company, "SAU", "KSA-mainland", "ot.standard_multiplier", "1.75", new DateOnly(2026, 1, 1), "policy", new DateOnly(2027, 1, 1)), default);
        var id = (Guid)Prop(created.Value!, "Id")!;
        (await Rates(db, tid, Override).ApproveStatutoryOverride(id, default)).Should().BeOfType<OkObjectResult>();
        (await Rates(db, tid, Override).ApproveStatutoryOverride(id, default)).Should().BeOfType<ConflictObjectResult>();
        (await Rates(db, tid, Override).RejectStatutoryOverride(id, null, default)).Should().BeOfType<ConflictObjectResult>();
        (await db.AuditLogs.CountAsync(a => a.Action == "payroll.rate.statutory_override.approved")).Should().Be(1);
    }

    [Fact]
    public async Task StatutoryList_CompanyScope_CarriesTheUiContract()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "T", Slug = $"t-{tid:N}" });
        var co = new Company { TenantId = tid, LegalNameEn = "Acme KSA", CountryCode = "SAU", Jurisdiction = "KSA-mainland", DefaultCurrency = "SAR" };
        db.Companies.Add(co);
        PlatformRule(db, "ot.standard_multiplier", "1.5", "Art. 107");
        PlatformRule(db, "lop.monthly_day_divisor", "30", "Common practice — VERIFY with counsel");
        PlatformRule(db, "nitaqat.default_target_ratio", "0.09");
        var maker = new User { TenantId = tid, Email = "maker@acme.test", FullName = "Mona Maker" };
        db.Users.Add(maker);
        await db.SaveChangesAsync();
        await Rates(db, tid, Override, maker.Id).CreateStatutoryOverride(new StatutoryOverrideRequest(co.Id, "SAU", "KSA-mainland", "ot.standard_multiplier", "2", new DateOnly(2026, 1, 1), "CBA", new DateOnly(2027, 1, 1)), default);

        Dictionary<string, object> Row(IActionResult r, string key) => Items(((OkObjectResult)r).Value!).Cast<object>()
            .Where(o => (string)Prop(o, "ruleKey")! == key).Select(o => o.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(o)!)).Single();

        var asMaker = await Rates(db, tid, Override, maker.Id).ListStatutory(co.Id, null, null, null, default);
        var ot = Row(asMaker, "ot.standard_multiplier");
        ot["countryCode"].Should().Be("SAU");
        ot["ruleEffectiveFrom"].Should().Be(new DateOnly(2020, 1, 1));
        ot["source"].Should().Be("Art. 107");
        ot["verified"].Should().Be(true);
        ot["appliedByPayroll"].Should().Be(true);
        ot["direction"].Should().Be("floor");
        ot["displayUnit"].Should().Be("multiplier");
        var pending = ot["pendingOverride"];
        Prop(pending, "value").Should().Be("2");
        Prop(pending, "requestedByName").Should().Be("Mona Maker");
        Prop(pending, "canApprove").Should().Be(false, "the requester never decides");
        Row(asMaker, "lop.monthly_day_divisor")["verified"].Should().Be(false);
        Row(asMaker, "nitaqat.default_target_ratio")["appliedByPayroll"].Should().Be(false);

        var asChecker = await Rates(db, tid, Override).ListStatutory(co.Id, null, null, null, default);
        Prop(Row(asChecker, "ot.standard_multiplier")["pendingOverride"], "canApprove").Should().Be(true);
        var readOnly = await Rates(db, tid, new[] { "payroll.rates.read" }).ListStatutory(co.Id, null, null, null, default);
        Prop(Row(readOnly, "ot.standard_multiplier")["pendingOverride"], "canApprove").Should().Be(false, "no override permission");

        // Group scope: no 400, read-only rows per country with the override count.
        var group = await Rates(db, tid, new[] { "payroll.rates.read" }).ListStatutory(null, null, null, null, default);
        var g = Row(group, "ot.standard_multiplier");
        g["countryCode"].Should().Be("SAU");
        g["overriddenInCompanies"].Should().Be(0, "the request is still pending");
        g["readOnly"].Should().Be(true);

        var queue = Items(((OkObjectResult)await Rates(db, tid, Override).ListPendingStatutory(default)).Value!);
        var item = queue.Should().ContainSingle().Subject;
        Prop(item, "companyName").Should().Be("Acme KSA");
        Prop(item, "ruleKey").Should().Be("ot.standard_multiplier");
        Prop(item, "canApprove").Should().Be(true);
    }

    // ═══ Company rates ═══════════════════════════════════════════════════════════

    private static readonly string[] RatePerms = { "payroll.rates.read", "payroll.rates.manage" };

    // Pinned on keys payroll actually reads (ProrationPolicyResolver). The allowance / deduction / EOSB
    // enhancement keys are recorded only and refused for new values — CompanyRateAppliedByPayrollTests.
    private const string Lookback = "payparameter.arrears_max_lookback_months";
    private const string Basis = "payparameter.proration_basis";

    [Fact]
    public async Task CompanyRate_KeyIsCanonical_Immutable_AndDatesAreValidated()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid(); var company = Guid.NewGuid();
        var r = Rates(db, tid, RatePerms);
        var created = (await r.CreateCompanyRate(company, new CompanyRateRequest("PayParameter.Arrears_Max_Lookback_Months", "24", new DateOnly(2026, 1, 1)), default)).Should().BeOfType<OkObjectResult>().Subject;
        Prop(created.Value!, "RateKey").Should().Be(Lookback);
        Prop(created.Value!, "label").Should().Be("How many past periods a retro/arrears settlement may reach back over (default 24)");
        Prop(created.Value!, "unit").Should().Be("months");
        Prop(created.Value!, "inEffect").Should().Be(true);
        Prop(created.Value!, "appliedByPayroll").Should().Be(true);
        var id = (Guid)Prop(created.Value!, "Id")!;

        (await r.UpdateCompanyRate(id, new CompanyRateRequest(Basis, "Calendar30", new DateOnly(2026, 6, 1)), default)).Should().BeOfType<BadRequestObjectResult>("the key never changes");
        (await r.UpdateCompanyRate(id, new CompanyRateRequest(Lookback, "36", new DateOnly(2025, 6, 1)), default)).Should().BeOfType<BadRequestObjectResult>("a new value must start later");
        (await r.CreateCompanyRate(company, new CompanyRateRequest(Basis, "Calendar30", new DateOnly(2026, 6, 1), new DateOnly(2026, 5, 1)), default)).Should().BeOfType<BadRequestObjectResult>("end before start");
        (await r.DeleteCompanyRate(id, default)).Should().BeOfType<ConflictObjectResult>("in effect → end-date it instead");

        // End-date in place: same value and start, new end.
        (await r.UpdateCompanyRate(id, new CompanyRateRequest(Lookback, "24", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)), default)).Should().BeOfType<OkObjectResult>();
        var future = (OkObjectResult)await r.CreateCompanyRate(company, new CompanyRateRequest(Basis, "Calendar30", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)), default);
        (await r.DeleteCompanyRate((Guid)Prop(future.Value!, "Id")!, default)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task CompanyRate_StartingInsideALockedRunOrClosedPeriod_IsRefused()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "T", Slug = $"t-{tid:N}" });
        var co = new Company { TenantId = tid, LegalNameEn = "Locked Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland", DefaultCurrency = "SAR" };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        var company = co.Id;
        db.PayrollRuns.Add(new PayrollRun { TenantId = tid, CompanyId = company, Year = 2026, Month = 5, Status = "Locked" });
        db.GlPeriodCloses.Add(new GlPeriodClose { TenantId = tid, CompanyId = null, Period = "2026-07", Status = GlPeriodStatuses.Closed });
        await db.SaveChangesAsync();
        var r = Rates(db, tid, RatePerms);
        var locked = (await r.CreateCompanyRate(company, new CompanyRateRequest(Lookback, "12", new DateOnly(2026, 5, 10)), default)).Should().BeOfType<ConflictObjectResult>().Subject;
        ((string)Prop(locked.Value!, "message")!).Should().Contain("2026-05").And.Contain("locked");
        var closed = (await r.CreateCompanyRate(company, new CompanyRateRequest(Lookback, "12", new DateOnly(2026, 6, 10)), default)).Should().BeOfType<ConflictObjectResult>().Subject;
        ((string)Prop(closed.Value!, "message")!).Should().Contain("2026-07").And.Contain("closed");
        (await r.CreateCompanyRate(company, new CompanyRateRequest(Lookback, "12", new DateOnly(2026, 8, 1)), default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task EosbEnhancementDays_IsNotStatutory_ButIsRefusedAsRecordedOnly()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        // It clears the statutory-prefix guard (an enhancement, not the floor) ...
        StatutoryRateGuard.IsStatutoryKey("eosb.enhancement_days_per_year").Should().BeFalse();
        StatutoryRateGuard.IsStatutoryKey("eosb.days_per_year_1to5").Should().BeTrue("the statutory floor itself stays off free CRUD");
        // ... but no EOSB calculation reads it, so a value would change nobody's award.
        var refused = (await Rates(db, tid, RatePerms).CreateCompanyRate(Guid.NewGuid(), new CompanyRateRequest("eosb.enhancement_days_per_year", "5", new DateOnly(2026, 1, 1)), default))
            .Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        Prop(refused.Value!, "code").Should().Be("COMPANY_RATE_NOT_APPLIED");
        ((string)Prop(refused.Value!, "message")!).Should().Contain("GCC Settings");
    }

    [Fact]
    public void Policy_DirectionsAndUnits()
    {
        StatutoryOverridePolicy.DirectionFor("ot.holiday_multiplier").Should().Be("floor");
        StatutoryOverridePolicy.DirectionFor("workhours.ramadan_minutes_per_day").Should().Be("ceiling");
        StatutoryOverridePolicy.DirectionFor("ot.standard_monthly_hours").Should().Be("ceiling");
        StatutoryOverridePolicy.DirectionFor("ot.hourly_base").Should().Be("floor", "basic → wage only: wage → basic cuts KSA overtime ~30%");
        StatutoryOverridePolicy.DisplayUnitFor("gosi.saudi_employee_rate").Should().Be("percent");
        StatutoryOverridePolicy.DisplayUnitFor("leave.annual_base_days").Should().Be("days");
        // Minutes were once reported as "hours", so 480 minutes read as "480 hours" in GL & rates.
        StatutoryOverridePolicy.DisplayUnitFor("ramadan.muslim_minutes_per_day").Should().Be("minutes");
        StatutoryOverridePolicy.IsVerified("Value NOT independently confirmed").Should().BeFalse();
        StatutoryOverridePolicy.CompanyAppliedKeys.Should().BeEquivalentTo(new[]
        {
            "ot.standard_multiplier", "ot.restday_multiplier", "ot.holiday_multiplier", "ot.standard_monthly_hours",
            "lop.monthly_day_divisor", "lop.standard_work_minutes_per_day",
        }, "each one is proven to change pay by CompanyStatutoryOverrideAppliedTests — add a key only with a payroll test");
    }
}
