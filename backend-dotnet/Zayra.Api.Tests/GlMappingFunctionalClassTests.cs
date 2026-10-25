using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostAccountingKit;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-B slice 2 — mappings by functional class with (driver, class) uniqueness (AM-16), a fixed cost centre that is
/// company-only (AM-4), a required reason (M-10) and the M-7 door guard (AM-9).
/// T-039, T-041, T-042, S-031, S-032, S-045.
/// </summary>
public class GlMappingFunctionalClassTests
{
    private sealed record Fx(Guid Tid, Company Co, GlAccount Basic, GlAccount Selling, GlAccount Liab);

    private static async Task<Fx> Seed(ZayraDbContext db, bool costingOn = false)
    {
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, default);
        await db.SaveChangesAsync();
        var co = await AddCompany(db, tid, costingOn);
        return new Fx(tid, co,
            await AddAccount(db, tid, co.Id, "5001", "Expense"),
            await AddAccount(db, tid, co.Id, "5011", "Expense"),
            await AddAccount(db, tid, co.Id, "2101", "Liability"));
    }

    private static GlMappingRequest Plain(Guid acct, string driver = "EARN:BASIC") => new(driver, acct);
    private static GlMappingRequest Cls(Guid acct, string cls, string driver = "EARN:BASIC") => new(driver, acct, null, cls);

    // ── T-039: class rows ───────────────────────────────────────────────────

    [Fact]
    public async Task Save_MultipleClassesPerDriver_AreAccepted_AndPersistedWithTheirClass()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var ctrl = Gl(db, f.Tid, ManagePerms);

        var res = await ctrl.SetMappings(f.Co.Id,
            new() { Plain(f.Basic.Id), Cls(f.Selling.Id, "SELLING"), Cls(f.Basic.Id, "G_AND_A") }, default, "Class routing for the sales team");

        Status(res).Should().Be(200);
        db.ChangeTracker.Clear();
        var rows = await db.GlAccountMappings.IgnoreQueryFilters().Where(m => m.CompanyId == f.Co.Id && m.DriverKey == "EARN:BASIC").ToListAsync();
        rows.Select(r => r.FunctionalClass).Should().BeEquivalentTo(new string?[] { null, "SELLING", "G_AND_A" });
        rows.Single(r => r.FunctionalClass == "SELLING").AccountId.Should().Be(f.Selling.Id);
    }

    [Fact]
    public async Task Save_DuplicateDriverAndClass_IsRefused()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var ctrl = Gl(db, f.Tid, ManagePerms);

        var res = await ctrl.SetMappings(f.Co.Id, new() { Cls(f.Selling.Id, "SELLING"), Cls(f.Basic.Id, "SELLING") }, default, "dup");

        Status(res).Should().Be(400);
        ErrorCode(res).Should().Be("GL_MAPPING_DUPLICATE_DRIVER_CLASS");
        db.ChangeTracker.Clear();
        (await db.GlAccountMappings.IgnoreQueryFilters().CountAsync(m => m.CompanyId == f.Co.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Save_TwoClasslessRowsForOneDriver_StillRefusedWithTheExistingCode()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Plain(f.Basic.Id), Plain(f.Selling.Id) }, default);

        ErrorCode(res).Should().Be("GL_MAPPING_DUPLICATE_DRIVER");
    }

    [Fact]
    public async Task Save_ClassRowToALiabilityAccount_IsRefused_AllowedAccountTypesAppliesToEachClassRow()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Plain(f.Basic.Id), Cls(f.Liab.Id, "SELLING") }, default, "route");

        Status(res).Should().Be(400);
        ErrorCode(res).Should().Be("GL_MAPPING_TYPE_MISMATCH");
    }

    [Fact]
    public async Task Save_ReservedClass_Is422_FunctionalClassReserved()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Cls(f.Basic.Id, "CAPITALISABLE") }, default, "capex");

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("functional_class_reserved");
    }

    [Theory]
    [InlineData("selling")]
    [InlineData("MARKETING")]
    public async Task Save_UnknownClass_Is422_FunctionalClassInvalid(string cls)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Cls(f.Basic.Id, cls) }, default, "x");

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("functional_class_invalid");
    }

    [Fact]
    public async Task ListMappings_ReturnsClassRulesPerDriver_AndKeepsTheClasslessShape()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var ctrl = Gl(db, f.Tid, ManagePerms);
        await ctrl.SetMappings(f.Co.Id, new() { Plain(f.Basic.Id), Cls(f.Selling.Id, "SELLING") }, default, "x");

        var row = Body(await ctrl.ListMappings(f.Co.Id, default)).EnumerateArray().Single(r => r.P("driverKey").GetString() == "EARN:BASIC");

        row.P("mappedAccountId").GetGuid().Should().Be(f.Basic.Id);
        var rule = row.P("classRules").EnumerateArray().Single();
        rule.P("functionalClass").GetString().Should().Be("SELLING");
        rule.P("accountId").GetGuid().Should().Be(f.Selling.Id);
    }

    // ── S-031 / AM-4: the fixed cost centre ─────────────────────────────────

    private static GlMappingRequest Fixed(Guid acct, Guid cc, string driver = "EARN:BASIC") => new(driver, acct, cc);

    [Fact]
    public async Task Save_FixedCostCentreOfThisCompany_IsAccepted()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var cc = await AddCostCentre(db, f.Tid, f.Co.Id, "SALES");

        Status(await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Fixed(f.Basic.Id, cc.Id) }, default, "Sales payroll to SALES")).Should().Be(200);

        db.ChangeTracker.Clear();
        (await db.GlAccountMappings.IgnoreQueryFilters().SingleAsync(m => m.CompanyId == f.Co.Id)).SegmentCostCenterId.Should().Be(cc.Id);
    }

    [Fact]
    public async Task Save_FixedCostCentre_OfAnotherCompany_Is422()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var other = await AddCompany(db, f.Tid);
        var cc = await AddCostCentre(db, f.Tid, other.Id, "OTHER");

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Fixed(f.Basic.Id, cc.Id) }, default, "x");

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("fixed_cost_centre_invalid");
    }

    [Fact]
    public async Task Save_FixedCostCentre_WithNoCompany_Is422()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var cc = await AddCostCentre(db, f.Tid, null, "GROUPCC");

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Fixed(f.Basic.Id, cc.Id) }, default, "x");

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("fixed_cost_centre_invalid");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Save_FixedCostCentre_InactiveOrDeleted_Is422(bool inactive, bool deleted)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var cc = await AddCostCentre(db, f.Tid, f.Co.Id, "GONE", active: !inactive, deleted: deleted);

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Fixed(f.Basic.Id, cc.Id) }, default, "x");

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("fixed_cost_centre_invalid");
    }

    [Fact]
    public async Task Save_FixedCostCentre_OnATenantDefaultMapping_Is422_AM4()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var group = await AddAccount(db, f.Tid, null, "5001", "Expense");
        var cc = await AddCostCentre(db, f.Tid, f.Co.Id, "SALES");

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(null, new() { Fixed(group.Id, cc.Id) }, default, "x");

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("fixed_cost_centre_requires_company");
    }

    [Fact]
    public async Task Save_FixedCostCentre_OfAnotherTenant_Is422()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var foreignCo = await AddCompany(db, Guid.NewGuid());
        var cc = await AddCostCentre(db, foreignCo.TenantId, foreignCo.Id, "FOREIGN");

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Fixed(f.Basic.Id, cc.Id) }, default, "x");

        Status(res).Should().Be(422);
    }

    [Fact]
    public async Task Save_FixedCostCentre_AndAClass_TogetherAreRefused()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var cc = await AddCostCentre(db, f.Tid, f.Co.Id, "SALES");

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { new GlMappingRequest("EARN:BASIC", f.Basic.Id, cc.Id, "SELLING") }, default, "x");

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("fixed_cost_centre_with_class");
    }

    // ── T-041: reason + audit ───────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t ")]
    public async Task Save_ThatTouchesClassOrFixedCostCentre_NeedsAReason_M10(string? reason)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Cls(f.Selling.Id, "SELLING") }, default, reason);

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("cost_centre_reason_required");
        Body(res).P("message").GetString().Should().Be("Enter a reason for this change.");
        db.ChangeTracker.Clear();
        (await db.GlAccountMappings.IgnoreQueryFilters().CountAsync(m => m.CompanyId == f.Co.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Save_AnyChange_WhenCostingIsOn_NeedsAReason()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db, costingOn: true);

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Plain(f.Basic.Id) }, default);

        Status(res).Should().Be(422);
        ErrorCode(res).Should().Be("cost_centre_reason_required");
    }

    [Fact]
    public async Task Save_PlainChange_WhenCostingIsOff_KeepsTheLegacyContract_NoReasonNeeded()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);

        Status(await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Plain(f.Basic.Id) }, default)).Should().Be(200);
    }

    [Fact]
    public async Task Save_AuditRow_CarriesReason_AndOldAndNewAccountClassAndCostCentre()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var cc = await AddCostCentre(db, f.Tid, f.Co.Id, "SALES");
        var ctrl = Gl(db, f.Tid, ManagePerms);
        await ctrl.SetMappings(f.Co.Id, new() { Plain(f.Basic.Id) }, default, "first");

        await ctrl.SetMappings(f.Co.Id, new() { Fixed(f.Selling.Id, cc.Id) }, default, "Re-point basic to the sales cost centre");

        var audit = (await db.AuditLogs.Where(a => a.Action == "finance.gl.mapping.saved").OrderBy(a => a.CreatedAtUtc).ToListAsync()).Last();
        audit.CompanyId.Should().Be(f.Co.Id);
        audit.Metadata.Should().Contain("Re-point basic to the sales cost centre")
            .And.Contain(f.Basic.Id.ToString()).And.Contain(f.Selling.Id.ToString()).And.Contain(cc.Id.ToString());
        audit.Metadata.Should().Contain("oldClass").And.Contain("newClass");
    }

    [Fact]
    public async Task Save_AuditFailure_RollsTheMappingChangeBack()
    {
        var failing = new FailOnAuditRow();
        var (db, conn) = NewDb(failing); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var ctrl = Gl(db, f.Tid, ManagePerms);
        await ctrl.SetMappings(f.Co.Id, new() { Plain(f.Basic.Id) }, default, "first");

        failing.Armed = true;
        var act = () => ctrl.SetMappings(f.Co.Id, new() { Plain(f.Selling.Id) }, default, "second");
        await act.Should().ThrowAsync<InvalidOperationException>();

        failing.Armed = false;
        db.ChangeTracker.Clear();
        (await db.GlAccountMappings.IgnoreQueryFilters().SingleAsync(m => m.CompanyId == f.Co.Id)).AccountId.Should().Be(f.Basic.Id);
    }

    // ── T-042 / S-005: door guard ───────────────────────────────────────────

    [Theory]
    [InlineData("PendingFinanceReview")]
    [InlineData("Approved")]
    public async Task Save_WhileARunAwaitsFinanceOrLock_Is409_M7(string status)
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        await AddRun(db, f.Tid, f.Co.Id, status);

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(f.Co.Id, new() { Plain(f.Basic.Id) }, default, "x");

        Status(res).Should().Be(409);
        ErrorCode(res).Should().Be("locked_into_approved_run");
        db.ChangeTracker.Clear();
        (await db.GlAccountMappings.IgnoreQueryFilters().CountAsync(m => m.CompanyId == f.Co.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Save_AfterTheRunIsLocked_Succeeds_AndAnotherCompanysRunNeverBlocks()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var other = await AddCompany(db, f.Tid);
        await AddRun(db, f.Tid, other.Id, "Approved");
        var run = await AddRun(db, f.Tid, f.Co.Id, "Approved");
        var ctrl = Gl(db, f.Tid, ManagePerms);

        Status(await ctrl.SetMappings(f.Co.Id, new() { Plain(f.Basic.Id) }, default, "x")).Should().Be(409);
        await db.PayrollRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Locked"));
        Status(await ctrl.SetMappings(f.Co.Id, new() { Plain(f.Basic.Id) }, default, "x")).Should().Be(200);
    }

    [Fact]
    public async Task Save_TenantDefaultMappings_AreBlocked_WhenAnyCompanyOfTheTenantHasAnApprovedRun()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);
        var group = await AddAccount(db, f.Tid, null, "5001", "Expense");
        await AddRun(db, f.Tid, f.Co.Id, "Approved");

        var res = await Gl(db, f.Tid, ManagePerms).SetMappings(null, new() { Plain(group.Id) }, default, "x");

        Status(res).Should().Be(409);
        ErrorCode(res).Should().Be("locked_into_approved_run");
    }

    [Fact]
    public async Task Save_NeedsFinanceGlManage_NotTheCostCentrePermission()
    {
        var (db, conn) = NewDb(); await using var _ = conn; await using var __ = db;
        var f = await Seed(db);

        (await Gl(db, f.Tid, new[] { "finance.gl.read", "organization.costcenter.manage" })
            .SetMappings(f.Co.Id, new() { Plain(f.Basic.Id) }, default, "x")).Should().BeOfType<ForbidResult>();
    }
}
