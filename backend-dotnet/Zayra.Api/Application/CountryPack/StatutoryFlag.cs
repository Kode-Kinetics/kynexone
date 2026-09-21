namespace Zayra.Api.Application.CountryPack;

/// <summary>
/// S1 — reads a BOOLEAN statutory rule.
///
/// <para>Every [CONF] and [COUNSEL] switch this stream introduces is an effective-dated
/// <c>StatutoryRule</c> row rather than a compiled literal, because GCC rules move — sometimes
/// mid-year and sometimes retroactively — and a literal cannot be back-dated. The reader interface
/// only exposes decimal and string, so this normalises the handful of spellings a seeder, a tenant
/// admin or a test stub might legitimately write: <c>true/false</c>, <c>yes/no</c>, <c>1/0</c>.</para>
///
/// <para>An UNPARSEABLE value falls back to the caller's statutory default rather than silently
/// reading as false — "someone typed nonsense into the rule table" must never quietly remove an
/// entitlement.</para>
/// </summary>
public static class StatutoryFlag
{
    public static async Task<bool> ReadAsync(
        IStatutoryRuleReader rules,
        string countryCode,
        string jurisdiction,
        string ruleKey,
        DateOnly effectiveDate,
        bool statutoryDefault,
        CancellationToken ct = default)
    {
        var raw = await rules.GetStringAsync(countryCode, jurisdiction, ruleKey, effectiveDate, null, ct);
        if (!string.IsNullOrWhiteSpace(raw) && TryParse(raw, out var fromString)) return fromString;

        var num = await rules.GetDecimalAsync(countryCode, jurisdiction, ruleKey, effectiveDate, null, ct);
        if (num.HasValue) return num.Value != 0m;

        return statutoryDefault;
    }

    private static bool TryParse(string raw, out bool value)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "true": case "yes": case "y": case "1": value = true; return true;
            case "false": case "no": case "n": case "0": value = false; return true;
            default: value = false; return false;
        }
    }
}
