namespace Zayra.Api.Infrastructure.Reports;

/// <summary>
/// Who may read one kind of business data, stated as the module that owns it states it.
///
/// <para>A report, a KPI or a trend is a summary of a module's data, so it admits AT LEAST everyone the
/// module's own read endpoint admits: the module's permission, OR a role on the module's role list, OR —
/// for modules whose lists have no permission gate at all, only the caller's employee scope — anyone,
/// with the rows cut to that scope. A domain may also admit the catalogue's own permission for the data
/// (compliance.read for identity documents) where the module predates it. A summary must never refuse
/// what the module it summarises would show; where the module is itself under-gated, the module is fixed
/// (loans, advances, bonus batches) rather than the summary made stricter than it.</para>
/// </summary>
/// <param name="DataLabel">Plain words for the data, used in refusals.</param>
/// <param name="Permissions">Any one of these admits.</param>
/// <param name="Roles">A role on this list admits, as the module's [Authorize(Roles=…)] does.</param>
/// <param name="ScopeOnly">The module's lists have no permission gate: everyone is admitted and the
/// employee scope is the only restriction, as it is in the module.</param>
/// <param name="Module">The module endpoint this mirrors, for the record.</param>
public sealed record DataDomain(string DataLabel, string[] Permissions, string[] Roles, bool ScopeOnly, string Module)
{
    public bool Admits(Func<string, bool> hasPermission, Func<string, bool> hasRole) =>
        ScopeOnly || Permissions.Any(hasPermission) || Roles.Any(hasRole);

    /// <summary>Why <see cref="Admits"/> said no, in words a user can act on.</summary>
    public string Refusal(string subject) =>
        $"{subject} {DataLabel}, which your role cannot view. Ask an administrator for {Requirement()} if you need it.";

    /// <summary>"the payroll.read permission, or one of the roles Admin, HR Manager".</summary>
    public string Requirement() =>
        (Permissions.Length == 1 ? $"the {Permissions[0]} permission" : $"one of the {string.Join(" or ", Permissions)} permissions") +
        (Roles.Length > 0 ? $", or one of the roles {string.Join(", ", Roles)}," : "");
}

/// <summary>The data domains, each mirroring its module's read gate.</summary>
public static class DataDomains
{
    public static readonly DataDomain Employees = new("employee records", ["employees.read"],
        ["Admin", "HR Manager", "HR Officer", "Payroll Officer", "Manager", "Auditor"], false, "GET /api/employees");

    public static readonly DataDomain Attendance = new("attendance records", ["attendance.read"], [], true,
        "GET /api/attendance/daily, /monthly, /reports/*, /regularization/my — no permission gate, employee scope only");

    public static readonly DataDomain Leave = new("leave records", ["leave.read"], [], true,
        "GET /api/leave/requests, /api/leave/balances, /api/leave/reports/* — no permission gate, employee scope only");

    public static readonly DataDomain Overtime = new("overtime records", ["overtime.read"], [], true,
        "GET /api/overtime/requests — no permission gate, employee scope only");

    public static readonly DataDomain Payroll = new("payroll data", ["payroll.read"],
        ["Admin", "HR Manager", "Payroll Manager", "Payroll Officer"], false, "GET /api/payroll/runs, /runs/{id}/slips");

    public static readonly DataDomain Recruitment = new("recruitment data", ["recruitment.read"],
        ["Admin", "HR Manager", "HR Officer", "Recruiter"], false, "GET /api/recruitment/reports/*");

    public static readonly DataDomain IdentityDocuments = new("visa and passport records", ["compliance.read"],
        ["Admin", "HR Manager", "HR Officer"], false, "GET /api/compliance/visa-tracking, /api/compliance/passports");

    public static readonly DataDomain Contracts = new("employment contracts", ["compliance.read"],
        ["Admin", "HR Manager", "HR Officer"], false, "GET /api/compliance/contracts");

