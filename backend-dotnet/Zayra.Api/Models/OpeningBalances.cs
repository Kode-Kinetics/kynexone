using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ── Mid-year cutover: opening balances ───────────────────────────────────────
//
// A customer arriving in September carries balances in from whatever they ran before. Three of those
// balances have nowhere to live in this product today (loan/advance mid-life state, the EOSB provision
// on the CFO's balance sheet, and YTD statutory split by component), and none of the ones that DO have
// a home are distinguishable from figures this system earned.
//
// The doctrine here is one sentence: A CARRIED-IN FIGURE IS A FACT ABOUT THE PREVIOUS SYSTEM, AND MUST
// STAY LABELLED AS ONE FOREVER. An auditor asking "where did this employee's 40,000 of YTD gross come
// from" must get "carried in from SAP at the 2026-09-01 cutover, batch <id>, source record PAY-OB-001",
// not silence. That is what OpeningBalanceOrigin exists for, and it is why imported loans and leave
// balances are NOT flagged with a boolean on their own tables: a boolean answers "was this imported"
// and nothing else, while the provenance row answers when, from what, under which cutover, in which
// batch, and for how much.

/// <summary>
/// The cutover date for ONE LEGAL ENTITY. Deliberately per-company and not per-tenant: a group migrates
/// its entities in waves, and the Saudi manufacturing company going live on 1 September has nothing to
/// do with the DIFC holding company going live on 1 January. Every opening-balance import is validated
/// against the cutover of the company that owns the employee.
/// </summary>
public class CompanyCutover : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>The legal entity this cutover governs. Never null in practice — a cutover with no
    /// company is meaningless — but nullable to satisfy ICompanyScopedOperational's naming contract.</summary>
    public Guid? CompanyId { get; set; }
    /// <summary>
    /// The first day this product owns the payroll for this entity. Everything dated BEFORE it is
    /// history carried in; everything on or after it is earned here. Opening balances state the world
    /// as at the day before.
    /// </summary>
    public DateOnly CutoverDate { get; set; }
    /// <summary>The system being migrated from, as the consultant names it (SAP, Oracle, Workday, ZenHR…).</summary>
    public string SourceSystem { get; set; } = string.Empty;
    /// <summary>Planned | Active | Closed. Only Active accepts opening-balance imports.</summary>
    public string Status { get; set; } = CutoverStatuses.Planned;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public static class CutoverStatuses
{
    public const string Planned = "Planned";
    public const string Active = "Active";
    public const string Closed = "Closed";

    public static readonly string[] All = { Planned, Active, Closed };
}

/// <summary>
/// The accrued end-of-service liability an employee already carries at cutover — the 2310 provision line
/// on the balance sheet the customer is bringing with them.
///
/// <para>This is NOT a replacement for <c>KsaEndOfServiceCalculator</c> and does not feed it. The
/// calculator computes a gratuity from service dates; this records what the PREVIOUS system had already
/// provided for, so the provision does not restart at zero on day one and so the first month's movement
/// is (computed liability − carried provision) rather than the whole computed liability. Keeping them
/// apart is deliberate: a carried provision is an accounting fact, a computed gratuity is a statutory
/// entitlement, and conflating them would let an import change what an employee is legally owed.</para>
/// </summary>
public class EmployeeEosbOpeningBalance : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    /// <summary>Employee.Id — the int PK payroll keys on.</summary>
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    /// <summary>The date the provision was struck — normally the day before cutover.</summary>
    public DateOnly AsAtDate { get; set; }
    /// <summary>
    /// Service start recognised by the PREVIOUS employer or system, where it differs from this product's
    /// <c>Employee.ServiceStartDate</c>. Carries a TUPE-style transfer or an acquired book, which the
    /// calculator cannot otherwise represent. Null = no prior-service claim.
    /// </summary>
    public DateOnly? PriorServiceStartDate { get; set; }
    /// <summary>Months of service already recognised at AsAtDate. Reconciliation aid, not an input to pay.</summary>
    public decimal AccruedMonths { get; set; }
    /// <summary>The provision amount carried in. This is the number that must tie to the customer's
    /// trial balance, and the control total the preview reconciles on.</summary>
    public decimal AccruedAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string SourceRecordId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// Provenance for one carried-in row, wherever it landed.
