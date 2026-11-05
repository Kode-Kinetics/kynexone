using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

/// <summary>A sign-off left over from an earlier processing of the run is retired when the run is signed again, not a reason to refuse.</summary>
public class ReconStaleSignoffTests
{
    // ── A stale sign-off is retired, not a reason to refuse ──────────────────────────────────────

    [Fact]
    public async Task ASignoffFromAnEarlierProcessing_IsSuperseded_WhenTheRunIsSignedAgain()
    {
        var k = await ReconWithdrawAndSignerTests.SeedAsync();
        await ReconWithdrawAndSignerTests.SignAsync(k);
        k.Current.ProcessVersion += 1; // the run was processed again; nothing retired the old sign-off on this path
        await k.Db.SaveChangesAsync();

        var signer = ReconWithdrawAndSignerTests.Ctrl(k);
        var res = await signer.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(signer, k.Current.Id), default);

        PayrollReconAccessTests.Status(res).Should().Be(201);
        var rows = await k.Db.PayrollRunReconSignoffs.AsNoTracking().OrderBy(s => s.ProcessVersion).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].Status.Should().Be(ReconSignoffStatuses.Superseded);
        rows[0].SupersededReason.Should().Be(ReconSupersededReasons.Reprocessed);
        rows[1].Status.Should().Be(ReconSignoffStatuses.Active);
        rows[1].ProcessVersion.Should().Be(k.Current.ProcessVersion);
        (await k.Db.PayrollAuditLogs.AsNoTracking().CountAsync(a => a.Action == "payroll.recon.superseded")).Should().Be(1);
    }

    [Fact]
    public async Task ASignoffForTheCurrentProcessing_StillBlocksASecondOne()
    {
        var k = await ReconWithdrawAndSignerTests.SeedAsync();
        await ReconWithdrawAndSignerTests.SignAsync(k);
        var signer = ReconWithdrawAndSignerTests.Ctrl(k);
        var res = await signer.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(signer, k.Current.Id), default);
        PayrollReconAccessTests.Status(res).Should().Be(409);
        PayrollReconAccessTests.Error(res).Should().Be("already_signed_off");
    }
}
