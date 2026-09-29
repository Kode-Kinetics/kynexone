using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Modules;

/// <summary>
/// How much freedom a tenant administrator has over a module.
/// </summary>
public enum ModuleLock
{
    /// <summary>The tenant administrator may switch this module on and off freely.</summary>
    Optional,

    /// <summary>
    /// Never disableable. The product is incoherent without it — either everything references it
    /// (core HR), or switching it off would remove the record of what happened (audit).
    /// </summary>
    Core,

    /// <summary>
    /// Not disableable while the legal obligation applies. The obligation is conditional on the
    /// tenant's country (<see cref="ModuleDefinition.StatutoryCountries"/>) and, for obligations
    /// that only arise from running something here, on
    /// <see cref="ModuleDefinition.ObligationArisesFrom"/>.
    /// A tenant outside those countries, or one that does not run the originating module, may
    /// switch it off — the obligation is simply not theirs.
    /// </summary>
    Statutory,
}

/// <summary>
/// One configurable module: what it is called, whether the client may switch it off, and
/// — the part that makes the switch real — every surface it owns.
/// </summary>
public sealed record ModuleDefinition
{
    public required string Key { get; init; }
    public required string LabelEn { get; init; }
    public required string LabelAr { get; init; }
    public required string Description { get; init; }

    public ModuleLock Lock { get; init; } = ModuleLock.Optional;

    /// <summary>Why this module cannot be switched off. Shown to the administrator verbatim.</summary>
    public string? LockReason { get; init; }

    /// <summary>
    /// <see cref="ModuleLock.Statutory"/> only: ISO-3166 alpha-2 codes where the obligation applies.
    /// Empty means "everywhere".
    /// </summary>
    public IReadOnlyList<string> StatutoryCountries { get; init; } = [];

    /// <summary>
    /// <see cref="ModuleLock.Statutory"/> only: the module whose use creates the obligation.
    /// GOSI contribution filing is only owed on wages this system pays; a tenant that runs payroll
    /// somewhere else files GOSI from there, and locking it on here would be a lie, not a safeguard.
    /// </summary>
    public string? ObligationArisesFrom { get; init; }

    /// <summary>
    /// API route prefixes this module owns. Longest prefix wins, so a module may carve a sub-path
    /// out of another module's prefix (<c>/api/ess/timesheets</c> out of <c>/api/ess</c>).
    /// </summary>
    public IReadOnlyList<string> RoutePrefixes { get; init; } = [];

    /// <summary>Frontend route paths this module owns — the route guard reads these.</summary>
    public IReadOnlyList<string> NavPaths { get; init; } = [];

    /// <summary>
    /// Notification categories this module owns. When the module is off these are suppressed at the
    /// single dispatch funnel, so a switched-off module cannot keep mailing people.
    /// Mandatory categories (<see cref="NotificationCategories.Mandatory"/>) are never suppressed.
    /// </summary>
    public IReadOnlyList<string> NotificationCategories { get; init; } = [];
}

/// <summary>
/// The authoritative registry of client-configurable modules.
///
/// <para>This exists because module enablement previously had three disagreeing sources — a
/// hand-kept allow-list and route map in <c>FeatureFlagGuardFilter</c>, a 16-entry array in
/// <c>PlatformController</c>, and two different arrays in the frontend — and nothing reconciled
/// them. Seven route prefixes in the guard's map matched no controller at all, so the
/// <c>finance</c> and <c>wps_export</c> flags gated nothing while appearing switchable.</para>
///
/// <para><b>Every enforcement layer reads this file.</b> The API guard, the tenant-facing module
/// API, the notification dispatcher and the frontend route guard all derive from
/// <see cref="All"/>; <c>ModuleCatalogCoverageTests</c> fails the build when a controller route
/// prefix is not classified here. A module added without its surfaces is a build failure, not a
/// silent hole.</para>
///
/// <para><b>Keys that are deliberately NOT modules</b> are listed in <see cref="NonModuleKeys"/>
/// with the reason. Following the doctrine in <c>ApprovalPoliciesController</c>, the write API
/// refuses them with a machine-readable code rather than storing a flag nothing reads.</para>
/// </summary>
public static class ModuleCatalog
{
    // ── The non-disableable reasoning, written down ──────────────────────────────
    //
    // Core:      everything references it, or switching it off destroys the record of what
    //            happened. No tenant, in any country, may switch these off.
    // Statutory: switching it off would switch off a legal obligation. Conditional — see
    //            ModuleLock.Statutory. A KSA tenant cannot turn GOSI off because they find the
    //            monthly filing inconvenient; a UK tenant has no GOSI obligation and may.
    // Optional:  a genuine business choice. An office-only tenant has no shift rosters; a tenant
    //            that recruits through an agency has no use for the recruitment pipeline.
    //
    // The conservative direction is Core: a route classified Core behaves exactly as it did
    // before this catalog existed (always allowed). Only routes we are confident are a business
    // choice became switches, because a switch that cannot be honoured end to end is worse than
    // no switch at all.

