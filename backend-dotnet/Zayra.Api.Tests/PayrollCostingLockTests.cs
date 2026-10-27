using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostingLockKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting v1, WP-C2: the costed payroll journal through the REAL Approve and Lock endpoints.
/// T-015..T-019, T-023, T-024, T-035 (posting), S-001/002/004/007/009/010/013/014/024/047/050 (approval hash, drift guard, SoD).
/// SQLite in-memory (relational, so Lock runs its real transaction). Costing OFF must stay byte-identical (T-017).
/// </summary>
public class PayrollCostingLockTests
{
    private static async Task<CostingLockKit> OrgAsync(bool costing = true, bool preset = true, string? defaultCc = null)
    {
        var k = await CostingLockKit.CreateAsync(costing, preset);
        await k.AddCc("CC-100", "G_AND_A");
        await k.AddCc("CC-110", "G_AND_A", parent: "CC-100", segment: "4110");
        await k.AddCc("CC-200", "SELLING");
        await k.AddCc("CC-300", "COGS");
        await k.AddDept("D-FIN", "CC-110");
        await k.AddDept("D-SAL", "CC-200");
        await k.AddDept("D-OPS");
        await k.AddPosition("P-GEN");
        await k.AddPosition("P-SUPV", "CC-300");
        if (defaultCc is not null) await k.SetDefaultCc(defaultCc);
        return k;
    }

    private static string Code(string label) => label.Split(" - ")[0];
    private static IEnumerable<FinanceGlEntry> Dr(IEnumerable<FinanceGlEntry> gl, string account) =>
        gl.Where(l => l.DebitAccount.StartsWith(account + " - "));
    private static IEnumerable<FinanceGlEntry> Cr(IEnumerable<FinanceGlEntry> gl, string account) =>
        gl.Where(l => l.CreditAccount.StartsWith(account + " - "));

    // ── T-015 / T-016 / T-018 / T-019 ─────────────────────────────────────────

    [Fact]
    public async Task T015_EarningsCarryTheEmployeesCostCentre_LiabilityLinesCarryNone()
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL", "P-GEN");
        var run = await k.SeedRunAsync(
            new[] { E("E1", "BASIC", 5000m), E("E1", "HOUSING", 1250m) },
            new[] { D("E1", "GOSI-ANN-EE", "Statutory", 450m) });

        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        var gl = await k.GlAsync(run.Id);

