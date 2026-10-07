using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// GOSI contribution rates and the contributory-wage bounds are STATUTORY: GOSI publishes them, the
/// platform holds them as effective-dated <c>statutory_rules</c> rows (TenantId = null), and every
/// GOSI figure — payslip, filing, preview, readiness report — reads the platform row only
/// (<c>KsaDeductionCalculator</c>, <c>KsaGosiCohortSchedule</c> and <c>KsaGosiWageBounds</c> all pass
/// <c>tenantId: null</c>).
///
/// <para><b>Why tenant writes are refused.</b> Until this guard, <c>/api/statutory-rules</c>, the
/// company statutory-override maker-checker, the setup assistant and the country-rules admin all
/// ACCEPTED a tenant value for these keys, audited it, and then nothing ever read it. An admin who
/// "changed the GOSI rate" saw a saved, approved row and a payslip that ignored it. A company cannot
/// set its own GOSI rate, so the honest answer is a refusal at write time, with a code.</para>
///
/// <para><b>Scope.</b> Every <c>gosi.*_rate</c> key (Saudi annuities, SANED, occupational hazards, the
/// per-home-state GCC keys) and the contributory-wage ceiling/floor. NOT refused:
/// <c>gosi.retirement_age_years</c>, a validator threshold read tenant-aware and marked [COUNSEL].
/// No GOSI value is modelled as company-specific: occupational hazards is a single 2% employer rate
/// in the seeded schedule and nothing in the product models an establishment hazard class.</para>
///
/// <para>Existing tenant rows are NOT deleted (they are an audit record of what someone tried to
/// do). <see cref="Infrastructure.Compliance.GosiReadinessReportService"/> warns about them, and the
/// runbook carries a read-only query that lists them.</para>
/// </summary>
public static class GosiStatutoryValues
{
    /// <summary>Coded reason for a refused tenant-level write of a GOSI statutory value.</summary>
    public const string RefusalCode = "GOSI_RATE_IS_STATUTORY";

    /// <summary>Readiness-report warning code for tenant rows that were saved but are never applied.</summary>
    public const string IgnoredOverrideWarningCode = "GOSI_TENANT_OVERRIDE_NEVER_APPLIED";

    /// <summary>True when <paramref name="ruleKey"/> is a GOSI rate or contributory-wage bound.</summary>
    public static bool IsStatutory(string? ruleKey)
    {
        if (string.IsNullOrWhiteSpace(ruleKey)) return false;
        var k = ruleKey.Trim().ToLowerInvariant();
        if (!k.StartsWith("gosi.", StringComparison.Ordinal)) return false;
        return k.EndsWith("_rate", StringComparison.Ordinal)
            || k.StartsWith("gosi.covered_wage_", StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal body for a tenant-level write of <paramref name="ruleKey"/>, or null when the key is
    /// not a GOSI statutory value. Returned with HTTP 422.
    /// </summary>
    public static object? TenantWriteRefusal(string? ruleKey)
    {
        if (!IsStatutory(ruleKey)) return null;
        var key = ruleKey!.Trim();
        return new
        {
            code = RefusalCode,
            ruleKey = key,
            message = $"'{key}' is a GOSI statutory value. GOSI contribution rates and the contributory-wage "
                    + "ceiling follow the rates GOSI publishes and are maintained for every company by effective "
                    + "date; a company cannot set its own. Nothing has been saved.",
            messageAr = $"'{key}' قيمة نظامية للمؤسسة العامة للتأمينات الاجتماعية. نسب الاشتراك والحد الأعلى "
                      + "للأجر الخاضع للاشتراك تتبع ما تنشره المؤسسة وتُطبَّق على جميع الشركات حسب تاريخ السريان، "
                      + "ولا يمكن للشركة تحديدها. لم يتم حفظ أي شيء.",
        };
    }

    /// <summary>
    /// GOSI rate and ceiling keys this tenant saved at tenant level — statutory_rules overrides, company
    /// statutory overrides (not deleted) and country payroll rules — none of which payroll applies.
    /// Read-only; the rows are kept as an audit record. Same predicate as the runbook query.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindIgnoredTenantOverridesAsync(
        ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var keys = new List<string>();
        keys.AddRange(await db.StatutoryRules.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.RuleKey.StartsWith("gosi."))
            .Select(r => r.RuleKey).ToListAsync(ct));
        keys.AddRange(await db.CompanyStatutoryOverrides.AsNoTracking()
            .Where(r => r.TenantId == tenantId && !r.IsDeleted && r.RuleKey.StartsWith("gosi."))
            .Select(r => r.RuleKey).ToListAsync(ct));
        keys.AddRange(await db.CountryPayrollRules.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.RuleKey.StartsWith("gosi."))
            .Select(r => r.RuleKey).ToListAsync(ct));
        return keys.Where(IsStatutory).ToList();
    }

    /// <summary>The readiness-report warning text for a tenant that holds never-applied GOSI rows.</summary>
    public static string IgnoredOverrideWarning(IEnumerable<string> keys) =>
        $"GOSI rate settings saved for this company ({string.Join(", ", keys.Distinct().OrderBy(k => k))}) were saved "
      + "but never applied; payroll uses the GOSI-published rate. They can be removed.";
}