    public static readonly DataDomain EmployeeDocuments = new("employee documents", ["employees.documents"], [], true,
        "GET /api/employees/{id}/documents — no permission gate, employee scope only");

    public static readonly DataDomain Loans = new("loans and salary advances", ["loans.read", "loans.write"], [], false,
        "GET /api/finance/loans, /api/finance/advances — gated on loans.read or loans.write in F10");

    public static readonly DataDomain Bonuses = new("bonus payouts", ["payroll.read"], [], false,
        "GET /api/finance/bonuses/batches — gated on payroll.read in F10");

    public static readonly DataDomain Qiwa = new("Qiwa readiness", ["qiwa.read", "compliance.read"], [], false,
        "GET /api/qiwa/readiness-summary (qiwa.read); compliance.read is the catalogue's permission for the same documents");

    public static readonly DataDomain Saudization = new("Saudization standing", ["compliance.read", "qiwa.read"], [], false,
        "GET /api/saudi-compliance/nitaqat");
}

/// <summary>
/// Authorizes a report against the BUSINESS DATA it exposes.
///
/// <para><c>reports.read</c>, <c>reports.export</c> and <c>reports.schedule</c> grant the reporting
/// capability. They used to be the only check, so any role holding <c>reports.read</c> read the payroll
/// register and the loan book. Each report now also asks its <see cref="DataDomain"/>, which admits
/// exactly who the module that owns the data admits (and never fewer).</para>
///
/// <para>One policy serves the interactive endpoints AND the scheduled-report worker, which evaluates it
/// against the schedule owner, and each recipient, on every run.</para>
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

    private sealed record Rule(DataDomain Domain, bool EmployeeKeyed = true, bool GroupOnly = false, string[]? SensitiveFields = null);

    private static readonly IReadOnlyDictionary<string, Rule> Rules =
        new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase)
        {
            ["hr.headcount"] = new(DataDomains.Employees),
            ["hr.new-joiners"] = new(DataDomains.Employees),
            ["hr.exits"] = new(DataDomains.Employees),
            ["hr.probation"] = new(DataDomains.Employees),
            ["hr.status"] = new(DataDomains.Employees),
            ["hr.nationality-mix"] = new(DataDomains.Employees),

            ["attendance.daily"] = new(DataDomains.Attendance),
            ["attendance.monthly"] = new(DataDomains.Attendance),
            ["attendance.late-arrivals"] = new(DataDomains.Attendance),
            ["attendance.absences"] = new(DataDomains.Attendance),
            ["attendance.corrections"] = new(DataDomains.Attendance),

            ["leave.balance"] = new(DataDomains.Leave),
            ["leave.usage"] = new(DataDomains.Leave),
            ["leave.pending"] = new(DataDomains.Leave),

            ["overtime.requests"] = new(DataDomains.Overtime),
            ["overtime.approved"] = new(DataDomains.Overtime),

            ["payroll.register"] = new(DataDomains.Payroll),
            ["payroll.summary"] = new(DataDomains.Payroll),
            ["payroll.slips"] = new(DataDomains.Payroll),

            // Candidates are not employees, so no employee scope can narrow these rows.
            ["recruitment.pipeline"] = new(DataDomains.Recruitment, EmployeeKeyed: false),
            ["recruitment.time-to-hire"] = new(DataDomains.Recruitment, EmployeeKeyed: false),

            ["compliance.visa-expiry"] = new(DataDomains.IdentityDocuments, SensitiveFields: ["VisaNumber"]),
            ["compliance.passport-expiry"] = new(DataDomains.IdentityDocuments, SensitiveFields: ["PassportNumber"]),
            ["compliance.contract-expiry"] = new(DataDomains.Contracts),
            ["compliance.document-compliance"] = new(DataDomains.EmployeeDocuments),

            ["finance.loan-balance"] = new(DataDomains.Loans),
            ["finance.advance-report"] = new(DataDomains.Loans),
            // A bonus batch is recorded for the whole group and carries no legal entity, so its totals
            // cannot be cut down to one company: only a group-level caller may see them.
            ["finance.bonus-payout"] = new(DataDomains.Bonuses, EmployeeKeyed: false, GroupOnly: true),

            ["qiwa.readiness"] = new(DataDomains.Qiwa),
            ["compliance.saudization"] = new(DataDomains.Saudization, EmployeeKeyed: false),
        };

    /// <summary>Every report key the policy knows. The catalog, the executor and the scheduler must agree with it.</summary>
    public static IReadOnlyCollection<string> Keys => Rules.Keys.ToArray();

    public static bool IsKnown(string? reportKey) => reportKey is not null && Rules.ContainsKey(reportKey);

    /// <summary>The domain a report reads. Null for an unknown key.</summary>
    public static DataDomain? DomainOf(string reportKey) =>
        Rules.TryGetValue(reportKey, out var rule) ? rule.Domain : null;

    /// <summary>The permissions any ONE of which unlocks the report's data. Empty for an unknown key.</summary>
    public static IReadOnlyList<string> AcceptedPermissions(string reportKey) =>
        Rules.TryGetValue(reportKey, out var rule) ? rule.Domain.Permissions : Array.Empty<string>();

    /// <summary>The roles that unlock the report's data because its module admits them by name.</summary>
    public static IReadOnlyList<string> AcceptedRoles(string reportKey) =>
        Rules.TryGetValue(reportKey, out var rule) ? rule.Domain.Roles : Array.Empty<string>();

    /// <summary>
    /// True when the caller may read the data behind <paramref name="reportKey"/>. An unknown key is
    /// denied: it exposes nothing, and the controller answers it with a 404 before asking.
    /// </summary>
    public static bool CanAccess(string reportKey, Func<string, bool> hasPermission, Func<string, bool> hasRole) =>
        Rules.TryGetValue(reportKey, out var rule) && rule.Domain.Admits(hasPermission, hasRole);

    /// <summary>Why <see cref="CanAccess"/> said no, in words a user can act on.</summary>
    public static string DenialMessage(string reportKey) =>
        Rules.TryGetValue(reportKey, out var rule) ? rule.Domain.Refusal("This report shows") : "This report does not exist.";

    /// <summary>The same refusal, said about someone else: the owner or a recipient of a scheduled report.</summary>
    public static string ThirdPartyDenialMessage(string who, string reportKey) =>
        Rules.TryGetValue(reportKey, out var rule)
            ? $"{who} can no longer view {rule.Domain.DataLabel}, which this report shows (it needs {rule.Domain.Requirement().TrimEnd(',')})."
            : "The scheduled report no longer exists.";

    /// <summary>
    /// The organisation-scope rule, stated once for callers that have permissions but no
    /// <see cref="Zayra.Api.Application.Common.IDataScopeService"/> result: the scheduled-report worker
    /// (whose owner and recipients are not the signed-in user) and the analytics endpoints. It is the same
    /// test <c>DataScopeService</c> applies first — <c>employees.write</c>, or <c>employees.read</c>
    /// without <c>manager.read</c> — and a test pins the two together.
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
            return $"This report summarises {rule.Domain.DataLabel} across the organisation and cannot be limited to " +
                   "your team, so it needs organisation-wide access.";
        if (rule.GroupOnly && !groupLevel)
            return $"{Capitalise(rule.Domain.DataLabel)} are recorded for the whole group, not per company, so this " +
                   "report needs access to every company.";
        return null;
    }

    /// <summary>The columns this report masks when the caller lacks <see cref="SensitivePermission"/>.</summary>
    public static IReadOnlyList<string> RestrictedFields(string reportKey, bool canSeeSensitive) =>
        !canSeeSensitive && Rules.TryGetValue(reportKey, out var rule) && rule.SensitiveFields is { } fields
            ? fields
            : Array.Empty<string>();

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
