using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// GL setup faults that used to block (or silently mis-route) a payroll Lock, and the setup API paths that
/// repair them: a switched-off system rule only blocks a run that would use it and can be switched back
/// on; an account re-code carries the driver defaults with it; a default code missing from the chart
/// blocks Lock (unless the tenant has no chart at all); saving mappings keeps a switched-off driver's
/// mapping. SQLite in-memory via <see cref="GlPostingKit"/>.
/// </summary>
public class GlPostingSetupRepairTests
{
    private static readonly (int, string, string, decimal)[] NoDeductions = Array.Empty<(int, string, string, decimal)>();

    private static FinanceGlController Gl(GlPostingKit k)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", k.TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        claims.AddRange(new[] { "finance.gl.read", "finance.gl.manage", "finance.gl.drivers.manage" }.Select(p => new Claim("permission", p)));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new FinanceGlController(k.Db) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static GlDriverRequest AsRequest(GlDriver d, bool isActive) => new(
        d.Key, d.Label, d.Category, d.PostingSide, d.DefaultCode, d.DefaultName, d.AccountType, d.MatchSource, d.MatchMode,
        d.MatchComponentCode, d.EmitsEmployerExpensePair, d.PairedExpenseDriverKey, d.SortOrder, isActive);

    private static JsonElement Body(IActionResult res) => JsonSerializer.SerializeToElement(((ObjectResult)res).Value);

    // ── 1. A switched-off SYSTEM rule ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASwitchedOffSystemRule_TheRunNeverUses_DoesNotBlockLock()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        // Switched off on the old API: no income tax in this tenant. The run has no tax component.
        (await k.Db.GlDrivers.SingleAsync(d => d.TenantId == k.TenantId && d.Key == "DED:TAX")).IsActive = false;
        await k.Db.SaveChangesAsync();
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) }, NoDeductions, 5_000m);

        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>(
            "a switched-off rule that no pay item on this run would use must not stop the month");
    }

    [Fact]
    public async Task ASwitchedOffSystemRule_ThatAComponentWouldHaveUsed_StillBlocks_EvenThoughItReroutesToACatchAll()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        (await k.Db.GlDrivers.SingleAsync(d => d.TenantId == k.TenantId && d.Key == "DED:TAX")).IsActive = false;
        await k.Db.SaveChangesAsync();
        // INCOME_TAX (Source Tax) would route to DED:TAX; with it off it falls through to DED:OTHER.
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) },
            new[] { (1, "INCOME_TAX", "Tax", 200m) }, 4_800m);

        var body = Body((await k.Payroll().Lock(run.Id, default)).Should().BeOfType<UnprocessableEntityObjectResult>().Subject);
        var problem = body.GetProperty("problems").EnumerateArray().Single();
        problem.GetProperty("driverKey").GetString().Should().Be("DED:TAX");
        problem.GetProperty("kind").GetString().Should().Be("InactiveDriver");
        (await k.RunGlAsync(run.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task ASwitchedOffSystemRule_CanBeSwitchedBackOn_ThroughTheApi_AndTheRunThenLocks()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var basic = await k.Db.GlDrivers.SingleAsync(d => d.TenantId == k.TenantId && d.Key == "EARN:BASIC");
        basic.IsActive = false;
        await k.Db.SaveChangesAsync();
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) }, NoDeductions, 5_000m);
        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<UnprocessableEntityObjectResult>();

        k.Db.ChangeTracker.Clear();
        (await Gl(k).UpdateDriver(basic.Id, AsRequest(basic, isActive: true), default)).Should().BeOfType<OkObjectResult>();
        k.Db.ChangeTracker.Clear();
        (await k.Db.GlDrivers.AsNoTracking().SingleAsync(d => d.Id == basic.Id)).IsActive.Should().BeTrue(
            "the error tells finance to switch the rule back on; the API must actually do it");

        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();
        (await k.RunGlAsync(run.Id)).Should().Contain(l => l.DebitAccount == "5001 - Basic Salary Expense");
    }

    [Fact]
    public async Task ASystemRule_StillCannotBeSwitchedOff()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var tax = await k.Db.GlDrivers.AsNoTracking().SingleAsync(d => d.TenantId == k.TenantId && d.Key == "DED:TAX");

        var res = await Gl(k).UpdateDriver(tax.Id, AsRequest(tax, isActive: false), default);

        Body(res.Should().BeOfType<BadRequestObjectResult>().Subject).GetProperty("code").GetString().Should().Be("GL_SYSTEM_DRIVER_REQUIRED");
        k.Db.ChangeTracker.Clear();
        (await k.Db.GlDrivers.AsNoTracking().SingleAsync(d => d.Id == tax.Id)).IsActive.Should().BeTrue();
    }

    // ── 4. Re-coded accounts and codes missing from the chart ──────────────────────────────────────

    [Fact]
    public async Task ReCodingAnAccount_MovesTheDriverDefaultsThatNamedTheOldCode()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        // EARN:BASIC resolves by its driver default (no mapping) to 5001.
        k.Db.GlAccountMappings.RemoveRange(k.Db.GlAccountMappings.Where(m => m.TenantId == k.TenantId && m.DriverKey == "EARN:BASIC"));
        await k.Db.SaveChangesAsync();
        var acct = await k.Db.GlAccounts.AsNoTracking().SingleAsync(a => a.TenantId == k.TenantId && a.CompanyId == null && a.Code == "5001");
        k.Db.ChangeTracker.Clear();

        (await Gl(k).UpdateAccount(acct.Id, false, new GlAccountRequest("5901", "Basic Salaries", "Expense"), default))
            .Should().BeOfType<OkObjectResult>();

        k.Db.ChangeTracker.Clear();
        var basic = await k.Db.GlDrivers.AsNoTracking().SingleAsync(d => d.TenantId == k.TenantId && d.Key == "EARN:BASIC");
        basic.DefaultCode.Should().Be("5901");
        basic.DefaultName.Should().Be("Basic Salaries");
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) }, NoDeductions, 5_000m);
        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();
        (await k.RunGlAsync(run.Id)).Should().Contain(l => l.DebitAccount == "5901 - Basic Salaries",
            "the journal names the account as the chart now has it, so the ERP export finds it");
    }

    [Fact]
    public async Task ADriverDefaultCodeMissingFromTheChart_BlocksLock_WithAPlainMessage()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        k.Db.GlAccountMappings.RemoveRange(k.Db.GlAccountMappings.Where(m => m.TenantId == k.TenantId && m.DriverKey == "EARN:BASIC"));
        (await k.Db.GlDrivers.SingleAsync(d => d.TenantId == k.TenantId && d.Key == "EARN:BASIC")).DefaultCode = "5777";
        await k.Db.SaveChangesAsync();
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) }, NoDeductions, 5_000m);

        var body = Body((await k.Payroll().Lock(run.Id, default)).Should().BeOfType<UnprocessableEntityObjectResult>().Subject);
        body.GetProperty("error").GetString().Should().Be("gl_account_not_in_chart");
        var problem = body.GetProperty("problems").EnumerateArray().Single();
        problem.GetProperty("driverKey").GetString().Should().Be("EARN:BASIC");
        problem.GetProperty("kind").GetString().Should().Be("NotInChart");
        problem.GetProperty("accountCode").GetString().Should().Be("5777");
        body.GetProperty("message").GetString().Should().Contain("5777").And.Contain("not in the chart of accounts");
        (await k.RunGlAsync(run.Id)).Should().BeEmpty("a refused lock posts nothing");
    }

    [Fact]
    public async Task ATenantWithNoChartAtAll_StillLocks_OnTheBuiltInAccounts()
    {
        await using var k = await GlPostingKit.CreateAsync(); // no gl_accounts, no drivers, no mappings
        var run = await k.SeedApprovedRunAsync(new[] { (1, "BASIC", "Salary", 5_000m) }, new[] { (1, "ATTENDANCE", "Attendance", 100m) }, 4_900m);

        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>(
            "a tenant that never set up a chart has always posted the built-in labels; blocking it would stop every payroll");
    }

    // ── 5. Saving mappings keeps a switched-off driver's mapping ───────────────────────────────────

    [Fact]
    public async Task SavingMappings_KeepsTheMappingOfASwitchedOffDriver_AndStillClearsAnOmittedListedOne()
    {
        await using var k = await GlPostingKit.CreateAsync();
        await k.SeedGlDefaultsAsync();
        var site = new GlAccount { TenantId = k.TenantId, Code = "6120", Name = "Site Allowance", AccountType = "Expense" };
        k.Db.GlAccounts.Add(site);
        k.Db.GlDrivers.Add(new GlDriver
        {
            TenantId = k.TenantId, Key = "EARN:SITE", Label = "Site", Category = GlDriverCategories.Earning, PostingSide = "DR",
            AccountType = "Expense", DefaultCode = "6120", DefaultName = "Site Allowance", MatchMode = "Exact", MatchComponentCode = "SITE",
            IsActive = false, SortOrder = 60,
        });
        k.Db.GlAccountMappings.Add(new GlAccountMapping { TenantId = k.TenantId, DriverKey = "EARN:SITE", AccountId = site.Id });
        await k.Db.SaveChangesAsync();
        k.Db.ChangeTracker.Clear();

        // What the UI sends: every LISTED driver's mapping except the one the user cleared (EARN:HOUSING).
        var listed = Body(await Gl(k).ListMappings(null, default)).EnumerateArray()
            .Where(r => r.GetProperty("mappedAccountId").ValueKind == JsonValueKind.String)
            .Select(r => new GlMappingRequest(r.GetProperty("driverKey").GetString()!, r.GetProperty("mappedAccountId").GetGuid()))
            .ToList();
        listed.Should().NotContain(m => m.DriverKey == "EARN:SITE", "GET /mappings lists active drivers only");
        var payload = listed.Where(m => m.DriverKey != "EARN:HOUSING").ToList();

        (await Gl(k).SetMappings(null, payload, default)).Should().BeOfType<OkObjectResult>();

        k.Db.ChangeTracker.Clear();
        var maps = await k.Db.GlAccountMappings.AsNoTracking().Where(m => m.TenantId == k.TenantId && m.CompanyId == null).ToListAsync();
        maps.Should().ContainSingle(m => m.DriverKey == "EARN:SITE" && m.AccountId == site.Id,
            "the caller could not have sent a switched-off driver's mapping, so its absence is not a delete");
        maps.Should().NotContain(m => m.DriverKey == "EARN:HOUSING", "omitting a listed driver still clears its mapping");
        maps.Should().Contain(m => m.DriverKey == "EARN:BASIC");
    }
}
