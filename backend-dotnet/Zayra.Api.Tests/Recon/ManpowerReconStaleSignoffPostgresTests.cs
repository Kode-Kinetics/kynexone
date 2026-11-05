using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Tests.Recon;

/// <summary>Replacing a sign-off from an earlier processing of the run, against the real unique index.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class ManpowerReconStaleSignoffPostgresTests
{
    private readonly PostgresFixture _fx;
    public ManpowerReconStaleSignoffPostgresTests(PostgresFixture fx) => _fx = fx;

    private static int StatusOf(IActionResult r) => PayrollReconAccessTests.Status(r);

    [Fact]
    public async Task ASignoffFromAnEarlierProcessing_IsSupersededAndReplaced_WithoutTrippingTheUniqueIndex()
    {
        var s = await ManpowerReconSignoffPostgresTests.NewScenarioAsync(_fx);
        var first = s.Recon();
        StatusOf(await first.Signoff(s.Current.Id, await ReconHarness.ValidRequestAsync(first, s.Current.Id), default)).Should().Be(201);
        await using (var bump = _fx.CreateDb())
            await bump.Database.ExecuteSqlInterpolatedAsync($"UPDATE payroll_runs SET process_version = process_version + 1 WHERE id = {s.Current.Id}");

        await using var db = _fx.CreateDb();
        var second = s.Recon(db: db);
        StatusOf(await second.Signoff(s.Current.Id, await ReconHarness.ValidRequestAsync(second, s.Current.Id), default)).Should().Be(201);

        await using var check = _fx.CreateDb();
        var rows = await check.PayrollRunReconSignoffs.AsNoTracking().Where(x => x.PayrollRunId == s.Current.Id).OrderBy(x => x.ProcessVersion).ToListAsync();
        rows.Select(x => (x.Status, x.SupersededReason)).Should().Equal(("Superseded", "Reprocessed"), ("Active", null));
    }
}
