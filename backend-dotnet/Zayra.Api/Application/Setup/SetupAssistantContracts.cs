namespace Zayra.Api.Application.Setup;

// ── Input ───────────────────────────────────────────────────────────────────

/// <summary>
/// On whose behalf a draft is generated.
///
/// The assistant calls a model, and every model call has to produce a tenant-scoped usage/cost
/// record, so it cannot run without knowing who asked. This is deliberately a separate parameter
/// rather than fields on <see cref="CompanyProfile"/>: the profile is the request body and the
/// client controls it, while these three values come from the caller's verified claims.
/// </summary>
public sealed record SetupRequester(Guid TenantId, Guid? UserId, string UserRole);

public sealed record SetupSections(bool Org, bool Leave, bool Shifts, bool Payroll, bool Entity, bool Governance);

public sealed record CompanyProfile(
    string CountryCode,   // 2- or 3-letter (e.g. "SA"/"SAU"); normalised by the service
    string Industry,
    string CompanySize,   // free text bucket, e.g. "1-50", "51-200", "200+"
    string CurrencyCode,
    string? Notes,
    string? LegalEntityName,
    string? BranchCity,
    string? OperatingModel,
    string? PayrollModel,
    string? ApprovalModel,
    bool StrictEntityScope,
    bool RequireCostCenterForPayroll,
    bool RequireGradeForApprovalPolicy,
    SetupSections Sections);

// ── Draft items (mirror the real entities but only the safe, configurable fields) ──

public sealed record DraftDepartment(string Code, string NameEn);
public sealed record DraftBranch(string Code, string NameEn, string City, bool IsHeadOffice);
public sealed record DraftCostCenter(string Code, string Name, string DepartmentCode);
public sealed record DraftDesignation(string Code, string TitleEn, string DepartmentCode, string GradeCode, string JobLevel, bool IsManagerRole, int LevelRank);
public sealed record DraftGrade(string Code, string Name, string Band, int Level, decimal MinSalary, decimal MidSalary, decimal MaxSalary, string Currency);
public sealed record DraftGradePayComponent(string GradeCode, string ComponentCode, string ComponentName, string ComponentType, string CalculationType, decimal Amount, decimal Percentage, bool IsTaxable, string Frequency);
public sealed record DraftLeaveType(string Code, string NameEn, string Category, bool IsPaid, int MaxConsecutiveDays, bool RequiresAttachment, string ColorCode);
public sealed record DraftShift(string Code, string Name, string Start, string End, int BreakMinutes, string Color);
public sealed record DraftWorkingWeek(string WorkWeek, string WeekStartDay);
public sealed record DraftPayComponent(string Code, string Name, string ComponentType, string CalculationType, decimal Amount, decimal Percentage, bool IsTaxable);
public sealed record DraftStatutoryRule(string RuleKey, string RuleValue, string DataType, string Description);
public sealed record DraftEmployeeIdRule(string CompanyPrefix, bool UseCountryPrefix, bool UseBranchPrefix, bool UseDepartmentPrefix, bool UseYear, int PaddingLength, int NextSequence, bool AllowManualOverride);
public sealed record DraftHrConfig(bool UseDeptHeadApproval, bool UseHrFinalApproval, bool UseSupervisorBeforeManager, bool AllowDottedLineApproval, bool AutoCreateDeptOnImport, bool AutoCreateDesignationOnImport, bool RequireImportPreviewBeforeCommit, bool AllowCrossDeptManager, bool AllowCrossLocationManager, bool RequireCostCenterForPayroll, bool RequireGradeForApprovalPolicy);

public sealed record SetupDraft(
    List<DraftBranch> Branches,
    List<DraftDepartment> Departments,
    List<DraftCostCenter> CostCenters,
    List<DraftDesignation> Designations,
    List<DraftGrade> Grades,
    List<DraftGradePayComponent> GradePayComponents,
    List<DraftLeaveType> LeaveTypes,
    List<DraftShift> Shifts,
    DraftWorkingWeek? WorkingWeek,
    List<DraftPayComponent> PayComponents,
    List<DraftStatutoryRule> StatutoryRules,
    DraftEmployeeIdRule? EmployeeIdRule,
    DraftHrConfig? HrConfig)
{
    public static SetupDraft Empty() => new(new(), new(), new(), new(), new(), new(), new(), new(), null, new(), new(), null, null);
}

public sealed record SetupPreviewResult(SetupDraft Draft, List<string> Notes, string Engine);

public interface ISetupAssistantService
{
    /// <summary>Generates a proposed (un-persisted) starter configuration for a company profile.
    /// Uses the configured LLM for the descriptive parts and deterministic templates for the
    /// risk-sensitive statutory parts. Never throws on LLM failure.
    ///
    /// <para><paramref name="requester"/> is not optional. Every attempt is recorded against that
    /// tenant and user — including the attempts that never reach a provider, which are precisely
    /// the ones that went unnoticed while this feature silently served a built-in template.</para></summary>
    Task<SetupPreviewResult> GenerateAsync(SetupRequester requester, CompanyProfile profile, CancellationToken ct);
}
