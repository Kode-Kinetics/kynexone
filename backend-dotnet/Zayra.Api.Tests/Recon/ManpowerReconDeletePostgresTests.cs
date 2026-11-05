using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Recon;

/// <summary>Deleting a run against the real run foreign key from the sign-off history.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class ManpowerReconDeletePostgresTests
{
    private readonly PostgresFixture _fx;
    public ManpowerReconDeletePostgresTests(PostgresFixture fx) => _fx = fx;

    private static int StatusOf(IActionResult r) => PayrollReconAccessTests.Status(r);

    // ── Delete ───────────────────────────────────────────────────────────────────────────────────

    private async Task<IActionResult> DeleteAsync(ManpowerReconSignoffPostgresTests.Scenario s, Guid runId)
    {
        await using var db = _fx.CreateDb();
        return await ReconHarness.Payroll(db, s.TenantId, "Admin", new[] { "payroll.read", "payroll.run_delete" }).DeleteRun(runId, default);
    }

    [Fact]
    public async Task ADraftRunWithSignoffHistory_CannotBeDeleted_AndTheAnswerSaysToVoidIt()
    {
        var s = await ManpowerReconSignoffPostgresTests.NewScenarioAsync(_fx);
        var signer = s.Recon();
        StatusOf(await signer.Signoff(s.Current.Id, await ReconHarness.ValidRequestAsync(signer, s.Current.Id), default)).Should().Be(201);
        // Sent back to Draft, which retires the sign-off but keeps it as history.
        await using (var back = _fx.CreateDb())
        {
            await back.Database.ExecuteSqlInterpolatedAsync($"UPDATE payroll_run_recon_signoffs SET status = 'Superseded', superseded_at_utc = now(), superseded_reason = 'SentBack' WHERE payroll_run_id = {s.Current.Id}");
            await back.Database.ExecuteSqlInterpolatedAsync($"UPDATE payroll_runs SET status = 'Draft' WHERE id = {s.Current.Id}");
        }

        var res = await DeleteAsync(s, s.Current.Id);

        StatusOf(res).Should().Be(409);
        PayrollReconAccessTests.Error(res).Should().Be("run_has_review_history");
        ReconHarness.Json(((ObjectResult)res).Value).GetProperty("message").GetString().Should().Contain("void");
        await using var check = _fx.CreateDb();
        (await check.PayrollRuns.AsNoTracking().CountAsync(r => r.Id == s.Current.Id)).Should().Be(1);
    }

    [Fact]
    public async Task ADraftRunWithNoSignoffHistory_IsStillDeleted()
    {
        var s = await ManpowerReconSignoffPostgresTests.NewScenarioAsync(_fx, withPrior: false);
        await using (var draft = _fx.CreateDb())
            await draft.Database.ExecuteSqlInterpolatedAsync($"UPDATE payroll_runs SET status = 'Draft' WHERE id = {s.Current.Id}");
        StatusOf(await DeleteAsync(s, s.Current.Id)).Should().Be(204);
    }
}
