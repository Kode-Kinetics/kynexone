using Zayra.Api.Application.CountryPack;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compliance;

// ─────────────────────────────────────────────────────────────────────────────
//  NITAQAT MUTAWAR (نطاقات المطور) — HOW MHRSD ACTUALLY BANDS AN ESTABLISHMENT.
//
//  ── WHAT WAS WRONG ──────────────────────────────────────────────────────────
//  The model in Nitaqat.cs assumes the band floor is a cell in a published
//  (economic activity × establishment size tier) percentage grid. That was true
//  under the 2017 "Balanced Nitaqat" regime. It has not been true since
//  1 December 2021.
//
//  MHRSD Ministerial Decision 182495 replaced the grid with a per-activity
//  LOGARITHMIC CURVE and explicitly abolished the size bands. From the Ministry's
//  own English procedural guideline, verbatim:
//
//      "Removing fixed size bands and moving to a smooth relationship between
//       worker count and required Saudization"
//      "Cancel the use of Saudization Rates according to fixed size bands and
//       apply a calculation based on the number of employees in the entity using
//       fixed values for its economic activity"
//      "y = m * ln(x) + c"
//        • y = the minimum Saudization of the range
//        • m = the gradient of the curve, differs by economic activity
//        • c = the y-axis intercept, differs by economic activity AND YEAR
//              ("The third-year value will be used in the third year and beyond")
//        • x = the total workforce in the entity
//
//  and the band boundaries, verbatim:
//      Red          n <  Y_LowGreen
//      Low Green    Y_LowGreen    <= n < Y_MediumGreen
//      Medium Green Y_MediumGreen <= n < Y_HighGreen
//      High Green   Y_HighGreen   <= n < Y_Platinum
//      Platinum     Y_Platinum    <= n
//
//  SOURCE, VERIFIED: MHRSD, "Nitaqat Program Procedural Guideline" (official
//  English edition of Ministerial Decision 182495 of 11/10/1442 AH, in force
//  1 December 2021), 22 pages, at
//    https://www.hrsd.gov.sa/sites/default/files/2023-06/E20210523.pdf
//  Retrieved, text-extracted and read 2026-09-20. The quotations above and the
//  worked example below were taken from that file, not from a secondary source.
//
//  ── WHY THIS DOES NOT DELETE THE GRID ───────────────────────────────────────
//  The stored (activity × tier × band) grid remains, for three reasons. It is the
//  correct model for any period before 2021-12-01, and a closed period must stay
//  explicable. It is the manual override a customer uses when they have a figure
//  from their own Qiwa screen and no curve. And ~30 existing tests pin its
//  arithmetic, which is sound. The calculator PREFERS a curve when one is
//  effective and falls back to the grid otherwise, so nothing that works today
//  stops working.
//
//  ── WHERE THE CONSTANTS LIVE ────────────────────────────────────────────────
//  In StatutoryRule, which is exactly what it is for: an effective-dated,
//  tenant-overridable, source-noted scalar store. Nitaqat.cs argued (correctly)
//  that the GRID could not live there — a 3,000-cell matrix cannot be queried for
//  its sibling rows through a key-value table. A curve is different: eight scalars
//  per activity, each looked up directly by key. No new table, and therefore no
//  migration and no model-snapshot collision with the other streams in flight.
//
//  ── WHAT IS SEEDED, AND WHAT IS NOT ─────────────────────────────────────────
//  Only the constants this change actually verified against a primary source: the
//  Manufacturing curve from the Ministry's own worked example (C-2023 and C-2024).
//  They are end-dated 2026-01-01, because MHRSD reissued the annex in January 2026
//  with 41 activities and re-baselined values, and THAT annex has not been
//  verified here. So an establishment asking for a band today gets a refusal that
//  names the exact document to load — not a silently stale answer. Transcribing
//  and verifying the 2026 annex is written up for the product owner.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The four non-Red curve floors for one establishment at one workforce size.</summary>
public sealed record NitaqatCurveFloors(
    decimal LowGreen,
    decimal MediumGreen,
    decimal HighGreen,
    decimal Platinum,
    string SourceNote,
    bool IsVerified);

public static class NitaqatCurve
{
    public const string SourceUrl =
        "https://www.hrsd.gov.sa/sites/default/files/2023-06/E20210523.pdf";

    public const string SourceTitle =
        "MHRSD Nitaqat Program Procedural Guideline (English edition of Ministerial Decision 182495, "
        + "11/10/1442 AH, in force 1 December 2021), retrieved 2026-09-20";

    /// <summary>
    /// The 2026 reissue, which supersedes the constants seeded here. Named in the refusal so a
    /// customer is told exactly which document to obtain rather than "contact support".
    /// </summary>
    public const string CurrentAnnexUrl =
        "https://www.hrsd.gov.sa/sites/default/files/2026-03/ntaqat-almtwr.pdf";

    /// <summary>Date the guideline was retrieved and its worked example reproduced.</summary>
    public const string VerifiedOn = "2026-09-20";

