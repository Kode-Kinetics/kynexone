namespace Zayra.Api.Infrastructure.Reports;

/// <summary>
/// Authorizes a report against the BUSINESS DATA it exposes.
///
/// <para><c>reports.read</c>, <c>reports.export</c> and <c>reports.schedule</c> grant the reporting
/// capability — running, downloading, scheduling. They used to be the only check, so any role holding
/// <c>reports.read</c> could read the payroll register, loan balances and passport numbers of the whole
/// workforce, whatever its payroll, loans or compliance rights said. This policy is the second check:
/// the caller must also hold the permission that guards the same data in its own module.</para>
///
/// <para>One policy serves the interactive endpoints AND the scheduled-report worker, which evaluates it
/// against the schedule OWNER's current permissions on every run. A schedule therefore stops delivering
/// the moment its owner could no longer open the report by hand.</para>
///
/// <para>Every report key must have an entry. <see cref="IsKnown"/> gates execution, so a report added to
/// the controller without an entry here returns 404 rather than running on <c>reports.read</c> alone.</para>
/// </summary>
public static class ReportAccessPolicy
{
    /// <summary>What an identity-document number reads as when the caller may not see it.</summary>
    public const string RestrictedValue = "Restricted";

    /// <summary>The permission that unlocks identity-document numbers, as on the employee export.</summary>
    public const string SensitivePermission = "employees.sensitive";

    private sealed record Rule(string[] AnyOf, string DataLabel, bool EmployeeKeyed = true, bool GroupOnly = false,
        string[]? SensitiveFields = null);

    private static readonly string[] EmployeesRead = ["employees.read"];
    private static readonly string[] AttendanceRead = ["attendance.read"];
    private static readonly string[] LeaveRead = ["leave.read"];
    private static readonly string[] OvertimeRead = ["overtime.read"];
    private static readonly string[] PayrollRead = ["payroll.read"];
    private static readonly string[] RecruitmentRead = ["recruitment.read"];
    private static readonly string[] LoansRead = ["loans.read"];
    // HR Manager and HR Officer hold employees.documents rather than compliance.read, and they are the
    // roles the visa/passport tracking screens are open to; compliance.read is the Compliance Officer's
    // and the Auditor's key to the same records.
    private static readonly string[] ComplianceRecords = ["compliance.read", "employees.documents"];
    private static readonly string[] Establishment = ["qiwa.read", "compliance.read"];

