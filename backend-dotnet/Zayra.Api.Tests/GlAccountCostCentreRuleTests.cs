using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostAccountingKit;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-B slice 1 — the GL account <c>cost_center_rule</c> API and the bulk "recommended" preset.
/// T-012 (Required refused on Asset/Liability/Equity; invalid rule string), T-040 (default Optional),
/// T-042 + S-005 + AM-9 (M-7 door guard), S-003/S-045 (finance.gl.manage, reason, audit atomic), T-039-adjacent.
/// </summary>
public class GlAccountCostCentreRuleTests
{
    // ── T-012 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Asset")]
    [InlineData("Liability")]
    [InlineData("Equity")]
    public async Task Create_RequiredOnBalanceSheetAccount_Is422_RuleInvalidForType(string type)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var ctrl = Gl(db, tid, ManagePerms);

        var res = await ctrl.CreateAccount(co.Id, new GlAccountRequest("9001", "Bad rule", type, true, null, "Required"), default);

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("cost_centre_rule_invalid_for_type");
        Body(res).P("message").GetString().Should().NotBeNullOrWhiteSpace();
        Body(res).P("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        (await db.GlAccounts.CountAsync(a => a.Code == "9001")).Should().Be(0);
    }

    [Theory]
    [InlineData("Expense")]
    [InlineData("Income")]
    public async Task Create_RequiredOnProfitAndLossAccount_IsAccepted_AndListed(string type)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var ctrl = Gl(db, tid, ManagePerms);

        Status(await ctrl.CreateAccount(co.Id, new GlAccountRequest("9002", "Pnl", type, true, null, "Required"), default)).Should().Be(200);

