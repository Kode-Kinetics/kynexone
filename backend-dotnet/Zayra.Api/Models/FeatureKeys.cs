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
    /// unlike every module key, an absent row means OFF. Switched on by the organisation's administrator
    /// (PUT /api/tenant-admin/features/release_a) when the licence grants it. See OptInFeatures.
    /// </summary>
    public const string ReleaseA = "release_a";
    /// <summary>
    /// Selfie attendance (a photo with each punch, single-use evidence id, consent per policy version). OPT-IN: an
    /// absent row means OFF. The organisation's administrator switches it on when the licence grants it, and the
    /// feature endpoint refuses unless the flag's ConfigJson records the DPIA sign-off and the KSA data-residency
    /// confirmation (SelfieAttendanceConfig). See OptInFeatures.
    /// </summary>
    public const string SelfieAttendance = "selfie_attendance";
    /// <summary>
    /// Server-side geofence on every employee self-punch, against the site's Location row. OPT-IN and independent of
    /// selfies (not biometric, so no DPIA gate); settings in ConfigJson (PunchGeofenceConfig). Switched by the
    /// organisation's administrator when licensed.
    /// </summary>
    public const string PunchGeofence = "attendance_geofence";
    /// <summary>
    /// CMP — merit planning, pay benchmarking and pay equity. OPT-IN: an absent row means OFF; switched on per tenant by the
    /// organisation's administrator (PUT /api/tenant-admin/features/compensation) when licensed. See OptInFeatures.
    /// </summary>
    public const string Compensation = "compensation";
    /// <summary>
    /// Engagement surveys (ENG): templates, anonymous/confidential/identified surveys, suppressed results, actions.
    /// OPT-IN: an absent row means OFF. Switched on by the organisation's administrator when licensed. See OptInFeatures.
    /// </summary>
    public const string Engagement = "engagement";
    /// <summary>
    /// Learning management (LRN: courses, paths, assignments, assessments, certificates, cmi5). OPT-IN: an absent row
    /// means OFF. Switched on by the organisation's administrator (PUT /api/tenant-admin/features/learning) when licensed while it
    /// is being rolled out. See OptInFeatures.
    /// </summary>
    public const string Learning = "learning";
    /// <summary>
    /// Succession and career development (SUC: critical positions, talent pools, nominations, talent review and 9-box,
    /// slates, development plans). OPT-IN: an absent row means OFF. Switched on by the organisation's
    /// administrator (PUT /api/tenant-admin/features/succession) when licensed. See OptInFeatures.
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
    /// Switched on only when licensed, by the organisation's administrator. Even when on, policy text is never sent to a model
    /// (PolicyContentModelGate). See OptInFeatures.
    /// </summary>
    public const string PolicyIntake = "policy_intake";
}
