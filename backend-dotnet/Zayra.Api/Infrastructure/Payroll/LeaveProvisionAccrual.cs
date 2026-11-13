using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// The per-company switch for the monthly annual-leave provision. A client rate (Surface A) exactly like
/// <see cref="EosbProvisioningRateKeys"/>: effective-dated, refused inside a locked payroll month or a closed GL
/// period by the rates write surface, and the effective date of the <see cref="BalanceMonthly"/> row is when
/// provisioning starts.
///
/// <para>Default <see cref="Off"/>: with no row the Lock journal is byte-identical to before, so no existing
/// tenant's books change until finance switches it on deliberately.</para>
/// </summary>
public static class LeaveProvisioningRateKeys
{
    public const string Mode = "payparameter.leave_provisioning";
    public const string Off = "Off";
    public const string BalanceMonthly = "BalanceMonthly";
    public static readonly string[] Modes = { Off, BalanceMonthly };

    public static IReadOnlyDictionary<string, string[]> AllowedValues { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [Mode] = Modes };

    public static bool IsOn(string? value) =>
        string.Equals(value?.Trim(), BalanceMonthly, StringComparison.OrdinalIgnoreCase);

    /// <summary>The posting driver of the liability (2330 by default, remappable per company).</summary>
    public const string ProvisionDriverKey = "LEAVE_PROVISION";
    /// <summary>The posting driver of the expense side (5115 by default, remappable per company).</summary>
    public const string ExpenseDriverKey = "LEAVE_PROVISION_EXPENSE";
}

/// <summary>One employee's measured unused-leave value, ready for the planner.</summary>
public sealed record LeaveObligation(
    int EmployeeId, string EmployeeCode, decimal Days, decimal DailyRate, string Basis,
    decimal Obligation, decimal CarriedBefore, bool IsCatchUp, Guid? CostCentreId = null,
    bool OpeningRoute = false);

/// <summary>Cost-accounting context for the planner: the expense side splits per cost centre and every line gets a posting key.</summary>
public sealed record LeaveCostContext(GlResolutionContext Gl);

/// <summary>
/// Turns measured leave obligations into the provision lines of a payroll Lock journal and the matching
/// snapshots. Pure: no database, so the arithmetic is testable on its own and the Lock and the GL preview build
/// exactly the same lines.
/// </summary>
public static class LeaveProvisionPlanner
{
    public const string CatchUpLabel = "Opening annual leave provision (catch-up)";
    public const string MonthlyLabel = "Monthly annual leave provision movement";
    public const string ReleaseLabel = "Annual leave provision reduced (unused leave value fell)";
    public const string OpeningBalanceLabel = "Opening annual leave provision (catch-up) - opening balance";

