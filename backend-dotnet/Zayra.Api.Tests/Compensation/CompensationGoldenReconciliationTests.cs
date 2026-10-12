using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Compensation;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Compensation.MeritTestKit;

namespace Zayra.Api.Tests.Compensation;

/// <summary>
/// CMP-01/02/04/05 golden budget reconciliation on real PostgreSQL. The expected table below was computed by hand
/// (CMP_DESIGN.md §Worked examples reproduces it line by line) from the MeritTestKit constants — it does not call
/// MeritMath, so a formula change that moves any figure fails here.
///
/// Rules applied by hand: compa = gross ÷ grade mid (4 dp); range position = (gross − min) ÷ (max − min) (4 dp);
/// increase from % = round(basic_SAR × % ÷ 100, 2); prorated = round(increase × factor, 2), factor = round(184/365, 4)
/// = 0.5041 for the May joiner; pay-currency award = round(prorated ÷ rate, 2) at AED 1.02; pool = round(Σ eligible
/// basic_SAR × 4 %, 2) for Engineering and a fixed 1,500.00 for Operations. Half away from zero everywhere.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CompensationGoldenReconciliationTests(PostgresFixture fx)
{
    // code, proposed SAR, %, prorated SAR, prorated pay, new basic (pay ccy), new gross (pay ccy), compa, range pos, new compa, factor, out-of-guideline
    private static readonly (string Code, decimal Proposed, decimal Pct, decimal Prorated, decimal ProratedPay, decimal NewBasic, decimal NewGross,
        decimal Compa, decimal RangePos, decimal NewCompa, decimal Factor, bool Out)[] Expected =
    [
        ("A1", 360.00m, 6.0000m, 360.00m, 360.00m, 6360.00m, 8360.00m, 0.8000m, 0.0000m, 0.8360m, 1.0000m, false),
        ("A2", 210.00m, 3.0000m, 210.00m, 210.00m, 7210.00m, 9460.00m, 0.9250m, 0.3125m, 0.9460m, 1.0000m, false),
        ("A3", 400.00m, 5.0000m, 400.00m, 400.00m, 8400.00m, 10900.00m, 1.0500m, 0.6250m, 1.0900m, 1.0000m, true),
        ("A4", 390.00m, 6.0000m, 390.00m, 390.00m, 6890.00m, 9015.00m, 0.8625m, 0.1563m, 0.9015m, 1.0000m, false),
        ("A5", 120.00m, 2.0000m, 60.49m, 60.49m, 6060.49m, 8060.49m, 0.8000m, 0.0000m, 0.8060m, 0.5041m, false),
        ("A6", 183.60m, 3.0000m, 183.60m, 180.00m, 6180.00m, 8180.00m, 0.8160m, 0.0400m, 0.8344m, 1.0000m, false),
        ("B1", 360.00m, 4.0000m, 360.00m, 360.00m, 9360.00m, 12360.00m, 0.8000m, 0.0000m, 0.8240m, 1.0000m, false),
        ("B2", 200.00m, 2.0000m, 200.00m, 200.00m, 10200.00m, 13700.00m, 0.9000m, 0.2500m, 0.9133m, 1.0000m, false),
        ("B3", 187.50m, 2.5000m, 187.50m, 187.50m, 7687.50m, 10062.50m, 0.9875m, 0.4688m, 1.0063m, 1.0000m, false),
        ("B4", 500.00m, 8.3333m, 500.00m, 500.00m, 6500.00m, 8000.00m, 0.7500m, -0.1250m, 0.8000m, 1.0000m, true),
        ("B5", 0.00m, 0.0000m, 0.00m, 0.00m, 6800.00m, 9000.00m, 0.9000m, 0.2500m, 0.9000m, 1.0000m, false),
        ("B6", 72.00m, 1.0000m, 72.00m, 72.00m, 7272.00m, 9572.00m, 0.9500m, 0.3750m, 0.9572m, 1.0000m, false),
    ];

    [Fact]
    public async Task GoldenCycle_EveryLineBudgetAndTotal_ReconcilesToTheHandComputedTable()
    {
        var w = await SeedAsync(fx);

        // CMP-04: a missing FX rate BLOCKS planning instead of being guessed.
        var draft = await ConfiguredCycleAsync(fx, w, withFx: false);
        draft.Blockers.Should().ContainSingle(b => b.Code == "fx_missing").Which.Message.Should().Contain("AED");
        await using (var db = fx.CreateDb())
        {
            var refused = () => Service(db).OpenPlanningAsync(Hr(w), draft.Id, new CompCycleTransitionRequest(draft.Version), default);
            (await refused.Should().ThrowAsync<CompRuleException>()).Which.Code.Should().Be("planning_blocked");
        }

        var (cycle, a, b) = await SubmittedAsync(fx, w);
        cycle.Currency.Should().Be("SAR");
        // CMP-01 eligibility: 12 eligible; A7 joined after the cutoff, G3 (managers, HR, Finance) excluded.
        var view = await HrViewAsync(w, cycle.Id);
        view.Ineligible.Should().Contain(x => x.EmployeeId == w.E["A7"] && x.Reason.Contains("hire-date cutoff"));
        view.Ineligible.Where(x => x.Reason.Contains("Grade is excluded")).Should().HaveCount(5);
        view.Totals.EligibleCount.Should().Be(12);

        // Every line, exactly.
        var lines = a.Lines.Concat(b.Lines).ToDictionary(l => l.EmployeeId);
        lines.Should().HaveCount(12);
        foreach (var x in Expected)
        {
            var l = lines[w.E[x.Code]];
            (l.ProposedIncrease, l.ProposedPercent, l.ProratedIncrease, l.ProratedIncreasePay, l.NewBasic, l.NewGross)
                .Should().Be((x.Proposed, x.Pct, x.Prorated, x.ProratedPay, x.NewBasic, x.NewGross), x.Code);
            (l.CompaRatio, l.RangePosition, l.NewCompaRatio, l.ProrationFactor, l.OutOfGuideline)
                .Should().Be((x.Compa, x.RangePos, x.NewCompa, x.Factor, x.Out), x.Code);
        }
        // A5: 184 of 365 eligible days; A6: AED basis normalised at the cycle rate.
        lines[w.E["A5"]].EligibleDays.Should().Be(184);
        lines[w.E["A5"]].Flags.Should().Contain(CompFlags.Prorated);
        lines[w.E["A6"]].Should().Match<CompLineDto>(l => l.PayCurrency == "AED" && l.FxRate == 1.02m && l.CurrentBasicCycle == 6120.00m && l.CurrentGrossCycle == 8160.00m);
        lines[w.E["A3"]].GuidelineMaxPercent.Should().Be(4m);

        // Budgets: Engineering 4 % of 39,620.00 = 1,584.80; proposed 1,604.09 → over by 19.29 (101.22 %).
        var eng = view.Budgets.Single(x => x.DepartmentId == w.Eng);
        (eng.EligibleCount, eng.EligibleBasicCycle, eng.Pool, eng.Proposed, eng.Remaining, eng.PercentUsed, eng.Overspent)
            .Should().Be((6, 39620.00m, 1584.80m, 1604.09m, -19.29m, 101.22m, true));
        // Operations fixed 1,500.00; proposed 1,319.50 → 180.50 left (87.97 %).
        var ops = view.Budgets.Single(x => x.DepartmentId == w.Ops);
        (ops.EligibleCount, ops.EligibleBasicCycle, ops.Pool, ops.Proposed, ops.Remaining, ops.PercentUsed, ops.Overspent)
            .Should().Be((6, 46500.00m, 1500.00m, 1319.50m, 180.50m, 87.97m, false));
        // Totals reconcile to the departments and to the lines (2,923.59 = Σ prorated).
        view.Totals.Should().Be(new CompTotalsDto(12, 86120.00m, 3084.80m, 2923.59m, 161.21m, 94.77m, 11));
        Expected.Sum(x => x.Prorated).Should().Be(view.Totals.Proposed);
        view.Worksheets.Single(s => s.ManagerEmployeeId == w.ManagerA && s.IsPrimary).Overspend.Should().BeTrue();

        // Persisted rows equal what was shown (no recompute drift between screen and database).
        await using var verify = fx.CreateDb();
        var stored = await verify.CompWorksheetLines.Where(l => l.CycleId == cycle.Id).ToListAsync();
        stored.Sum(l => l.ProratedIncrease).Should().Be(2923.59m);
        stored.Single(l => l.EmployeeId == w.E["A6"]).ProratedIncreasePay.Should().Be(180.00m);
    }

    [Fact]
    public async Task Overspend_IsRefusedWithoutJustification_AndFlaggedToApproversWithIt()
    {
        var w = await SeedAsync(fx);
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        var sheet = await WorksheetOfAsync(fx, w, cycle.Id);
        await using var db = fx.CreateDb();
        var svc = Service(db);
        sheet = await svc.SaveWorksheetAsync(Manager(w), sheet.Id, new SaveCompWorksheetRequest(sheet.Version, null, ProposalsA(w)), default);
        sheet.Overspend.Should().BeTrue();
        sheet.CanSubmit.Should().BeFalse();
        var refused = await FluentActions.Invoking(() => svc.SubmitWorksheetAsync(Manager(w), sheet.Id, new SubmitCompWorksheetRequest(sheet.Version), default))
            .Should().ThrowAsync<CompRuleException>();
        refused.Which.Blockers.Should().ContainSingle(x => x.Code == "overspend_justification_required").Which.Message.Should().Contain("1,604.09 of 1,584.80 SAR");

        sheet = await svc.SaveWorksheetAsync(Manager(w), sheet.Id, new SaveCompWorksheetRequest(sheet.Version, "Retention plan agreed with the CTO.", ProposalsA(w)), default);
        sheet = await svc.SubmitWorksheetAsync(Manager(w), sheet.Id, new SubmitCompWorksheetRequest(sheet.Version), default);
        sheet.Status.Should().Be(CompWorksheetStatuses.Submitted);
        var approval = await db.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == sheet.ApprovalRequestId);
        approval.Title.Should().Contain("OVER BUDGET");
        approval.Priority.Should().Be("High");
        approval.EntityName.Should().Be(CompensationApprovalSync.EntityName);
        approval.RequestedForEmployeeId.Should().Be(w.ManagerA, "the manager is the subject, so the engine's subject bar applies to them");
    }

    [Fact]
    public async Task OutOfGuideline_NeedsAJustification()
    {
        var w = await SeedAsync(fx);
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        var sheet = await WorksheetOfAsync(fx, w, cycle.Id, managerB: true);
        await using var db = fx.CreateDb();
        var svc = Service(db);
        var noReason = ProposalsB(w).Select(l => l.EmployeeId == w.E["B4"] ? l with { Justification = null } : l).ToList();
        sheet = await svc.SaveWorksheetAsync(Manager(w, true), sheet.Id, new SaveCompWorksheetRequest(sheet.Version, null, noReason), default);
        sheet.Lines.Single(l => l.EmployeeId == w.E["B4"]).OutOfGuideline.Should().BeTrue();
        var refused = await FluentActions.Invoking(() => svc.SubmitWorksheetAsync(Manager(w, true), sheet.Id, new SubmitCompWorksheetRequest(sheet.Version), default))
            .Should().ThrowAsync<CompRuleException>();
        refused.Which.Blockers.Should().Contain(x => x.Code == "justification_required" && x.Message.Contains("8.33 %"));
    }

    [Fact]
    public async Task AnAwardTheSalaryRecordWouldRefuse_IsBlockedAtPlanning_NotDiscoveredOnTheFirst()
    {
        var w = await SeedAsync(fx);
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        var sheet = await WorksheetOfAsync(fx, w, cycle.Id, managerB: true);
        await using var db = fx.CreateDb();
        var svc = Service(db);
        // B4 gross 7,500 is below G1's 8,000 minimum: a 6 % award (360) still leaves 7,860 → the writer refuses it.
        var low = ProposalsB(w).Select(l => l.EmployeeId == w.E["B4"] ? new CompLineInput(l.EmployeeId, null, 6m, null) : l).ToList();
        sheet = await svc.SaveWorksheetAsync(Manager(w, true), sheet.Id, new SaveCompWorksheetRequest(sheet.Version, null, low), default);
        var b4 = sheet.Lines.Single(l => l.EmployeeId == w.E["B4"]);
        b4.Flags.Should().Contain(CompFlags.BelowRangeMin);
        b4.BlockingMessages.Should().ContainSingle().Which.Should().Contain("below grade G1's minimum 8,000.00");
        sheet.CanSubmit.Should().BeFalse();
    }

    private async Task<CompCycleDto> HrViewAsync(MeritWorld w, Guid cycleId)
    {
        await using var db = fx.CreateDb();
        return await Service(db).GetCycleAsync(Hr(w), cycleId, default);
    }
}
