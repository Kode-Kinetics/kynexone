using Zayra.Api.Application.WorkWeek;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// POD-C3 — the resolved, per-company proration/arrears policy for ONE run. Every value is NAMED,
/// persisted through the EXISTING <c>CompanyRatePolicy</c> surface (company row → tenant default →
/// compiled default), and reported on the run's audit record so "which basis paid this month?" always
/// has an answer.
/// </summary>
public sealed record ProrationPolicy(
    string Basis,
    string GosiBase,
    string ArrearsGosiTreatment,
    int ArrearsMaxLookbackMonths,
    IReadOnlySet<string> ProratedComponents,
    string BasisSource,
    string GosiBaseSource)
{
    /// <summary>Is this component in the configurable PRORATED SET? The set is half the basis — "everything
    /// prorates" is a policy decision, not a mechanical one (a billed phone line or a school-transport
    /// reimbursement is commonly paid in full in the joining month).</summary>
    public bool Prorates(string componentCode) => ProratedComponents.Contains(componentCode);

    public bool IsEnabled => Basis != ProrationBases.None;
    public bool ProratesStatutoryBase => GosiBase == ProrationGosiBases.Prorated;
    public bool ArrearsAreGosiBearing => ArrearsGosiTreatment == ArrearsGosiTreatments.PeriodPaid;
}

/// <summary>The prorate-able component codes. Named so a company can shorten the set explicitly.</summary>
public static class ProratedComponentCodes
{
    public const string Basic           = "BASIC";
    public const string Housing         = "HOUSING";
    public const string Transport       = "TRANSPORT";
    public const string OtherAllowances = "OTHER_ALLOWANCES";
    /// <summary>Prorating a fixed deduction INCREASES net. Right for a canteen charge, wrong for a
    /// recovery instalment — which is why it is in the configurable set and stated on the payslip.</summary>
    public const string FixedDeduction  = "FIXED_DEDUCTION";

    public static readonly string[] All =
        { Basic, Housing, Transport, OtherAllowances, FixedDeduction };

    /// <summary>Default set = the whole recurring package. Reproduces the "one factor across the package"
    /// behaviour that GCC practice expects, while making the choice explicit and narrowable.</summary>
    public static IReadOnlySet<string> Default { get; } =
        new HashSet<string>(All, StringComparer.OrdinalIgnoreCase);
}

/// <summary>The client-rate keys this pod owns. Registered in <c>StatutoryRateGuard.DefaultClientRateDefinitions</c>
/// so <c>RatesController</c>'s allow-list actually ACCEPTS them (they are otherwise unwritable).</summary>
public static class ProrationRateKeys
{
    public const string Basis                = "payparameter.proration_basis";
    public const string GosiBase             = "payparameter.proration_gosi_base";
    public const string ArrearsGosiTreatment = "payparameter.arrears_gosi_treatment";
    public const string ArrearsMaxLookback   = "payparameter.arrears_max_lookback_months";
    public const string ProratedComponents   = "payparameter.prorated_components";

    /// <summary>Allowed values for the STRING-typed keys, so an unrecognised basis 400s at the write
    /// surface instead of landing in the database and 422-ing every future payroll run.</summary>
    public static IReadOnlyDictionary<string, string[]> AllowedValues { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Basis]                = ProrationBases.All,
            [GosiBase]             = ProrationGosiBases.All,
            [ArrearsGosiTreatment] = ArrearsGosiTreatments.All,
        };
}

/// <summary>Why a policy could not be resolved. Non-null ⇒ the run REFUSES; nothing silently defaults.</summary>
public sealed record ProrationPolicyError(string Code, string Message, object? Detail = null);

public interface IProrationPolicyResolver
{
    Task<(ProrationPolicy? Policy, ProrationPolicyError? Error)> ResolveAsync(
        Guid tenantId, Guid? companyId, DateOnly onDate, WorkWeekConfig workWeek, CancellationToken ct = default);
}

/// <summary>
/// Resolves the proration policy through the EXISTING <see cref="ICompanyRatePolicyResolver"/> chain —
/// company row wins over tenant default, newest effective row that covers the date wins. No new policy
/// table, and it inherits the group-safe company-first precedence POD-D2 will need.
///
/// <para><b>FAIL LOUD, NEVER FALL BACK.</b> A stored value that does not canonicalise is an ERROR, not a
/// reason to use the default: a silent fallback is exactly how a tenant ends up paying on a basis nobody
/// chose. The same rule applies to <see cref="ProrationBases.WorkingDays"/> with no configured working
/// week — the platform Fri/Sat default is a fallback for LEAVE counting, not a mandate to pay salary on
/// it.</para>
/// </summary>
public sealed class ProrationPolicyResolver : IProrationPolicyResolver
{
    private readonly ICompanyRatePolicyResolver _rates;
    public ProrationPolicyResolver(ICompanyRatePolicyResolver rates) => _rates = rates;