    private const string CoreHrReason =
        "Core HR holds the employee record every other module points at. Switching it off would "
        + "leave payroll, leave and approvals referring to people the system no longer serves.";

    private const string AccessReason =
        "Sign-in, roles and permissions are how the product knows who anyone is. There is no "
        + "coherent state in which a tenant is using KynexOne with authentication switched off.";

    private const string AuditReason =
        "The audit log is the record of what was changed and by whom. A tenant that could switch "
        + "it off could erase the evidence of doing so — and KSA labour inspections, GOSI audits "
        + "and dispute hearings all rest on it.";

    private const string ApprovalsReason =
        "Leave, expenses, loans, offboarding and employee-data changes all route through approvals. "
        + "Switching it off would strand every request already in flight.";

    private const string StatutoryGosiReason =
        "GOSI registration and monthly contribution filing are compulsory for employers in Saudi "
        + "Arabia (Social Insurance Law). A module that reports a statutory contribution is not a "
        + "preference. It becomes switchable only if payroll is not run in KynexOne at all, in "
        + "which case the filing is owed from wherever payroll does run.";

    private const string StatutorySaudizationReason =
        "Saudization (Nitaqat) banding and Qiwa reporting are compulsory for employers in Saudi "
        + "Arabia regardless of where payroll runs. The obligation follows from employing people, "
        + "which this tenant does.";

    private const string StatutoryComplianceReason =
        "Iqama, passport and work-permit expiry tracking is how the tenant avoids employing "
        + "someone on a lapsed permit — an offence for the employer, not the employee. Switching "
        + "off the tracking does not switch off the liability.";

