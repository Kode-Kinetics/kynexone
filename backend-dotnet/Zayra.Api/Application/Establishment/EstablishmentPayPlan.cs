namespace Zayra.Api.Application.Establishment;

/// <summary>A grade's pay scale as the plan sees it (projection of Grade; no EF dependency).</summary>
public sealed record PayGrade(Guid Id, Guid? CompanyId, bool IsDeleted, bool IsActive, decimal Min, decimal Mid, decimal Max, string? Currency);
/// <summary>A designation's level mapping and grade link (projection of Designation).</summary>
public sealed record PayDesignation(Guid? StaffingLevelId, Guid? GradeId, Guid? CompanyId = null);
/// <summary>One staffing level of a department: Budgeted null = uncontrolled, 0 = frozen.</summary>
public sealed record PayLevelInput(Guid LevelId, int? Budgeted);
public sealed record PayBand(decimal Min, decimal Mid, decimal Max, string Currency);
public sealed record LevelPayBand(PayBand? Band, bool CurrencyMismatch, int PricedDesignations, int UnpricedDesignations)
{
    /// <summary>"priced" = band exists; "currencyMismatch" = valid grades existed but mixed currencies or differ from
    /// the company default; "noRange" = no usable grade at all.</summary>
    public string Status => Band is not null ? "priced" : CurrencyMismatch ? "currencyMismatch" : "noRange";
}
public sealed record DepartmentPayPlan(decimal? AvgPerHead, decimal PlannedMin, decimal PlannedMid, decimal PlannedMax,
    decimal UnallocatedBudget, int UnpricedHeads, int UncontrolledLevels, IReadOnlyList<string> Flags,
    IReadOnlyDictionary<Guid, LevelPayBand> Bands);

/// <summary>
/// Pay-range reconciliation of a department's headcount budget against the grade bands of the
/// designations mapped to each staffing level. WARNING-ONLY: nothing here blocks anything.
/// Money is decimal throughout and never summed across currencies. Results do not depend on
/// input ordering.
/// </summary>
public static class EstablishmentPayPlan
{
    /// <summary>Unallocated share of the budget at/above which "underBudget" is flagged (inclusive).</summary>
    public const decimal UnderBudgetThreshold = 0.25m;

    public const string FlagNotComparable = "notComparable";
    public const string FlagOverBudget = "overBudget";
    public const string FlagBelowMinimum = "belowMinimum";
    public const string FlagUnderBudget = "underBudget";
    public const string FlagCurrencyMismatch = "currencyMismatch";

    private static string Cur(string? c) => (c ?? string.Empty).Trim().ToUpperInvariant();

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static bool ValidGrade(PayGrade g) => !g.IsDeleted && g.IsActive && g.Max > 0 && g.Min <= g.Max;

    private static decimal MidOf(PayGrade g)
    {
        var mid = g.Mid > 0 ? g.Mid : (g.Min + g.Max) / 2m;
        return Math.Min(g.Max, Math.Max(g.Min, mid));
    }

    /// <summary>
    /// Band for one level as seen from a department of <paramref name="deptCompanyId"/>: min of mins,
    /// UNWEIGHTED average of mids (per designation, not per headcount - no per-designation headcount
    /// exists in the budget), max of maxes. A designation with no usable grade (null, unknown,
    /// deleted, inactive, invalid, other company) is counted unpriced, not dropped. Mixed currencies
    /// within the level, or differing from <paramref name="companyCurrency"/>, leave the level unpriced.
    /// </summary>
    public static LevelPayBand ComputeBand(Guid levelId, Guid? deptCompanyId, string? companyCurrency,
        IReadOnlyCollection<PayDesignation> designations, IReadOnlyCollection<PayGrade> grades)
    {
        var byId = grades.ToDictionary(g => g.Id);
        // Company scope (grades and designations alike): a row of company X counts for a department of company X
        // or when it is shared (CompanyId null). A department with no company sees ONLY shared rows - it must never
        // blend the bands of different companies. A designation of another company is out of scope entirely.
        static bool InScope(Guid? rowCompany, Guid? deptCompany) => rowCompany is null || rowCompany == deptCompany;
        var mapped = designations.Where(d => d.StaffingLevelId == levelId && InScope(d.CompanyId, deptCompanyId)).ToList();
        var usable = new List<PayGrade>();
        foreach (var d in mapped)
        {
            if (d.GradeId is { } gid && byId.TryGetValue(gid, out var g) && ValidGrade(g)
                && InScope(g.CompanyId, deptCompanyId))
                usable.Add(g);
        }

        if (usable.Count == 0)
            return new LevelPayBand(null, false, 0, mapped.Count);

        var currencies = usable.Select(g => Cur(g.Currency)).Distinct().ToList();
        var company = Cur(companyCurrency);
        var mismatch = currencies.Count > 1 || (company.Length > 0 && currencies[0] != company);
        if (mismatch)
            return new LevelPayBand(null, true, 0, mapped.Count);

        var band = new PayBand(
            usable.Min(g => g.Min),
            R2(usable.Sum(MidOf) / usable.Count),
            usable.Max(g => g.Max),
            currencies[0]);
        return new LevelPayBand(band, false, usable.Count, mapped.Count - usable.Count);
    }

    public static DepartmentPayPlan Plan(decimal budget, int approved, Guid? deptCompanyId, string? companyCurrency,
        IReadOnlyCollection<PayLevelInput> levels, IReadOnlyCollection<PayDesignation> designations,
        IReadOnlyCollection<PayGrade> grades)
    {
        decimal? avg = approved > 0 && budget > 0 ? R2(budget / approved) : null;

        var bands = new Dictionary<Guid, LevelPayBand>();
        decimal pMin = 0, pMid = 0, pMax = 0;
        int unpricedHeads = 0, uncontrolled = 0, pricedHeads = 0;
        var currencyMismatch = false;

        foreach (var l in levels.GroupBy(x => x.LevelId).Select(g => g.First()).OrderBy(x => x.LevelId))
        {
            var lb = ComputeBand(l.LevelId, deptCompanyId, companyCurrency, designations, grades);
            bands[l.LevelId] = lb;
            if (l.Budgeted is not { } heads) { uncontrolled++; continue; }
            if (lb.CurrencyMismatch) currencyMismatch = true;
            if (lb.Band is null) { unpricedHeads += heads; continue; }
            pricedHeads += heads;
            pMin += heads * lb.Band.Min;
            pMid += heads * lb.Band.Mid;
            pMax += heads * lb.Band.Max;
        }

        // Round once, then judge the flags on the figures the user sees.
        pMin = R2(pMin); pMid = R2(pMid); pMax = R2(pMax);
        var unallocated = R2(budget - pMid);
        var flags = new List<string>();
        var comparable = budget > 0 && pricedHeads > 0;
        if (!comparable) flags.Add(FlagNotComparable);
        else
        {
            if (pMid > budget) flags.Add(FlagOverBudget);
            if (pMin > budget) flags.Add(FlagBelowMinimum);
            if (unallocated >= UnderBudgetThreshold * budget) flags.Add(FlagUnderBudget);
        }
        if (currencyMismatch) flags.Add(FlagCurrencyMismatch);

        return new DepartmentPayPlan(avg, pMin, pMid, pMax, unallocated, unpricedHeads, uncontrolled, flags, bands);
    }
}