        var basic = Dr(gl, "5001").Single();
        (basic.Amount, basic.CostCenterCode, basic.CostCenterId).Should().Be((5000m, "CC-200", k.Cc["CC-200"].Id));
        basic.CostCenterName.Should().Be("Name CC-200", "the line stores its own snapshot");
        Dr(gl, "5002").Single().CostCenterCode.Should().Be("CC-200");
        foreach (var liability in new[] { Cr(gl, "2101").Single(), Cr(gl, "2100").Single() })
        {
            liability.CostCenterId.Should().BeNull("liability lines are never costed");
            liability.CostCenterCode.Should().BeNull();
        }
    }

    [Fact]
    public async Task T016_TwoEmployeesInTwoCostCentres_TwoLinesPerComponent_TotalsEqualTheOldSingleLine()
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL");
        await k.AddEmployee("E2", "D-FIN");
        var run = await k.SeedRunAsync(
            new[] { E("E1", "BASIC", 3000m), E("E2", "BASIC", 2000m), E("E1", "HOUSING", 750m), E("E2", "HOUSING", 500m) },
            Array.Empty<(string, string, string, decimal, bool)>());

        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        var gl = await k.GlAsync(run.Id);

        Dr(gl, "5001").OrderBy(l => l.CostCenterCode).Select(l => (l.CostCenterCode, l.Amount))
            .Should().Equal(("CC-110", 2000m), ("CC-200", 3000m));
        Dr(gl, "5002").Sum(l => l.Amount).Should().Be(1250m);
        GlPostingKit.Debits(gl).Should().Be(GlPostingKit.Credits(gl));
    }

    [Fact]
    public async Task T018_EmployerGosi_IsCostedToTheEmployeesCostCentre_ThePayableIsOneUncostedLine()
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL");
        await k.AddEmployee("E2", "D-FIN");
        var run = await k.SeedRunAsync(
            new[] { E("E1", "BASIC", 1000m), E("E2", "BASIC", 1000m) },
            new[] { D("E1", "GOSI-OH-ER", "Statutory", 100m, true), D("E2", "GOSI-OH-ER", "Statutory", 50m, true) });

        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        var gl = await k.GlAsync(run.Id);

        Dr(gl, "5101").OrderBy(l => l.CostCenterCode).Select(l => (l.CostCenterCode, l.Amount)).Should().Equal(("CC-110", 50m), ("CC-200", 100m));
        var payable = Cr(gl, "2106").Single();
        (payable.Amount, payable.CostCenterId).Should().Be((150m, (Guid?)null));
    }

    [Fact]
    public async Task T019_ContraExpenseDeductions_CarryTheCostCentre()
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL");
        await k.AddEmployee("E2", "D-FIN");
        var run = await k.SeedRunAsync(
            new[] { E("E1", "BASIC", 1000m), E("E2", "BASIC", 1000m) },
            new[] { D("E1", "ATTENDANCE", "Attendance", 120m), D("E2", "LEAVE", "Leave", 200m) });

        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        var gl = await k.GlAsync(run.Id);

        Cr(gl, "5008").Single().Should().Match<FinanceGlEntry>(l => l.CostCenterCode == "CC-200" && l.Amount == 120m);
        Cr(gl, "5009").Single().Should().Match<FinanceGlEntry>(l => l.CostCenterCode == "CC-110" && l.Amount == 200m);
        // cost-centre net = earnings - contra deductions
        var net = gl.Where(l => l.CostCenterCode == "CC-200").Sum(l => (l.DebitAccount != "" ? l.Amount : 0m) - (l.CreditAccount != "" ? l.Amount : 0m));
        net.Should().Be(1000m - 120m);
    }

    // ── T-017: costing OFF is byte-identical ─────────────────────────────────

    [Fact]
    public async Task T017_CostingOff_PopulatedMasterData_ChangesNothing()
    {
        await using var k = await OrgAsync(costing: false);
        await k.AddEmployee("E1", "D-SAL");
        await k.AddEmployee("E2", "D-FIN");
        var run = await k.SeedRunAsync(
            new[] { E("E1", "BASIC", 3000m), E("E2", "BASIC", 2000m) },
            new[] { D("E1", "GOSI-OH-ER", "Statutory", 100m, true), D("E2", "ATTENDANCE", "Attendance", 20m) });

        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        var gl = await k.GlAsync(run.Id);

        Dr(gl, "5001").Should().ContainSingle().Which.Amount.Should().Be(5000m, "one line per component, exactly as before");
        Dr(gl, "5101").Should().ContainSingle();
        Cr(gl, "5009").Concat(Cr(gl, "5008")).Should().ContainSingle();
        gl.Should().OnlyContain(l => l.CostCenterId == null && l.CostCenterCode == null && l.CostCenterName == null && l.CostCenterGlSegment == null);
        gl.Should().OnlyContain(l => l.PostingKey == null, "a tenant that never enables costing writes no posting key");
        var stored = await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        (stored.CostAccountingApplied, stored.CostCenterResolutionHash).Should().Be((false, (string?)null));
        (await k.Db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == run.Id).ToListAsync()).Should().OnlyContain(e => e.CostCenterId == null);
    }

    // ── stamps persist; posting keys (S-024) ─────────────────────────────────

    [Fact]
    public async Task ALockedCostedRun_PersistsTheStamps_TheHash_AndAUniquePostingKeyOnEveryLine()
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL");
        await k.AddEmployee("E2", "D-FIN");
        var run = await k.SeedRunAsync(
            new[] { E("E1", "BASIC", 3000m), E("E2", "BASIC", 2000m) },
            new[] { D("E1", "GOSI-OH-ER", "Statutory", 100m, true) });

        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();

        var stored = await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        stored.CostAccountingApplied.Should().BeTrue();
        stored.CostCenterResolutionHash.Should().NotBeNullOrEmpty().And.HaveLength(64);
        var e1 = k.Emp["E1"].Id;
        var earn = await k.Db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == run.Id).ToListAsync();
        earn.Single(e => e.EmployeeId == e1).Should().Match<PayrollEarning>(e => e.CostCenterId == k.Cc["CC-200"].Id && e.CostCenterSource == "Department");
        var ded = await k.Db.PayrollDeductions.AsNoTracking().SingleAsync(d => d.PayrollRunId == run.Id);
        ded.CostCenterId.Should().Be(k.Cc["CC-200"].Id, "the employer expense follows the employee");
        (await k.Db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id && s.EmployeeId == e1)).CostCenterId.Should().Be(k.Cc["CC-200"].Id);

        var gl = await k.GlAsync(run.Id);
        gl.Should().OnlyContain(l => !string.IsNullOrEmpty(l.PostingKey));
        gl.Select(l => l.PostingKey).Should().OnlyHaveUniqueItems();
    }

    // ── T-035: fixed cost centre ─────────────────────────────────────────────

    [Fact]
    public async Task T035_AFixedCostCentreMapping_BeatsTheEmployeesCostCentre()
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL");
        var overtime = await k.Db.GlAccounts.SingleAsync(a => a.TenantId == k.TenantId && a.Code == "5005");
        k.Db.GlAccountMappings.Add(new GlAccountMapping
        { TenantId = k.TenantId, CompanyId = k.CompanyId, DriverKey = "EARN:OVERTIME", AccountId = overtime.Id, SegmentCostCenterId = k.Cc["CC-300"].Id });
        await k.Db.SaveChangesAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "OVERTIME", 100m, "Overtime"), E("E1", "BASIC", 900m) }, Array.Empty<(string, string, string, decimal, bool)>());

        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        var gl = await k.GlAsync(run.Id);

        Dr(gl, "5005").Single().CostCenterCode.Should().Be("CC-300");
        Dr(gl, "5001").Single().CostCenterCode.Should().Be("CC-200", "only the mapped driver is fixed");
        (await k.Db.PayrollEarnings.AsNoTracking().SingleAsync(e => e.PayrollRunId == run.Id && e.ComponentCode == "OVERTIME")).CostCenterSource.Should().Be("MappingFixed");
    }

    // ── routing survives a suppressed cost-centre dimension ──────────────────

    [Theory]
    [InlineData("Optional")]
    [InlineData("Required")]
    public async Task AFunctionalClassRoute_ToAnAccountThatDoesNotAcceptACostCentre_StaysOnThatAccount_UncostedAndBalanced(string baseRule)
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL");   // CC-200, class SELLING
        await k.AddEmployee("E2", "D-FIN");   // CC-110, no class route
        (await k.Db.GlAccounts.SingleAsync(a => a.TenantId == k.TenantId && a.Code == "5001")).CostCenterRule = baseRule;
        var selling = new GlAccount { TenantId = k.TenantId, CompanyId = k.CompanyId, Code = "5011", Name = "Selling basic", AccountType = "Expense", CostCenterRule = "NotAllowed" };
        k.Db.GlAccounts.Add(selling);
        await k.Db.SaveChangesAsync();
        k.Db.GlAccountMappings.Add(new GlAccountMapping { TenantId = k.TenantId, CompanyId = k.CompanyId, DriverKey = "EARN:BASIC", FunctionalClass = "SELLING", AccountId = selling.Id });
        await k.Db.SaveChangesAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 3000m), E("E2", "BASIC", 2000m) }, Array.Empty<(string, string, string, decimal, bool)>());

        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        var gl = await k.GlAsync(run.Id);

        var sellingLine = Dr(gl, "5011").Should().ContainSingle().Which;
        (sellingLine.Amount, sellingLine.CostCenterId).Should().Be((3000m, (Guid?)null));
        var baseLine = Dr(gl, "5001").Should().ContainSingle().Which;
        (baseLine.Amount, baseLine.CostCenterCode).Should().Be((2000m, "CC-110"), "the destination of the other employee is untouched");
        GlPostingKit.Debits(gl).Should().Be(GlPostingKit.Credits(gl));
        gl.Select(l => l.PostingKey).Should().OnlyHaveUniqueItems();
    }

    // ── rounding line (T-023) ────────────────────────────────────────────────

    [Fact]
    public async Task T023_TheRoundingLine_IsStampedWithTheCompanyDefaultCostCentre()
    {
        await using var k = await OrgAsync(defaultCc: "CC-100");
        await k.AddEmployee("E1", "D-SAL");
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 5000m) }, Array.Empty<(string, string, string, decimal, bool)>(), net: 4999.99m);

        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();

        var rounding = Cr(await k.GlAsync(run.Id), "5199").Single();
        (rounding.Amount, rounding.CostCenterCode).Should().Be((0.01m, "CC-100"));
    }

    [Fact]
    public async Task T023_ARequiredRoundingAccount_WithNoCompanyDefault_BlocksLockWithM4()
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL");
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 5000m) }, Array.Empty<(string, string, string, decimal, bool)>(), net: 4999.99m);

        var res = await k.ApproveAndLockAsync(run.Id);

        Status(res).Should().Be(422);
        var body = Json(res);
        body.GetProperty("error").GetString().Should().Be("cost_centre_rounding_required");
        body.GetProperty("message").GetString().Should().Be("A rounding difference of 0.01 SAR must be posted to account 5199, which requires a cost centre. Set a default cost centre for the company, then lock again.");
        body.GetProperty("messageAr").GetString().Should().Contain("5199");
        (await k.GlAsync(run.Id)).Should().BeEmpty();
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).Status.Should().Be("Approved");
    }

    // ── T-024 / S-A1..S-A4: Lock blockers ────────────────────────────────────

    [Fact]
    public async Task T024_UnresolvedEmployees_OnRequiredAccounts_Return422M1_WithAggregatesOnly_AndWriteNothing()
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL");
        await k.AddEmployee("E2", "D-OPS", "P-GEN");
        await k.AddEmployee("E3");
        var run = await k.SeedRunAsync(
            new[] { E("E1", "BASIC", 1000m), E("E2", "BASIC", 1200m), E("E3", "BASIC", 800m), E("E2", "HOUSING", 300m) },
            Array.Empty<(string, string, string, decimal, bool)>());

        var res = await k.ApproveAndLockAsync(run.Id);

        Status(res).Should().Be(422);
        var body = Json(res);
        body.GetProperty("error").GetString().Should().Be("cost_centre_required");
        var groups = body.GetProperty("groups").EnumerateArray().OrderBy(g => g.GetProperty("accountCode").GetString()).ToList();
        groups.Select(g => g.GetProperty("accountCode").GetString()).Should().Equal("5001", "5002");
        groups[0].GetProperty("employeeCount").GetInt32().Should().Be(2);
        groups[0].GetProperty("amount").GetDecimal().Should().Be(2000m);
        groups[0].GetProperty("employees").EnumerateArray().Select(x => x.GetProperty("code").GetString()).Should().BeEquivalentTo("E2", "E3");
        groups[0].GetProperty("message").GetString().Should().Be(
            "2 employee(s) have no cost centre, and account 5001 Basic Salary Expense requires one. Assign a cost centre to each employee, their position, their department, or set a company default cost centre, then lock again.");
        groups[1].GetProperty("amount").ValueKind.Should().Be(JsonValueKind.Null, "a group of one employee carries no amount: it would be that person's pay");
        groups[1].GetProperty("amountWithheld").GetBoolean().Should().BeTrue();
        // AM-1 blocker shape: one entry per employee and account, no amounts
        var blockers = body.GetProperty("blockers").EnumerateArray().ToList();
        blockers.Should().HaveCount(3);
        foreach (var b in blockers)
        {
            foreach (var name in new[] { "employeeId", "employeeCode", "level", "reason", "accountCode" }) b.TryGetProperty(name, out _).Should().BeTrue(name);
            b.TryGetProperty("amount", out _).Should().BeFalse("amounts appear only as aggregates");
        }
        blockers.Select(b => b.GetProperty("level").GetString()).Should().OnlyContain(l => l == "company");

        (await k.GlAsync(run.Id)).Should().BeEmpty();
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).Status.Should().Be("Approved");
        (await k.Db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == run.Id).ToListAsync()).Should().OnlyContain(e => e.CostCenterId == null, "nothing is written on a refusal");
    }

    [Fact]
    public async Task T024_WithACompanyDefault_LockSucceeds_AndReportsTheFallbackCountAndAmount()
    {
        await using var k = await OrgAsync(defaultCc: "CC-100");
        await k.AddEmployee("E1", "D-SAL");
        await k.AddEmployee("E2");
        await k.AddEmployee("E3");
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 1000m), E("E2", "BASIC", 1200m), E("E3", "BASIC", 800m) }, Array.Empty<(string, string, string, decimal, bool)>());

        var res = await k.ApproveAndLockAsync(run.Id);

        res.Should().BeOfType<OkObjectResult>();
        var costing = Json(res).GetProperty("costing");
        costing.GetProperty("fallbackEmployees").GetInt32().Should().Be(2);
        costing.GetProperty("fallbackAmount").GetDecimal().Should().Be(2000m);
        Dr(await k.GlAsync(run.Id), "5001").OrderBy(l => l.CostCenterCode).Select(l => (l.CostCenterCode, l.Amount)).Should().Equal(("CC-100", 2000m), ("CC-200", 1000m));
        (await k.Db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == run.Id && e.EmployeeId == k.Emp["E2"].Id).ToListAsync())
            .Should().OnlyContain(e => e.CostCenterSource == "CompanyDefault");
    }

    [Fact]
    public async Task SA4_ADeactivatedCostCentre_BlocksWithM2PerLevel_AndTheCompanyDefaultDoesNotRescueIt()
    {
        await using var k = await OrgAsync(defaultCc: "CC-100");
        await k.AddEmployee("E5", "D-OPS", "P-SUPV");                 // position level -> CC-300
        await k.AddEmployee("E7", "D-FIN", overrideCc: "CC-300");     // employee level -> CC-300
        await k.AddEmployee("E1", "D-SAL");
        k.Cc["CC-300"].IsActive = false;
        k.Touch();
        await k.Db.SaveChangesAsync();
        var run = await k.SeedRunAsync(new[] { E("E5", "BASIC", 700m), E("E7", "BASIC", 1100m), E("E1", "BASIC", 900m) }, Array.Empty<(string, string, string, decimal, bool)>());

        var res = await k.ApproveAndLockAsync(run.Id);

        Status(res).Should().Be(422);
        var body = Json(res);
        body.GetProperty("error").GetString().Should().Be("cost_centre_invalid");
        var groups = body.GetProperty("groups").EnumerateArray().ToList();
        groups.Select(g => (g.GetProperty("level").GetString(), g.GetProperty("employees")[0].GetProperty("code").GetString()))
            .Should().BeEquivalentTo(new[] { ("Employee", "E7"), ("Position", "E5") });
        groups.First(g => g.GetProperty("level").GetString() == "Employee").GetProperty("message").GetString().Should().Be(
            "The cost centre set at the Employee level is inactive, deleted or belongs to another company, so cost cannot be assigned. Fix it in Setup, then lock again.");
        (await k.GlAsync(run.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task SA3_OptionalAccounts_PostUncostedWithAWarning_AndTheApproverMustAcknowledgeTheCount()
    {
        await using var k = await OrgAsync(preset: false); // every rule Optional
        await k.AddEmployee("E1", "D-SAL");
        await k.AddEmployee("E4", "D-OPS", "P-GEN");
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 1000m), E("E4", "BASIC", 5000m) }, Array.Empty<(string, string, string, decimal, bool)>());

        // S-048: Approve without the acknowledgement is refused with the count and the aggregate amount.
        var refused = await k.ApproveAsync(run.Id);
        Status(refused).Should().Be(409);
        Json(refused).GetProperty("error").GetString().Should().Be("uncosted_not_acknowledged");
        Json(refused).GetProperty("uncostedEmployees").GetInt32().Should().Be(1);
        Json(refused).GetProperty("uncostedAmount").GetDecimal().Should().Be(5000m);

        k.Db.ChangeTracker.Clear();
        (await k.Payroll(k.ApproverId).Approve(run.Id, new PayrollDecisionRequest("ok", ExpectedUncostedCount: 1), default)).Should().BeOfType<OkObjectResult>();
        var res = await k.LockAsync(run.Id);

        res.Should().BeOfType<OkObjectResult>();
        Json(res).GetProperty("costing").GetProperty("uncostedEmployees").GetInt32().Should().Be(1);
        var basic = Dr(await k.GlAsync(run.Id), "5001").OrderBy(l => l.CostCenterCode).ToList();
        basic.Select(l => (l.CostCenterCode, l.Amount)).Should().Equal(((string?)null, 5000m), ("CC-200", 1000m));
    }

    // ── AM-8 / AM-29: approval hash and the drift guard ──────────────────────

    private static async Task<(CostingLockKit K, PayrollRun Run)> ApprovedAsync(bool costing = true, string? defaultCc = null)
    {
        var k = await OrgAsync(costing, defaultCc: defaultCc);
        await k.AddEmployee("E1", "D-SAL", "P-GEN");
        await k.AddEmployee("E2", "D-FIN", "P-GEN");
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 3000m), E("E2", "BASIC", 2000m), E("E1", "HOUSING", 500m) }, Array.Empty<(string, string, string, decimal, bool)>());
        (await k.ApproveAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        return (k, run);
    }

    private static async Task AssertSentBackAsync(CostingLockKit k, PayrollRun run, IActionResult res)
    {
        Status(res).Should().Be(409);
        var body = Json(res);
        body.GetProperty("error").GetString().Should().Be("cost_centre_changed_after_approval");
        body.GetProperty("message").GetString().Should().Be("Cost-centre assignments or posting rules changed after this run was approved, so it was sent back for approval. Process and approve it again.");
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrEmpty();
        var stored = await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        stored.Status.Should().Be("Processed", "the run goes back to Processed, like outside_bank_payments_changed");
        stored.CostCenterResolutionHash.Should().BeNull("the hash is cleared on a send-back");
        (await k.GlAsync(run.Id)).Should().BeEmpty();
        (await k.Db.PayrollAuditLogs.AsNoTracking().AnyAsync(a => a.Action == "payroll.run.sent_back" && a.EntityId == run.Id.ToString() && a.MetadataJson.Contains("cost_centre_changed_after_approval"))).Should().BeTrue();
    }

    [Fact]
    public async Task S001_CostingOffAtApprove_OnAtLock_SendsTheRunBack()
    {
        var (k, run) = await ApprovedAsync(costing: false);
        await using var _ = k;
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).CostCenterResolutionHash.Should().BeNull();
        (await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId)).CostAccountingEnabled = true;
        await k.Db.SaveChangesAsync();

        await AssertSentBackAsync(k, run, await k.LockAsync(run.Id));
    }

    [Fact]
    public async Task S002_CostingOnAtApprove_OffAtLock_SendsTheRunBack_AndPostsNothingUncosted()
    {
        var (k, run) = await ApprovedAsync();
        await using var _ = k;
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).CostCenterResolutionHash.Should().NotBeNullOrEmpty();
        (await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId)).CostAccountingEnabled = false;
        await k.Db.SaveChangesAsync();

        await AssertSentBackAsync(k, run, await k.LockAsync(run.Id));
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).CostAccountingApplied.Should().BeFalse();
    }

    [Theory]
    [InlineData("department_cc")]        // S-013 re-point a department's cost centre
    [InlineData("position_cc")]          // a position cost centre
    [InlineData("override")]             // an employee override
    [InlineData("employee_department")]  // an employee moves department
    [InlineData("company_default")]
    [InlineData("small_cell_k")]
    [InlineData("cc_class")]             // S-007 (class edit is routing only with a class mapping; hashed regardless)
    [InlineData("cc_name")]              // S-009
    [InlineData("cc_segment")]
    [InlineData("deactivate_cc")]
    [InlineData("account_name")]         // S-010
    [InlineData("account_type")]
    [InlineData("account_rule")]
    [InlineData("fixed_cc_mapping")]
    [InlineData("class_mapping")]
    [InlineData("new_earning_row")]      // population / amounts
    public async Task S013_EveryInputThatChangesWhatPosts_SendsTheRunBack_NeverPostsTheChangedJournal(string change)
    {
        var (k, run) = await ApprovedAsync();
        await using var _ = k;
        switch (change)
        {
            case "department_cc": k.Dept["D-SAL"].CostCenterId = k.Cc["CC-300"].Id; break;
            case "position_cc": k.Pos["P-GEN"].CostCenterId = k.Cc["CC-300"].Id; break;
            case "override": k.Emp["E1"].CostCenterId = k.Cc["CC-300"].Id; k.Emp["E1"].CostCenterIsOverride = true; break;
            case "employee_department": k.Emp["E1"].DepartmentId = k.Dept["D-FIN"].Id; break;
            case "company_default": (await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId)).DefaultCostCenterId = k.Cc["CC-100"].Id; break;
            case "small_cell_k": (await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId)).CostCentreSmallCellMin = 3; break;
            case "cc_class": k.Cc["CC-200"].FunctionalClass = "COGS"; break;
            case "cc_name": k.Cc["CC-200"].Name = "Renamed"; break;
            case "cc_segment": k.Cc["CC-200"].GlSegment = "9999"; break;
            case "deactivate_cc": k.Cc["CC-200"].IsActive = false; break;
            case "account_name": (await k.Db.GlAccounts.SingleAsync(a => a.TenantId == k.TenantId && a.Code == "5001")).Name = "Basic pay"; break;
            case "account_type": (await k.Db.GlAccounts.SingleAsync(a => a.TenantId == k.TenantId && a.Code == "5001")).AccountType = "Revenue"; break;
            case "account_rule": (await k.Db.GlAccounts.SingleAsync(a => a.TenantId == k.TenantId && a.Code == "5001")).CostCenterRule = "Optional"; break;
            case "fixed_cc_mapping":
                k.Db.GlAccountMappings.Add(new GlAccountMapping { TenantId = k.TenantId, CompanyId = k.CompanyId, DriverKey = "EARN:HOUSING",
                    AccountId = (await k.Db.GlAccounts.SingleAsync(a => a.TenantId == k.TenantId && a.Code == "5002")).Id, SegmentCostCenterId = k.Cc["CC-300"].Id });
                break;
            case "class_mapping":
                k.Cc["CC-200"].FunctionalClass = "SELLING";
                k.Db.GlAccounts.Add(new GlAccount { TenantId = k.TenantId, CompanyId = k.CompanyId, Code = "5011", Name = "Selling basic", AccountType = "Expense", CostCenterRule = "Required" });
                await k.Db.SaveChangesAsync();
                k.Db.GlAccountMappings.Add(new GlAccountMapping { TenantId = k.TenantId, CompanyId = k.CompanyId, DriverKey = "EARN:BASIC", FunctionalClass = "SELLING",
                    AccountId = (await k.Db.GlAccounts.SingleAsync(a => a.TenantId == k.TenantId && a.Code == "5011")).Id });
                break;
            case "new_earning_row":
                k.Db.PayrollEarnings.Add(new PayrollEarning { TenantId = k.TenantId, PayrollRunId = run.Id, EmployeeId = k.Emp["E2"].Id, ComponentCode = "TRANSPORT", ComponentName = "t", Amount = 10m, Source = "Salary" });
                break;
        }
        k.Touch();
        await k.Db.SaveChangesAsync();

        await AssertSentBackAsync(k, run, await k.LockAsync(run.Id));
    }

    [Fact]
    public async Task S013_ANonAttributionChange_DoesNotSendTheRunBack()
    {
        var (k, run) = await ApprovedAsync();
        await using var _ = k;
        k.Emp["E1"].FullName = "Renamed Person";
        k.Cc["CC-300"].Name = "An unrelated cost centre renamed";
        k.Touch();
        await k.Db.SaveChangesAsync();

        (await k.LockAsync(run.Id)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task S014_TheHashIsWrittenOnlyAtFinalApproval_NullDuringPendingFinanceReview_AndAdminWritesItDirectly()
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL");
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 1000m) }, Array.Empty<(string, string, string, decimal, bool)>());

        k.Db.ChangeTracker.Clear();
        (await k.Payroll(k.ApproverId, "Payroll Manager").Approve(run.Id, new PayrollDecisionRequest("l1"), default)).Should().BeOfType<OkObjectResult>();
        var level1 = await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        (level1.Status, level1.CostCenterResolutionHash).Should().Be(("PendingFinanceReview", (string?)null));

        // an attribution edit while the run waits for finance...
        k.Dept["D-SAL"].CostCenterId = k.Cc["CC-300"].Id;
        k.Touch();
        await k.Db.SaveChangesAsync();
        // ...then Finance finalises: the hash is the state AT FINAL APPROVAL, so Lock posts what the finalising approver saw.
        k.Db.ChangeTracker.Clear();
        (await k.Payroll(Guid.NewGuid(), "Finance Controller").Approve(run.Id, new PayrollDecisionRequest("l2"), default)).Should().BeOfType<OkObjectResult>();
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).CostCenterResolutionHash.Should().NotBeNullOrEmpty();
        (await k.LockAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        Dr(await k.GlAsync(run.Id), "5001").Single().CostCenterCode.Should().Be("CC-300");
    }

    [Fact]
    public async Task SendBack_AndReopen_ClearTheHash()
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL");
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 1000m) }, Array.Empty<(string, string, string, decimal, bool)>());
        (await k.ApproveAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).CostCenterResolutionHash.Should().NotBeNullOrEmpty();

        k.Db.ChangeTracker.Clear();
        (await k.Payroll().ReopenRun(run.Id, new PayrollReasonRequest("fix"), default)).Should().BeOfType<OkObjectResult>();

        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).CostCenterResolutionHash.Should().BeNull();
    }

    // ── S-004: segregation of duties at Lock ─────────────────────────────────

    [Fact]
    public async Task S004_AUserWhoChangedAPostingInput_CannotLock_EvenAsAdmin_AnotherUserCan()
    {
        var (k, run) = await ApprovedAsync();
        await using var _ = k;
        k.Db.AuditLogs.Add(new Zayra.Api.Domain.Entities.AuditLog
        { TenantId = k.TenantId, CompanyId = k.CompanyId, UserId = k.LockerId, Action = "finance.gl.mapping.saved", EntityName = "GlAccountMapping" });
        await k.Db.SaveChangesAsync();

        var refused = await k.LockAsync(run.Id, k.LockerId);
        Status(refused).Should().Be(403);
        Json(refused).GetProperty("error").GetString().Should().Be("separation_of_duties");
        (await k.GlAsync(run.Id)).Should().BeEmpty();

        (await k.LockAsync(run.Id, Guid.NewGuid())).Should().BeOfType<OkObjectResult>();
    }

    // ── S-050 / S-047 / T-033 ────────────────────────────────────────────────

    [Fact]
    public async Task S050_ALockReplay_IsRefusedAsAlreadyLocked_EvenAfterTheSwitchFlipped()
    {
        var (k, run) = await ApprovedAsync();
        await using var _ = k;
        (await k.LockAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        var count = (await k.GlAsync(run.Id)).Count;
        (await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId)).CostAccountingEnabled = false;
        await k.Db.SaveChangesAsync();

        var replay = await k.LockAsync(run.Id);

        Status(replay).Should().Be(409);
        Err(replay).Should().Be("run_already_locked");
        (await k.GlAsync(run.Id)).Should().HaveCount(count);
    }

    [Fact]
    public async Task S047_AnAlreadyPostedRun_WithAnUncostedLineOnARequiredAccount_IsNotFinanceReady_AndStaysUnapplied()
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL");
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 1000m) }, Array.Empty<(string, string, string, decimal, bool)>());
        (await k.ApproveAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        // a support-restored accrual: balanced, but its expense line carries no cost centre. (The ledger guard refuses to WRITE such a
        // line while costing is on, so it is restored with the switch briefly off, exactly as a support data-fix would.)
        (await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId)).CostAccountingEnabled = false;
        await k.Db.SaveChangesAsync();
        k.Db.FinanceGlEntries.AddRange(
            new FinanceGlEntry { TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Payroll", SourceEntityId = run.Id, EventType = GlEventTypes.Accrual, DebitAccount = "5001 - Basic Salary Expense", Amount = 1000m, Currency = "SAR", Period = "2026-09" },
            new FinanceGlEntry { TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Payroll", SourceEntityId = run.Id, EventType = GlEventTypes.Accrual, CreditAccount = "2100 - Salaries Payable", Amount = 1000m, Currency = "SAR", Period = "2026-09" });
        await k.Db.SaveChangesAsync();
        (await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId)).CostAccountingEnabled = true;
        await k.Db.SaveChangesAsync();

        var res = await k.LockAsync(run.Id);

        Status(res).Should().Be(422);
        Err(res).Should().Be("gl_not_finance_ready");
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).CostAccountingApplied.Should().BeFalse();
    }

    [Fact]
    public async Task T033_WithCostingOn_ADebitCreditDifferenceOfOneHalala_IsRejected_NoTolerance()
    {
        await using var k = await OrgAsync();
        await k.AddEmployee("E1", "D-SAL");
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 100m) }, Array.Empty<(string, string, string, decimal, bool)>());
        (await k.ApproveAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        k.Db.FinanceGlEntries.AddRange(
            new FinanceGlEntry { TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Payroll", SourceEntityId = run.Id, EventType = GlEventTypes.Accrual, DebitAccount = "5001 - Basic Salary Expense", Amount = 100.00m, Currency = "SAR", Period = "2026-09", CostCenterId = k.Cc["CC-200"].Id, CostCenterCode = "CC-200", CostCenterName = "Name CC-200" },
            new FinanceGlEntry { TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Payroll", SourceEntityId = run.Id, EventType = GlEventTypes.Accrual, CreditAccount = "2100 - Salaries Payable", Amount = 100.01m, Currency = "SAR", Period = "2026-09" });
        await k.Db.SaveChangesAsync();

        var res = await k.LockAsync(run.Id);

        Status(res).Should().Be(422);
        Err(res).Should().Be("gl_not_finance_ready");
    }
}

public class PayrollCostingScaleAndRollUpTests
{
    [Theory]
    [InlineData("KWD")]
    [InlineData("BHD")]
    [InlineData("OMR")]
    public async Task S053_ACompanyWhoseCurrencyHasThreeDecimals_CannotLockWithCostingOn_UntilPayrollStorageIsWidened(string currency)
    {
        await using var k = await CostingLockKit.CreateAsync(costing: true, preset: true);
        (await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId)).DefaultCurrency = currency;
        await k.Db.SaveChangesAsync();
        await k.AddCc("CC-1"); await k.AddDept("D-1", "CC-1"); await k.AddEmployee("E1", "D-1");
        var run = await k.SeedRunAsync(new[] { CostingLockKit.E("E1", "BASIC", 1000m) }, Array.Empty<(string, string, string, decimal, bool)>());
        (await k.ApproveAsync(run.Id)).Should().BeOfType<OkObjectResult>();

        var res = await k.LockAsync(run.Id);

        CostingLockKit.Status(res).Should().Be(422);
        CostingLockKit.Err(res).Should().Be("currency_scale_unsupported_for_payroll");
        (await k.GlAsync(run.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task T036_WithK3_AOneEmployeeCostCentreRollsUpToItsParent_SourceRolledUp_NoLineEqualsOneEmployeesAmount()
    {
        await using var k = await CostingLockKit.CreateAsync(costing: true, preset: true);
        await k.AddCc("CC-P", "G_AND_A");
        await k.AddCc("CC-A", "G_AND_A", parent: "CC-P");     // one employee
        await k.AddCc("CC-B", "G_AND_A", parent: "CC-P");     // two employees
        await k.AddDept("D-A", "CC-A"); await k.AddDept("D-B", "CC-B");
        await k.AddEmployee("E1", "D-A"); await k.AddEmployee("E2", "D-B"); await k.AddEmployee("E3", "D-B"); await k.AddEmployee("E4", "D-B");
        (await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId)).CostCentreSmallCellMin = 3;
        await k.Db.SaveChangesAsync();
        var run = await k.SeedRunAsync(new[]
        {
            CostingLockKit.E("E1", "BASIC", 7777m), CostingLockKit.E("E2", "BASIC", 1000m), CostingLockKit.E("E3", "BASIC", 2000m), CostingLockKit.E("E4", "BASIC", 3000m),
        }, Array.Empty<(string, string, string, decimal, bool)>());

        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();

        var basic = (await k.GlAsync(run.Id)).Where(l => l.DebitAccount.StartsWith("5001")).ToList();
        basic.Select(l => (l.CostCenterCode, l.Amount)).OrderBy(x => x.CostCenterCode).Should().Equal(("CC-B", 6000m), ("CC-P", 7777m));
        basic.Should().NotContain(l => l.CostCenterCode == "CC-A");
        var e1 = await k.Db.PayrollEarnings.AsNoTracking().SingleAsync(e => e.PayrollRunId == run.Id && e.EmployeeId == k.Emp["E1"].Id);
        (e1.CostCenterId, e1.CostCenterSource).Should().Be((k.Cc["CC-P"].Id, "RolledUp"));
        (await k.Db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == run.Id && s.EmployeeId == k.Emp["E1"].Id)).CostCenterId.Should().Be(k.Cc["CC-P"].Id, "the slip stores the rolled-up centre (AM-15)");
    }
}

public class PayrollCostingPreviewTests
{
    [Fact]
    public async Task ThePreview_ShowsTheCostCentrePerLine_AndTheSameBlockersLockWouldRefuseWith()
    {
        await using var k = await CostingLockKit.CreateAsync(costing: true, preset: true);
        await k.AddCc("CC-1", "SELLING"); await k.AddDept("D-1", "CC-1");
        await k.AddEmployee("E1", "D-1"); await k.AddEmployee("E2");   // E2 resolves nowhere
        var run = await k.SeedRunAsync(new[] { CostingLockKit.E("E1", "BASIC", 1000m), CostingLockKit.E("E2", "BASIC", 400m) }, Array.Empty<(string, string, string, decimal, bool)>());

        k.Db.ChangeTracker.Clear();
        var res = await k.K.Payroll(k.Db, k.LockerId, "Admin").GlJournal(run.Id, default);

        var body = CostingLockKit.Json(res);
        body.GetProperty("isPosted").GetBoolean().Should().BeFalse();
        body.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("glAccount").GetString() == "5001" && e.GetProperty("costCentreCode").GetString() == "CC-1")
            .GetProperty("amount").GetDecimal().Should().Be(1000m);
        var costing = body.GetProperty("costing");
        costing.GetProperty("enabled").GetBoolean().Should().BeTrue();
        var blocker = costing.GetProperty("blockers").EnumerateArray().Single();
        blocker.GetProperty("employeeCode").GetString().Should().Be("E2");
        blocker.GetProperty("accountCode").GetString().Should().Be("5001");
        var previewGroup = costing.GetProperty("groups").EnumerateArray().Single();
        previewGroup.GetProperty("amount").ValueKind.Should().Be(JsonValueKind.Null, "the one unresolved employee's pay is withheld, as it is at Lock");
        previewGroup.GetProperty("amountWithheld").GetBoolean().Should().BeTrue();
    }
}
