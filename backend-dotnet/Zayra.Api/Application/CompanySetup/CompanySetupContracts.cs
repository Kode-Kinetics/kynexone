namespace Zayra.Api.Application.CompanySetup;

// The create-company wizard's wire contract — docs/design/company-setup-wizard.md, section "API".
// The TypeScript mirror is frontend/src/api/companySetup.ts. JSON is camelCase (the API's default naming), so a
// property here is the contract's field name with its first letter upper-cased. Do not rename a member without
// changing the contract through the Integration Owner: the frontend is built against these exact names.

/// <summary>Vocabularies used in the shapes below, so the server never spells one two ways.</summary>
public static class CompanySetupVocabulary
{
    public const string StartFromDefaults = "defaults";
    public const string StartFromCopy = "copy";

    public const string AdminInvite = "invite";
    public const string AdminExisting = "existing";
    public const string AdminGroupOnly = "groupOnly";

    public const string ActionCreate = "create";
    public const string ActionReuse = "reuse";
    public const string ActionConflict = "conflict";
    /// <summary>Existing-company mode (Addendum A3): an existing row whose fields change; <c>reason</c> names them.</summary>
    public const string ActionUpdate = "update";
    /// <summary>Existing-company mode (Addendum A3): an existing row the commit leaves exactly as it is.</summary>
    public const string ActionUnchanged = "unchanged";

    public const string ReadinessDone = "done";
    public const string ReadinessTodo = "todo";
    public const string ReadinessRecommended = "recommended";
    public const string ScopeCompany = "company";
    public const string ScopeWorkspace = "workspace";

    public static class Areas
    {
        public const string Company = "company";
        public const string Branch = "branch";
        public const string Location = "location";
        public const string CostCentre = "costCentre";
        public const string Department = "department";
        public const string Designation = "designation";
        public const string Grade = "grade";
        public const string EmployeeIdRule = "employeeIdRule";
        public const string Cutover = "cutover";
        public const string HolidayCalendar = "holidayCalendar";
        public const string LeavePolicy = "leavePolicy";
        public const string GccSetting = "gccSetting";
        public const string WorkWeek = "workWeek";
        public const string CompanyAdmin = "companyAdmin";
    }
}

public sealed record CompanySetupContext(
    bool CanCreate,
    string? RefusalCode,
    string? RefusalMessage,
    string CreationMode,
    string AccountType,
    int CompaniesUsed,
    int? CompaniesAllowed,
    bool IsGroupScope,
    IReadOnlyList<CompanySetupSisterCompany> SisterCompanies,
    CompanySetupSharedCatalog SharedCatalog,
    string? GradesCurrency,
    /// <summary>Whether this caller may make someone a company super admin — the same role ceiling User
    /// Management applies (group scope + the Admin role + Admin assignable + security.manage).</summary>
    bool CanGrantCompanyAdmin = false)
{
    /// <summary>Addendum A5: the active companies this caller may run existing-company mode for (the chooser's cards).
    /// A group-scope caller: every active company (the switcher's selection, when one narrows the request). A company
    /// super admin (company-scoped Admin): their own companies only. Anyone else: none.</summary>
    public IReadOnlyList<CompanySetupManageableCompany> ManageableCompanies { get; init; } = [];
}

/// <summary>One chooser card: the company, its readiness ("5 of 9 done" counts every item <c>GET /readiness</c> lists),
/// and the open existing-mode draft to resume, if there is one (one per company, Addendum A1).</summary>
public sealed record CompanySetupManageableCompany(
    Guid Id,
    string LegalNameEn,
    string? LegalNameAr,
    string CountryCode,
    int ReadinessDone,
    int ReadinessTotal,
    Guid? OpenDraftId);

public sealed record CompanySetupSisterCompany(Guid Id, string LegalNameEn, string CountryCode);

public sealed record CompanySetupSharedCatalog(
    IReadOnlyList<CodeName> Departments,
    IReadOnlyList<CodeName> Designations,
    IReadOnlyList<GradeRef> Grades);

public sealed record CodeName(string Code, string NameEn, string? NameAr);

public sealed record GradeRef(string Code, string NameEn, string? NameAr, string Currency);

