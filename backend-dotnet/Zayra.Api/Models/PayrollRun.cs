using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class PayrollRun : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string Status { get; set; } = "Draft";
    // POD-B2 — run TYPE. Exactly one non-voided Regular run may exist per (tenant, company, year, month);
    // any number of OffCycle/Supplementary/Correction runs may coexist for the same period.
    // Enforced by the partial unique indexes IX_payroll_runs_tenant_id_company_id_year_month and
    // IX_payroll_runs_tenant_id_year_month (the null-company companion), both filtered on run_type = 'Regular'.
    public string RunType { get; set; } = PayrollRunTypes.Regular;
    // POD-B2 — links a Correction/Supplementary run to the run it amends. Null for Regular/standalone runs.
    public Guid? ParentRunId { get; set; }
    // POD-B2 — pay BASIS, persisted on the run rather than derived from RunType, so an off-cycle run for a
    // missed joiner CAN pay full recurring salary while a mid-month bonus run does not. When false the run
    // pays supplemental items only (bonuses, adjustments, statutory on the supplemental base) and takes no
    // recurring deduction, consumes no attendance/leave/OT impact and decrements no loan/advance balance.
    // The real double-pay control is the ALREADY_PAID_THIS_PERIOD validation Error, not this flag.
    public bool IncludesRecurringPay { get; set; } = true;
    // POD-B2 — GL posting period ("yyyy-MM") for the ACCRUAL journal (Lock) and its void contra, when it
    // must differ from the pay period. Null (the default, and the only legal value for a Regular run) means
    // "post into the run's own period". A prior-period correction booked into the current open month sets
    // this; the run still REPORTS under Year/Month, only the journal moves.
    public string? GlPostingPeriod { get; set; }
    public decimal TotalGrossSalary { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal TotalNetSalary { get; set; }
    // Employer statutory cost (GOSI/GPSSA/GRSIA employer side) — not deducted from employee net.
    public decimal TotalEmployerStatutoryCost { get; set; }
    public int EmployeeCount { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? ProcessedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public DateTime? LockedAtUtc { get; set; }
    public string ErpPostingStatus { get; set; } = ErpPostingStatuses.NotReady;
    public DateTime? ErpPostingStatusChangedAtUtc { get; set; }
    public string? ErpPostingReference { get; set; }
    public string? ErpPostingFailureReason { get; set; }
    // Void tracking — populated only when Status == "Voided".
    public string? VoidReason { get; set; }
    public DateTime? VoidedAtUtc { get; set; }
    public Guid? VoidedByUserId { get; set; }
    public string? VoidedByName { get; set; }
}

/// <summary>
/// POD-B2 — payroll run types.
///
///   Regular      the monthly cycle. Exactly one non-voided Regular run per (tenant, company, year, month).
///   OffCycle     an out-of-band run inside the period (mid-month bonus, a missed joiner). Basis is
///                operator-selected: supplemental by default, full-recurring on request.
///   Supplementary an additive top-up to an already-run period. Supplemental basis.
///   Correction   amends a named parent run. Supplemental basis, ADDITIVE-ONLY in B2 — a negative delta
///                (clawback) is rejected at Process with 422 negative_net_unsupported and is POD-B3 work.
///   Replacement  POD-B3 — RE-RUNS a month whose run was voided. Full-recurring basis, period-OWNING
///                (it takes the voided run's place in the monthly cycle), and it REQUIRES a ParentRunId
///                naming a run that is already Voided. That ordering is the whole contract: recovery is a
///                two-phase, separately-audited act (void unwinds, replacement re-posts), and because the
///                parent is voided before the replacement exists, ALREADY_PAID_THIS_PERIOD cannot fire
///                (LoadSiblingRunsAsync excludes voided runs) and the partial unique index has a free slot.
///
/// A run that has posted a LIVE accrual is recovered by void → Replacement, never by re-processing the
/// same run: Process DELETES the slips/earnings/deductions that are the supporting detail behind journals
/// still visible in the ledger, and one run cannot carry two ERP documents or two payment batches. A run
/// that never locked has no accounting identity and is recovered in place by POST runs/{id}/reopen.
/// </summary>
public static class PayrollRunTypes
{
    public const string Regular       = "Regular";
    public const string OffCycle      = "OffCycle";
    public const string Supplementary = "Supplementary";
    public const string Correction    = "Correction";
    /// <summary>POD-B3 — the re-run of a month whose run was voided. See the type table above.</summary>
    public const string Replacement   = "Replacement";

    public static readonly string[] All = { Regular, OffCycle, Supplementary, Correction, Replacement };

    /// <summary>
    /// POD-B3 — types that OWN the period's monthly cycle. Exactly one non-voided period-owning run may
    /// exist per (tenant, company, year, month); the two partial unique indexes carry the same predicate
    /// (<c>run_type IN ('Regular','Replacement')</c>) so the database is the backstop for the API check.
    /// </summary>
    public static bool IsPeriodOwning(string? runType) =>
        string.Equals(runType, Regular, StringComparison.OrdinalIgnoreCase)
     || string.Equals(runType, Replacement, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// POD-B3 — must Process refuse until the operator has stated the population explicitly?
    ///
    /// True for the SUPPLEMENTAL types only: a bonus/correction run over the whole company would consume
    /// the period's bonuses and adjustments out from under the Regular run, so its population must be a
    /// deliberate act. A Replacement is period-OWNING — it is the month — so "everyone eligible" is the
    /// correct default, exactly as it is for a Regular run. (CreateRun still materialises the voided
    /// parent's paid population as Include rows when the parent produced slips, so a hold-out survives
    /// recovery; this predicate only decides whether the ABSENCE of a selector is an error.)
    /// </summary>
    public static bool RequiresExplicitPopulation(string? runType) =>
        IsNonRegular(runType) && !IsPeriodOwning(runType);

    /// <summary>
    /// Case-insensitive canonicalisation. Returns null for an unrecognised value so the caller can 400 —
    /// NEVER silently coerces to Regular (a typo'd type must not become the tenant's monthly run).
    /// A null/blank input means "not supplied" and canonicalises to Regular.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Regular;
        foreach (var t in All)
            if (string.Equals(t, value.Trim(), StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    public static bool IsValid(string? value) => Normalize(value) is not null;

    /// <summary>True for every type except Regular — i.e. the run does not own the period's monthly cycle.</summary>
    public static bool IsNonRegular(string? runType) =>
        !string.Equals(runType, Regular, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Default pay basis for a type. Regular is always full-recurring. Supplementary/Correction are always
    /// supplemental. OffCycle defaults to supplemental but the operator may opt into full recurring pay
    /// (the missed-joiner case) via CreatePayrollRunRequest.IncludesRecurringPay.
    ///
    /// <para>POD-B3: Replacement is full-recurring — it REPLACES the monthly cycle, so a supplemental
    /// basis would re-pay the month with no salary in it. Derived from <see cref="IsPeriodOwning"/> rather
    /// than a second literal list so the basis and the uniqueness rule can never drift apart.</para>
    /// </summary>
    public static bool DefaultIncludesRecurringPay(string runType) => IsPeriodOwning(runType);

    /// <summary>Only OffCycle lets the operator choose the basis; the others are fixed by their contract.</summary>
    public static bool AllowsBasisOverride(string runType) =>
        string.Equals(runType, OffCycle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>POD-B2 — Include/Exclude modes for the run population selector.</summary>
public static class PayrollRunSelectionModes
{
    public const string Include = "Include";
    public const string Exclude = "Exclude";

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (string.Equals(Include, value.Trim(), StringComparison.OrdinalIgnoreCase)) return Include;
        if (string.Equals(Exclude, value.Trim(), StringComparison.OrdinalIgnoreCase)) return Exclude;
        return null;
    }
}

/// <summary>POD-B2 — outcome stamped on a selection row by Process, so intent is never silently dropped.</summary>
public static class PayrollRunSelectionOutcomes
{
    public const string Included    = "Included";
    public const string Excluded    = "Excluded";
    public const string NotEligible = "NotEligible";
}

public static class ErpPostingStatuses
{
    public const string NotReady = "NotReady";
    public const string ReadyForErp = "ReadyForErp";
    public const string Exported = "Exported";
    public const string Posted = "Posted";
    public const string Rejected = "Rejected";

    public static readonly string[] All = { NotReady, ReadyForErp, Exported, Posted, Rejected };
}

public static class ErpPostingTransitions
{
    private static readonly IReadOnlyDictionary<string, string[]> Allowed =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [ErpPostingStatuses.NotReady] = new[] { ErpPostingStatuses.ReadyForErp },
            [ErpPostingStatuses.ReadyForErp] = new[] { ErpPostingStatuses.Exported, ErpPostingStatuses.Posted, ErpPostingStatuses.Rejected },
            [ErpPostingStatuses.Exported] = new[] { ErpPostingStatuses.Posted, ErpPostingStatuses.Rejected },
            [ErpPostingStatuses.Rejected] = new[] { ErpPostingStatuses.Exported, ErpPostingStatuses.Posted },
            [ErpPostingStatuses.Posted] = Array.Empty<string>(),
        };

    public static bool IsAllowed(string from, string to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to, StringComparer.OrdinalIgnoreCase);

    public static string[] AllowedFrom(string from) =>
        Allowed.TryGetValue(from, out var next) ? next : Array.Empty<string>();
}

public class PayrollSlip : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid RunId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public decimal BasicSalary { get; set; }
    public decimal HousingAllowance { get; set; }
    public decimal TransportAllowance { get; set; }
    public decimal OtherAllowances { get; set; }
    public decimal GrossSalary { get; set; }
    public decimal Deductions { get; set; }
    public decimal NetSalary { get; set; }
    public string Status { get; set; } = "Draft";
    // Statutory deduction totals — split for reporting without re-querying PayrollDeductions.
    // EmployeeStatutoryTotal reduces employee net pay; EmployerStatutoryTotal does NOT.
    public decimal EmployeeStatutoryTotal { get; set; }
    public decimal EmployerStatutoryTotal { get; set; }
    // Compliance: YTD accumulators (populated during Process, from all prior locked runs in same year)
    public decimal YtdGross { get; set; }
    public decimal YtdDeductions { get; set; }
    public decimal YtdNet { get; set; }
    // Compliance: loan/advance deductions this period (for payslip line-item)
    public decimal LoanDeductions { get; set; }
}
