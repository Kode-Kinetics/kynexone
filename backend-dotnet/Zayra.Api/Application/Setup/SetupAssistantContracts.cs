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

/// <summary>
/// Which areas the draft covers. The four trailing flags were added after the first six and carry
/// defaults so every existing caller — including the tests — keeps compiling; the form sends all
/// ten explicitly.
/// </summary>
public sealed record SetupSections(
    bool Org, bool Leave, bool Shifts, bool Payroll, bool Entity, bool Governance,
    bool LeavePolicies = true, bool Holidays = true, bool Attendance = true, bool Localization = true);

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
    SetupSections Sections,

    // ── Operating choices ────────────────────────────────────────────────────
    // Each one lands in a column the product already has and the setup previously left at its
    // construction-time default: a Saudi tenant was written America/New_York + MM/DD/YYYY, and
    // its leave types were created granting nobody any days at all.
    //
    // Optional with null/0 defaults so callers built before they existed still compile; the
    // service reads every one of them through a Resolve* helper that supplies the country- or
    // industry-derived answer when the caller left it unset.

    /// <summary>SingleDayShift | TwoShifts | ContinuousThreeShifts | FieldRoster. Decides the shift
    /// set and the attendance day length. Previously guessed from a regex over the free-text note.</summary>
    string? WorkPattern = null,

    /// <summary>CountryDefault | Fri-Sat | Sat-Sun | Fri | Sun — the REST days, not the working ones.</summary>
    string? WeekendPattern = null,

    /// <summary>Calendar | JoiningDate | Fiscal — recorded as the leave.year_basis rule.</summary>
    string? LeaveYearBasis = null,

    /// <summary>0 = unset; the service then uses the country default.</summary>
    int ProbationMonths = 0,

    /// <summary>0 = unset; the service then uses the country default.</summary>
    int NoticePeriodDays = 0,

    /// <summary>MostlyNational | Mixed | MostlyExpat — decides whether expatriate pay components
    /// (air ticket accrual) belong in the structure.</summary>
    string? WorkforceMix = null,

    /// <summary>PaidOvertime | CompensatoryOff | NotApplicable. NotApplicable produces no overtime
    /// policy rather than a disabled one.</summary>
    string? OvertimeHandling = null,

    /// <summary>BiometricDevice | MobileGeofence | WebCheckIn | Manual — sets the grace and
    /// rounding an honest policy can claim for that capture method.</summary>
    string? AttendanceCapture = null,

    /// <summary>Monthly | SemiMonthly | Biweekly | Weekly — the pay component frequency.</summary>
    string? PayCycle = null,

    /// <summary>IANA zone. Empty = derive from the country.</summary>
    string? TimeZone = null,

    /// <summary>en | ar | bilingual. Empty = derive from the country.</summary>
    string? DefaultLanguage = null);

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
/// <summary>Entitlement for one leave type. Without this the applied draft creates leave types that
/// grant nobody any days — <see cref="Zayra.Api.Models.LeavePolicy"/>, not the type, carries the
/// entitlement, accrual and carry-forward that the leave engine reads.</summary>
/// <remarks>Carry-forward is deliberately absent. <c>LeavePolicy</c> has the two columns, but
/// <c>LeavePoliciesController</c> refuses any non-zero cap or expiry — there is no year-end rollover
/// in this build, so a stored cap would read back correctly and govern nothing. Drafting a field the
/// rest of the product rejects would be offering a setting that does not exist.</remarks>
public sealed record DraftLeavePolicy(
    string Name, string LeaveTypeCode, decimal AnnualEntitlementDays, string AccrualMethod,
    bool EncashmentAllowed, decimal EncashmentMaxDays,
    decimal MinimumDaysPerRequest, decimal MaximumDaysPerRequest, int NoticeRequiredDays,
    bool WeekendsIncluded, bool PublicHolidaysIncluded, bool AppliesOnProbation, string PayrollImpact);

/// <summary><paramref name="Date"/> is yyyy-MM-dd. Only holidays with a fixed Gregorian date are
/// ever drafted; moon-sighting holidays are named in a note instead of being given an invented date.</summary>
public sealed record DraftHoliday(string NameEn, string NameAr, string Date, bool IsRecurring, bool IsOptional, string HolidayType, string Notes);
public sealed record DraftHolidayCalendar(string Name, int CalendarYear, List<DraftHoliday> Holidays);

public sealed record DraftAttendancePolicy(
    string Code, string Name, int GraceMinutes, int LateThresholdMinutes, int EarlyExitThresholdMinutes,
    int HalfDayThresholdMinutes, int AbsentThresholdMinutes, int StandardWorkMinutes, int BreakMinutes,
    string RoundingRule, bool RequiresOvertimeApproval, bool AllowAbsenceToLeaveConversion);

public sealed record DraftOvertimeMultiplier(string DayCategory, decimal Multiplier);
public sealed record DraftOvertimePolicy(
    string Code, string Name, string HourlyRateBasis, int StandardMonthlyHours, int MinimumMinutes,
    int MaximumMinutesPerDay, int MonthlyCapMinutes, string RoundingRule, bool RequiresApproval,
    bool AllowCompOffConversion, List<DraftOvertimeMultiplier> Multipliers);

public sealed record DraftLocalization(
    string DefaultLanguage, bool RtlEnabled, string CalendarSystem, string DefaultTimezone,
    string DateFormat, bool HijriDatesEnabled);

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
    DraftHrConfig? HrConfig,
    List<DraftLeavePolicy> LeavePolicies,
    DraftHolidayCalendar? HolidayCalendar,
    DraftAttendancePolicy? AttendancePolicy,
    DraftOvertimePolicy? OvertimePolicy,
    DraftLocalization? Localization)
{
    public static SetupDraft Empty() =>
        new(new(), new(), new(), new(), new(), new(), new(), new(), null, new(), new(), null, null,
            new(), null, null, null, null);
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

/// <summary>
/// The platform's own statutory rules for one country, as ruleKey → ruleValue.
///
/// The assistant needs a handful of legal figures — the annual-leave floor, the sick-leave bands,
/// the overtime multipliers — to fill a leave policy or an overtime policy with something other
/// than zero. Those figures already exist, seeded, effective-dated and flagged for sign-off, in
/// <c>statutory_rules</c>. Copying them into this file would create a second set that drifts from
/// the first, so the assistant reads the real ones and leaves the field at zero when there is no
/// rule to read.
/// </summary>
public interface ISetupStatutoryDefaults
{
    /// <param name="iso3">Normalised 3-letter country, e.g. "SAU".</param>
    /// <param name="tenantId">Whose rules to read. A tenant that has overridden a rate gets its own
    /// value in the draft rather than the platform default it deliberately moved away from.</param>
    /// <returns>An empty dictionary when the country has no platform rules — never null, and never
    /// an exception: a draft is still worth producing without them.</returns>
    Task<IReadOnlyDictionary<string, string>> LoadAsync(string iso3, Guid tenantId, CancellationToken ct);
}
