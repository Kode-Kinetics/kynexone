using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Authorization;
using static Zayra.Api.Tests.CostingLockKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting v1, WP-C3 (contract 2.10, AM-17, D-7): <c>PayrollController.CostCenterAllocation</c> reads what Lock STAMPED and the
/// journal-line snapshot names, never the live employee or cost-centre rows. T-038 (legacy / costed / preview), S-035 (small cells in the
/// report), S-038 (permission and company scope; snapshot names), D-7 (the retro note).
/// </summary>
public class CostCenterAllocationReportTests
{
    private static readonly string[] NoEarnings = [];

    private static async Task<CostingLockKit> OrgAsync(bool costing = true, short k = 0)
    {
        var kit = await CostingLockKit.CreateAsync(costing: costing, preset: costing);
        await kit.AddCc("CC-1", "SELLING");
        await kit.AddCc("CC-2", "G_AND_A");
        await kit.AddDept("D-1", "CC-1");
        await kit.AddDept("D-2", "CC-2");
        await kit.AddEmployee("E1", "D-1");
        await kit.AddEmployee("E2", "D-2");
        if (k > 0) (await kit.Db.Companies.SingleAsync(c => c.Id == kit.CompanyId)).CostCentreSmallCellMin = k;
        await kit.Db.SaveChangesAsync();
        return kit;
    }

    private static async Task<JsonElement> ReportAsync(CostingLockKit k, Guid runId)
    {
        k.Db.ChangeTracker.Clear();
        var res = await k.Payroll().CostCenterAllocation(runId, default);
        res.Should().BeOfType<OkObjectResult>(Json(res).ToString());
        return Json(res);
    }

    private static List<JsonElement> Buckets(JsonElement report) => report.GetProperty("allocations").EnumerateArray().ToList();

