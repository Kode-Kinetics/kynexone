using Zayra.Api.Models;

namespace Zayra.Api.Application.Entitlements;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════
// Release A shared contracts (plan §1.2), fixed by the integration owner in R0 before parallel work.
// Slices implement these; they do not change them. A change here is an integration-owner decision and
// fails ReleaseAContractTests, which pins every member listed below.
//
// Employee id convention: the API speaks the int employees.id. employee_entitlements and
// contract_renewal_cases store the uuid PublicId (to FK onto employee_contracts, whose employee_id is the
// PublicId). Convert at the edge, never inside a query that crosses both.
//
// Per-employee lock: a transaction-level advisory lock, FinanceDecisionSerializer scope
// FinanceDecisionSerializer.ScopeEmployeePackage ("employee.package"). Fixed order inside it:
// case row → contract → salary → entitlement rows.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Reads what an employee is entitled to, and what their grade's standard is. Read-only. Slice R2.</summary>
public interface IEntitlementResolver
{
    /// <summary>The employee's whole package as of <paramref name="asOf"/> (tenant-local date).</summary>
    Task<EmployeePackage> ResolveAsync(Guid tenantId, int employeeId, DateOnly asOf, CancellationToken ct);

    /// <summary>The grade's standard for one company: the company cell where one exists, else the tenant cell.</summary>
    Task<IReadOnlyList<GradeStandardLine>> GradeStandardAsync(Guid tenantId, Guid gradeId, Guid companyId, DateOnly asOf, CancellationToken ct);
}

/// <summary>One employee's package on one date.</summary>
/// <param name="EmployeeId">The int employees.id.</param>
/// <param name="TermEndsOn">End of the contract term the Contractual lines are frozen for (NULL = indefinite or none).</param>
/// <param name="BlockCodes">Codes from <c>ReleaseABlockReasons</c> (e.g. GRADE_MISSING) — never shown raw.</param>
public sealed record EmployeePackage(
    int EmployeeId,
    Guid? GradeId,
    Guid? ContractId,
    DateOnly? TermEndsOn,
    DateOnly AsOf,
    IReadOnlyList<PackageLine> Lines,
    IReadOnlyList<string> BlockCodes);

/// <summary>One line of a package.</summary>
/// <param name="Class">A <see cref="PayEntitlementClasses"/> value.</param>
/// <param name="Floor">A <see cref="PayStatutoryFloors"/> value.</param>
/// <param name="Source">A <see cref="PackageLineSources"/> value: where the figure was read from.</param>
/// <param name="Offered">False when the employee's company skips this component.</param>
/// <param name="MonthlyCash">Cash per month this line pays (QiwaWage lines only); NULL for non-cash lines.</param>
/// <param name="Quantity">A count (tickets per period). Never the education child cap.</param>
/// <param name="DependantScope">A <see cref="DependantScopes"/> value.</param>
/// <param name="MaxDependants">The most dependants covered. Education: the child cap is here (scope Children), not in Quantity.</param>
/// <param name="DependantsCovered">How many of the employee's recorded dependants this line covers today.</param>
/// <param name="GradeStandardDiffers">The grade's standard now differs from the frozen value — "reviewed at renewal".</param>
/// <param name="ReasonCode">Why the line is not eligible or not offered, as a block code; NULL when it is.</param>
/// <param name="MaxOutstandingAmount">Facility only: the most that may be owed at once (e.g. one housing advance).</param>
/// <param name="ResolvedAmount">The cash figure the line comes to today, for a Facility or a rate (e.g. 3 × housing = SAR 6,000;
/// 25% of basic = SAR 2,000). NULL when the line is not a sum of money.</param>
/// <param name="EligibleFrom">When an ineligible line becomes eligible by a criterion (service months, end of probation);
/// NULL when eligible now or never by date.</param>
/// <param name="StandardValue">The grade's standard for this component today (the company cell where one exists), so the
/// offer editor and the "Why?" popover can show it beside the frozen value; NULL when the grade has no cell.</param>
public sealed record PackageLine(
    string ComponentCode,
    string Class,
    string Floor,
    string Source,
    bool Offered,
    bool Eligible,
    string? ValueType,
    decimal? Amount,
    decimal? Rate,
    decimal? MonthlyCash,
    string? CoverageTier,
    short? Quantity,
    string DependantScope,
    short? MaxDependants,
    int DependantsCovered,
    string? LimitPeriod,
    Guid? GradeEntitlementId,
    Guid? EmployeeEntitlementId,
    bool IsCompanyOverride,
    bool GradeStandardDiffers,
    string? ReasonCode,
    decimal? MaxOutstandingAmount,
    decimal? ResolvedAmount,
    DateOnly? EligibleFrom,
    GradeStandardLine? StandardValue);

