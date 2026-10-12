using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ─────────────────────────────────────────────────────────────────────────────
// CMP — merit planning. A cycle is planned in the company's currency, managers build worksheets for their
// own direct reports, worksheets are approved through the shared approval engine, and an approved award
// reaches pay of record ONLY through EmployeeChangeRequest(salaryBreakdown) → EffectiveChangeJobHandler →
// EmployeeSalaryBreakdownWriter. Nothing here writes employee_salary_structures. Design and formulas:
// docs/acceptance/capability-closure/CMP_DESIGN.md.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Cycle lifecycle (CMP_DESIGN.md §State machines). Every transition is a named service method.</summary>
public static class CompCycleStatuses
{
    public const string Draft = "Draft";
    public const string Planning = "Planning";
    public const string Review = "Review";
    public const string Approved = "Approved";
    public const string Applied = "Applied";
    public const string Cancelled = "Cancelled";
    public static readonly IReadOnlyList<string> All = [Draft, Planning, Review, Approved, Applied, Cancelled];
}

public static class CompWorksheetStatuses
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Returned = "Returned";
    public const string Approved = "Approved";
    public const string Cancelled = "Cancelled";
    public static readonly IReadOnlyList<string> All = [Draft, Submitted, Returned, Approved, Cancelled];
    public static bool IsEditable(string status) => status is Draft or Returned;
}

public static class CompProrationRules
{
    /// <summary>Eligible employees get the full award.</summary>
    public const string None = "None";
    /// <summary>Award × (eligible days ÷ performance-period days), factor rounded to 4 dp.</summary>
    public const string EligibleDays = "EligibleDays";
    public static readonly IReadOnlyList<string> All = [None, EligibleDays];
}

public static class CompBudgetPoolTypes
{
    /// <summary>Pool = round(Σ eligible monthly basic in cycle currency × percent ÷ 100, 2).</summary>
    public const string PercentOfEligibleBasic = "PercentOfEligibleBasic";
    /// <summary>Pool = a fixed monthly amount in cycle currency.</summary>
    public const string FixedAmount = "FixedAmount";
    public static readonly IReadOnlyList<string> All = [PercentOfEligibleBasic, FixedAmount];
}

