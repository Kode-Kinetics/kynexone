using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Payroll GL posting integrity (audit fixes 2–7): absence deductions are contra-expenses, catch-all
/// routing is reported, a switched-off account or system posting rule blocks Lock, the stored journal
/// balances exactly, every legal entity balances, and the GL preview is the journal Lock posts.
/// SQLite in-memory (relational, so Lock runs its real transaction); concurrency is proven on Postgres
/// in <see cref="GlPostingConcurrencyPostgresTests"/>.
/// </summary>
public class GlPostingIntegrityTests
{
    // ── 2. Absence deductions are contra-expenses, never uncleared payables ──────────────────────────

    [Theory]
    [InlineData(false)] // no driver store: compiled catalog
    [InlineData(true)]  // freshly seeded tenant: gl_drivers + chart + mappings
    public async Task AttendanceLopAndLeaveDeductions_CreditContraExpenseAccounts_NotPayables(bool seeded)
    {
        await using var k = await GlPostingKit.CreateAsync();
        if (seeded) await k.SeedGlDefaultsAsync();
        var run = await k.SeedApprovedRunAsync(
            earnings: new[] { (1, "BASIC", "Salary", 10_000m) },
            deductions: new[] { (1, "ATTENDANCE", "Attendance", 120m), (1, "LOP_DEDUCTION", "Attendance", 300m), (1, "LEAVE", "Leave", 200m) },
            net: 9_380m);

        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();

        var gl = await k.RunGlAsync(run.Id);
        gl.Where(l => l.CreditAccount == "5008 - Attendance & Late Deductions (contra)").Sum(l => l.Amount).Should().Be(420m);
        gl.Where(l => l.CreditAccount == "5009 - Unpaid Leave & LOP Deductions (contra)").Sum(l => l.Amount).Should().Be(200m);
        gl.Should().NotContain(l => l.CreditAccount.StartsWith("2104") || l.CreditAccount.StartsWith("2105"),
            "nothing ever remits or clears 2104/2105, so crediting them leaves a payable nobody owes");
    }