/// <summary>
/// The one rule for comparing rates (plan §1.2, round 2). Grade cells and frozen rows store <c>rate</c> as numeric(9,4);
/// the salary row stores housing/transport rates as numeric(9,6). Every comparison across them — notably
/// <see cref="PackageLine.GradeStandardDiffers"/> — rounds both sides to 4 decimal places first, so 0.250000 and 0.2500
/// are the same rate and storage precision never reads as a difference.
/// </summary>
public static class EntitlementRates
{
    public const int ComparisonScale = 4;

    public static decimal? Normalise(decimal? rate) =>
        rate is { } r ? Math.Round(r, ComparisonScale, MidpointRounding.AwayFromZero) : null;

    public static bool Same(decimal? a, decimal? b) => Normalise(a) == Normalise(b);
}

/// <summary>Value set of <see cref="PackageLine.Source"/>.</summary>
public static class PackageLineSources
{
    /// <summary>QiwaWage cash, read from the salary row in force.</summary>
    public const string Salary = "Salary";
    /// <summary>A Contractual row frozen for the current term (employee_entitlements).</summary>
    public const string ContractFrozen = "ContractFrozen";
    /// <summary>Not frozen yet: the grade cell, shown as a preview.</summary>
    public const string GradeStandard = "GradeStandard";
    /// <summary>A Facility read from the cell as of today (policy, not contract).</summary>
    public const string Facility = "Facility";
    public static readonly string[] All = [Salary, ContractFrozen, GradeStandard, Facility];
}

/// <summary>One grade cell in force for a company (the company override where one exists). Quantity is a count (tickets);
/// the education child cap is <c>MaxDependants</c> with scope Children.</summary>
public sealed record GradeStandardLine(
    string ComponentCode,
    string Class,
    string Floor,
    bool Offered,
    bool Eligible,
    string ValueType,
    decimal? Amount,
    decimal? Rate,
    decimal? MaxOutstandingAmount,
    string? CoverageTier,
    short? Quantity,
    string DependantScope,
    short? MaxDependants,
    string? LimitPeriod,
    short? MinServiceMonths,
    bool AfterProbation,
    string NationalityScope,
    Guid GradeEntitlementId,
    bool IsCompanyOverride,
    DateOnly EffectiveFrom);

/// <summary>
/// The ONLY writer of <c>employee_entitlements</c>. Slice R2 implements all three; slice R6 calls the last two
/// inside its own Apply / holdover transaction (the writer never opens a transaction of its own when one is
/// active, and never calls SaveChanges — the caller owns the unit of work).
/// </summary>
public interface IEntitlementWriter
{
    /// <summary>Freeze the Contractual package for a contract term from the grade cells in force at its start. Idempotent. R2.</summary>
    Task<FreezeResult> FreezeTermAsync(Guid tenantId, Guid contractId, CancellationToken ct);

    /// <summary>Write the approved renewal package for the new term and close the prior rows at the old end. R2 implements; R6 calls.</summary>
    Task ApplyRenewalAsync(Guid tenantId, RenewalApplyPlan plan, CancellationToken ct);

    /// <summary>Copy the expiring term's Contractual rows into a provisional successor term as Carried rows. R2 implements; R6 calls.</summary>
    Task CarryToProvisionalAsync(Guid tenantId, Guid fromContractId, Guid provisionalContractId, CancellationToken ct);
}

