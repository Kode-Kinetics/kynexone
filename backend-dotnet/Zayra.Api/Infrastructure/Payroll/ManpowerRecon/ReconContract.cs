namespace Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

// The wire contract of the payroll manpower + value reconciliation. Property names are the contract
// (serialised camelCase by MVC's defaults) and are mirrored by the frontend fixture
// Zayra.Api.Tests/Recon/Fixtures/recon-full.json - ReconContractShapeTests keeps the two in step.
// Money is the run currency at two decimals; headcounts are whole numbers. Exit amounts are negative.

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
    bool PriorNotFinal,
    bool PriorVoided,
    string Currency,
    ReconScopeDto Scope,
    ReconThresholdsDto Thresholds,
    ReconVolumeDto Volume,
    ReconValueDto Value,
    ReconFinalSettlementDto FinalSettlement,
    ReconIntegrityDto Integrity,
    ReconSignoffDto? Signoff);

public sealed record ReconScopeDto(string Level, bool Partial, bool CanSignOff, IReadOnlyList<string> Reasons);

public sealed record ReconThresholdsDto(decimal Pct, decimal MinAmount, string Source);

/// <summary>A reconciliation total and whether it closes. <c>Difference</c> is 0 when it does.</summary>
public sealed record ReconTieDto(bool Ok, decimal Difference);

public sealed record ReconVolumeDto(
    int Opening,
    int Closing,
    IReadOnlyList<ReconVolumeLineDto> Lines,
    ReconTieDto Ties,
    ReconVolumeMemoDto Memo);

public sealed record ReconVolumeLineDto(string Category, string Kind, int Count, int? FsApproved, int? FsMissing);

public sealed record ReconVolumeMemoDto(int FinalWageMonth, ReconOffCycleMemoDto OffCycle);

public sealed record ReconOffCycleMemoDto(ReconOffCycleTotalsDto Prior, ReconOffCycleTotalsDto Current);

public sealed record ReconOffCycleTotalsDto(int Runs, decimal Gross);

public sealed record ReconValueDto(
    IReadOnlyList<ReconValueSectionDto> Sections,
    ReconHeadlineDto Headline,
    ReconTieDto Ties,
    ReconRegisterDifferenceDto RegisterDifference);

public sealed record ReconValueSectionDto(string Kind, IReadOnlyList<ReconItemDto> Items);

public sealed record ReconHeadlineDto(ReconItemDto Gross, ReconItemDto Deductions, ReconItemDto Net, ReconItemDto Employer);

public sealed record ReconItemDto(
    string Code,
    string Label,
    decimal Prior,
    ReconAddsDto Adds,
    ReconExitsDto Exits,
    ReconContinuingDto Continuing,
    decimal Current,
    decimal Delta,
    decimal DeltaPct,
    bool Flagged,
    string? Explanation);

public sealed record ReconAddsDto(decimal NewJoiner, decimal Rehire, decimal LateJoiner, decimal Reinstated, decimal TransferIn)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal Total => NewJoiner + Rehire + LateJoiner + Reinstated + TransferIn;
}

public sealed record ReconExitsDto(decimal Leaver, decimal TransferOut, decimal NotPaid, decimal Unexplained)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal Total => Leaver + TransferOut + NotPaid + Unexplained;
}

public sealed record ReconContinuingDto(
    decimal Package, decimal Proration, decimal Variable, decimal Arrears,
    decimal Attendance, decimal Loans, decimal Statutory, decimal Other)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal Total => Package + Proration + Variable + Arrears + Attendance + Loans + Statutory + Other;
}

/// <summary>Line totals minus the slip register, per column. Non-zero only inside the documented rounding tolerance.</summary>
public sealed record ReconRegisterDifferenceDto(
    decimal PriorGross, decimal PriorDeductions, decimal CurrentGross, decimal CurrentDeductions);

public sealed record ReconFinalSettlementDto(
    IReadOnlyList<ReconFsCellDto> Cells,
    IReadOnlyList<ReconFsComponentDto> PerComponent,
    IReadOnlyList<ReconLeaverWithoutFsDto> LeaversWithoutFs,
    ReconTieDto Ties);

public sealed record ReconFsCellDto(string Cell, int Count, decimal Gross);

public sealed record ReconFsComponentDto(string Code, decimal Prior, decimal Current, decimal Delta);

public sealed record ReconLeaverWithoutFsDto(int EmployeeId, string EmployeeCode, string EmployeeName);

public sealed record ReconIntegrityDto(bool Ok, IReadOnlyList<ReconIssueDto> Issues);

public sealed record ReconIssueDto(string Code, string Message, int? EmployeeId);

public sealed record ReconSignoffDto(
    Guid Id,
    string Status,
    string SignedOffByName,
    DateTime SignedOffAtUtc,
    string SnapshotHash,
    string? SupersededReason);

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

/// <summary>
/// GET recon/signoff. Carries what a sign-off request must restate (process version, snapshot hash, the count of
/// flagged employees to acknowledge) next to the Active sign-off, so the bridge DTO itself stays exactly the
/// agreed contract.
/// </summary>
public sealed record ReconSignoffStateDto(
    int ProcessVersion,
    string SnapshotHash,
    int FlaggedEmployeeCount,
    bool SignoffRequired,
    ReconSignoffDto? Signoff,
    IReadOnlyList<ReconRequiredExplanationDto> RequiredExplanations);

/// <summary>An explanation the sign-off will refuse to proceed without.</summary>
public sealed record ReconRequiredExplanationDto(string Scope, string Key, string Label);

public sealed record ReconEmployeePageDto(int Total, int Page, int PageSize, IReadOnlyList<ReconEmployeeRowDto> Items);

public sealed record ReconEmployeeRowDto(
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Category,
    decimal Prior,
    decimal Current,
    decimal Delta,
    string? Bucket,
    bool FinalWageMonth,
    bool Prorated,
    bool Flagged);
