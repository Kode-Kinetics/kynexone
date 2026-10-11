namespace Zayra.Api.Infrastructure.Compensation;

// ── requests ─────────────────────────────────────────────────────────────────

public sealed record CompEligibilityInput(
    DateOnly? HireDateCutoff,
    IReadOnlyList<string>? EligibleStatuses,
    IReadOnlyList<Guid>? IncludeGradeIds,
    IReadOnlyList<Guid>? ExcludeGradeIds,
    IReadOnlyList<Guid>? IncludeDepartmentIds,
    IReadOnlyList<Guid>? ExcludeDepartmentIds,
    int MinimumEligibleDays,
    string ProrationRule);

public sealed record CreateCompCycleRequest(
    Guid CompanyId, string Name, DateOnly PerformancePeriodStart, DateOnly PerformancePeriodEnd, DateOnly EffectiveDate,
    CompEligibilityInput Eligibility, bool EquityIncludesNationality = false);

public sealed record UpdateCompCycleRequest(
    int Version, string Name, DateOnly PerformancePeriodStart, DateOnly PerformancePeriodEnd, DateOnly EffectiveDate,
    CompEligibilityInput Eligibility, bool EquityIncludesNationality = false);

public sealed record CompBudgetInput(Guid DepartmentId, string PoolType, decimal? PoolPercent, decimal? FixedAmount);
public sealed record SaveCompBudgetsRequest(IReadOnlyList<CompBudgetInput> Budgets);

public sealed record CompGuidelineInput(string Rating, decimal CompaRatioMin, decimal? CompaRatioMax, decimal MinPercent, decimal TargetPercent, decimal MaxPercent);
public sealed record SaveCompGuidelinesRequest(IReadOnlyList<CompGuidelineInput> Guidelines);

public sealed record CompFxInput(string FromCurrency, decimal Rate, string Source, DateOnly RateDate);
public sealed record SaveCompFxRequest(IReadOnlyList<CompFxInput> Rates);

public sealed record CompCycleTransitionRequest(int Version, string? Reason = null, bool AcknowledgeRetroCost = false);

public sealed record CreateCompScenarioRequest(int? ManagerEmployeeId, string ScenarioName, Guid? CopyFromWorksheetId);

public sealed record CompLineInput(int EmployeeId, decimal? ProposedIncrease, decimal? ProposedPercent, string? Justification);
public sealed record SaveCompWorksheetRequest(int Version, string? OverspendJustification, IReadOnlyList<CompLineInput> Lines);
public sealed record SubmitCompWorksheetRequest(int Version);

public sealed record WithdrawCompAwardRequest(string Reason);
public sealed record ApplyCompCycleRequest(bool AcknowledgeRetroCost = false);
public sealed record ReverseCompAwardRequest(DateOnly EffectiveDate, string Reason, bool AcknowledgeLockedPeriods = false);

public sealed record MapBenchmarkRowRequest(Guid? DesignationId, Guid? GradeId);

public sealed record OpenCompWorksheetRequest(int ManagerEmployeeId);
public sealed record AssignCompEmployeeRequest(int EmployeeId);
public sealed record CloseCompWorksheetRequest(string Reason);

// ── responses ────────────────────────────────────────────────────────────────

public sealed record CompCycleSummaryDto(
    Guid Id, Guid? CompanyId, string CompanyName, string Name, string Status, DateOnly EffectiveDate, string Currency,
    DateOnly PerformancePeriodStart, DateOnly PerformancePeriodEnd, int WorksheetCount, int ApprovedWorksheetCount, int Version);

public sealed record CompBudgetDto(
    Guid DepartmentId, string DepartmentName, bool HasBudget, string? PoolType, decimal? PoolPercent, decimal? FixedAmount,
    int EligibleCount, decimal EligibleBasicCycle, decimal Pool, decimal Proposed, decimal Remaining, decimal? PercentUsed, bool Overspent);

public sealed record CompGuidelineDto(Guid Id, string Rating, decimal CompaRatioMin, decimal? CompaRatioMax, decimal MinPercent, decimal TargetPercent, decimal MaxPercent);

public sealed record CompFxDto(Guid Id, string FromCurrency, decimal Rate, string Source, DateOnly RateDate, string EnteredByName, DateTime EnteredAtUtc);

