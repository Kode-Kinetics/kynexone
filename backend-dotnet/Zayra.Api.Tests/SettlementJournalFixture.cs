using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Posts a batch's net settlement journal the way PayrollController.SettlePaymentBatch does (Payroll / NetSettlement, source = the
/// run, reference = the batch number), so a test can state "this batch was settled" (the BankOutcomeLedger.SettledAtAsync test).
/// Returned money needs the GL correction only after it (owner ruling 2026-11-17). Synthetic data.
/// </summary>
internal static class SettlementJournalFixture
{
    public static void Settle(ZayraDbContext db, Guid batchId)
    {
        var batch = db.PayrollPaymentBatches.Single(b => b.Id == batchId);
        var run = db.PayrollRuns.Single(r => r.Id == batch.PayrollRunId);
        if (string.IsNullOrWhiteSpace(batch.BatchNumber)) batch.BatchNumber = "PB-SETTLED-" + batch.Id.ToString("N")[..8];
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = batch.TenantId, CompanyId = run.CompanyId, SourceModule = "Payroll", SourceEntityId = run.Id,
            SourceEntityRef = batch.BatchNumber, EventType = GlEventTypes.NetSettlement, DebitAccount = "2100 - Salaries Payable",
            CreditAccount = "1000 - Cash/Bank", Description = "Net pay settlement", Amount = 1m, Currency = "SAR",
            EntryDate = DateOnly.FromDateTime(DateTime.UtcNow), Period = DateTime.UtcNow.ToString("yyyy-MM"),
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
        });
        db.SaveChanges();
    }
}
