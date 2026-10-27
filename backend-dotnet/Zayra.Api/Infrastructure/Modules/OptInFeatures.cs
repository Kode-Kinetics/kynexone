using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Modules;

/// <summary>
/// Features that are OFF unless a tenant has an explicit <c>tenant_feature_flags</c> row with IsEnabled = true —
/// the reverse of every <see cref="ModuleCatalog"/> module, where an absent row means on.
///
/// <para><b>Why a separate list.</b> A module defaults on so a forgotten row never locks a paying tenant out of
/// what they bought. A feature still being rolled out must default the other way: a live tenant (Evostel) must
/// not see Release A screens or reach its APIs until the platform team switches it on. Keys here are refused by
/// the tenant-facing module API (they are in <see cref="ModuleCatalog.NonModuleKeys"/>), so only the platform
/// flag endpoint can enable them.</para>
///
/// <para><b>Enforcement.</b> <see cref="TenantModuleService"/> reports an opt-in key as disabled unless enabled,
/// so the frontend's <c>isFeatureEnabled</c> and every server check read one answer.
/// <c>FeatureFlagGuardFilter</c> refuses the API prefixes below with 403 <c>feature_not_enabled</c>; an endpoint
/// outside them (e.g. a deductions statement under /api/payroll) carries <see cref="Filters.RequireOptInFeatureAttribute"/>.</para>
/// </summary>
public static class OptInFeatures
{
    /// <summary>API prefixes each opt-in feature owns. Longest prefix wins.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> RoutePrefixes =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [FeatureKeys.HireWizardV2] = [],
            [FeatureKeys.ReleaseA] =
            [
                "/api/entitlements",       // R1 matrix, R2 HR package view
                "/api/contracts",          // R4–R6 renewal case and chain
                "/api/ess/package",        // R2 employee package
                "/api/ess/deductions",     // R3 employee deductions
                "/api/ess/renewal-offer",  // R6 employee acceptance
            ],
            // Selfie attendance: only the upload is a route of its own. Consent withdrawal and the ESS read stay
            // reachable when it is off (withdrawing is always possible); punches check the flag in code. HR's review
            // view (GET /api/attendance/evidence/{rawEventId}/selfie) stays reachable too: a stored selfie backs a pay
            // record for its whole retention window, whatever the flag says now.
            [FeatureKeys.SelfieAttendance] =
            [
                "/api/attendance/evidence/selfie",
            ],
            // The geofence owns no route: it is enforced inside every self-punch (AttendanceVerificationService).
            [FeatureKeys.PunchGeofence] = [],
            // CMP: merit cycles, worksheets, benchmarks and pay equity. The awards it schedules are ordinary employee change
            // requests, so switching the flag off never strands a scheduled award: the effective-date job still applies it.
            [FeatureKeys.Compensation] =
            [
                "/api/compensation/merit",
            ],
            // Engagement surveys: HR's side and the respondent's Self-Service side. The scheduled results report
            // (engagement.results) checks the flag in its own arm, as it runs under /api/reports.
            [FeatureKeys.Engagement] =
            [
                "/api/engagement",
                "/api/ess/surveys",
            ],
            // Learning management. The cmi5 launch endpoints under /api/learning/cmi5 and /api/learning/xapi carry no
            // JWT (the AU authenticates with its session token), so this guard cannot see their tenant: LearningCmi5Service
            // checks the flag itself against the session's tenant.
            [FeatureKeys.Learning] =
            [
                "/api/learning",
                "/api/ess/learning",
            ],
            // Succession and career development. The employee's own development plan is under /api/ess, classified by
            // Self-Service, and closed with the rest of the module while the flag is off.
            [FeatureKeys.Succession] =
            [
                "/api/succession",
                "/api/ess/development-plan",
            ],
            // Candidate visibility owns no route: the ESS development-plan read checks it (default OFF).
            [FeatureKeys.SuccessionCandidateVisibility] = [],
            // Policy intake (owner decision 2026-10-20: only manual company setup is enabled). Every policy upload,
            // parse, local preparation, AI extraction and review (/api/setup-assistant/policy/...), and the policy
            // library with its Q&A for HR and employees (/api/ai/policy/...). Even with the flag on, policy text never
            // reaches a model (PolicyContentModelGate).
            [FeatureKeys.PolicyIntake] =
            [
                "/api/setup-assistant/policy",
                "/api/ai/policy",
            ],
        };

    public static IReadOnlyCollection<string> Keys => RoutePrefixes.Keys.ToArray();

    public static bool IsOptIn(string? key) => key is not null && RoutePrefixes.ContainsKey(key);

    private static readonly (string Prefix, string Key)[] Index =
        RoutePrefixes.SelectMany(kv => kv.Value.Select(p => (Prefix: p, kv.Key)))
            .OrderByDescending(x => x.Prefix.Length)
            .ToArray();

    /// <summary>The opt-in feature that owns an API path, or NULL when none does.</summary>
    public static string? ResolveApiPath(string path)
    {
        foreach (var (prefix, key) in Index)
        {
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return null;
    }
}
