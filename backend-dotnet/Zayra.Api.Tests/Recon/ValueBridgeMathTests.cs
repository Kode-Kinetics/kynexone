using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

public class ValueBridgeMathTests
{
    private const string E = ReconSectionKinds.Earning;
    private const string D = ReconSectionKinds.Deduction;
    private const string R = ReconSectionKinds.Employer;

    private static ReconLine L(int emp, string kind, string code, decimal amount, string source = "Salary", string? name = null) =>
        new(emp, kind, code, name ?? code, source, amount);

    private static ValueBridgeResult Build(
        IEnumerable<ReconLine> prior, IEnumerable<ReconLine> current, IDictionary<int, ReconCategory> cats,
        IDictionary<int, ReconSlipWitness>? pw = null, IDictionary<int, ReconSlipWitness>? cw = null,
        ReconThresholds? thresholds = null, IDictionary<string, string>? explanations = null,
        IDictionary<string, ComponentHint>? hints = null) =>
        ValueBridgeMath.Build(new ValueBridgeInput
        {
            PriorLines = prior.ToList(),
            CurrentLines = current.ToList(),
            Categories = new Dictionary<int, ReconCategory>(cats),
            PriorWitness = pw is null ? new Dictionary<int, ReconSlipWitness>() : new Dictionary<int, ReconSlipWitness>(pw),
            CurrentWitness = cw is null ? new Dictionary<int, ReconSlipWitness>() : new Dictionary<int, ReconSlipWitness>(cw),
            Thresholds = thresholds ?? ReconThresholds.Default,
            ItemExplanations = explanations is null ? null : new Dictionary<string, string>(explanations),
            Hints = hints is null ? new Dictionary<string, ComponentHint>() : new Dictionary<string, ComponentHint>(hints),
        });

    private static ReconItemDto Item(ValueBridgeResult r, string kind, string code) =>
        r.Sections.Single(s => s.Kind == kind).Items.Single(i => i.Code == code);

    private static void AssertTies(ReconItemDto i) =>
        (i.Prior + i.Adds.Total + i.Exits.Total + i.Continuing.Total).Should().Be(i.Current, $"I1 for {i.Code}");

    // ── The per-item bridge ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void OneItem_BridgesPriorToCurrent_ThroughAddsExitsAndContinuing()
    {
        var cats = new Dictionary<int, ReconCategory>
        {
            [1] = ReconCategory.Continuing, [2] = ReconCategory.Leaver, [3] = ReconCategory.NewJoiner, [4] = ReconCategory.Rehire,
            [5] = ReconCategory.NotPaid, [6] = ReconCategory.TransferIn, [7] = ReconCategory.TransferOut,
            [8] = ReconCategory.LateJoiner, [9] = ReconCategory.Reinstated, [10] = ReconCategory.Unexplained,
        };
        var prior = new[]
        {
            L(1, E, "BASIC", 1000), L(2, E, "BASIC", 2000), L(5, E, "BASIC", 500), L(7, E, "BASIC", 700), L(10, E, "BASIC", 90),
        };
        var current = new[]
        {
            L(1, E, "BASIC", 1100), L(3, E, "BASIC", 1500), L(4, E, "BASIC", 1200), L(6, E, "BASIC", 800),
            L(8, E, "BASIC", 400), L(9, E, "BASIC", 600),
        };
        var r = Build(prior, current, cats);
        var basic = Item(r, E, "BASIC");

        basic.Prior.Should().Be(4290);
        basic.Adds.NewJoiner.Should().Be(1500);
        basic.Adds.Rehire.Should().Be(1200);
        basic.Adds.TransferIn.Should().Be(800);
        basic.Adds.LateJoiner.Should().Be(400);
        basic.Adds.Reinstated.Should().Be(600);
        basic.Exits.Leaver.Should().Be(-2000);
        basic.Exits.NotPaid.Should().Be(-500);
        basic.Exits.TransferOut.Should().Be(-700);
        basic.Exits.Unexplained.Should().Be(-90);
        basic.Continuing.Package.Should().Be(100);
        basic.Current.Should().Be(5600);
        basic.Delta.Should().Be(1310);
        AssertTies(basic);
        r.Ties.Ok.Should().BeTrue();
        r.Violations.Should().BeEmpty();
    }

