using System.Text.Json.Serialization;

namespace Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

// The wire contract of the payroll manpower + value reconciliation. Property names are the contract (serialised camelCase by
// MVC's defaults) and are mirrored by the fixtures under Zayra.Api.Tests/Recon/Fixtures, which the frontend builds against;
// ReconContractShapeTests keeps the two in step. Money is the run currency; headcounts are whole numbers. Exit amounts are
// negative. Optional properties marked WhenWritingNull are absent from the JSON, not null, when they have no value.

public static class ReconModes
{
    public const string Full = "Full";
    public const string NoPrior = "NoPrior";
    public const string NotApplicable = "NotApplicable";
}

public static class ReconKinds
{
    public const string Add = "Add";
    public const string Exit = "Exit";
}

public static class ReconSectionKinds
{
    public const string Earning = "Earning";
    public const string Deduction = "Deduction";
    public const string Employer = "Employer";
}

public sealed record ReconDto(
    Guid RunId,
    string Period,
    string Mode,
    Guid? PriorRunId,
    string? PriorPeriod,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastRunPeriod,
    bool PriorNotFinal,
    bool PriorVoided,
    string Currency,
    int ProcessVersion,
    string SnapshotHash,
    bool SignoffRequired,
    ReconScopeDto Scope,
    ReconThresholds Thresholds,
    ReconVolumeDto Volume,
    ReconValueDto Value,
    ReconFinalSettlementDto FinalSettlement,
    ReconIntegrityDto Integrity,
    ReconSignoffDto? Signoff);

public sealed record ReconScopeDto(string Level, bool Partial, bool CanSignOff, IReadOnlyList<string> Reasons);

/// <summary>A reconciliation total and how far it is from closing. Internal bookkeeping; the wire carries only whether it closes.</summary>
public readonly record struct ReconTie(bool Ok, decimal Difference);

public sealed record ReconVolumeDto(
    int Opening,
    int Closing,
    IReadOnlyList<ReconVolumeLineDto> Lines,
    bool Ties,
    ReconVolumeMemoDto Memo);

public sealed record ReconVolumeLineDto(
    string Category,
    string Kind,
    int Count,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FsApproved,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FsMissing);

public sealed record ReconVolumeMemoDto(int FinalWageMonth, ReconOffCycleMemoDto OffCycle);

public sealed record ReconOffCycleMemoDto(ReconOffCycleTotalsDto Prior, ReconOffCycleTotalsDto Current);

public sealed record ReconOffCycleTotalsDto(int Count, decimal Gross);

public sealed record ReconValueDto(
    IReadOnlyList<ReconValueSectionDto> Sections,
    ReconHeadlineDto Headline,
    bool Ties,
    decimal RegisterDifference);

public sealed record ReconValueSectionDto(string Kind, IReadOnlyList<ReconItemDto> Items);

public sealed record ReconHeadlineDto(ReconBridgeRowDto Gross, ReconBridgeRowDto Deductions, ReconBridgeRowDto Net, ReconBridgeRowDto Employer);

/// <summary>One row of the bridge: <c>prior + adds + exits + continuing = current</c>. <c>DeltaPct</c> is null when the prior is zero.</summary>
public record ReconBridgeRowDto(
    decimal Prior,
    ReconAddsDto Adds,
    ReconExitsDto Exits,
    ReconContinuingDto Continuing,
    decimal Current,
    decimal Delta,
    decimal? DeltaPct);

public sealed record ReconItemDto(
    string Code,
    string Label,
    decimal Prior,
    ReconAddsDto Adds,
    ReconExitsDto Exits,
    ReconContinuingDto Continuing,
    decimal Current,
    decimal Delta,
    decimal? DeltaPct,
    bool Flagged,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Explanation)
    : ReconBridgeRowDto(Prior, Adds, Exits, Continuing, Current, Delta, DeltaPct)
{
    /// <summary>The key a written explanation for an item is filed under: <c>Kind:CODE</c> (a deduction and an employer cost can share a code).</summary>
    public static string ExplanationKey(string kind, string code) => $"{kind}:{code}";
}

