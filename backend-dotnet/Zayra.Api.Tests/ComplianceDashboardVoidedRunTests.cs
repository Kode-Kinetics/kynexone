using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A Voided payroll run is not waiting for anyone: the Saudi Compliance dashboard counted two Voided Novembers as "payroll runs awaiting approval" beside the
/// live Locked one, and read the newest Voided run as the latest run ("not yet approved/locked"). Voided and Locked runs are excluded from the pending count.
/// </summary>
public class ComplianceDashboardVoidedRunTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static ZayraDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<SaudiComplianceDashboard> BuildAsync(ZayraDbContext db) =>
        await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(Tenant, CancellationToken.None);

    [Fact]
    public async Task VoidedRuns_AreNotCounted_AsAwaitingApproval()
    {
        using var db = MakeDb();
        db.PayrollRuns.AddRange(
            new PayrollRun { TenantId = Tenant, Year = 2026, Month = 11, Status = "Voided" },
            new PayrollRun { TenantId = Tenant, Year = 2026, Month = 11, Status = "Voided" },
            new PayrollRun { TenantId = Tenant, Year = 2026, Month = 11, Status = "Locked" });
        await db.SaveChangesAsync();

        var dashboard = await BuildAsync(db);

        Assert.Equal(0, dashboard.Wps.PendingApprovals);
        Assert.DoesNotContain(dashboard.ActionItems, a => a.Id == "wps_pending_approvals");
    }

    [Fact]
    public async Task ARunThatIsReallyWaiting_IsStillCounted_NextToVoidedAndLockedOnes()
    {
        using var db = MakeDb();
        db.PayrollRuns.AddRange(
            new PayrollRun { TenantId = Tenant, Year = 2026, Month = 9, Status = "Voided" },
            new PayrollRun { TenantId = Tenant, Year = 2026, Month = 10, Status = "Locked" },
            new PayrollRun { TenantId = Tenant, Year = 2026, Month = 11, Status = "PendingFinanceReview" });
        await db.SaveChangesAsync();

        Assert.Equal(1, (await BuildAsync(db)).Wps.PendingApprovals);
    }

    [Fact]
    public async Task ANewerVoidedRun_IsNotTheLatestRun()
    {
        using var db = MakeDb();
        db.PayrollRuns.AddRange(
            new PayrollRun { TenantId = Tenant, Year = 2026, Month = 10, Status = "Locked" },
            new PayrollRun { TenantId = Tenant, Year = 2026, Month = 11, Status = "Voided" });
        await db.SaveChangesAsync();

        var wps = (await BuildAsync(db)).Wps;

        Assert.Equal("Locked", wps.LastRunStatus);
        Assert.Equal("2026-10", wps.LastRunPeriod);
    }
}
