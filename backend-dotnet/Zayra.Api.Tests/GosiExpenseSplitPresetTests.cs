using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// L1.4 — employer GOSI expense: pooled 5101 by default, one-click split to 5102 / 5103 / 5104 by scheme. The split must keep the journal
/// balanced and leave the CREDIT side (2106 by component) exactly as it was.
///
/// <para>Yardstick run: two Saudis and one expat. Employer annuities 1,125.00 + 750.00 = 1,875.00; SANED 93.75 + 56.25 = 150.00;
/// occupational hazards 250.00 + 150.00 + 150.00 (the expat) = 550.00; total 2,575.00.</para>
/// </summary>
public class GosiExpenseSplitPresetTests
{
    private static readonly (int Emp, string Code, string Source, decimal Amount)[] EmployerGosi =
    {
        (1, "GOSI-ANN-ER", "Statutory", 1_125.00m), (1, "GOSI-SANED-ER", "Statutory", 93.75m), (1, "GOSI-OH-ER", "Statutory", 250.00m),
        (2, "GOSI-ANN-ER", "Statutory", 750.00m),   (2, "GOSI-SANED-ER", "Statutory", 56.25m), (2, "GOSI-OH-ER", "Statutory", 150.00m),
        (3, "GOSI-OH-ER", "Statutory", 150.00m),
    };

    private static readonly (int Emp, string Code, string Source, decimal Amount)[] Earnings =
    {
        (1, "BASIC", "Salary", 10_000m), (2, "BASIC", "Salary", 6_000m), (3, "BASIC", "Salary", 6_000m),
    };

    private static async Task<PayrollRun> LockedRunAsync(GlPostingKit k, int month)
    {
        var run = await k.SeedApprovedRunAsync(Earnings, EmployerGosi, net: 22_000m, month: month);
        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();
        return run;
    }

    private static decimal DebitTo(IEnumerable<FinanceGlEntry> gl, string code) =>
        gl.Where(l => l.DebitAccount.StartsWith(code + " ")).Sum(l => l.Amount);

    private static List<string> CreditSide(IEnumerable<FinanceGlEntry> gl) =>
        gl.Where(l => !string.IsNullOrEmpty(l.CreditAccount))
          .Select(l => $"{l.CreditAccount}|{l.Description}|{l.Amount:0.00}")
          .OrderBy(x => x, StringComparer.Ordinal).ToList();

    // T2.1 — preset off: the pooled journal, exactly as before.
    [Fact]
    public async Task PresetOff_PostsOnePooledExpenseLine_To5101()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var run = await LockedRunAsync(k, 9);