    public static (List<FinanceGlEntry> Lines, List<LeaveProvisionSnapshot> Snapshots) Build(
        Guid tenantId, Guid? companyId, Guid runId, string period, DateOnly measuredAt, string currency,
        string expenseAccount, string provisionAccount, IEnumerable<LeaveObligation> obligations,
        Guid? postedBy, string postedByName, DateOnly entryDate, LeaveCostContext? cost = null, string? snapshotPeriod = null,
        string? openingBalanceAccount = null)
    {
        var lines = new List<FinanceGlEntry>();
        var snapshots = new List<LeaveProvisionSnapshot>();
        // Per cost centre when costing is on (one bucket, key null, when it is off). Keyed by cost centre id,
        // Guid.Empty = no cost centre (a dictionary cannot hold a null key).
        var catchUpRise = new Dictionary<Guid, decimal>();
        decimal openingRise = 0m;   // M6: see ProvisionCatchUpRoute
        var monthlyRise = new Dictionary<Guid, decimal>();
        var fall = new Dictionary<Guid, decimal>();
        var keyOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var gl = cost?.Gl;

        FinanceGlEntry Line(string debit, string credit, decimal amount, string description, string entityRef, Guid? ccId = null)
        {
            var line = new FinanceGlEntry
            {
                TenantId = tenantId, CompanyId = companyId,
                SourceModule = FinalSettlementGlDescriptions.SourceModule, SourceEntityId = runId,
                SourceEntityRef = entityRef, EventType = GlEventTypes.LeaveProvisionAccrual,
                DebitAccount = debit, CreditAccount = credit, Amount = amount, Currency = currency,
                EntryDate = entryDate, Period = period, Description = description,
                PostedBy = postedBy, PostedByName = postedByName,
            };
            if (gl is null) return line;
            if (ccId is Guid g && gl.CostCentres.TryGetValue(g, out var info))
            {
                line.CostCenterId = g; line.CostCenterCode = info.Code; line.CostCenterName = info.Name; line.CostCenterGlSegment = info.GlSegment;
            }
            var side = string.IsNullOrEmpty(debit) ? "CR" : "DR";
            var basis = $"{entityRef}|{side}|{description}|{ccId?.ToString("N") ?? "-"}";
            var ordinal = keyOrdinals.GetValueOrDefault(basis);
            keyOrdinals[basis] = ordinal + 1;
            line.PostingKey = PayrollCostStamper.PostingKey(runId, GlEventTypes.LeaveProvisionAccrual, entityRef, side,
                ccId is null ? LeaveProvisioningRateKeys.ProvisionDriverKey : LeaveProvisioningRateKeys.ExpenseDriverKey,
                description, "LeaveProvision", ccId, ordinal);
            return line;
        }
        string ExpenseAt(Guid? ccId)
        {
            if (gl is null) return expenseAccount;
            var (code, name) = GlAccountResolver.AccountFor(LeaveProvisioningRateKeys.ExpenseDriverKey, PayrollCostStamper.ClassOf(ccId, gl), gl);
            return $"{code} - {name}";
        }
        string CcCode(Guid id) => gl is not null && gl.CostCentres.TryGetValue(id, out var c) ? c.Code : string.Empty;
        void Add(Dictionary<Guid, decimal> bucket, Guid? cc, decimal amount) { var k = cc ?? Guid.Empty; bucket[k] = bucket.GetValueOrDefault(k) + amount; }
        static Guid? Real(Guid id) => id == Guid.Empty ? null : id;

        foreach (var o in obligations.OrderBy(o => o.EmployeeId))
        {
            var obligation = Math.Round(Math.Max(0m, o.Obligation), 2);
            var carried = Math.Round(Math.Max(0m, o.CarriedBefore), 2);
            var movement = Math.Round(obligation - carried, 2);
            snapshots.Add(new LeaveProvisionSnapshot
            {
                // Keyed on the PAY period (a Replacement may book its journal into a later GL period).
                TenantId = tenantId, CompanyId = companyId, EmployeeId = o.EmployeeId, Period = snapshotPeriod ?? period,
                MeasuredAt = measuredAt, PayrollRunId = runId, Days = Math.Round(o.Days, 2),
                DailyRate = Math.Round(o.DailyRate, 6), Basis = o.Basis, Obligation = obligation,
                CarriedBefore = carried, Movement = movement, IsCatchUp = o.IsCatchUp, Currency = currency,
            });
            if (movement == 0m) continue;

            var label = o.IsCatchUp ? CatchUpLabel : movement > 0m ? MonthlyLabel : ReleaseLabel;
            var employeeRef = LeaveProvisionLedger.EmployeeRef(o.EmployeeId);
            if (movement > 0m)
            {
                lines.Add(Line(string.Empty, provisionAccount, movement, label, employeeRef));
                if (o.IsCatchUp && o.OpeningRoute && openingBalanceAccount is not null) openingRise += movement;
                else if (o.IsCatchUp) Add(catchUpRise, gl is null ? null : o.CostCentreId, movement); else Add(monthlyRise, gl is null ? null : o.CostCentreId, movement);
            }
            else
            {
                lines.Add(Line(provisionAccount, string.Empty, -movement, label, employeeRef));
                Add(fall, gl is null ? null : o.CostCentreId, -movement);
            }
        }

        // Equity is not profit and loss: one line, no cost centre.
        if (openingRise > 0m && openingBalanceAccount is not null)
            lines.Add(Line(openingBalanceAccount, string.Empty, openingRise, OpeningBalanceLabel, period));
        foreach (var (key, amount) in catchUpRise.OrderBy(kv => CcCode(kv.Key), StringComparer.Ordinal).Where(kv => kv.Value > 0m))
            lines.Add(Line(ExpenseAt(Real(key)), string.Empty, amount, CatchUpLabel, period, Real(key)));
        foreach (var (key, amount) in monthlyRise.OrderBy(kv => CcCode(kv.Key), StringComparer.Ordinal).Where(kv => kv.Value > 0m))
            lines.Add(Line(ExpenseAt(Real(key)), string.Empty, amount, MonthlyLabel, period, Real(key)));
        foreach (var (key, amount) in fall.OrderBy(kv => CcCode(kv.Key), StringComparer.Ordinal).Where(kv => kv.Value > 0m))
            lines.Add(Line(string.Empty, ExpenseAt(Real(key)), amount, ReleaseLabel, period, Real(key)));
        return (lines, snapshots);
    }
}