    private static readonly IReadOnlyDictionary<string, Rule> Rules =
        new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase)
        {
            ["hr.headcount"] = new(EmployeesRead, "employee records"),
            ["hr.new-joiners"] = new(EmployeesRead, "employee records"),
            ["hr.exits"] = new(EmployeesRead, "employee records"),
            ["hr.probation"] = new(EmployeesRead, "employee records"),
            ["hr.status"] = new(EmployeesRead, "employee records"),
            ["hr.nationality-mix"] = new(EmployeesRead, "employee records"),

            ["attendance.daily"] = new(AttendanceRead, "attendance records"),
            ["attendance.monthly"] = new(AttendanceRead, "attendance records"),
            ["attendance.late-arrivals"] = new(AttendanceRead, "attendance records"),
            ["attendance.absences"] = new(AttendanceRead, "attendance records"),
            ["attendance.corrections"] = new(AttendanceRead, "attendance records"),

            ["leave.balance"] = new(LeaveRead, "leave records"),
            ["leave.usage"] = new(LeaveRead, "leave records"),
            ["leave.pending"] = new(LeaveRead, "leave records"),

            ["overtime.requests"] = new(OvertimeRead, "overtime records"),
            ["overtime.approved"] = new(OvertimeRead, "overtime records"),

            ["payroll.register"] = new(PayrollRead, "payroll data"),
            ["payroll.summary"] = new(PayrollRead, "payroll data"),
            ["payroll.slips"] = new(PayrollRead, "payroll data"),

            // Candidates are not employees, so no employee scope can narrow these rows.
            ["recruitment.pipeline"] = new(RecruitmentRead, "recruitment data", EmployeeKeyed: false),
            ["recruitment.time-to-hire"] = new(RecruitmentRead, "recruitment data", EmployeeKeyed: false),

            ["compliance.visa-expiry"] = new(ComplianceRecords, "visa records", SensitiveFields: ["VisaNumber"]),
            ["compliance.passport-expiry"] = new(ComplianceRecords, "passport records", SensitiveFields: ["PassportNumber"]),
            ["compliance.contract-expiry"] = new(ComplianceRecords, "employment contracts"),
            ["compliance.document-compliance"] = new(ComplianceRecords, "employee documents"),

            ["finance.loan-balance"] = new(LoansRead, "loan balances"),
            ["finance.advance-report"] = new(LoansRead, "salary advances"),
            // A bonus batch is recorded for the whole group and carries no legal entity, so its totals
            // cannot be cut down to one company: only a group-level caller may see them.
            ["finance.bonus-payout"] = new(PayrollRead, "bonus payouts", EmployeeKeyed: false, GroupOnly: true),

            ["qiwa.readiness"] = new(Establishment, "Qiwa readiness"),
            ["compliance.saudization"] = new(Establishment, "Saudization standing", EmployeeKeyed: false),
        };

    /// <summary>Every report key the policy knows. The catalog, the executor and the scheduler must agree with it.</summary>
    public static IReadOnlyCollection<string> Keys => Rules.Keys.ToArray();

    public static bool IsKnown(string? reportKey) => reportKey is not null && Rules.ContainsKey(reportKey);

    /// <summary>The permissions any ONE of which unlocks the report's data. Empty for an unknown key.</summary>
    public static IReadOnlyList<string> AcceptedPermissions(string reportKey) =>
        Rules.TryGetValue(reportKey, out var rule) ? rule.AnyOf : Array.Empty<string>();

    /// <summary>
    /// True when the caller may read the data behind <paramref name="reportKey"/>. An unknown key is
    /// denied: it exposes nothing, and the controller answers it with a 404 before asking.
    /// </summary>
    public static bool CanAccess(string reportKey, Func<string, bool> hasPermission) =>
        Rules.TryGetValue(reportKey, out var rule) && rule.AnyOf.Any(hasPermission);

    /// <summary>Why <see cref="CanAccess"/> said no, in words a user can act on.</summary>
    public static string DenialMessage(string reportKey) =>
        Rules.TryGetValue(reportKey, out var rule)
            ? $"This report shows {rule.DataLabel}, which your role cannot view. " +
              $"Ask an administrator for {Describe(rule.AnyOf)} if you need it."
            : "This report does not exist.";

    /// <summary>The same refusal, said about someone else: the owner of a scheduled report.</summary>
    public static string OwnerDenialMessage(string reportKey) =>
        Rules.TryGetValue(reportKey, out var rule)
            ? $"The schedule's owner can no longer view {rule.DataLabel}, which this report shows " +
              $"(it needs {Describe(rule.AnyOf)})."
            : "The scheduled report no longer exists.";

    /// <summary>
    /// The organisation-scope rule, stated once for callers that have permissions but no
    /// <see cref="Zayra.Api.Application.Common.IDataScopeService"/> result: the scheduled-report worker
    /// (whose owner is not the signed-in user) and the analytics endpoints. It is the same test
    /// <c>DataScopeService</c> applies first — <c>employees.write</c>, or <c>employees.read</c> without
    /// <c>manager.read</c> — and a test pins the two together.
    /// </summary>
    public static bool GrantsOrganisationScope(Func<string, bool> hasPermission) =>
        hasPermission("employees.write") || (hasPermission("employees.read") && !hasPermission("manager.read"));

    /// <summary>
    /// A denial that depends on HOW WIDELY the caller may see, not on what they may see. Returns null
    /// when the report can be cut to the caller's scope.
    /// </summary>
    /// <param name="organisationLevel">The caller's employee scope is the whole organisation (or their
    /// companies' slice of it), rather than a team, a department or themselves.</param>
    /// <param name="groupLevel">The caller sees every legal entity in the tenant.</param>
    public static string? ScopeDenial(string reportKey, bool organisationLevel, bool groupLevel)
    {
        if (!Rules.TryGetValue(reportKey, out var rule)) return null;
        if (!rule.EmployeeKeyed && !organisationLevel)
            return $"This report summarises {rule.DataLabel} across the organisation and cannot be limited to " +
                   "your team, so it needs organisation-wide access.";
        if (rule.GroupOnly && !groupLevel)
            return $"{Capitalise(rule.DataLabel)} are recorded for the whole group, not per company, so this " +
                   "report needs access to every company.";
        return null;
    }

    /// <summary>The columns this report masks when the caller lacks <see cref="SensitivePermission"/>.</summary>
    public static IReadOnlyList<string> RestrictedFields(string reportKey, bool canSeeSensitive) =>
        !canSeeSensitive && Rules.TryGetValue(reportKey, out var rule) && rule.SensitiveFields is { } fields
            ? fields
            : Array.Empty<string>();

    private static string Describe(IReadOnlyList<string> anyOf) =>
        anyOf.Count == 1 ? $"the {anyOf[0]} permission" : $"one of the {string.Join(" or ", anyOf)} permissions";

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>
/// The slice of the tenant one report execution may read. Built from the signed-in user for the
/// interactive endpoints and from the schedule owner for the worker, which has no HTTP context and so
/// no ambient company filter: every restriction the worker needs must be in here.
/// </summary>
/// <param name="EmployeeIds">Employees whose rows may appear; null means the whole organisation.</param>
/// <param name="CompanyIds">Legal entities whose rows may appear; null means every company in the tenant.</param>
/// <param name="CanSeeSensitive">Identity-document numbers are shown rather than masked.</param>
public sealed record ReportDataScope(
    IReadOnlyCollection<int>? EmployeeIds,
    IReadOnlyCollection<Guid>? CompanyIds,
    bool CanSeeSensitive);
