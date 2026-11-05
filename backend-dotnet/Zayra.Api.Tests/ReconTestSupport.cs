using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Approve now needs a recorded manpower and value reconciliation sign-off. A test about something else (WPS, costing, maker-checker,
/// hold-outs) that has to reach or pass Approve records one here for the run's current figures, so it goes on testing what it was
/// written to test. The reconciliation gate itself is tested in Zayra.Api.Tests.Recon.
/// </summary>
internal static class ReconTestSupport
{
    public static async Task SignOffAsync(ZayraDbContext db, Guid runId)
    {
        var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        if (!PayrollRunTypes.IsPeriodOwning(run.RunType)) return; // off-cycle runs are not gated
        if (await db.PayrollRunReconSignoffs.AnyAsync(s => s.PayrollRunId == runId && s.Status == ReconSignoffStatuses.Active)) return;
        db.PayrollRunReconSignoffs.Add(new PayrollRunReconSignoff
        {
            TenantId = run.TenantId, CompanyId = run.CompanyId, PayrollRunId = runId, ProcessVersion = run.ProcessVersion,
            SnapshotHash = new string('0', 64), SnapshotJson = "{}", SignedOffByUserId = Guid.NewGuid(), SignedOffByName = "Test reviewer",
            Status = ReconSignoffStatuses.Active,
        });
        await db.SaveChangesAsync();
    }
}