    public async Task<(ProrationPolicy? Policy, ProrationPolicyError? Error)> ResolveAsync(
        Guid tenantId, Guid? companyId, DateOnly onDate, WorkWeekConfig workWeek, CancellationToken ct = default)
    {
        var (basisRaw, basisSource) = await ReadAsync(tenantId, companyId, ProrationRateKeys.Basis, onDate, ct);
        var basis = basisRaw is null ? ProrationBases.Calendar30 : ProrationBases.Normalize(basisRaw);
        if (basis is null)
            return (null, new ProrationPolicyError("proration_basis_invalid",
                $"Proration basis '{basisRaw}' (from {basisSource}) is not a recognised value. " +
                $"Allowed: {string.Join(", ", ProrationBases.All)}. Payroll run aborted — no payslips written.",
                new { rateKey = ProrationRateKeys.Basis, value = basisRaw, allowed = ProrationBases.All }));

        if (basis == ProrationBases.WorkingDays && workWeek.Source.StartsWith("default:", StringComparison.Ordinal))
            return (null, new ProrationPolicyError("proration_working_week_not_configured",
                "The proration basis is 'WorkingDays' but no working week is configured for this legal entity, " +
                "so the platform Fri/Sat fallback would silently decide how much every joiner and leaver is paid. " +
                "Configure the company's working week (Setup → Compliance), or choose a calendar-day basis.",
                new { rateKey = ProrationRateKeys.Basis, workWeekSource = workWeek.Source }));

        var (gosiRaw, gosiSource) = await ReadAsync(tenantId, companyId, ProrationRateKeys.GosiBase, onDate, ct);
        var gosiBase = gosiRaw is null ? ProrationGosiBases.FullMonth : ProrationGosiBases.Normalize(gosiRaw);
        if (gosiBase is null)
            return (null, new ProrationPolicyError("proration_gosi_base_invalid",
                $"Proration GOSI base '{gosiRaw}' (from {gosiSource}) is not a recognised value. " +
                $"Allowed: {string.Join(", ", ProrationGosiBases.All)}.",
                new { rateKey = ProrationRateKeys.GosiBase, value = gosiRaw, allowed = ProrationGosiBases.All }));

        var (arrRaw, arrSource) = await ReadAsync(tenantId, companyId, ProrationRateKeys.ArrearsGosiTreatment, onDate, ct);
        var arrears = arrRaw is null ? ArrearsGosiTreatments.PeriodPaid : ArrearsGosiTreatments.Normalize(arrRaw);
        if (arrears is null)
            return (null, new ProrationPolicyError("arrears_gosi_treatment_invalid",
                $"Arrears GOSI treatment '{arrRaw}' (from {arrSource}) is not a recognised value. " +
                $"Allowed: {string.Join(", ", ArrearsGosiTreatments.All)}.",
                new { rateKey = ProrationRateKeys.ArrearsGosiTreatment, value = arrRaw, allowed = ArrearsGosiTreatments.All }));
        if (arrears == ArrearsGosiTreatments.PeriodEarned)
            return (null, new ProrationPolicyError("arrears_gosi_period_earned_unsupported",
                "Arrears GOSI treatment 'PeriodEarned' assesses each retro amount against the month it was EARNED, " +
                "which requires an AMENDED GOSI DECLARATION workflow this product does not implement. It is refused " +
                "rather than silently downgraded to 'PeriodPaid'. The earned-basis figure IS computed and persisted " +
                "on every arrears line (earned_basis_gosi_delta) so an amended return can be prepared from it.",
                new { rateKey = ProrationRateKeys.ArrearsGosiTreatment, value = arrears }));

        var (lookbackRaw, _) = await ReadAsync(tenantId, companyId, ProrationRateKeys.ArrearsMaxLookback, onDate, ct);
        var lookback = 24;
        if (lookbackRaw is not null)
        {
            if (!decimal.TryParse(lookbackRaw, System.Globalization.NumberStyles.Any,
                                  System.Globalization.CultureInfo.InvariantCulture, out var lv) || lv < 0m || lv > 120m)
                return (null, new ProrationPolicyError("arrears_lookback_invalid",
                    $"Arrears lookback '{lookbackRaw}' is not a whole number of months between 0 and 120.",
                    new { rateKey = ProrationRateKeys.ArrearsMaxLookback, value = lookbackRaw }));
            lookback = (int)Math.Floor(lv);
        }

        var (componentsRaw, _) = await ReadAsync(tenantId, companyId, ProrationRateKeys.ProratedComponents, onDate, ct);
        IReadOnlySet<string> components = ProratedComponentCodes.Default;
        if (!string.IsNullOrWhiteSpace(componentsRaw))
        {
            var parsed = componentsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var unknown = parsed.Where(p => !ProratedComponentCodes.All.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
                return (null, new ProrationPolicyError("prorated_components_invalid",
                    $"Unknown prorated component code(s): {string.Join(", ", unknown)}. " +
                    $"Allowed: {string.Join(", ", ProratedComponentCodes.All)}.",
                    new { rateKey = ProrationRateKeys.ProratedComponents, value = componentsRaw, allowed = ProratedComponentCodes.All }));
            components = new HashSet<string>(parsed, StringComparer.OrdinalIgnoreCase);
        }

        return (new ProrationPolicy(basis, gosiBase, arrears, lookback, components, basisSource, gosiSource), null);
    }

    private async Task<(string? Value, string Source)> ReadAsync(
        Guid tenantId, Guid? companyId, string key, DateOnly onDate, CancellationToken ct)
    {
        var row = await _rates.ResolveAsync(tenantId, companyId, key, onDate, ct);
        if (row is null) return (null, "compiled default");
        return (row.RateValue, row.CompanyId is null ? "tenant default policy" : "company policy");
    }
}