public sealed record CompanySetupDraftBody(
    string StartFrom,
    Guid? CopyFromCompanyId,
    CompanySetupCompany Company,
    List<CompanySetupBranch>? Branches,
    List<CompanySetupCostCentre>? CostCentres,
    List<CompanySetupDepartment>? Departments,
    List<CompanySetupDesignation>? Designations,
    List<CompanySetupGrade>? Grades,
    CompanySetupRules Rules,
    CompanySetupCompanyAdmin CompanyAdmin,
    /// <summary>Addendum A1: null = create a new company; set = complete this existing company. Fixed for the draft's life.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? ExistingCompanyId = null);

public sealed record CompanySetupCompany(
    string LegalNameEn,
    string? LegalNameAr,
    string? TradeName,
    string CountryCode,
    string Currency,
    string RegistrationNumber,
    string? MolEstablishmentNumber,
    string? GosiEmployerId,
    string? UnifiedNationalNumber,
    string? TaxNumber,
    string? QiwaEstablishmentId,
    string? EmailDomain,
    string? WorkEmailPattern,
    string? ErpCompanyCode,
    /// <summary>"YYYY-MM". Its first day becomes the company's Planned cutover.</summary>
    string FirstPayrollMonth);

public sealed record CompanySetupBranch(
    string Code,
    string NameEn,
    string? NameAr,
    string City,
    string TimeZoneId,
    bool IsHeadOffice,
    List<CompanySetupLocation>? Locations,
    /// <summary>Addendum A2: the id of the existing row this entry edits (prefilled). Null = a new row.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? ExistingId = null);

public sealed record CompanySetupLocation(
    string Code,
    string NameEn,
    string? NameAr,
    decimal? Latitude,
    decimal? Longitude,
    decimal? RadiusMeters,
    /// <summary>Addendum A2: the id of the existing row this entry edits (prefilled). Null = a new row.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? ExistingId = null);

public sealed record CompanySetupCostCentre(string Code, string NameEn, string? NameAr,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? ExistingId = null);

public sealed record CompanySetupDepartment(
    string Code,
    string NameEn,
    string? NameAr,
    string? ParentCode,
    string? BranchCode,
    string? CostCentreCode,
    /// <summary>Addendum A2: the id of the existing row this entry edits (prefilled). Null = a new row.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? ExistingId = null);

public sealed record CompanySetupDesignation(
    string Code,
    string TitleEn,
    string? TitleAr,
    string? DepartmentCode,
    string? GradeCode,
    /// <summary>Addendum A2: the id of the existing row this entry edits (prefilled). Null = a new row.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? ExistingId = null);

public sealed record CompanySetupGrade(
    string Code,
    string NameEn,
    string? NameAr,
    int Level,
    decimal MinSalary,
    decimal MidSalary,
    decimal MaxSalary,
    /// <summary>Addendum A2: the id of the existing row this entry edits (prefilled). Null = a new row.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? ExistingId = null);

public sealed record CompanySetupRules(
    CompanySetupWorkWeek WorkWeek,
    CompanySetupPackChoice HolidayCalendar,
    CompanySetupPackChoice LeavePolicies,
    CompanySetupEmployeeNumbering EmployeeNumbering);

/// <summary><see cref="WeekendDays"/> is the REST days ("Fri,Sat"). <see cref="Override"/> = write a company
/// override; otherwise the resolved working week is confirmed and nothing is written.</summary>
public sealed record CompanySetupWorkWeek(string WeekendDays, bool Override);

public sealed record CompanySetupPackChoice(bool UsePack);

public sealed record CompanySetupEmployeeNumbering(
    string Prefix,
    bool UseCountryPrefix,
    bool UseBranchPrefix,
    bool UseDepartmentPrefix,
    bool UseYear,
    int PaddingLength,
    int NextSequence);

public sealed record CompanySetupCompanyAdmin(
    string Mode,
    Guid? ExistingUserId,
    string? FullNameEn,
    string? FullNameAr,
    string? Email);

public sealed record CompanySetupDraft(
    Guid Id,
    int Version,
    int Step,
    string Status,
    CompanySetupDraftBody Body,
    Guid CreatedByUserId,
    string CreatedByName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? ResultCompanyId);

public sealed record CompanySetupDraftSummary(
    Guid Id,
    string LegalNameEn,
    int Step,
    DateTime UpdatedAtUtc,
    string CreatedByName,
    /// <summary>Set on an existing-company draft (Addendum A1), so the resume list can tell the two kinds apart.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? ExistingCompanyId = null);

/// <summary>PUT /drafts/{id} body.</summary>
public sealed record CompanySetupDraftUpdate(int Version, int Step, CompanySetupDraftBody Body);

/// <summary>POST / body (the commit). The Idempotency-Key header must equal <see cref="DraftId"/>.</summary>
public sealed record CompanySetupCommitRequest(Guid DraftId, int Version);

public sealed record CompanySetupPlan(
    bool Ok,
    IReadOnlyList<PlanItem> Items,
    IReadOnlyList<Problem> Problems,
    IReadOnlyList<Problem> Warnings);

public sealed record PlanItem(string Area, string Code, string Label, string Action, string? Reason, string? LabelAr = null);

/// <summary><see cref="Field"/> is a dotted lower-camel path with numeric indexes, e.g. <c>company.registrationNumber</c>,
/// <c>branches.0.code</c>, <c>departments.2.costCentreCode</c>.</summary>
public sealed record Problem(int Step, string Field, string Code, string Message, string? MessageAr);

/// <summary>The body of every 4xx answer from /api/company-setup: <c>{ code, message, messageAr?, field?, step? }</c>.
/// A refused commit or draft may add <c>plan</c> (the refused plan) or <c>currentVersion</c> (a stale save).</summary>
public sealed record CompanySetupError(string Code, string Message, string? MessageAr = null, string? Field = null, int? Step = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CompanySetupPlan? Plan { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? CurrentVersion { get; init; }
    /// <summary>The open draft that already completes this company (<c>existing_company_draft_open</c>): resume it.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DraftId { get; init; }
}

public sealed record CompanySetupResult(
    Guid CompanyId,
    IReadOnlyDictionary<string, int> Created,
    IReadOnlyDictionary<string, int> Reused,
    IReadOnlyList<ReadinessItem> Readiness,
    bool Replayed,
    /// <summary>Existing-company mode only: existing rows the commit changed, by area (omitted on create).</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, int>? Updated = null);

/// <summary><see cref="UserId"/> is set only on the <c>companyAdmin</c> item while that admin has not signed in yet: the
/// login the screen can send a single-use sign-in link to (<c>POST /api/access/users/{userId}/password-reset-link</c>).</summary>
public sealed record ReadinessItem(
    string Key,
    string Label,
    string? LabelAr,
    string Status,
    string Scope,
    string Href,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? UserId = null);