        var gl = await k.RunGlAsync(run.Id);
        DebitTo(gl, "5101").Should().Be(2_575.00m);
        gl.Where(l => l.DebitAccount.StartsWith("5101 ")).Should().ContainSingle();
        gl.Should().NotContain(l => l.DebitAccount.StartsWith("5102") || l.DebitAccount.StartsWith("5103") || l.DebitAccount.StartsWith("5104"));
        GlPostingKit.Debits(gl).Should().Be(GlPostingKit.Credits(gl));
    }

    // T2.2 — preset on (whole workspace): three expense lines, no 5101, the credit side byte-for-byte what it was.
    [Fact]
    public async Task PresetOn_SplitsTheDebitByScheme_AndLeavesTheCreditSideAndTotalsUnchanged()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var before = await LockedRunAsync(k, 9);
        var beforeGl = await k.RunGlAsync(before.Id);

        var result = await GosiExpenseSplitPreset.EnableAsync(k.Db, k.TenantId, null, default);
        result.Ok.Should().BeTrue();
        result.AccountsCreated.Should().Be(3);
        result.DriversCreated.Should().Be(6, "three routing drivers and three paired expense drivers");

        var after = await LockedRunAsync(k, 10);
        var afterGl = await k.RunGlAsync(after.Id);

        DebitTo(afterGl, "5102").Should().Be(1_875.00m);
        DebitTo(afterGl, "5103").Should().Be(150.00m);
        DebitTo(afterGl, "5104").Should().Be(550.00m);
        afterGl.Should().NotContain(l => l.DebitAccount.StartsWith("5101"), "the pooled account carries nothing once the split is on");
        GlPostingKit.Debits(afterGl).Should().Be(GlPostingKit.Credits(afterGl)).And.Be(GlPostingKit.Debits(beforeGl), "totals do not move");

        // The credit side: same accounts, same component descriptions, same amounts, same count.
        CreditSide(afterGl).Should().Equal(CreditSide(beforeGl));
        afterGl.Where(l => l.CreditAccount.StartsWith("2106 ")).Sum(l => l.Amount).Should().Be(2_575.00m);

        // History is never rewritten: the first run still shows the pooled line.
        (await k.RunGlAsync(before.Id)).Should().Contain(l => l.DebitAccount.StartsWith("5101 ") && l.Amount == 2_575.00m);
    }

    [Fact]
    public async Task Expat_OnlyOccupationalHazardsCarriesAnExpenseLine()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        await GosiExpenseSplitPreset.EnableAsync(k.Db, k.TenantId, null, default);
        var run = await k.SeedApprovedRunAsync(
            new[] { (3, "BASIC", "Salary", 6_000m) }, new[] { (3, "GOSI-OH-ER", "Statutory", 150.00m) }, net: 6_000m, month: 9);
        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();

        var gl = await k.RunGlAsync(run.Id);
        DebitTo(gl, "5104").Should().Be(150.00m);
        gl.Should().NotContain(l => l.DebitAccount.StartsWith("5102") || l.DebitAccount.StartsWith("5103") || l.DebitAccount.StartsWith("5101"));
    }

    // The preset is per company when asked to be: the company posts the split, nothing else about it changes.
    [Fact]
    public async Task CompanyScopedPreset_SplitsThatCompanysRun()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        (await GosiExpenseSplitPreset.EnableAsync(k.Db, k.TenantId, k.CompanyId, default)).Ok.Should().BeTrue();
        (await GosiExpenseSplitPreset.IsEnabledAsync(k.Db, k.TenantId, k.CompanyId, default)).Should().BeTrue();
        (await GosiExpenseSplitPreset.IsEnabledAsync(k.Db, k.TenantId, null, default)).Should().BeFalse("the group scope was not switched on");

        var run = await LockedRunAsync(k, 9);
        var gl = await k.RunGlAsync(run.Id);
        DebitTo(gl, "5102").Should().Be(1_875.00m);
        DebitTo(gl, "5103").Should().Be(150.00m);
        DebitTo(gl, "5104").Should().Be(550.00m);
    }

    // A tenant that had pointed employer social insurance at its own liability keeps crediting THAT account.
    [Fact]
    public async Task ARemappedLiabilityAccount_IsKept_OnTheCreditSide()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var custom = new GlAccount { TenantId = k.TenantId, CompanyId = null, Code = "2250", Name = "GOSI Employer Payable (client)", AccountType = "Liability", IsActive = true };
        k.Db.GlAccounts.Add(custom);
        var mapping = await k.Db.GlAccountMappings.SingleAsync(m => m.TenantId == k.TenantId && m.CompanyId == null && m.DriverKey == "DED:STATUTORY_ER");
        mapping.AccountId = custom.Id;
        await k.Db.SaveChangesAsync();

        await GosiExpenseSplitPreset.EnableAsync(k.Db, k.TenantId, null, default);
        var run = await LockedRunAsync(k, 9);
        var gl = await k.RunGlAsync(run.Id);

        gl.Where(l => l.CreditAccount.StartsWith("2250 ")).Sum(l => l.Amount).Should().Be(2_575.00m);
        gl.Should().NotContain(l => l.CreditAccount.StartsWith("2106 "));
        DebitTo(gl, "5102").Should().Be(1_875.00m);
    }

    // Group scope with a company that pays its employer GOSI into its own liability (2150) while the tenant default is 2106: each
    // company keeps crediting ITS account. Before, the first company's account was copied onto the group default, so every other
    // company's employer GOSI moved to that company's payable.
    [Fact]
    public async Task GroupEnable_KeepsEachCompanysCreditAccount_AndDoesNotCopyOneCompanysToTheGroupDefault()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var other = await k.AddCompanyAsync();
        var own = new GlAccount { TenantId = k.TenantId, CompanyId = null, Code = "2150", Name = "GOSI Employer Payable (own)", AccountType = "Liability", IsActive = true };
        k.Db.GlAccounts.Add(own);
        await k.Db.SaveChangesAsync();
        k.Db.GlAccountMappings.Add(new GlAccountMapping { TenantId = k.TenantId, CompanyId = k.CompanyId, DriverKey = "DED:STATUTORY_ER", AccountId = own.Id, IsActive = true });
        await k.Db.SaveChangesAsync();

        (await GosiExpenseSplitPreset.EnableAsync(k.Db, k.TenantId, null, default)).Ok.Should().BeTrue();

        var codes = await k.Db.GlAccounts.ToDictionaryAsync(a => a.Id, a => a.Code);
        var mappings = await k.Db.GlAccountMappings.AsNoTracking()
            .Where(m => m.TenantId == k.TenantId && m.DriverKey.StartsWith("DED:GOSI_")).ToListAsync();
        foreach (var scheme in GosiExpenseSplitPreset.Schemes)
        {
            var rows = mappings.Where(m => m.DriverKey == scheme.CreditDriverKey).ToList();
            rows.Where(m => m.CompanyId == null).Should().ContainSingle($"{scheme.CreditDriverKey} has one group default")
                .Which.AccountId.Should().Be(codes.Single(c => c.Value == "2106").Key, "the group default is the tenant's own pooled account");
            rows.Where(m => m.CompanyId == k.CompanyId).Should().ContainSingle($"{scheme.CreditDriverKey} keeps the company's own account")
                .Which.AccountId.Should().Be(own.Id);
            rows.Where(m => m.CompanyId == other).Should().BeEmpty("a company with no mapping of its own follows the group default");
        }

        var run = await LockedRunAsync(k, 9);
        var gl = await k.RunGlAsync(run.Id);
        gl.Where(l => l.CreditAccount.StartsWith("2150 ")).Sum(l => l.Amount).Should().Be(2_575.00m, "the company's credit side does not move");
        gl.Should().NotContain(l => l.CreditAccount.StartsWith("2106 "));
        DebitTo(gl, "5102").Should().Be(1_875.00m);
    }

    [Fact]
    public async Task ACodeAlreadyUsedForSomethingElse_RefusesTheWholePreset_AndChangesNothing()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        k.Db.GlAccounts.Add(new GlAccount { TenantId = k.TenantId, CompanyId = null, Code = "5103", Name = "Marketing Expense", AccountType = "Expense", IsActive = true });
        await k.Db.SaveChangesAsync();

        var result = await GosiExpenseSplitPreset.EnableAsync(k.Db, k.TenantId, null, default);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be(GosiExpenseSplitPreset.CodeTakenCode);
        result.ErrorMessage.Should().Contain("5103").And.Contain("Marketing Expense");
        (await k.Db.GlDrivers.CountAsync(d => d.TenantId == k.TenantId && (d.Key.StartsWith("EXP:GOSI") || d.Key.StartsWith("DED:GOSI_")))).Should().Be(0);
        (await k.Db.GlAccounts.CountAsync(a => a.TenantId == k.TenantId && (a.Code == "5102" || a.Code == "5104"))).Should().Be(0, "nothing is created when one code clashes");
        (await GosiExpenseSplitPreset.IsEnabledAsync(k.Db, k.TenantId, null, default)).Should().BeFalse();
    }

    [Fact]
    public async Task EnablingTwice_IsIdempotent()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        (await GosiExpenseSplitPreset.EnableAsync(k.Db, k.TenantId, null, default)).DriversCreated.Should().Be(6);
        var again = await GosiExpenseSplitPreset.EnableAsync(k.Db, k.TenantId, null, default);
        again.Ok.Should().BeTrue();
        again.AccountsCreated.Should().Be(0);
        again.DriversCreated.Should().Be(0);
        (await k.Db.GlDrivers.CountAsync(d => d.TenantId == k.TenantId && d.Key == "DED:GOSI_ANN_ER")).Should().Be(1);
    }

    [Fact]
    public async Task Disabling_ReturnsTheNextRunToThePooledAccount()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        await GosiExpenseSplitPreset.EnableAsync(k.Db, k.TenantId, null, default);
        (await GosiExpenseSplitPreset.DisableAsync(k.Db, k.TenantId, null, default)).Should().Be(3);
        (await GosiExpenseSplitPreset.IsEnabledAsync(k.Db, k.TenantId, null, default)).Should().BeFalse();

        var run = await LockedRunAsync(k, 9);
        var gl = await k.RunGlAsync(run.Id);
        DebitTo(gl, "5101").Should().Be(2_575.00m);
    }

    [Fact]
    public async Task AWorkspaceWithNoSeededChart_IsToldToSeedFirst()
    {
        await using var k = await GlPostingKit.CreateAsync();   // no SeedGlDefaultsAsync
        var result = await GosiExpenseSplitPreset.EnableAsync(k.Db, k.TenantId, null, default);
        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be(GosiExpenseSplitPreset.NotSeededCode);
    }

    // ── The endpoint: who may press the button ────────────────────────────────────────────────────

    private static FinanceGlController Controller(ZayraDbContext db, Guid tenantId, params string[] permissions)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new FinanceGlController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }

    [Fact]
    public async Task Endpoint_NeedsBothTheAccountAndTheDriverManagePermissions()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();

        (await Controller(k.Db, k.TenantId, "finance.gl.read").EnableEmployerGosiSplit(null, default)).Should().BeOfType<ForbidResult>();
        (await Controller(k.Db, k.TenantId, "finance.gl.manage").EnableEmployerGosiSplit(null, default)).Should().BeOfType<ForbidResult>();
        (await Controller(k.Db, k.TenantId, "finance.gl.drivers.manage").EnableEmployerGosiSplit(null, default)).Should().BeOfType<ForbidResult>();
        (await GosiExpenseSplitPreset.IsEnabledAsync(k.Db, k.TenantId, null, default)).Should().BeFalse();

        (await Controller(k.Db, k.TenantId, "finance.gl.manage", "finance.gl.drivers.manage").EnableEmployerGosiSplit(null, default))
            .Should().BeOfType<OkObjectResult>();
        (await GosiExpenseSplitPreset.IsEnabledAsync(k.Db, k.TenantId, null, default)).Should().BeTrue();
        k.Db.AuditLogs.Should().Contain(a => a.TenantId == k.TenantId && a.Action == "finance.gl.gosi_split.enabled");
    }

    [Fact]
    public async Task Endpoint_IsRefusedWhileARunAwaitsLock_BecauseItWouldChangeWhatThatRunPosts()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        await k.SeedApprovedRunAsync(Earnings, EmployerGosi, net: 22_000m, month: 9);   // Approved, not yet Locked

        var res = await Controller(k.Db, k.TenantId, "finance.gl.manage", "finance.gl.drivers.manage").EnableEmployerGosiSplit(null, default);

        res.Should().BeOfType<ConflictObjectResult>();
        (await GosiExpenseSplitPreset.IsEnabledAsync(k.Db, k.TenantId, null, default)).Should().BeFalse();
    }

    [Fact]
    public async Task StatusEndpoint_ReportsEnabledAndTheAccounts()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var read = Controller(k.Db, k.TenantId, "finance.gl.read");

        var off = (OkObjectResult)await read.GetEmployerGosiSplit(null, default);
        off.Value!.GetType().GetProperty("enabled")!.GetValue(off.Value).Should().Be(false);

        await GosiExpenseSplitPreset.EnableAsync(k.Db, k.TenantId, null, default);
        var on = (OkObjectResult)await read.GetEmployerGosiSplit(null, default);
        on.Value!.GetType().GetProperty("enabled")!.GetValue(on.Value).Should().Be(true);
    }
}
