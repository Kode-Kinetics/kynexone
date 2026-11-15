using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// The part of a salary advance a paid final settlement moved to the 1420 Employee Receivable
/// (<c>PayrollController.FinalizePaidSettlementsAsync</c>). The move lowers the advance's OutstandingBalance without being a
/// repayment, so TotalRepaid is rightly unchanged; the advance audit's <c>ApprovedAmount − TotalRepaid − OutstandingBalance</c>
/// check must therefore also subtract what was moved, or a settled leaver's advance reads as unreconciled for ever.
///
/// <para>The amount is recorded per advance on the advance's own audit trail. The 1420 sub-ledger cannot give it: its rows
/// are per EMPLOYEE and add the loan and the advance residuals of one settlement together, and the reclass journal is keyed
/// on the settlement, not the advance. A per-advance audit row needs no new column or migration.</para>
/// </summary>
public static class AdvanceResidualAudit
{
    public const string Action = "ResidualMovedToEmployeeReceivable";

    private sealed record Moved(decimal MovedToEmployeeReceivable, decimal OutstandingBefore, decimal OutstandingAfter,
        Guid SettlementId, Guid PayrollRunId, Guid PaymentBatchId);

    public static AdvanceAuditLog Entry(Guid tenantId, SalaryAdvance advance, decimal moved, decimal outstandingBefore,
        Guid settlementId, Guid runId, Guid batchId, Guid? userId, string userName) => new()
    {
        TenantId = tenantId, AdvanceId = advance.Id, Action = Action,
        OldValuesJson = JsonSerializer.Serialize(new { outstandingBalance = outstandingBefore, status = "Active" }),
        NewValuesJson = JsonSerializer.Serialize(new Moved(moved, outstandingBefore, advance.OutstandingBalance, settlementId, runId, batchId)),
        PerformedBy = userId, PerformedByName = userName,
    };

    /// <summary>What has been moved to 1420 from each of <paramref name="advanceIds"/>, from their audit trails.</summary>
    public static async Task<Dictionary<Guid, decimal>> MovedByAdvanceAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> advanceIds, CancellationToken ct)
    {
        if (advanceIds.Count == 0) return new Dictionary<Guid, decimal>();
        var rows = await db.AdvanceAuditLogs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Action == Action && advanceIds.Contains(x.AdvanceId))
            .Select(x => new { x.AdvanceId, x.NewValuesJson })
            .ToListAsync(ct);
        return rows
            .GroupBy(x => x.AdvanceId)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(x => JsonSerializer.Deserialize<Moved>(x.NewValuesJson)?.MovedToEmployeeReceivable ?? 0m), 2));
    }
}
