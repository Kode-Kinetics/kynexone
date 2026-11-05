namespace Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

/// <summary>
/// The movement category of one employee between the prior and the current payroll run. Every employee who
/// has a slip in either run is in exactly one.
/// </summary>
public enum ReconCategory
{
    Continuing,
    TransferIn,
    Rehire,
    NewJoiner,
    LateJoiner,
    Reinstated,
    TransferOut,
    Leaver,
    NotPaid,
    Unexplained,
}

public static class ReconCategories
{
    /// <summary>Adds in contract order.</summary>
    public static readonly IReadOnlyList<ReconCategory> Adds = new[]
    {
        ReconCategory.NewJoiner, ReconCategory.Rehire, ReconCategory.LateJoiner, ReconCategory.Reinstated, ReconCategory.TransferIn,
    };

    /// <summary>Exits in contract order.</summary>
    public static readonly IReadOnlyList<ReconCategory> Exits = new[]
    {
        ReconCategory.Leaver, ReconCategory.TransferOut, ReconCategory.NotPaid, ReconCategory.Unexplained,
    };

    public static string Name(ReconCategory c) => c switch
    {
        ReconCategory.Continuing => "CONTINUING",
        ReconCategory.TransferIn => "TRANSFER_IN",
        ReconCategory.Rehire => "REHIRE",
        ReconCategory.NewJoiner => "NEW_JOINER",
        ReconCategory.LateJoiner => "LATE_JOINER",
        ReconCategory.Reinstated => "REINSTATED",
        ReconCategory.TransferOut => "TRANSFER_OUT",
        ReconCategory.Leaver => "LEAVER",
        ReconCategory.NotPaid => "NOT_PAID",
        ReconCategory.Unexplained => "UNEXPLAINED",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public static ReconCategory? Parse(string? name)
    {
        foreach (var c in Enum.GetValues<ReconCategory>())
            if (string.Equals(Name(c), name?.Trim(), StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    public static bool IsAdd(ReconCategory c) => Adds.Contains(c);
    public static bool IsExit(ReconCategory c) => Exits.Contains(c);

    /// <summary>"Add" or "Exit" for a movement category; "Continuing" is neither and reports as such.</summary>
    public static string Kind(ReconCategory c) => IsAdd(c) ? ReconKinds.Add : IsExit(c) ? ReconKinds.Exit : "Continuing";
}

/// <summary>
/// Everything the classifier needs to know about one employee, flattened so the rules are pure. The service
/// builds these from the slips, the employee master, offboardings, selections and settlements.
/// </summary>
public sealed record ReconEmployeeFacts
{
    public int EmployeeId { get; init; }
    public bool InPrior { get; init; }
    public bool InCurrent { get; init; }

    /// <summary>False when the employee master row could not be found.</summary>
    public bool EmployeeKnown { get; init; } = true;
    public string? Status { get; init; }
    public DateOnly? JoiningDate { get; init; }
    public DateOnly? OriginalJoiningDate { get; init; }

    public bool CurrentFinalWageMonth { get; init; }
    public bool CurrentProrated { get; init; }
    public bool PriorFinalWageMonth { get; init; }

    /// <summary>The employee's company today differs from the run's company.</summary>
    public bool EmployeeInOtherCompanyNow { get; init; }
    /// <summary>Paid in the prior period-owning run of ANOTHER company in the same tenant.</summary>
    public bool InPriorRunOfOtherCompany { get; init; }
    /// <summary>A non-cancelled offboarding ended before the (current) joining date: they left and came back.</summary>
    public bool OffboardedBeforeJoining { get; init; }
    /// <summary>A non-cancelled offboarding whose last working day is before the period starts.</summary>
    public bool OffboardedBeforePeriod { get; init; }
    /// <summary>Has a wage slip in some earlier period-owning run anywhere in the tenant.</summary>
    public bool EarlierWageSlipInTenant { get; init; }
    /// <summary>The run's population selector deliberately left this employee out.</summary>
    public bool ExcludedBySelection { get; init; }
    /// <summary>A final settlement for this employee has accrued (Approved, Disbursing or Paid).</summary>
    public bool HasAccruedSettlement { get; init; }
}

public readonly record struct ReconClassification(ReconCategory Category, bool FsApproved, bool FsMissing);

public sealed record VolumeBridge(
    int Opening,
    int Closing,
    IReadOnlyDictionary<ReconCategory, int> Counts,
    int FsApproved,
    int FsMissing,
    int FinalWageMonthInCurrent,
    IReadOnlyDictionary<int, ReconClassification> Assignments)
{
    public int Count(ReconCategory c) => Counts.TryGetValue(c, out var n) ? n : 0;

    public int Adds => ReconCategories.Adds.Sum(Count);
    public int Exits => ReconCategories.Exits.Sum(Count);

    /// <summary>Opening + adds - exits - closing. Zero by construction; the field exists so the tie is checked, not assumed.</summary>
    public int Difference => Opening + Adds - Exits - Closing;
    public bool Ties => Difference == 0;
    public int UnexplainedCount => Count(ReconCategory.Unexplained);
}

public static class ManpowerReconClassifier
{
    private static readonly HashSet<string> SeparatedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Offboarded", "Terminated", "Exited", "Archived",
    };

    private static readonly HashSet<string> NotActiveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Suspended", "Draft",
    };

    public static ReconClassification Classify(ReconEmployeeFacts f, DateOnly periodStart, DateOnly periodEnd)
    {
        if (f.InPrior && f.InCurrent) return new(ReconCategory.Continuing, false, false);

        if (f.InCurrent)
        {
            // In the current run only. Precedence, top-down.
            if (f.InPriorRunOfOtherCompany) return new(ReconCategory.TransferIn, false, false);
            if (IsRehire(f)) return new(ReconCategory.Rehire, false, false);
            if (f.EarlierWageSlipInTenant) return new(ReconCategory.Reinstated, false, false);
            var joined = f.JoiningDate;
            if (joined is { } j && j >= periodStart && j <= periodEnd) return new(ReconCategory.NewJoiner, false, false);
            // Paid for the first time but not joining this month (earlier, later, or no date at all): a control flag.
            return new(ReconCategory.LateJoiner, false, false);
        }

        if (!f.InPrior) throw new ArgumentException($"Employee {f.EmployeeId} is in neither run.", nameof(f));

        // In the prior run only.
        if (f.EmployeeKnown && f.EmployeeInOtherCompanyNow) return new(ReconCategory.TransferOut, false, false);
        if (f.PriorFinalWageMonth || f.OffboardedBeforePeriod || (f.Status is { } s && SeparatedStatuses.Contains(s)))
            return new(ReconCategory.Leaver, f.HasAccruedSettlement, !f.HasAccruedSettlement);
        if (f.ExcludedBySelection || (f.Status is { } st && NotActiveStatuses.Contains(st)))
            return new(ReconCategory.NotPaid, false, false);
        return new(ReconCategory.Unexplained, false, false);
    }

    private static bool IsRehire(ReconEmployeeFacts f) =>
        f.OffboardedBeforeJoining
        || (f.OriginalJoiningDate is { } original && f.JoiningDate is { } current && original < current);

    /// <summary>Classifies everyone and counts. <paramref name="facts"/> must hold one row per employee.</summary>
    public static VolumeBridge Bridge(IEnumerable<ReconEmployeeFacts> facts, DateOnly periodStart, DateOnly periodEnd)
    {
        var assignments = new Dictionary<int, ReconClassification>();
        var counts = new Dictionary<ReconCategory, int>();
        int opening = 0, closing = 0, fsApproved = 0, fsMissing = 0, finalWage = 0;
        foreach (var f in facts)
        {
            if (assignments.ContainsKey(f.EmployeeId))
                throw new ArgumentException($"Employee {f.EmployeeId} appears more than once.", nameof(facts));
            var c = Classify(f, periodStart, periodEnd);
            assignments[f.EmployeeId] = c;
            counts[c.Category] = counts.GetValueOrDefault(c.Category) + 1;
            if (f.InPrior) opening++;
            if (f.InCurrent) closing++;
            if (c.FsApproved) fsApproved++;
            if (c.FsMissing) fsMissing++;
            if (f.InCurrent && f.CurrentFinalWageMonth) finalWage++;
        }
        return new VolumeBridge(opening, closing, counts, fsApproved, fsMissing, finalWage, assignments);
    }
}
