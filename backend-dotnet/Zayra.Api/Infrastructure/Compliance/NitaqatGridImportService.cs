using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compliance;

// ─────────────────────────────────────────────────────────────────────────────
//  Loading the MHRSD Nitaqat grid.
//
//  THE HOLE THIS CLOSES. The Nitaqat engine is complete and wired through every
//  surface, but the twelve real MHRSD economic activities ship with ZERO band
//  thresholds, and the only activity that produces a band is called
//  "General (illustrative — not an MHRSD activity)". A customer who enters their
//  real activity and headcount therefore gets a named refusal
//  (nitaqat_thresholds_not_published) and no band at all, while the marketing
//  site advertises Saudization tracking.
//
//  WHY THE FIX IS A LOADER AND NOT A SEEDED GRID. MHRSD publishes a distinct
//  required percentage per activity per size tier, revises it periodically, and
//  does not publish it as open machine-readable data — the authoritative grid for
//  an establishment is the one visible on its own Qiwa establishment account.
//  Seeding ~3,000 numbers nobody has checked would produce confident wrong
//  answers about a customer's compliance status, which is exactly the failure
//  mode KsaNitaqatTests.Seeder_ShipsNoThresholdsForRealActivities_SoTheyMustRefuse
//  exists to prevent. That test is respected here and not weakened: this service
//  adds no platform-default rows at all.
//
//  So the product ships the MECHANISM: a customer (or an implementation
//  consultant, from the establishment's own Qiwa screen) loads the rows for the
//  activities they actually operate, each row carrying a mandatory source
//  citation and an explicit verification flag, effective-dated so a later MHRSD
//  reissue layers on top rather than rewriting history.
//
//  MULTI-TENANCY. Every imported row is written with TenantId = the importing
//  tenant. Never platform-null. One customer's reading of the MHRSD table must
//  not become every customer's.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One (activity × size tier) cell of the grid as submitted by a caller.</summary>
public sealed record NitaqatGridCellInput(
    string SizeTierCode,
    decimal? LowGreen,
    decimal? MediumGreen,
    decimal? HighGreen,
    decimal? Platinum);

/// <summary>A grid load for one activity, effective from a date, with its source.</summary>
public sealed record NitaqatGridImportRequest(
    string ActivityCode,
    DateOnly EffectiveFrom,
    // Where these numbers came from, verbatim. MANDATORY and length-checked. A statutory
    // threshold with no provenance is a rumour, and the whole Nitaqat model is built on every
    // row being able to say where it came from.
    string SourceNote,
    // True only when the importer has checked these figures against the published MHRSD table or
    // the establishment's own Qiwa screen. False marks them provisional, and every surface that
    // consumes them says so.
    bool IsVerified,
    IReadOnlyList<NitaqatGridCellInput> Cells);

public sealed record NitaqatGridImportResult(
    bool Ok,
    string? Error,
    string? Message,
    int RowsInserted,
    int RowsSuperseded,
    int RowsUpdatedInPlace,
    IReadOnlyList<string> Rejections);

/// <summary>Coverage of one activity: does it have a usable grid, and how much of one.</summary>
public sealed record NitaqatActivityCoverage(
    string ActivityCode,
    string ActivityNameEn,
    string ActivityGroup,
    int SizeTiersCovered,
    int SizeTiersTotal,
    bool IsComplete,
    bool AnyVerified,
    bool AllVerified,
    DateOnly? EffectiveFrom,
    string SourceNote);

public sealed record NitaqatGridCoverageResponse(
    int ActivitiesTotal,
    int ActivitiesWithAnyGrid,
    int ActivitiesWithCompleteGrid,
    int ActivitiesFullyVerified,
    // The blunt product statement for the UI. Non-null whenever the grid is not loaded, so the
    // screen can never quietly imply that Saudization banding is configured when it is not.
    string? ConfigurationRequiredNotice,
    IReadOnlyList<NitaqatActivityCoverage> Activities,
    IReadOnlyList<string> SizeTierCodes);

public sealed class NitaqatGridImportService
{
    private readonly ZayraDbContext _db;

