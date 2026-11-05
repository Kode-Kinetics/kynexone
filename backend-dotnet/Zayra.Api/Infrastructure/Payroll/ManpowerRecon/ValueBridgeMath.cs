namespace Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

/// <summary>One payroll line (an earning, an employee deduction or an employer cost) of one employee in one run.</summary>
public sealed record ReconLine(int EmployeeId, string Kind, string Code, string Name, string Source, decimal Amount);

/// <summary>The proration witnesses a slip carries; every value is optional (a slip processed before they existed has none).</summary>
public sealed record ReconSlipWitness(
    int EmployeeId, decimal? FullBasic, decimal? FullHousing, decimal? FullTransport, decimal? ProrationFactor);

/// <summary>One employee's share of one bucket of one item. The drill-down and the bridge are built from the same cells.</summary>
public sealed record ReconCell(int EmployeeId, string Kind, string Code, string Group, string Bucket, decimal Amount);

public sealed class ValueBridgeInput
{
    public IReadOnlyList<ReconLine> PriorLines { get; init; } = Array.Empty<ReconLine>();
    public IReadOnlyList<ReconLine> CurrentLines { get; init; } = Array.Empty<ReconLine>();
    /// <summary>The movement category of every employee who has a slip in either run.</summary>
    public IReadOnlyDictionary<int, ReconCategory> Categories { get; init; } = new Dictionary<int, ReconCategory>();
    public IReadOnlyDictionary<int, ReconSlipWitness> PriorWitness { get; init; } = new Dictionary<int, ReconSlipWitness>();
    public IReadOnlyDictionary<int, ReconSlipWitness> CurrentWitness { get; init; } = new Dictionary<int, ReconSlipWitness>();
    /// <summary>Catalogue facts by component code, used when a line's Source alone does not decide its driver.</summary>
    public IReadOnlyDictionary<string, ComponentHint> Hints { get; init; } = new Dictionary<string, ComponentHint>();
    public ReconThresholds Thresholds { get; init; } = ReconThresholds.Default;
    /// <summary>Written explanations by item key, <c>Kind:CODE</c> (see <see cref="ReconItemDto.ExplanationKey"/>).</summary>
    public IReadOnlyDictionary<string, string>? ItemExplanations { get; init; }
}

public sealed record ValueBridgeResult(
    IReadOnlyList<ReconValueSectionDto> Sections,
    ReconHeadlineDto Headline,
    ReconTie Ties,
    IReadOnlyList<ReconCell> Cells,
    IReadOnlyList<string> Violations);

/// <summary>
/// The value bridge: for every item (kind + component code),
/// <c>prior + adds + exits + continuing = current</c>, to the penny, where
/// adds are the current amounts of employees new to the run, exits are minus the prior amounts of employees who are
/// gone from it, and continuing is the change for employees in both, split by what drove it. Built from the payslip
/// LINES; the line-versus-register difference is a separate row (a reconciliation that quietly used the other source
/// would hide it).
/// </summary>
public static class ValueBridgeMath
{
    private const string FullBasicCode = "BASIC";
    private const string FullHousingCode = "HOUSING";
    private const string FullTransportCode = "TRANSPORT";

    private static readonly string[] EarningPriority = { "BASIC", "HOUSING", "TRANSPORT", "OTHER_ALLOWANCES" };

