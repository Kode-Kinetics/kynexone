using System.Text.Json;
using System.Text.Json.Serialization;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R1 — the shapes the benefits-by-grade matrix API reads and writes. Owned by R1.

/// <summary>Who is acting, as the matrix service needs it. Built by the controller from the request.</summary>
/// <param name="IsFinance">The caller holds the Finance permission (<c>payroll.approve</c>): a cash allowance they add is confirmed at once, and only
/// such a caller reaches the confirm and correct actions (the controller's permission gate). Never read from a request body.</param>
public sealed record MatrixActor(Guid TenantId, Guid? UserId, EntityScopeContext Scope, string? IpAddress, bool IsFinance = false);

/// <summary>A service outcome the controller returns as-is: an HTTP status and its body.</summary>
public sealed record MatrixResult(int Status, object Body)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>A block reason as the UI renders it: plain sentences in both languages, never the raw code alone.</summary>
public sealed record BlockReasonDto(string Code, string TitleEn, string TitleAr, string WhyEn, string WhyAr, string FixEn, string FixAr)
{
    public static BlockReasonDto? For(string? code) =>
        code is not null && ReleaseABlockReasons.All.TryGetValue(code, out var r)
            ? new BlockReasonDto(r.Code, r.TitleEn, r.TitleAr, r.WhyEn, r.WhyAr, r.FixEn, r.FixAr)
            : null;
}

public sealed record MatrixGradeDto(Guid Id, string Code, string Name, string? NameAr, int Level);

/// <param name="Group">Wage (paid monthly through payroll), Contract (fixed per contract year) or Facility (read when used).</param>
/// <param name="CanBeSkipped">Whether a company may stop offering it from this screen. False for statutory floors, loans
/// (offered per company in Loans) and wage components (a company row would shadow the payroll catalogue).</param>
/// <param name="FloorReason">Why a statutory-floor row is locked, in both languages.</param>
public sealed record MatrixComponentDto(
    string Code, string NameEn, string NameAr, string Class, string Floor, string Group,
    IReadOnlyList<string> AllowedValueTypes, IReadOnlyList<string> AllowedCoverageTiers, IReadOnlyList<string> AllowedDependantScopes,
    IReadOnlyList<string> AllowedLimitPeriods, string? DefaultLimitPeriod,
    bool IsFloor, bool CanBeSkipped, bool IsLoanFacility, BlockReasonDto? FloorReason, bool IsCustom = false,
    CashAllowanceInfoDto? CashAllowance = null, IReadOnlyList<AllowanceWarningDto>? Warnings = null, BenefitDefinitionDto? Definition = null);

/// <summary>What a customer-added benefit is, beyond its name (feature <c>benefit_definition_v1</c>; null on every component while the feature is off).</summary>
/// <param name="Delivery">Coverage (a contract benefit, <c>BEN_*</c>) or SalaryAllowance (a monthly cash allowance, <c>ALW_*</c>). Derived from the code, never a second column.</param>
/// <param name="OfferedCompanyIds">The active companies that offer it today. Cash allowances are always offered to every company.</param>
/// <param name="Version">An opaque token for the optimistic-concurrency check of an edit (<c>expectedVersion</c>): changes whenever the definition does.</param>
/// <param name="CodeLocked">True once any grade value, package line or salary line uses the code: it can no longer be renamed.</param>
public sealed record BenefitDefinitionDto(
    string Delivery, string? Category, string? PolicyClass, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, bool IsActive, bool RequiresEnrolment,
    IReadOnlyList<Guid> OfferedCompanyIds, string Version, Guid? SourcePlanId, bool CodeLocked);

/// <summary>What this API can do, so a newer screen against an older API (or a tenant without the feature) hides what it cannot rely on.</summary>
public sealed record MatrixCapabilitiesDto(bool BenefitDefinition);

/// <summary>What Finance and the grid need to know about a customer-defined cash allowance (<c>ALW_*</c>): whether it is still waiting for Finance to
/// confirm it, and the GL driver and end-of-service setting in effect (for the version dated <paramref name="EffectiveFrom"/>; null = since the start).</summary>
public sealed record CashAllowanceInfoDto(
    bool FinancePending, DateTime? FinanceConfirmedAtUtc, string? GlDriverKey, bool EosbIncluded, string? EosbExclusionCategory,
    string? EosbExclusionReason, DateOnly? EffectiveFrom);