/// <param name="Frozen">Rows were written by this call.</param>
/// <param name="AlreadyFrozen">The term already had its rows; nothing was written.</param>
public sealed record FreezeResult(bool Frozen, bool AlreadyFrozen, int RowsWritten);

/// <summary>Everything <see cref="IEntitlementWriter.ApplyRenewalAsync"/> needs, resolved and validated by R6 before the call.</summary>
/// <param name="EmployeeId">The employee's PublicId.</param>
/// <param name="NewContractId">The new (or promoted provisional) term the rows belong to.</param>
/// <param name="NewTermEndsOn">NULL for ConvertIndefinite.</param>
/// <param name="ApprovalRequestId">The approved offer; required for any Exception line.</param>
public sealed record RenewalApplyPlan(
    Guid CaseId,
    Guid EmployeeId,
    Guid CompanyId,
    Guid ExpiringContractId,
    Guid NewContractId,
    DateOnly NewTermStartsOn,
    DateOnly? NewTermEndsOn,
    Guid? ApprovalRequestId,
    IReadOnlyList<RenewalApplyLine> Lines);

/// <summary>One component of an approved renewal offer.</summary>
/// <param name="Action">A <see cref="RenewalLineActions"/> value.</param>
/// <param name="Source">GradeDefault (matches the cell) or Exception (differs; needs the approval).</param>
public sealed record RenewalApplyLine(
    string ComponentCode,
    string Action,
    string EntitlementClass,
    string ValueType,
    decimal? Amount,
    decimal? Rate,
    decimal? MaxOutstandingAmount,
    string? CoverageTier,
    short? Quantity,
    string DependantScope,
    short? MaxDependants,
    string? LimitPeriod,
    string Source,
    Guid? GradeEntitlementId,
    decimal? ResolvedAmount,
    Guid? ResolvedBasisSalaryId);

/// <summary>Per-component offer actions (rev 8.3 §1: Keep / Remove / Lower / Raise).</summary>
public static class RenewalLineActions
{
    public const string Keep = "Keep";
    public const string Remove = "Remove";
    public const string Lower = "Lower";
    public const string Raise = "Raise";
    public static readonly string[] All = [Keep, Remove, Lower, Raise];
    /// <summary>Actions that reduce an acquired right: need a reason code and evidence, and are barred during a dispute.</summary>
    public static readonly string[] Reductions = [Remove, Lower];
}

/// <summary>
/// Called when a contract term starts or stops being the term in force, inside the same unit of work, only for
/// tenants with the <c>release_a</c> flag on. Registered as <c>IEnumerable</c>: R2 freezes / closes the package, R4
/// stamps the chain and cancels an open case (T21). Implementations add or change entities; they never SaveChanges.
/// Callers: ContractsController (UpdateStatus, Supersede) and the migration import of contracts.
/// </summary>
public interface IContractTermLifecycle
{
    /// <summary>The term became Active (UpdateStatus → Active, or imported as / changed to Active).</summary>
    Task OnActivatedAsync(EmployeeContract contract, CancellationToken ct);

    /// <summary>
    /// An Active term ended: <paramref name="reason"/> is a <see cref="ContractEndReasons"/> value. What a hook may do
    /// depends on the reason (CTO decision):
    /// <list type="bullet">
    /// <item><b>Terminated, Separated, Superseded</b> — the employment or the term really ended: an open renewal case for
    /// it is cancelled (T21) and the package rows are closed.</item>
    /// <item><b>Expired</b> — NEVER cancels an open case and never closes the package. An expired fixed-term contract
    /// with the employee still working renews by operation of law (Art. 74(2); Art. 37/55); the holdover (T22, R6)
    /// writes the provisional successor term. A hook may only record the event (e.g. flag "expired with no outcome").</item>
    /// </list>
    /// </summary>
    Task OnEndedAsync(EmployeeContract contract, string reason, CancellationToken ct);
}