    public NitaqatGridImportService(ZayraDbContext db) => _db = db;

    /// <summary>Minimum length of a usable citation. Short enough to be writable, long enough to exclude "MHRSD".</summary>
    internal const int MinSourceNoteLength = 20;

    internal const string NoticeNoGrid =
        "No MHRSD Nitaqat band thresholds are loaded for any real economic activity, so this "
        + "product cannot compute a Nitaqat band for your establishment. The required Saudization "
        + "percentage is published by MHRSD per activity per size tier and is visible on your own "
        + "establishment's Qiwa account; it is not distributed as open data and is not shipped with "
        + "this product. Load your activity's row under Saudi Compliance → Saudization → Nitaqat "
        + "grid. Until it is loaded, no band is shown — a wrong band is worse than none, because a "
        + "Nitaqat band gates work-visa issuance and Iqama transfer.";

    internal static string NoticePartialGrid(int complete, int total) =>
        $"{complete} of {total} economic activities have a complete MHRSD Nitaqat grid loaded. An "
        + "establishment mapped to any other activity is refused a band rather than shown a guess. "
        + "Load the remaining activities' rows from your Qiwa establishment account.";

    internal static string NoticeUnverified =
        "The loaded Nitaqat grid is marked UNVERIFIED — nobody has confirmed these figures against "
        + "the published MHRSD table. Bands computed from it are provisional and are labelled as "
        + "such wherever they appear.";

    // ── Coverage ──────────────────────────────────────────────────────────────