    public static ValueBridgeResult Build(ValueBridgeInput input)
    {
        var violations = new List<string>();
        var cells = new List<ReconCell>();
        var flaggedUncategorised = new HashSet<int>();

        var priorByKey = input.PriorLines.GroupBy(l => (l.Kind, l.Code)).ToDictionary(g => g.Key, g => g.ToList());
        var currentByKey = input.CurrentLines.GroupBy(l => (l.Kind, l.Code)).ToDictionary(g => g.Key, g => g.ToList());
        var keys = priorByKey.Keys.Concat(currentByKey.Keys).Distinct().ToList();

        var itemsByKind = new Dictionary<string, List<ReconItemDto>>
        {
            [ReconSectionKinds.Earning] = new(), [ReconSectionKinds.Deduction] = new(), [ReconSectionKinds.Employer] = new(),
        };
        decimal maxResidual = 0m;

        foreach (var key in keys)
        {
            var prior = priorByKey.GetValueOrDefault(key) ?? new List<ReconLine>();
            var current = currentByKey.GetValueOrDefault(key) ?? new List<ReconLine>();
            var itemCells = new List<ReconCell>();

            var employees = prior.Select(l => l.EmployeeId).Concat(current.Select(l => l.EmployeeId)).Distinct().OrderBy(x => x);
            foreach (var emp in employees)
            {
                if (!input.Categories.TryGetValue(emp, out var category))
                {
                    if (flaggedUncategorised.Add(emp))
                        violations.Add($"Employee {emp} has payroll lines but no slip in either run, so no movement category.");
                    continue;
                }
                var p = prior.Where(l => l.EmployeeId == emp).ToList();
                var c = current.Where(l => l.EmployeeId == emp).ToList();
                AllocateEmployee(input, key.Kind, key.Code, emp, category, p, c, itemCells);
            }

            var priorTotal = prior.Sum(l => l.Amount);
            var currentTotal = current.Sum(l => l.Amount);
            var item = BuildItem(key.Kind, key.Code, Label(prior, current, key.Code), priorTotal, currentTotal, itemCells, input);
            var residual = item.Prior + item.Adds.Total + item.Exits.Total + item.Continuing.Total - item.Current;
            if (residual != 0m)
            {
                violations.Add($"{key.Kind} {key.Code}: prior + adds + exits + continuing differs from current by {residual:0.00}.");
                maxResidual = Math.Max(maxResidual, Math.Abs(residual));
            }
            itemsByKind[key.Kind].Add(item);
            cells.AddRange(itemCells);
        }

        var sections = itemsByKind.Select(kv => new ReconValueSectionDto(kv.Key, Order(kv.Key, kv.Value))).ToList();
        var gross = Sum(sections, ReconSectionKinds.Earning);
        var deductions = Sum(sections, ReconSectionKinds.Deduction);
        var employer = Sum(sections, ReconSectionKinds.Employer);
        var net = Net(gross, deductions);

        return new ValueBridgeResult(
            sections, new ReconHeadlineDto(gross, deductions, net, employer),
            new ReconTie(violations.Count == 0, maxResidual), cells, violations);
    }

    /// <summary>
    /// With no comparison run there is no bridge: each item shows what it is this month (prior zero, the bridge columns zero,
    /// delta equal to current, nothing flagged), so a first run is still reviewable without inventing movements.
    /// </summary>
    public static ValueBridgeResult Composition(IReadOnlyList<ReconLine> current)
    {
        var zeroAdds = new ReconAddsDto(0, 0, 0, 0, 0);
        var zeroExits = new ReconExitsDto(0, 0, 0, 0);
        var zeroCont = new ReconContinuingDto(0, 0, 0, 0, 0, 0, 0, 0);
        var itemsByKind = new Dictionary<string, List<ReconItemDto>>
        {
            [ReconSectionKinds.Earning] = new(), [ReconSectionKinds.Deduction] = new(), [ReconSectionKinds.Employer] = new(),
        };
        foreach (var g in current.GroupBy(l => (l.Kind, l.Code)))
        {
            var lines = g.ToList();
            var total = lines.Sum(l => l.Amount);
            itemsByKind[g.Key.Kind].Add(new ReconItemDto(g.Key.Code, Label(new List<ReconLine>(), lines, g.Key.Code), 0m,
                zeroAdds, zeroExits, zeroCont, total, total, null, false, null));
        }
        var sections = itemsByKind.Select(kv => new ReconValueSectionDto(kv.Key, Order(kv.Key, kv.Value))).ToList();
        ReconBridgeRowDto Total(string kind)
        {
            var current2 = sections.Single(s => s.Kind == kind).Items.Sum(i => i.Current);
            return new ReconBridgeRowDto(0m, zeroAdds, zeroExits, zeroCont, current2, current2, null);
        }
        var gross = Total(ReconSectionKinds.Earning);
        var deductions = Total(ReconSectionKinds.Deduction);
        var net = new ReconBridgeRowDto(0m, zeroAdds, zeroExits, zeroCont, gross.Current - deductions.Current, gross.Current - deductions.Current, null);
        return new ValueBridgeResult(sections, new ReconHeadlineDto(gross, deductions, net, Total(ReconSectionKinds.Employer)),
            new ReconTie(true, 0m), Array.Empty<ReconCell>(), Array.Empty<string>());
    }

    // ── One employee's share of one item ─────────────────────────────────────────────────────────

