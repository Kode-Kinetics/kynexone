using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Contracts;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Contracts;

/// <summary>
/// Every renewal rule one tenant runs on, read in ONE query from <c>statutory_rules</c> (the 12
/// <see cref="RenewalRuleKeys"/>): a tenant row overrides the platform row, the newest effective row wins — the same
/// precedence as <c>StatutoryRuleReader</c>. A key with no row, or a value that does not parse or is out of range,
/// falls back to the seeded platform default (<see cref="RenewalDeadlineRules"/> / <see cref="Art55Input"/>) and is
/// listed in <see cref="FellBack"/>, so a caller can say so instead of silently using it. Release A slice R4.
/// </summary>
public sealed record RenewalRuleSet(
    RenewalDeadlineRules Deadlines,
    int OriginalTermJoiningToleranceDays,
    Art55Reading Art55Reading,
    int Art55MaxConsecutiveRenewals,
    int Art55MaxTotalYears,
    DateOnly UnifiedContractFrom,
    bool AsIsRequiresQiwaStep,
    Guid? QiwaRuleId,
    IReadOnlyList<string> FellBack)
{
    /// <summary>
    /// Days a term may start AFTER the employee's joining date and still count as the original contract (R4 review P1-2).
    /// Default 0: the first term is the original only when it starts on the joining date. Effective-dated like every rule;
    /// a tenant row may widen it (0–31). Seeding the platform row belongs to <c>StatutoryRuleSeeder</c> (R0); an absent
    /// row reads as 0.
    /// </summary>
    public const string OriginalTermJoiningToleranceKey = "contracts.original_term_joining_tolerance_days";

    /// <summary>The seeded platform values, used when nothing is on file.</summary>
    public static RenewalRuleSet Defaults { get; } = new(
        new RenewalDeadlineRules(), 0, Art55Reading.Conservative, 3, 4, new DateOnly(2025, 10, 6), true, null, []);

    /// <summary>Loads the set in force on <paramref name="asOf"/> for <paramref name="tenantId"/>.</summary>
    public static async Task<RenewalRuleSet> LoadAsync(ZayraDbContext db, Guid tenantId, DateOnly asOf, CancellationToken ct)
    {
        var cutoff = asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var keys = RenewalRuleKeys.All.Append(OriginalTermJoiningToleranceKey).ToArray();
        // Platform defaults have TenantId NULL, which the tenant filter hides: read this tenant's overrides and the
        // platform rows through the sanctioned bypass, each pinned to exactly one owner — never another tenant's rows.
        var rows = new List<RuleRow>();
        foreach (Guid? owner in new Guid?[] { tenantId, null })
            rows.AddRange(await ScopedBypass.NullableTenantWide(db.StatutoryRules, owner,
                    "Renewal rules: this tenant's overrides and the platform defaults, each owner pinned.")
                .AsNoTracking()
                .Where(r => r.CountryCode == CountryCodes.Saudi && r.Jurisdiction == Jurisdictions.KsaMainland
                            && keys.Contains(r.RuleKey)
                            && r.EffectiveFrom <= cutoff && (r.EffectiveTo == null || r.EffectiveTo > cutoff))
                .Select(r => new RuleRow(r.Id, r.RuleKey, r.RuleValue, r.TenantId, r.EffectiveFrom))
                .ToListAsync(ct));
        var winner = rows
            .GroupBy(r => r.RuleKey)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.TenantId != null).ThenByDescending(r => r.EffectiveFrom).First());

        var fellBack = new List<string>();
        var d = Defaults;
        int Int(string key, int fallback, int min)
        {
            if (winner.TryGetValue(key, out var row)
                && int.TryParse(row.RuleValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min)
                return v;
            fellBack.Add(key);
            return fallback;
        }
        bool Bool(string key, bool fallback)
        {
            if (winner.TryGetValue(key, out var row) && bool.TryParse(row.RuleValue, out var v)) return v;
            fellBack.Add(key);
            return fallback;
        }

        // Every lead is at least one day (R0 round 2: tenant overrides are validated on save; this is the read-side guard).
        var deadlines = new RenewalDeadlineRules(
            RenewalLeadDays: Int(RenewalRuleKeys.RenewalLeadDays, d.Deadlines.RenewalLeadDays, 1),
            OfferLeadDays: Int(RenewalRuleKeys.OfferLeadDays, d.Deadlines.OfferLeadDays, 1),
            QiwaSubmitLeadDays: Int(RenewalRuleKeys.QiwaSubmitLeadDays, d.Deadlines.QiwaSubmitLeadDays, 1),
            QiwaGateLeadDays: Int(RenewalRuleKeys.QiwaGateLeadDays, d.Deadlines.QiwaGateLeadDays, 1),
            DefaultNonRenewalNoticeDays: Int(RenewalRuleKeys.DefaultNonRenewalNoticeDays, d.Deadlines.DefaultNonRenewalNoticeDays, 1),
            QiwaResponseDays: Int(RenewalRuleKeys.QiwaContractResponseDays, d.Deadlines.QiwaResponseDays, 1),
            OpenMarginDays: Int(RenewalRuleKeys.OpenMarginDays, d.Deadlines.OpenMarginDays, 1));

        Art55Reading reading;
        if (winner.TryGetValue(RenewalRuleKeys.Art55Reading, out var readingRow)
            && Enum.TryParse<Art55Reading>(readingRow.RuleValue, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            reading = parsed;
        else
        {
            fellBack.Add(RenewalRuleKeys.Art55Reading);
            reading = d.Art55Reading;
        }

        DateOnly unified;
        if (winner.TryGetValue(RenewalRuleKeys.UnifiedContractFrom, out var unifiedRow)
            && DateOnly.TryParseExact(unifiedRow.RuleValue, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u))
            unified = u;
        else
        {
            fellBack.Add(RenewalRuleKeys.UnifiedContractFrom);
            unified = d.UnifiedContractFrom;
        }

        // Absent row = 0 days (the seeded default); it is not a "fall back" worth reporting.
        var tolerance = winner.TryGetValue(OriginalTermJoiningToleranceKey, out var tolRow)
                        && int.TryParse(tolRow.RuleValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tol) && tol is >= 0 and <= 31
            ? tol
            : 0;
        return new RenewalRuleSet(
            deadlines,
            tolerance,
            reading,
            Int(RenewalRuleKeys.Art55MaxConsecutiveRenewals, d.Art55MaxConsecutiveRenewals, 1),
            Int(RenewalRuleKeys.Art55MaxTotalYears, d.Art55MaxTotalYears, 1),
            unified,
            Bool(RenewalRuleKeys.AsIsRequiresQiwaStep, d.AsIsRequiresQiwaStep),
            winner.TryGetValue(RenewalRuleKeys.QiwaContractResponseDays, out var qiwaRow) ? qiwaRow.Id : null,
            fellBack);
    }

    private sealed record RuleRow(Guid Id, string RuleKey, string RuleValue, Guid? TenantId, DateTime EffectiveFrom);
}
