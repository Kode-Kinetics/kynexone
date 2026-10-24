using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ── Feature key constants ─────────────────────────────────────────────────────

public static class FeatureKeys
{
    public const string Recruitment = "recruitment";
    public const string Performance = "performance";
    public const string Compliance  = "compliance";
    public const string AiAssistant = "ai_assistant";
    public const string Finance     = "finance";
    public const string Payroll     = "payroll";
    public const string Shifts      = "shifts";
    public const string Overtime    = "overtime";
    public const string MobileApp        = "mobile_app";
    public const string WpsExport        = "wps_export";
    public const string QiwaIntegration  = "qiwa_integration";
    public const string EosbCalc            = "eosb_calc";
    public const string ResumeScreening     = "resume_screening";
    public const string PayrollAiValidation = "payroll_ai_validation";
    public const string RiskScores              = "risk_scores";
    public const string HijriCalendar           = "hijri_calendar";
    public const string PayslipTemplateDesigner = "payslip_template_designer";
    /// <summary>
    /// Release A (grade entitlements, contract-year package, contract renewals, deductions statement). OPT-IN:
    /// unlike every module key, an absent row means OFF. Switched on per tenant by the platform team
    /// (PUT /api/platform/tenants/{id}/features/release_a), never from Tenant Admin. See OptInFeatures.
    /// </summary>
    public const string ReleaseA = "release_a";
    /// <summary>
    /// Selfie attendance (a photo with each punch, single-use evidence id, consent per policy version). OPT-IN: an
    /// absent row means OFF. Only the platform team can switch it on, and the platform endpoint refuses unless the
    /// flag's ConfigJson records the owner's DPIA sign-off and the KSA data-residency confirmation
    /// (SelfieAttendanceConfig). Tenant Admin never can. See OptInFeatures.
    /// </summary>
    public const string SelfieAttendance = "selfie_attendance";
    /// <summary>
    /// Server-side geofence on every employee self-punch, against the site's Location row. OPT-IN and independent of
    /// selfies (not biometric, so no DPIA gate); settings in ConfigJson (PunchGeofenceConfig). Platform-switched.
    /// </summary>
    public const string PunchGeofence = "attendance_geofence";
    /// <summary>
    /// CMP — merit planning, pay benchmarking and pay equity. OPT-IN: an absent row means OFF; switched on per tenant by the
    /// platform team (PUT /api/platform/tenants/{id}/features/compensation). See OptInFeatures.
    /// </summary>
    public const string Compensation = "compensation";
    /// <summary>
    /// Engagement surveys (ENG): templates, anonymous/confidential/identified surveys, suppressed results, actions.
    /// OPT-IN: an absent row means OFF. Switched on per tenant by the platform team. See OptInFeatures.
    /// </summary>
    public const string Engagement = "engagement";
    /// <summary>
    /// Learning management (LRN: courses, paths, assignments, assessments, certificates, cmi5). OPT-IN: an absent row
    /// means OFF. Switched on per tenant by the platform team (PUT /api/platform/tenants/{id}/features/learning) while it
    /// is being rolled out. See OptInFeatures.
    /// </summary>
    public const string Learning = "learning";
    /// <summary>
    /// Succession and career development (SUC: critical positions, talent pools, nominations, talent review and 9-box,
    /// slates, development plans). OPT-IN: an absent row means OFF. Switched on per tenant by the platform team
    /// (PUT /api/platform/tenants/{id}/features/succession). See OptInFeatures.
    /// </summary>
    public const string Succession = "succession";
    /// <summary>
    /// SUC: lets an employee see, in self-service, the talent pools and nominations they belong to (never other
    /// candidates, never a slate or a 9-box placement). OPT-IN, default OFF; owns no route — EssDevelopmentPlanController
    /// reads it. See OptInFeatures.
    /// </summary>
    public const string SuccessionCandidateVisibility = "succession_candidate_visibility";
    /// <summary>
    /// Policy intake: uploading company policy documents, parsing them (the local preparation rules included),
    /// extracting setup values from them, recording reviews against them, the policy library and policy Q&amp;A.
    /// OPT-IN, default OFF for every tenant (owner decision 2026-10-20: only manual company setup is enabled).
    /// Switched on per tenant only by the platform team. Even when on, policy text is never sent to a model
    /// (PolicyContentModelGate). See OptInFeatures.
    /// </summary>
    public const string PolicyIntake = "policy_intake";
}