    [Fact]
    public void AnItemThatExistsOnlyInTheCurrentPeriod_HasNoPrior_AndItsMovementIsFlagged()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing };
        var r = Build(new[] { L(1, E, "BASIC", 100) },
            new[] { L(1, E, "BASIC", 100), L(1, E, "TELEWORK", 40, "Salary") }, cats,
            thresholds: new ReconThresholds(5, 10, "company"));
        var tele = Item(r, E, "TELEWORK");
        tele.Prior.Should().Be(0);
        tele.Current.Should().Be(40);
        tele.Continuing.Package.Should().Be(40);
        tele.DeltaPct.Should().Be(0);
        tele.Flagged.Should().BeTrue("with no prior only the minimum amount applies, and 40 > 10");
        AssertTies(tele);
    }

    [Fact]
    public void ADroppedItem_AppearsWithZeroCurrent()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing };
        var r = Build(new[] { L(1, E, "BASIC", 100), L(1, E, "CAR", 30) }, new[] { L(1, E, "BASIC", 100) }, cats);
        var car = Item(r, E, "CAR");
        car.Current.Should().Be(0);
        car.Continuing.Package.Should().Be(-30);
        AssertTies(car);
    }

    // ── Drivers ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AContinuingEmployeesMovement_IsAllocatedToTheDriverOfEachLine()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing };
        var prior = new[]
        {
            L(1, E, "OVERTIME", 100, "Overtime"), L(1, E, "BONUS_X", 0, "Bonus"), L(1, E, "ARREARS", 0, "Arrears"),
            L(1, D, "ABSENCE", 20, "Attendance"), L(1, D, "LOAN_EMI", 300, "Loan"), L(1, D, "GOSI-EE", 90, "Statutory"),
            L(1, D, "MYSTERY", 5, "Whatever"),
        };
        var current = new[]
        {
            L(1, E, "OVERTIME", 160, "Overtime"), L(1, E, "BONUS_X", 500, "Bonus"), L(1, E, "ARREARS", 75, "Arrears"),
            L(1, D, "ABSENCE", 50, "Attendance"), L(1, D, "LOAN_EMI", 250, "Loan"), L(1, D, "GOSI-EE", 99, "Statutory"),
            L(1, D, "MYSTERY", 9, "Whatever"),
        };
        var r = Build(prior, current, cats);

        Item(r, E, "OVERTIME").Continuing.Variable.Should().Be(60);
        Item(r, E, "BONUS_X").Continuing.Variable.Should().Be(500);
        Item(r, E, "ARREARS").Continuing.Arrears.Should().Be(75);
        Item(r, D, "ABSENCE").Continuing.Attendance.Should().Be(30);
        Item(r, D, "LOAN_EMI").Continuing.Loans.Should().Be(-50);
        Item(r, D, "GOSI-EE").Continuing.Statutory.Should().Be(9);
        var mystery = Item(r, D, "MYSTERY");
        mystery.Continuing.Other.Should().Be(4);
        mystery.Flagged.Should().BeTrue("an unclassified movement must be explained whatever its size");
        foreach (var s in r.Sections) foreach (var i in s.Items) AssertTies(i);
    }

    [Fact]
    public void WhenTheSameCodeMovesBetweenSources_EachSourceKeepsItsOwnShare_AndTheItemStillTies()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing };
        // Same code, paid as ordinary salary last month and as arrears this month.
        var r = Build(new[] { L(1, E, "BASIC", 1000, "Salary") },
            new[] { L(1, E, "BASIC", 900, "Salary"), L(1, E, "BASIC", 300, "Arrears") }, cats);
        var basic = Item(r, E, "BASIC");
        basic.Continuing.Package.Should().Be(-100);
        basic.Continuing.Arrears.Should().Be(300);
        basic.Current.Should().Be(1200);
        AssertTies(basic);
    }

    // ── Package versus proration ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ForBasicHousingAndTransport_PackageIsTheChangeInTheFullPackageAtThisMonthsFactor_AndProrationIsTheResidual()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing };
        var pw = new Dictionary<int, ReconSlipWitness> { [1] = new(1, 1000m, 250m, 100m, 1m) };
        var cw = new Dictionary<int, ReconSlipWitness> { [1] = new(1, 1200m, 250m, 100m, 0.5m) };
        // Paid 1000 last month; this month the package rose to 1200 but only half the month was paid: 600.
        var r = Build(new[] { L(1, E, "BASIC", 1000) }, new[] { L(1, E, "BASIC", 600) }, cats, pw, cw);
        var basic = Item(r, E, "BASIC");
        basic.Delta.Should().Be(-400);
        basic.Continuing.Package.Should().Be(100);       // (1200 - 1000) x 0.5
        basic.Continuing.Proration.Should().Be(-500);    // the residual, so it ties exactly
        AssertTies(basic);
    }

    [Fact]
    public void Housing_AndTransport_UseTheirOwnFullAmounts()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing };
        var pw = new Dictionary<int, ReconSlipWitness> { [1] = new(1, 1000m, 250m, 100m, 1m) };
        var cw = new Dictionary<int, ReconSlipWitness> { [1] = new(1, 1000m, 300m, 120m, 0.8m) };
        var r = Build(
            new[] { L(1, E, "HOUSING", 250), L(1, E, "TRANSPORT", 100) },
            new[] { L(1, E, "HOUSING", 240), L(1, E, "TRANSPORT", 96) }, cats, pw, cw);
        Item(r, E, "HOUSING").Continuing.Package.Should().Be(40);     // (300 - 250) x 0.8
        Item(r, E, "HOUSING").Continuing.Proration.Should().Be(-50);
        Item(r, E, "TRANSPORT").Continuing.Package.Should().Be(16);   // (120 - 100) x 0.8
        Item(r, E, "TRANSPORT").Continuing.Proration.Should().Be(-20);
        foreach (var i in r.Sections.SelectMany(s => s.Items)) AssertTies(i);
    }

    [Fact]
    public void WithoutFullAmountWitnesses_TheWholeMovementIsPackage()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing };
        var r = Build(new[] { L(1, E, "BASIC", 1000) }, new[] { L(1, E, "BASIC", 1100) }, cats);
        var basic = Item(r, E, "BASIC");
        basic.Continuing.Package.Should().Be(100);
        basic.Continuing.Proration.Should().Be(0);
    }

    [Fact]
    public void ANullFullAmountOnOneSide_FallsBackToThePaidAmountAndAFactorOfOne()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing };
        var pw = new Dictionary<int, ReconSlipWitness> { [1] = new(1, null, null, null, null) };
        var cw = new Dictionary<int, ReconSlipWitness> { [1] = new(1, 1200m, null, null, 0.5m) };
        var r = Build(new[] { L(1, E, "BASIC", 1000) }, new[] { L(1, E, "BASIC", 600) }, cats, pw, cw);
        var basic = Item(r, E, "BASIC");
        // full prior = 1000 (the paid amount), factor prior 1; full current = 1200 at 0.5.
        basic.Continuing.Package.Should().Be(100);
        basic.Continuing.Proration.Should().Be(-500);
        AssertTies(basic);
    }

    [Theory]
    [InlineData(1.0, 1.0, 50, 0)]       // same factor: the change is package
    [InlineData(1.0, 0.5, 0, 50)]       // factors differ: all of it is proration
    public void OtherPackageCodes_AreWhollyPackage_WhenTheFactorIsUnchanged_AndWhollyProrationWhenItIsNot(
        double priorFactor, double currentFactor, double expectedPackage, double expectedProration)
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing };
        var pw = new Dictionary<int, ReconSlipWitness> { [1] = new(1, 1000m, 250m, 100m, (decimal)priorFactor) };
        var cw = new Dictionary<int, ReconSlipWitness> { [1] = new(1, 1000m, 250m, 100m, (decimal)currentFactor) };
        var r = Build(new[] { L(1, E, "OTHER_ALLOWANCES", 100) }, new[] { L(1, E, "OTHER_ALLOWANCES", 150) }, cats, pw, cw);
        var item = Item(r, E, "OTHER_ALLOWANCES");
        item.Continuing.Package.Should().Be((decimal)expectedPackage);
        item.Continuing.Proration.Should().Be((decimal)expectedProration);
        AssertTies(item);
    }

    [Fact]
    public void AVariableLine_IsNeverSplitIntoProration_EvenWhenTheFactorChanges()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing };
        var pw = new Dictionary<int, ReconSlipWitness> { [1] = new(1, 1000m, 0m, 0m, 1m) };
        var cw = new Dictionary<int, ReconSlipWitness> { [1] = new(1, 1000m, 0m, 0m, 0.5m) };
        var r = Build(new[] { L(1, E, "OVERTIME", 100, "Overtime") }, new[] { L(1, E, "OVERTIME", 150, "Overtime") }, cats, pw, cw);
        var ot = Item(r, E, "OVERTIME");
        ot.Continuing.Variable.Should().Be(50);
        ot.Continuing.Proration.Should().Be(0);
    }

    // ── Headline and employer cost ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheHeadline_IsGrossDeductionsNet_AndEmployerCostNeverEntersNet()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing, [2] = ReconCategory.NewJoiner, [3] = ReconCategory.Leaver };
        var prior = new[]
        {
            L(1, E, "BASIC", 1000), L(1, D, "GOSI-EE", 90, "Statutory"), L(1, R, "GOSI-ER", 110, "Statutory"),
            L(3, E, "BASIC", 800), L(3, D, "GOSI-EE", 72, "Statutory"), L(3, R, "GOSI-ER", 88, "Statutory"),
        };
        var current = new[]
        {
            L(1, E, "BASIC", 1100), L(1, D, "GOSI-EE", 99, "Statutory"), L(1, R, "GOSI-ER", 121, "Statutory"),
            L(2, E, "BASIC", 600), L(2, D, "GOSI-EE", 54, "Statutory"), L(2, R, "GOSI-ER", 66, "Statutory"),
        };
        var r = Build(prior, current, cats);

        r.Headline.Gross.Prior.Should().Be(1800);
        r.Headline.Gross.Current.Should().Be(1700);
        r.Headline.Deductions.Prior.Should().Be(162);
        r.Headline.Deductions.Current.Should().Be(153);
        r.Headline.Net.Prior.Should().Be(1638);
        r.Headline.Net.Current.Should().Be(1547);
        r.Headline.Employer.Prior.Should().Be(198);
        r.Headline.Employer.Current.Should().Be(187);

        // Net is gross minus employee deductions in every column of the bridge, employer cost in none of them.
        var g = r.Headline.Gross; var d = r.Headline.Deductions; var n = r.Headline.Net;
        n.Adds.NewJoiner.Should().Be(g.Adds.NewJoiner - d.Adds.NewJoiner);
        n.Exits.Leaver.Should().Be(g.Exits.Leaver - d.Exits.Leaver);
        n.Continuing.Statutory.Should().Be(g.Continuing.Statutory - d.Continuing.Statutory);
        n.Continuing.Package.Should().Be(g.Continuing.Package - d.Continuing.Package);
        foreach (var h in new[] { r.Headline.Gross, r.Headline.Deductions, r.Headline.Net, r.Headline.Employer }) AssertTies(h);
        r.Sections.Single(s => s.Kind == R).Items.Should().ContainSingle(i => i.Code == "GOSI-ER");
        r.Sections.Single(s => s.Kind == E).Items.Should().NotContain(i => i.Code == "GOSI-ER");
    }

    [Fact]
    public void I2_TheSectionsAddUpToTheHeadline_ColumnByColumn()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing, [2] = ReconCategory.Leaver, [3] = ReconCategory.Rehire };
        var prior = new[] { L(1, E, "BASIC", 1000), L(1, E, "HOUSING", 250), L(2, E, "BASIC", 900), L(2, E, "HOUSING", 225) };
        var current = new[] { L(1, E, "BASIC", 1000), L(1, E, "HOUSING", 275), L(3, E, "BASIC", 700), L(3, E, "HOUSING", 175) };
        var r = Build(prior, current, cats);
        var earnings = r.Sections.Single(s => s.Kind == E).Items;
        r.Headline.Gross.Prior.Should().Be(earnings.Sum(i => i.Prior));
        r.Headline.Gross.Adds.Rehire.Should().Be(earnings.Sum(i => i.Adds.Rehire));
        r.Headline.Gross.Exits.Leaver.Should().Be(earnings.Sum(i => i.Exits.Leaver));
        r.Headline.Gross.Continuing.Package.Should().Be(earnings.Sum(i => i.Continuing.Package));
        r.Headline.Gross.Current.Should().Be(earnings.Sum(i => i.Current));
    }

    // ── Labels, flags, explanations ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheLabel_IsTheShortestNameSeenForTheCode_WithATieBrokenAlphabetically()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing, [2] = ReconCategory.Continuing };
        var r = Build(
            new[] { L(1, E, "OVERTIME", 10, "Overtime", "Overtime (12.00 h x 1.50)"), L(2, E, "OVERTIME", 10, "Overtime", "Overtime") },
            new[] { L(1, E, "OVERTIME", 10, "Overtime", "Overtime (8.00 h x 1.50)"), L(2, E, "OVERTIME", 10, "Overtime", "Overtime") }, cats);
        Item(r, E, "OVERTIME").Label.Should().Be("Overtime");
    }

    [Fact]
    public void AnItemOverTheThreshold_IsFlagged_AndCarriesItsExplanation()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing };
        var r = Build(new[] { L(1, E, "BASIC", 1000), L(1, E, "HOUSING", 250) }, new[] { L(1, E, "BASIC", 1100), L(1, E, "HOUSING", 252) }, cats,
            explanations: new Dictionary<string, string> { ["BASIC"] = "Annual increment", ["HOUSING"] = "ignored: not flagged" });
        var basic = Item(r, E, "BASIC");
        basic.Flagged.Should().BeTrue();
        basic.Explanation.Should().Be("Annual increment");
        basic.DeltaPct.Should().Be(10m);
        var housing = Item(r, E, "HOUSING");
        housing.Flagged.Should().BeFalse();
        housing.Explanation.Should().Be("ignored: not flagged", "an explanation given for an unflagged item is still shown");
    }

    // ── Integrity ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALineForAnEmployeeWithNoCategory_IsReportedAsAViolation_NotSilentlyDropped()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing };
        var r = Build(new[] { L(1, E, "BASIC", 100) }, new[] { L(1, E, "BASIC", 100), L(99, E, "BASIC", 50) }, cats);
        r.Ties.Ok.Should().BeFalse();
        r.Ties.Difference.Should().Be(50);
        r.Violations.Should().Contain(v => v.Contains("99"));
    }

    [Fact]
    public void ACurrentLineForAnEmployeeCategorisedAsLeft_BreaksTheBridge_AndSaysSo()
    {
        var cats = new Dictionary<int, ReconCategory> { [2] = ReconCategory.Leaver };
        var r = Build(new[] { L(2, E, "BASIC", 100) }, new[] { L(2, E, "BASIC", 40) }, cats);
        r.Ties.Ok.Should().BeFalse();
        r.Violations.Should().NotBeEmpty();
    }

    [Fact]
    public void Cells_RecordEveryEmployeesShareOfEveryBucket_AndSumBackToTheItem()
    {
        var cats = new Dictionary<int, ReconCategory> { [1] = ReconCategory.Continuing, [2] = ReconCategory.Leaver, [3] = ReconCategory.NewJoiner };
        var r = Build(new[] { L(1, E, "BASIC", 1000), L(2, E, "BASIC", 900) }, new[] { L(1, E, "BASIC", 1050), L(3, E, "BASIC", 700) }, cats);
        var cells = r.Cells.Where(c => c.Code == "BASIC" && c.Kind == E).ToList();
        cells.Should().Contain(c => c.EmployeeId == 2 && c.Group == "exits" && c.Bucket == "leaver" && c.Amount == -900);
        cells.Should().Contain(c => c.EmployeeId == 3 && c.Group == "adds" && c.Bucket == "newJoiner" && c.Amount == 700);
        cells.Should().Contain(c => c.EmployeeId == 1 && c.Group == "continuing" && c.Bucket == "package" && c.Amount == 50);
        cells.Sum(c => c.Amount).Should().Be(Item(r, E, "BASIC").Delta);
    }

    // ── Property ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(2026)]
    [InlineData(99991)]
    public void Property_EveryItemAndEveryHeadlineColumnTiesToZeroPointZeroZero(int seed)
    {
        var rng = new Random(seed);
        string[] sources = { "Salary", "Overtime", "Bonus", "Adjustment", "Attendance", "Leave", "Loan", "Recovery", "Statutory", "Tax", "Arrears", "Mystery" };
        string[] earnCodes = { "BASIC", "HOUSING", "TRANSPORT", "OTHER_ALLOWANCES", "OVERTIME", "BONUS_A", "ADJ_B", "ARREARS" };
        string[] dedCodes = { "ABSENCE", "LOAN_EMI", "GOSI-EE", "INCOME_TAX", "RECOVERY", "MYSTERY" };
        string[] erCodes = { "GOSI-ER", "GOSI-OH-ER" };
        decimal Money() => Math.Round((decimal)(rng.NextDouble() * 5000), 2);

        for (var round = 0; round < 60; round++)
        {
            var cats = new Dictionary<int, ReconCategory>();
            var prior = new List<ReconLine>();
            var current = new List<ReconLine>();
            var pw = new Dictionary<int, ReconSlipWitness>();
            var cw = new Dictionary<int, ReconSlipWitness>();
            for (var emp = 1; emp <= rng.Next(1, 40); emp++)
            {
                var inPrior = rng.Next(2) == 0;
                var inCurrent = rng.Next(2) == 0;
                if (!inPrior && !inCurrent) inCurrent = true;
                ReconCategory cat;
                if (inPrior && inCurrent) cat = ReconCategory.Continuing;
                else if (inCurrent) cat = ReconCategories.Adds[rng.Next(ReconCategories.Adds.Count)];
                else cat = ReconCategories.Exits[rng.Next(ReconCategories.Exits.Count)];
                cats[emp] = cat;

                void Lines(List<ReconLine> into, Dictionary<int, ReconSlipWitness> w)
                {
                    foreach (var c in earnCodes) if (rng.Next(3) != 0) into.Add(L(emp, E, c, Money(), sources[rng.Next(sources.Length)]));
                    foreach (var c in dedCodes) if (rng.Next(3) != 0) into.Add(L(emp, D, c, Money(), sources[rng.Next(sources.Length)]));
                    foreach (var c in erCodes) if (rng.Next(3) != 0) into.Add(L(emp, R, c, Money(), "Statutory"));
                    w[emp] = new ReconSlipWitness(emp,
                        rng.Next(4) == 0 ? null : Money(), rng.Next(4) == 0 ? null : Money(), rng.Next(4) == 0 ? null : Money(),
                        rng.Next(4) == 0 ? null : Math.Round((decimal)rng.NextDouble(), 6));
                }
                if (inPrior) Lines(prior, pw);
                if (inCurrent) Lines(current, cw);
            }

            var r = Build(prior, current, cats, pw, cw,
                new ReconThresholds(rng.Next(1, 20), rng.Next(0, 500), "company"));

            r.Violations.Should().BeEmpty($"seed {seed} round {round}");
            r.Ties.Ok.Should().BeTrue();
            r.Ties.Difference.Should().Be(0m);
            foreach (var i in r.Sections.SelectMany(s => s.Items))
            {
                AssertTies(i);
                (i.Current - i.Prior).Should().Be(i.Delta);
            }
            foreach (var h in new[] { r.Headline.Gross, r.Headline.Deductions, r.Headline.Net, r.Headline.Employer }) AssertTies(h);
            r.Headline.Net.Current.Should().Be(r.Headline.Gross.Current - r.Headline.Deductions.Current);
            r.Headline.Net.Prior.Should().Be(r.Headline.Gross.Prior - r.Headline.Deductions.Prior);
            r.Headline.Gross.Current.Should().Be(r.Sections.Single(s => s.Kind == E).Items.Sum(i => i.Current));
            r.Headline.Deductions.Current.Should().Be(r.Sections.Single(s => s.Kind == D).Items.Sum(i => i.Current));
            r.Headline.Employer.Current.Should().Be(r.Sections.Single(s => s.Kind == R).Items.Sum(i => i.Current));
            // Cells reproduce the item deltas to the penny.
            foreach (var g in r.Cells.GroupBy(c => (c.Kind, c.Code)))
                g.Sum(c => c.Amount).Should().Be(Item(r, g.Key.Kind, g.Key.Code).Delta);
        }
    }
}
