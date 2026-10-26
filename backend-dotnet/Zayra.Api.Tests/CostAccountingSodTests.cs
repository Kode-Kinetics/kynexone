using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostAccountingKit;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-B slice 5c — identity segregation of duties (AM-14, S-004's data half) and the reusable door-guard helpers
/// that WP-C (Lock) and WP-D (export create) call. Costing is ON in every scenario unless stated.
/// </summary>
public class CostAccountingSodTests
{
    private const string Period = "2026-09";

    private static async Task<(Guid tid, Company co, GlAccount a1, GlAccount a2)> Seed(ZayraDbContext db, bool enabled = true)
    {
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, default);
        await db.SaveChangesAsync();
        var co = await AddCompany(db, tid, enabled);
        return (tid, co, await AddAccount(db, tid, co.Id, "5001", "Expense"), await AddAccount(db, tid, co.Id, "5002", "Expense"));
    }

    private static Task<bool> Conflict(ZayraDbContext db, Guid tid, Guid co, Guid user, string period = Period)
        => CostAccountingSod.HasConflictAsync(db, tid, co, period, user, default);

    private static async Task SaveMapping(ZayraDbContext db, Guid tid, Guid? co, Guid user, Guid account, string reason = "r")
    {
        var res = await Gl(db, tid, ManagePerms, uid: user).SetMappings(co, new() { new GlMappingRequest("EARN:BASIC", account) }, default, reason);
        Status(res).Should().Be(200);
    }

    [Fact]
    public async Task ChangingAMapping_BlocksThatUser_FromLockAndExport_ButNotAnotherUser()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, a1, _) = await Seed(db);
        var u = Guid.NewGuid();
        await SaveMapping(db, tid, co.Id, u, a1.Id);

        (await Conflict(db, tid, co.Id, u)).Should().BeTrue();
        (await Conflict(db, tid, co.Id, Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task AnAdminHoldingEveryPermission_IsRefused_BecauseTheCheckIsIdentityNotRole()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, a1, _) = await Seed(db);
        var admin = Guid.NewGuid();
        var res = await Gl(db, tid, new[] { "finance.gl.read", "finance.gl.manage", "payroll.lock", "payroll.approve", "finance.gl.export", "organization.costcenter.manage" }, uid: admin)
            .SetMappings(co.Id, new() { new GlMappingRequest("EARN:BASIC", a1.Id) }, default, "r");
        Status(res).Should().Be(200);

        (await Conflict(db, tid, co.Id, admin)).Should().BeTrue();
    }

    [Fact]
    public async Task ATenantDefaultMappingChange_BlocksTheUserForEveryCompanyOfTheTenant()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _) = await Seed(db);
        var other = await AddCompany(db, tid, enabled: true);
        var group = await AddAccount(db, tid, null, "5001", "Expense");
        var u = Guid.NewGuid();
        await SaveMapping(db, tid, null, u, group.Id);

        (await Conflict(db, tid, co.Id, u)).Should().BeTrue();
        (await Conflict(db, tid, other.Id, u)).Should().BeTrue();
    }

    [Fact]
    public async Task AnotherCompanysMappingChange_DoesNotBlock()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _) = await Seed(db);
        var other = await AddCompany(db, tid, enabled: true);
        var otherAcct = await AddAccount(db, tid, other.Id, "5001", "Expense");
        var u = Guid.NewGuid();
        await SaveMapping(db, tid, other.Id, u, otherAcct.Id);

        (await Conflict(db, tid, co.Id, u)).Should().BeFalse();
        (await Conflict(db, tid, other.Id, u)).Should().BeTrue();
    }

    [Fact]
    public async Task SwitchDefaultCostCentreAndSmallCell_EachBlockTheirEditor()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _) = await Seed(db, enabled: false);
        var cc = await AddCostCentre(db, tid, co.Id, "ADMIN");
        var sw = Guid.NewGuid(); var dflt = Guid.NewGuid(); var k = Guid.NewGuid();

        Status(await Gl(db, tid, ManagePerms, uid: sw).SetCostAccounting(co.Id, new CostAccountingSettingsRequest(Enabled: true, Reason: "on"), default)).Should().Be(200);
        Status(await Gl(db, tid, ManagePerms, uid: dflt).SetCostAccounting(co.Id, new CostAccountingSettingsRequest(DefaultCostCenterId: cc.Id, Reason: "d"), default)).Should().Be(200);
        Status(await Gl(db, tid, ManagePerms, uid: k).SetCostAccounting(co.Id, new CostAccountingSettingsRequest(SmallCellMin: 3, Reason: "k"), default)).Should().Be(200);

        (await Conflict(db, tid, co.Id, sw)).Should().BeTrue();
        (await Conflict(db, tid, co.Id, dflt)).Should().BeTrue();
        (await Conflict(db, tid, co.Id, k)).Should().BeTrue();
        (await Conflict(db, tid, co.Id, Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task AnAccountRuleChange_AndThePreset_BlockTheirEditor()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, a1, _) = await Seed(db);
        var ruleEditor = Guid.NewGuid(); var preset = Guid.NewGuid();

        Status(await Gl(db, tid, ManagePerms, uid: ruleEditor).UpdateAccount(a1.Id, false, new GlAccountRequest("5001", a1.Name, "Expense", true, null, "Required"), default)).Should().Be(200);
        Status(await Gl(db, tid, ManagePerms, uid: preset).ApplyCostCentreRulePreset(co.Id, false, new CostCentreRulePresetRequest("p"), default)).Should().Be(200);

        (await Conflict(db, tid, co.Id, ruleEditor)).Should().BeTrue();
        (await Conflict(db, tid, co.Id, preset)).Should().BeTrue();
    }

    [Fact]
    public async Task AnAccountActiveChange_BlocksItsEditor()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, a1, _) = await Seed(db);
        var editor = Guid.NewGuid();

        // Same cost-centre rule, account deactivated: only finance.gl.account.updated is emitted, not rule_changed.
        Status(await Gl(db, tid, ManagePerms, uid: editor).UpdateAccount(a1.Id, false, new GlAccountRequest("5001", a1.Name, a1.AccountType, false, null, a1.CostCenterRule), default)).Should().Be(200);
        (await db.AuditLogs.CountAsync(a => a.UserId == editor && a.Action == "finance.gl.account.rule_changed")).Should().Be(0);

        (await Conflict(db, tid, co.Id, editor)).Should().BeTrue();
    }

    [Theory]
    [InlineData("organization.costcenter.class_changed")]
    [InlineData("employee.cost_centre_override.changed")]
    [InlineData("finance.gl.account.updated")]
    public async Task TheMasterDataActionsWpAEmits_AreInTheSet(string action)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _) = await Seed(db);
        var u = Guid.NewGuid();
        CostAccountingSod.RelevantActions.Should().Contain(action);
        db.AuditLogs.Add(new AuditLog { TenantId = tid, CompanyId = co.Id, UserId = u, Action = action, EntityName = "x", CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        (await Conflict(db, tid, co.Id, u)).Should().BeTrue();
    }

    [Fact]
    public async Task UnrelatedAuditActions_DoNotBlock()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _) = await Seed(db);
        var u = Guid.NewGuid();
        foreach (var a in new[] { "finance.gl.account.created", "finance.gl.driver.updated", "employee.updated", "finance.gl.period.closed" })
            db.AuditLogs.Add(new AuditLog { TenantId = tid, CompanyId = co.Id, UserId = u, Action = a, EntityName = "x", CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        (await Conflict(db, tid, co.Id, u)).Should().BeFalse();
    }

    [Fact]
    public async Task OnlyChangesSinceThePreviousLock_Count()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _) = await Seed(db);
        var early = Guid.NewGuid(); var late = Guid.NewGuid();
        var t0 = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);
        var lockAt = new DateTime(2026, 8, 31, 18, 0, 0, DateTimeKind.Utc);
        db.AuditLogs.Add(new AuditLog { TenantId = tid, CompanyId = co.Id, UserId = early, Action = "finance.gl.mapping.saved", EntityName = "m", CreatedAtUtc = t0 });
        db.AuditLogs.Add(new AuditLog { TenantId = tid, CompanyId = co.Id, UserId = late, Action = "finance.gl.mapping.saved", EntityName = "m", CreatedAtUtc = lockAt.AddDays(3) });
        await db.SaveChangesAsync();
        await AddRun(db, tid, co.Id, "Locked", year: 2026, month: 8, lockedAt: lockAt, applied: true);

        (await Conflict(db, tid, co.Id, early, "2026-09")).Should().BeFalse("the change was reviewed by the August lock");
        (await Conflict(db, tid, co.Id, late, "2026-09")).Should().BeTrue();
        (await Conflict(db, tid, co.Id, early, "2026-08")).Should().BeTrue("there is no earlier lock to have reviewed it");
    }

    [Fact]
    public async Task WhenCostingIsOff_NoOneIsBlocked_SoOldTenantsKeepTheirBehaviour()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, a1, _) = await Seed(db, enabled: false);
        var u = Guid.NewGuid();
        await SaveMapping(db, tid, co.Id, u, a1.Id);

        (await Conflict(db, tid, co.Id, u)).Should().BeFalse();
    }

    // ── ERP API keys (AM-14) ────────────────────────────────────────────────

    private static async Task<ErpApiKey> Key(ZayraDbContext db, Guid tid, Guid? co, Guid createdBy)
    {
        var k = new ErpApiKey { TenantId = tid, CompanyId = co, Name = "k", Prefix = "kx_live_ab", KeyHash = Guid.NewGuid().ToString("N"), Scopes = "journal.read", ExpiresAtUtc = DateTime.UtcNow.AddDays(30), CreatedByUserId = createdBy, CreatedByName = "u" };
        db.ErpApiKeys.Add(k);
        await db.SaveChangesAsync();
        return k;
    }

    [Fact]
    public async Task AnApiKeyCreatedBy_ATheChanger_IsBlocked_AKeyOfAnotherUserIsNot()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, a1, _) = await Seed(db);
        var changer = Guid.NewGuid(); var other = Guid.NewGuid();
        await SaveMapping(db, tid, co.Id, changer, a1.Id);
        var mine = await Key(db, tid, co.Id, changer);
        var group = await Key(db, tid, null, changer);
        var theirs = await Key(db, tid, co.Id, other);

        (await CostAccountingSod.ApiKeyHasConflictAsync(db, tid, co.Id, Period, mine.Id, default)).Should().BeTrue();
        (await CostAccountingSod.ApiKeyHasConflictAsync(db, tid, co.Id, Period, group.Id, default)).Should().BeTrue();
        (await CostAccountingSod.ApiKeyHasConflictAsync(db, tid, co.Id, Period, theirs.Id, default)).Should().BeFalse();
    }

    [Fact]
    public async Task AnUnknownApiKey_FailsClosed()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _) = await Seed(db);

        (await CostAccountingSod.ApiKeyHasConflictAsync(db, tid, co.Id, Period, Guid.NewGuid(), default)).Should().BeTrue();
    }

    [Fact]
    public void TheRefusalBody_IsBilingual_AndLeaksNothing()
    {
        var body = System.Text.Json.JsonSerializer.Serialize(CostAccountingSod.RefusalBody);
        body.Should().Contain("separation_of_duties").And.Contain("messageAr");
        CostAccountingSod.ErrorCode.Should().Be("separation_of_duties");
    }

    // ── door-guard helpers WP-A / WP-C / WP-D call ──────────────────────────

    [Theory]
    [InlineData("PendingFinanceReview", true)]
    [InlineData("Approved", true)]
    [InlineData("Locked", false)]
    [InlineData("Processed", false)]
    [InlineData("Draft", false)]
    [InlineData("Voided", false)]
    public async Task AwaitingRun_IsPendingFinanceReviewOrApproved_Only(string status, bool expected)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _) = await Seed(db);
        await AddRun(db, tid, co.Id, status);

        (await CostAccountingGuards.HasRunAwaitingFinanceOrLockAsync(db, tid, co.Id, default)).Should().Be(expected);
    }

    [Fact]
    public async Task AwaitingRun_IsPerCompany_AndNullMeansAnyCompanyOfTheTenant()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var (tid, co, _, _) = await Seed(db);
        var other = await AddCompany(db, tid);
        var foreignCo = await AddCompany(db, Guid.NewGuid());
        await AddRun(db, foreignCo.TenantId, foreignCo.Id, "Approved");
        await AddRun(db, tid, other.Id, "Approved");

        (await CostAccountingGuards.HasRunAwaitingFinanceOrLockAsync(db, tid, co.Id, default)).Should().BeFalse();
        (await CostAccountingGuards.HasRunAwaitingFinanceOrLockAsync(db, tid, null, default)).Should().BeTrue();
        (await CostAccountingGuards.HasRunAwaitingFinanceOrLockAsync(db, Guid.NewGuid(), null, default)).Should().BeFalse();
    }

    [Fact]
    public void LockedIntoApprovedRun_NamesTheRealUnlockPaths_NotSendBack()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(CostAccountingGuards.LockedIntoApprovedRunBody);
        json.Should().Contain("locked_into_approved_run").And.Contain("awaiting finance review or lock").And.Contain("reopen").And.Contain("messageAr");
        json.Should().NotContain("send back");
    }
}