    // ── T-038 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T038_ALegacyLockedRun_IsOneNotRecordedBucket_AndNeverReadsTheLiveEmployeeRow()
    {
        await using var k = await OrgAsync(costing: false);
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 1000m), E("E2", "BASIC", 3000m) }, Array.Empty<(string, string, string, decimal, bool)>());
        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).CostAccountingApplied.Should().BeFalse();
        // the live employee rows now say something else entirely
        await k.Db.Employees.Where(e => e.TenantId == k.TenantId).ExecuteUpdateAsync(s => s.SetProperty(e => e.CostCenterId, k.Cc["CC-1"].Id));

        var report = await ReportAsync(k, run.Id);

        report.GetProperty("basis").GetString().Should().Be("NotRecorded");
        var buckets = Buckets(report);
        buckets.Should().ContainSingle("a run locked before cost centres has no recorded attribution");
        buckets[0].GetProperty("costCenterName").GetString().Should().Be("Not recorded (run locked before cost centres)");
        buckets[0].GetProperty("costCenterId").ValueKind.Should().Be(JsonValueKind.Null);
        (buckets[0].GetProperty("employeeCount").GetInt32(), buckets[0].GetProperty("grossSalary").GetDecimal()).Should().Be((2, 4000m));
        report.GetProperty("totalGross").GetDecimal().Should().Be(4000m);
    }

    [Fact]
    public async Task T038_ACostedRun_MatchesTheStamps_AndSurvivesARenameAndATransferAfterLock()
    {
        await using var k = await OrgAsync();
        var run = await k.SeedRunAsync(
            new[] { E("E1", "BASIC", 1000m), E("E2", "BASIC", 3000m) },
            new[] { D("E2", "ATTENDANCE", "Attendance", 100m), D("E1", "GOSI-OH-ER", "Statutory", 20m, true) });
        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();

        var before = await ReportAsync(k, run.Id);
        before.GetProperty("basis").GetString().Should().Be("Stamped");
        var cc1 = Buckets(before).Single(b => b.GetProperty("costCenterCode").GetString() == "CC-1");
        (cc1.GetProperty("employeeCount").GetInt32(), cc1.GetProperty("grossSalary").GetDecimal(), cc1.GetProperty("employerCost").GetDecimal(), cc1.GetProperty("totalCost").GetDecimal())
            .Should().Be((1, 1000m, 20m, 1020m));
        var cc2 = Buckets(before).Single(b => b.GetProperty("costCenterCode").GetString() == "CC-2");
        (cc2.GetProperty("grossSalary").GetDecimal(), cc2.GetProperty("contraDeductions").GetDecimal()).Should().Be((3000m, 100m));
        cc1.GetProperty("costCenterName").GetString().Should().Be("Name CC-1");

        // After Lock: rename a cost centre, move an employee, change a department. None of it may touch the report.
        await k.Db.CostCenters.Where(c => c.Id == k.Cc["CC-1"].Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, "Renamed later"));
        await k.Db.Employees.Where(e => e.Id == k.Emp["E1"].Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.CostCenterId, k.Cc["CC-2"].Id).SetProperty(e => e.CostCenterIsOverride, true));
        await k.Db.Departments.Where(d => d.Id == k.Dept["D-1"].Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.CostCenterId, k.Cc["CC-2"].Id));

        var after = await ReportAsync(k, run.Id);

        after.GetRawText().Should().Be(before.GetRawText(), "a locked, costed run's report is its stamps and snapshot names, whatever master data does later");
    }

    [Fact]
    public async Task T038_ANonLockedRun_IsLabelledPreview_AndResolvesTheWayLockWould()
    {
        await using var k = await OrgAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 1000m), E("E2", "BASIC", 3000m) }, Array.Empty<(string, string, string, decimal, bool)>());

        var report = await ReportAsync(k, run.Id);

        report.GetProperty("basis").GetString().Should().Be("Preview");
        report.GetProperty("label").GetString().Should().Be("Preview");
        Buckets(report).Select(b => (b.GetProperty("costCenterCode").GetString(), b.GetProperty("grossSalary").GetDecimal()))
            .OrderBy(x => x.Item1).Should().Equal(("CC-1", 1000m), ("CC-2", 3000m));
        report.TryGetProperty("reconciles", out var rec).Should().BeTrue();
        rec.ValueKind.Should().Be(JsonValueKind.Null, "a preview has no journal to reconcile with");
    }

    [Fact]
    public async Task D7_TheReportStatesThatRetroPayAndBackDatedTransfersPostToTheCostCentreCurrentAtLock()
    {
        await using var k = await OrgAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 1000m) }, Array.Empty<(string, string, string, decimal, bool)>());

        var report = await ReportAsync(k, run.Id);

        report.GetProperty("retroNote").GetString().Should().Contain("retro").And.Contain("current at Lock");
        report.GetProperty("retroNoteAr").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ── S-035: the report never reveals a single employee ────────────────────

    [Fact]
    public async Task S035_WithK3_WhenEveryoneRollsIntoOneCentre_NoBucketIsASingleEmployee()
    {
        await using var k = await CostingLockKit.CreateAsync(costing: true, preset: true);
        await k.AddCc("CC-P", "G_AND_A"); await k.AddCc("CC-A", "G_AND_A", parent: "CC-P"); await k.AddCc("CC-B", "G_AND_A", parent: "CC-P");
        await k.AddDept("D-A", "CC-A"); await k.AddDept("D-B", "CC-B"); await k.AddDept("D-P", "CC-P");
        await k.AddEmployee("E1", "D-A"); await k.AddEmployee("E2", "D-B"); await k.AddEmployee("E3", "D-B"); await k.AddEmployee("E4", "D-P");
        (await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId)).CostCentreSmallCellMin = 3;
        await k.Db.SaveChangesAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 7777m), E("E2", "BASIC", 1000m), E("E3", "BASIC", 2000m), E("E4", "BASIC", 3000m) },
            Array.Empty<(string, string, string, decimal, bool)>());
        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();

        var report = await ReportAsync(k, run.Id);

        Buckets(report).Should().OnlyContain(b => b.GetProperty("employeeCount").GetInt32() >= 3, "the slips carry the rolled-up centre, so the report cannot show a one-person cell");
        Buckets(report).Should().OnlyContain(b => !b.GetProperty("smallCell").GetBoolean());
    }

    [Fact]
    public async Task S035_WhenARolledUpCentreStillHoldsFewerThanKPeople_TheBucketIsFlaggedSmallCell()
    {
        await using var k = await CostingLockKit.CreateAsync(costing: true, preset: true);
        await k.AddCc("CC-P", "G_AND_A"); await k.AddCc("CC-A", "G_AND_A", parent: "CC-P"); await k.AddCc("CC-B", "G_AND_A", parent: "CC-P");
        await k.AddDept("D-A", "CC-A"); await k.AddDept("D-B", "CC-B");
        await k.AddEmployee("E1", "D-A"); await k.AddEmployee("E2", "D-B"); await k.AddEmployee("E3", "D-B"); await k.AddEmployee("E4", "D-B");
        (await k.Db.Companies.SingleAsync(c => c.Id == k.CompanyId)).CostCentreSmallCellMin = 3;
        await k.Db.SaveChangesAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 7777m), E("E2", "BASIC", 1000m), E("E3", "BASIC", 2000m), E("E4", "BASIC", 3000m) },
            Array.Empty<(string, string, string, decimal, bool)>());
        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();

        var report = await ReportAsync(k, run.Id);

        // E1's cost centre rolls to CC-P (the subtree holds four people) but CC-P itself holds only E1: the report says so, so a screen can mask it.
        var parent = Buckets(report).Single(b => b.GetProperty("costCenterCode").GetString() == "CC-P");
        (parent.GetProperty("employeeCount").GetInt32(), parent.GetProperty("smallCell").GetBoolean()).Should().Be((1, true));
        Buckets(report).Single(b => b.GetProperty("costCenterCode").GetString() == "CC-B").GetProperty("smallCell").GetBoolean().Should().BeFalse();
    }

    // ── S-038: permission and company scope ──────────────────────────────────

    [Fact]
    public void S038_TheReportIsGatedByFinanceGlRead_NotByARoleString()
    {
        var method = typeof(PayrollController).GetMethod(nameof(PayrollController.CostCenterAllocation))!;
        var gates = method.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        gates.OfType<HasPermissionAttribute>().Should().ContainSingle().Which.Permissions.Should().Equal("finance.gl.read");
        gates.Where(g => !string.IsNullOrEmpty(g.Roles)).Should().BeEmpty("a Payroll Officer role string is not the control (access F11)");
    }

    [Fact]
    public async Task S038_ARunOfACompanyTheCallerCannotSee_IsNotFound()
    {
        await using var k = await OrgAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 1000m) }, Array.Empty<(string, string, string, decimal, bool)>());
        var other = Guid.NewGuid();
        var ctrl = k.Payroll();
        ctrl.ControllerContext.HttpContext!.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            ctrl.ControllerContext.HttpContext.User.Claims.Concat(new[] { new System.Security.Claims.Claim("entity_scope", CostAccountingKit.CompanyScope(other)) }), "test"));

        (await ctrl.CostCenterAllocation(run.Id, default)).Should().BeOfType<NotFoundResult>();
    }
}
