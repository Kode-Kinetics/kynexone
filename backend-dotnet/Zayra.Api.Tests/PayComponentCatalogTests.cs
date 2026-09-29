using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F2 — tenant pay-component catalog: the write API, effective dating, and the money-path guarantees for
/// runs that carry tenant components (net = Σ lines, GL DR = CR, GOSI deducted = recomputed = posted to
/// 2101/2106, supplemental runs untouched, locked periods never rewritten).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PayComponentCatalogTests
{
    private readonly PostgresFixture _fx;
    public PayComponentCatalogTests(PostgresFixture fx) => _fx = fx;

    private static readonly DateOnly Jun = new(2026, 6, 1);

    // ══ 1. The tenant boundary ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("BASIC", "Earning", "Fixed", 100, "EARN:OTHER", "2026-06-01", "code")]              // system code
    [InlineData("GOSI_TOPUP", "Deduction", "Fixed", 100, "DED:OTHER", "2026-06-01", "code")]        // statutory prefix
    [InlineData("GPSSA_EXTRA", "Deduction", "Fixed", 100, "DED:OTHER", "2026-06-01", "code")]       // statutory prefix
    [InlineData("STATUTORY_X", "Deduction", "Fixed", 100, "DED:OTHER", "2026-06-01", "code")]       // statutory prefix
    [InlineData("BONUS_EID", "Earning", "Fixed", 100, "EARN:OTHER", "2026-06-01", "code")]          // bonus family
    [InlineData("ADJ_MANUAL", "Deduction", "Fixed", 100, "DED:OTHER", "2026-06-01", "code")]        // adjustment family
    [InlineData("ARREARS_X", "Earning", "Fixed", 100, "EARN:OTHER", "2026-06-01", "code")]          // arrears family
    [InlineData("EOSB_GRATUITY", "Earning", "Fixed", 100, "EARN:OTHER", "2026-06-01", "code")]      // settlement code
    [InlineData("BONUS_TAX", "Deduction", "Fixed", 100, "DED:OTHER", "2026-06-01", "code")]         // bonus tax code
    [InlineData("MY_ALLOW", "Earning", "StructureField", 100, "EARN:OTHER", "2026-06-01", "calcMethod")]
    [InlineData("MY_STAT", "Deduction", "Statutory", 100, "DED:OTHER", "2026-06-01", "calcMethod")]
    [InlineData("MY_FORMULA", "Deduction", "Formula", 100, "DED:OTHER", "2026-06-01", "calcMethod")]
    [InlineData("MY_ER", "EmployerContribution", "Fixed", 100, "DED:OTHER", "2026-06-01", "componentType")]
    [InlineData("MY_DED", "Deduction", "Fixed", 100.123, "DED:OTHER", "2026-06-01", "value")]        // 3dp money
    [InlineData("MY_DED", "Deduction", "Fixed", 0, "DED:OTHER", "2026-06-01", "value")]
    [InlineData("MY_PCT", "Earning", "PercentOfBasic", 101, "EARN:OTHER", "2026-06-01", "value")]
    [InlineData("MY_DED", "Deduction", "Fixed", 100, "", "2026-06-01", "glDriverKey")]              // no mapping
    [InlineData("MY_DED", "Deduction", "Fixed", 100, "DED:STATUTORY_EE", "2026-06-01", "glDriverKey")] // GOSI 2101
    [InlineData("MY_ER2", "Deduction", "Fixed", 100, "DED:STATUTORY_ER", "2026-06-01", "glDriverKey")] // GOSI 2106
    [InlineData("MY_DED", "Deduction", "Fixed", 100, "EARN:OTHER", "2026-06-01", "glDriverKey")]    // wrong category
    [InlineData("MY_EARN", "Earning", "Fixed", 100, "NET_PAYABLE", "2026-06-01", "glDriverKey")]    // balancing driver
    [InlineData("MY_DED", "Deduction", "Fixed", 100, "DED:DOES_NOT_EXIST", "2026-06-01", "glDriverKey")]
    [InlineData("MY_DED", "Deduction", "Fixed", 100, "DED:OTHER", "2026-06-15", "effectiveFrom")]   // mid-period
    public async Task Create_RefusesAnythingOutsideTheTenantBoundary(
        string code, string type, string calc, double value, string driver, string from, string field)
    {
        var (tenantId, companyId) = await NewTenantAsync();
        await using var db = _fx.CreateDb();
        var res = await Catalog(db, tenantId).Create(null,
            new PayComponentsController.CreateRequest(code, "X", null, type, calc, (decimal)value, driver, DateOnly.Parse(from)),
            CancellationToken.None);
        var body = Assert.IsType<UnprocessableEntityObjectResult>(res);
        var errors = JsonDocument.Parse(JsonSerializer.Serialize(body.Value)).RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out _), $"expected an error on '{field}', got {errors}");
        Assert.Equal(0, await db.PayComponents.CountAsync(c => c.TenantId == tenantId && !c.IsSystem));
        _ = companyId;
    }

    [Fact]
    public async Task Create_RefusesACustomDriverThatLandsOnTheGosiLiabilityAccount()
    {
        var (tenantId, _) = await NewTenantAsync();
        await using var db = _fx.CreateDb();
        // A custom driver whose DEFAULT ACCOUNT is 2101 — the GOSI employee liability. Its key is not on the
        // deny-list, so only the resolved-account check can catch it.
        db.GlDrivers.Add(CustomDriver(tenantId, "DED:SNEAKY", "2101", "Social Insurance Payable (Employee)", "SNEAKY"));
        db.GlDrivers.Add(CustomDriver(tenantId, "DED:UNION", "2150", "Union Dues Payable", "UNION_DUES"));
        await db.SaveChangesAsync();

        var bad = await Catalog(db, tenantId).Create(null, Req("SNEAKY", "Deduction", "Fixed", 50m, "DED:SNEAKY"), CancellationToken.None);
        var body = JsonSerializer.Serialize(Assert.IsType<UnprocessableEntityObjectResult>(bad).Value);
        Assert.Contains("2101", body);

        var ok = await Catalog(db, tenantId).Create(null, Req("UNION_DUES", "Deduction", "Fixed", 50m, "DED:UNION"), CancellationToken.None);
        Assert.IsType<OkObjectResult>(ok);
    }

    [Fact]
    public async Task SystemAndStatutoryRows_AreReadOnly()
    {
        var (tenantId, _) = await NewTenantAsync(seed: true);
        await using var db = _fx.CreateDb();
        var basic = await db.PayComponents.FirstAsync(c => c.TenantId == tenantId && c.Code == "BASIC");
        var statEe = await db.PayComponents.FirstAsync(c => c.TenantId == tenantId && c.Code == "STATUTORY_EE");

        var put = await Catalog(db, tenantId).Update(basic.Id,
            new PayComponentsController.UpdateRequest("Hacked", null, "Fixed", 1m, "EARN:OTHER", Jun), CancellationToken.None);
        Assert.Contains("system_component", JsonSerializer.Serialize(Assert.IsType<ConflictObjectResult>(put).Value));

        var off = await Catalog(db, tenantId).Deactivate(statEe.Id, new PayComponentsController.DeactivateRequest(Jun), CancellationToken.None);
        Assert.Contains("statutory_component", JsonSerializer.Serialize(Assert.IsType<ConflictObjectResult>(off).Value));
        Assert.Equal("Basic salary", (await db.PayComponents.AsNoTracking().FirstAsync(c => c.Id == basic.Id)).NameEn);
    }

    // ══ 2. Seed on first write; the fallback is a last resort ═══════════════════════════════════

    [Fact]
    public async Task FirstWrite_SeedsTheSystemCatalog_AndTheListReportsItsSource()
    {
        var (tenantId, companyId) = await NewTenantAsync(seed: false);
        await using var db = _fx.CreateDb();
        var ctrl = Catalog(db, tenantId);

        var before = Json(await ctrl.List(companyId, "2026-06", CancellationToken.None));
        Assert.Equal("compiled-fallback", before.GetProperty("source").GetString());
        Assert.Equal(17, before.GetProperty("components").GetArrayLength());

        Assert.IsType<OkObjectResult>(await ctrl.Create(null, Req("UNION_DUES", "Deduction", "Fixed", 100m, "DED:OTHER"), CancellationToken.None));

        Assert.Equal(17, await db.PayComponents.CountAsync(c => c.TenantId == tenantId && c.IsSystem && c.CompanyId == null));
        var after = Json(await ctrl.List(companyId, "2026-06", CancellationToken.None));
        Assert.Equal("catalog", after.GetProperty("source").GetString());
        Assert.Equal(18, after.GetProperty("components").GetArrayLength());
        // Not yet in effect before its start period.
        var may = Json(await ctrl.List(companyId, "2026-05", CancellationToken.None));
        Assert.Equal(17, may.GetProperty("components").GetArrayLength());
    }

    [Fact]
    public async Task Provisioning_SeedsTheCatalogOnce_EvenWhenTheSeederAlreadyRanInTheSameUnitOfWork()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        // PlatformController.CreateTenantCore order: seeder (no save) → bundle (which seeds + saves).
        await PayComponentSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
        var first = await TenantProvisioningBundle.ProvisionAsync(db, tenantId, "SA", CancellationToken.None);
        var again = await TenantProvisioningBundle.ProvisionAsync(db, tenantId, "SA", CancellationToken.None);
        Assert.Equal(0, first.PayComponents); // already added in the unit of work — not added twice
        Assert.Equal(0, again.PayComponents);
        Assert.Equal(17, await db.PayComponents.CountAsync(c => c.TenantId == tenantId));

        var fresh = await PostgresFixture.SeedMinimalTenant(db);
        Assert.Equal(17, (await TenantProvisioningBundle.ProvisionAsync(db, fresh, "SA", CancellationToken.None)).PayComponents);
    }

    // ══ 3. Effective dating across periods; a locked period is never rewritten ═══════════════════

    [Fact]
    public async Task EffectiveDating_AppliesFromADateForward_AndNeverRewritesALockedPeriod()
    {
        var (tenantId, companyId) = await NewTenantAsync(seed: true);
        var empId = await AddEmployeeAsync(tenantId, companyId, "ED-1", "Saudi", 10_000m, 3_000m);
        Guid componentId;
        await using (var db = _fx.CreateDb())
        {
            var res = await Catalog(db, tenantId).Create(null, Req("UNION_DUES", "Deduction", "Fixed", 100m, "DED:OTHER"), CancellationToken.None);
            componentId = Json(res).GetProperty("component").GetProperty("Id").GetGuid();
        }

        // June: 100, processed and LOCKED.
        var june = await NewRunAsync(tenantId, companyId, 2026, 6);
        await ProcessAsync(tenantId, june);
        await ApproveAndLockAsync(tenantId, june);
        var juneBefore = await SnapshotAsync(june);
        Assert.Equal(100m, juneBefore.Dues);
        Assert.Equal(13_000m - 1_267.50m - 100m, juneBefore.Net);

        await using (var db = _fx.CreateDb())
        {
            var ctrl = Catalog(db, tenantId);
            // A change effective in the locked period is refused, and says what the earliest date is.
            var refused = await ctrl.Update(componentId, new PayComponentsController.UpdateRequest(null, null, null, 150m, null, Jun), CancellationToken.None);
            var body = JsonSerializer.Serialize(Assert.IsType<ConflictObjectResult>(refused).Value);
            Assert.Contains("period_committed", body);
            Assert.Contains("2026-07-01", body);

            // From August: 150 (a new version; the June version is closed on 31 July, not edited).
            Assert.IsType<OkObjectResult>(await ctrl.Update(componentId,
                new PayComponentsController.UpdateRequest(null, null, null, 150m, null, new DateOnly(2026, 8, 1)), CancellationToken.None));
            // From October: gone.
            var v2 = await db.PayComponents.AsNoTracking().SingleAsync(c => c.TenantId == tenantId && c.Code == "UNION_DUES" && c.EffectiveFrom == new DateOnly(2026, 8, 1));
            Assert.IsType<OkObjectResult>(await ctrl.Deactivate(v2.Id,
                new PayComponentsController.DeactivateRequest(new DateOnly(2026, 10, 1)), CancellationToken.None));

            var versions = await db.PayComponents.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Code == "UNION_DUES").OrderBy(c => c.EffectiveFrom).ToListAsync();
            Assert.Equal(2, versions.Count);
            Assert.Equal((Jun, new DateOnly(2026, 7, 31), 100m), (versions[0].EffectiveFrom!.Value, versions[0].EffectiveTo!.Value, versions[0].Value!.Value));
            Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 30), 150m), (versions[1].EffectiveFrom!.Value, versions[1].EffectiveTo!.Value, versions[1].Value!.Value));
        }

        var expected = new Dictionary<int, decimal> { [7] = 100m, [8] = 150m, [9] = 150m, [10] = 0m };
        foreach (var (month, dues) in expected)
        {
            var run = await NewRunAsync(tenantId, companyId, 2026, month);
            await ProcessAsync(tenantId, run);
            var snap = await SnapshotAsync(run);
            Assert.Equal(dues, snap.Dues);
            Assert.Equal(13_000m - 1_267.50m - dues, snap.Net);
            Assert.Empty(snap.Errors);
        }

        // The locked June run is byte-for-byte what it was at Lock.
        Assert.Equal(juneBefore, await SnapshotAsync(june));
        _ = empId;
    }

    // ══ 4. GL balances and GOSI ties out, with tenant components on the run ═════════════════════

    [Fact]
    public async Task TenantComponents_GlBalances_PostsToTheirOwnAccount_AndGosiStillTiesOutTo2101And2106()
    {
        var (tenantId, companyId) = await NewTenantAsync(seed: true);
        var saudi = await AddEmployeeAsync(tenantId, companyId, "TO-SA", "Saudi", 10_000m, 3_000m);
        var expat = await AddEmployeeAsync(tenantId, companyId, "TO-IN", "Indian", 8_000m, 2_000m);
        await using (var db = _fx.CreateDb())
        {
            db.GlDrivers.Add(CustomDriver(tenantId, "DED:UNION", "2150", "Union Dues Payable", "UNION_DUES"));
            await db.SaveChangesAsync();
            var ctrl = Catalog(db, tenantId);
            Assert.IsType<OkObjectResult>(await ctrl.Create(null, Req("HARDSHIP", "Earning", "PercentOfBasic", 10m, "EARN:OTHER"), CancellationToken.None));
            Assert.IsType<OkObjectResult>(await ctrl.Create(null, Req("UNION_DUES", "Deduction", "Fixed", 100m, "DED:UNION"), CancellationToken.None));
        }

        var runId = await NewRunAsync(tenantId, companyId, 2026, 6);
        await ProcessAsync(tenantId, runId);

        await using (var db = _fx.CreateDb())
        {
            var slips = await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == runId).ToDictionaryAsync(s => s.EmployeeId);
            // Saudi: +1,000 hardship, −100 dues; GOSI on the UNCHANGED covered wage 13,000 (not 14,000).
            Assert.Equal(14_000m, slips[saudi].GrossSalary);
            Assert.Equal(1_267.50m, slips[saudi].EmployeeStatutoryTotal);
            Assert.Equal(1_527.50m, slips[saudi].EmployerStatutoryTotal);
            Assert.Equal(14_000m - 1_267.50m - 100m, slips[saudi].NetSalary);
            // Expat: +800, −100; only OH 2% on 10,000.
            Assert.Equal(10_800m, slips[expat].GrossSalary);
            Assert.Equal(0m, slips[expat].EmployeeStatutoryTotal);
            Assert.Equal(200m, slips[expat].EmployerStatutoryTotal);
            Assert.Equal(10_800m - 100m, slips[expat].NetSalary);

            // The driver is pinned on the line at Process.
            var dues = await db.PayrollDeductions.AsNoTracking().Where(d => d.PayrollRunId == runId && d.ComponentCode == "UNION_DUES").ToListAsync();
            Assert.Equal(2, dues.Count);
            Assert.All(dues, d => Assert.Equal("DED:UNION", d.GlDriverKey));
            // System / pack lines stay un-pinned (pre-F2 routing).
            Assert.False(await db.PayrollDeductions.AnyAsync(d => d.PayrollRunId == runId && d.Source == "Statutory" && d.GlDriverKey != null));
        }

        await ApproveAndLockAsync(tenantId, runId);

        await using (var db = _fx.CreateDb())
        {
            var gl = await db.FinanceGlEntries.AsNoTracking()
                .Where(g => g.TenantId == tenantId && g.SourceEntityId == runId && g.EventType == GlEventTypes.Accrual).ToListAsync();
            var dr = gl.Where(g => g.DebitAccount != "").Sum(g => g.Amount);
            var cr = gl.Where(g => g.CreditAccount != "").Sum(g => g.Amount);
            Assert.Equal(dr, cr);
            Assert.Equal(200m, gl.Single(g => g.CreditAccount == "2150 - Union Dues Payable").Amount);
            Assert.Equal(1_800m, gl.Single(g => g.DebitAccount.StartsWith("5099")).Amount);
            // Net payable credit == Σ slip net.
            var net = await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == runId).SumAsync(s => s.NetSalary);
            Assert.Equal(net, gl.Single(g => g.CreditAccount.StartsWith("2100")).Amount);

            // GOSI: deducted = recomputed = posted to 2101 / 2106, per employee, zero variance.
            var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            var recon = await new GosiReconciliationService(db, new KsaTestPackResolver(PayComponentNetPayDefectTests.KsaRules()))
                .ReconcileAsync(tenantId, run, CancellationToken.None);
            Assert.True(recon.GlPosted);
            Assert.Equal(1_267.50m, recon.ActualEmployeeTotal);
            Assert.Equal(1_727.50m, recon.ActualEmployerTotal);
            Assert.Equal(0m, recon.ExpectedVsActualEmployeeDelta);
            Assert.Equal(0m, recon.ExpectedVsActualEmployerDelta);
            Assert.Equal(1_267.50m, recon.GlEmployeeLiability);
            Assert.Equal(1_727.50m, recon.GlEmployerLiability);
            Assert.Equal(0m, recon.GlEmployeeDelta);
            Assert.Equal(0m, recon.GlEmployerDelta);
            Assert.StartsWith("2101", recon.GlEmployeeAccount);
            Assert.StartsWith("2106", recon.GlEmployerAccount);
            Assert.Equal(0, recon.VarianceCount);
        }
    }

    // ══ 5. Supplemental runs pay no recurring component ══════════════════════════════════════════

    [Fact]
    public async Task OffCycleRun_CarriesNoTenantComponent_AndLocksBalanced()
    {
        var (tenantId, companyId) = await NewTenantAsync(seed: true);
        var emp = await AddEmployeeAsync(tenantId, companyId, "OC-1", "Saudi", 10_000m, 3_000m);
        await using (var db = _fx.CreateDb())
        {
            var ctrl = Catalog(db, tenantId);
            Assert.IsType<OkObjectResult>(await ctrl.Create(null, Req("HARDSHIP", "Earning", "Fixed", 500m, "EARN:OTHER"), CancellationToken.None));
            Assert.IsType<OkObjectResult>(await ctrl.Create(null, Req("UNION_DUES", "Deduction", "Fixed", 100m, "DED:OTHER"), CancellationToken.None));
        }
        var regular = await NewRunAsync(tenantId, companyId, 2026, 6);
        await ProcessAsync(tenantId, regular);
        await ApproveAndLockAsync(tenantId, regular);

        Guid offCycle;
        await using (var db = _fx.CreateDb())
        {
            var res = await PayComponentNetPayDefectTests.Build(db, tenantId, "payroll.write").CreateRun(
                new CreatePayrollRunRequest(2026, 6, companyId, PayrollRunTypes.OffCycle), CancellationToken.None);
            offCycle = ((PayrollRun)Assert.IsType<CreatedResult>(res).Value!).Id;
            db.ChangeTracker.Clear();
            var sel = await PayComponentNetPayDefectTests.Build(db, tenantId, "payroll.write").UpsertRunSelection(offCycle,
                new PayrollRunSelectionRequest("Include", "F2 off-cycle correction", new List<int> { emp }), CancellationToken.None);
            if (sel is ObjectResult { StatusCode: >= 400 } badSel)
                Assert.Fail($"Selection refused: HTTP {badSel.StatusCode} {JsonSerializer.Serialize(badSel.Value)}");
            db.PayrollAdjustments.Add(new PayrollAdjustment
            {
                TenantId = tenantId, PayrollRunId = offCycle, EmployeeId = emp, AdjustmentType = "Shift Correction",
                Amount = 700m, Status = "Approved", Reason = "F2",
            });
            await db.SaveChangesAsync();
        }
        await ProcessAsync(tenantId, offCycle);
        await using (var db = _fx.CreateDb())
        {
            Assert.False(await db.PayrollEarnings.AnyAsync(e => e.PayrollRunId == offCycle && e.ComponentCode == "HARDSHIP"));
            Assert.False(await db.PayrollDeductions.AnyAsync(d => d.PayrollRunId == offCycle && d.ComponentCode == "UNION_DUES"));
            var slip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == offCycle);
            Assert.Equal(700m, slip.GrossSalary);
            // Statutory is period-incremental: the adjustment is outside the covered wage, the regular run
            // already took the month's GOSI, so this run takes none.
            Assert.Equal(0m, slip.EmployeeStatutoryTotal);
            Assert.Equal(700m, slip.NetSalary);
        }
        await ApproveAndLockAsync(tenantId, offCycle);
    }

    // ══ 6. The invariant names any divergence and blocks the run ═════════════════════════════════

    [Fact]
    public async Task LinesThatDoNotReconcileToTheSlip_AreANamedNonOverridableErrorThatBlocksApproval()
    {
        var (tenantId, companyId) = await NewTenantAsync(seed: true);
        var emp = await AddEmployeeAsync(tenantId, companyId, "IV-1", "Indian", 8_000m, 0m);
        await using (var db = _fx.CreateDb())
        {
            // Written straight to the table (the API refuses StructureField): a second BASIC-valued line the
            // slip aggregates know nothing about — exactly the pre-F2 defect shape, via a different door.
            db.PayComponents.Add(new PayComponent
            {
                TenantId = tenantId, Code = "BASIC_COPY", NameEn = "Basic copy", NameAr = "x",
                ComponentType = PayComponentTypes.Earning, CalcMethod = PayComponentCalcMethods.StructureField,
                StructureField = PayComponentStructureFields.BasicSalary, DisplayOrder = 35, IsActive = true,
            });
            await db.SaveChangesAsync();
        }
        var runId = await NewRunAsync(tenantId, companyId, 2026, 6);
        await ProcessAsync(tenantId, runId);

        await using var db2 = _fx.CreateDb();
        var err = await db2.PayrollValidationResults.AsNoTracking()
            .SingleAsync(v => v.PayrollRunId == runId && v.Code == "PAYSLIP_LINES_MISMATCH");
        Assert.Equal("Error", err.Severity);
        Assert.Equal(emp, err.EmployeeId);
        Assert.Contains("16,000.00", err.Message); // earning lines
        Assert.Contains("8,000.00", err.Message);  // gross
        Assert.False(PayrollValidationOverridePolicy.IsOverridable("PAYSLIP_LINES_MISMATCH"));
        Assert.Contains("PAYSLIP_LINES_MISMATCH", PayrollValidationOverridePolicy.NonOverridable);

        var approve = await PayComponentNetPayDefectTests.Build(db2, tenantId, "payroll.approve")
            .Approve(runId, new PayrollDecisionRequest("F2"), CancellationToken.None);
        Assert.Contains("PAYSLIP_LINES_MISMATCH", JsonSerializer.Serialize(Assert.IsType<UnprocessableEntityObjectResult>(approve).Value));
    }

    // ══ 7. Proration and taxability ═════════════════════════════════════════════════════════════

    [Fact]
    public async Task Joiner_PercentFollowsProratedBasic_FixedIsMonthly_AndLinesEqualTotals()
    {
        var (tenantId, companyId) = await NewTenantAsync(seed: true);
        var emp = await AddEmployeeAsync(tenantId, companyId, "PR-1", "Indian", 9_000m, 3_000m, joined: new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc));
        await using (var db = _fx.CreateDb())
        {
            var ctrl = Catalog(db, tenantId);
            Assert.IsType<OkObjectResult>(await ctrl.Create(null, Req("HARDSHIP", "Earning", "PercentOfBasic", 10m, "EARN:OTHER"), CancellationToken.None));
            Assert.IsType<OkObjectResult>(await ctrl.Create(null, Req("UNION_DUES", "Deduction", "Fixed", 100m, "DED:OTHER"), CancellationToken.None));
        }
        var runId = await NewRunAsync(tenantId, companyId, 2026, 6);
        await ProcessAsync(tenantId, runId);

        await using var db2 = _fx.CreateDb();
        var slip = await db2.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId);
        Assert.True(slip.BasicSalary < 9_000m, "the joiner's basic is prorated");
        var earnings = await db2.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == runId).ToListAsync();
        var deductions = await db2.PayrollDeductions.AsNoTracking().Where(d => d.PayrollRunId == runId && !d.IsEmployerContribution).ToListAsync();
        Assert.Equal(Math.Round(slip.BasicSalary * 0.10m, 2), earnings.Single(e => e.ComponentCode == "HARDSHIP").Amount);
        Assert.Equal(100m, deductions.Single(d => d.ComponentCode == "UNION_DUES").Amount);
        Assert.Equal(earnings.Sum(e => e.Amount), slip.GrossSalary);
        Assert.Equal(deductions.Sum(d => d.Amount), slip.Deductions);
        Assert.Equal(slip.GrossSalary - slip.Deductions, slip.NetSalary);
        Assert.False(await db2.PayrollValidationResults.AnyAsync(v => v.PayrollRunId == runId && v.Code == "PAYSLIP_LINES_MISMATCH"));
        _ = emp;
    }

    [Theory]
    [InlineData(true, 1_100)]   // (10,000 basic + 1,000 taxable allowance) × 10%
    [InlineData(false, 1_000)]  // allowance not taxable: basic only
    public async Task TaxableTenantEarning_EntersTheIncomeTaxBase(bool taxable, int expectedTax)
    {
        var (tenantId, companyId) = await NewTenantAsync(seed: true);
        await AddEmployeeAsync(tenantId, companyId, "TX-1", "Indian", 10_000m, 0m);
        await using (var db = _fx.CreateDb())
        {
            db.SystemSettings.Add(new SystemSetting { TenantId = tenantId, Category = "Payroll", SettingKey = "IncomeTaxRate", SettingValue = "10" });
            await db.SaveChangesAsync();
            Assert.IsType<OkObjectResult>(await Catalog(db, tenantId).Create(null,
                Req("SITE_ALLOW", "Earning", "Fixed", 1_000m, "EARN:OTHER") with { IsTaxable = taxable }, CancellationToken.None));
        }
        var runId = await NewRunAsync(tenantId, companyId, 2026, 6);
        await ProcessAsync(tenantId, runId);
        await using var db2 = _fx.CreateDb();
        var tax = await db2.PayrollDeductions.AsNoTracking().SingleAsync(d => d.PayrollRunId == runId && d.ComponentCode == "INCOME_TAX");
        Assert.Equal(expectedTax, tax.Amount);
        var slip = await db2.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId);
        Assert.Equal(11_000m - expectedTax, slip.NetSalary);
    }

    // ══ harness ═════════════════════════════════════════════════════════════════════════════════

    private sealed record RunSnap(decimal Gross, decimal Deductions, decimal Net, decimal Dues, string Lines, string Gl, string Errors);

    private async Task<RunSnap> SnapshotAsync(Guid runId)
    {
        await using var db = _fx.CreateDb();
        var slip = await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId);
        var ded = await db.PayrollDeductions.AsNoTracking().Where(d => d.PayrollRunId == runId)
            .OrderBy(d => d.ComponentCode).ThenBy(d => d.IsEmployerContribution).ToListAsync();
        var earn = await db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == runId).OrderBy(e => e.ComponentCode).ToListAsync();
        var gl = await db.FinanceGlEntries.AsNoTracking().Where(g => g.SourceEntityId == runId)
            .OrderBy(g => g.DebitAccount).ThenBy(g => g.CreditAccount).ThenBy(g => g.Amount).ToListAsync();
        var errors = await db.PayrollValidationResults.AsNoTracking()
            .Where(v => v.PayrollRunId == runId && v.Severity == "Error").Select(v => v.Code).ToListAsync();
        return new RunSnap(slip.GrossSalary, slip.Deductions, slip.NetSalary,
            ded.Where(d => d.ComponentCode == "UNION_DUES").Sum(d => d.Amount),
            string.Join(";", earn.Select(e => $"{e.ComponentCode}:{e.Amount}").Concat(ded.Select(d => $"{d.ComponentCode}:{d.Amount}:{d.IsEmployerContribution}"))),
            string.Join(";", gl.Select(g => $"{g.DebitAccount}|{g.CreditAccount}|{g.Amount}")),
            string.Join(";", errors));
    }

    private static PayComponentsController.CreateRequest Req(string code, string type, string calc, decimal value, string driver)
        => new(code, code.Replace('_', ' '), null, type, calc, value, driver, Jun);

    private static GlDriver CustomDriver(Guid tenantId, string key, string code, string name, string matchCode) => new()
    {
        TenantId = tenantId, Key = key, Label = key, Category = GlDriverCategories.Deduction, PostingSide = "CR",
        AccountType = "Liability", DefaultCode = code, DefaultName = name, MatchMode = GlDriverMatchModes.Exact,
        MatchComponentCode = matchCode, IsSystem = false, IsActive = true, SortOrder = 500,
    };

    private static JsonElement Json(IActionResult r)
        => JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(r).Value)).RootElement;

    private static PayComponentsController Catalog(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new PayComponentsController(db);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                    new Claim("permission", "payroll.write"),
                    new Claim("permission", "payroll.read"),
                }, "Test")),
            },
        };
        return ctrl;
    }

    private async Task<(Guid TenantId, Guid CompanyId)> NewTenantAsync(bool seed = false)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        if (seed) await PayComponentSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = $"F2 Co {Guid.NewGuid():N}", CountryCode = "SAU",
            Jurisdiction = "KSA-mainland", RegistrationNumber = $"F2-{Guid.NewGuid():N}", DefaultCurrency = "SAR",
            IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return (tenantId, company.Id);
    }

    private async Task<int> AddEmployeeAsync(Guid tenantId, Guid companyId, string code, string nationality,
        decimal basic, decimal housing, DateTime? joined = null)
    {
        await using var db = _fx.CreateDb();
        var e = new Employee
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeCode = code, FullName = $"Employee {code}",
            Nationality = nationality, ContractType = "Indefinite", Status = "Active",
            JoiningDate = joined ?? new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = e.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = basic,
            HousingAllowance = housing, EffectiveDate = new DateOnly(2022, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = e.Id, Iban = "SA4420000001234567891234", MolId = $"MOL-{Guid.NewGuid():N}", SalaryCurrency = "SAR",
        });
        await db.SaveChangesAsync();
        return e.Id;
    }

    private async Task<Guid> NewRunAsync(Guid tenantId, Guid companyId, int year, int month)
    {
        await using var db = _fx.CreateDb();
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = companyId, Year = year, Month = month, Status = "Draft",
            CreatedAtUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task ProcessAsync(Guid tenantId, Guid runId)
    {
        await using var db = _fx.CreateDb();
        var res = await PayComponentNetPayDefectTests.Build(db, tenantId).Process(runId, CancellationToken.None);
        if (res is ObjectResult { StatusCode: >= 400 } bad)
            Assert.Fail($"Process refused: HTTP {bad.StatusCode} {JsonSerializer.Serialize(bad.Value)}");
    }

    /// <summary>The REAL Approve (block-on-error gate) then the REAL Lock (GL balance gate).</summary>
    private async Task ApproveAndLockAsync(Guid tenantId, Guid runId)
    {
        await using (var db = _fx.CreateDb())
        {
            var errors = await db.PayrollValidationResults.AsNoTracking()
                .Where(v => v.PayrollRunId == runId && v.Severity == "Error").Select(v => v.Code + ": " + v.Message).ToListAsync();
            Assert.True(errors.Count == 0, string.Join("\n", errors));
            var res = await PayComponentNetPayDefectTests.Build(db, tenantId, "payroll.approve")
                .Approve(runId, new PayrollDecisionRequest("F2"), CancellationToken.None);
            if (res is ObjectResult { StatusCode: >= 400 } bad)
                Assert.Fail($"Approve refused: HTTP {bad.StatusCode} {JsonSerializer.Serialize(bad.Value)}");
        }
        await using (var db = _fx.CreateDb())
        {
            var status = await db.PayrollRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync();
            if (status == "PendingFinanceReview")
                await PayComponentNetPayDefectTests.Build(db, tenantId, "payroll.approve")
                    .Approve(runId, new PayrollDecisionRequest("F2"), CancellationToken.None);
        }
        await using (var db = _fx.CreateDb())
        {
            var res = await PayComponentNetPayDefectTests.Build(db, tenantId, "payroll.lock").Lock(runId, CancellationToken.None);
            if (res is ObjectResult { StatusCode: >= 400 } bad)
                Assert.Fail($"Lock refused: HTTP {bad.StatusCode} {JsonSerializer.Serialize(bad.Value)}");
            Assert.IsType<OkObjectResult>(res);
        }
    }
}