/// <summary>A non-blocking caution about a cash allowance's end-of-service or GOSI treatment. <c>Code</c> is stable for the UI; <c>Message</c> is plain words.</summary>
public sealed record AllowanceWarningDto(string Code, string Message);

/// <param name="Inherited">A company view showing the group default (no company value of its own).</param>
/// <param name="NextChangeOn">The start of a later, already-published version in the same scope, if any.</param>
public sealed record MatrixCellDto(
    Guid GradeId, string ComponentCode, Guid CellId, bool IsCompanyOverride, bool Inherited, bool Eligible, string ValueType,
    decimal? Amount, decimal? Rate, decimal? MaxOutstandingAmount, string? CoverageTier, short? Quantity, string DependantScope,
    short? MaxDependants, string? LimitPeriod, short? MinServiceMonths, bool AfterProbation, string NationalityScope,
    string? NationalityBasis, string? SourceRule, string? Note, DateOnly EffectiveFrom, DateOnly? EffectiveTo, DateOnly? NextChangeOn);

/// <param name="Mode">Group (the tenant view), Adopted (the group values, no company cells), Tailored (at least one company
/// cell) or Skipped (the company does not offer it). Derived, never stored.</param>
/// <param name="ChangesOn">A scheduled skip or re-offer, if one is already published for a later month.</param>
public sealed record MatrixOfferingDto(string ComponentCode, string Mode, bool Offered, DateOnly? SkippedFrom, DateOnly? ChangesOn, bool CanBeSkipped);

/// <summary>A grade with no value in force — not even "Not offered" — for a component that applies to it.</summary>
/// <param name="ScheduledFrom">A value is already published and starts on this date; until then the grade has none.</param>
public sealed record MatrixGapDto(Guid GradeId, string ComponentCode, DateOnly? ScheduledFrom = null);

public sealed record EntitlementMatrixDto(
    DateOnly AsOf, DateOnly Today, Guid? CompanyId, string? Currency,
    IReadOnlyList<MatrixGradeDto> Grades, IReadOnlyList<MatrixComponentDto> Components, IReadOnlyList<MatrixCellDto> Cells,
    IReadOnlyList<MatrixOfferingDto> Offerings, IReadOnlyList<MatrixGapDto> Gaps, MatrixCapabilitiesDto? Capabilities = null);

