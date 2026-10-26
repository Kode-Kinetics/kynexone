using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostingLockKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting v1, WP-C2 reversal slice on real PostgreSQL WITH migration M2's ledger triggers (the shared fixture applies M2's DDL):
/// T-028, T-029, T-030, S-050 and the WP-E findings that belong to payroll: every contra copies the cost centre id and its three
/// snapshots verbatim, every is_reversed flag is written with its contra in the SAME SaveChanges (the triggers check at COMMIT),
/// and the hand ERP status endpoint no longer fails on a line that has already left the draft state.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class PayrollCostingReversalPostgresTests
{
    private readonly PostgresFixture _fx;
    public PayrollCostingReversalPostgresTests(PostgresFixture fx) => _fx = fx;

    private async Task<CostingLockKit> KitAsync()
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var k = await CostingLockKit.CreateAsync(costing: true, preset: true, _fx.CreateDb, tenantId);
        await k.AddCc("CC-100", "G_AND_A");
        await k.AddCc("CC-110", "G_AND_A", parent: "CC-100", segment: "4110");
        await k.AddCc("CC-200", "SELLING");
        await k.AddDept("D-FIN", "CC-110");
        await k.AddDept("D-SAL", "CC-200");
        await k.AddEmployee("E1", "D-SAL");
        await k.AddEmployee("E2", "D-FIN");
        return k;
    }

    private static async Task<PayrollRun> LockedRunAsync(CostingLockKit k, int month = 9)
    {
        var run = await k.SeedRunAsync(
            new[] { E("E1", "BASIC", 3000m), E("E2", "BASIC", 2000m), E("E1", "HOUSING", 750m) },
            new[] { D("E1", "GOSI-OH-ER", "Statutory", 100m, true), D("E2", "ATTENDANCE", "Attendance", 40m) }, month: month);
        (await k.ApproveAndLockAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        return run;
    }

    [Fact]
    public async Task T028_VoidingACostedRun_NetsEveryCostCentreToZero_AndEveryContraCopiesTheIdAndSnapshots()
    {
        await using var k = await KitAsync();
        var run = await LockedRunAsync(k);

        await using (var db = k.K.NewContext())
            (await new PayrollVoidService(db).VoidAsync(run.Id, k.TenantId, null, "test", "cost reversal", default)).IsVoided.Should().BeTrue();

        await using var read = k.K.NewContext();
        var gl = await k.K.RunGlAsync(run.Id, read);
        var originals = gl.Where(l => l.EventType == GlEventTypes.Accrual).ToList();
        var contras = gl.Where(l => l.ReversalOfEntryId != null).ToList();
        originals.Should().NotBeEmpty().And.OnlyContain(l => l.IsReversed);
        originals.Should().Contain(l => l.CostCenterId != null, "the run was costed");
        contras.Should().HaveCount(originals.Count);
        foreach (var o in originals)
        {
            var c = contras.Single(x => x.ReversalOfEntryId == o.Id);
            (c.CostCenterId, c.CostCenterCode, c.CostCenterName, c.CostCenterGlSegment).Should().Be((o.CostCenterId, o.CostCenterCode, o.CostCenterName, o.CostCenterGlSegment));
            c.PostingKey.Should().Be(PayrollCostStamper.ReversalKey(o.Id));
        }
        // every (cost centre, account) nets to zero
        foreach (var g in gl.GroupBy(l => (l.CostCenterCode, Account: l.DebitAccount != "" ? l.DebitAccount : l.CreditAccount)))
            g.Sum(l => (l.DebitAccount != "" ? l.Amount : 0m) - (l.CreditAccount != "" ? l.Amount : 0m)).Should().Be(0m, g.Key.ToString());
    }

    [Fact]
    public async Task T029_TwoSimultaneousVoids_WriteOneReversalSet_AndASecondInsertOfTheSameKeyFailsAtTheDatabase()
    {
        await using var k = await KitAsync();
        var run = await LockedRunAsync(k);

        async Task<VoidRunResult> Void()
        {
            await using var db = k.K.NewContext();
            return await new PayrollVoidService(db).VoidAsync(run.Id, k.TenantId, null, "race", "x", default);
        }
        var results = await Task.WhenAll(Task.Run(Void), Task.Run(Void));

        results.Count(r => r.IsVoided).Should().Be(1);
        results.Count(r => r.IsAlreadyVoid).Should().Be(1);
        await using var read = k.K.NewContext();
        var gl = await k.K.RunGlAsync(run.Id, read);
        gl.Count(l => l.ReversalOfEntryId != null).Should().Be(gl.Count(l => l.EventType == GlEventTypes.Accrual));

        var original = gl.First(l => l.EventType == GlEventTypes.Accrual);
        await using var dup = k.K.NewContext();
        dup.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Payroll", SourceEntityId = run.Id, EventType = "X",
            DebitAccount = "1000 - Cash/Bank", Amount = 1m, Currency = "SAR", Period = "2026-09", PostingKey = original.PostingKey,
        });
        var failure = await Record.ExceptionAsync(() => dup.SaveChangesAsync());
        failure.Should().NotBeNull("(tenant_id, posting_key) is unique");
    }

    [Fact]
    public async Task T030_TwoSimultaneousLocks_PostOneSetOfLines()
    {
        await using var k = await KitAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 3000m), E("E2", "BASIC", 2000m) }, Array.Empty<(string, string, string, decimal, bool)>());
        (await k.ApproveAsync(run.Id)).Should().BeOfType<OkObjectResult>();

        async Task<IActionResult> Lock()
        {
            await using var db = k.K.NewContext();
            return await k.K.Payroll(db, Guid.NewGuid(), "Admin").Lock(run.Id, default);
        }
        var results = await Task.WhenAll(Task.Run(Lock), Task.Run(Lock));

        results.OfType<OkObjectResult>().Should().ContainSingle();
        results.OfType<ConflictObjectResult>().Should().ContainSingle();
        await using var read = k.K.NewContext();
        var gl = await k.K.RunGlAsync(run.Id, read);
        gl.Select(l => l.PostingKey).Should().OnlyHaveUniqueItems().And.NotContainNulls();
        GlPostingKit.Debits(gl).Should().Be(GlPostingKit.Credits(gl));
        gl.Count(l => l.DebitAccount.StartsWith("5001")).Should().Be(2);
    }

    // ── BuildContraGl (settle / remit / outside-payment reversals) ────────────

    [Fact]
    public void BuildContraGl_CopiesTheCostCentreIdAndSnapshotsVerbatim_AndKeysTheContra()
    {
        var original = new FinanceGlEntry
        {
            TenantId = Guid.NewGuid(), CompanyId = Guid.NewGuid(), SourceEntityRef = "2026-09", DebitAccount = "5001 - Basic Salary Expense",
            Amount = 10m, Currency = "SAR", Period = "2026-09", Description = "d",
            CostCenterId = Guid.NewGuid(), CostCenterCode = "CC-1", CostCenterName = "One", CostCenterGlSegment = "S1",
        };
        var m = typeof(PayrollController).GetMethod("BuildContraGl", BindingFlags.NonPublic | BindingFlags.Static)!;

        var contra = ((List<FinanceGlEntry>)m.Invoke(null, new object?[] { original.TenantId, Guid.NewGuid(), new[] { original }, "X", null, "u", "why" })!).Single();

        (contra.CostCenterId, contra.CostCenterCode, contra.CostCenterName, contra.CostCenterGlSegment)
            .Should().Be((original.CostCenterId, "CC-1", "One", "S1"));
        contra.PostingKey.Should().Be(PayrollCostStamper.ReversalKey(original.Id));
    }

    // ── WP-E finding 2a: the hand ERP status endpoint ────────────────────────

    [Fact]
    public async Task TheHandErpStatusEndpoint_SkipsLinesThatAreAlreadyPosted_AndSaysHowManyItSkipped()
    {
        await using var k = await KitAsync();
        var run = await LockedRunAsync(k);
        // one line the ERP has already confirmed (written Posted at insert, which the state machine allows)
        k.Db.ChangeTracker.Clear();
        k.Db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Payroll", SourceEntityId = run.Id, EventType = "PayrollAccrual",
            DebitAccount = "1000 - Cash/Bank", Amount = 1m, Currency = "SAR", Period = "2026-09", Description = "already in the ERP",
            ErpPostingStatus = ErpPostingStatuses.Posted, ErpDocumentNumber = "DOC-1",
        });
        await k.Db.SaveChangesAsync();
        k.Db.ChangeTracker.Clear();

        var ctrl = k.K.Payroll(k.Db, Guid.NewGuid(), "Admin", "payroll.export");
        var res = await ctrl.UpdateErpPostingStatus(run.Id, new ErpPostingStatusRequest(ErpPostingStatuses.Rejected, "ref", "the ERP refused the file"), default);

        res.Should().BeOfType<OkObjectResult>();
        Json(res).GetProperty("skippedLines").GetInt32().Should().Be(1);
        await using var read = k.K.NewContext();
        var gl = await k.K.RunGlAsync(run.Id, read);
        gl.Single(l => l.ErpDocumentNumber == "DOC-1").ErpPostingStatus.Should().Be(ErpPostingStatuses.Posted, "a posted line is never walked back");
        gl.Where(l => l.ErpDocumentNumber != "DOC-1").Should().OnlyContain(l => l.ErpPostingStatus == ErpPostingStatuses.Rejected);
    }
}