/// <summary>Why an Active term stopped being in force. See <see cref="IContractTermLifecycle.OnEndedAsync"/>.</summary>
public static class ContractEndReasons
{
    /// <summary>ContractsController.UpdateStatus → Terminated. Cancels an open case (T21).</summary>
    public const string Terminated = "Terminated";
    /// <summary>ContractsController.UpdateStatus → Expired (and the import). Record only: NEVER cancels a case (Art. 74(2)).</summary>
    public const string Expired = "Expired";
    /// <summary>ContractsController.Supersede (and the import). Cancels an open case (T21).</summary>
    public const string Superseded = "Superseded";
    /// <summary>OffboardingController completion (the separation is final: settlement paid, employee archived), for
    /// every Active term of the employee. Cancels an open case (T21).</summary>
    public const string Separated = "Separated";

    /// <summary>The reasons that cancel an open renewal case (T21). Expired is deliberately absent.</summary>
    public static readonly string[] CancelOpenCase = [Terminated, Separated, Superseded];
    public static readonly string[] All = [Terminated, Expired, Superseded, Separated];
}

/// <summary>The four renewal deadlines of an expiring term, from statutory_rules (tenant row overrides platform). Slice R4.</summary>
public interface IRenewalDeadlineCalculator
{
    Task<RenewalDeadlines> ComputeAsync(Guid tenantId, EmployeeContract expiring, CancellationToken ct);
}

/// <summary>Frozen on the case at open. Formulas: <c>Application/Contracts/RenewalRules.RenewalDeadlineFormulas</c>.</summary>
/// <param name="QiwaRuleId">The statutory_rules row the Qiwa response window came from.</param>
public sealed record RenewalDeadlines(
    DateOnly OpensOn,
    DateOnly NoticeDueOn,
    DateOnly OfferDueOn,
    DateOnly QiwaSubmitDueOn,
    DateOnly QiwaGateDueOn,
    int QiwaResponseDays,
    Guid? QiwaRuleId);

/// <summary>Who a deduction statement is for: HR sees every line; the employee sees their own whitelisted view.</summary>
public enum DeductionAudience
{
    Hr,
    Employee,
}

/// <summary>The deductions on one payslip with their legal basis, balance and the Art. 93 cap. Slice R3.</summary>
public interface IDeductionStatementService
{
    Task<DeductionStatement> ForSlipAsync(Guid tenantId, Guid slipId, DeductionAudience who, CancellationToken ct);
}

/// <param name="WageDue">The wage the Art. 93 cap is computed on.</param>
/// <param name="DebtTotal">Deductions that count toward the cap.</param>
/// <param name="CapLimit">50% of <paramref name="WageDue"/> (GOSI excluded — owner decision).</param>
/// <param name="Flags">Block codes, e.g. DEDUCTIONS_OVER_HALF_WAGE.</param>
public sealed record DeductionStatement(
    Guid SlipId,
    int EmployeeId,
    int Year,
    int Month,
    decimal WageDue,
    decimal DebtTotal,
    decimal CapLimit,
    decimal Headroom,
    IReadOnlyList<DeductionStatementLine> Lines,
    IReadOnlyList<string> Flags);

/// <param name="Category">A <see cref="DeductionCategories"/> value.</param>
/// <param name="LegalBasisKey">An i18n key for the Art. 92 clause; never free text.</param>
/// <param name="PercentOfWage">Loan lines: instalment ÷ wage, for the 10%-without-consent rule.</param>
public sealed record DeductionStatementLine(
    string ComponentCode,
    string Label,
    string Category,
    decimal Amount,
    bool CountsTowardCap,
    string LegalBasisKey,
    Guid? LoanId,
    decimal? BalanceAfter,
    int? InstalmentsRemaining,
    decimal? PercentOfWage,
    bool? ConsentOnFile);

/// <summary>Value set of <see cref="DeductionStatementLine.Category"/>.</summary>
public static class DeductionCategories
{
    public const string Statutory = "Statutory";
    public const string EmployerLoan = "EmployerLoan";
    public const string SalaryAdvance = "SalaryAdvance";
    public const string Absence = "Absence";
    public const string PenaltyOrAdjustment = "PenaltyOrAdjustment";
    public const string CourtOrder = "CourtOrder";
    public const string Other = "Other";
    public static readonly string[] All = [Statutory, EmployerLoan, SalaryAdvance, Absence, PenaltyOrAdjustment, CourtOrder, Other];
}