public sealed record CompWorksheetSummaryDto(
    Guid Id, int ManagerEmployeeId, string ManagerName, string ScenarioName, bool IsPrimary, string Status, int Revision,
    int LineCount, decimal ProposedTotal, bool Overspend, Guid? ApprovalRequestId, string? DecisionComments, int Version, Guid CycleId);

public sealed record CompExcludedDto(int EmployeeId, string EmployeeCode, string Name, string Reason, int? OwnerEmployeeId = null, string? OwnerName = null, bool OwnerCanBeOpened = false);

public sealed record CompBlockerDto(string Code, string Message);

public sealed record CompTotalsDto(int EligibleCount, decimal EligibleBasicCycle, decimal Pool, decimal Proposed, decimal Remaining, decimal? PercentUsed, int AwardCount);

public sealed record CompCycleDto(
    Guid Id, Guid? CompanyId, string CompanyName, string Name, string Status, DateOnly PerformancePeriodStart, DateOnly PerformancePeriodEnd,
    DateOnly EffectiveDate, string Currency, string PayComponent, DateOnly? HireDateCutoff, IReadOnlyList<string> EligibleStatuses,
    IReadOnlyList<Guid> IncludeGradeIds, IReadOnlyList<Guid> ExcludeGradeIds, IReadOnlyList<Guid> IncludeDepartmentIds, IReadOnlyList<Guid> ExcludeDepartmentIds,
    int MinimumEligibleDays, string ProrationRule, bool EquityIncludesNationality, int Version,
    IReadOnlyList<CompBudgetDto> Budgets, IReadOnlyList<CompGuidelineDto> Guidelines, IReadOnlyList<CompFxDto> FxRates,
    IReadOnlyList<string> PayCurrencies, IReadOnlyList<CompWorksheetSummaryDto> Worksheets, IReadOnlyList<CompExcludedDto> Uncovered,
    IReadOnlyList<CompExcludedDto> Ineligible, IReadOnlyList<CompBlockerDto> Blockers, CompTotalsDto Totals,
    IReadOnlyList<string> AllowedActions, CompApplySummaryDto? Apply, DateTime? ApprovedAtUtc, DateTime? AppliedAtUtc, string? CancelReason,
    CompRetroDto? Retro = null);

/// <summary>A past effective date: the payroll months already paid at the old rate, the monthly increase, and the arrears
/// they imply (months × monthly delta, cycle currency) — shown to the approver, who must acknowledge it.</summary>
public sealed record CompRetroDto(int MonthsInPast, IReadOnlyList<string> PaidPeriods, decimal MonthlyDeltaCycle, decimal RetroCostCycle, bool AcknowledgementRequired);

public sealed record CompApplySummaryDto(int Awards, int Scheduled, int Applied, int ReturnedForReview, int Cancelled, int Blocked, IReadOnlyList<CompApplyLineDto> Lines);
public sealed record CompApplyLineDto(Guid LineId, int EmployeeId, string EmployeeName, Guid? ChangeRequestId, string? ChangeStatus, string? BlockedReason, decimal NewBasic, string PayCurrency, Guid? ReversalChangeRequestId, string? ReversalStatus);

public sealed record CompBenchmarkPositionDto(string State, Guid? SetId, string? SetName, bool IsSynthetic, DateOnly? SurveyDate, string? PayBasis, decimal? P50Cycle, decimal? Position, string? Detail);

public sealed record CompLineDto(
    Guid Id, int EmployeeId, string EmployeeCode, string EmployeeName, string DepartmentName, string DesignationName, string GradeName,
    string PayCurrency, decimal? FxRate, decimal CurrentBasic, decimal CurrentGross, decimal? CurrentBasicCycle, decimal? CurrentGrossCycle,
    decimal? GradeMin, decimal? GradeMid, decimal? GradeMax, string? GradeCurrency, decimal? CompaRatio, decimal? RangePosition, string Rating,
    int EligibleDays, decimal ProrationFactor, decimal? GuidelineMinPercent, decimal? GuidelineTargetPercent, decimal? GuidelineMaxPercent,
    decimal ProposedIncrease, decimal ProposedPercent, decimal ProratedIncrease, decimal ProratedIncreasePay, decimal NewBasic, decimal NewGross,
    decimal? NewCompaRatio, bool OutOfGuideline, string? Justification, IReadOnlyList<string> Flags, IReadOnlyList<string> BlockingMessages,
    bool ProposedAsPercent, bool AssignedByHr,
    CompBenchmarkPositionDto Benchmark, Guid? ChangeRequestId, string? ChangeStatus, string? ApplyBlockedReason);