/// <summary>One cell to publish. A class (not a record) so unknown JSON properties are caught: a banned criterion
/// such as "gender" is refused out loud (ENTITLEMENT_BANNED_CRITERION), never silently dropped.</summary>
public sealed class MatrixCellInput
{
    public Guid GradeId { get; set; }
    public string ComponentCode { get; set; } = string.Empty;
    public bool Eligible { get; set; }
    public string? ValueType { get; set; }
    public decimal? Amount { get; set; }
    /// <summary>PercentOfBasic as a fraction: 0.25 = 25%.</summary>
    public decimal? Rate { get; set; }
    public string? CoverageTier { get; set; }
    public short? Quantity { get; set; }
    public string? DependantScope { get; set; }
    public short? MaxDependants { get; set; }
    public string? LimitPeriod { get; set; }
    public short? MinServiceMonths { get; set; }
    public bool AfterProbation { get; set; }
    public string? NationalityScope { get; set; }
    public string? NationalityBasis { get; set; }
    public string? Note { get; set; }
    /// <summary>Company scope only: drop this company's own value from the effective date, so the group default applies again.</summary>
    public bool UseGroupDefault { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unrecognised { get; set; }
}

public sealed record PublishMatrixRequest(Guid? CompanyId, DateOnly EffectiveFrom, List<MatrixCellInput>? Cells);

/// <summary>What a benefit the customer adds is: a non-paying contract benefit (<c>BEN_*</c>) or a monthly cash allowance paid in salary (<c>ALW_*</c>).</summary>
public static class BenefitKinds
{
    public const string Contract = "Contract";
    public const string Cash = "Cash";
}

/// <summary>A benefit the customer adds to Benefits by grade. The English name is required (it makes the code); Arabic is optional.
/// <paramref name="Kind"/> is <see cref="BenefitKinds.Contract"/> (the default) or <see cref="BenefitKinds.Cash"/>. A cash allowance also needs the
/// GL driver its expense posts to, and — when it is left out of the end-of-service wage — the reason.</summary>
public sealed record CreateBenefitRequest(
    string? NameEn, string? NameAr, string? Kind = null, string? GlDriverKey = null,
    bool EosbIncluded = true, string? EosbExclusionReason = null, string? EosbExclusionCategory = null)
{
    // ── Benefit definition (feature benefit_definition_v1). Every one is optional; a request without them is the old three-field body. ──

    /// <summary>Optional code of the customer's own, without the prefix (<c>BEN_</c> / <c>ALW_</c> is added by the server). Letters, digits and underscores, starting with a letter.</summary>
    public string? Code { get; init; }
    /// <summary>Coverage (a contract benefit) or SalaryAllowance (a monthly cash allowance). Reimbursement and PayrollDeduction are refused (<c>delivery_not_supported</c>).</summary>
    public string? Delivery { get; init; }
    /// <summary>Monthly is the only frequency a cash allowance has; Annual and OneTime are refused (<c>delivery_not_supported</c>). None for coverage.</summary>
    public string? Frequency { get; init; }
    public string? Category { get; init; }
    /// <summary>Contractual or Discretionary. Mandatory is refused (<c>policy_class_invalid</c>).</summary>
    public string? PolicyClass { get; init; }
    public DateOnly? EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public bool? IsActive { get; init; }
    /// <summary>Always false: Release A derives the package from the grade. True is refused (<c>delivery_not_supported</c>).</summary>
    public bool? RequiresEnrolment { get; init; }
    /// <summary>Which companies offer it. Absent or <c>companyIds</c> null = every company.</summary>
    public BenefitScopeInput? Scope { get; init; }
    /// <summary>A client-generated id: a replay of the same request by the same person returns the original result instead of 409 benefit_exists.</summary>
    public Guid? RequestId { get; init; }

    /// <summary>Any JSON property the contract does not know. A request carrying one is refused (<c>unrecognised_field</c>), never silently dropped.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unrecognised { get; init; }

    /// <summary>Whether the body carries anything beyond the old fields. Used to refuse a definition field while the feature is off.</summary>
    internal bool CarriesDefinitionFields =>
        Code is not null || Delivery is not null || Frequency is not null || Category is not null || PolicyClass is not null || EffectiveFrom is not null
        || EffectiveTo is not null || IsActive is not null || RequiresEnrolment is not null || Scope is not null || RequestId is not null;
}

/// <summary>The companies that offer a contract benefit. <c>CompanyIds</c> null = every company; a list = those companies only (the others skip it from the effective date).</summary>
public sealed record BenefitScopeInput(List<Guid>? CompanyIds);

/// <summary>Edits a benefit's definition. A full replacement of the editable fields: a field left null is cleared (category, policy class, dates);
/// <c>IsActive</c> null and <c>Scope</c> null mean unchanged. <c>ExpectedVersion</c> is the definition's <c>version</c> as the screen read it (409 <c>stale_definition</c> otherwise).
/// The kind and delivery cannot change; the code only while nothing uses it (contract benefits only); the company scope is switched in the offerings.</summary>
public sealed record UpdateBenefitRequest(string? ExpectedVersion, string? NameEn, string? NameAr)
{
    public string? Code { get; init; }
    public string? Kind { get; init; }
    public string? Delivery { get; init; }
    public string? Frequency { get; init; }
    public string? Category { get; init; }
    public string? PolicyClass { get; init; }
    public DateOnly? EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public bool? IsActive { get; init; }
    public bool? RequiresEnrolment { get; init; }
    public BenefitScopeInput? Scope { get; init; }
    public Guid? RequestId { get; init; }
    /// <summary>Cash allowance only, and only while it is still pending Finance. A confirmed allowance's driver and end-of-service setting change through Finance's correction.</summary>
    public string? GlDriverKey { get; init; }
    public bool? EosbIncluded { get; init; }
    public string? EosbExclusionCategory { get; init; }
    public string? EosbExclusionReason { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unrecognised { get; init; }
}

/// <summary>Finance confirms a cash allowance somebody without Finance rights added. Every field is optional: left out, it is confirmed as it was entered.
/// Finance may change the GL driver and the end-of-service setting while confirming (the same rules as adding it).</summary>
public sealed record ConfirmAllowanceRequest(
    string? GlDriverKey = null, bool? EosbIncluded = null, string? EosbExclusionCategory = null, string? EosbExclusionReason = null);

/// <summary>Finance changes a confirmed cash allowance's GL driver and/or end-of-service setting from the first day of a month (never a locked period).
/// <paramref name="Reason"/> is required and audited with the old and new values.</summary>
public sealed record CorrectAllowanceRequest(
    DateOnly? EffectiveFrom, string? Reason, string? GlDriverKey = null, bool? EosbIncluded = null,
    string? EosbExclusionCategory = null, string? EosbExclusionReason = null);

public sealed record SetOfferingRequest(Guid CompanyId, string ComponentCode, bool Offered, DateOnly EffectiveFrom);

/// <param name="Code">A <see cref="ReleaseABlockReasons"/> code for a legal refusal, otherwise a plain machine code.</param>
public sealed record MatrixCellError(Guid GradeId, string ComponentCode, string Code, string Message, BlockReasonDto? Reason);

/// <param name="AffectedNow">Employees whose package changes on the effective date (facilities, read when used).</param>
/// <param name="AffectedAtRenewal">Employees who get the change at their next contract year (wage standard and contract benefits;
/// a running contract year keeps the package it was fixed with).</param>
/// <param name="Superseded">Values that had not started yet and that nothing used, replaced on their own start date
/// (audited as <c>entitlements.matrix.superseded</c>).</param>
public sealed record PublishMatrixResult(
    bool DryRun, int Published, int Unchanged, int Reverted, int AffectedNow, int AffectedAtRenewal, int GapsRemaining,
    EntitlementMatrixDto? Matrix, int Superseded = 0);

/// <summary>One legacy row and what the import does with it.</summary>
/// <param name="Source">PayScale (grade_pay_scale_components) or Eligibility (benefit_eligibility_rules).</param>
/// <param name="Outcome">Import (becomes a matrix cell) or Skip (shown with its reason; nothing is written).</param>
public sealed record LegacyImportItem(
    string Source, Guid SourceId, Guid GradeId, string GradeCode, string GradeName, string? GradeNameAr,
    string SourceCode, string SourceName, string? Detail, string? ComponentCode, string Outcome, string? ReasonCode, string? Reason,
    MatrixCellInput? Cell);

public sealed record LegacyImportResult(bool Committed, DateOnly EffectiveFrom, int ToImport, int Skipped, int Imported, IReadOnlyList<LegacyImportItem> Items);

/// <summary>
/// One cash wage allowance (housing, transport, other) of a grade's standard on a date, as salary prefill reads it for a
/// release_a tenant — the matrix, never the frozen legacy pay scale. <c>Missing</c>: the grade has no value in force.
/// </summary>
public sealed record GradeCashAllowance(string ComponentCode, string Field, string? ValueType, decimal? Amount, decimal? Rate, bool Eligible, bool Missing)
{
    /// <summary>The monthly cash it comes to on <paramref name="basic"/>: the amount; the rate × basic (to the halala);
    /// 0 when it is provided in kind or not offered to the grade. NULL when the grade has no value, or a percentage has no
    /// basic salary to apply to — never guessed.</summary>
    public decimal? MonthlyCash(decimal basic)
    {
        if (Missing) return null;
        if (!Eligible || ValueType == GradeEntitlementValueTypes.InKind || ValueType == GradeEntitlementValueTypes.EligibilityOnly) return 0m;
        return ValueType switch
        {
            GradeEntitlementValueTypes.Amount => Amount,
            GradeEntitlementValueTypes.PercentOfBasic when Rate is decimal r && basic > 0 => Math.Round(basic * r, 2, MidpointRounding.AwayFromZero),
            _ => null,
        };
    }
}
