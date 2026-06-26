namespace Zayra.Api.Application.Setup;

// ── Input ───────────────────────────────────────────────────────────────────

public sealed record SetupSections(bool Org, bool Leave, bool Shifts, bool Payroll);

public sealed record CompanyProfile(
    string CountryCode,   // 2- or 3-letter (e.g. "SA"/"SAU"); normalised by the service
    string Industry,
    string CompanySize,   // free text bucket, e.g. "1-50", "51-200", "200+"
    string CurrencyCode,
    string? Notes,
    SetupSections Sections);

// ── Draft items (mirror the real entities but only the safe, configurable fields) ──

public sealed record DraftDepartment(string Code, string NameEn);
public sealed record DraftDesignation(string Code, string TitleEn, string DepartmentCode, string JobLevel, bool IsManagerRole, int LevelRank);
public sealed record DraftGrade(string Code, string Name, string Band, int Level);
public sealed record DraftLeaveType(string Code, string NameEn, string Category, bool IsPaid, int MaxConsecutiveDays, bool RequiresAttachment, string ColorCode);
public sealed record DraftShift(string Code, string Name, string Start, string End, int BreakMinutes, string Color);
public sealed record DraftWorkingWeek(string WorkWeek, string WeekStartDay);
public sealed record DraftPayComponent(string Code, string Name, string ComponentType, string CalculationType, decimal Amount, decimal Percentage, bool IsTaxable);
public sealed record DraftStatutoryRule(string RuleKey, string RuleValue, string DataType, string Description);

public sealed record SetupDraft(
    List<DraftDepartment> Departments,
    List<DraftDesignation> Designations,
    List<DraftGrade> Grades,
    List<DraftLeaveType> LeaveTypes,
    List<DraftShift> Shifts,
    DraftWorkingWeek? WorkingWeek,
    List<DraftPayComponent> PayComponents,
    List<DraftStatutoryRule> StatutoryRules)
{
    public static SetupDraft Empty() => new(new(), new(), new(), new(), new(), null, new(), new());
}

public sealed record SetupPreviewResult(SetupDraft Draft, List<string> Notes, string Engine);

public interface ISetupAssistantService
{
    /// <summary>Generates a proposed (un-persisted) starter configuration for a company profile.
    /// Uses the configured LLM for the descriptive parts and deterministic templates for the
    /// risk-sensitive statutory parts. Never throws on LLM failure.</summary>
    Task<SetupPreviewResult> GenerateAsync(CompanyProfile profile, CancellationToken ct);
}
