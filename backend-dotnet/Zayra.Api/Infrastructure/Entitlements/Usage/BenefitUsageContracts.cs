namespace Zayra.Api.Infrastructure.Entitlements.Usage;

/// <summary>Who is acting: tenant, login, effective permissions and the companies the request may reach.</summary>
public sealed record BenefitUsageActor(Guid TenantId, Guid? UserId, IReadOnlyCollection<string> Permissions, Func<Guid?, bool> CanAccessCompany)
{
    public bool Has(string permission) => Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Existing permission keys only. HR requests for an employee with entitlements.manage; the employee requests for
/// themselves with ess.write; Finance decides with payroll.approve (the key that confirms a cash allowance); HR reads with
/// entitlements.read and Finance with payroll.approve.</summary>
public static class BenefitUsagePermissions
{
    public const string Read = "entitlements.read";
    public const string RequestForEmployee = "entitlements.manage";
    public const string RequestForSelf = "ess.write";
    public const string Decide = "payroll.approve";
}

/// <summary>A benefit request. <paramref name="RequestId"/> is chosen by the client: a retried submit is the same request.
/// The employee's own request (ESS) ignores <paramref name="EmployeeId"/>: the employee is the signed-in one.</summary>
public sealed record BenefitUsageRequestInput(
    Guid RequestId,
    int EmployeeId,
    string BenefitCode,
    DateOnly UsageDate,
    short? Units,
    decimal? Amount,
    string? EvidenceReference,
    Guid? DependantId = null,
    string? AcademicYear = null,
    string? ProviderName = null,
    string? Note = null,
    string? Currency = null);

/// <summary>Value set of the measure: tickets and items are counts, education is money.</summary>
public static class BenefitUsageMeasures
{
    public const string Units = "Units";
    public const string Amount = "Amount";
}

/// <summary>Stable refusal codes (the screens translate them).</summary>
public static class BenefitUsageErrors
{
    public const string NotAllowed = "usage_not_allowed";
    public const string NotFound = "usage_not_found";
    public const string WrongCompany = "usage_wrong_company";
    public const string NoEntitlement = "usage_no_entitlement";
    public const string NotConsumable = "usage_not_consumable";
    public const string EntitlementUnverified = "usage_entitlement_unverified";
    public const string PeriodClosed = "usage_period_closed";
    public const string OverEntitlement = "usage_over_entitlement";
    public const string WrongMeasure = "usage_wrong_measure";
    public const string CurrencyMismatch = "usage_currency_mismatch";
    public const string EvidenceRequired = "usage_evidence_required";
    public const string ReasonRequired = "usage_reason_required";
    public const string MakerCannotApprove = "usage_maker_cannot_approve";
    public const string AlreadyDecided = "usage_already_decided";
    public const string RequestIdConflict = "usage_id_conflict";
    public const string DependantRequired = "usage_dependant_required";
    public const string DependantInvalid = "usage_dependant_invalid";
    public const string DependantCap = "usage_dependant_cap";
    public const string AcademicYearRequired = "usage_academic_year_required";
    public const string TextTooLong = "usage_text_too_long";
}

/// <summary>A refused write or read. <see cref="Status"/> is the HTTP status the controller answers with.</summary>
public sealed class BenefitUsageRefusedException(string code, string message, int status = 409) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

/// <summary>One benefit for one employee for one period: the frozen entitlement, what is pending and approved, what is left.</summary>
/// <param name="PerBeneficiary">The amount or count each child or traveller has; NULL when the benefit is the employee's alone.</param>
/// <param name="CanRequest">A request can be made now (confirmed, period open, something left).</param>
public sealed record BenefitUtilisationDto(
    int EmployeeId,
    string EmployeeName,
    string? EmployeeCode,
    Guid CompanyId,
    string BenefitCode,
    string LabelEn,
    string LabelAr,
    string VerificationState,
    string Measure,
    string? Currency,
    decimal Entitlement,
    decimal Pending,
    decimal Approved,
    decimal Remaining,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string DependantScope,
    short? MaxDependants,
    decimal? PerBeneficiary,
    IReadOnlyList<BenefitUsageBeneficiaryDto> Beneficiaries,
    bool CanRequest);

/// <param name="DependantId">NULL = the employee.</param>
/// <param name="Named">Already has a request this period (counts towards the dependant limit).</param>
public sealed record BenefitUsageBeneficiaryDto(Guid? DependantId, string Name, decimal Cap, decimal Used, decimal Remaining, bool Named);

/// <summary>One request, as the HR and Finance screens list it.</summary>
public sealed record BenefitUsageRequestDto(
    Guid Id,
    int EmployeeId,
    string EmployeeName,
    Guid CompanyId,
    string BenefitCode,
    string LabelEn,
    string LabelAr,
    string Status,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly UsageDate,
    short? Units,
    decimal? Amount,
    string? Currency,
    Guid? DependantId,
    string? DependantName,
    string? AcademicYear,
    string? ProviderName,
    string? EvidenceReference,
    string? Note,
    Guid RequestedByUserId,
    string? RequestedByName,
    DateTime CreatedAtUtc,
    string? DecidedByName,
    DateTime? DecidedAtUtc,
    string? DecisionReason,
    bool CanDecide,
    bool CanCancel);

/// <summary>The employee's own view: their benefits (with their own dependants) and their own requests. No user or row ids but the
/// request's own, which the employee needs to withdraw it.</summary>
public sealed record EssBenefitUsageDto(IReadOnlyList<BenefitUtilisationDto> Benefits, IReadOnlyList<EssBenefitUsageRequestDto> Requests);

public sealed record EssBenefitUsageRequestDto(
    Guid Id,
    string BenefitCode,
    string LabelEn,
    string LabelAr,
    string Status,
    DateOnly UsageDate,
    short? Units,
    decimal? Amount,
    string? Currency,
    string? ForName,
    string? AcademicYear,
    string? ProviderName,
    string? DecisionReason,
    DateTime CreatedAtUtc,
    bool CanCancel);
