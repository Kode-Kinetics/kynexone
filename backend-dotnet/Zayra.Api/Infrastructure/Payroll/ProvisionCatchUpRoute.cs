namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// M6 — where the go-live catch-up of the end-of-service and annual-leave provisions is booked. The first month a company provisions, the measurement
/// books ALL past service (EOSB) or ALL unused leave in one go; by default that is an expense, so the go-live liability hits profit and loss in one month.
/// Company policy <see cref="Key"/> (effective-dated, like the provisioning switches):
/// <c>Expense</c> (default, the journal exactly as it always was) | <c>OpeningBalance</c> (the catch-up debits Opening Balance Equity instead).
///
/// <para>Only the catch-up of a person whose service began BEFORE the go-live month, in the company's FIRST provisioning run, moves. A new joiner's first
/// measurement, the month's own accrual, every later month and any release stay expense, and the per-employee credit to the provision (2310 / 2330)
/// is identical either way, so the sub-ledger, settlement consumption and void behave as before. The go-live month's own accrual is inside the opening
/// figure: the catch-up is measured at the end of that month.</para>
///
/// <para>This is an accounting policy choice (a charge to equity instead of profit and loss), so it is off until the client's finance team sets it.</para>
/// </summary>
public static class ProvisionCatchUpRoute
{
    public const string Key = "payparameter.provision_catchup_route";
    public const string Expense = "Expense";
    public const string OpeningBalance = "OpeningBalance";
    public static readonly string[] Routes = { Expense, OpeningBalance };

    public static IReadOnlyDictionary<string, string[]> AllowedValues { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [Key] = Routes };

    public static bool IsOpeningBalance(string? value) =>
        string.Equals(value?.Trim(), OpeningBalance, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a run for <paramref name="payPeriod"/> ("yyyy-MM") books the go-live catch-up of a person whose service began before it to opening
    /// balance equity. It is keyed on the GO-LIVE PERIOD, not on "no snapshot exists yet": two pay groups of one company locking in the same
    /// period must both take the route, and any later period is expense. The go-live period is the month the route took effect
    /// (<paramref name="routeEffectiveFrom"/>, the company rate's effective date) or, when the company has already been measured, the month it was
    /// first measured (<paramref name="earliestMeasuredPeriod"/>); a company never measured before is, by definition, in its first period.
    /// </summary>
    public static bool AppliesToPeriod(string? routeValue, DateOnly? routeEffectiveFrom, string payPeriod, string? earliestMeasuredPeriod)
    {
        if (!IsOpeningBalance(routeValue)) return false;
        if (earliestMeasuredPeriod is null) return true;
        if (string.Equals(earliestMeasuredPeriod, payPeriod, StringComparison.Ordinal)) return true;
        return routeEffectiveFrom is { } from
            && string.Equals(from.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), payPeriod, StringComparison.Ordinal);
    }

    /// <summary>The posting driver of the equity side (3900 by default, remappable per company).</summary>
    public const string EquityDriverKey = "OPENING_BALANCE_EQUITY";
}