    /// <summary>
    /// Curve gradient for one activity and band. Keyed by activity because MHRSD publishes m per
    /// activity; NOT per year (only c moves by year).
    /// </summary>
    public static string GradientKey(string activityCode, string band) =>
        $"nitaqat.curve.{Normalise(activityCode)}.{Normalise(band)}.m";

    /// <summary>
    /// Curve intercept for one activity and band. Effective-dated, because MHRSD publishes a
    /// different c per year and says the third-year value applies "in the third year and beyond".
    /// </summary>
    public static string InterceptKey(string activityCode, string band) =>
        $"nitaqat.curve.{Normalise(activityCode)}.{Normalise(band)}.c";

    private static string Normalise(string s) =>
        (s ?? string.Empty).Trim().ToUpperInvariant().Replace(' ', '_');

    /// <summary>
    /// y = m · ln(x) + c, rounded to two decimal places — the precision MHRSD's own worked
    /// example reports ("1.68 * ln(400) + 12.08" → "22.15").
    ///
    /// <para><paramref name="totalWorkforce"/> is the establishment's TOTAL worker count. Note
    /// Decision 61706 clause Twenty-one: special-category workers "shall be calculated as one
    /// worker in all cases for the purpose of calculating the volume of the Establishment", so the
    /// weighting that moves the Saudization RATIO must not also move x.</para>
    /// </summary>
    public static decimal MinimumSaudization(decimal gradient, decimal intercept, decimal totalWorkforce)
    {
        // ln is undefined at 0 and negative at x < 1. A one-worker establishment has ln(1) = 0, so
        // the floor is simply the intercept, which is the correct limit of the published formula.
        if (totalWorkforce < 1m) totalWorkforce = 1m;

        var y = (double)gradient * Math.Log((double)totalWorkforce) + (double)intercept;
        return decimal.Round((decimal)y, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Resolves all four floors for an activity on a date, from the effective-dated rule store.
    /// Returns null when the activity has no curve in force — which is the common case and must
    /// produce a refusal, never a guess.
    /// </summary>
    public static async Task<NitaqatCurveFloors?> ResolveAsync(
        IStatutoryRuleReader rules, string activityCode, decimal totalWorkforce,
        DateOnly asOf, Guid? tenantId, CancellationToken ct = default)
    {
        var bands = new[]
        {
            NitaqatBands.LowGreen, NitaqatBands.MediumGreen,
            NitaqatBands.HighGreen, NitaqatBands.Platinum,
        };

        var floors = new decimal[bands.Length];

        for (var i = 0; i < bands.Length; i++)
        {
            var m = await rules.GetDecimalAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland,
                GradientKey(activityCode, bands[i]), asOf, tenantId, ct);
            var c = await rules.GetDecimalAsync(CountryCodes.Saudi, Jurisdictions.KsaMainland,
                InterceptKey(activityCode, bands[i]), asOf, tenantId, ct);

            // All four bands or none. A partial curve would band an establishment off an
            // incomplete ladder, which is a wrong answer rather than a missing one.
            if (m is null || c is null) return null;

            floors[i] = MinimumSaudization(m.Value, c.Value, totalWorkforce);
        }

        return new NitaqatCurveFloors(
            LowGreen: floors[0], MediumGreen: floors[1], HighGreen: floors[2], Platinum: floors[3],
            SourceNote:
                $"Computed from the MHRSD Nitaqat Mutawar curve y = m·ln(x) + c for activity "
                + $"'{activityCode}' at x = {totalWorkforce:0.##} total workers, effective "
                + $"{asOf:yyyy-MM-dd}. Fixed establishment size bands were abolished by Ministerial "
                + $"Decision 182495 with effect from 1 December 2021. Method source: {SourceTitle}.",
            // The METHOD is verified; whether the loaded m/c are current is a property of the
            // loaded rows, and the caller reports the rule store's own verification state.
            IsVerified: true);
    }

    /// <summary>
    /// Materialises the curve as in-memory threshold rows so every downstream consumer — band
    /// resolution, distance-to-next-band, hire scenarios, the snapshot writer — works unchanged.
    /// Nothing is persisted: these rows are derived, and persisting them would create a second,
    /// staleable copy of a number the curve can always recompute.
    /// </summary>
    public static List<NitaqatBandThreshold> ToThresholds(
        NitaqatCurveFloors floors, string activityCode, string sizeTierCode, DateOnly asOf)
    {
        var effectiveFrom = asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        return new[]
            {
                (NitaqatBands.LowGreen,    floors.LowGreen),
                (NitaqatBands.MediumGreen, floors.MediumGreen),
                (NitaqatBands.HighGreen,   floors.HighGreen),
                (NitaqatBands.Platinum,    floors.Platinum),
            }
            .Select(x => new NitaqatBandThreshold
            {
                ActivityCode = activityCode,
                SizeTierCode = sizeTierCode,
                Band = x.Item1,
                BandRank = NitaqatBands.RankOf(x.Item1),
                MinSaudizationPercent = x.Item2,
                EffectiveFrom = effectiveFrom,
                SourceNote = floors.SourceNote.Length > 500 ? floors.SourceNote[..500] : floors.SourceNote,
                IsVerified = floors.IsVerified,
            })
            .OrderByDescending(t => t.BandRank)
            .ToList();
    }
}
