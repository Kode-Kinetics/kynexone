using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// ─────────────────────────────────────────────────────────────────────────────
///  THE 2026 MHRSD ANNEX, AS LOADED.
/// ─────────────────────────────────────────────────────────────────────────────
///
/// <para>Before this data existed, EVERY establishment on EVERY activity was refused a band:
/// twelve real activities had no thresholds at all, and the one activity that did produce a
/// band was called "General (illustrative — not an MHRSD activity)". The Manufacturing curve
/// that had been seeded from the Ministry's worked example expired on 2026-01-01, so by the
/// time this was written not even Manufacturing answered. Saudization was non-functional for
/// every customer of the product.</para>
///
/// <para>These tests pin both halves of that: the refusal that was, and the band that is.</para>
///
/// <para><b>What "verified" means here.</b> Exactly one thing: MHRSD published a worked example
/// for that activity and this code reproduces the Ministry's own stated answer. That is true of
/// Manufacturing and of no other activity, so every other curve is loaded and reported
/// PROVISIONAL. The numbers are published, not invented — but published and checked are not the
/// same claim, and the product only makes the one it can support.</para>
/// </summary>
public class NitaqatMhrsdAnnexTests
{
    private static readonly DateOnly Today2026 = new(2026, 9, 21);

    private static ZayraDbContext NewDb([System.Runtime.CompilerServices.CallerMemberName] string name = "")
        => new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase($"{nameof(NitaqatMhrsdAnnexTests)}.{name}")
            .Options);

    /// <summary>Resolves against exactly the rows StatutoryRuleSeeder would write.</summary>
    private sealed class SeededRuleReader : IStatutoryRuleReader
    {
        private readonly List<StatutoryRule> _rules = StatutoryRuleSeeder.BuildRules();

        public Task<decimal?> GetDecimalAsync(
            string cc, string j, string key, DateOnly asOf, Guid? tenantId = null, CancellationToken ct = default)
        {
            var cutoff = asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var row = _rules
                .Where(r => r.CountryCode == cc && r.Jurisdiction == j && r.RuleKey == key
                         && r.EffectiveFrom <= cutoff
                         && (r.EffectiveTo == null || r.EffectiveTo > cutoff))
                .OrderByDescending(r => r.EffectiveFrom)
                .Select(r => r.RuleValue)
                .FirstOrDefault();

            return Task.FromResult(row is null
                ? (decimal?)null
                : decimal.Parse(row, System.Globalization.CultureInfo.InvariantCulture));
        }

        public Task<string?> GetStringAsync(
            string cc, string j, string key, DateOnly asOf, Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  1. FAIL BEFORE — quoted from the state this change found
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The exact "before" this change fixes, reconstructed from the constants that were seeded
    /// prior to it: Manufacturing's curve ran out on 2026-01-01 and nothing replaced it, so on
    /// any date in 2026 the curve did not resolve and the establishment was refused.
    ///
    /// <para>Verified against PRODUCTION on 2026-09-21: <c>statutory_rules</c> held 12
    /// <c>nitaqat.curve.%</c> rows, all MANUFACTURING, all with
    /// <c>effective_to = 2026-01-01</c>; the count of curve rows in force on 2026-09-21 was
    /// ZERO, and <c>nitaqat_band_thresholds</c> held 36 rows, all GENERAL_UNVERIFIED.</para>
    /// </summary>
    [Fact]
    public async Task Before_TheAnnexWasLoaded_EveryActivityRefused()
    {
        // The pre-change rule set: only the 2023/2024 Manufacturing rows, which expire at the
        // reissue date. This is the seeded data as it stood, filtered to it.
        var reissue = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var before = StatutoryRuleSeeder.BuildRules()
            .Where(r => r.RuleKey.StartsWith("nitaqat.curve.", StringComparison.Ordinal))
            .Where(r => r.EffectiveFrom < reissue)
            .ToList();

        before.Should().NotBeEmpty("the old Manufacturing constants are still seeded, for history");
        before.Select(r => r.RuleKey.Split('.')[2]).Distinct()
            .Should().BeEquivalentTo(new[] { "MANUFACTURING" },
                "before this change, Manufacturing was the only activity with any curve at all");

        var reader = new FilteredReader(before);

        // Manufacturing itself — expired, therefore refused.
        (await NitaqatCurve.ResolveAsync(reader, "MANUFACTURING", 400m, Today2026, null))
            .Should().BeNull("the 2024 constants expired on 2026-01-01 and nothing replaced them");

        // And every other activity, which never had constants at all.
        foreach (var code in new[] { "CONSTRUCTION", "RETAIL", "WHOLESALE", "ICT", "FINANCE",
                                     "HEALTHCARE", "EDUCATION", "HOSPITALITY", "TRANSPORT",
                                     "PROF_SERVICES", "ADMIN_SUPPORT" })
            (await NitaqatCurve.ResolveAsync(reader, code, 400m, Today2026, null))
                .Should().BeNull($"{code} had no curve before the annex was loaded");
    }

    private sealed class FilteredReader : IStatutoryRuleReader
    {
        private readonly List<StatutoryRule> _rules;
        public FilteredReader(List<StatutoryRule> rules) => _rules = rules;

        public Task<decimal?> GetDecimalAsync(
            string cc, string j, string key, DateOnly asOf, Guid? tenantId = null, CancellationToken ct = default)
        {
            var cutoff = asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var row = _rules
                .Where(r => r.RuleKey == key && r.EffectiveFrom <= cutoff
                         && (r.EffectiveTo == null || r.EffectiveTo > cutoff))
                .OrderByDescending(r => r.EffectiveFrom)
                .Select(r => r.RuleValue)
                .FirstOrDefault();
            return Task.FromResult(row is null
                ? (decimal?)null
                : decimal.Parse(row, System.Globalization.CultureInfo.InvariantCulture));
        }

        public Task<string?> GetStringAsync(
            string cc, string j, string key, DateOnly asOf, Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  2. PASS AFTER — the same activities, same date, now banded
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task After_TheAnnexIsLoaded_TheSameActivitiesBand()
    {
        var reader = new SeededRuleReader();

        foreach (var code in new[] { "MANUFACTURING", "CONSTRUCTION", "FINANCE", "WHOLESALE" })
        {
            var floors = await NitaqatCurve.ResolveAsync(reader, code, 400m, Today2026, null);
            floors.Should().NotBeNull($"{code} maps one-to-one onto an annex row and is now loaded");
            floors!.LowGreen.Should().BeLessThan(floors.MediumGreen);
            floors.MediumGreen.Should().BeLessThan(floors.HighGreen);
            floors.HighGreen.Should().BeLessThan(floors.Platinum);
        }
    }

    /// <summary>
    /// The coarse activity codes that span SEVERAL annex rows keep refusing, on purpose.
    ///
    /// <para>"Retail Trade" is four different annex rows whose Low Green floors at 400 workers
    /// run from 23.25% to 82.00%. Loading any one of them would tell a mobile-phone retailer
    /// they are Low Green at 25% when the Ministry requires 82% — Red, and barred from renewing
    /// a single work permit. The refusal is the correct answer until the customer picks the
    /// specific MHRSD activity, which they now can.</para>
    /// </summary>
    [Fact]
    public async Task CoarseActivitiesSpanningSeveralAnnexRows_StillRefuse()
    {
        var reader = new SeededRuleReader();

        foreach (var code in new[] { "RETAIL", "ICT", "HEALTHCARE", "EDUCATION",
                                     "HOSPITALITY", "TRANSPORT", "PROF_SERVICES", "ADMIN_SUPPORT" })
            (await NitaqatCurve.ResolveAsync(reader, code, 400m, Today2026, null))
                .Should().BeNull($"'{code}' spans several MHRSD activities with materially "
                               + "different floors; picking one would be a guess");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  3. THE CALIBRATION — the Ministry's own worked example
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// MHRSD's worked example, reproduced from the seeded constants rather than from a literal.
    /// "Abdullah for Plastics Company … Manufacturing Sector with 400 employees and its
    /// Saudization rate is (35.00%)" → High Green from Jan 2023, Low Green from Jan 2024.
    /// If the extraction method is wrong, this is where it shows.
    /// </summary>
    [Theory]
    // asOf,            LowGreen, MediumGreen, HighGreen, Platinum, band at 35.00%
    [InlineData("2023-06-01", 22.15, 30.07, 34.93, 40.83, NitaqatBands.HighGreen)]
    [InlineData("2024-06-01", 27.15, 35.07, 37.93, 45.33, NitaqatBands.LowGreen)]
    public async Task MinistryWorkedExample_Manufacturing400Workers_IsReproduced(
        string asOf, double low, double medium, double high, double platinum, string expectedBand)
    {
        var floors = await NitaqatCurve.ResolveAsync(
            new SeededRuleReader(), "MANUFACTURING", 400m, DateOnly.Parse(asOf), null);

        floors.Should().NotBeNull();
        floors!.LowGreen.Should().Be((decimal)low);
        floors.MediumGreen.Should().Be((decimal)medium);
        floors.HighGreen.Should().Be((decimal)high);
        floors.Platinum.Should().Be((decimal)platinum);

        BandFor(35.00m, floors).Should().Be(expectedBand);
        floors.IsVerified.Should().BeTrue(
            "Manufacturing is the one activity whose constants reproduce a Ministry worked example");
    }

    private static string BandFor(decimal saudization, NitaqatCurveFloors f)
    {
        if (saudization >= f.Platinum) return NitaqatBands.Platinum;
        if (saudization >= f.HighGreen) return NitaqatBands.HighGreen;
        if (saudization >= f.MediumGreen) return NitaqatBands.MediumGreen;
        if (saudization >= f.LowGreen) return NitaqatBands.LowGreen;
        return NitaqatBands.Red;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  4. A WORKED EXAMPLE PER LOADED ACTIVITY
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One hand-computed example per activity loaded from the 2026 annex, on the C-2026 column,
    /// asserting the four floors and the resulting band.
    ///
    /// <para>Each expectation was computed independently of the seeder, from the annex cell and
    /// y = m·ln(x) + c, and is written out in full here so a reader can check one by hand against
    /// page N of the PDF without running anything. These are the numbers a customer will be
    /// shown; they are worth stating.</para>
    /// </summary>
    [Theory]
    // code, x (total workforce), n (Saudi %),   LowGreen, MediumGreen, HighGreen, Platinum, band
    [InlineData("MANUFACTURING",                 400, 35.00, 25.15, 33.07, 36.43, 42.33, NitaqatBands.MediumGreen)]
    [InlineData("CONSTRUCTION",                  400, 20.00, 11.95, 13.95, 17.50, 22.50, NitaqatBands.HighGreen)]
    [InlineData("FINANCE",                       400, 60.00, 65.58, 72.58, 77.58, 80.58, NitaqatBands.Red)]
    [InlineData("WHOLESALE",                     400, 40.00, 38.05, 42.52, 46.41, 55.93, NitaqatBands.LowGreen)]
    [InlineData("RETAIL_GENERAL",                400, 40.00, 38.05, 42.52, 46.41, 55.93, NitaqatBands.LowGreen)]
    [InlineData("RETAIL_LADIES_MOBILE",          400, 40.00, 82.00, 85.00, 89.00, 95.04, NitaqatBands.Red)]
    [InlineData("IT_SOLUTIONS",                  100, 45.00, 36.85, 43.32, 53.42, 62.98, NitaqatBands.MediumGreen)]
    [InlineData("TRANSPORT_LAND_STORAGE",         50, 15.00, 16.59, 20.70, 23.69, 34.43, NitaqatBands.Red)]
    [InlineData("ACCOMMODATION_LEISURE_TOURISM", 250, 50.00, 37.96, 44.38, 50.70, 56.82, NitaqatBands.MediumGreen)]
    [InlineData("MEDICAL_LABS_HEALTH",           600, 30.00, 27.98, 32.98, 36.48, 36.98, NitaqatBands.LowGreen)]
    [InlineData("HIGHER_EDUCATION",             1000, 80.00, 34.00, 48.00, 78.34, 84.97, NitaqatBands.HighGreen)]
    [InlineData("COMBINED_ENTITIES",              12, 25.00, 16.53, 27.94, 39.35, 49.54, NitaqatBands.LowGreen)]
    public async Task WorkedExample_PerLoadedActivity_OnTheC2026Column(
        string code, int workforce, double saudization,
        double low, double medium, double high, double platinum, string expectedBand)
    {
        var floors = await NitaqatCurve.ResolveAsync(
            new SeededRuleReader(), code, workforce, new DateOnly(2026, 6, 1), null);

        floors.Should().NotBeNull($"{code} is loaded from the 2026 annex");
        floors!.LowGreen.Should().Be((decimal)low, $"{code} Low Green at {workforce} workers");
        floors.MediumGreen.Should().Be((decimal)medium, $"{code} Medium Green at {workforce} workers");
        floors.HighGreen.Should().Be((decimal)high, $"{code} High Green at {workforce} workers");
        floors.Platinum.Should().Be((decimal)platinum, $"{code} Platinum at {workforce} workers");

        BandFor((decimal)saudization, floors).Should().Be(expectedBand);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  5. PROVISIONAL MEANS PROVISIONAL
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A band computed from an unreproduced curve must reach the screen labelled provisional.
    /// The flag is the whole reason the product can ship useful numbers before they are certain;
    /// if it silently read "verified" the honesty would be decorative.
    /// </summary>
    [Fact]
    public async Task EveryActivityButManufacturing_ReportsItsBandAsProvisional()
    {
        var reader = new SeededRuleReader();

        foreach (var activity in StatutoryRuleSeeder.Annex2026.Where(a => a.Code != "MANUFACTURING"))
        {
            var floors = await NitaqatCurve.ResolveAsync(reader, activity.Code, 400m, Today2026, null);

            floors.Should().NotBeNull($"{activity.Code} is loaded");
            floors!.IsVerified.Should().BeFalse(
                $"{activity.Code} has no Ministry worked example to reproduce, so its band is provisional");
            floors.SourceNote.Should().Contain("PROVISIONAL",
                "and the note that travels to the screen has to say so");
        }
    }

    /// <summary>
    /// An absent verification flag means UNVERIFIED, never verified-by-default. A curve loaded by
    /// some future path that forgets the flag must degrade to provisional rather than claim
    /// a confidence nobody established.
    /// </summary>
    [Fact]
    public async Task AVerificationFlagThatIsMissing_MeansProvisional()
    {
        var noFlag = new DictionaryReader(new Dictionary<string, decimal>
        {
            [NitaqatCurve.GradientKey("MANUFACTURING", NitaqatBands.LowGreen)]     = 1.68m,
            [NitaqatCurve.InterceptKey("MANUFACTURING", NitaqatBands.LowGreen)]    = 12.08m,
            [NitaqatCurve.GradientKey("MANUFACTURING", NitaqatBands.MediumGreen)]  = 1.87m,
            [NitaqatCurve.InterceptKey("MANUFACTURING", NitaqatBands.MediumGreen)] = 18.87m,
            [NitaqatCurve.GradientKey("MANUFACTURING", NitaqatBands.HighGreen)]    = 2.08m,
            [NitaqatCurve.InterceptKey("MANUFACTURING", NitaqatBands.HighGreen)]   = 22.47m,
            [NitaqatCurve.GradientKey("MANUFACTURING", NitaqatBands.Platinum)]     = 2.08m,
            [NitaqatCurve.InterceptKey("MANUFACTURING", NitaqatBands.Platinum)]    = 28.37m,
            // VerifiedKey deliberately absent.
        });

        var floors = await NitaqatCurve.ResolveAsync(noFlag, "MANUFACTURING", 400m, new DateOnly(2023, 6, 1), null);

        floors.Should().NotBeNull();
        floors!.IsVerified.Should().BeFalse("an absent flag is not a claim of verification");
    }

    private sealed class DictionaryReader : IStatutoryRuleReader
    {
        private readonly IReadOnlyDictionary<string, decimal> _v;
        public DictionaryReader(IReadOnlyDictionary<string, decimal> v) => _v = v;

        public Task<decimal?> GetDecimalAsync(string cc, string j, string key, DateOnly d, Guid? t = null, CancellationToken ct = default)
            => Task.FromResult(_v.TryGetValue(key, out var x) ? x : (decimal?)null);

        public Task<string?> GetStringAsync(string cc, string j, string key, DateOnly d, Guid? t = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  6. THE CATALOGUE
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every activity that carries a curve is in the catalogue under that exact code, and every
    /// new catalogue code is unique. A curve keyed to a code no activity uses is dead data; two
    /// activities sharing a code is a duplicate row the Postgres unique index will NOT catch,
    /// because a NULL tenant_id makes platform rows distinct for uniqueness purposes.
    /// </summary>
    [Fact]
    public async Task EveryCurveActivity_IsInTheCatalogue_AndCodesAreUnique()
    {
        await using var db = NewDb();
        await NitaqatReferenceSeeder.SeedAsync(db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var codes = await db.NitaqatActivities.Select(a => a.Code).ToListAsync();

        codes.Should().OnlyHaveUniqueItems("a duplicate platform activity code would persist twice");

        foreach (var activity in StatutoryRuleSeeder.Annex2026)
            codes.Should().Contain(activity.Code,
                $"the curve for '{activity.AnnexName}' is keyed to {activity.Code}, so that code "
                + "must exist in the activity catalogue or the curve is unreachable");
    }

    /// <summary>
    /// The honesty contract still holds for the new rows: an MHRSD activity has a CURVE, never a
    /// grid, and says so on the row itself.
    /// </summary>
    [Fact]
    public async Task TheNewActivities_ShipNoGridAndSaySo()
    {
        await using var db = NewDb();
        await NitaqatReferenceSeeder.SeedAsync(db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var annexCodes = StatutoryRuleSeeder.Annex2026.Select(a => a.Code).ToHashSet();

        (await db.NitaqatBandThresholds.Select(t => t.ActivityCode).Distinct().ToListAsync())
            .Should().BeEquivalentTo(new[] { "GENERAL_UNVERIFIED" },
                "loading curves must not have quietly loaded a grid as well");

        var added = await db.NitaqatActivities.Where(a => annexCodes.Contains(a.Code)).ToListAsync();
        added.Should().OnlyContain(a => !a.IsVerified);
        added.Should().OnlyContain(a => a.SourceNote.Contains("NO BAND THRESHOLDS"));
    }

    /// <summary>
    /// Every string the new activity rows write fits its column. Only NameEn is covered by
    /// NitaqatSeedFitsColumnTests; Code, NameAr and ActivityGroup are equally capable of
    /// throwing 22001 and rolling back the ENTIRE Nitaqat reference seed inside its single
    /// SaveChangesAsync, leaving the product with an empty activity dropdown and no way to
    /// compute a band — which is how this feature was disabled once before.
    /// </summary>
    [Fact]
    public void EveryActivityStringFitsItsColumn()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=u;Password=p").Options;
        using var db = new ZayraDbContext(options);
        var entity = db.Model.FindEntityType(typeof(NitaqatActivity))!;

        int Max(string p) => entity.FindProperty(p)!.GetMaxLength()!.Value;

        var field = typeof(NitaqatReferenceSeeder)
            .GetField("Activities", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var items = ((System.Collections.IEnumerable)field.GetValue(null)!).Cast<object>().ToList();
        items.Should().NotBeEmpty();

        var overlong = new List<string>();
        foreach (var item in items)
        {
            var t = item.GetType();
            string Get(string m) => t.GetProperty(m)!.GetValue(item)?.ToString() ?? "";
            var code = Get("Code");

            foreach (var (member, column) in new[]
                     { ("Code", "Code"), ("NameEn", "NameEn"), ("NameAr", "NameAr"), ("Group", "ActivityGroup") })
            {
                var value = Get(member);
                var max = Max(column);
                if (value.Length > max)
                    overlong.Add($"{code}.{member} is {value.Length} chars against varchar({max})");
            }
        }

        overlong.Should().BeEmpty(
            "an over-length string throws 22001 inside the seeder's single SaveChangesAsync and "
            + "rolls back every Nitaqat reference row. Shorten the value; do not widen the column.");
    }
}