public sealed record ReconAddsDto(decimal NewJoiner, decimal Rehire, decimal LateJoiner, decimal Reinstated, decimal TransferIn)
{
    [JsonIgnore]
    public decimal Total => NewJoiner + Rehire + LateJoiner + Reinstated + TransferIn;
}

public sealed record ReconExitsDto(decimal Leaver, decimal TransferOut, decimal NotPaid, decimal Unexplained)
{
    [JsonIgnore]
    public decimal Total => Leaver + TransferOut + NotPaid + Unexplained;
}

public sealed record ReconContinuingDto(
    decimal Package, decimal Proration, decimal Variable, decimal Arrears,
    decimal Attendance, decimal Loans, decimal Statutory, decimal Other)
{
    [JsonIgnore]
    public decimal Total => Package + Proration + Variable + Arrears + Attendance + Loans + Statutory + Other;
}

public sealed record ReconFinalSettlementDto(
    IReadOnlyList<ReconFsCellDto> Cells,
    IReadOnlyList<ReconFsComponentDto> PerComponent,
    IReadOnlyList<ReconLeaverWithoutFsDto> LeaversWithoutFs,
    bool Ties);

public sealed record ReconFsCellDto(string Code, int Count);

public sealed record ReconFsComponentDto(string Code, decimal Prior, decimal Current, decimal Delta);

public sealed record ReconLeaverWithoutFsDto(int EmployeeId, string EmployeeCode, string EmployeeName, string? LastWorkingDay);

public sealed record ReconIntegrityDto(bool Ok, IReadOnlyList<ReconIssueDto> Issues);

public sealed record ReconIssueDto(string Code, string Message);

public sealed record ReconSignoffDto(
    string Id,
    string Status,
    string SignedOffByName,
    DateTime SignedOffAtUtc,
    string SnapshotHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SupersededReason);

public static class ReconSignoffStatuses
{
    public const string Active = "Active";
    public const string Superseded = "Superseded";
    public static readonly string[] All = { Active, Superseded };
}

public static class ReconSupersededReasons
{
    public const string Reprocessed = "Reprocessed";
    public const string Reopened = "Reopened";
    public const string SentBack = "SentBack";
    public const string Voided = "Voided";
    public const string Withdrawn = "Withdrawn";
    public const string PriorChanged = "PriorChanged";
    public const string InputsChanged = "InputsChanged";
}

/// <summary>The scope reasons the screen words. Only these strings are produced.</summary>
public static class ReconScopeReasons
{
    public const string RequiresOrganizationScope = "signoff_requires_organization_scope";
}

// ── Requests / smaller responses ─────────────────────────────────────────────────────────────

public static class ReconExplanationScopes
{
    public const string Item = "Item";
    public const string Employee = "Employee";
}

public sealed record ReconExplanationRequest(string Scope, string Key, string Text);

public sealed record ReconSignoffRequest(
    int ExpectedProcessVersion,
    string ExpectedSnapshotHash,
    Guid? PriorRunId,
    int AcknowledgedFlaggedEmployeeCount,
    IReadOnlyList<ReconExplanationRequest>? Explanations,
    string? Notes);

public sealed record ReconWithdrawRequest(string? Reason);

/// <summary>An explanation the sign-off will refuse to proceed without. <c>Key</c> is <c>Kind:CODE</c> for an item, the employee id for an employee.</summary>
public sealed record ReconRequiredExplanationDto(string Scope, string Key, string Label);

public sealed record ReconEmployeePageDto(
    IReadOnlyList<ReconEmployeeRowDto> Items, int Total, int Page, int PageSize);

public sealed record ReconEmployeeRowDto(
    int EmployeeId,
    string? EmployeeCode,
    string? EmployeeName,
    string? Category,
    string? Note,
    decimal? Prior,
    decimal? Current,
    decimal? Delta);