    private static void AllocateEmployee(
        ValueBridgeInput input, string kind, string code, int emp, ReconCategory category,
        List<ReconLine> prior, List<ReconLine> current, List<ReconCell> into)
    {
        if (ReconCategories.IsAdd(category))
        {
            Emit(into, emp, kind, code, "adds", AddBucket(category), current.Sum(l => l.Amount));
            return;
        }
        if (ReconCategories.IsExit(category))
        {
            Emit(into, emp, kind, code, "exits", ExitBucket(category), -prior.Sum(l => l.Amount));
            return;
        }

        // Continuing: the change, allocated driver by driver. Σ over drivers = Σ current - Σ prior exactly.
        input.Hints.TryGetValue(code, out var hint);
        var priorByDriver = prior.GroupBy(l => ReconDriverMap.Classify(l.Source, code, hint)).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        var currentByDriver = current.GroupBy(l => ReconDriverMap.Classify(l.Source, code, hint)).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        foreach (var driver in priorByDriver.Keys.Concat(currentByDriver.Keys).Distinct())
        {
            var priorAmount = priorByDriver.GetValueOrDefault(driver);
            var currentAmount = currentByDriver.GetValueOrDefault(driver);
            var delta = currentAmount - priorAmount;
            if (driver == ReconDriver.Package)
            {
                var (package, proration) = SplitPackage(input, code, emp, priorAmount, currentAmount, delta);
                Emit(into, emp, kind, code, "continuing", ReconDriverMap.BucketName(ReconDriver.Package), package);
                Emit(into, emp, kind, code, "continuing", ReconDriverMap.BucketName(ReconDriver.Proration), proration);
            }
            else
            {
                Emit(into, emp, kind, code, "continuing", ReconDriverMap.BucketName(driver), delta);
            }
        }
    }

    /// <summary>
    /// Package versus proration for a continuing employee's salary-sourced amount. BASIC, HOUSING and TRANSPORT use the
    /// slip's full-package witnesses: the package moved by (full now - full before) at this month's factor, and
    /// proration is whatever else moved (so the split ties exactly). Any other package code has no full-amount witness: if
    /// the proration factor is unchanged the whole movement is package, otherwise all of it is proration.
    /// </summary>
    private static (decimal Package, decimal Proration) SplitPackage(
        ValueBridgeInput input, string code, int emp, decimal priorAmount, decimal currentAmount, decimal delta)
    {
        input.PriorWitness.TryGetValue(emp, out var pw);
        input.CurrentWitness.TryGetValue(emp, out var cw);
        var priorFactor = pw?.ProrationFactor ?? 1m;
        var currentFactor = cw?.ProrationFactor ?? 1m;

        decimal? Full(ReconSlipWitness? w) => code switch
        {
            FullBasicCode => w?.FullBasic,
            FullHousingCode => w?.FullHousing,
            FullTransportCode => w?.FullTransport,
            _ => null,
        };

        if (code is FullBasicCode or FullHousingCode or FullTransportCode)
        {
            var fullPrior = Full(pw) ?? priorAmount;
            var fullCurrent = Full(cw) ?? currentAmount;
            var package = Math.Round((fullCurrent - fullPrior) * currentFactor, 2, MidpointRounding.AwayFromZero);
            return (package, delta - package);
        }
        return priorFactor == currentFactor ? (delta, 0m) : (0m, delta);
    }

    private static void Emit(List<ReconCell> into, int emp, string kind, string code, string group, string bucket, decimal amount)
    {
        if (amount != 0m) into.Add(new ReconCell(emp, kind, code, group, bucket, amount));
    }

