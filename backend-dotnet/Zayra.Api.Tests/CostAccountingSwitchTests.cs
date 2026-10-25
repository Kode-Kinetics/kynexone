using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostAccountingKit;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-B slice 5a — the <c>cost_accounting_enabled</c> switch, the company default cost centre and the small-cell K
/// (AM-3, AM-9, AM-15, S-003, S-005, S-006, S-037, S-045, S-053, T-042).
/// </summary>
public class CostAccountingSwitchTests
{
    private static CostAccountingSettingsRequest On(string? reason = "Go live", bool ack = false) => new(Enabled: true, Reason: reason, AcknowledgeBlockers: ack);
    private static CostAccountingSettingsRequest Off(string? reason = "Rolling back") => new(Enabled: false, Reason: reason);

    private static async Task<Employee> Emp(ZayraDbContext db, Guid tid, Guid co, string code, Action<Employee>? cfg = null)
    {
        var e = new Employee { TenantId = tid, CompanyId = co, EmployeeCode = code, FullName = "Emp " + code, Status = "Active", JoiningDate = new DateTime(2024, 1, 1) };
        cfg?.Invoke(e);
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    // ── S-003: who may change it, reason, omitted fields ────────────────────

    [Fact]
    public async Task OnlyFinanceGlManage_CanChangeTheSwitch_NotHrOrACostCentreOnlyRole()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var cc = await AddCostCentre(db, tid, co.Id, "ADMIN");

        var hr = new[] { "organization.read", "employees.read", "employees.write", "finance.gl.read" };
        var costOnly = new[] { "organization.costcenter.manage", "organization.read", "finance.gl.read" };
        foreach (var perms in new[] { hr, costOnly, ReadPerms })
        {
            var ctrl = Gl(db, tid, perms);
            (await ctrl.SetCostAccounting(co.Id, On(), default)).Should().BeOfType<ForbidResult>();
            (await ctrl.SetCostAccounting(co.Id, new CostAccountingSettingsRequest(DefaultCostCenterId: cc.Id, Reason: "r"), default)).Should().BeOfType<ForbidResult>();
            (await ctrl.SetCostAccounting(co.Id, new CostAccountingSettingsRequest(SmallCellMin: 3, Reason: "r"), default)).Should().BeOfType<ForbidResult>();
        }
        db.ChangeTracker.Clear();
        var after = await db.Companies.SingleAsync(c => c.Id == co.Id);
        (after.CostAccountingEnabled, after.DefaultCostCenterId, after.CostCentreSmallCellMin).Should().Be((false, (Guid?)null, (short)0));

        Status(await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, On(), default)).Should().Be(200);
    }

    [Fact]
    public async Task AdminHoldingEveryPermission_IsStillJustFinanceGlManage_AndPassesThisGate()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);

        var admin = new[] { "finance.gl.read", "finance.gl.manage", "organization.costcenter.manage", "payroll.approve", "payroll.lock", "finance.gl.export" };
        Status(await Gl(db, tid, admin).SetCostAccounting(co.Id, On(), default)).Should().Be(200);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EachChange_NeedsAReason_M10(string? reason)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var cc = await AddCostCentre(db, tid, co.Id, "ADMIN");
        var ctrl = Gl(db, tid, ManagePerms);

        foreach (var req in new[]
        {
            new CostAccountingSettingsRequest(Enabled: true, Reason: reason),
            new CostAccountingSettingsRequest(DefaultCostCenterId: cc.Id, Reason: reason),
            new CostAccountingSettingsRequest(SmallCellMin: 3, Reason: reason),
        })
        {
            var res = await ctrl.SetCostAccounting(co.Id, req, default);
            Status(res).Should().Be(422);
            ErrorCode(res).Should().Be("cost_centre_reason_required");
            Body(res).P("messageAr").GetString().Should().Be("أدخل سببًا لهذا التغيير.");
        }
        db.ChangeTracker.Clear();
        var after = await db.Companies.SingleAsync(c => c.Id == co.Id);
        (after.CostAccountingEnabled, after.DefaultCostCenterId, after.CostCentreSmallCellMin).Should().Be((false, (Guid?)null, (short)0));
    }

    [Fact]
    public async Task AnOmittedField_LeavesItUnchanged()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var cc0 = Guid.NewGuid();
        var co = await AddCompany(db, tid, enabled: true);
        var cc = await AddCostCentre(db, tid, co.Id, "ADMIN");
        co.DefaultCostCenterId = cc.Id; co.CostCentreSmallCellMin = 4;
        await db.SaveChangesAsync();
        var ctrl = Gl(db, tid, ManagePerms);

        Status(await ctrl.SetCostAccounting(co.Id, new CostAccountingSettingsRequest(SmallCellMin: 5, Reason: "tune"), default)).Should().Be(200);

        db.ChangeTracker.Clear();
        var after = await db.Companies.SingleAsync(c => c.Id == co.Id);
        (after.CostAccountingEnabled, after.DefaultCostCenterId, after.CostCentreSmallCellMin).Should().Be((true, (Guid?)cc.Id, (short)5));
    }

    [Fact]
    public async Task ANoOpRequest_ChangesNothing_NeedsNoReason_WritesNoAudit()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var ctrl = Gl(db, tid, ManagePerms);

        Status(await ctrl.SetCostAccounting(co.Id, new CostAccountingSettingsRequest(), default)).Should().Be(200);
        Status(await ctrl.SetCostAccounting(co.Id, new CostAccountingSettingsRequest(Enabled: false, SmallCellMin: 0), default)).Should().Be(200);

        (await db.AuditLogs.CountAsync(a => a.Action.StartsWith("finance.gl.cost_accounting"))).Should().Be(0);
    }

    // ── audit + atomicity (S-003, S-045) ────────────────────────────────────

    [Fact]
    public async Task Enabling_StagesAnAuditRow_WithBeforeAfterReasonAndCompany()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var userId = Guid.NewGuid();

        Status(await Gl(db, tid, ManagePerms, uid: userId).SetCostAccounting(co.Id, On("Finance sign-off 2026-10"), default)).Should().Be(200);

        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "finance.gl.cost_accounting.switch_changed");
        (audit.CompanyId, audit.UserId).Should().Be((co.Id, (Guid?)userId));
        audit.Metadata.Should().Contain("Finance sign-off 2026-10").And.Contain("\"before\"").And.Contain("\"after\"");
        db.ChangeTracker.Clear();
        (await db.Companies.SingleAsync(c => c.Id == co.Id)).CostAccountingEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("default")]
    [InlineData("k")]
    public async Task AuditFailure_RollsTheChangeBack(string which)
    {
        var failing = new FailOnAuditRow();
        var (db, conn) = NewDb(failing); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var cc = await AddCostCentre(db, tid, co.Id, "ADMIN");
        var ctrl = Gl(db, tid, ManagePerms);
        var req = which switch
        {
            "switch" => On(),
            "default" => new CostAccountingSettingsRequest(DefaultCostCenterId: cc.Id, Reason: "r"),
            _ => new CostAccountingSettingsRequest(SmallCellMin: 3, Reason: "r"),
        };

        failing.Armed = true;
        var act = () => ctrl.SetCostAccounting(co.Id, req, default);
        await act.Should().ThrowAsync<InvalidOperationException>();

        failing.Armed = false;
        db.ChangeTracker.Clear();
        var after = await db.Companies.SingleAsync(c => c.Id == co.Id);
        (after.CostAccountingEnabled, after.DefaultCostCenterId, after.CostCentreSmallCellMin).Should().Be((false, (Guid?)null, (short)0));
    }

    // ── M-7 (AM-3 / AM-9) ───────────────────────────────────────────────────

    [Theory]
    [InlineData("PendingFinanceReview", "switch-on")]
    [InlineData("Approved", "switch-on")]
    [InlineData("Approved", "switch-off")]
    [InlineData("PendingFinanceReview", "default")]
    [InlineData("Approved", "k")]
    public async Task WhileARunAwaitsFinanceOrLock_EveryChangeIs409_M7(string status, string change)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid, enabled: change == "switch-off");
        var cc = await AddCostCentre(db, tid, co.Id, "ADMIN");
        await AddRun(db, tid, co.Id, status);
        var req = change switch
        {
            "switch-on" => On(),
            "switch-off" => Off(),
            "default" => new CostAccountingSettingsRequest(DefaultCostCenterId: cc.Id, Reason: "r"),
            _ => new CostAccountingSettingsRequest(SmallCellMin: 3, Reason: "r"),
        };

        var res = await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, req, default);

        Status(res).Should().Be(409);
        ErrorCode(res).Should().Be("locked_into_approved_run");
        db.ChangeTracker.Clear();
        var after = await db.Companies.SingleAsync(c => c.Id == co.Id);
        (after.CostAccountingEnabled, after.DefaultCostCenterId, after.CostCentreSmallCellMin).Should().Be((change == "switch-off", (Guid?)null, (short)0));
    }

    [Fact]
    public async Task AfterTheRunIsLocked_TheSameCallSucceeds()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var run = await AddRun(db, tid, co.Id, "Approved");
        var ctrl = Gl(db, tid, ManagePerms);

        Status(await ctrl.SetCostAccounting(co.Id, On(), default)).Should().Be(409);
        run.Status = "Locked"; await db.SaveChangesAsync();
        Status(await ctrl.SetCostAccounting(co.Id, On(), default)).Should().Be(200);
    }

    // ── S-006: turning it off after costed runs exist ───────────────────────

    [Fact]
    public async Task TurningOff_AfterACostedRunWasLocked_IsRefused_AndTheAttemptIsAudited()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid, enabled: true);
        await AddRun(db, tid, co.Id, "Locked", lockedAt: DateTime.UtcNow.AddDays(-3), applied: true);

        var res = await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, Off("Back to old basis"), default);

        Status(res).Should().Be(409);
        ErrorCode(res).Should().Be("cost_accounting_has_costed_runs");
        db.ChangeTracker.Clear();
        (await db.Companies.SingleAsync(c => c.Id == co.Id)).CostAccountingEnabled.Should().BeTrue();
        var attempt = await db.AuditLogs.SingleAsync(a => a.Action == "finance.gl.cost_accounting.switch_change_refused");
        (attempt.CompanyId).Should().Be(co.Id);
        attempt.Metadata.Should().Contain("Back to old basis");
    }

    [Fact]
    public async Task TurningOff_WithOnlyLegacyLockedRuns_IsAllowed()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid, enabled: true);
        await AddRun(db, tid, co.Id, "Locked", lockedAt: DateTime.UtcNow.AddDays(-3), applied: false);

        Status(await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, Off(), default)).Should().Be(200);
        db.ChangeTracker.Clear();
        (await db.Companies.SingleAsync(c => c.Id == co.Id)).CostAccountingEnabled.Should().BeFalse();
    }

    // ── AM-3: blockers need an acknowledgement ──────────────────────────────

    private static async Task<(Department dept, CostCenter dead)> InvalidDepartment(ZayraDbContext db, Guid tid, Guid co)
    {
        var dead = await AddCostCentre(db, tid, co, "DEAD", deleted: true);
        var d = new Department { TenantId = tid, CompanyId = co, Code = "OPS", NameEn = "Ops", CostCenterId = dead.Id };
        db.Departments.Add(d);
        await db.SaveChangesAsync();
        return (d, dead);
    }

    [Fact]
    public async Task Enabling_WithBlockers_WithoutAcknowledgement_Is409_WithTheReadinessPayload()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        await InvalidDepartment(db, tid, co.Id);

        var res = await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, On(ack: false), default);

        Status(res).Should().Be(409);
        ErrorCode(res).Should().Be("cost_accounting_blockers");
        var checks = Body(res).P("checks").EnumerateArray().ToList();
        checks.Single(c => c.P("key").GetString() == "invalid-reference").P("count").GetInt32().Should().Be(1);
        db.ChangeTracker.Clear();
        (await db.Companies.SingleAsync(c => c.Id == co.Id)).CostAccountingEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Enabling_WithBlockers_AndAcknowledgement_Succeeds_AndTheAuditRecordsTheAcknowledgement()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        await InvalidDepartment(db, tid, co.Id);

        Status(await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, On("Pilot", ack: true), default)).Should().Be(200);

        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "finance.gl.cost_accounting.switch_changed");
        audit.Metadata.Should().Contain("acknowledgeBlockers").And.Contain("invalid-reference");
        db.ChangeTracker.Clear();
        (await db.Companies.SingleAsync(c => c.Id == co.Id)).CostAccountingEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Enabling_WithNoBlockers_NeedsNoAcknowledgement()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);

        Status(await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, On(ack: false), default)).Should().Be(200);
    }

    // ── S-037: small-cell exposure ──────────────────────────────────────────

    [Fact]
    public async Task Enabling_WithAOneEmployeeCostCentre_AndKZero_ReportsSmallCellExposure_AndNeedsAcknowledgement()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var cc = await AddCostCentre(db, tid, co.Id, "SOLO");
        await Emp(db, tid, co.Id, "E1", e => { e.CostCenterId = cc.Id; e.CostCenterIsOverride = true; });
        var ctrl = Gl(db, tid, ManagePerms);

        var refused = await ctrl.SetCostAccounting(co.Id, On(ack: false), default);
        Status(refused).Should().Be(409);
        Body(refused).P("smallCellExposure").P("count").GetInt32().Should().Be(1);

        var ok = await ctrl.SetCostAccounting(co.Id, On("Accepting the single-employee cell", ack: true), default);
        Status(ok).Should().Be(200);
        Body(ok).P("smallCellExposure").P("count").GetInt32().Should().Be(1);
        (await db.AuditLogs.SingleAsync(a => a.Action == "finance.gl.cost_accounting.switch_changed")).Metadata.Should().Contain("smallCellExposureAcknowledged");
    }

    [Fact]
    public async Task SmallCellExposure_IsZero_WhenKIsAtLeastThree()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid, cfg: c => c.CostCentreSmallCellMin = 3);
        var cc = await AddCostCentre(db, tid, co.Id, "SOLO");
        await Emp(db, tid, co.Id, "E1", e => { e.CostCenterId = cc.Id; e.CostCenterIsOverride = true; });

        var res = await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, On(ack: false), default);

        Status(res).Should().Be(200);
        Body(res).P("smallCellExposure").P("count").GetInt32().Should().Be(0);
    }

    // ── S-053: 3-dp currencies ──────────────────────────────────────────────

    [Theory]
    [InlineData("KWD")]
    [InlineData("BHD")]
    [InlineData("OMR")]
    [InlineData("kwd")]
    public async Task Enabling_ForAThreeDecimalCurrency_IsRefused_EvenWithAcknowledgement(string currency)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid, currency: currency);

        var res = await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, On(ack: true), default);

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("currency_scale_unsupported_for_payroll");
        db.ChangeTracker.Clear();
        (await db.Companies.SingleAsync(c => c.Id == co.Id)).CostAccountingEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("SAR")]
    [InlineData("AED")]
    [InlineData("QAR")]
    public async Task Enabling_ForATwoDecimalCurrency_IsAllowed(string currency)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid, currency: currency);

        Status(await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, On(), default)).Should().Be(200);
    }

    // ── default cost centre ─────────────────────────────────────────────────

    [Fact]
    public async Task DefaultCostCentre_OfThisCompany_IsSet_Audited_AndCanBeCleared()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var cc = await AddCostCentre(db, tid, co.Id, "ADMIN");
        var ctrl = Gl(db, tid, ManagePerms);

        Status(await ctrl.SetCostAccounting(co.Id, new CostAccountingSettingsRequest(DefaultCostCenterId: cc.Id, Reason: "Fallback for unassigned staff"), default)).Should().Be(200);
        db.ChangeTracker.Clear();
        (await db.Companies.SingleAsync(c => c.Id == co.Id)).DefaultCostCenterId.Should().Be(cc.Id);
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "finance.gl.cost_accounting.default_cost_centre_changed");
        audit.CompanyId.Should().Be(co.Id);
        audit.Metadata.Should().Contain(cc.Id.ToString()).And.Contain("Fallback for unassigned staff");

        Status(await ctrl.SetCostAccounting(co.Id, new CostAccountingSettingsRequest(ClearDefaultCostCenter: true, Reason: "Removing"), default)).Should().Be(200);
        db.ChangeTracker.Clear();
        (await db.Companies.SingleAsync(c => c.Id == co.Id)).DefaultCostCenterId.Should().BeNull();
    }

    [Theory]
    [InlineData("other-company")]
    [InlineData("no-company")]
    [InlineData("inactive")]
    [InlineData("deleted")]
    [InlineData("unknown")]
    [InlineData("other-tenant")]
    public async Task DefaultCostCentre_ThatIsNotAnActiveCostCentreOfThisCompany_Is422(string kind)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var other = await AddCompany(db, tid);
        var foreign = await AddCompany(db, Guid.NewGuid());
        var id = kind switch
        {
            "other-company" => (await AddCostCentre(db, tid, other.Id, "X")).Id,
            "no-company" => (await AddCostCentre(db, tid, null, "X")).Id,
            "inactive" => (await AddCostCentre(db, tid, co.Id, "X", active: false)).Id,
            "deleted" => (await AddCostCentre(db, tid, co.Id, "X", deleted: true)).Id,
            "other-tenant" => (await AddCostCentre(db, foreign.TenantId, foreign.Id, "X")).Id,
            _ => Guid.NewGuid(),
        };

        var res = await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, new CostAccountingSettingsRequest(DefaultCostCenterId: id, Reason: "r"), default);

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("default_cost_centre_invalid");
        db.ChangeTracker.Clear();
        (await db.Companies.SingleAsync(c => c.Id == co.Id)).DefaultCostCenterId.Should().BeNull();
    }

    // ── small-cell K ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task SmallCellMin_OutOfRange_Is422(int k)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);

        var res = await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, new CostAccountingSettingsRequest(SmallCellMin: (short)k, Reason: "r"), default);

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("small_cell_min_invalid");
    }

    [Fact]
    public async Task SmallCellMin_IsSet_AndAudited()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);

        Status(await Gl(db, tid, ManagePerms).SetCostAccounting(co.Id, new CostAccountingSettingsRequest(SmallCellMin: 3, Reason: "Privacy floor"), default)).Should().Be(200);

        db.ChangeTracker.Clear();
        (await db.Companies.SingleAsync(c => c.Id == co.Id)).CostCentreSmallCellMin.Should().Be(3);
        (await db.AuditLogs.SingleAsync(a => a.Action == "finance.gl.cost_accounting.small_cell_changed")).Metadata.Should().Contain("Privacy floor");
    }

    // ── scope ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task OtherCompaniesAndTenants_AreRefused()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await AddCompany(db, tid);
        var other = await AddCompany(db, tid);
        var foreign = await AddCompany(db, Guid.NewGuid());

        (await Gl(db, tid, ManagePerms, CompanyScope(other.Id)).SetCostAccounting(co.Id, On(), default)).Should().BeOfType<ForbidResult>();
        Status(await Gl(db, tid, ManagePerms).SetCostAccounting(foreign.Id, On(), default)).Should().Be(404);
        Status(await Gl(db, tid, ManagePerms).SetCostAccounting(Guid.NewGuid(), On(), default)).Should().Be(404);
    }
}
