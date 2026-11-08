using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>Which approval stamp a transfer transition records with its new status.</summary>
public enum TransferStamp { None, CurrentManager, NewManager, Hr }

/// <summary>
/// The one way a transfer leaves a step: a conditional claim on the status the caller saw. The row moves in a single
/// <c>UPDATE ... WHERE Status = expected</c>, so of two decisions that arrive together exactly one gets a row and the other is told
/// it lost. A read-check-write would let both through, and the later write would overwrite the earlier decision.
/// </summary>
public static class EmployeeTransferClaim
{
    /// <summary>
    /// Moves <paramref name="transfer"/> from <paramref name="expectedStatus"/> to <paramref name="nextStatus"/> (with the stamp for the
    /// step) when it is still at <paramref name="expectedStatus"/> in the store; false, with nothing written, when it is not. On success
    /// the tracked row reflects the new state.
    /// </summary>
    public static async Task<bool> TryClaimAsync(ZayraDbContext db, EmployeeTransferRequest transfer, string expectedStatus, string nextStatus, TransferStamp stamp, CancellationToken ct)
    {
        var tenantId = transfer.TenantId;
        var id = transfer.Id;
        DateTime? now = DateTime.UtcNow;

        if (!db.Database.IsRelational())
        {
            // EF InMemory has neither a set-based update nor concurrent writers: compare with the store, then stage on the tracked row
            // (the caller's SaveChanges writes it).
            var stored = await db.EmployeeTransferRequests.AsNoTracking()
                .Where(x => x.Id == id && x.TenantId == tenantId).Select(x => x.Status).FirstOrDefaultAsync(ct);
            if (stored != expectedStatus) return false;
            transfer.Status = nextStatus;
            switch (stamp)
            {
                case TransferStamp.CurrentManager: transfer.CurrentManagerApprovedAtUtc = now; break;
                case TransferStamp.NewManager: transfer.NewManagerApprovedAtUtc = now; break;
                case TransferStamp.Hr: transfer.HrApprovedAtUtc = now; break;
            }
            return true;
        }

        var claim = db.EmployeeTransferRequests.Where(x => x.Id == id && x.TenantId == tenantId && x.Status == expectedStatus);
        var rows = stamp switch
        {
            TransferStamp.CurrentManager => await claim.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, nextStatus).SetProperty(x => x.CurrentManagerApprovedAtUtc, now), ct),
            TransferStamp.NewManager => await claim.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, nextStatus).SetProperty(x => x.NewManagerApprovedAtUtc, now), ct),
            TransferStamp.Hr => await claim.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, nextStatus).SetProperty(x => x.HrApprovedAtUtc, now), ct),
            _ => await claim.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, nextStatus), ct),
        };
        if (rows != 1) return false;
        await db.Entry(transfer).ReloadAsync(ct);   // the tracked row now matches the store, so no later SaveChanges rewrites it
        return true;
    }
}