///
/// <para>Loans, advances and leave balances are imported into the SAME tables that hold transactions
/// this product generated, because payroll has to consume them through exactly one code path — a
/// separate "imported loan" table would mean a second deduction path, and two deduction paths is how
/// you pay someone twice. The cost of that choice is that the rows are indistinguishable once written.
/// This table pays that cost back: one row per carried-in entity, keyed on (EntityType, EntityId),
/// naming the cutover, the batch, the source system and the amount as at cutover.</para>
///
/// <para>It is append-and-update-only and is never deleted when the underlying row changes. A loan
/// imported at 17,000 outstanding that is now at 4,250 still has an origin row saying 17,000 was
/// carried in, which is precisely the question an audit asks.</para>
/// </summary>
public class OpeningBalanceOrigin : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    /// <summary>See <see cref="OpeningBalanceEntityTypes"/>.</summary>
    public string EntityType { get; set; } = string.Empty;
    /// <summary>PK of the carried-in row in its own table.</summary>
    public Guid EntityId { get; set; }
    public DateOnly CutoverDate { get; set; }
    /// <summary>The amount as at cutover — frozen. Not maintained as the underlying row moves.</summary>
    public decimal CarriedAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string SourceRecordId { get; set; } = string.Empty;
    /// <summary>The MigrationImportBatch that carried it in.</summary>
    public Guid MigrationBatchId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public static class OpeningBalanceEntityTypes
{
    public const string Loan = "Loan";
    public const string Advance = "Advance";
    public const string LeaveBalance = "LeaveBalance";
    public const string EosbProvision = "EosbProvision";
    public const string PayrollYtd = "PayrollYtd";

    public static readonly string[] All = { Loan, Advance, LeaveBalance, EosbProvision, PayrollYtd };
}

/// <summary>
/// The accepted vocabulary for <c>PayrollOpeningBalance.BalanceType</c>.
///
/// <para>CRITICAL — the statutory and tax buckets are deliberately NOT spelled as variants of
/// <c>YTD_DEDUCTIONS</c>. <c>PayrollController.SumOpeningBalance</c> matches on BalanceType against a
/// fixed accepted set and adds the result to the payslip's YTD deductions figure. If GOSI-employee YTD
/// were imported as <c>YTD_DEDUCTIONS</c> it would be added to a total that ALREADY includes it via the
/// aggregate row, and every payslip would over-report YTD deductions by the statutory amount. These
/// keys are outside every accepted set, so they are carried, queryable and reportable without touching
/// a number the payroll engine computes.</para>
/// </summary>
public static class OpeningBalanceTypes
{
    // ── Aggregates consumed by PayrollController's payslip YTD ──────────────
    public const string YtdGross = "YTD_GROSS";
    public const string YtdDeductions = "YTD_DEDUCTIONS";
    public const string YtdNet = "YTD_NET";

    // ── Detail carried for statutory reconciliation and filing. Not summed into the payslip. ──
    /// <summary>Employee-side social insurance already contributed this year (GOSI / GPSSA / GRSIA).</summary>
    public const string YtdStatutoryEmployee = "YTD_STATUTORY_EE";
    /// <summary>Employer-side social insurance already contributed this year.</summary>
    public const string YtdStatutoryEmployer = "YTD_STATUTORY_ER";
    /// <summary>Income tax withheld year to date. Zero across the GCC today; required the moment a
    /// progressive-tax jurisdiction is added, and cheaper to carry now than to retrofit.</summary>
    public const string YtdTax = "YTD_TAX";
    /// <summary>Contributory/covered wage assessed year to date — the denominator a GOSI audit checks
    /// the contributions against.</summary>
    public const string YtdCoveredWage = "YTD_COVERED_WAGE";

    public static readonly string[] All =
    {
        YtdGross, YtdDeductions, YtdNet,
        YtdStatutoryEmployee, YtdStatutoryEmployer, YtdTax, YtdCoveredWage
    };

    /// <summary>The buckets PayrollController already folds into the payslip. Importing a detail row
    /// under one of these double-counts, so the importer rejects the combination loudly.</summary>
    public static readonly string[] PayslipAggregates = { YtdGross, YtdDeductions, YtdNet };

    public static bool IsKnown(string balanceType) =>
        All.Contains(balanceType, StringComparer.OrdinalIgnoreCase);
}