    public static string AddBucket(ReconCategory c) => c switch
    {
        ReconCategory.NewJoiner => "newJoiner",
        ReconCategory.Rehire => "rehire",
        ReconCategory.LateJoiner => "lateJoiner",
        ReconCategory.Reinstated => "reinstated",
        ReconCategory.TransferIn => "transferIn",
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "Not an add category."),
    };

    public static string ExitBucket(ReconCategory c) => c switch
    {
        ReconCategory.Leaver => "leaver",
        ReconCategory.TransferOut => "transferOut",
        ReconCategory.NotPaid => "notPaid",
        ReconCategory.Unexplained => "unexplained",
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "Not an exit category."),
    };

    // ── Items and headline ───────────────────────────────────────────────────────────────────────

    private static ReconItemDto BuildItem(
        string kind, string code, string label, decimal prior, decimal current, List<ReconCell> cells, ValueBridgeInput input)
    {
        decimal Bucket(string group, string bucket) => cells.Where(c => c.Group == group && c.Bucket == bucket).Sum(c => c.Amount);
        var adds = new ReconAddsDto(
            Bucket("adds", "newJoiner"), Bucket("adds", "rehire"), Bucket("adds", "lateJoiner"),
            Bucket("adds", "reinstated"), Bucket("adds", "transferIn"));
        var exits = new ReconExitsDto(
            Bucket("exits", "leaver"), Bucket("exits", "transferOut"), Bucket("exits", "notPaid"), Bucket("exits", "unexplained"));
        var continuing = new ReconContinuingDto(
            Bucket("continuing", "package"), Bucket("continuing", "proration"), Bucket("continuing", "variable"),
            Bucket("continuing", "arrears"), Bucket("continuing", "attendance"), Bucket("continuing", "loans"),
            Bucket("continuing", "statutory"), Bucket("continuing", "other"));
        var delta = current - prior;
        var flagged = input.Thresholds.IsFlagged(prior, delta) || continuing.Other != 0m;
        string? explanation = null;
        input.ItemExplanations?.TryGetValue(ReconItemDto.ExplanationKey(kind, code), out explanation);
        return new ReconItemDto(code, label, prior, adds, exits, continuing, current, delta,
            ReconThresholds.DeltaPct(prior, delta), flagged, explanation);
    }

    /// <summary>The shortest component name seen for the code in either run (a tie goes to the alphabetically first).</summary>
    private static string Label(List<ReconLine> prior, List<ReconLine> current, string code) =>
        prior.Concat(current).Select(l => l.Name).Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).FirstOrDefault() ?? code;

    private static List<ReconItemDto> Order(string kind, List<ReconItemDto> items) =>
        items.OrderBy(i => Priority(kind, i.Code)).ThenBy(i => i.Code, StringComparer.Ordinal).ToList();

    private static int Priority(string kind, string code)
    {
        if (kind != ReconSectionKinds.Earning) return int.MaxValue;
        var ix = Array.IndexOf(EarningPriority, code);
        return ix >= 0 ? ix : int.MaxValue;
    }

    private static ReconBridgeRowDto Sum(IReadOnlyList<ReconValueSectionDto> sections, string kind)
    {
        var items = sections.Single(s => s.Kind == kind).Items;
        return Row(items.Sum(i => i.Prior), items.Sum(i => i.Current),
            new ReconAddsDto(items.Sum(i => i.Adds.NewJoiner), items.Sum(i => i.Adds.Rehire), items.Sum(i => i.Adds.LateJoiner),
                items.Sum(i => i.Adds.Reinstated), items.Sum(i => i.Adds.TransferIn)),
            new ReconExitsDto(items.Sum(i => i.Exits.Leaver), items.Sum(i => i.Exits.TransferOut), items.Sum(i => i.Exits.NotPaid),
                items.Sum(i => i.Exits.Unexplained)),
            new ReconContinuingDto(items.Sum(i => i.Continuing.Package), items.Sum(i => i.Continuing.Proration),
                items.Sum(i => i.Continuing.Variable), items.Sum(i => i.Continuing.Arrears), items.Sum(i => i.Continuing.Attendance),
                items.Sum(i => i.Continuing.Loans), items.Sum(i => i.Continuing.Statutory), items.Sum(i => i.Continuing.Other)));
    }

    private static ReconBridgeRowDto Net(ReconBridgeRowDto gross, ReconBridgeRowDto deductions) =>
        Row(gross.Prior - deductions.Prior, gross.Current - deductions.Current,
            new ReconAddsDto(gross.Adds.NewJoiner - deductions.Adds.NewJoiner, gross.Adds.Rehire - deductions.Adds.Rehire,
                gross.Adds.LateJoiner - deductions.Adds.LateJoiner, gross.Adds.Reinstated - deductions.Adds.Reinstated,
                gross.Adds.TransferIn - deductions.Adds.TransferIn),
            new ReconExitsDto(gross.Exits.Leaver - deductions.Exits.Leaver, gross.Exits.TransferOut - deductions.Exits.TransferOut,
                gross.Exits.NotPaid - deductions.Exits.NotPaid, gross.Exits.Unexplained - deductions.Exits.Unexplained),
            new ReconContinuingDto(gross.Continuing.Package - deductions.Continuing.Package,
                gross.Continuing.Proration - deductions.Continuing.Proration, gross.Continuing.Variable - deductions.Continuing.Variable,
                gross.Continuing.Arrears - deductions.Continuing.Arrears, gross.Continuing.Attendance - deductions.Continuing.Attendance,
                gross.Continuing.Loans - deductions.Continuing.Loans, gross.Continuing.Statutory - deductions.Continuing.Statutory,
                gross.Continuing.Other - deductions.Continuing.Other));

    private static ReconBridgeRowDto Row(
        decimal prior, decimal current, ReconAddsDto adds, ReconExitsDto exits, ReconContinuingDto continuing)
    {
        var delta = current - prior;
        return new ReconBridgeRowDto(prior, adds, exits, continuing, current, delta, ReconThresholds.DeltaPct(prior, delta));
    }
}
