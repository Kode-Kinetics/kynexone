using System.Text.Json;
using System.Text.Json.Serialization;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Contracts;

namespace Zayra.Api.Infrastructure.Entitlements;

// Release A slice R1 — the shapes the benefits-by-grade matrix API reads and writes. Owned by R1.

/// <summary>Who is acting, as the matrix service needs it. Built by the controller from the request.</summary>
public sealed record MatrixActor(Guid TenantId, Guid? UserId, EntityScopeContext Scope, string? IpAddress);

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
    bool IsFloor, bool CanBeSkipped, bool IsLoanFacility, BlockReasonDto? FloorReason);

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
    IReadOnlyList<MatrixOfferingDto> Offerings, IReadOnlyList<MatrixGapDto> Gaps);

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

public sealed record SetOfferingRequest(Guid CompanyId, string ComponentCode, bool Offered, DateOnly EffectiveFrom);

/// <param name="Code">A <see cref="ReleaseABlockReasons"/> code for a legal refusal, otherwise a plain machine code.</param>
public sealed record MatrixCellError(Guid GradeId, string ComponentCode, string Code, string Message, BlockReasonDto? Reason);

/// <param name="AffectedNow">Employees whose package changes on the effective date (facilities, read when used).</param>
/// <param name="AffectedAtRenewal">Employees who get the change at their next contract year (wage standard and contract benefits;
/// a running contract year keeps the package it was fixed with).</param>
public sealed record PublishMatrixResult(
    bool DryRun, int Published, int Unchanged, int Reverted, int AffectedNow, int AffectedAtRenewal, int GapsRemaining,
    EntitlementMatrixDto? Matrix);

/// <summary>One legacy row and what the import does with it.</summary>
/// <param name="Source">PayScale (grade_pay_scale_components) or Eligibility (benefit_eligibility_rules).</param>
/// <param name="Outcome">Import (becomes a matrix cell) or Skip (shown with its reason; nothing is written).</param>
public sealed record LegacyImportItem(
    string Source, Guid SourceId, Guid GradeId, string GradeCode, string GradeName, string? GradeNameAr,
    string SourceCode, string SourceName, string? Detail, string? ComponentCode, string Outcome, string? ReasonCode, string? Reason,
    MatrixCellInput? Cell);

public sealed record LegacyImportResult(bool Committed, DateOnly EffectiveFrom, int ToImport, int Skipped, int Imported, IReadOnlyList<LegacyImportItem> Items);