/// <summary>
/// One merit cycle for one company.
///
/// <para>WHY A TABLE. No existing table holds a planning round with an effective date, a performance period, an
/// eligibility rule set, a currency and a lifecycle. <c>IncrementRecommendation</c> is one ad-hoc proposal per
/// employee with no budget, no currency and no cycle; <c>PerformanceCycle</c> rates people and never touches pay.
/// HR owns it; kept for as long as the salary rows its awards produced (employment + statutory minimum).</para>
/// </summary>
public class CompCycle : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = CompCycleStatuses.Draft;

    public DateOnly PerformancePeriodStart { get; set; }
    public DateOnly PerformancePeriodEnd { get; set; }
    /// <summary>Always the 1st of a month (CHECK): payroll pays a whole month at the latest row ≤ period end.</summary>
    public DateOnly EffectiveDate { get; set; }
    /// <summary>The company's DefaultCurrency, copied at creation and never changed.</summary>
    public string Currency { get; set; } = string.Empty;
    /// <summary>Only <c>Basic</c> today (CHECK). Allowances are carried unchanged.</summary>
    public string PayComponent { get; set; } = "Basic";

    // ── eligibility ──
    /// <summary>Joined after this date ⇒ not eligible. Null = no cutoff.</summary>
    public DateOnly? HireDateCutoff { get; set; }
    /// <summary>Comma-separated employee statuses that may be eligible (default "Active").</summary>
    public string EligibleStatuses { get; set; } = "Active";
    /// <summary>Grade/department include and exclude lists. JSON because nothing joins or reports on them: they are
    /// read whole when the eligibility rule is evaluated. Shape: {"includeGradeIds":[],"excludeGradeIds":[],
    /// "includeDepartmentIds":[],"excludeDepartmentIds":[]}.</summary>
    public string EligibilityFiltersJson { get; set; } = "{}";
    /// <summary>Fewer eligible days than this in the performance period ⇒ not eligible.</summary>
    public int MinimumEligibleDays { get; set; }
    public string ProrationRule { get; set; } = CompProrationRules.EligibleDays;

    /// <summary>Pay-equity view also breaks cohorts down by nationality (tenant choice, off by default).</summary>
    public bool EquityIncludesNationality { get; set; }

    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    public DateTime? PlanningOpenedAtUtc { get; set; }
    public Guid? PlanningOpenedByUserId { get; set; }
    public DateTime? ReviewStartedAtUtc { get; set; }
    public Guid? ReviewStartedByUserId { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public Guid? AppliedByUserId { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>Payroll periods (yyyy-MM, comma-separated) whose retro cost an approver acknowledged — at approve, or at
    /// apply when a period was run between approval and apply. Apply refuses while a paid period is not in this list.</summary>
    public string? RetroAcknowledgedPeriods { get; set; }
}

/// <summary>
/// A department pool for one cycle.
///
/// <para>WHY A TABLE. <c>Department.MonthlyBudgetAmount</c> is the department's whole salary envelope, undated and
/// cycle-less; a merit pool is per cycle, in the cycle's currency, and either a percentage of eligible payroll or a
/// fixed amount. HR sets it, Finance reads it. Lifetime = its cycle (cascade).</para>
/// </summary>
public class CompCycleBudget : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid CycleId { get; set; }
    public Guid DepartmentId { get; set; }
    public string PoolType { get; set; } = CompBudgetPoolTypes.PercentOfEligibleBasic;
    /// <summary>Percent (e.g. 3.5000 = 3.5 %) when PoolType is PercentOfEligibleBasic.</summary>
    public decimal? PoolPercent { get; set; }
    /// <summary>Monthly amount in cycle currency when PoolType is FixedAmount.</summary>
    public decimal? FixedAmount { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// One cell of the guideline matrix: performance rating × compa-ratio band → minimum / target / maximum increase %.
///
/// <para>WHY A TABLE. Nothing in the product holds an increase policy. Rows, not JSON: a worksheet line resolves
/// its cell by rating and band, and the matrix is audited cell by cell. Lifetime = its cycle (cascade).</para>
/// </summary>
public class CompGuideline : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid CycleId { get; set; }
    /// <summary>An appraisal FinalRating label as published (e.g. "Exceeds"), or "Unrated".</summary>
    public string Rating { get; set; } = string.Empty;
    /// <summary>Inclusive lower bound of the compa-ratio band (e.g. 0.8000).</summary>
    public decimal CompaRatioMin { get; set; }
    /// <summary>Exclusive upper bound; null = open-ended.</summary>
    public decimal? CompaRatioMax { get; set; }
    public decimal MinPercent { get; set; }
    public decimal TargetPercent { get; set; }
    public decimal MaxPercent { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A cycle FX rate: 1 unit of <see cref="FromCurrency"/> = <see cref="Rate"/> units of the cycle currency.
///
/// <para>WHY A TABLE. The product has no FX table at all. A cycle must normalise people paid in another currency
/// for budget and equity, with the rate's source, date and the person who entered it on record; a missing rate
/// blocks rather than being guessed. Lifetime = its cycle (cascade).</para>
/// </summary>
public class CompFxRate : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid CycleId { get; set; }
    public string FromCurrency { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateOnly RateDate { get; set; }
    public Guid? EnteredByUserId { get; set; }
    public string EnteredByName { get; set; } = string.Empty;
    public DateTime EnteredAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A manager's worksheet (one scenario) for one cycle.
///
/// <para>WHY A TABLE (exception to the CMP cap of 7, see CMP_DESIGN.md). The approval engine needs one subject per
/// submission — its status, revision, the approval request it raised, the manager it is about and the overspend
/// justification. Line rows cannot carry that without repeating it on every employee. Scenarios are named
/// worksheets; exactly one per manager and cycle is primary (partial unique), and only the primary can be
/// submitted. Lifetime = its cycle (cascade).</para>
/// </summary>
public class CompWorksheet : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid CycleId { get; set; }
    public int ManagerEmployeeId { get; set; }
    public string ScenarioName { get; set; } = "Main";
    public bool IsPrimary { get; set; }
    public string Status { get; set; } = CompWorksheetStatuses.Draft;
    /// <summary>Starts at 1; +1 each time a returned worksheet is reopened for revision.</summary>
    public int Revision { get; set; } = 1;
    /// <summary>Every user who changed this revision (saved, submitted, assigned, made primary), comma-separated. None
    /// of them may decide its approval. Cleared when a new revision starts.</summary>
    public string RevisionEditorIds { get; set; } = "";
    public string? OverspendJustification { get; set; }

    public void NoteEditor(Guid? userId)
    {
        if (userId is not Guid u || EditedBy(u)) return;
        RevisionEditorIds = RevisionEditorIds.Length == 0 ? u.ToString() : $"{RevisionEditorIds},{u}";
    }

    public bool EditedBy(Guid userId) =>
        RevisionEditorIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(userId.ToString(), StringComparer.OrdinalIgnoreCase);

    /// <summary>A returned worksheet starts a new revision with no editors yet.</summary>
    public void StartRevision()
    {
        Revision++;
        RevisionEditorIds = "";
    }
    public Guid? ApprovalRequestId { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComments { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>
/// One employee on one worksheet: what the planner saw (snapshot of pay of record, grade range, rating, FX),
/// what they proposed, and — once applied — the change request that carries it to payroll.
///
/// <para>WHY A TABLE. The award itself; nothing else holds a proposed increase with its basis. The snapshot columns
/// are "what the approver saw", re-checked against live pay of record at apply (a line whose basis moved is
/// refused, never re-based). The applied award is unique per (cycle, employee) in the database. Lifetime = its
/// worksheet (cascade) — except that an applied line's change request id is the link from pay of record back to
/// the decision, so a cycle with applied lines cannot be deleted (no delete path exists).</para>
/// </summary>
public class CompWorksheetLine : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid CycleId { get; set; }
    public Guid WorksheetId { get; set; }
    public int EmployeeId { get; set; }

    // ── snapshot of the basis (pay currency unless named *Cycle) ──
    public Guid? DepartmentId { get; set; }
    public Guid? GradeId { get; set; }
    public Guid? DesignationId { get; set; }
    public string PayCurrency { get; set; } = string.Empty;
    /// <summary>Cycle-currency units per 1 pay-currency unit (1 when the same currency).</summary>
    public decimal? FxRate { get; set; }
    public Guid? SalaryRowId { get; set; }
    public decimal CurrentBasic { get; set; }
    public decimal CurrentGross { get; set; }
    public decimal? CurrentBasicCycle { get; set; }
    public decimal? CurrentGrossCycle { get; set; }
    public decimal? CompaRatio { get; set; }
    public decimal? RangePosition { get; set; }
    public string Rating { get; set; } = "Unrated";
    public int EligibleDays { get; set; }
    public decimal ProrationFactor { get; set; }
    public decimal? GuidelineMinPercent { get; set; }
    public decimal? GuidelineTargetPercent { get; set; }
    public decimal? GuidelineMaxPercent { get; set; }

    // ── the proposal (cycle currency is authoritative; pay-currency figures are derived and stored) ──
    /// <summary>Full (un-prorated) monthly increase in cycle currency.</summary>
    public decimal ProposedIncrease { get; set; }
    /// <summary>The percent as ENTERED when <see cref="ProposedAsPercent"/>; otherwise derived: ProposedIncrease ÷
    /// CurrentBasicCycle × 100, 4 dp.</summary>
    public decimal ProposedPercent { get; set; }
    /// <summary>True when the planner entered a percent (the guideline is tested against it); false for an amount.</summary>
    public bool ProposedAsPercent { get; set; }
    /// <summary>round(ProposedIncrease × ProrationFactor, 2), cycle currency — what the budget consumes.</summary>
    public decimal ProratedIncrease { get; set; }
    /// <summary>round(ProratedIncrease ÷ FxRate, 2), pay currency — what is added to basic.</summary>
    public decimal ProratedIncreasePay { get; set; }
    public decimal NewBasic { get; set; }
    public decimal NewGross { get; set; }
    public decimal? NewCompaRatio { get; set; }
    public bool OutOfGuideline { get; set; }
    public string? Justification { get; set; }
    /// <summary>Comma-separated flag codes (CompFlags). Recomputed on every save.</summary>
    public string Flags { get; set; } = string.Empty;

    // ── application ──
    public Guid? ChangeRequestId { get; set; }
    public DateTime? ChangeRequestedAtUtc { get; set; }
    /// <summary>Why apply refused this line (plain language), when it did.</summary>
    public string? ApplyBlockedReason { get; set; }
    public Guid? ReversalChangeRequestId { get; set; }
    /// <summary>Set when HR assigned this employee to the worksheet explicitly (no manager on record, or the manager has
    /// no worksheet). Otherwise the line follows the reporting line.</summary>
    public Guid? AssignedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>Stable flag codes on a worksheet line. "Blocking" ones refuse submission.</summary>
public static class CompFlags
{
    public const string AboveRangeMax = "above_range_max";        // new gross above grade max — blocking (the salary writer refuses it)
    public const string BelowRangeMin = "below_range_min";        // new gross still below grade min — blocking (same reason)
    public const string NoGradeRange = "no_grade_range";          // no grade or no mid ⇒ no compa-ratio
    public const string MissingBenchmark = "missing_benchmark";
    public const string StaleBenchmark = "stale_benchmark";
    public const string OutOfScopeBenchmark = "out_of_scope_benchmark";
    public const string FxMissing = "fx_missing";                 // blocking
    public const string NoSalaryRow = "no_salary_row";            // blocking: pay of record has no salary row
    public const string ContractBoundPay = "contract_bound_pay";  // blocking: renewal/allowance-basis row
    public const string PendingSalaryChange = "pending_salary_change"; // blocking at apply; warned while planning
    public const string LaterSalaryRow = "later_salary_row";      // blocking: a row dated after the effective date
    public const string OutOfGuideline = "out_of_guideline";      // needs justification
    public const string Prorated = "prorated";

    public static readonly IReadOnlySet<string> Blocking = new HashSet<string>(StringComparer.Ordinal)
    {
        AboveRangeMax, BelowRangeMin, FxMissing, NoSalaryRow, ContractBoundPay, LaterSalaryRow,
    };
}

/// <summary>
/// The salary-row duplicate guard CMP adds to <c>employee_salary_structures</c> (register §2 P1: nothing stopped two
/// ACTIVE rows for one employee on one effective date, and payroll then picks one of them arbitrarily). EF cannot
/// model it, so the AddMeritPlanning migration runs this SQL and PostgresFixture runs the same constants.
///
/// <para><b>Why a DEFERRED exclusion constraint and not a plain unique index.</b> It enforces exactly the same rule
/// as <c>UNIQUE (tenant_id, employee_id, effective_date) WHERE is_active</c> (btree, equality only), but is checked
/// at COMMIT. The single salary writer deactivates the row it replaces and inserts the new one in one SaveChanges;
/// a non-deferrable index would fail that unit of work whenever EF happened to order the INSERT before the UPDATE.
/// Deferred, the order inside a transaction is irrelevant and two concurrent transactions still cannot both commit
/// an active row for the same date.</para>
///
/// <para><b>Pre-flight.</b> If production already holds duplicates the constraint cannot be added. The migration
/// checks first and FAILS with a message naming the count and the runbook query — it never deletes or deactivates
/// anything itself. Run <see cref="PreDeployDuplicateQuery"/> (read-only) before deploying.</para>
/// </summary>
public static class MeritPlanningSql
{
    public const string SalaryRowExclusionName = "ex_employee_salary_structures__one_active_per_date";

    /// <summary>Read-only. Lists every (tenant, employee, effective date) with more than one active salary row.</summary>
    public const string PreDeployDuplicateQuery =
        "SELECT tenant_id, employee_id, effective_date, count(*) AS active_rows, array_agg(id ORDER BY created_at_utc) AS row_ids " +
        "FROM employee_salary_structures WHERE is_active " +
        "GROUP BY tenant_id, employee_id, effective_date HAVING count(*) > 1 " +
        "ORDER BY tenant_id, employee_id, effective_date;";

    // The DDL itself (pre-flight, the deferred exclusion constraint, the effective-date CHECK) is declared in the
    // AddMeritPlanning migration, where DestructiveMigrationGuardTests can read it; PostgresFixture runs those constants.
}
