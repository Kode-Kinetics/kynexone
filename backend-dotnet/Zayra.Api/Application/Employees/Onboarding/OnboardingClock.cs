using Zayra.Api.Application.Common;

namespace Zayra.Api.Application.Employees.Onboarding;

/// <summary>"Today" for a hire rule is the company's calendar day: Saudi companies always use Asia/Riyadh, never the UTC date.</summary>
public static class OnboardingClock
{
    public static TimeZoneInfo ZoneFor(string? companyCountry)
    {
        if ((CountryCodeStandard.NormalizeToIso2(companyCountry) ?? companyCountry?.Trim().ToUpperInvariant()) != "SA") return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh"); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        { return TimeZoneInfo.CreateCustomTimeZone("Riyadh", TimeSpan.FromHours(3), "Riyadh", "Riyadh"); }
    }

    public static DateOnly TodayIn(TimeZoneInfo zone, DateTime? utcNow = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow ?? DateTime.UtcNow, zone));
}
