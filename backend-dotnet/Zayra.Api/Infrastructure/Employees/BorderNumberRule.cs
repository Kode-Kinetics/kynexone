using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// KSA: how long, and for what, a Border Number (رقم الحدود) may stand in for the Iqama. The RULE always exists —
/// a border number is accepted in place of the Iqama at pay for a window after joining, and in the Saudi wage file
/// as the employee ID until the Iqama is issued. Only two NUMBERS vary, and they resolve in one order:
/// <list type="number">
/// <item>the company's own rate (<c>company_rate_policies</c>, key <see cref="GraceDaysRateKey"/> /
/// <see cref="UseAsWageFileIdRateKey"/>), else the tenant-wide row of the same key;</item>
/// <item>the platform default in <c>statutory_rules</c> (<see cref="GraceDaysRuleKey"/>, seeded 90 days for KSA);</item>
/// <item>the compiled fallback <see cref="DefaultGraceDays"/> / On — only when no row can be read at all.</item>
/// </list>
/// </summary>
public static class BorderNumberRule
{
    /// <summary>Platform default, effective-dated, in <c>statutory_rules</c> (KSA / mainland).</summary>
    public const string GraceDaysRuleKey = "ksa.border_number_grace_days";
    /// <summary>Per-company (or tenant-default) override, <c>company_rate_policies</c>.</summary>
    public const string GraceDaysRateKey = "payparameter.ksa_border_number_grace_days";
    /// <summary>Per-company: write the border number as the employee ID in the Saudi wage / bank file until the Iqama exists.</summary>
    public const string UseAsWageFileIdRateKey = "payparameter.ksa_border_number_as_wage_file_id";

    public const string On = "On";
    public const string Off = "Off";
    public static readonly string[] OnOff = { On, Off };

    /// <summary>Compiled fallback ONLY (no statutory row readable). The 90 that matters is the seeded row.</summary>
    public const int DefaultGraceDays = 90;
    public const int MinGraceDays = 0;
    public const int MaxGraceDays = 180;

    public const string BorderNumberUsedAsIdCode = "BORDER_NUMBER_USED_AS_ID";
    public const string BorderNumberNotAllowedAsIdCode = "BORDER_NUMBER_NOT_ALLOWED_AS_ID";

    public sealed record Settings(int GraceDays, bool UseAsWageFileId)
    {
        public static Settings Default { get; } = new(DefaultGraceDays, true);
    }

    public static int Clamp(decimal days) => (int)Math.Clamp(Math.Floor(days), MinGraceDays, MaxGraceDays);

    /// <summary>
    /// True while a border number may stand in for the Iqama on <paramref name="asOf"/>: up to and including
    /// <paramref name="graceDays"/> days after the joining date. 0 days means the Iqama is needed from the first
    /// payroll — the window is never open. An unknown joining date cannot open it (fail closed).
    /// </summary>
    public static bool IsWithinWindow(DateTime joiningDate, int graceDays, DateOnly asOf)
        => graceDays > 0
           && joiningDate != default
           && asOf <= DateOnly.FromDateTime(joiningDate).AddDays(graceDays);

    /// <summary>The company's effective settings on <paramref name="onDate"/> (see the type summary for the order).</summary>
    public static async Task<Settings> ResolveAsync(
        ICompanyRatePolicyResolver rates, IStatutoryRuleReader rules,
        Guid tenantId, Guid? companyId, DateOnly onDate, CancellationToken ct)
    {
        int days;
        var rate = await rates.ResolveAsync(tenantId, companyId, GraceDaysRateKey, onDate, ct);
        if (rate is not null && TryParseDays(rate.RateValue, out var rateDays)) days = rateDays;
        else
        {
            var platform = await rules.GetDecimalAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland, GraceDaysRuleKey, onDate, tenantId, ct);
            days = platform is { } p ? Clamp(p) : DefaultGraceDays;
        }

        var useRow = await rates.ResolveAsync(tenantId, companyId, UseAsWageFileIdRateKey, onDate, ct);
        var useAsId = !string.Equals(useRow?.RateValue?.Trim(), Off, StringComparison.OrdinalIgnoreCase);
        return new Settings(days, useAsId);
    }

    private static bool TryParseDays(string? raw, out int days)
    {
        days = 0;
        if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        days = Clamp(v);
        return true;
    }

    /// <summary>An employee inside the border-number window who has no Iqama: the Saudi wage / bank file needs an ID
    /// for them. <see cref="Allowed"/> is the company's "use the Border Number as the employee ID" setting.</summary>
    public sealed record WageFileBorderId(string BorderNumber, bool Allowed, int GraceDays);

    public static bool IsKsa(string? countryCode)
        => string.Equals(CountryCodeStandard.NormalizeToIso2(countryCode) ?? countryCode, "SA", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which employees of a wage / bank file depend on the Border Number for their ID: KSA, no Iqama, a border
    /// number held, and <paramref name="periodEnd"/> still inside the company's window. The window is measured at the
    /// PERIOD END, not today. Outside the window an employee is absent from the result — the Iqama is required, as
    /// before. Callers decide what "no primary ID" means for their file (IdNumber / Iqama).
    /// </summary>
    public static async Task<Dictionary<int, WageFileBorderId>> ResolveWageFileIdsAsync(
        ZayraDbContext db, Guid tenantId, IEnumerable<Employee> employees, DateOnly periodEnd, CancellationToken ct,
        IStatutoryRuleReader? rules = null)
    {
        var result = new Dictionary<int, WageFileBorderId>();
        var candidates = employees
            .Where(e => IsKsa(e.CountryCode) && string.IsNullOrWhiteSpace(e.IqamaNumber) && !string.IsNullOrWhiteSpace(e.BorderNumber))
            .ToList();
        if (candidates.Count == 0) return result;
        var rates = new CompanyRatePolicyResolver(db);
        rules ??= new StatutoryRuleReader(db);
        var settingsByCompany = new Dictionary<Guid, Settings>();
        foreach (var e in candidates)
        {
            var companyKey = e.CompanyId ?? Guid.Empty;
            if (!settingsByCompany.TryGetValue(companyKey, out var settings))
                settingsByCompany[companyKey] = settings = await ResolveAsync(rates, rules, tenantId, e.CompanyId, periodEnd, ct);
            if (!IsWithinWindow(e.JoiningDate, settings.GraceDays, periodEnd)) continue;
            result[e.Id] = new WageFileBorderId(e.BorderNumber.Trim(), settings.UseAsWageFileId, settings.GraceDays);
        }
        return result;
    }

    /// <summary>The last day of the payroll period a run is for.</summary>
    public static DateOnly PeriodEnd(int year, int month) => new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
}