    [Fact]
    public async Task SeededChart_CarriesTheContraAccountsAsExpense_AndTheRoundingRule()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var accounts = await k.Db.GlAccounts.AsNoTracking().Where(a => a.TenantId == k.TenantId).ToListAsync();
        accounts.Single(a => a.Code == "5008").AccountType.Should().Be("Expense");
        accounts.Single(a => a.Code == "5009").AccountType.Should().Be("Expense");
        accounts.Single(a => a.Code == "5199").Name.Should().Be("Payroll Rounding Differences");
        accounts.Should().NotContain(a => a.Code == "2104" || a.Code == "2105");
        var drivers = await k.Db.GlDrivers.AsNoTracking().Where(d => d.TenantId == k.TenantId).ToListAsync();
        drivers.Single(d => d.Key == "DED:ATTENDANCE").Should().Match<GlDriver>(d => d.DefaultCode == "5008" && d.AccountType == "Expense" && d.PostingSide == "CR");
        drivers.Single(d => d.Key == "DED:LEAVE").Should().Match<GlDriver>(d => d.DefaultCode == "5009" && d.AccountType == "Expense" && d.PostingSide == "CR");
        drivers.Single(d => d.Key == "ROUNDING").Category.Should().Be(GlDriverCategories.Balancing);
    }

    // ── 3. Catch-all routing is reported, never silent, never blocking ──────────────────────────────

    [Fact]
    public async Task CatchAllComponents_AreReportedInPreview_LockResponse_AndLockAudit_WithoutBlocking()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var run = await k.SeedApprovedRunAsync(
            earnings: new[] { (1, "BASIC", "Salary", 5_000m), (1, "EID_GIFT", "Adjustment", 250m) },
            deductions: new[] { (1, "CANTEEN", "Adjustment", 40m) },
            net: 5_210m);

        var preview = await k.JournalJsonAsync(run.Id);
        preview.GetProperty("isPosted").GetBoolean().Should().BeFalse();
        CatchAllCodes(preview).Should().BeEquivalentTo("EID_GIFT:EARN:OTHER", "CANTEEN:DED:OTHER");

        var locked = await k.Payroll().Lock(run.Id, default);
        var body = JsonSerializer.SerializeToElement(locked.Should().BeOfType<OkObjectResult>().Subject.Value);
        CatchAllCodes(body).Should().BeEquivalentTo("EID_GIFT:EARN:OTHER", "CANTEEN:DED:OTHER");
        body.GetProperty("catchAllComponents").EnumerateArray().Single(c => c.GetProperty("componentCode").GetString() == "EID_GIFT")
            .GetProperty("amount").GetDecimal().Should().Be(250m);

        var audit = await k.Db.PayrollAuditLogs.AsNoTracking()
            .SingleAsync(a => a.TenantId == k.TenantId && a.Action == "payroll.run.locked" && a.EntityId == run.Id.ToString());
        audit.MetadataJson.Should().Contain("catchAllComponents").And.Contain("EID_GIFT").And.Contain("DED:OTHER");
    }

    private static IEnumerable<string> CatchAllCodes(JsonElement body) =>
        body.GetProperty("catchAllComponents").EnumerateArray()
            .Select(c => $"{c.GetProperty("componentCode").GetString()}:{c.GetProperty("driverKey").GetString()}");

    // ── 4. Switched-off account / system rule fails CLOSED ──────────────────────────────────────────

    [Fact]
    public async Task MappingToAnInactiveAccount_BlocksLock_NamingTheRuleAndTheAccount()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        // Housing is used by the run; overtime is not — an inactive account on an unused rule must not block.
        foreach (var code in new[] { "5002", "5005" })
            (await k.Db.GlAccounts.SingleAsync(a => a.TenantId == k.TenantId && a.Code == code)).IsActive = false;
        await k.Db.SaveChangesAsync();
        var run = await k.SeedApprovedRunAsync(
            earnings: new[] { (1, "BASIC", "Salary", 5_000m), (1, "HOUSING", "Salary", 1_250m) },
            deductions: Array.Empty<(int, string, string, decimal)>(), net: 6_250m);

        var preview = await k.JournalJsonAsync(run.Id);
        preview.GetProperty("blockingProblems").EnumerateArray().Select(p => p.GetProperty("driverKey").GetString())
            .Should().Equal("EARN:HOUSING");

        var res = await k.Payroll().Lock(run.Id, default);
        var body = JsonSerializer.SerializeToElement(res.Should().BeOfType<UnprocessableEntityObjectResult>().Subject.Value);
        body.GetProperty("error").GetString().Should().Be("gl_posting_rule_inactive");
        var problem = body.GetProperty("problems").EnumerateArray().Single();
        problem.GetProperty("driverKey").GetString().Should().Be("EARN:HOUSING");
        problem.GetProperty("kind").GetString().Should().Be("InactiveAccount");
        problem.GetProperty("accountCode").GetString().Should().Be("5002");
        body.GetProperty("message").GetString().Should().Contain("5002").And.Contain("switched off");

        (await k.RunGlAsync(run.Id)).Should().BeEmpty("a refused lock posts nothing");
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).Status.Should().Be("Approved");
    }

    [Fact]
    public async Task CompanyMappingToAnInactiveAccount_Blocks_EvenWhenTheGroupMappingIsActive()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var off = new GlAccount { TenantId = k.TenantId, CompanyId = k.CompanyId, Code = "6001", Name = "Co Basic", AccountType = "Expense", IsActive = false };
        k.Db.GlAccounts.Add(off);
        k.Db.GlAccountMappings.Add(new GlAccountMapping { TenantId = k.TenantId, CompanyId = k.CompanyId, DriverKey = "EARN:BASIC", AccountId = off.Id });
        await k.Db.SaveChangesAsync();
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) }, Array.Empty<(int, string, string, decimal)>(), 5_000m);

        var res = await k.Payroll().Lock(run.Id, default);
        var body = JsonSerializer.SerializeToElement(res.Should().BeOfType<UnprocessableEntityObjectResult>().Subject.Value);
        body.GetProperty("problems").EnumerateArray().Single().GetProperty("accountCode").GetString()
            .Should().Be("6001", "the company chose 6001; silently posting to the group's 5001 instead is the defect");
    }

    [Fact]
    public async Task ASwitchedOffSystemPostingRule_BlocksLock()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        (await k.Db.GlDrivers.SingleAsync(d => d.TenantId == k.TenantId && d.Key == "EARN:BASIC")).IsActive = false;
        await k.Db.SaveChangesAsync();
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) }, Array.Empty<(int, string, string, decimal)>(), 5_000m);

        var res = await k.Payroll().Lock(run.Id, default);
        var body = JsonSerializer.SerializeToElement(res.Should().BeOfType<UnprocessableEntityObjectResult>().Subject.Value);
        var problem = body.GetProperty("problems").EnumerateArray().Single();
        problem.GetProperty("driverKey").GetString().Should().Be("EARN:BASIC");
        problem.GetProperty("kind").GetString().Should().Be("InactiveDriver");
        (await k.RunGlAsync(run.Id)).Should().BeEmpty("basic salary must not quietly fall through to 5099 Other Earnings");
    }

    // ── 5. The stored journal balances exactly ───────────────────────────────────────────────────────

    [Fact]
    public async Task ASubUnitDifference_IsBookedToPayrollRounding_SoTheStoredJournalBalancesExactly()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        // Two employees ⇒ tolerance 0.02. Net is 0.01 short of earnings − deductions.
        var run = await k.SeedApprovedRunAsync(
            new[] { (1, "BASIC", "Salary", 5_000m), (2, "BASIC", "Salary", 5_000m) },
            Array.Empty<(int, string, string, decimal)>(), net: 9_999.99m);

        var res = await k.Payroll().Lock(run.Id, default);
        var body = JsonSerializer.SerializeToElement(res.Should().BeOfType<OkObjectResult>().Subject.Value);
        body.GetProperty("roundingAdjustment").GetDecimal().Should().Be(-0.01m, "debits exceeded credits, so rounding is a credit");

        var gl = await k.RunGlAsync(run.Id);
        var rounding = gl.Single(l => l.Description == PayrollGlDescriptions.Rounding);
        rounding.CreditAccount.Should().Be("5199 - Payroll Rounding Differences");
        rounding.Amount.Should().Be(0.01m);
        rounding.CompanyId.Should().Be(k.CompanyId);
        GlPostingKit.Debits(gl).Should().Be(GlPostingKit.Credits(gl), "the STORED journal must balance to the halala");
    }

    [Theory]
    [InlineData(1, 0.02)]    // one employee: tolerance 0.01
    [InlineData(150, 1.01)]  // tolerance caps at 1.00 however many employees
    public async Task ADifferenceAboveTolerance_StillBlocks(int employees, double shortBy)
    {
        await using var k = await GlPostingKit.CreateAsync();
        var earnings = Enumerable.Range(1, employees).Select(i => (i, "BASIC", "Salary", 100m)).ToArray();
        var run = await k.SeedApprovedRunAsync(earnings, Array.Empty<(int, string, string, decimal)>(),
            net: employees * 100m - (decimal)shortBy);

        var res = await k.Payroll().Lock(run.Id, default);
        JsonSerializer.SerializeToElement(res.Should().BeOfType<UnprocessableEntityObjectResult>().Subject.Value)
            .GetProperty("error").GetString().Should().Be("gl_unbalanced");
        (await k.RunGlAsync(run.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task ADifferenceAtTheCap_IsRounded()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var earnings = Enumerable.Range(1, 150).Select(i => (i, "BASIC", "Salary", 100m)).ToArray();
        var run = await k.SeedApprovedRunAsync(earnings, Array.Empty<(int, string, string, decimal)>(), net: 15_001.00m);

        var body = JsonSerializer.SerializeToElement(
            (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>().Subject.Value);
        body.GetProperty("roundingAdjustment").GetDecimal().Should().Be(1.00m, "credits exceeded debits, so rounding is a debit");
        var gl = await k.RunGlAsync(run.Id);
        gl.Single(l => l.Description == PayrollGlDescriptions.Rounding).DebitAccount.Should().Be("5199 - Payroll Rounding Differences");
        GlPostingKit.Debits(gl).Should().Be(GlPostingKit.Credits(gl));
    }

    // ── 6 + 7. Per-company balance; preview == post with a final settlement ─────────────────────────

    [Fact]
    public async Task ARunPayingASettlement_PreviewsExactlyTheLinesItPosts()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var run = await k.SeedApprovedRunAsync(
            new[] { (1, "BASIC", "Salary", 4_000m), (1, FinalSettlementComponents.Gratuity, FinalSettlementComponents.SettlementSource, 3_000m) },
            new[] { (1, FinalSettlementComponents.NoticeShortfall, FinalSettlementComponents.SettlementSource, 500m) },
            net: 6_500m);
        await k.SeedSettlementAccrualAsync(run.Id, k.CompanyId, 3_000m);

        var preview = await k.JournalJsonAsync(run.Id);
        preview.GetProperty("isPosted").GetBoolean().Should().BeFalse();
        var previewLines = GlPostingKit.Lines(preview);
        previewLines.Should().Contain("DR|2320|3000.00", "the gratuity was expensed at approval; paying it clears the payable");
        previewLines.Should().NotContain(l => l.StartsWith("DR|5110"), "a preview must not show an expense the posting never books");

        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();
        var posted = await k.JournalJsonAsync(run.Id);
        posted.GetProperty("isPosted").GetBoolean().Should().BeTrue();
        GlPostingKit.Lines(posted).Should().BeEquivalentTo(previewLines);
    }

    [Fact]
    public async Task AClearingBookedToAnotherCompany_BlocksLock_WithAPerCompanyBreakdown()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var other = await k.AddCompanyAsync();
        var run = await k.SeedApprovedRunAsync(
            new[] { (1, "BASIC", "Salary", 4_000m), (1, FinalSettlementComponents.Gratuity, FinalSettlementComponents.SettlementSource, 3_000m) },
            Array.Empty<(int, string, string, decimal)>(), net: 7_000m);
        await k.SeedSettlementAccrualAsync(run.Id, other, 3_000m);

        var res = await k.Payroll().Lock(run.Id, default);
        var body = JsonSerializer.SerializeToElement(res.Should().BeOfType<UnprocessableEntityObjectResult>().Subject.Value);
        body.GetProperty("error").GetString().Should().Be("gl_company_unbalanced");
        var byCompany = body.GetProperty("companyBalances").EnumerateArray().ToList();
        byCompany.Should().HaveCount(2);
        byCompany.Single(c => c.GetProperty("CompanyId").GetString() == other.ToString())
            .GetProperty("Debits").GetDecimal().Should().Be(3_000m);
        (await k.RunGlAsync(run.Id)).Should().BeEmpty();
    }

    // ── 1 (sequential half). A second Lock is a plain 409, and posts nothing ────────────────────────

    [Fact]
    public async Task LockingAnAlreadyLockedRun_Is409_AndPostsNothing()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 1_000m) }, Array.Empty<(int, string, string, decimal)>(), 1_000m);
        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();
        var before = (await k.RunGlAsync(run.Id)).Count;

        var again = await k.Payroll().Lock(run.Id, default);
        JsonSerializer.SerializeToElement(again.Should().BeOfType<ConflictObjectResult>().Subject.Value)
            .GetProperty("error").GetString().Should().Be("run_already_locked");
        (await k.RunGlAsync(run.Id)).Should().HaveCount(before);
    }
}

