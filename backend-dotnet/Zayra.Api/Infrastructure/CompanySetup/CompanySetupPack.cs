using Zayra.Api.Application.Common;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.CompanySetup;

/// <summary>
/// The country-pack values the create-company wizard writes. Each one comes from an existing source of truth
/// (the provisioning bundle's packs, <see cref="HomeJurisdiction"/>); nothing statutory is restated here.
/// </summary>
public static class CompanySetupPack
{
    private static readonly DayOfWeek[] WeekOrder =
        [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday];

    /// <summary>"Fri,Sat" — the comma form <c>GCCComplianceSetting.WeekendDays</c> stores, in week order.</summary>
    public static string FormatWeekend(IReadOnlySet<DayOfWeek> days) =>
        string.Join(",", WeekOrder.Where(days.Contains).Select(d => d.ToString()[..3]));

    /// <summary>The working-week range for a weekend, when the working days are contiguous ("Sun-Thu").</summary>
    public static string WorkWeekFor(IReadOnlySet<DayOfWeek> weekend)
    {
        var working = WeekOrder.Where(d => !weekend.Contains(d)).ToList();
        if (working.Count == 0) return string.Empty;
        // Rotate so the first working day after a rest day leads ("Sun" after Fri/Sat).
        var start = working.FirstOrDefault(d => weekend.Contains((DayOfWeek)(((int)d + 6) % 7)));
        var ordered = Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)start + i) % 7)).Where(d => !weekend.Contains(d)).ToList();
        return $"{ordered[0].ToString()[..3]}-{ordered[^1].ToString()[..3]}";
    }

    /// <summary>
    /// The tenant-default <see cref="GCCComplianceSetting"/> for a country with none (Decision 11), so end of
    /// service cannot hard-fail ("EOSB is not enabled …") for the first company in a new country.
    ///
    /// <para>EOSB rates are stored in <b>days per year</b> on a /30 day rate (see <c>KsaCalculators</c>): Saudi
    /// Art. 84's half a month / one month per year is 15 / 30. The contract's "0.5 / 1.0" is that same scale in
    /// months; writing 0.5 here would be read as half a DAY and refused as below the floor on every settlement.
    /// Other countries get 0 / 0, which every pack reads as "not configured — apply the statutory scale".
    /// The minimum-service gate is 0: KSA Art. 84 has none, and a configured one only adds a refusal notice.</para>
    /// </summary>
    public static GCCComplianceSetting TenantDefaultGccRow(Guid tenantId, string country)
    {
        var weekend = WorkWeekParser.ParseWeekendDays(TenantProvisioningBundle.PackWeekend(country))
                      ?? WorkWeekConfig.GccDefault.WeekendDays;
        var isSa = country == "SA";
        return new GCCComplianceSetting
        {
            TenantId = tenantId,
            CompanyId = null,
            CountryCode = country,
            EosbEnabled = true,
            EosbYears1To5Rate = isSa ? 15m : 0m,
            EosbYearsAbove5Rate = isSa ? 30m : 0m,
            EosbMinYears = 0,
            WeekendDays = FormatWeekend(weekend),
            WorkWeek = WorkWeekFor(weekend),
            IqamaRequired = isSa,
            EmiratesIdRequired = country == "AE",
        };
    }

    /// <summary>
    /// A company override of the working week. Every other field is copied from the tenant default so a sister
    /// company's lookup that happens to land on this row (the EOSB and WPS readers pick by country) reads the
    /// same EOSB and WPS settings it would have read before; only the weekend differs.
    /// </summary>
    public static GCCComplianceSetting CompanyOverrideRow(Guid tenantId, string country, GCCComplianceSetting? template, IReadOnlySet<DayOfWeek> weekend)
    {
        var t = template ?? TenantDefaultGccRow(tenantId, country);
        return new GCCComplianceSetting
        {
            TenantId = tenantId,
            CompanyId = null, // set to the new company at commit
            CountryCode = country,
            WpsEnabled = t.WpsEnabled,
            WpsAgentId = string.Empty, // the company's own MOL number is the authority (Decision 10)
            WpsMolCode = t.WpsMolCode,
            SifEnabled = t.SifEnabled,
            EosbEnabled = t.EosbEnabled,
            EosbYears1To5Rate = t.EosbYears1To5Rate,
            EosbYearsAbove5Rate = t.EosbYearsAbove5Rate,
            EosbMinYears = t.EosbMinYears,
            WeekendDays = FormatWeekend(weekend),
            WorkWeek = WorkWeekFor(weekend),
            VisaTrackingEnabled = t.VisaTrackingEnabled,
            VisaAlertDays = t.VisaAlertDays,
            IqamaRequired = t.IqamaRequired,
            IqamaAlertDays = t.IqamaAlertDays,
            EmiratesIdRequired = t.EmiratesIdRequired,
            RamadanHoursEnabled = t.RamadanHoursEnabled,
            RamadanReducedHoursPerDay = t.RamadanReducedHoursPerDay,
        };
    }
}