    /// <summary>
    /// Every module, in the order an administrator should see them.
    /// </summary>
    public static readonly IReadOnlyList<ModuleDefinition> All =
    [
        // ── Core: never disableable ──────────────────────────────────────────────
        new ModuleDefinition
        {
            Key = ModuleKeys.CoreHr,
            LabelEn = "Core HR",
            LabelAr = "الموارد البشرية الأساسية",
            Description = "Employee records, org structure, branches, departments, grades and positions.",
            Lock = ModuleLock.Core,
            LockReason = CoreHrReason,
            RoutePrefixes =
            [
                "/api/employees", "/api/branches", "/api/departments", "/api/designations",
                "/api/grades", "/api/cost-centers", "/api/locations", "/api/positions",
                "/api/companies", "/api/organization", "/api/reference", "/api/establishment",
                "/api/jobs", "/api/planning",
            ],
            NavPaths = ["/people", "/org-chart", "/companies"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.AccessControl,
            LabelEn = "Access & Identity",
            LabelAr = "الوصول والهوية",
            Description = "Sign-in, multi-factor authentication, users, roles and permissions.",
            Lock = ModuleLock.Core,
            LockReason = AccessReason,
            RoutePrefixes = ["/api/auth", "/api/access", "/api/enterprise-identity", "/api/platform"],
            NavPaths = ["/user-management"],
            NotificationCategories = [NotificationCategories.Security],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Audit,
            LabelEn = "Audit Trail",
            LabelAr = "سجل التدقيق",
            Description = "The immutable record of who changed what, and when.",
            Lock = ModuleLock.Core,
            LockReason = AuditReason,
            RoutePrefixes = ["/api/audit-logs"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Approvals,
            LabelEn = "Approvals",
            LabelAr = "الموافقات",
            Description = "Approval routing and the request centre every other module submits into.",
            Lock = ModuleLock.Core,
            LockReason = ApprovalsReason,
            RoutePrefixes =
            [
                "/api/approval-requests", "/api/approval-workflows",
                "/api/approval-policies", "/api/hr-requests",
            ],
            NavPaths = ["/approvals", "/hr-requests"],
            NotificationCategories = [NotificationCategories.Approvals, NotificationCategories.HrRequests],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.LeaveAttendance,
            LabelEn = "Leave & Attendance",
            LabelAr = "الإجازات والحضور",
            Description = "Leave entitlement, balances and accrual; attendance and working-time records.",
            Lock = ModuleLock.Core,
            LockReason =
                "Annual leave is a statutory entitlement that accrues whether or not it is tracked, "
                + "and working-time records are the basis of end-of-service and overtime "
                + "calculations. Switching the tracking off would not stop the entitlement accruing "
                + "— it would only stop the tenant knowing what it owes.",
            RoutePrefixes = ["/api/leave", "/api/leave-requests", "/api/attendance"],
            NavPaths = ["/leave", "/attendance"],
            NotificationCategories = [NotificationCategories.Leave, NotificationCategories.Attendance],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.SelfService,
            LabelEn = "Employee Self-Service",
            LabelAr = "الخدمة الذاتية للموظف",
            Description = "The employee's own view of their profile, payslips, leave and documents.",
            Lock = ModuleLock.Core,
            LockReason =
                "Self-service is how an employee obtains their own payslip and leave record. "
                + "Access to one's own pay record is not an employer preference.",
            RoutePrefixes = ["/api/ess"],
            NavPaths = ["/ess"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Documents,
            LabelEn = "Documents & Policies",
            LabelAr = "المستندات والسياسات",
            Description = "Policy documents and employee document storage.",
            Lock = ModuleLock.Core,
            LockReason =
                "Employment contracts and policy acknowledgements are records the tenant is "
                + "required to retain and produce on request.",
            // PolicyDocumentController is routed at /api/ai/policy, which sits inside the AI
            // Assistant's /api/ai prefix. Claiming the longer prefix here is what stops switching
            // the AI Assistant off from also taking the tenant's policy documents offline.
            RoutePrefixes = ["/api/ai/policy"],
            NotificationCategories = [NotificationCategories.Documents],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Offboarding,
            LabelEn = "Offboarding",
            LabelAr = "إنهاء الخدمة",
            Description = "Leaver workflow, clearance and final settlement.",
            Lock = ModuleLock.Core,
            LockReason =
                "End-of-service benefit is a statutory entitlement calculated on the leaver's "
                + "service and final wage. Every tenant has leavers, and every leaver is owed a "
                + "settlement.",
            RoutePrefixes = ["/api/offboarding"],
            NavPaths = ["/offboarding"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Reporting,
            LabelEn = "Reports & Analytics",
            LabelAr = "التقارير والتحليلات",
            Description = "Scheduled and ad-hoc reporting across whichever modules are enabled.",
            Lock = ModuleLock.Core,
            LockReason =
                "Reporting is the read surface over every other module rather than a module of its "
                + "own; a disabled module simply contributes no rows.",
            RoutePrefixes = ["/api/reports", "/api/analytics"],
            NavPaths = ["/reports"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Dashboard,
            LabelEn = "Dashboard",
            LabelAr = "لوحة المعلومات",
            Description = "The landing overview. Individual cards follow their own module's switch.",
            Lock = ModuleLock.Core,
            LockReason = "The dashboard is the application's landing surface.",
            RoutePrefixes = ["/api/dashboard", "/api/group/dashboard"],
            NavPaths = ["/dashboard", "/group"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Notifications,
            LabelEn = "Notifications",
            LabelAr = "الإشعارات",
            Description = "Delivery of in-app, email, SMS and push notices. Per-category opt-out lives in each employee's preferences.",
            Lock = ModuleLock.Core,
            LockReason =
                "Notification delivery is the transport every module shares. Employees control "
                + "what they receive per category in their own preferences; silencing the "
                + "transport tenant-wide would suppress security notices too.",
            RoutePrefixes = ["/api/notifications"],
            NotificationCategories = [NotificationCategories.Announcements],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Configuration,
            LabelEn = "Configuration & Setup",
            LabelAr = "الإعداد والتهيئة",
            Description = "Tenant settings, localisation, country packs, branding and the setup assistant.",
            Lock = ModuleLock.Core,
            LockReason =
                "This is the surface that holds the module switches themselves. It must never be "
                + "able to switch itself off.",
            RoutePrefixes =
            [
                "/api/tenant-admin", "/api/tenant-hr-config", "/api/tenant-modules", "/api/setup",
                "/api/setup-assistant", "/api/localization", "/api/help-text", "/api/help-texts",
                "/api/country-packs", "/api/statutory-rules", "/api/migrations", "/api/pricing",
                "/api/features", "/api/admin", "/api/company-tax-policies",
                "/api/company-compliance-profiles", "/api/finance/gl", "/api/finance/rates",
            ],
            NavPaths = ["/setup", "/tenant-admin", "/tax-policies", "/compliance-profiles", "/opening-balances"],
        },

        // ── Statutory: locked while the obligation applies ───────────────────────
        new ModuleDefinition
        {
            Key = ModuleKeys.Gosi,
            LabelEn = "GOSI",
            LabelAr = "التأمينات الاجتماعية",
            Description = "Saudi social-insurance registration, contribution calculation and monthly filing.",
            Lock = ModuleLock.Statutory,
            LockReason = StatutoryGosiReason,
            StatutoryCountries = ["SA"],
            ObligationArisesFrom = ModuleKeys.Payroll,
            RoutePrefixes = ["/api/gosi"],
            NavPaths = ["/gosi-filing"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Saudization,
            LabelEn = "Saudization & Qiwa",
            LabelAr = "السعودة وقوى",
            Description = "Nitaqat banding, Saudization ratio and Qiwa reporting.",
            Lock = ModuleLock.Statutory,
            LockReason = StatutorySaudizationReason,
            StatutoryCountries = ["SA"],
            RoutePrefixes = ["/api/qiwa", "/api/saudi-compliance"],
            NavPaths = ["/saudi-compliance"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Compliance,
            LabelEn = "Compliance & Document Expiry",
            LabelAr = "الامتثال وانتهاء المستندات",
            Description = "Iqama, passport, visa and work-permit expiry tracking, contracts and compliance reporting.",
            Lock = ModuleLock.Statutory,
            LockReason = StatutoryComplianceReason,
            StatutoryCountries = ["SA", "AE", "KW", "QA", "BH", "OM"],
            RoutePrefixes = ["/api/compliance"],
            NavPaths = ["/compliance"],
        },

        // ── Optional: genuine business choices ───────────────────────────────────
        new ModuleDefinition
        {
            Key = ModuleKeys.Payroll,
            LabelEn = "Payroll",
            LabelAr = "الرواتب",
            Description = "Salary structures, payroll runs, payslips and bank/WPS payment files.",
            RoutePrefixes = ["/api/payroll"],
            NavPaths = ["/payroll", "/payroll/variance"],
            NotificationCategories = [NotificationCategories.Payslip],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Recruitment,
            LabelEn = "Recruitment",
            LabelAr = "التوظيف",
            Description = "Requisitions, openings, candidates, interviews and offers.",
            RoutePrefixes = ["/api/recruitment"],
            NavPaths = ["/recruitment"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Performance,
            LabelEn = "Performance",
            LabelAr = "الأداء",
            Description = "Goals, review cycles, appraisals, probation and calibration.",
            RoutePrefixes = ["/api/performance"],
            NavPaths = ["/performance"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Shifts,
            LabelEn = "Shifts & Rosters",
            LabelAr = "الورديات والجداول",
            Description = "Shift definitions and roster planning.",
            RoutePrefixes = ["/api/shifts"],
            NavPaths = ["/shifts"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Overtime,
            LabelEn = "Overtime",
            LabelAr = "العمل الإضافي",
            Description =
                "Pre-authorisation and approval of extra hours. Statutory overtime pay is a payroll "
                + "pay component and is unaffected by this switch.",
            RoutePrefixes = ["/api/overtime"],
            NavPaths = ["/overtime"],
            NotificationCategories = [NotificationCategories.Overtime],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Timesheets,
            LabelEn = "Timesheets",
            LabelAr = "سجلات الدوام",
            Description = "Project and task time capture with manager sign-off.",
            RoutePrefixes = ["/api/timesheets", "/api/ess/timesheets"],
            NavPaths = ["/timesheets"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Finance,
            LabelEn = "Loans & Advances",
            LabelAr = "القروض والسلف",
            Description = "Employee loans, salary advances and bonuses, with repayment scheduling.",
            RoutePrefixes = ["/api/finance/loans", "/api/finance/advances", "/api/finance/bonuses"],
            NavPaths = ["/loans"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.Benefits,
            LabelEn = "Benefits",
            LabelAr = "المزايا",
            Description = "Benefit plans, eligibility and employee enrolment.",
            RoutePrefixes = ["/api/compensation/benefits", "/api/ess/benefits"],
            NavPaths = ["/benefits", "/ess/benefits"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.HrLetters,
            LabelEn = "HR Letters",
            LabelAr = "الخطابات",
            Description = "Templated employment letters, salary certificates and the issued-letter register.",
            RoutePrefixes = ["/api/hr-letters"],
            NavPaths = ["/hr-letters"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.AiAssistant,
            LabelEn = "AI Assistant",
            LabelAr = "المساعد الذكي",
            Description = "Natural-language questions over your HR data, and generated insights.",
            RoutePrefixes = ["/api/ai"],
            NavPaths = ["/ai-assistant"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.PayslipTemplateDesigner,
            LabelEn = "Payslip Template Designer",
            LabelAr = "مصمم قوالب الرواتب",
            Description = "Customise payslip layout and wording. Payslips still issue from the default template when off.",
            RoutePrefixes = ["/api/payslip-templates"],
            NavPaths = ["/payroll/templates"],
        },
        new ModuleDefinition
        {
            Key = ModuleKeys.MobileApp,
            LabelEn = "Mobile App",
            LabelAr = "تطبيق الجوال",
            Description = "The KynexOne mobile app endpoints for your employees.",
            RoutePrefixes = ["/api/mobile"],
        },
    ];

    /// <summary>
    /// Feature-flag keys that exist in <see cref="FeatureKeys"/> but are deliberately NOT offered
    /// as client switches, with the reason. The write API refuses these with
    /// <c>module_not_enforceable</c> rather than storing a flag no runtime path reads — the same
    /// doctrine <c>ApprovalPoliciesController</c> applies to retired configuration.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NonModuleKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FeatureKeys.WpsExport] =
                "WPS file generation is a statutory output of payroll and is reached through "
                + "/api/payroll/payment-batches, so it has no route of its own to gate. Switch payroll "
                + "off if wages are not paid from KynexOne.",
            [FeatureKeys.EosbCalc] =
                "End-of-service benefit is a statutory entitlement computed inside payroll and "
                + "offboarding. No runtime path reads this key.",
            [FeatureKeys.ResumeScreening] =
                "Sub-feature of Recruitment with no runtime reader. Switch Recruitment off instead.",
            [FeatureKeys.PayrollAiValidation] =
                "Sub-feature of Payroll with no runtime reader. Switch Payroll or the AI Assistant off instead.",
            [FeatureKeys.RiskScores] =
                "Sub-feature of the AI Assistant with no runtime reader. Switch the AI Assistant off instead.",
            [FeatureKeys.HijriCalendar] =
                "Hijri dates are configured on the localisation settings "
                + "(PUT /api/tenant-admin/localization, hijriDatesEnabled), which is the setting the "
                + "runtime actually reads. This duplicate key reads nothing.",
            ["demo_seed_version"] =
                "Not a feature. The demo seeder stamps its version into this row; it is not a module.",
        };

    private static readonly Dictionary<string, ModuleDefinition> ByKey =
        All.ToDictionary(m => m.Key, StringComparer.Ordinal);

    /// <summary>Route prefixes sorted longest-first, so the most specific owner wins.</summary>
    private static readonly (string Prefix, ModuleDefinition Module)[] RouteIndex =
        All.SelectMany(m => m.RoutePrefixes.Select(p => (Prefix: p, Module: m)))
           .OrderByDescending(x => x.Prefix.Length)
           .ToArray();

    /// <summary>Frontend nav paths sorted longest-first.</summary>
    private static readonly (string Path, ModuleDefinition Module)[] NavIndex =
        All.SelectMany(m => m.NavPaths.Select(p => (Path: p, Module: m)))
           .OrderByDescending(x => x.Path.Length)
           .ToArray();

    public static ModuleDefinition? TryGet(string? key)
        => key is not null && ByKey.TryGetValue(key, out var m) ? m : null;

    /// <summary>
    /// The module that owns an API path, or null when no module claims it.
    /// Null means "not classified" — <c>ModuleCatalogCoverageTests</c> exists to keep that set empty
    /// for real controller routes, and the guard allows unclassified paths so a new controller is
    /// never accidentally unreachable in production before the test is run.
    /// </summary>
    public static ModuleDefinition? ResolveApiPath(string path)
    {
        foreach (var (prefix, module) in RouteIndex)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return module;
        }
        return null;
    }

    /// <summary>The module that owns a frontend route path, or null when unowned (always visible).</summary>
    public static ModuleDefinition? ResolveNavPath(string path)
    {
        foreach (var (navPath, module) in NavIndex)
        {
            if (path.Equals(navPath, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(navPath + "/", StringComparison.OrdinalIgnoreCase))
                return module;
        }
        return null;
    }

    /// <summary>The module that owns a notification category, or null when unowned (never suppressed).</summary>
    public static ModuleDefinition? ResolveNotificationCategory(string? category)
    {
        if (string.IsNullOrEmpty(category)) return null;
        foreach (var module in All)
        {
            if (module.NotificationCategories.Contains(category, StringComparer.Ordinal))
                return module;
        }
        return null;
    }

    /// <summary>
    /// Whether a tenant administrator may switch <paramref name="module"/> off.
    ///
    /// <para>Pure by design: the caller supplies the tenant's country and a predicate over the
    /// current flag state, so the decision is unit-testable without a database and cannot differ
    /// between the API that enforces it and the UI that explains it.</para>
    /// </summary>
    /// <param name="module">The module being switched.</param>
    /// <param name="countryCode">The tenant's ISO-3166 alpha-2 country, from tenant localisation.</param>
    /// <param name="isEnabled">Whether another module key is currently enabled.</param>
    public static ModuleDisableDecision CanDisable(
        ModuleDefinition module,
        string? countryCode,
        Func<string, bool> isEnabled)
    {
        switch (module.Lock)
        {
            case ModuleLock.Optional:
                return ModuleDisableDecision.Allowed;

            case ModuleLock.Core:
                return ModuleDisableDecision.Blocked(module.LockReason ?? CoreHrReason);

            case ModuleLock.Statutory:
                // The obligation only binds tenants in the countries that impose it.
                //
                // An UNKNOWN country counts as "applies". This guard is deliberately fail-closed,
                // unlike the module gate itself: the cost of being wrong is asymmetric. Wrongly
                // locking Saudization on for a British tenant is an annoyance they fix by setting
                // their country; wrongly letting a Saudi tenant switch off GOSI filing because
                // their localisation row had not been written yet is a statutory breach we caused.
                //
                // The stored value is normalised through CountryCodeStandard first. Tenant rows
                // hold a mix of alpha-2 and alpha-3 in practice — the seeded KSA tenant stores
                // "SAU", not "SA" — and a naive string comparison would have quietly decided that
                // a Saudi tenant has no Saudi obligations. An unrecognised code normalises to null
                // and is treated as unknown, which is locked.
                var normalisedCountry = CountryCodeStandard.NormalizeToIso2(countryCode);
                var countryApplies =
                    module.StatutoryCountries.Count == 0
                    || normalisedCountry is null
                    || module.StatutoryCountries.Contains(normalisedCountry, StringComparer.OrdinalIgnoreCase);
                if (!countryApplies) return ModuleDisableDecision.Allowed;

                // Some obligations only arise because the tenant runs the originating module here.
                if (module.ObligationArisesFrom is { } origin && !isEnabled(origin))
                    return ModuleDisableDecision.Allowed;

                return ModuleDisableDecision.Blocked(module.LockReason ?? StatutoryGosiReason);

            default:
                return ModuleDisableDecision.Blocked("Unknown lock class.");
        }
    }
}

/// <summary>Outcome of <see cref="ModuleCatalog.CanDisable"/>.</summary>
public readonly record struct ModuleDisableDecision(bool IsAllowed, string? Reason)
{
    public static ModuleDisableDecision Allowed => new(true, null);
    public static ModuleDisableDecision Blocked(string reason) => new(false, reason);
}