/// <summary>Shared harness for the GL posting tests: a tenant + SAR company, approved runs seeded
/// directly (earnings/deductions/slips), and a PayrollController wired like the other payroll tests.</summary>
internal sealed class GlPostingKit : IAsyncDisposable
{
    public ZayraDbContext Db { get; }
    public Guid TenantId { get; private init; } = Guid.NewGuid();
    public Guid CompanyId { get; private set; }
    private readonly SqliteConnection? _conn;
    private readonly Func<ZayraDbContext>? _factory;

    private GlPostingKit(ZayraDbContext db, SqliteConnection? conn, Func<ZayraDbContext>? factory)
    { Db = db; _conn = conn; _factory = factory; }

    public static async Task<GlPostingKit> CreateAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var k = new GlPostingKit(db, conn, null);
        k.CompanyId = await k.AddCompanyAsync();
        return k;
    }

    /// <summary>Postgres flavour: <paramref name="factory"/> hands out a fresh context per contender.</summary>
    public static async Task<GlPostingKit> CreateAsync(Func<ZayraDbContext> factory, Guid tenantId)
    {
        var k = new GlPostingKit(factory(), null, factory) { TenantId = tenantId };
        k.CompanyId = await k.AddCompanyAsync();
        return k;
    }

    public ZayraDbContext NewContext() => _factory?.Invoke() ?? throw new InvalidOperationException("SQLite kit shares one context.");

    public async Task<Guid> AddCompanyAsync()
    {
        var c = new Company { TenantId = TenantId, LegalNameEn = $"Co {Guid.NewGuid():N}"[..12], CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR" };
        Db.Companies.Add(c);
        await Db.SaveChangesAsync();
        return c.Id;
    }

    public async Task SeedGlDefaultsAsync()
    {
        await GlDriverSeeder.SeedTenantDefaultsAsync(Db, TenantId, CancellationToken.None);
        await Db.SaveChangesAsync();
    }

    public async Task<PayrollRun> SeedApprovedRunAsync(
        IEnumerable<(int Emp, string Code, string Source, decimal Amount)> earnings,
        IEnumerable<(int Emp, string Code, string Source, decimal Amount)> deductions,
        decimal net, int month = 9)
    {
        var e = earnings.ToList();
        var d = deductions.ToList();
        var run = new PayrollRun
        {
            TenantId = TenantId, CompanyId = CompanyId, Year = 2026, Month = month, Status = "Approved",
            TotalGrossSalary = e.Sum(x => x.Amount), TotalDeductions = d.Sum(x => x.Amount), TotalNetSalary = net,
            EmployeeCount = e.Select(x => x.Emp).Concat(d.Select(x => x.Emp)).Distinct().Count(),
        };
        Db.PayrollRuns.Add(run);
        foreach (var x in e)
            Db.PayrollEarnings.Add(new PayrollEarning { TenantId = TenantId, PayrollRunId = run.Id, EmployeeId = x.Emp, ComponentCode = x.Code, ComponentName = $"{x.Code} name", Amount = x.Amount, Source = x.Source });
        foreach (var x in d)
            Db.PayrollDeductions.Add(new PayrollDeduction { TenantId = TenantId, CompanyId = CompanyId, PayrollRunId = run.Id, EmployeeId = x.Emp, ComponentCode = x.Code, ComponentName = $"{x.Code} name", Amount = x.Amount, Source = x.Source });
        foreach (var emp in e.Select(x => x.Emp).Distinct())
            Db.PayrollSlips.Add(new PayrollSlip
            {
                TenantId = TenantId, CompanyId = CompanyId, RunId = run.Id, EmployeeId = emp, EmployeeCode = $"E{emp}",
                GrossSalary = e.Where(x => x.Emp == emp).Sum(x => x.Amount), Status = "Processed",
                NetSalary = e.Where(x => x.Emp == emp).Sum(x => x.Amount) - d.Where(x => x.Emp == emp).Sum(x => x.Amount),
            });
        await Db.SaveChangesAsync();
        return run;
    }

    /// <summary>An approved settlement disbursed by <paramref name="runId"/>, with its live accrual CR 2320.</summary>
    public async Task SeedSettlementAccrualAsync(Guid runId, Guid accrualCompanyId, decimal amount)
    {
        var s = new EmployeeFinalSettlement
        {
            TenantId = TenantId, CompanyId = accrualCompanyId, EmployeeId = 1, EmployeeCode = "E1", EmployeeName = "Leaver",
            TerminationReason = "Resignation", Status = "Approved", PayrollRunId = runId, GrossPayable = amount,
        };
        Db.EmployeeFinalSettlements.Add(s);
        Db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = TenantId, CompanyId = accrualCompanyId, SourceModule = FinalSettlementGlDescriptions.SourceModule,
            SourceEntityId = s.Id, EventType = GlEventTypes.SettlementAccrual,
            DebitAccount = string.Empty, CreditAccount = "2320 - Final Settlement Payable",
            Amount = amount, Currency = "SAR", Period = "2026-09", EntryDate = new DateOnly(2026, 9, 1),
            Description = $"{FinalSettlementGlDescriptions.AccrualPrefix}{s.Id}",
        });
        await Db.SaveChangesAsync();
    }

    public PayrollController Payroll(ZayraDbContext? db = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "gl-posting-test"),
            new("permission", "payroll.lock"),
        };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new PayrollController(db ?? Db, new _GpUnrestrictedScope(), new _GpHttpAccessor(http), new _GpNullNotifications(),
            new _GpNullPackResolver(), new StubRuleReader(), new _GpNullLetterService(), new _GpNullDocStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    public async Task<JsonElement> JournalJsonAsync(Guid runId)
    {
        Db.ChangeTracker.Clear();
        var res = await Payroll().GlJournal(runId, default);
        return JsonSerializer.SerializeToElement(((ObjectResult)res).Value);
    }

    /// <summary>"DR|account|amount" per journal line, for comparing preview with post.</summary>
    public static List<string> Lines(JsonElement journal) =>
        journal.GetProperty("entries").EnumerateArray()
            .Select(e => $"{e.GetProperty("entryType").GetString()}|{e.GetProperty("glAccount").GetString()}|{e.GetProperty("amount").GetDecimal():0.00}")
            .ToList();

    public async Task<List<FinanceGlEntry>> RunGlAsync(Guid runId, ZayraDbContext? db = null) =>
        await (db ?? Db).FinanceGlEntries.AsNoTracking()
            .Where(x => x.TenantId == TenantId && x.SourceModule == "Payroll" && x.SourceEntityId == runId).ToListAsync();

    public static decimal Debits(IEnumerable<FinanceGlEntry> gl) => gl.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
    public static decimal Credits(IEnumerable<FinanceGlEntry> gl) => gl.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        if (_conn is not null) await _conn.DisposeAsync();
    }
}

file sealed class _GpUnrestrictedScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _GpHttpAccessor : IHttpContextAccessor
{
    public _GpHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _GpNullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _GpNullPackResolver : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _GpNullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _GpNullDocStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