public sealed record CompWorksheetDto(
    Guid Id, Guid CycleId, string CycleName, string CycleStatus, string Currency, DateOnly EffectiveDate, int ManagerEmployeeId, string ManagerName,
    string ScenarioName, bool IsPrimary, string Status, int Revision, int Version, string? OverspendJustification, Guid? ApprovalRequestId,
    string? DecisionComments, DateTime? SubmittedAtUtc, IReadOnlyList<CompLineDto> Lines, IReadOnlyList<CompExcludedDto> Excluded,
    IReadOnlyList<CompBudgetDto> BudgetImpact, decimal ProposedTotal, bool Overspend, bool CanEdit, bool CanSubmit,
    IReadOnlyList<CompBlockerDto> Blockers, IReadOnlyList<CompScenarioRefDto> Scenarios);

public sealed record CompScenarioRefDto(Guid Id, string ScenarioName, bool IsPrimary, string Status, decimal ProposedTotal);

public sealed record CompMyWorksheetsDto(IReadOnlyList<CompCycleSummaryDto> Cycles, IReadOnlyList<CompWorksheetSummaryDto> Worksheets);

// ── equity ──
public sealed record CompEquityGroupDto(string Group, bool Suppressed, int? Count, decimal? MeanCompaRatio, decimal? MedianCompaRatio, decimal? MeanGrossCycle, decimal? ProjectedMeanCompaRatio);
public sealed record CompEquityCohortDto(string GradeName, string DesignationName, bool Suppressed, string? CountLabel, IReadOnlyList<CompEquityGroupDto> Groups, decimal? GapPercent, decimal? ProjectedGapPercent, string? Dimension);
public sealed record CompEquityDto(Guid CycleId, string Currency, int MinimumCohort, IReadOnlyList<CompEquityCohortDto> Cohorts, IReadOnlyList<CompEquityCohortDto> NationalityCohorts, int ExcludedNoGrade, int ExcludedNoSalary, string Notice);

// ── benchmarks ──
public sealed record PayBenchmarkSetDto(
    Guid Id, Guid? CompanyId, string Name, string Provider, string LicenceStatement, DateOnly SurveyDate, string Geography, string Currency,
    string PayBasis, bool IsSynthetic, bool IsStale, string SourceFileName, string SourceFileSha256, int RowCount, bool IsActive,
    string ImportedByName, DateTime ImportedAtUtc, int MappedRows);
public sealed record PayBenchmarkRowDto(Guid Id, string JobFamily, string Level, int SampleSize, decimal P25, decimal P50, decimal P75, decimal P90, Guid? DesignationId, Guid? GradeId);
public sealed record PayBenchmarkImportIssueDto(int Row, string Field, string Message);
public sealed record PayBenchmarkImportResultDto(bool Imported, PayBenchmarkSetDto? Set, IReadOnlyList<PayBenchmarkImportIssueDto> Issues, int CarriedMappings);

// ── exceptions (mapped to HTTP by the controller) ──

/// <summary>A business rule refused the request (422) — the message is shown to the user verbatim.</summary>
public sealed class CompRuleException(string code, string message, IReadOnlyList<CompBlockerDto>? blockers = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<CompBlockerDto> Blockers { get; } = blockers ?? [];
}

/// <summary>Someone else changed the record first (409).</summary>
public sealed class CompConflictException(string message) : Exception(message);

/// <summary>The request itself is malformed (400) — e.g. a percent with more precision than is stored.</summary>
public sealed class CompBadRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Not found or outside the caller's scope (404) — the two are not told apart.</summary>
public sealed class CompNotFoundException(string message) : Exception(message);

/// <summary>The caller may not do this to this record (403).</summary>
public sealed class CompForbiddenException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
