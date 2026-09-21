using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Modules;

/// <summary>
/// The module key vocabulary.
///
/// <para>Keys that already existed in <see cref="FeatureKeys"/> are re-exported here by reference,
/// never re-spelled: a tenant's stored <c>tenant_feature_flags</c> rows are keyed by these strings,
/// so a typo would silently orphan live configuration. The compiler now enforces the link.</para>
///
/// <para>The remaining keys name modules that previously had no flag at all because they were
/// hard-coded into <c>FeatureFlagGuardFilter.AlwaysAllowedPrefixes</c>. They are declared here so
/// the catalog can state <i>why</i> they cannot be switched off, rather than leaving the answer
/// implicit in a string array.</para>
/// </summary>
public static class ModuleKeys
{
    // ── Pre-existing keys (stored flags depend on these exact strings) ───────────
    public const string Recruitment             = FeatureKeys.Recruitment;
    public const string Performance             = FeatureKeys.Performance;
    public const string Compliance              = FeatureKeys.Compliance;
    public const string AiAssistant             = FeatureKeys.AiAssistant;
    public const string Finance                 = FeatureKeys.Finance;
    public const string Payroll                 = FeatureKeys.Payroll;
    public const string Shifts                  = FeatureKeys.Shifts;
    public const string Overtime                = FeatureKeys.Overtime;
    public const string MobileApp               = FeatureKeys.MobileApp;
    public const string PayslipTemplateDesigner = FeatureKeys.PayslipTemplateDesigner;

    /// <summary>
    /// Saudization / Qiwa reporting. Keeps the historical <c>qiwa_integration</c> spelling so
    /// tenants that already have it switched off keep that state.
    /// </summary>
    public const string Saudization = FeatureKeys.QiwaIntegration;

    // ── Keys introduced with the module catalog ─────────────────────────────────
    //
    // "core_hr", "leave_attendance" and "documents" reuse the spellings already in
    // PricingModuleKeys so the commercial catalogue and the runtime catalogue agree.
    public const string CoreHr          = PricingModuleKeys.CoreHr;          // "core_hr"
    public const string LeaveAttendance = PricingModuleKeys.LeaveAttendance; // "leave_attendance"
    public const string Documents       = PricingModuleKeys.Documents;       // "documents"

    public const string AccessControl = "access_control";
    public const string Audit         = "audit";
    public const string Approvals     = "approvals";
    public const string SelfService   = "self_service";
    public const string Offboarding   = "offboarding";
    public const string Reporting     = "reporting";
    public const string Dashboard     = "dashboard";
    public const string Notifications = "notifications";
    public const string Configuration = "configuration";

    /// <summary>
    /// GOSI gets its own key. It was previously gated by <c>qiwa_integration</c>, which meant a
    /// tenant switching off Qiwa also switched off social-insurance filing — a statutory
    /// obligation disabled as a side effect of an unrelated preference.
    /// </summary>
    public const string Gosi = "gosi";

    public const string Timesheets = "timesheets";
    public const string Benefits   = "benefits";
    public const string HrLetters  = "hr_letters";
}
