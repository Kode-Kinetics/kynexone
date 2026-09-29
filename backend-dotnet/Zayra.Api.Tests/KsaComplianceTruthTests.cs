using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The four places the Saudi compliance story did not survive a technical evaluation.
///
/// Each section below pins a claim the product makes to something the product can actually do.
/// Where it cannot — the WPS byte layout — the test pins the HONEST STATEMENT instead, because
/// the remedy is a gateway acceptance test and no amount of code closes it.
/// </summary>
public class KsaComplianceTruthTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid CompanyA = Guid.NewGuid();

    private static readonly DateOnly AsOf = new(2026, 6, 1);
    private static readonly DateTime Eff2021 = new(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ZayraDbContext NewDb([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(name + Guid.NewGuid()).Options);

    // ═════════════════════════════════════════════════════════════════════════
    //  HOLE 1 — Nitaqat could not band a real establishment
    //
    //  The engine was complete; the twelve real MHRSD activities shipped with no
    //  thresholds, so a customer entering their real activity got no band at all.
    //  The fix is the LOADER, because inventing the grid is the failure mode the
    //  seeder's honesty test already guards against.
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ── THE WORKED EXAMPLE ───────────────────────────────────────────────────
    ///
    /// A Saudi contracting establishment in Riyadh: 240 people on the register —
    /// 46 Saudi full-time above the wage floor, 4 Saudi part-time, 190 non-Saudi.
    ///
    ///   Weighted Saudi  = 46×1.0 + 4×0.5            =  48.0 units
    ///   Weighted total  = 46×1.0 + 4×0.5 + 190×1.0  = 238.0 units
    ///   Saudization     = 48 / 238                  =  20.17%
    ///   Size tier       = 238 weighted units        →  Medium C (200–499)
    ///
    /// BEFORE: the activity CONSTRUCTION has no grid, so the product refuses with
    /// nitaqat_thresholds_not_published and shows no band — correct, but useless.
    ///
    /// AFTER: the customer loads their own activity's MHRSD row from Qiwa, and the
    /// same establishment bands as Medium Green with the full ladder around it.
    ///
    /// THE THRESHOLDS IN THIS TEST ARE THE FIXTURE'S, NOT MHRSD'S. They are what a
    /// customer loads, and the point of the test is that the loaded grid drives the
    /// answer — not that these particular percentages are the published ones.
    /// </summary>
    [Fact]
    public async Task WorkedExample_RealActivity_RefusesBeforeTheGridIsLoaded_AndBandsAfter()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        db.NitaqatEstablishmentProfiles.Add(new NitaqatEstablishmentProfile
        {
            TenantId = TenantA, CompanyId = CompanyA, ActivityCode = "CONSTRUCTION", IsActive = true,
        });

        for (var i = 0; i < 46; i++)  AddEmployee(db, TenantA, CompanyA, "Saudi",    "FullTime", 9_000m);
        for (var i = 0; i < 4;  i++)  AddEmployee(db, TenantA, CompanyA, "Saudi",    "PartTime", 9_000m);
        for (var i = 0; i < 190; i++) AddEmployee(db, TenantA, CompanyA, "Indian",   "FullTime", 4_500m);
        await db.SaveChangesAsync();

        // ── FAIL BEFORE ──────────────────────────────────────────────────────
        var before = await Nitaqat(db).GetStandingAsync(TenantA, CompanyA, AsOf);

        before.Ok.Should().BeFalse("CONSTRUCTION ships with no MHRSD grid");
        before.Standing.Should().BeNull();
        before.Refusal!.Reason.Should().Be(NitaqatRefusalReasons.ThresholdsMissing);
        before.Refusal.Message.Should().Contain("Construction");

        // …and the product says so in as many words, rather than showing an empty screen.
        var coverageBefore = await Grid(db).GetCoverageAsync(TenantA, AsOf);
        coverageBefore.ActivitiesWithCompleteGrid.Should().Be(0);
        coverageBefore.ConfigurationRequiredNotice.Should().Be(NitaqatGridImportService.NoticeNoGrid);

        // ── LOAD THE GRID ────────────────────────────────────────────────────
        var import = await Grid(db).ImportAsync(TenantA, new NitaqatGridImportRequest(
            ActivityCode: "CONSTRUCTION",
            EffectiveFrom: new DateOnly(2026, 1, 1),
            SourceNote: "MHRSD Nitaqat table for Construction & Contracting, read from the "
                      + "establishment's Qiwa account (labour office 1, establishment 234567) on "
                      + "2026-09-14 by the implementation consultant.",
            IsVerified: true,
            Cells: new[]
            {
                new NitaqatGridCellInput("MediumC", LowGreen: 12m, MediumGreen: 18m, HighGreen: 25m, Platinum: 35m),
            }), userId: null);

        import.Ok.Should().BeTrue(import.Error + " " + string.Join("; ", import.Rejections));
        import.RowsInserted.Should().Be(4);

        // ── PASS AFTER ───────────────────────────────────────────────────────
        var after = await Nitaqat(db).GetStandingAsync(TenantA, CompanyA, AsOf);

        after.Ok.Should().BeTrue();
        var s = after.Standing!;

        s.SaudiWeighted.Should().Be(48m);
        s.TotalWeighted.Should().Be(238m);
        s.RawSaudiHeadcount.Should().Be(50);
        s.RawTotalHeadcount.Should().Be(240);
        s.AchievedPercent.Should().Be(20.17m);
        s.SizeTierCode.Should().Be("MediumC");

        s.Band.Should().Be(NitaqatBands.MediumGreen);
        s.CurrentBandFloorPercent.Should().Be(18m);
        s.RestrictsServices.Should().BeFalse("Medium Green is full green standing");

        // The scenario answers an HR director actually asks, all exact:
        //   to High Green (25%): (48+n)/(238+n) ≥ 0.25 ⇒ n ≥ 11.5/0.75 = 15.33 ⇒ 16
        s.NextBandUp!.Band.Should().Be(NitaqatBands.HighGreen);
        s.NextBandUp.RequiredPercent.Should().Be(25m);
        s.NextBandUp.SaudiHiresRequired.Should().Be(16);
        //   expat headroom at the 18% floor: 48/0.18 − 238 = 28.67 ⇒ 28
        s.Scenario.ExpatHiresBeforeDowngrade.Should().Be(28);
        //   Saudi attrition tolerance: (48 − 0.18×238)/(1 − 0.18) = 6.29 ⇒ 6
        s.Scenario.SaudiLeaversBeforeDowngrade.Should().Be(6);

        // Provenance survives to the answer: the grid was loaded verified, so the
        // thresholds are not in the unverified list.
        s.UnverifiedInputs.Should().NotContain(x => x.Contains("threshold"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  HOLE 1(b) — the model was banding off the WRONG REGIME
    //
    //  MHRSD abolished fixed establishment size bands on 1 December 2021 and
    //  replaced the grid with a per-activity curve. These tests reproduce the
    //  Ministry's OWN published worked example, which is the strongest evidence
    //  available that the implementation is right: the inputs, the intermediate
    //  figures and the final band are all MHRSD's, not ours.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ── MHRSD'S OWN WORKED EXAMPLE, REPRODUCED EXACTLY ───────────────────────
    ///
    /// Source: MHRSD "Nitaqat Program Procedural Guideline" (official English edition of
    /// Ministerial Decision 182495), pages 8–9, at
    /// https://www.hrsd.gov.sa/sites/default/files/2023-06/E20210523.pdf — retrieved and
    /// text-extracted 2026-09-20.
    ///
    /// The guideline's example: activity Manufacturing, entity with 400 total workers, Saudization
    /// 35.00%. It prints the constants, the four arithmetic expressions, the four results, and the
    /// band the entity lands in. Every one of those is asserted below, verbatim from the PDF.
    /// </summary>
    [Theory]
    // Ministry's own printed results for "the year starting Jan 2023" (guideline p.9):
    //   Low Green    1.68 * ln(400) + 12.08 = 22.15
    //   Medium Green 1.87 * ln(400) + 18.87 = 30.07
    //   High Green   2.08 * ln(400) + 22.47 = 34.93
    //   Platinum     2.08 * ln(400) + 28.37 = 40.83
    [InlineData(1.68, 12.08, 400, 22.15)]
    [InlineData(1.87, 18.87, 400, 30.07)]
    [InlineData(2.08, 22.47, 400, 34.93)]
    [InlineData(2.08, 28.37, 400, 40.83)]
    // And "starting Jan 2024" (guideline p.9), which uses the same m with the third-year c:
    //   27.15 / 35.07 / 37.93 / 45.33
    [InlineData(1.68, 17.08, 400, 27.15)]
    [InlineData(1.87, 23.87, 400, 35.07)]
    [InlineData(2.08, 25.47, 400, 37.93)]
    [InlineData(2.08, 32.87, 400, 45.33)]
    public void NitaqatCurve_ReproducesTheMinistrysPublishedArithmetic(
        double m, double c, int workforce, double expected)
    {
        NitaqatCurve.MinimumSaudization((decimal)m, (decimal)c, workforce)
            .Should().Be((decimal)expected);
    }

    /// <summary>
    /// The Ministry's example continues: "As the entity's Saudization is 35.00% it will be in the
    /// High Green range starting Jan 2023" and "...in the Low Green range starting Jan 2024".
    /// Both are asserted, because the year rollover changing the band is the whole point of the
    /// effective-dated intercept.
    /// </summary>
    [Fact]
    public void NitaqatCurve_LandsTheMinistrysExampleEntityInTheBandTheMinistrySays()
    {
        // Jan 2023 ladder: Red < 22.15 <= LowGreen < 30.07 <= MediumGreen < 34.93 <= HighGreen < 40.83 <= Platinum
        var y2023 = Floors(lowGreen: 22.15m, medium: 30.07m, high: 34.93m, platinum: 40.83m);
        NitaqatCalculationService.ResolveBand(y2023, 35.00m).Should().Be(NitaqatBands.HighGreen);

        // Jan 2024 ladder, same entity, same 35.00% — the Ministry says it drops to Low Green.
        var y2024 = Floors(lowGreen: 27.15m, medium: 35.07m, high: 37.93m, platinum: 45.33m);
        NitaqatCalculationService.ResolveBand(y2024, 35.00m).Should().Be(NitaqatBands.LowGreen);

        // Boundaries are inclusive-lower, exclusive-upper, exactly as the guideline's table states.
        NitaqatCalculationService.ResolveBand(y2023, 34.93m).Should().Be(NitaqatBands.HighGreen);
        NitaqatCalculationService.ResolveBand(y2023, 34.92m).Should().Be(NitaqatBands.MediumGreen);
        NitaqatCalculationService.ResolveBand(y2023, 40.83m).Should().Be(NitaqatBands.Platinum);
        NitaqatCalculationService.ResolveBand(y2023, 22.14m).Should().Be(NitaqatBands.Red);
    }

    /// <summary>
    /// End to end through the real service and the real seeded constants: a Manufacturing
    /// establishment of 400 workers at 35% Saudization is banded High Green in 2023, exactly as
    /// the Ministry's guideline says — and the answer reports that it came from the curve, not
    /// from the obsolete size-tier grid.
    /// </summary>
    [Fact]
    public async Task WorkedExample_ManufacturingCurve_EndToEnd_MatchesTheMinistry()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        db.NitaqatActivities.Add(new NitaqatActivity
        {
            TenantId = null, Code = "MANUFACTURING", NameEn = "Manufacturing",
            ActivityGroup = "Manufacturing", IsActive = true, SourceNote = "NO BAND THRESHOLDS seeded.",
        });
        SeedCompany(db, TenantA, CompanyA, "SA");
        db.NitaqatEstablishmentProfiles.Add(new NitaqatEstablishmentProfile
        {
            TenantId = TenantA, CompanyId = CompanyA, ActivityCode = "MANUFACTURING", IsActive = true,
        });

        // 400 workers, 140 Saudi = 35.00% exactly — the Ministry's example entity.
        for (var i = 0; i < 140; i++) AddEmployee(db, TenantA, CompanyA, "Saudi",  "FullTime", 9_000m);
        for (var i = 0; i < 260; i++) AddEmployee(db, TenantA, CompanyA, "Indian", "FullTime", 4_500m);
        await db.SaveChangesAsync();

        var svc = new NitaqatCalculationService(db, ManufacturingCurveReader.For2023());
        var s = (await svc.GetStandingAsync(TenantA, CompanyA, new DateOnly(2023, 6, 1))).Standing!;

        s.TotalWeighted.Should().Be(400m);
        s.SaudiWeighted.Should().Be(140m);
        s.AchievedPercent.Should().Be(35.00m);

        s.Band.Should().Be(NitaqatBands.HighGreen, "MHRSD's guideline says High Green for this entity");
        s.CurrentBandFloorPercent.Should().Be(34.93m);
        s.NextBandUp!.Band.Should().Be(NitaqatBands.Platinum);
        s.NextBandUp.RequiredPercent.Should().Be(40.83m);

        // The regime is reported, so nobody is silently banded off the pre-2021 grid.
        s.BandingMethod.Should().Be(NitaqatBandingMethods.Curve);
        s.BandingMethodNote.Should().Contain("1 December 2021");
        s.UnverifiedInputs.Should().NotContain(x => x.Contains("size-tier table"));
    }

    /// <summary>
    /// The fallback is reported honestly. When no curve is loaded the product still answers off a
    /// manually loaded grid — but says, on the standing, that the floors did not come from the
    /// regime in force.
    /// </summary>
    [Fact]
    public async Task WhenBandedOffTheObsoleteGrid_TheStandingSaysSo()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        db.NitaqatEstablishmentProfiles.Add(new NitaqatEstablishmentProfile
        {
            TenantId = TenantA, CompanyId = CompanyA, ActivityCode = "CONSTRUCTION", IsActive = true,
        });
        for (var i = 0; i < 60; i++)  AddEmployee(db, TenantA, CompanyA, "Saudi",  "FullTime", 9_000m);
        for (var i = 0; i < 140; i++) AddEmployee(db, TenantA, CompanyA, "Indian", "FullTime", 4_500m);
        await db.SaveChangesAsync();

        await Grid(db).ImportAsync(TenantA, Request("CONSTRUCTION",
            cells: new[] { new NitaqatGridCellInput("MediumC", 12m, 18m, 25m, 35m) }), null);

        var s = (await Nitaqat(db).GetStandingAsync(TenantA, CompanyA, AsOf)).Standing!;

        s.Band.Should().Be(NitaqatBands.HighGreen);
        s.BandingMethod.Should().Be(NitaqatBandingMethods.SizeTierGrid);
        s.BandingMethodNote.Should().Contain("abolished fixed size bands");
        s.AllInputsVerified.Should().BeFalse();
        s.UnverifiedInputs.Should().Contain(x => x.Contains("not the MHRSD curve in force since"));
    }

    /// <summary>
    /// A partial curve must never band. Three bands loaded and one missing would put the
    /// establishment on an incomplete ladder — a wrong answer, not a missing one.
    /// </summary>
    [Fact]
    public async Task NitaqatCurve_RefusesAPartialCurve()
    {
        var partial = new DictionaryRuleReader(new Dictionary<string, decimal>
        {
            [NitaqatCurve.GradientKey("MANUFACTURING", NitaqatBands.LowGreen)] = 1.68m,
            [NitaqatCurve.InterceptKey("MANUFACTURING", NitaqatBands.LowGreen)] = 12.08m,
            // MediumGreen, HighGreen and Platinum deliberately absent.
        });

        (await NitaqatCurve.ResolveAsync(partial, "MANUFACTURING", 400m, new DateOnly(2023, 6, 1), null))
            .Should().BeNull();
    }

    /// <summary>
    /// The seeded Manufacturing constants EXPIRE at 2026-01-01, because MHRSD reissued the annex
    /// in January 2026 and those values were not verified here. A 2026 question must get a refusal
    /// naming the document to load — never a 2024 constant answering quietly.
    /// </summary>
    [Fact]
    public void SeededCurveConstants_ExpireWhenTheMinistryReissuedTheAnnex()
    {
        var annexReissue = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // The subject of this test is the PRE-REISSUE constants: the Manufacturing curve read out
        // of the 2021/2023 English guideline's worked example. The 2026 annex has since been read
        // and loaded (see SeededCurveConstants_FromThe2026Annex_*), which is why rows effective
        // ON or after the reissue now exist — but that must not buy the OLD rows a single extra
        // day. Every constant published before the reissue still has to stop at it.
        var preReissue = Zayra.Api.Infrastructure.Seed.StatutoryRuleSeeder.BuildRules()
            .Where(r => r.RuleKey.StartsWith("nitaqat.curve.", StringComparison.Ordinal))
            .Where(r => r.EffectiveFrom < annexReissue)
            .ToList();

        preReissue.Should().NotBeEmpty("the verified Manufacturing curve is seeded");
        preReissue.Should().OnlyContain(r => r.EffectiveTo != null,
            "an un-expiring constant would answer a 2026 question with a 2024 number");
        preReissue.Should().OnlyContain(r => r.EffectiveTo <= annexReissue);
        preReissue.Should().OnlyContain(r => r.Description.Contains("hrsd.gov.sa"),
            "every statutory constant must carry the source it was read from");
        preReissue.Should().OnlyContain(r => r.Description.Contains("2026-09-20"),
            "and the date it was read");

        // Nothing but Manufacturing was verified from that document, so nothing but Manufacturing
        // was seeded from it.
        preReissue.Select(r => r.RuleKey.Split('.')[2]).Distinct()
            .Should().BeEquivalentTo(new[] { "MANUFACTURING" });
    }

    /// <summary>
    /// Every curve constant, of any vintage, names the document it was read from and the day it
    /// was read. This is the provenance half of the old single test, kept whole and applied to
    /// the 2026 annex rows as well.
    /// </summary>
    [Fact]
    public void EverySeededCurveConstant_CarriesItsSourceAndReadDate()
    {
        var rules = Zayra.Api.Infrastructure.Seed.StatutoryRuleSeeder.BuildRules()
            .Where(r => r.RuleKey.StartsWith("nitaqat.curve.", StringComparison.Ordinal))
            .ToList();

        rules.Should().NotBeEmpty();
        rules.Should().OnlyContain(r => r.Description.Contains("hrsd.gov.sa"),
            "every statutory constant must carry the source it was read from");
        rules.Should().OnlyContain(
            r => System.Text.RegularExpressions.Regex.IsMatch(r.Description, @"\b20\d\d-\d\d-\d\d\b"),
            "and the date it was read");

        // A constant may say VERIFIED only where a Ministry worked example was reproduced for
        // that activity. Everything else must say so on the row, because the words travel to the
        // screen with the band.
        rules.Where(r => !r.RuleKey.Contains(".MANUFACTURING."))
            .Should().OnlyContain(r => r.Description.Contains("UNVERIFIED"),
                "no activity but Manufacturing has a Ministry worked example to reproduce");
    }

    /// <summary>
    /// The 2026 annex load is COMPLETE per activity and CONTINUOUS in time.
    ///
    /// <para>Two separate ways to hand a customer a wrong band. A half-loaded activity bands off
    /// an incomplete ladder — <see cref="NitaqatCurve.ResolveAsync"/> refuses that, so it becomes
    /// a silent refusal rather than an answer. A hole between two intercept windows is worse: the
    /// curve simply stops resolving on 1 January of some year and a working customer starts
    /// getting refused with no deploy having happened.</para>
    /// </summary>
    [Fact]
    public void SeededCurveConstants_FromThe2026Annex_AreCompleteAndContinuous()
    {
        var annexStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bands = new[] { "LOWGREEN", "MEDIUMGREEN", "HIGHGREEN", "PLATINUM" };

        var rules = Zayra.Api.Infrastructure.Seed.StatutoryRuleSeeder.BuildRules()
            .Where(r => r.RuleKey.StartsWith("nitaqat.curve.", StringComparison.Ordinal))
            .Where(r => r.EffectiveFrom >= annexStart)
            .ToList();

        var activities = rules.Select(r => r.RuleKey.Split('.')[2]).Distinct().ToList();
        activities.Should().HaveCount(
            Zayra.Api.Infrastructure.Seed.StatutoryRuleSeeder.Annex2026.Length,
            "every activity in the transcribed annex is loaded, and nothing else is");

        foreach (var activity in activities)
        {
            rules.Should().Contain(r => r.RuleKey == $"nitaqat.curve.{activity}.verified",
                $"{activity} must declare whether its constants were reproduced");

            foreach (var band in bands)
            {
                rules.Where(r => r.RuleKey == $"nitaqat.curve.{activity}.{band}.m")
                    .Should().ContainSingle($"{activity}/{band} needs exactly one gradient");

                // The intercept windows must tile [2026-01-01, forever) with no gap and no
                // overlap: each window starts exactly where the previous one ended, and the last
                // runs open-ended because the guideline applies the third-year value "in the
                // third year and beyond".
                var cs = rules.Where(r => r.RuleKey == $"nitaqat.curve.{activity}.{band}.c")
                    .OrderBy(r => r.EffectiveFrom).ToList();

                cs.Should().NotBeEmpty($"{activity}/{band} needs an intercept");
                cs[0].EffectiveFrom.Should().Be(annexStart);
                for (var i = 0; i < cs.Count - 1; i++)
                    cs[i].EffectiveTo.Should().Be(cs[i + 1].EffectiveFrom,
                        $"{activity}/{band} intercept windows must not leave a hole");
                cs[^1].EffectiveTo.Should().BeNull(
                    $"{activity}/{band}'s last intercept must not expire — the guideline applies "
                    + "the third-year value in the third year and beyond");
            }
        }
    }

    /// <summary>
    /// THE ANTI-INVENTION GUARD FOR CURVES.
    ///
    /// <para><see cref="NitaqatGridImportService.ImportCurveAsync"/> refuses a customer-supplied
    /// curve whose band ladder crosses or leaves 0..100, because MHRSD's published curves do not
    /// and a crossing means a transcription error — usually m and c swapped or two rows
    /// interchanged. Seeded constants go in through a different door and would bypass that gate
    /// entirely, so the same check is applied to them here, at the same workforce sizes.</para>
    ///
    /// <para>This is what stands between a mis-transcribed annex row and a customer being told
    /// they are Green when they are Red.</para>
    /// </summary>
    [Fact]
    public void SeededCurveConstants_FromThe2026Annex_NeverCrossAndStayPercentages()
    {
        var bands = new[]
        {
            NitaqatBands.LowGreen, NitaqatBands.MediumGreen,
            NitaqatBands.HighGreen, NitaqatBands.Platinum,
        };

        var problems = new List<string>();

        foreach (var activity in Zayra.Api.Infrastructure.Seed.StatutoryRuleSeeder.Annex2026)
        foreach (var (year, pick) in new (int, Func<Zayra.Api.Infrastructure.Seed.StatutoryRuleSeeder.AnnexBand, decimal>)[]
                 {
                     (2026, b => b.C2026), (2027, b => b.C2027), (2028, b => b.C2028),
                 })
        foreach (var x in new[] { 6m, 50m, 500m, 3_000m, 20_000m })
        {
            decimal? previous = null;
            string? previousBand = null;

            for (var i = 0; i < bands.Length; i++)
            {
                var band = activity.Bands[i];
                var y = NitaqatCurve.MinimumSaudization(band.M, pick(band), x);

                if (y < 0m || y > 100m)
                    problems.Add($"{activity.Code} {bands[i]} C-{year} at {x:0} workers = {y:0.##}%, not a percentage");

                if (previous is not null && y < previous.Value)
                    problems.Add($"{activity.Code} C-{year} at {x:0} workers: {bands[i]} ({y:0.##}%) "
                               + $"requires LESS than {previousBand} ({previous.Value:0.##}%)");

                previous = y;
                previousBand = bands[i];
            }
        }

        problems.Should().BeEmpty(
            "MHRSD's published curves do not cross; a crossing means the annex was transcribed "
            + "wrongly. Fix the transcription — never relax this check.");
    }

    /// <summary>
    /// One activity's constants may appear under two catalogue codes only where they are two
    /// names for the same annex row, and then they must be IDENTICAL. Anything else means a row
    /// was pasted onto the wrong activity.
    /// </summary>
    [Fact]
    public void SeededCurveConstants_FromThe2026Annex_AgreeWhereverAnnexRowsAreShared()
    {
        foreach (var group in Zayra.Api.Infrastructure.Seed.StatutoryRuleSeeder.Annex2026
                     .GroupBy(a => a.AnnexName)
                     .Where(g => g.Count() > 1))
        {
            var first = group.First();
            foreach (var other in group.Skip(1))
                other.Bands.Should().BeEquivalentTo(first.Bands,
                    $"'{group.Key}' is one annex row, so {other.Code} and {first.Code} must carry "
                    + "the same constants");
        }
    }

    // ── The curve loader — the path the UI actually points a customer at ─────

    /// <summary>
    /// End to end on the current regime: a customer loads their activity's curve constants from
    /// the MHRSD annex and the establishment is banded by the curve, not the obsolete grid.
    /// Uses MHRSD's own Manufacturing constants so the expected floors are the Ministry's.
    /// </summary>
    [Fact]
    public async Task CurveImport_LoadedByACustomer_BandsTheEstablishmentOffTheCurve()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        db.NitaqatEstablishmentProfiles.Add(new NitaqatEstablishmentProfile
        {
            TenantId = TenantA, CompanyId = CompanyA, ActivityCode = "CONSTRUCTION", IsActive = true,
        });
        for (var i = 0; i < 140; i++) AddEmployee(db, TenantA, CompanyA, "Saudi",  "FullTime", 9_000m);
        for (var i = 0; i < 260; i++) AddEmployee(db, TenantA, CompanyA, "Indian", "FullTime", 4_500m);
        await db.SaveChangesAsync();

        var r = await Grid(db).ImportCurveAsync(TenantA, CurveRequest("CONSTRUCTION"), null);

        r.Ok.Should().BeTrue(r.Error + " " + string.Join("; ", r.Rejections));
        r.RowsInserted.Should().Be(9, "four bands × (m, c), plus the verification flag");
        // The result echoes the curve at a recognisable headcount, so a transcription error that
        // passed validation is still visible to the person who loaded it.
        r.Message.Should().Contain("At 100 total workers");

        // Written tenant-scoped, never as a platform default.
        var written = await db.StatutoryRules.Where(x => x.RuleKey.StartsWith("nitaqat.curve.")).ToListAsync();
        // Eight coefficients (m and c for each of the four non-Red bands) plus the verification
        // flag that travels with them — NitaqatCurve.ResolveAsync treats an absent flag as
        // unverified, so the loader has to write it or every customer-loaded curve would be
        // silently downgraded to provisional.
        written.Should().HaveCount(9);
        written.Should().ContainSingle(x => x.RuleKey == NitaqatCurve.VerifiedKey("CONSTRUCTION"));
        written.Should().OnlyContain(x => x.TenantId == TenantA);
        written.Should().OnlyContain(x => x.Description.Contains("VERIFIED."));

        // And the band now comes from the curve. 400 workers, 35.00% Saudi, MHRSD's Manufacturing
        // C-2023 constants ⇒ High Green (floor 34.93%), exactly as the guideline's example.
        var svc = new NitaqatCalculationService(db, new DbStatutoryRuleReader(db));
        var s = (await svc.GetStandingAsync(TenantA, CompanyA, new DateOnly(2023, 6, 1))).Standing!;

        s.AchievedPercent.Should().Be(35.00m);
        s.Band.Should().Be(NitaqatBands.HighGreen);
        s.CurrentBandFloorPercent.Should().Be(34.93m);
        s.BandingMethod.Should().Be(NitaqatBandingMethods.Curve);
    }

    [Fact]
    public async Task CurveImport_RefusesAnUnsourcedLoad()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        var r = await Grid(db).ImportCurveAsync(TenantA, CurveRequest("CONSTRUCTION", source: "MHRSD"), null);

        r.Ok.Should().BeFalse();
        r.Error.Should().Be("source_note_required");
        (await db.StatutoryRules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CurveImport_RefusesAPartialLadder()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        var r = await Grid(db).ImportCurveAsync(TenantA, CurveRequest("CONSTRUCTION", bands: new[]
        {
            new NitaqatCurveBandInput(NitaqatBands.LowGreen, 1.68m, 12.08m),
            new NitaqatCurveBandInput(NitaqatBands.MediumGreen, 1.87m, 18.87m),
            // HighGreen and Platinum missing.
        }), null);

        r.Ok.Should().BeFalse();
        r.Error.Should().Be("curve_rejected");
        r.Rejections.Should().HaveCount(2);
        r.Rejections.Should().Contain(x => x.Contains("HighGreen"));
        r.Rejections.Should().Contain(x => x.Contains("Platinum"));
        (await db.StatutoryRules.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// A curve ladder can CROSS even when each row looks sane in isolation: a steeper gradient on
    /// a lower band overtakes a higher band at some headcount. MHRSD's published curves do not
    /// cross, so a crossing means a transcription slip — most often m and c swapped. The grid
    /// loader's flat monotonicity check cannot catch this, which is why the curve gets its own.
    /// </summary>
    [Fact]
    public async Task CurveImport_RefusesALadderThatCrossesAtSomeHeadcount()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        // At 6 workers Low Green = 0.5·ln6 + 20 = 20.90 and Medium Green = 5·ln6 + 8 = 16.96 —
        // Medium Green is BELOW Low Green. They only cross higher up. Sane-looking rows, broken
        // ladder.
        var r = await Grid(db).ImportCurveAsync(TenantA, CurveRequest("CONSTRUCTION", bands: new[]
        {
            new NitaqatCurveBandInput(NitaqatBands.LowGreen,    0.50m, 20m),
            new NitaqatCurveBandInput(NitaqatBands.MediumGreen, 5.00m,  8m),
            new NitaqatCurveBandInput(NitaqatBands.HighGreen,   5.00m, 20m),
            new NitaqatCurveBandInput(NitaqatBands.Platinum,    5.00m, 30m),
        }), null);

        r.Ok.Should().BeFalse();
        r.Error.Should().Be("curve_rejected");
        r.Rejections.Should().Contain(x => x.Contains("must not cross"));
        (await db.StatutoryRules.CountAsync()).Should().Be(0);
    }

    /// <summary>A reissue closes the prior rows rather than rewriting them.</summary>
    [Fact]
    public async Task CurveImport_SupersedesRatherThanRewritingHistory()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        await Grid(db).ImportCurveAsync(TenantA,
            CurveRequest("CONSTRUCTION", from: new DateOnly(2024, 1, 1)), null);
        var reissue = await Grid(db).ImportCurveAsync(TenantA,
            CurveRequest("CONSTRUCTION", from: new DateOnly(2026, 1, 1)), null);

        reissue.Ok.Should().BeTrue();
        reissue.RowsInserted.Should().Be(9);
        reissue.RowsSuperseded.Should().Be(9);

        var lowGreenM = await db.StatutoryRules
            .Where(x => x.RuleKey == NitaqatCurve.GradientKey("CONSTRUCTION", NitaqatBands.LowGreen))
            .OrderBy(x => x.EffectiveFrom).ToListAsync();

        lowGreenM.Should().HaveCount(2, "the old row is retained, closed — not overwritten");
        lowGreenM[0].EffectiveTo.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        lowGreenM[1].EffectiveTo.Should().BeNull();
    }

    [Fact]
    public async Task GridImport_RefusesAnUnsourcedLoad()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        var r = await Grid(db).ImportAsync(TenantA, Request("CONSTRUCTION", source: "MHRSD",
            cells: new[] { new NitaqatGridCellInput("MediumC", 12m, 18m, 25m, 35m) }), null);

        r.Ok.Should().BeFalse();
        r.Error.Should().Be("source_note_required");
        (await db.NitaqatBandThresholds.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// The single most likely data-entry error: two columns transposed. It must not be
    /// loadable, because ResolveBand walks the ladder best-first and would hand back a band
    /// the establishment has not earned.
    /// </summary>
    [Fact]
    public async Task GridImport_RefusesANonMonotonicLadder()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        var r = await Grid(db).ImportAsync(TenantA, Request("CONSTRUCTION",
            cells: new[] { new NitaqatGridCellInput("MediumC", LowGreen: 12m, MediumGreen: 25m, HighGreen: 18m, Platinum: 35m) }), null);

        r.Ok.Should().BeFalse();
        r.Error.Should().Be("grid_rejected");
        r.Rejections.Should().ContainSingle()
            .Which.Should().Contain("requires LESS Saudization").And.Contain("transposed");
        (await db.NitaqatBandThresholds.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GridImport_RefusesAPercentageOutsideZeroToOneHundred()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        // 0.18 is the classic ratio-for-percentage slip; 118 is a fat finger.
        var r = await Grid(db).ImportAsync(TenantA, Request("CONSTRUCTION",
            cells: new[] { new NitaqatGridCellInput("MediumC", 12m, 18m, 25m, 118m) }), null);

        r.Ok.Should().BeFalse();
        r.Rejections.Should().ContainSingle().Which.Should().Contain("118");
        (await db.NitaqatBandThresholds.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GridImport_RefusesATierWithNoLowGreenFloor()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        var r = await Grid(db).ImportAsync(TenantA, Request("CONSTRUCTION",
            cells: new[] { new NitaqatGridCellInput("MediumC", LowGreen: null, MediumGreen: 18m, HighGreen: 25m, Platinum: 35m) }), null);

        r.Ok.Should().BeFalse();
        r.Rejections.Should().ContainSingle().Which.Should().Contain("Low Green");
        (await db.NitaqatBandThresholds.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// All-or-nothing. A half-loaded grid bands an establishment off an incomplete ladder,
    /// which is a wrong answer rather than a missing one.
    /// </summary>
    [Fact]
    public async Task GridImport_WritesNothingWhenAnyCellIsRejected()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        var r = await Grid(db).ImportAsync(TenantA, Request("CONSTRUCTION", cells: new[]
        {
            new NitaqatGridCellInput("SmallB",  10m, 16m, 22m, 30m),   // fine
            new NitaqatGridCellInput("MediumC", 12m, 18m, 25m, 200m),  // bad
        }), null);

        r.Ok.Should().BeFalse();
        r.RowsInserted.Should().Be(0);
        (await db.NitaqatBandThresholds.CountAsync()).Should().Be(0,
            "the good cell must not land either — a partial ladder is a wrong answer");
    }

    [Fact]
    public async Task GridImport_RefusesToWriteRealFiguresOntoTheIllustrativeActivity()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        var r = await Grid(db).ImportAsync(TenantA, Request("GENERAL_UNVERIFIED",
            cells: new[] { new NitaqatGridCellInput("MediumC", 12m, 18m, 25m, 35m) }), null);

        r.Ok.Should().BeFalse();
        r.Error.Should().Be("activity_is_illustrative");
    }

    /// <summary>
    /// MHRSD reissues the grid. A reissue must not retroactively restate what the
    /// establishment's band was under the old grid — the prior row is CLOSED at the new
    /// effective date, never mutated and never deleted, so a snapshot taken last quarter
    /// stays explicable.
    /// </summary>
    [Fact]
    public async Task GridImport_SupersedesRatherThanRewritingHistory()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        await Grid(db).ImportAsync(TenantA, Request("CONSTRUCTION", from: new DateOnly(2025, 1, 1),
            cells: new[] { new NitaqatGridCellInput("MediumC", 10m, 16m, 22m, 30m) }), null);

        var reissue = await Grid(db).ImportAsync(TenantA, Request("CONSTRUCTION", from: new DateOnly(2026, 1, 1),
            cells: new[] { new NitaqatGridCellInput("MediumC", 12m, 18m, 25m, 35m) }), null);

        reissue.Ok.Should().BeTrue();
        reissue.RowsInserted.Should().Be(4);
        reissue.RowsSuperseded.Should().Be(4);

        var lowGreen = await db.NitaqatBandThresholds
            .Where(t => t.Band == NitaqatBands.LowGreen)
            .OrderBy(t => t.EffectiveFrom).ToListAsync();

        lowGreen.Should().HaveCount(2, "the old row is retained, closed — not overwritten");
        lowGreen[0].MinSaudizationPercent.Should().Be(10m);
        lowGreen[0].EffectiveTo.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        lowGreen[1].MinSaudizationPercent.Should().Be(12m);
        lowGreen[1].EffectiveTo.Should().BeNull();

        // And the closed period still reads the OLD figure, which is the whole point.
        var old2025 = await Grid(db).GetCoverageAsync(TenantA, new DateOnly(2025, 6, 1));
        old2025.Activities.Single(a => a.ActivityCode == "CONSTRUCTION")
            .SizeTiersCovered.Should().Be(1);
    }

    /// <summary>Re-importing the same effective date is a correction, not a second regime.</summary>
    [Fact]
    public async Task GridImport_SameEffectiveDateCorrectsInPlace()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        var d = new DateOnly(2026, 1, 1);
        await Grid(db).ImportAsync(TenantA, Request("CONSTRUCTION", from: d,
            cells: new[] { new NitaqatGridCellInput("MediumC", 12m, 18m, 25m, 35m) }), null);

        var fix = await Grid(db).ImportAsync(TenantA, Request("CONSTRUCTION", from: d,
            cells: new[] { new NitaqatGridCellInput("MediumC", 13m, 18m, 25m, 35m) }), null);

        fix.Ok.Should().BeTrue();
        fix.RowsInserted.Should().Be(0);
        fix.RowsUpdatedInPlace.Should().Be(4);
        (await db.NitaqatBandThresholds.CountAsync(t => t.Band == NitaqatBands.LowGreen)).Should().Be(1);
        (await db.NitaqatBandThresholds.SingleAsync(t => t.Band == NitaqatBands.LowGreen))
            .MinSaudizationPercent.Should().Be(13m);
    }

    /// <summary>
    /// One customer's reading of the MHRSD table must never become every customer's. Imported
    /// rows are tenant-scoped, never platform-null.
    /// </summary>
    [Fact]
    public async Task GridImport_IsTenantScoped_NeverAPlatformDefault()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        await db.SaveChangesAsync();

        await Grid(db).ImportAsync(TenantA, Request("CONSTRUCTION",
            cells: new[] { new NitaqatGridCellInput("MediumC", 12m, 18m, 25m, 35m) }), null);

        var written = await db.NitaqatBandThresholds.Where(t => t.ActivityCode == "CONSTRUCTION").ToListAsync();
        written.Should().HaveCount(4);
        written.Should().OnlyContain(t => t.TenantId == TenantA);
        written.Should().NotContain(t => t.TenantId == null);

        // Tenant B sees nothing: no grid, and the loud notice.
        var otherTenant = await Grid(db).GetCoverageAsync(TenantB, AsOf);
        otherTenant.ActivitiesWithCompleteGrid.Should().Be(0);
        otherTenant.ConfigurationRequiredNotice.Should().Be(NitaqatGridImportService.NoticeNoGrid);
    }

    /// <summary>
    /// An UNVERIFIED load is usable but labelled. Provisional data must never pass for fact —
    /// that is the same contract the seeder's rows carry.
    /// </summary>
    [Fact]
    public async Task GridImport_UnverifiedGridIsUsableButSaysSoOnTheAnswer()
    {
        await using var db = NewDb();
        SeedNitaqatReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        db.NitaqatEstablishmentProfiles.Add(new NitaqatEstablishmentProfile
        {
            TenantId = TenantA, CompanyId = CompanyA, ActivityCode = "CONSTRUCTION", IsActive = true,
        });
        for (var i = 0; i < 60; i++)  AddEmployee(db, TenantA, CompanyA, "Saudi",  "FullTime", 9_000m);
        for (var i = 0; i < 140; i++) AddEmployee(db, TenantA, CompanyA, "Indian", "FullTime", 4_500m);
        await db.SaveChangesAsync();

        await Grid(db).ImportAsync(TenantA, Request("CONSTRUCTION", verified: false,
            cells: new[] { new NitaqatGridCellInput("MediumC", 12m, 18m, 25m, 35m) }), null);

        var standing = (await Nitaqat(db).GetStandingAsync(TenantA, CompanyA, AsOf)).Standing!;

        standing.AchievedPercent.Should().Be(30m);          // 60/200
        standing.Band.Should().Be(NitaqatBands.HighGreen);  // ≥ 25, < 35
        standing.AllInputsVerified.Should().BeFalse();
        standing.UnverifiedInputs.Should().Contain(x => x.Contains("HighGreen threshold"));

        var coverage = await Grid(db).GetCoverageAsync(TenantA, AsOf);
        coverage.ActivitiesFullyVerified.Should().Be(0);
        coverage.ConfigurationRequiredNotice.Should().NotBeNull(
            "an unverified grid is still something the screen must disclose");
    }

    /// <summary>
    /// The loader must not weaken the seeder's honesty contract: it adds no platform rows, so
    /// a fresh install still refuses every real activity until someone loads a grid.
    /// </summary>
    [Fact]
    public async Task GridLoader_DoesNotSeedAnyPlatformThresholds()
    {
        await using var db = NewDb();
        await NitaqatReferenceSeeder.SeedAsync(db, NullLogger.Instance);

        var platform = await db.NitaqatBandThresholds.Where(t => t.TenantId == null).ToListAsync();
        platform.Select(t => t.ActivityCode).Distinct().Should().BeEquivalentTo(new[] { "GENERAL_UNVERIFIED" });

        var coverage = await Grid(db).GetCoverageAsync(TenantA, AsOf);
        coverage.ActivitiesWithAnyGrid.Should().Be(0,
            "the illustrative activity must not be counted as coverage");
        coverage.ActivitiesTotal.Should().BeGreaterThan(5);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HOLE 2 — Qiwa ran its sandbox mock while claiming to be live
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SandboxAdapter_DeclaresItselfASimulationAndSaysSoInEveryPayload()
    {
        var sandbox = new SandboxQiwaApiAdapter(NullLogger<SandboxQiwaApiAdapter>.Instance);

        sandbox.IsLiveIntegration.Should().BeFalse();
        sandbox.AdapterName.Should().Be("sandbox");

        var ok = await sandbox.PushEmployeeAsync("t", FullPayload(), Guid.NewGuid(), default);
        ok.Success.Should().BeTrue();
        ok.RawResponse.Should().Contain("\"simulated\":true")
                      .And.Contain("\"filed_with_qiwa\":false")
                      .And.NotContain("\"status\":\"synced\"",
                          "a stored response payload is evidence a customer may be asked to produce");

        var rejected = await sandbox.PushEmployeeAsync("t",
            FullPayload() with { IdNumber = "" }, Guid.NewGuid(), default);
        rejected.Success.Should().BeFalse();
        rejected.RawResponse.Should().Contain("\"simulated\":true");
    }

    /// <summary>
    /// The defect itself: the pipeline completing against a mock is not a filing. Before this,
    /// the employee read "synced" and the tenant connection read "Connected" without a byte
    /// leaving the process.
    /// </summary>
    [Fact]
    public void SyncOutcome_UnderASimulator_IsSimulatedNotSynced()
    {
        // The worker's honesty gate in one line, so the vocabulary cannot drift apart from it.
        var live = new StubAdapter(isLive: true);
        var mock = new StubAdapter(isLive: false);

        Outcome(live).Should().Be(QiwaSyncStatuses.Synced);
        Outcome(mock).Should().Be(QiwaSyncStatuses.Simulated);

        ConnectionState(live).Should().Be(QiwaConnectionStatuses.Connected);
        ConnectionState(mock).Should().Be(QiwaConnectionStatuses.Simulated);

        QiwaSyncStatuses.IsRealFiling(QiwaSyncStatuses.Synced).Should().BeTrue();
        QiwaSyncStatuses.IsRealFiling(QiwaSyncStatuses.Simulated).Should().BeFalse();

        static string Outcome(IQiwaApiAdapter a) =>
            a.IsLiveIntegration ? QiwaSyncStatuses.Synced : QiwaSyncStatuses.Simulated;
        static string ConnectionState(IQiwaApiAdapter a) =>
            a.IsLiveIntegration ? QiwaConnectionStatuses.Connected : QiwaConnectionStatuses.Simulated;
    }

    /// <summary>
    /// Any adapter that has not explicitly declared itself live is a simulation. Opting IN is a
    /// deliberate act; the safe default is the honest one.
    /// </summary>
    [Fact]
    public void AnAdapterThatDoesNotDeclareItselfLive_IsTreatedAsASimulation()
    {
        IQiwaApiAdapter silent = new SilentAdapter();
        silent.IsLiveIntegration.Should().BeFalse();
    }

    [Fact]
    public void LiveAdapterIsTheOnlyOneThatClaimsToFile()
    {
        var types = typeof(IQiwaApiAdapter).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IQiwaApiAdapter).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        types.Should().Contain("LiveQiwaApiAdapter").And.Contain("SandboxQiwaApiAdapter");
        // Non-vacuous: the production assembly ships exactly these two.
        types.Should().HaveCount(2);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HOLE 3 — the WPS/SIF file has never been accepted by a real gateway
    //
    //  This CANNOT be closed by engineering. What is pinned here is the honesty of
    //  the claim, and the fact that the generated bytes did not change.
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The brief asked: if there is a published specification, validate the byte layout against it
    /// and report the conformance level precisely. That was done on 2026-09-20 against the MHRSD
    /// "WPS Wages File Specification" (hrsd.gov.sa, 21pp, MD5 392f62cbb9a3eec39c7137d0974c988a).
    /// The precise answer is that the layout does NOT conform, and this test pins the product to
    /// saying so rather than to a softer "unverified".
    /// </summary>
    [Fact]
    public void WpsConformance_ReportsTheRealResultOfCheckingAgainstThePublishedSpecification()
    {
        var c = WpsConformance.For(null);

        c.CheckedAgainstPublishedSpecification.Should().BeTrue();
        c.SpecificationUrl.Should().StartWith("https://www.hrsd.gov.sa/");
        c.SpecificationCheckedOn.Should().Be("2026-09-20");
        c.SpecificationChecked.Should().Contain("Wages Protection System");

        c.AcceptedByLiveGateway.Should().BeFalse(
            "no file from this generator has been submitted to Mudad, MHRSD or any Saudi bank");
        c.Level.Should().Be(WpsConformance.Levels.ProprietaryExportNotWpsConformant);
        c.FormatVersion.Should().Be(SifFileGenerator.FormatVersion);

        // The headline must not be readable as a WPS claim.
        c.Headline.Should().Contain("NOT a Saudi WPS file");

        // Non-vacuous: both lists carry real, specific, checkable content.
        c.Verified.Should().HaveCountGreaterThan(3);
        c.Verified.Should().Contain(x => x.Contains("mod-97"));
        c.Verified.Should().Contain(x => x.Contains("deterministic"));

        c.NotVerified.Should().HaveCountGreaterThan(5);
        // Each of the five concrete non-conformances found against the spec.
        c.NotVerified.Should().Contain(x => x.Contains("TAB-delimited"));
        c.NotVerified.Should().Contain(x => x.Contains("EDI_DC40") && x.Contains("SAP IDoc"));
        c.NotVerified.Should().Contain(x => x.Contains("MOL-BAS"));
        c.NotVerified.Should().Contain(x => x.Contains("COMMA decimal separator"));
        c.NotVerified.Should().Contain(x => x.Contains("signed by the BANK"),
            "the structural reason no layout fix would help must be stated");

        // The remedy is procurement, and says so, with both real routes named.
        c.RemediationOwner.Should().Contain("engineering cannot close this");
        c.Remediation.Should().Contain("PER-BANK PAYROLL INSTRUCTION");
        c.Remediation.Should().Contain("MUDAD PMS PARTNER INTEGRATION");
    }

    [Fact]
    public void WpsConformance_DownloadHeaderCarriesTheClaimWithTheBytes()
    {
        var v = WpsConformance.HeaderValue("SIF_SA_V1");

        v.Should().Contain("accepted-by-live-gateway=false");
        v.Should().Contain("wps-conformant=false");
        v.Should().Contain("format=SIF_SA_V1");
        v.Should().Contain("does NOT conform");
    }

    /// <summary>
    /// The attestation rides beside the artefact, never inside it: the file is content-addressed
    /// by SHA-256 and a gateway would reject an unexpected line. This pins the bytes.
    /// </summary>
    [Fact]
    public void SifBytes_AreUnchangedByTheConformanceWork()
    {
        var batch = new PayrollPaymentBatch { TotalAmount = 3_000m };
        var records = new List<SIFFileRecord>
        {
            new() { EmployeeCode = "E001", Iban = "SA0380000000608010167519", NetPay = 1_000m, MolId = "M1", RoutingCode = "R1" },
            new() { EmployeeCode = "E002", Iban = "SA0380000000608010167519", NetPay = 2_000m, MolId = "M2", RoutingCode = "R2" },
        };

        var a = SifFileGenerator.Generate(batch, records, "AG1", "MOL1", "SAR", new DateTime(2026, 6, 1));
        var b = SifFileGenerator.Generate(batch, records, "AG1", "MOL1", "SAR", new DateTime(2026, 6, 1));

        a.FileHash.Should().Be(b.FileHash, "generation must stay deterministic");
        a.Content.Should().StartWith("EDI_DC40+");
        a.Content.Should().NotContain("conformance",
            "the statement must not leak into the bytes a gateway would parse");
        a.Content.Should().NotContain("UNVERIFIED");
        a.TotalSalaryAmount.Should().Be(3_000m);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HOLE 4 — the compliance surface's GOSI figure disagreed with the payslip
    //
    //  The root cause (the seeder never setting the ceiling) is another stream's.
    //  MY half: the readiness report read the ceiling from a DIFFERENT SOURCE than
    //  the payslip, so populating one would never have fixed the other.
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ── THE WORKED EXAMPLE ───────────────────────────────────────────────────
    /// A Saudi national on SAR 45,000 basic + SAR 15,000 housing = SAR 60,000 covered wage.
    /// Employee GOSI = 9% Annuities + 0.75% SANED = 9.75%.
    ///
    ///   Payslip (country pack, capped at the statutory 45,000):  4,387.50
    ///   Readiness report BEFORE (uncapped, 60,000):              5,850.00
    ///
    /// The platform GOSI rule seeder does not set MaxContributoryWage, so
    /// GosiCalculationService had nothing to cap with. This test uses that real seeder
    /// unmodified — the fix must hold WITHOUT the other stream's seeder change.
    /// </summary>
    [Fact]
    public async Task ReadinessReport_CapsAtTheSameCeilingThePayslipUses()
    {
        await using var db = NewDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        // Prove the premise rather than assuming it: the rule table really has no ceiling.
        (await db.GosiContributionRules.IgnoreQueryFilters()
            .Where(r => r.Classification == GosiClassifications.Saudi)
            .AllAsync(r => r.MaxContributoryWage == null))
            .Should().BeTrue("this is the seeder gap the other stream owns; the fix must not depend on it");

        db.Employees.Add(new Employee
        {
            Id = 1, TenantId = TenantA, EmployeeCode = "EMP00001", FullName = "Test Saudi",
            Nationality = "Saudi", Status = "Active", GosiReference = "SA001",
        });
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = TenantA, EmployeeId = 1,
            BasicSalary = 45_000m, HousingAllowance = 15_000m,
            EffectiveDate = new DateOnly(2024, 1, 1),
        });
        await db.SaveChangesAsync();

        var report = await TestReconciliation.GosiReadiness(db).BuildAsync(TenantA, CancellationToken.None);
        var emp = report.Employees.Single();

        emp.IsReady.Should().BeTrue();
        emp.EmployeeContributionTotal.Should().Be(4_387.50m,
            "45,000 × 9.75% — the capped figure the payslip deducts");
        emp.EmployeeContributionTotal.Should().NotBe(5_850.00m,
            "60,000 × 9.75% is the uncapped figure this report used to show against the payslip");

        // Employer: 9% + 0.75% + 2% occupational hazards = 11.75% of 45,000.
        emp.EmployerContributionTotal.Should().Be(5_287.50m);

        // The surface says WHY the number is smaller than the salary implies.
        report.ContributoryWageCeiling.Should().Be(45_000m);
        report.EmployeesAtWageCeiling.Should().Be(1);
        report.ContributoryWageBasis.Should().Contain("gosi.covered_wage_ceiling_sar");
    }

    /// <summary>
    /// The anti-drift device. The readiness report and the payslip must resolve the SAME ceiling
    /// from the SAME key; if anyone changes one, this fails rather than the two silently parting.
    /// </summary>
    [Fact]
    public async Task GosiCoveredWageCeiling_IsOneNumberFromOnePlace()
    {
        var reader = TestReconciliation.KsaRuleReader();
        var asOf = new DateOnly(2026, 6, 1);

        // What the compliance surface will use.
        var complianceCeiling = await GosiContributoryWageBasis.CeilingAsync(reader, asOf);

        // What the payslip actually deducts on, observed through the pack rather than asserted.
        var pack = new KsaDeductionCalculator(reader);
        var overCeiling = await pack.CalculateAsync(new StatutoryDeductionInput(
            EmployeeId: Guid.Empty, CompanyId: Guid.Empty,
            Salary: new SalaryBreakdown(45_000m, 15_000m, 0m, 0m),   // 60,000 covered wage
            Nationality: "Saudi", ContractType: "Indefinite",
            PeriodYear: 2026, PeriodMonth: 6));

        var atCeiling = await pack.CalculateAsync(new StatutoryDeductionInput(
            EmployeeId: Guid.Empty, CompanyId: Guid.Empty,
            Salary: new SalaryBreakdown(complianceCeiling, 0m, 0m, 0m),
            Nationality: "Saudi", ContractType: "Indefinite",
            PeriodYear: 2026, PeriodMonth: 6));

        overCeiling.TotalEmployeeDeduction.Should().Be(atCeiling.TotalEmployeeDeduction,
            "the pack caps at exactly the ceiling the compliance surface resolves");
        overCeiling.TotalEmployeeDeduction.Should().Be(4_387.50m);

        // And the named fallback matches the pack's own literal, for a deployment with no rule row.
        GosiContributoryWageBasis.DefaultCoveredWageCeilingSar.Should().Be(45_000m);
        GosiContributoryWageBasis.CeilingRuleKey.Should().Be("gosi.covered_wage_ceiling_sar");
    }

    /// <summary>The base is also one definition, not two. The report used to inline its own.</summary>
    [Fact]
    public void CoveredWageBase_DelegatesToTheOneSalaryBreakdownDefinition()
    {
        GosiContributoryWageBasis.CoveredWage(10_000m, 2_500m).Should().Be(12_500m);
        GosiContributoryWageBasis.CoveredWage(10_000m, 2_500m)
            .Should().Be(new SalaryBreakdown(10_000m, 2_500m, 900m, 500m).GosiCoveredWage,
                "transport and other allowances are not part of the GOSI covered wage");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Fixtures
    // ═════════════════════════════════════════════════════════════════════════

    private static NitaqatGridImportService Grid(ZayraDbContext db) => new(db);

    private static NitaqatCalculationService Nitaqat(ZayraDbContext db) =>
        new(db, new WageFloorReader());

    private static NitaqatGridImportRequest Request(
        string activity,
        string? source = null,
        bool verified = true,
        DateOnly? from = null,
        IReadOnlyList<NitaqatGridCellInput>? cells = null) =>
        new(activity,
            from ?? new DateOnly(2026, 1, 1),
            source ?? "MHRSD Nitaqat table read from the establishment's Qiwa account on 2026-09-14.",
            verified,
            cells ?? Array.Empty<NitaqatGridCellInput>());

    private static QiwaEmployeePayload FullPayload() => new(
        "E001", "1234567890", "Iqama", "Indian", "NonSaudi", "2512", "EST1", "LOC1", "C1");

    private sealed class StubAdapter : IQiwaApiAdapter
    {
        private readonly bool _live;
        public StubAdapter(bool isLive) => _live = isLive;
        public string AdapterName => _live ? "stub-live" : "stub-mock";
        public bool IsLiveIntegration => _live;
        public Task<QiwaApiResult> PushEmployeeAsync(string t, QiwaEmployeePayload p, Guid k, CancellationToken ct)
            => Task.FromResult(new QiwaApiResult(true, null, null, "{}"));
        public Task<QiwaApiResult> GetEmployeeStatusAsync(string t, string e, string i, CancellationToken ct)
            => Task.FromResult(new QiwaApiResult(true, null, null, "{}"));
        public Task<string?> AcquireAccessTokenAsync(string c, string s, string e, CancellationToken ct)
            => Task.FromResult<string?>("tok");
    }

    /// <summary>An adapter that never mentions liveness — must be read as a simulation.</summary>
    private sealed class SilentAdapter : IQiwaApiAdapter
    {
        public string AdapterName => "silent";
        public Task<QiwaApiResult> PushEmployeeAsync(string t, QiwaEmployeePayload p, Guid k, CancellationToken ct)
            => Task.FromResult(new QiwaApiResult(true, null, null, "{}"));
        public Task<QiwaApiResult> GetEmployeeStatusAsync(string t, string e, string i, CancellationToken ct)
            => Task.FromResult(new QiwaApiResult(true, null, null, "{}"));
        public Task<string?> AcquireAccessTokenAsync(string c, string s, string e, CancellationToken ct)
            => Task.FromResult<string?>("tok");
    }

    /// <summary>
    /// A curve load carrying MHRSD's own Manufacturing constants (C-2023), so a test that loads a
    /// curve and then asserts a band is asserting the Ministry's numbers.
    /// </summary>
    private static NitaqatCurveImportRequest CurveRequest(
        string activity,
        string? source = null,
        DateOnly? from = null,
        DateOnly? to = null,
        bool verified = true,
        IReadOnlyList<NitaqatCurveBandInput>? bands = null) =>
        new(activity,
            from ?? new DateOnly(2023, 1, 1),
            to,
            source ?? "MHRSD Nitaqat Mutawar procedural guide, Annex 1, read from hrsd.gov.sa on 2026-09-20.",
            verified,
            bands ?? new[]
            {
                new NitaqatCurveBandInput(NitaqatBands.LowGreen,    1.68m, 12.08m),
                new NitaqatCurveBandInput(NitaqatBands.MediumGreen, 1.87m, 18.87m),
                new NitaqatCurveBandInput(NitaqatBands.HighGreen,   2.08m, 22.47m),
                new NitaqatCurveBandInput(NitaqatBands.Platinum,    2.08m, 28.37m),
            });

    /// <summary>
    /// Reads statutory rules out of the test database the way production does — tenant row first,
    /// then platform default — so a curve written by the loader is read back by the calculator
    /// through the real path rather than a stub.
    /// </summary>
    private sealed class DbStatutoryRuleReader : IStatutoryRuleReader
    {
        private readonly ZayraDbContext _db;
        public DbStatutoryRuleReader(ZayraDbContext db) => _db = db;

        public async Task<decimal?> GetDecimalAsync(
            string cc, string j, string key, DateOnly asOf, Guid? tenantId = null, CancellationToken ct = default)
        {
            // The Nitaqat wage floors are not seeded in these fixtures; the calculator's own
            // defaults (4,000 / 3,000) apply when this returns null, which matches production.
            var d = asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var rows = await _db.StatutoryRules.IgnoreQueryFilters()
                .Where(r => r.CountryCode == cc && r.Jurisdiction == j && r.RuleKey == key
                         && (r.TenantId == null || r.TenantId == tenantId)
                         && r.EffectiveFrom <= d && (r.EffectiveTo == null || r.EffectiveTo > d))
                .ToListAsync(ct);

            var row = rows.OrderByDescending(r => r.TenantId != null)
                          .ThenByDescending(r => r.EffectiveFrom)
                          .FirstOrDefault();

            return row is null
                ? null
                : decimal.Parse(row.RuleValue, System.Globalization.CultureInfo.InvariantCulture);
        }

        public Task<string?> GetStringAsync(
            string cc, string j, string key, DateOnly asOf, Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    /// <summary>Four floors as threshold rows, for driving ResolveBand directly.</summary>
    private static IReadOnlyList<NitaqatBandThreshold> Floors(
        decimal lowGreen, decimal medium, decimal high, decimal platinum) =>
        NitaqatCurve.ToThresholds(
            new NitaqatCurveFloors(lowGreen, medium, high, platinum, "test", true),
            "MANUFACTURING", "MediumC", new DateOnly(2023, 1, 1));

    /// <summary>A rule reader backed by an explicit key→value map, plus the Nitaqat wage floors.</summary>
    private sealed class DictionaryRuleReader : IStatutoryRuleReader
    {
        private readonly IReadOnlyDictionary<string, decimal> _values;
        public DictionaryRuleReader(IReadOnlyDictionary<string, decimal> values) => _values = values;

        public Task<decimal?> GetDecimalAsync(string cc, string j, string key, DateOnly d, Guid? t = null, CancellationToken ct = default)
            => Task.FromResult(_values.TryGetValue(key, out var v) ? v : (decimal?)null);

        public Task<string?> GetStringAsync(string cc, string j, string key, DateOnly d, Guid? t = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// The Ministry's Manufacturing constants for the year starting Jan 2023, exactly as printed
    /// in the procedural guideline, plus the Nitaqat wage floors the calculator also needs.
    /// </summary>
    private static class ManufacturingCurveReader
    {
        public static IStatutoryRuleReader For2023() => new DictionaryRuleReader(
            new Dictionary<string, decimal>
            {
                [NitaqatCurve.GradientKey("MANUFACTURING", NitaqatBands.LowGreen)]     = 1.68m,
                [NitaqatCurve.GradientKey("MANUFACTURING", NitaqatBands.MediumGreen)]  = 1.87m,
                [NitaqatCurve.GradientKey("MANUFACTURING", NitaqatBands.HighGreen)]    = 2.08m,
                [NitaqatCurve.GradientKey("MANUFACTURING", NitaqatBands.Platinum)]     = 2.08m,
                [NitaqatCurve.InterceptKey("MANUFACTURING", NitaqatBands.LowGreen)]    = 12.08m,
                [NitaqatCurve.InterceptKey("MANUFACTURING", NitaqatBands.MediumGreen)] = 18.87m,
                [NitaqatCurve.InterceptKey("MANUFACTURING", NitaqatBands.HighGreen)]   = 22.47m,
                [NitaqatCurve.InterceptKey("MANUFACTURING", NitaqatBands.Platinum)]    = 28.37m,
                [NitaqatCalculationService.RuleKeyWageFloorFull] = 4_000m,
                [NitaqatCalculationService.RuleKeyWageFloorHalf] = 3_000m,
            });
    }

    private sealed class WageFloorReader : IStatutoryRuleReader
    {
        public Task<decimal?> GetDecimalAsync(string cc, string j, string key, DateOnly d, Guid? t = null, CancellationToken ct = default)
            => Task.FromResult<decimal?>(key switch
            {
                NitaqatCalculationService.RuleKeyWageFloorFull => 4_000m,
                NitaqatCalculationService.RuleKeyWageFloorHalf => 3_000m,
                _ => null,
            });

        public Task<string?> GetStringAsync(string cc, string j, string key, DateOnly d, Guid? t = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private static void SeedCompany(ZayraDbContext db, Guid tenantId, Guid companyId, string country) =>
        db.Companies.Add(new Company
        {
            Id = companyId, TenantId = tenantId, CountryCode = country,
            TradeName = "Riyadh Contracting Co.", LegalNameEn = "Riyadh Contracting Co.", IsActive = true,
        });

    private static int _nextEmployeeId = 100_000;

    private static void AddEmployee(
        ZayraDbContext db, Guid tenantId, Guid companyId, string nationality, string type, decimal salary)
    {
        var id = Interlocked.Increment(ref _nextEmployeeId);
        db.Employees.Add(new Employee
        {
            Id = id, TenantId = tenantId, CompanyId = companyId,
            Nationality = nationality, EmploymentType = type, Salary = salary,
            Status = EmployeeStatuses.Active,
            JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FullName = $"Employee {id}",
        });
    }

    /// <summary>Platform-default tiers, activities and weight rules — the real seeder's shape.</summary>
    private static void SeedNitaqatReference(ZayraDbContext db)
    {
        var tiers = new (string Code, int Min, int? Max, int Rank)[]
        {
            ("VerySmall", 1, 5, 1), ("SmallA", 6, 9, 2), ("SmallB", 10, 24, 3), ("SmallC", 25, 49, 4),
            ("MediumA", 50, 99, 5), ("MediumB", 100, 199, 6), ("MediumC", 200, 499, 7),
            ("Large", 500, 2999, 8), ("Giant", 3000, null, 9),
        };
        foreach (var t in tiers)
            db.NitaqatSizeTiers.Add(new NitaqatSizeTier
            {
                TenantId = null, Code = t.Code, NameEn = t.Code, MinWorkforce = t.Min,
                MaxWorkforce = t.Max, Rank = t.Rank, EffectiveFrom = Eff2021, SourceNote = "Test fixture.",
            });

        db.NitaqatActivities.Add(new NitaqatActivity
        {
            TenantId = null, Code = "CONSTRUCTION", NameEn = "Construction & Contracting",
            ActivityGroup = "Construction", IsActive = true, SourceNote = "NO BAND THRESHOLDS seeded.",
        });
        db.NitaqatActivities.Add(new NitaqatActivity
        {
            TenantId = null, Code = "RETAIL", NameEn = "Retail Trade",
            ActivityGroup = "Wholesale & Retail", IsActive = true, SourceNote = "NO BAND THRESHOLDS seeded.",
        });
        db.NitaqatActivities.Add(new NitaqatActivity
        {
            TenantId = null, Code = "GENERAL_UNVERIFIED", NameEn = "General (illustrative)",
            ActivityGroup = "Illustrative", IsActive = true, SourceNote = "ILLUSTRATIVE ONLY.",
        });

        var weights = new (string Code, string Cls, string Basis, string Cat, decimal Num, decimal Den, int Prec)[]
        {
            ("SAUDI_FULLTIME",    GosiClassifications.Saudi,    NitaqatCountBasis.FullTime, NitaqatWeightCategories.Standard, 1m,   1m,   10),
            ("NONSAUDI_STANDARD", GosiClassifications.NonSaudi, NitaqatCountBasis.Any,      NitaqatWeightCategories.Standard, 0m,   1m,   10),
            ("SAUDI_PARTTIME",    GosiClassifications.Saudi,    NitaqatCountBasis.PartTime, NitaqatWeightCategories.Standard, 0.5m, 0.5m, 20),
        };
        foreach (var w in weights)
            db.NitaqatWeightRules.Add(new NitaqatWeightRule
            {
                TenantId = null, RuleCode = w.Code, Classification = w.Cls, CountBasis = w.Basis,
                Category = w.Cat, NumeratorWeight = w.Num, DenominatorWeight = w.Den,
                Precedence = w.Prec, EffectiveFrom = Eff2021, IsVerified = true, SourceNote = "Test fixture.",
            });
    }
}