    /// <summary>
    /// What the product can actually band today. Powers the UI's plain statement that the grid
    /// needs configuring, which is the honest answer while no grid is loaded.
    /// </summary>
    public async Task<NitaqatGridCoverageResponse> GetCoverageAsync(
        Guid tenantId, DateOnly asOf, CancellationToken ct = default)
    {
        var activities = (await ReferenceAsync(_db.NitaqatActivities, tenantId, ct))
            .Where(a => a.IsActive)
            .GroupBy(a => a.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(a => a.TenantId != null).First())
            .OrderBy(a => a.ActivityGroup).ThenBy(a => a.NameEn)
            .ToList();

        var tierCodes = Effective(await ReferenceAsync(_db.NitaqatSizeTiers, tenantId, ct), asOf)
            .GroupBy(t => t.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(t => t.TenantId != null).First())
            .OrderBy(t => t.Rank)
            .Select(t => t.Code)
            .ToList();

        var thresholds = Effective(await ReferenceAsync(_db.NitaqatBandThresholds, tenantId, ct), asOf)
            .ToList();

        var rows = new List<NitaqatActivityCoverage>();

        foreach (var a in activities)
        {
            var mine = thresholds
                .Where(t => string.Equals(t.ActivityCode, a.Code, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // A tier is "covered" only when it has at least a LowGreen floor: without the bottom
            // floor every establishment below the next band reads as Red, which is a wrong answer
            // rather than a missing one.
            var coveredTiers = mine
                .Where(t => string.Equals(t.Band, NitaqatBands.LowGreen, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.SizeTierCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(c => tierCodes.Contains(c, StringComparer.OrdinalIgnoreCase));

            rows.Add(new NitaqatActivityCoverage(
                ActivityCode:     a.Code,
                ActivityNameEn:   a.NameEn,
                ActivityGroup:    a.ActivityGroup,
                SizeTiersCovered: coveredTiers,
                SizeTiersTotal:   tierCodes.Count,
                IsComplete:       tierCodes.Count > 0 && coveredTiers == tierCodes.Count,
                AnyVerified:      mine.Any(t => t.IsVerified),
                AllVerified:      mine.Count > 0 && mine.All(t => t.IsVerified),
                EffectiveFrom:    mine.Count == 0
                    ? null
                    : DateOnly.FromDateTime(mine.Max(t => t.EffectiveFrom)),
                SourceNote:       mine.Count == 0 ? a.SourceNote : mine[0].SourceNote));
        }

        // The illustrative activity is deliberately excluded from the counts below. It exists so
        // the mechanism is demonstrable; counting it as coverage would let the screen claim the
        // grid is configured when the only loaded grid says, in its own name, that it is not real.
        var real = rows
            .Where(r => !string.Equals(r.ActivityCode, IllustrativeActivityCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var withAny      = real.Count(r => r.SizeTiersCovered > 0);
        var withComplete = real.Count(r => r.IsComplete);
        var verified     = real.Count(r => r.IsComplete && r.AllVerified);

        var notice =
            withComplete == 0 ? NoticeNoGrid
            : withComplete < real.Count ? NoticePartialGrid(withComplete, real.Count)
            : verified == 0 ? NoticeUnverified
            : null;

        return new NitaqatGridCoverageResponse(
            ActivitiesTotal:            real.Count,
            ActivitiesWithAnyGrid:      withAny,
            ActivitiesWithCompleteGrid: withComplete,
            ActivitiesFullyVerified:    verified,
            ConfigurationRequiredNotice: notice,
            Activities:                 rows,
            SizeTierCodes:              tierCodes);
    }

    internal const string IllustrativeActivityCode = "GENERAL_UNVERIFIED";

    // ── Import ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads one activity's grid for this tenant, effective from a date.
    ///
    /// <para><b>EFFECTIVE-DATING, NOT OVERWRITING.</b> A row already in force on the new effective
    /// date is CLOSED at that date (EffectiveTo = EffectiveFrom) rather than mutated. MHRSD
    /// reissues the grid, and a reissue must not retroactively restate what the establishment's
    /// band was last quarter — a Nitaqat standing snapshot taken under the old grid has to stay
    /// explicable. Re-importing the SAME effective date updates in place, because that is a
    /// correction to a load rather than a new regime, and the unique index
    /// (TenantId, ActivityCode, SizeTierCode, Band, EffectiveFrom) requires it.</para>
    ///
    /// <para>Validation is all-or-nothing: if any cell is rejected, NOTHING is written. A
    /// half-loaded grid produces a band from an incomplete ladder, which is a wrong answer.</para>
    /// </summary>
    public async Task<NitaqatGridImportResult> ImportAsync(
        Guid tenantId, NitaqatGridImportRequest request, Guid? userId, CancellationToken ct = default)
    {
        var rejections = new List<string>();

        if (string.IsNullOrWhiteSpace(request.ActivityCode))
            return Fail("activity_code_required", "An economic activity code is required.");

        var source = (request.SourceNote ?? string.Empty).Trim();
        if (source.Length < MinSourceNoteLength)
            return Fail("source_note_required",
                "A source citation of at least " + MinSourceNoteLength + " characters is required — "
                + "state where these figures came from and when they were read (for example: "
                + "\"MHRSD Nitaqat table for Retail, read from Qiwa establishment 1-234567 on "
                + "2026-09-14\"). A statutory threshold with no provenance cannot be audited, and "
                + "every other Nitaqat reference row in this system carries one.");

        if (request.Cells is null || request.Cells.Count == 0)
            return Fail("cells_required", "At least one size tier's thresholds must be supplied.");

        // The illustrative activity is a demo fixture, not a place to put real MHRSD figures.
        if (string.Equals(request.ActivityCode, IllustrativeActivityCode, StringComparison.OrdinalIgnoreCase))
            return Fail("activity_is_illustrative",
                $"'{IllustrativeActivityCode}' is the built-in illustrative activity and is not an "
                + "MHRSD activity. Load real thresholds against the real economic activity your "
                + "establishment is registered under.");

        var activities = await ReferenceAsync(_db.NitaqatActivities, tenantId, ct);
        var activity = activities.FirstOrDefault(a => a.IsActive
            && string.Equals(a.Code, request.ActivityCode, StringComparison.OrdinalIgnoreCase));
        if (activity is null)
            return Fail("activity_code_unknown",
                $"'{request.ActivityCode}' is not in the Nitaqat activity catalogue for this tenant.");

        var effFrom = request.EffectiveFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var tiers = Effective(await ReferenceAsync(_db.NitaqatSizeTiers, tenantId, ct), request.EffectiveFrom)
            .GroupBy(t => t.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(t => t.TenantId != null).First())
            .ToDictionary(t => t.Code, StringComparer.OrdinalIgnoreCase);

        if (tiers.Count == 0)
            return Fail("size_tiers_missing",
                "No Nitaqat size tiers are configured effective on that date, so a grid cannot be "
                + "keyed against them.");

        // ── Validate every cell before writing anything ───────────────────────
        var planned = new List<(string Tier, string Band, decimal Percent)>();
        var seenTiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cell in request.Cells)
        {
            if (string.IsNullOrWhiteSpace(cell.SizeTierCode))
            {
                rejections.Add("A cell has no size tier code.");
                continue;
            }

            if (!tiers.ContainsKey(cell.SizeTierCode))
            {
                rejections.Add($"Size tier '{cell.SizeTierCode}' is not configured effective "
                             + $"{request.EffectiveFrom:yyyy-MM-dd}.");
                continue;
            }

            if (!seenTiers.Add(cell.SizeTierCode))
            {
                rejections.Add($"Size tier '{cell.SizeTierCode}' appears more than once.");
                continue;
            }

            var ladder = new (string Band, decimal? Value)[]
            {
                (NitaqatBands.LowGreen,    cell.LowGreen),
                (NitaqatBands.MediumGreen, cell.MediumGreen),
                (NitaqatBands.HighGreen,   cell.HighGreen),
                (NitaqatBands.Platinum,    cell.Platinum),
            };

            // A tier without a LowGreen floor cannot be banded: everything below the next band up
            // silently reads as Red, which is a confident wrong answer rather than a refusal.
            if (cell.LowGreen is null)
            {
                rejections.Add($"Size tier '{cell.SizeTierCode}' has no Low Green floor. Low Green "
                             + "is the boundary between Red and green standing and is required; "
                             + "without it every establishment under the next band reads as Red.");
                continue;
            }

            var bad = false;
            foreach (var (band, value) in ladder)
            {
                if (value is null) continue;
                if (value.Value < 0m || value.Value > 100m)
                {
                    rejections.Add($"{cell.SizeTierCode}/{band}: {value.Value} is not a percentage "
                                 + "between 0 and 100. MHRSD publishes percentages, not ratios.");
                    bad = true;
                }
            }
            if (bad) continue;

            // Monotonicity. A better band must never require less Saudization than a worse one:
            // ResolveBand walks the ladder best-first and would otherwise return a band the
            // establishment has not earned.
            decimal? previous = null;
            string? previousBand = null;
            foreach (var (band, value) in ladder)
            {
                if (value is null) continue;
                if (previous is not null && value.Value < previous.Value)
                {
                    rejections.Add($"{cell.SizeTierCode}: {band} ({value.Value:0.##}%) requires LESS "
                                 + $"Saudization than {previousBand} ({previous.Value:0.##}%). The "
                                 + "band ladder must not decrease — check for a transposed column.");
                    bad = true;
                }
                previous = value;
                previousBand = band;
            }
            if (bad) continue;

            foreach (var (band, value) in ladder)
                if (value is not null)
                    planned.Add((cell.SizeTierCode, band, value.Value));
        }

        if (rejections.Count > 0)
            return new NitaqatGridImportResult(false, "grid_rejected",
                "The grid was not loaded. Nothing was written — a partially loaded grid bands an "
                + "establishment off an incomplete ladder, which is a wrong answer rather than a "
                + "missing one.", 0, 0, 0, rejections);

        if (planned.Count == 0)
            return Fail("cells_required", "No usable thresholds were supplied.");

        // ── Write ─────────────────────────────────────────────────────────────
        if (source.Length > 500) source = source[..500];

        var existing = await _db.NitaqatBandThresholds
            .Where(t => t.TenantId == tenantId
                     && t.ActivityCode == activity.Code)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        int inserted = 0, superseded = 0, updated = 0;

        foreach (var (tier, band, percent) in planned)
        {
            // Same effective date = a correction to this load. Update in place; the unique index
            // forbids a second row and a duplicate would be indistinguishable to the reader anyway.
            var sameDate = existing.FirstOrDefault(t =>
                string.Equals(t.SizeTierCode, tier, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Band, band, StringComparison.OrdinalIgnoreCase)
                && t.EffectiveFrom == effFrom);

            if (sameDate is not null)
            {
                sameDate.MinSaudizationPercent = percent;
                sameDate.BandRank    = NitaqatBands.RankOf(band);
                sameDate.SourceNote  = source;
                sameDate.IsVerified  = request.IsVerified;
                sameDate.UpdatedAtUtc = now;
                sameDate.UpdatedBy   = userId;
                updated++;
                continue;
            }

            // Close any row still in force on the new date. NOT deleted and NOT mutated in value:
            // a snapshot taken last quarter under the old figure must remain explicable.
            foreach (var prior in existing.Where(t =>
                         string.Equals(t.SizeTierCode, tier, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(t.Band, band, StringComparison.OrdinalIgnoreCase)
                         && t.EffectiveFrom < effFrom
                         && (t.EffectiveTo == null || t.EffectiveTo > effFrom)))
            {
                prior.EffectiveTo  = effFrom;
                prior.UpdatedAtUtc = now;
                prior.UpdatedBy    = userId;
                superseded++;
            }

            _db.NitaqatBandThresholds.Add(new NitaqatBandThreshold
            {
                TenantId     = tenantId,
                ActivityCode = activity.Code,
                SizeTierCode = tier,
                Band         = band,
                BandRank     = NitaqatBands.RankOf(band),
                MinSaudizationPercent = percent,
                EffectiveFrom = effFrom,
                SourceNote    = source,
                IsVerified    = request.IsVerified,
                CreatedAtUtc  = now,
                CreatedBy     = userId,
            });
            inserted++;
        }

        // One SaveChanges is implicitly transactional; no BeginTransactionAsync is opened, so no
        // execution-strategy wrapper is required (ExecutionStrategyLintTests).
        await _db.SaveChangesAsync(ct);

        return new NitaqatGridImportResult(
            true, null,
            $"Loaded {inserted + updated} threshold row(s) for '{activity.NameEn}' effective "
            + $"{request.EffectiveFrom:yyyy-MM-dd}"
            + (request.IsVerified
                ? "."
                : ", marked UNVERIFIED — bands computed from them are labelled provisional until "
                  + "someone confirms the figures against the published MHRSD table."),
            inserted, superseded, updated, Array.Empty<string>());
    }

    private static NitaqatGridImportResult Fail(string error, string message) =>
        new(false, error, message, 0, 0, 0, Array.Empty<string>());

    // ── Reference reads ───────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors <see cref="NitaqatCalculationService"/>: platform rows live under TenantId = null,
    /// which the tenant filter removes entirely, so both scopes are re-pinned explicitly through
    /// the sanctioned helper rather than a raw IgnoreQueryFilters.
    /// </summary>
    private static async Task<List<T>> ReferenceAsync<T>(DbSet<T> set, Guid tenantId, CancellationToken ct)
        where T : class, Zayra.Api.Domain.Entities.INullableTenantOwned
    {
        const string why =
            "Nitaqat reference tables hold platform-default rows under TenantId = null alongside "
            + "tenant overrides; the tenant filter excludes the platform rows entirely. Both scopes "
            + "are re-pinned explicitly here, so no other tenant's rows can be reached.";

        var platform = await ScopedBypass.NullableTenantWide(set, null, why).ToListAsync(ct);
        var tenant   = await ScopedBypass.NullableTenantWide(set, tenantId, why).ToListAsync(ct);
        platform.AddRange(tenant);
        return platform;
    }

    private static IEnumerable<NitaqatSizeTier> Effective(IEnumerable<NitaqatSizeTier> rows, DateOnly asOf)
    {
        var d = asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return rows.Where(r => r.EffectiveFrom <= d && (r.EffectiveTo == null || r.EffectiveTo > d));
    }

    private static IEnumerable<NitaqatBandThreshold> Effective(IEnumerable<NitaqatBandThreshold> rows, DateOnly asOf)
    {
        var d = asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return rows.Where(r => r.EffectiveFrom <= d && (r.EffectiveTo == null || r.EffectiveTo > d));
    }
}