        var listed = Body(await ctrl.ListAccounts(co.Id, default)).EnumerateArray().Single(a => a.P("code").GetString() == "9002");
        listed.P("costCenterRule").GetString().Should().Be("Required");
    }

    [Fact]
    public async Task Create_WithoutRule_DefaultsToOptional()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var ctrl = Gl(db, tid, ManagePerms);

        Status(await ctrl.CreateAccount(co.Id, new GlAccountRequest("9003", "Plain", "Expense"), default)).Should().Be(200);

        (await db.GlAccounts.SingleAsync(a => a.Code == "9003")).CostCenterRule.Should().Be("Optional");
    }

    [Theory]
    [InlineData("required")]   // wrong case is not silently accepted: the column CHECK is case-sensitive
    [InlineData("Mandatory")]
    [InlineData("  ")]
    public async Task Create_InvalidRuleString_Is422(string rule)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var ctrl = Gl(db, tid, ManagePerms);

        var res = await ctrl.CreateAccount(co.Id, new GlAccountRequest("9004", "Bad", "Expense", true, null, rule), default);

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("cost_centre_rule_invalid");
    }

    [Fact]
    public async Task Update_OmittedRule_LeavesRuleUnchanged()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var acct = await AddAccount(db, tid, co.Id, "5001", "Expense", "Required");
        var ctrl = Gl(db, tid, ManagePerms);

        Status(await ctrl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", "Renamed", "Expense"), default)).Should().Be(200);

        db.ChangeTracker.Clear();
        (await db.GlAccounts.SingleAsync(a => a.Id == acct.Id)).CostCenterRule.Should().Be("Required");
    }

    [Fact]
    public async Task Update_ChangeTypeToLiability_WhileRequired_IsRefused_NotSilentlyChanged()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var acct = await AddAccount(db, tid, co.Id, "5001", "Expense", "Required");
        var ctrl = Gl(db, tid, ManagePerms);

        var res = await ctrl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", acct.Name, "Liability"), default);

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("cost_centre_rule_invalid_for_type");
        db.ChangeTracker.Clear();
        var after = await db.GlAccounts.SingleAsync(a => a.Id == acct.Id);
        (after.AccountType, after.CostCenterRule).Should().Be(("Expense", "Required"));
    }

    [Fact]
    public async Task Update_RuleChange_IsAuditedWithBeforeAfter_AndCompanyId()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var acct = await AddAccount(db, tid, co.Id, "5001", "Expense");
        var ctrl = Gl(db, tid, ManagePerms);

        Status(await ctrl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", acct.Name, "Expense", true, null, "Required"), default)).Should().Be(200);

        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "finance.gl.account.rule_changed");
        audit.CompanyId.Should().Be(co.Id);
        audit.Metadata.Should().Contain("Optional").And.Contain("Required");
    }

    [Fact]
    public async Task Update_RuleChange_NeedsFinanceGlManage()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var acct = await AddAccount(db, tid, co.Id, "5001", "Expense");
        var ctrl = Gl(db, tid, new[] { "finance.gl.read", "organization.costcenter.manage" });

        (await ctrl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", acct.Name, "Expense", true, null, "Required"), default))
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Update_RuleChange_AuditFailure_RollsTheChangeBack()
    {
        var failing = new FailOnAuditRow();
        var (db, conn) = NewDb(failing); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var acct = await AddAccount(db, tid, co.Id, "5001", "Expense");
        var ctrl = Gl(db, tid, ManagePerms);

        failing.Armed = true;
        var act = () => ctrl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", acct.Name, "Expense", true, null, "Required"), default);
        await act.Should().ThrowAsync<InvalidOperationException>();

        failing.Armed = false;
        db.ChangeTracker.Clear();
        (await db.GlAccounts.SingleAsync(a => a.Id == acct.Id)).CostCenterRule.Should().Be("Optional");
    }

    // ── M-7 door guard (AM-9): PendingFinanceReview and Approved, nothing else ─

    [Theory]
    [InlineData("PendingFinanceReview")]
    [InlineData("Approved")]
    public async Task Update_RuleChange_WhileARunAwaitsFinanceOrLock_Is409_LockedIntoApprovedRun(string status)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var acct = await AddAccount(db, tid, co.Id, "5001", "Expense");
        var run = await AddRun(db, tid, co.Id, status);
        var ctrl = Gl(db, tid, ManagePerms);

        var res = await ctrl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", acct.Name, "Expense", true, null, "Required"), default);

        Status(res).Should().Be(409);
        ErrorCode(res).Should().Be("locked_into_approved_run");
        var body = Body(res);
        body.P("message").GetString().Should().Contain("awaiting finance review or lock").And.Contain("reopen");
        body.P("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        // S-042: the refusal never tells the caller which run, period or amounts are involved.
        body.ToString().Should().NotContain(run.Id.ToString()).And.NotContain("2026");
        db.ChangeTracker.Clear();
        (await db.GlAccounts.SingleAsync(a => a.Id == acct.Id)).CostCenterRule.Should().Be("Optional");
    }

    [Theory]
    [InlineData("Locked", true)]
    [InlineData("Processed", true)]
    [InlineData("Draft", true)]
    [InlineData("Voided", true)]
    [InlineData("Approved", false)]   // approved, but in ANOTHER company
    public async Task Update_RuleChange_IsAllowed_WhenNoRunOfThisCompanyAwaitsFinanceOrLock(string status, bool sameCompany)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var other = await AddCompany(db, tid);
        var acct = await AddAccount(db, tid, co.Id, "5001", "Expense");
        await AddRun(db, tid, sameCompany ? co.Id : other.Id, status);
        var ctrl = Gl(db, tid, ManagePerms);

        Status(await ctrl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", acct.Name, "Expense", true, null, "Required"), default)).Should().Be(200);
    }

    [Fact]
    public async Task Update_NonRuleEdit_IsAllowed_WhileARunIsApproved()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var acct = await AddAccount(db, tid, co.Id, "5001", "Expense", "Required");
        await AddRun(db, tid, co.Id, "Approved");
        var ctrl = Gl(db, tid, ManagePerms);

        // Same type, same rule (omitted); only the display name moves. This is not an attribution change (S-043).
        Status(await ctrl.UpdateAccount(acct.Id, false, new GlAccountRequest("5001", "Basic salary expense", "Expense"), default)).Should().Be(200);
    }

    // ── Bulk preset ───────────────────────────────────────────────────────────

    private static async Task<(Guid tid, Company co, GlAccount exp, GlAccount inc, GlAccount asset, GlAccount liab, GlAccount eq)> SeedChart(Zayra.Api.Data.ZayraDbContext db)
    {
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        return (tid, co,
            await AddAccount(db, tid, co.Id, "5001", "Expense"),
            await AddAccount(db, tid, co.Id, "4001", "Income"),
            await AddAccount(db, tid, co.Id, "1001", "Asset"),
            await AddAccount(db, tid, co.Id, "2001", "Liability"),
            await AddAccount(db, tid, co.Id, "3001", "Equity"));
    }

    [Fact]
    public async Task Preset_DryRun_ReportsCounts_AndWritesNothing()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, exp, inc, asset, liab, eq) = await SeedChart(db);
        var ctrl = Gl(db, tid, ManagePerms);

        var res = await ctrl.ApplyCostCentreRulePreset(co.Id, true, new CostCentreRulePresetRequest(null), default);

        Status(res).Should().Be(200);
        var b = Body(res);
        b.P("dryRun").GetBoolean().Should().BeTrue();
        b.P("changed").GetInt32().Should().Be(5);
        db.ChangeTracker.Clear();
        (await db.GlAccounts.CountAsync(a => a.CostCenterRule != "Optional")).Should().Be(0);
        (await db.AuditLogs.CountAsync(a => a.Action.StartsWith("finance.gl.account.rule"))).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Preset_Apply_WithoutReason_Is422_M10(string? reason)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _, _, _, _) = await SeedChart(db);
        var ctrl = Gl(db, tid, ManagePerms);

        var res = await ctrl.ApplyCostCentreRulePreset(co.Id, false, new CostCentreRulePresetRequest(reason), default);

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("cost_centre_reason_required");
        Body(res).P("message").GetString().Should().Be("Enter a reason for this change.");
        Body(res).P("messageAr").GetString().Should().Be("أدخل سببًا لهذا التغيير.");
    }

    [Fact]
    public async Task Preset_Apply_SetsPnlRequired_BalanceSheetNotAllowed_AndAuditsTheChange()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, exp, inc, asset, liab, eq) = await SeedChart(db);
        var ctrl = Gl(db, tid, ManagePerms);

        var res = await ctrl.ApplyCostCentreRulePreset(co.Id, false, new CostCentreRulePresetRequest("Month-end policy"), default);

        Status(res).Should().Be(200);
        Body(res).P("changed").GetInt32().Should().Be(5);
        db.ChangeTracker.Clear();
        var rules = await db.GlAccounts.ToDictionaryAsync(a => a.Code, a => a.CostCenterRule);
        rules["5001"].Should().Be("Required");
        rules["4001"].Should().Be("Required");
        rules["1001"].Should().Be("NotAllowed");
        rules["2001"].Should().Be("NotAllowed");
        rules["3001"].Should().Be("NotAllowed");
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "finance.gl.account.rule_preset_applied");
        audit.CompanyId.Should().Be(co.Id);
        audit.Metadata.Should().Contain("Month-end policy").And.Contain("5001");
    }

    [Fact]
    public async Task Preset_Apply_IsIdempotent_NothingLeftToChange()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _, _, _, _) = await SeedChart(db);
        var ctrl = Gl(db, tid, ManagePerms);
        await ctrl.ApplyCostCentreRulePreset(co.Id, false, new CostCentreRulePresetRequest("first"), default);

        var again = await ctrl.ApplyCostCentreRulePreset(co.Id, false, new CostCentreRulePresetRequest("second"), default);

        Status(again).Should().Be(200);
        Body(again).P("changed").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Preset_AppliesOnlyToTheScopeSelected_NeverToTenantDefaultOrOtherCompany()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _, _, _, _) = await SeedChart(db);
        var other = await AddCompany(db, tid);
        await AddAccount(db, tid, other.Id, "5001", "Expense");
        await AddAccount(db, tid, null, "5001", "Expense");
        var ctrl = Gl(db, tid, ManagePerms);

        await ctrl.ApplyCostCentreRulePreset(co.Id, false, new CostCentreRulePresetRequest("scoped"), default);

        db.ChangeTracker.Clear();
        (await db.GlAccounts.Where(a => a.CompanyId != co.Id).AllAsync(a => a.CostCenterRule == "Optional")).Should().BeTrue();
    }

    [Fact]
    public async Task Preset_NeedsFinanceGlManage_AndCompanyScope()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _, _, _, _) = await SeedChart(db);
        var other = await AddCompany(db, tid);

        (await Gl(db, tid, ReadPerms).ApplyCostCentreRulePreset(co.Id, false, new CostCentreRulePresetRequest("x"), default))
            .Should().BeOfType<ForbidResult>();
        (await Gl(db, tid, ManagePerms, CompanyScope(other.Id)).ApplyCostCentreRulePreset(co.Id, false, new CostCentreRulePresetRequest("x"), default))
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Preset_Apply_WhileARunIsApproved_Is409_M7()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _, _, _, _) = await SeedChart(db);
        await AddRun(db, tid, co.Id, "PendingFinanceReview");
        var ctrl = Gl(db, tid, ManagePerms);

        var res = await ctrl.ApplyCostCentreRulePreset(co.Id, false, new CostCentreRulePresetRequest("x"), default);

        Status(res).Should().Be(409);
        ErrorCode(res).Should().Be("locked_into_approved_run");
        db.ChangeTracker.Clear();
        (await db.GlAccounts.CountAsync(a => a.CostCenterRule != "Optional")).Should().Be(0);
    }

    [Fact]
    public async Task Preset_AuditFailure_RollsEveryRuleBack()
    {
        var failing = new FailOnAuditRow();
        var (db, conn) = NewDb(failing); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _, _, _, _) = await SeedChart(db);
        var ctrl = Gl(db, tid, ManagePerms);

        failing.Armed = true;
        var act = () => ctrl.ApplyCostCentreRulePreset(co.Id, false, new CostCentreRulePresetRequest("x"), default);
        await act.Should().ThrowAsync<InvalidOperationException>();

        failing.Armed = false;
        db.ChangeTracker.Clear();
        (await db.GlAccounts.CountAsync(a => a.CostCenterRule != "Optional")).Should().Be(0);
    }
}
