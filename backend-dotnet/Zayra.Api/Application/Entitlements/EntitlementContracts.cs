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
/// <param name="DependantScope">A <see cref="DependantScopes"/> value.</param>
/// <param name="DependantsCovered">How many of the employee's recorded dependants this line covers today.</param>
/// <param name="GradeStandardDiffers">The grade's standard now differs from the frozen value — "reviewed at renewal".</param>
/// <param name="ReasonCode">Why the line is not eligible or not offered, as a block code; NULL when it is.</param>
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
    string? ReasonCode);

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

/// <summary>One grade cell in force for a company (the company override where one exists).</summary>
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
/// Called when a contract term becomes Active (ContractsController.UpdateStatus → Active), inside the same unit
/// of work, only for tenants with the <c>release_a</c> flag on. Registered as <c>IEnumerable</c>: R2 freezes
/// the package, R4 stamps the chain. Implementations add entities to the context; they never SaveChanges.
/// </summary>
public interface IContractTermLifecycle
{
    Task OnActivatedAsync(EmployeeContract contract, CancellationToken ct);
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
