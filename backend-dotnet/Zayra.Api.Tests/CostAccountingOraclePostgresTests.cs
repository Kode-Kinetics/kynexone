using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostingLockKit;

namespace Zayra.Api.Tests;

/// <summary>
/// AM-6: the independent oracle (/tmp/kynexone-cc/oracle, copied verbatim into Fixtures/CostAccountingOracle). The fixture company is
/// built into the app on real PostgreSQL WITH migration M2, M1 and M2 are produced through the real Approve and Lock, and every journal is
/// compared line for line (journal, account, side, cost centre, amount) with expected/*.csv. A difference is a defect until proven otherwise.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class CostAccountingOraclePostgresTests
{
    private readonly PostgresFixture _fx;
    public CostAccountingOraclePostgresTests(PostgresFixture fx) => _fx = fx;

    // ── building M1 and M2 ───────────────────────────────────────────────────

    private async Task<OracleWorld> M1Async()
    {
        var w = await OracleWorld.CreateAsync(_fx);
        w.Reg1Seed(await w.SeedRunAsync("inputs/09_payroll_register_m1_regular.csv", "Regular", 9));
        await w.ApproveBonusBatchAsync();
        await w.ApproveSettlementAsync();
        await w.ConsumeBonusesAsync(w.Reg1.Id);
        (await w.K.ApproveAndLockAsync(w.Reg1.Id)).Should().BeOfType<OkObjectResult>();

        w.Oc1Seed(await w.SeedRunAsync("inputs/10_payroll_register_m1_offcycle_E009.csv", "OffCycle", 9));
        await w.K.Db.EmployeeFinalSettlements.Where(s => s.Id == w.Settlement.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.PayrollRunId, (Guid?)w.Oc1.Id));
        var oc = await w.K.ApproveAndLockAsync(w.Oc1.Id);
        oc.Should().BeOfType<OkObjectResult>(Json(oc).ToString());

        await w.IssueAdvanceAsync();
        return w;
    }

    private static string? M1Journal(OracleWorld w, FinanceGlEntry e) =>
        e.SourceModule == "Bonus" && e.EventType == GlEventTypes.BonusAccrual ? "J-BON-ACCR-2026-09"
        : e.SourceModule == "Advance" ? "J-ADV-ISS-2026-09"
        : e.EventType == GlEventTypes.SettlementAccrual && e.SourceEntityId == w.Settlement.Id ? "J-SETT-ACCR-2026-09"
        : e.SourceModule == "Payroll" && e.ReversalOfEntryId == null && e.SourceEntityId == w.Reg1.Id ? "RUN-REG-2026-09"
        : e.SourceModule == "Payroll" && e.ReversalOfEntryId == null && e.SourceEntityId == w.Oc1.Id ? "RUN-OC-2026-09"
        : null;

    private static void AssertEqualToOracle(IEnumerable<OracleLine> produced, IEnumerable<OracleLine> expected, string what)
    {
        var p = OracleWorld.Aggregate(produced);
        var e = OracleWorld.Aggregate(expected);
        var differences = p.Keys.Union(e.Keys).OrderBy(k => k, StringComparer.Ordinal)
            .Where(k => p.GetValueOrDefault(k) != e.GetValueOrDefault(k))
            .Select(k => $"{k}: produced {p.GetValueOrDefault(k):0.00} vs oracle {e.GetValueOrDefault(k):0.00}").ToList();
        differences.Should().BeEmpty($"{what} must equal the oracle line for line:\n" + string.Join("\n", differences));
    }

    [Fact]
    public async Task M1_TheProducedJournals_EqualTheOracle_LineForLine()
    {
        await using var w = await M1Async();
        var ledger = await w.AllLedgerAsync();

        AssertEqualToOracle(OracleWorld.Expand(ledger, e => M1Journal(w, e)), OracleWorld.Expected("expected/m1_expected_journal.csv"), "the M1 journals");
        ledger.Where(e => M1Journal(w, e) is null).Should().BeEmpty("nothing outside the oracle's journals was posted in M1");

        // balance per journal (m1_balance_check.csv)
        foreach (var row in OracleFiles.Csv("expected/m1_balance_check.csv").Where(r => r["journal_id"] != "ALL-M1"))
        {
            var lines = OracleWorld.Expand(ledger, e => M1Journal(w, e)).Where(l => l.Journal == row["journal_id"]).ToList();
            lines.Where(l => l.Side == "DR").Sum(l => l.Amount).Should().Be(OracleFiles.D(row["total_dr"]), row["journal_id"]);
            lines.Where(l => l.Side == "CR").Sum(l => l.Amount).Should().Be(OracleFiles.D(row["total_cr"]), row["journal_id"]);
        }
        // no ROUNDING line anywhere in the pack
        ledger.Should().NotContain(e => e.DebitAccount.StartsWith("5199") || e.CreditAccount.StartsWith("5199"));
    }

    [Fact]
    public async Task M1_TotalsByCostCentre_EqualTheOracle_AndTheLedgerIsAppendOnly()
    {
        await using var w = await M1Async();
        var ledger = await w.AllLedgerAsync();
        var costed = OracleWorld.Expand(ledger.Where(e => M1Journal(w, e) is not null), e => M1Journal(w, e)).Where(l => l.CostCentre != "").ToList();

        foreach (var row in OracleFiles.Csv("expected/m1_totals_by_cost_centre.csv").Where(r => r["account_code"] != "TOTAL"))
        {
            var lines = costed.Where(l => l.CostCentre == row["cost_centre_code"] && l.Account == row["account_code"]).ToList();
            (lines.Where(l => l.Side == "DR").Sum(l => l.Amount) - lines.Where(l => l.Side == "CR").Sum(l => l.Amount))
                .Should().Be(OracleFiles.D(row["net_dr_minus_cr"]), $"{row["cost_centre_code"]} {row["account_code"]}");
        }
        foreach (var row in OracleFiles.Csv("expected/m1_totals_by_cost_centre.csv").Where(r => r["account_code"] == "TOTAL" && r["cost_centre_code"] != "ALL"))
        {
            var lines = costed.Where(l => l.CostCentre == row["cost_centre_code"]).ToList();
            (lines.Where(l => l.Side == "DR").Sum(l => l.Amount) - lines.Where(l => l.Side == "CR").Sum(l => l.Amount))
                .Should().Be(OracleFiles.D(row["net_dr_minus_cr"]), $"P&L net {row["cost_centre_code"]}");
        }
        costed.GroupBy(l => l.CostCentre).Sum(g => g.Where(l => l.Side == "DR").Sum(l => l.Amount) - g.Where(l => l.Side == "CR").Sum(l => l.Amount))
            .Should().Be(OracleFiles.D(OracleFiles.Csv("expected/m1_totals_by_cost_centre.csv").Single(r => r["cost_centre_code"] == "ALL")["net_dr_minus_cr"]));
    }

    [Fact]
    public async Task M1_EveryExpenseLineCarriesACostCentre_AndNoPayableOrProvisionLineDoes()
    {
        await using var w = await M1Async();
        var ledger = await w.AllLedgerAsync();
        var lines = OracleWorld.Expand(ledger, e => M1Journal(w, e));
        lines.Where(l => l.Account.StartsWith("5") || l.Account == "6100").Should().OnlyContain(l => l.CostCentre != "");
        lines.Where(l => l.Account.StartsWith("1") || l.Account.StartsWith("2")).Should().OnlyContain(l => l.CostCentre == "");
        // every cost-centre code on a line exists in the master and its class matches the oracle's class
        var classes = OracleFiles.Csv("inputs/02_cost_centres.csv").ToDictionary(r => r["code"], r => r["functional_class"]);
        ledger.Where(l => l.CostCenterCode != null).Select(l => l.CostCenterCode!).Distinct().Should().OnlyContain(c => classes.ContainsKey(c));
        ledger.Select(l => l.PostingKey).Where(k => k != null).Should().OnlyHaveUniqueItems("posting keys are unique");
    }

    [Fact]
    public async Task M2_TheClassReRoute_TheVoidAndTheRepost_EqualTheOracle()
    {
        await using var w = await M1Async();
        var m1 = await w.AllLedgerAsync();
        var m1Ids = m1.Select(e => e.Id).ToHashSet();

        // between the M1 Lock and M2: close 2026-09, then save the class mapping through the real API as a DIFFERENT user than the locker
        await w.ClosePeriodAsync("2026-09");
        w.K.Db.ChangeTracker.Clear();
        var accounts = await w.K.Db.GlAccounts.AsNoTracking().Where(a => a.TenantId == w.TenantId && a.CompanyId == w.K.CompanyId).ToDictionaryAsync(a => a.Code, a => a.Id);
        var save = await CostAccountingKit.Gl(w.K.Db, w.TenantId, CostAccountingKit.ManagePerms, uid: w.MappingUser).SetMappings(w.K.CompanyId, new()
        {
            new GlMappingRequest("EARN:BASIC", accounts["5011"], null, "COGS"),
            new GlMappingRequest("EARN:HOUSING", accounts["5012"], null, "COGS"),
        }, default, "reroute COGS labour");
        CostAccountingKit.Status(save).Should().Be(200, Json(save).ToString());

        // M2 regular run
        w.Reg2Seed(await w.SeedRunAsync("inputs/11_payroll_register_m2_regular.csv", "Regular", 10));
        var lock2 = await w.K.ApproveAndLockAsync(w.Reg2.Id);
        lock2.Should().BeOfType<OkObjectResult>(Json(lock2).ToString());

        // void the off-cycle settlement run into the next open period (2026-10), then re-post it without the recovery
        await using (var db = w.K.K.NewContext())
        {
            var voided = await new PayrollVoidService(db).VoidAsync(w.Oc1.Id, w.TenantId, Guid.NewGuid(), "finance", "recovery was raised in error", default,
                new PayrollVoidOptions { PriorPeriodAdjustment = true, AdjustmentPeriod = "2026-10" });
            voided.IsVoided.Should().BeTrue(voided.BlockMessage ?? voided.BlockCode);
        }
        w.K.Db.ChangeTracker.Clear();
        w.ReplSeed(await w.SeedReplacementAsync());
        var lock3 = await w.K.ApproveAndLockAsync(w.Repl!.Id);
        lock3.Should().BeOfType<OkObjectResult>(Json(lock3).ToString());

        var all = await w.AllLedgerAsync();
        var m2 = all.Where(e => !m1Ids.Contains(e.Id) || e.IsReversed != m1.Single(x => x.Id == e.Id).IsReversed).Where(e => !m1Ids.Contains(e.Id)).ToList();
        string? Journal(FinanceGlEntry e) =>
            e.SourceModule == "Payroll" && e.SourceEntityId == w.Reg2.Id ? "RUN-REG-2026-10"
            : e.SourceModule == "Payroll" && e.SourceEntityId == w.Oc1.Id && e.ReversalOfEntryId != null ? "VOID-RUN-OC-2026-09"
            : e.SourceModule == "Payroll" && e.SourceEntityId == w.Repl!.Id ? "RUN-OC-2026-09-R1"
            : null;
        m2.Where(e => Journal(e) is null).Should().BeEmpty("M2 posted nothing outside the oracle's journals");
        AssertEqualToOracle(OracleWorld.Expand(m2, Journal), OracleWorld.Expected("expected/m2_expected_journal.csv"), "the M2 journals");

        // M1 untouched by the re-route: the M1 journals are byte-identical lines (same rows, nothing rewritten)
        var m1After = all.Where(e => m1Ids.Contains(e.Id)).ToList();
        m1After.Select(e => (e.Id, e.DebitAccount, e.CreditAccount, e.Amount, e.CostCenterCode, e.PostingKey))
            .Should().BeEquivalentTo(m1.Select(e => (e.Id, e.DebitAccount, e.CreditAccount, e.Amount, e.CostCenterCode, e.PostingKey)));

        // void_netting.csv, first scenario: the voided off-cycle run nets every (cost centre, account) to zero
        var ocAll = OracleWorld.Expand(all.Where(e => e.SourceEntityId == w.Oc1.Id && e.SourceModule == "Payroll"), _ => "OC");
        foreach (var row in OracleFiles.Csv("expected/void_netting.csv").Where(r => r["scenario"].StartsWith("void RUN-OC")))
        {
            var cc = row["cost_centre_code"] == "(UNCOSTED)" ? "" : row["cost_centre_code"];
            var lines = ocAll.Where(l => l.Account == row["account_code"] && l.CostCentre == cc).ToList();
            lines.Should().NotBeEmpty($"{row["account_code"]} {cc}");
            (lines.Where(l => l.Side == "DR").Sum(l => l.Amount) - lines.Where(l => l.Side == "CR").Sum(l => l.Amount))
                .Should().Be(OracleFiles.D(row["net_dr_minus_cr"]), $"void netting {row["account_code"]} {cc}");
        }
        // every contra copies the cost centre id and its snapshots, and the originals are flagged
        var contras = all.Where(e => e.SourceEntityId == w.Oc1.Id && e.ReversalOfEntryId != null).ToList();
        foreach (var c in contras)
        {
            var o = all.Single(x => x.Id == c.ReversalOfEntryId);
            (c.CostCenterId, c.CostCenterCode, c.CostCenterName, c.CostCenterGlSegment).Should().Be((o.CostCenterId, o.CostCenterCode, o.CostCenterName, o.CostCenterGlSegment));
            o.IsReversed.Should().BeTrue();
        }

        // m2_totals_by_cost_centre.csv: P&L net by cost centre over M2's journals
        var costed2 = OracleWorld.Expand(m2, Journal).Where(l => l.CostCentre != "").ToList();
        foreach (var row in OracleFiles.Csv("expected/m2_totals_by_cost_centre.csv").Where(r => r["account_code"] == "TOTAL" && r["cost_centre_code"] != "ALL"))
        {
            var lines = costed2.Where(l => l.CostCentre == row["cost_centre_code"]).ToList();
            (lines.Where(l => l.Side == "DR").Sum(l => l.Amount) - lines.Where(l => l.Side == "CR").Sum(l => l.Amount))
                .Should().Be(OracleFiles.D(row["net_dr_minus_cr"]), $"M2 P&L net {row["cost_centre_code"]}");
        }
    }

    [Fact]
    public async Task TheHypotheticalWholeRunVoid_OfTheM1RegularRun_NetsEveryCostCentreAndAccountToZero()
    {
        await using var w = await M1Async();
        await w.ClosePeriodAsync("2026-09");
        await using (var db = w.K.K.NewContext())
        {
            var voided = await new PayrollVoidService(db).VoidAsync(w.Reg1.Id, w.TenantId, Guid.NewGuid(), "finance", "hypothetical", default,
                new PayrollVoidOptions { PriorPeriodAdjustment = true, AdjustmentPeriod = "2026-10" });
            voided.IsVoided.Should().BeTrue(voided.BlockMessage ?? voided.BlockCode);
        }

        var runLines = OracleWorld.Expand((await w.AllLedgerAsync()).Where(e => e.SourceModule == "Payroll" && e.SourceEntityId == w.Reg1.Id), _ => "REG");
        foreach (var row in OracleFiles.Csv("expected/void_netting.csv").Where(r => r["scenario"].StartsWith("HYPOTHETICAL")))
        {
            var cc = row["cost_centre_code"] == "(UNCOSTED)" ? "" : row["cost_centre_code"];
            var lines = runLines.Where(l => l.Account == row["account_code"] && l.CostCentre == cc).ToList();
            lines.Should().NotBeEmpty($"{row["account_code"]} {cc}");
            (lines.Where(l => l.Side == "DR").Sum(l => l.Amount) - lines.Where(l => l.Side == "CR").Sum(l => l.Amount))
                .Should().Be(0m, $"{row["account_code"]} {cc}");
        }
        // and nothing outside the oracle's rows is left unnetted
        foreach (var g in runLines.GroupBy(l => (l.Account, l.CostCentre)))
            (g.Where(l => l.Side == "DR").Sum(l => l.Amount) - g.Where(l => l.Side == "CR").Sum(l => l.Amount)).Should().Be(0m, g.Key.ToString());
    }

    // ── lock_blockers.csv ────────────────────────────────────────────────────

    private static List<Dictionary<string, string>> Rows(string scenario) =>
        OracleFiles.Csv("expected/lock_blockers.csv").Where(r => r["scenario"] == scenario).ToList();

    private async Task<OracleWorld> BlockerWorldAsync(bool preset = true, bool defaultCc = true, string[]? exclude = null)
    {
        var w = await OracleWorld.CreateAsync(_fx, preset, defaultCc);
        w.Reg1Seed(await w.SeedRunAsync("inputs/09_payroll_register_m1_regular.csv", "Regular", 9, exclude: exclude));
        return w;
    }

    private static void AssertGroupsEqualCsv(System.Text.Json.JsonElement body, string scenario)
    {
        var expected = Rows(scenario);
        var groups = body.GetProperty("groups").EnumerateArray().ToList();
        groups.Should().HaveCount(expected.Count, scenario);
        foreach (var row in expected)
        {
            var g = row["error_code"] == "cost_centre_required"
                ? groups.Single(x => x.TryGetProperty("accountCode", out var a) && a.GetString() == row["account_code"])
                : groups.Single(x => x.GetProperty("message").GetString() == row["message_en"]);
            g.GetProperty("error").GetString().Should().Be(row["error_code"]);
            g.GetProperty("employeeCount").GetInt32().Should().Be(int.Parse(row["n_employees"]));
            g.GetProperty("employees").EnumerateArray().Select(e => e.GetProperty("code").GetString()).Order()
                .Should().Equal(row["employee_codes(max10)"].Split(';', StringSplitOptions.RemoveEmptyEntries).Order());
            if (row["aggregate_amount"] != "") g.GetProperty("amount").GetDecimal().Should().Be(OracleFiles.D(row["aggregate_amount"]));
            g.GetProperty("message").GetString().Should().Be(row["message_en"]);
        }
    }

    [Fact]
    public async Task SA1_NoCompanyDefault_OneUnresolvedEmployee_ReturnsM1PerRequiredAccount()
    {
        await using var w = await BlockerWorldAsync(defaultCc: false);
        var res = await w.K.ApproveAndLockAsync(w.Reg1.Id);
        Status(res).Should().Be(422);
        AssertGroupsEqualCsv(Json(res), "S-A1");
        (await w.AllLedgerAsync()).Should().BeEmpty();
        (await w.K.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == w.Reg1.Id)).Status.Should().Be("Approved");
    }

    [Fact]
    public async Task SA2_TheSubDepartmentLosesItsParent_TwoUnresolvedEmployees()
    {
        await using var w = await BlockerWorldAsync(defaultCc: false);
        w.K.Dept["D-SUP"].ParentDepartmentId = null;
        w.K.Touch();
        await w.K.Db.SaveChangesAsync();
        var res = await w.K.ApproveAndLockAsync(w.Reg1.Id);
        Status(res).Should().Be(422);
        AssertGroupsEqualCsv(Json(res), "S-A2");
    }

    [Fact]
    public async Task SA3_AllAccountsOptional_NoBlocker_TheUncostedEmployeeIsAcknowledgedAndPostsWithNoCostCentre()
    {
        await using var w = await BlockerWorldAsync(preset: false, defaultCc: false);
        w.K.Db.ChangeTracker.Clear();
        (await w.K.Payroll(w.K.ApproverId).Approve(w.Reg1.Id, new PayrollDecisionRequest("ok", ExpectedUncostedCount: 1), default)).Should().BeOfType<OkObjectResult>();
        var res = await w.K.LockAsync(w.Reg1.Id);
        res.Should().BeOfType<OkObjectResult>(Json(res).ToString());
        Json(res).GetProperty("costing").GetProperty("uncostedEmployees").GetInt32().Should().Be(1);
        var e004 = (await w.K.Db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == w.Reg1.Id && e.EmployeeId == w.K.Emp["E004"].Id).ToListAsync());
        e004.Should().OnlyContain(e => e.CostCenterId == null);
        var basic = (await w.AllLedgerAsync()).Where(l => l.DebitAccount.StartsWith("5001") && l.CostCenterCode == null).ToList();
        basic.Should().ContainSingle().Which.Amount.Should().Be(5000m);
    }

    [Fact]
    public async Task SA4_ADeactivatedCostCentre_IsM2AtEmployeeAndPositionLevel_TheCompanyDefaultDoesNotRescueIt()
    {
        await using var w = await BlockerWorldAsync();
        // Migration M2 refuses to deactivate a cost centre that a live employee or position points at, so the fixture is the legacy data
        // that predates the guard: the same UPDATE with the guard trigger switched off for the statement, as an owner-run repair would.
        await w.K.Db.Database.ExecuteSqlRawAsync("ALTER TABLE cost_centers DISABLE TRIGGER trg_cost_centers__guard");
        try { await w.K.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cost_centers SET is_active = false WHERE id = {w.K.Cc["CC-300"].Id}"); }
        finally { await w.K.Db.Database.ExecuteSqlRawAsync("ALTER TABLE cost_centers ENABLE ALWAYS TRIGGER trg_cost_centers__guard"); }
        var res = await w.K.ApproveAndLockAsync(w.Reg1.Id);
        Status(res).Should().Be(422);
        AssertGroupsEqualCsv(Json(res), "S-A4");
    }

    [Fact]
    public async Task SB1_ARequiredRoundingAccount_WithNoDefault_IsM4_AndWithADefaultItPostsOnThatCostCentre()
    {
        // every employee resolves; the run's stored net is 0.01 below earnings minus employee deductions
        await using (var w = await BlockerWorldAsync(defaultCc: false, exclude: new[] { "E004" }))
        {
            await w.K.Db.PayrollRuns.Where(r => r.Id == w.Reg1.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.TotalNetSalary, r => r.TotalNetSalary - 0.01m));
            var res = await w.K.ApproveAndLockAsync(w.Reg1.Id);
            Status(res).Should().Be(422);
            var row = Rows("S-B1").Single();
            Json(res).GetProperty("error").GetString().Should().Be(row["error_code"]);
            Json(res).GetProperty("message").GetString().Should().Be(row["message_en"]);
        }
        await using (var w = await BlockerWorldAsync(exclude: new[] { "E004" }))
        {
            await w.K.Db.PayrollRuns.Where(r => r.Id == w.Reg1.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.TotalNetSalary, r => r.TotalNetSalary - 0.01m));
            (await w.K.ApproveAndLockAsync(w.Reg1.Id)).Should().BeOfType<OkObjectResult>();
            var rounding = (await w.AllLedgerAsync()).Single(l => l.DebitAccount.StartsWith("5199") || l.CreditAccount.StartsWith("5199"));
            (rounding.Amount, rounding.CostCenterCode).Should().Be((0.01m, "CC-100"));
            // AM-34: a net 0.01 BELOW the component sums leaves debits 0.01 above credits, so the rounding line is a CREDIT of 0.01 to 5199
            // (the oracle prose now says so; amount, account and cost centre were always as stated).
            rounding.CreditAccount.Should().StartWith("5199");
        }
    }

    [Fact]
    public async Task SB2_AFixedCostCentreOnANotAllowedAccount_IsRefusedWithM3_AndNothingIsSaved()
    {
        await using var w = await OracleWorld.CreateAsync(_fx);
        var overtime = await w.K.Db.GlAccounts.SingleAsync(a => a.TenantId == w.TenantId && a.CompanyId == null && a.Code == "5005");
        overtime.CostCenterRule = "NotAllowed";
        await w.K.Db.SaveChangesAsync();
        w.K.Db.ChangeTracker.Clear();

        var res = await CostAccountingKit.Gl(w.K.Db, w.TenantId, CostAccountingKit.ManagePerms)
            .SetMappings(w.K.CompanyId, new() { new GlMappingRequest("EARN:OVERTIME", overtime.Id, w.K.Cc["CC-200"].Id) }, default, "fixed overtime");

        var row = Rows("S-B2").Single();
        CostAccountingKit.Status(res).Should().Be(int.Parse(row["http"]));
        CostAccountingKit.ErrorCode(res).Should().Be(row["error_code"]);
        CostAccountingKit.Body(res).P("message").GetString().Should().Be(row["message_en"]);
        (await w.K.Db.GlAccountMappings.IgnoreQueryFilters().CountAsync(m => m.TenantId == w.TenantId && m.DriverKey == "EARN:OVERTIME" && m.CompanyId == w.K.CompanyId)).Should().Be(0);
    }

    [Fact]
    public async Task SB3_ABonusHolderWithNoCostCentre_IsM1OnTheBonusAccount_AtLockAndAtAccrual()
    {
        await using var w = await OracleWorld.CreateAsync(_fx, defaultCc: false);
        w.K.Dept["D-FIN"].CostCenterId = null;
        w.K.Touch();
        await w.K.Db.SaveChangesAsync();
        // Lock path: E001's un-accrued bonus of 1,000 is the only line
        var run = await w.K.SeedRunAsync(new[] { E("E001", "BONUS_PERFORMANCE", 1000m, "Bonus") }, Array.Empty<(string, string, string, decimal, bool)>());
        var res = await w.K.ApproveAndLockAsync(run.Id);
        Status(res).Should().Be(422);
        AssertGroupsEqualCsv(Json(res), "S-B3");

        // accrual path: the same refusal at approval, nothing written
        var accrual = await w.TryApproveBonusBatchAsync("E001", 1000m);
        accrual.Should().BeOfType<UnprocessableEntityObjectResult>();
        Json(accrual).GetProperty("error").GetString().Should().Be("cost_centre_required");
        (await w.AllLedgerAsync()).Should().BeEmpty();
    }
}
