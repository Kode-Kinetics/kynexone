using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// POD-C2 — the per-company switch for the monthly end-of-service provision.
///
/// <para>A client rate (Surface A, <see cref="StatutoryRateGuard.DefaultClientRateDefinitions"/>) rather
/// than a new settings table: it is effective-dated already, superseding a value keeps the earlier
/// months resolvable, and the rates write surface already refuses a start date inside a locked payroll
/// month or a closed GL period. The start of provisioning is the effective date of the
/// <see cref="StatutoryMonthly"/> row — no separate start key is needed.</para>
///
/// <para>Default <see cref="Off"/>: with no row the Lock journal is byte-identical to before, so no
/// existing tenant's books change until finance switches it on deliberately.</para>
/// </summary>
public static class EosbProvisioningRateKeys
{
    public const string Mode = "payparameter.eosb_provisioning";
    public const string Off = "Off";
    public const string StatutoryMonthly = "StatutoryMonthly";
    public static readonly string[] Modes = { Off, StatutoryMonthly };

    public static IReadOnlyDictionary<string, string[]> AllowedValues { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [Mode] = Modes };

    public static bool IsOn(string? value) =>
        string.Equals(value?.Trim(), StatutoryMonthly, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The separation scenario the obligation is measured under: an employer-side termination / end of
    /// contract, i.e. the FULL Art. 84 award with no Art. 85 resignation reduction.
    ///
    /// <para>This is the standard IAS 19 practice for the statutory-formula (non-actuarial) approach in
    /// the GCC: the liability is measured as the amount that would be payable if every employee left at
    /// the reporting date on the basis that the employer cannot avoid. It deliberately over-states the
    /// obligation for a would-be resigner, and the difference is released to 5110 when the settlement
    /// is approved (see <see cref="FinalSettlementGlLedger.BuildAccrual"/>). An actuarial valuation
    /// (projected salary, attrition, discounting) is layered on top through the approved true-up.</para>
    /// </summary>
    public const string ObligationScenario = "Termination";
}

/// <summary>One employee's measured obligation, ready for the planner.</summary>
public sealed record EosbObligation(
    int EmployeeId, string EmployeeCode, decimal Obligation, decimal CarriedBefore, bool IsCatchUp,
    decimal WageBase, decimal ServiceYears, string Rule, Guid? CostCentreId = null);

/// <summary>Cost-accounting context for the planner: the expense side splits per cost centre and every line gets a posting key.</summary>
public sealed record EosbCostContext(GlResolutionContext Gl);

/// <summary>
/// POD-C2 — turns measured obligations into the provision lines of a payroll Lock journal and the
/// matching snapshots. Pure: no database, so the arithmetic is testable on its own and the Lock and the
/// GL preview build exactly the same lines.
///
/// <para><b>THE LINES.</b> The PROVISION side is per employee (<c>SourceEntityRef</c> = employee id), so
/// <see cref="EosbProvisionLedger"/> can attribute every movement and a settlement consumes only what
/// was provided for that person: CR 2310 when the obligation rose, DR 2310 when it fell. The EXPENSE
/// side is aggregated per label (<c>SourceEntityRef</c> = period, never an employee id, so the
/// sub-ledger can never mistake it for a provision): DR 5110 for the opening catch-up, DR 5110 for the
/// monthly rise, CR 5110 for any fall. Balanced by construction, and all in the run's company.</para>
/// </summary>
public static class EosbProvisionPlanner
{
    public const string CatchUpLabel = "Opening EOSB provision (catch-up)";
    public const string MonthlyLabel = "Monthly EOSB provision movement";
    public const string ReleaseLabel = "EOSB provision reduced (obligation fell)";

    public static (List<FinanceGlEntry> Lines, List<EosbProvisionSnapshot> Snapshots) Build(
        Guid tenantId, Guid? companyId, Guid runId, string period, DateOnly measuredAt, string currency,
        string expenseAccount, string provisionAccount, IEnumerable<EosbObligation> obligations,
        Guid? postedBy, string postedByName, DateOnly entryDate, EosbCostContext? cost = null)
    {
        var lines = new List<FinanceGlEntry>();
        var snapshots = new List<EosbProvisionSnapshot>();
        // Per cost centre when costing is on (one bucket, key null, when it is off — the pre-cost-accounting lines exactly).
        // Keyed by cost centre id, Guid.Empty = no cost centre (a dictionary cannot hold a null key).
        var catchUpRise = new Dictionary<Guid, decimal>();
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
                SourceEntityRef = entityRef, EventType = GlEventTypes.EosbProvisionAccrual,
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
            line.PostingKey = PayrollCostStamper.PostingKey(runId, GlEventTypes.EosbProvisionAccrual, entityRef, side,
                ccId is null ? "EOSB_PROVISION" : "EARN:EOSB", description, "Eosb", ccId, ordinal);
            return line;
        }
        string ExpenseAt(Guid? ccId)
        {
            if (gl is null) return expenseAccount;
            var (code, name) = GlAccountResolver.AccountFor("EARN:EOSB", PayrollCostStamper.ClassOf(ccId, gl), gl);
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
            snapshots.Add(new EosbProvisionSnapshot
            {
                TenantId = tenantId, CompanyId = companyId, EmployeeId = o.EmployeeId, Period = period,
                MeasuredAt = measuredAt, PayrollRunId = runId, Obligation = obligation,
                CarriedBefore = carried, Movement = movement, IsCatchUp = o.IsCatchUp,
                WageBase = Math.Round(o.WageBase, 2), ServiceYears = Math.Round(o.ServiceYears, 4),
                Currency = currency, Rule = o.Rule, CostCenterId = gl is null ? null : o.CostCentreId,
            });
            if (movement == 0m) continue;

            var label = o.IsCatchUp ? CatchUpLabel : movement > 0m ? MonthlyLabel : ReleaseLabel;
            var employeeRef = EosbProvisionLedger.EmployeeRef(o.EmployeeId);
            if (movement > 0m)
            {
                lines.Add(Line(string.Empty, provisionAccount, movement, label, employeeRef));
                if (o.IsCatchUp) Add(catchUpRise, gl is null ? null : o.CostCentreId, movement); else Add(monthlyRise, gl is null ? null : o.CostCentreId, movement);
            }
            else
            {
                lines.Add(Line(provisionAccount, string.Empty, -movement, label, employeeRef));
                Add(fall, gl is null ? null : o.CostCentreId, -movement);
            }
        }

        foreach (var (key, amount) in catchUpRise.OrderBy(kv => CcCode(kv.Key), StringComparer.Ordinal).Where(kv => kv.Value > 0m))
            lines.Add(Line(ExpenseAt(Real(key)), string.Empty, amount, CatchUpLabel, period, Real(key)));
        foreach (var (key, amount) in monthlyRise.OrderBy(kv => CcCode(kv.Key), StringComparer.Ordinal).Where(kv => kv.Value > 0m))
            lines.Add(Line(ExpenseAt(Real(key)), string.Empty, amount, MonthlyLabel, period, Real(key)));
        foreach (var (key, amount) in fall.OrderBy(kv => CcCode(kv.Key), StringComparer.Ordinal).Where(kv => kv.Value > 0m))
            lines.Add(Line(string.Empty, ExpenseAt(Real(key)), amount, ReleaseLabel, period, Real(key)));
        return (lines, snapshots);
    }
}
