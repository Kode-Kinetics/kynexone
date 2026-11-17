using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// CTO follow-up on F1, item 3: the payment-batch endpoints in PayrollController tell "returned" (the money came back) apart
/// from "not paid: rejected by bank" (it never left). A Rejected record is never isReturned, never counted in
/// unresolvedReturns, and the Reconciled refusal names each.
/// </summary>
public class PaymentBatchRejectedFieldsTests
{
    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record World(Guid TenantId, PayrollPaymentBatch Batch, PayrollPaymentRecord Returned, PayrollPaymentRecord Rejected);

    /// <summary>A settled batch (status Paid) with one Paid, one Returned (200) and one Rejected (300) record.</summary>
    private static World Seed(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var run = new PayrollRun { TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = 7, Status = "Locked" };
        var batch = new PayrollPaymentBatch
        {
            TenantId = tenantId, PayrollRunId = run.Id, BatchNumber = "PB-2026-07-009", Currency = "SAR", TotalAmount = 600m,
            WpsStatus = WpsStatuses.Paid,
        };
        PayrollPaymentRecord Record(int emp, decimal amount, string status) => new()
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = emp, Amount = amount, Status = status,
            Iban = "SA0380000000608010167519", WpsReference = $"W{emp}",
        };
        var returned = Record(2, 200m, PaymentRecordStatuses.Returned);
        var rejected = Record(3, 300m, PaymentRecordStatuses.Rejected);
        db.AddRange(run, batch, Record(1, 100m, PaymentRecordStatuses.Paid), returned, rejected);
        // The bank file the batch went out with (a WPS status change needs one).
        db.WPSFileBatches.Add(new WPSFileBatch
        {
            TenantId = tenantId, CompanyId = companyId, PaymentBatchId = batch.Id, Status = WpsStatuses.Paid, SifFileName = "f.sif",
        });
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tenantId, CompanyId = companyId, SourceModule = "Payroll", SourceEntityId = run.Id,
            SourceEntityRef = batch.BatchNumber, EventType = GlEventTypes.NetSettlement, DebitAccount = "2100 - Salaries Payable",
            CreditAccount = "1000 - Cash/Bank", Description = "Net pay settlement", Amount = 600m, Currency = "SAR",
            EntryDate = new DateOnly(2026, 7, 31), Period = "2026-07",
        });
        db.SaveChanges();
        return new World(tenantId, batch, returned, rejected);
    }

    private static PayrollController Ctrl(ZayraDbContext db, World w) => KsaWpsReviewFixesTests.Payroll(db, w.TenantId, Guid.NewGuid());

    private static JsonElement Web(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

    [Fact]
    public async Task PaymentRecords_IsReturnedOnlyForReturned_AndIsRejectedForRejected()
    {
        using var db = NewDb();
        var w = Seed(db);

        var rows = Web(Assert.IsType<OkObjectResult>(await Ctrl(db, w).PaymentRecords(w.Batch.Id, default)).Value).EnumerateArray().ToList();

        var returned = rows.Single(r => r.GetProperty("id").GetGuid() == w.Returned.Id);
        returned.GetProperty("isReturned").GetBoolean().Should().BeTrue();
        returned.GetProperty("isRejected").GetBoolean().Should().BeFalse();
        var rejected = rows.Single(r => r.GetProperty("id").GetGuid() == w.Rejected.Id);
        rejected.GetProperty("isReturned").GetBoolean().Should().BeFalse("a rejected salary never left; it did not come back");
        rejected.GetProperty("isRejected").GetBoolean().Should().BeTrue();
        rejected.GetProperty("bankConfirmed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task BatchList_UnresolvedReturnsCountsReturnedOnly_AndRejectionsAreCountedApart()
    {
        using var db = NewDb();
        var w = Seed(db);

        var view = Web(Assert.IsType<OkObjectResult>(await Ctrl(db, w).ListPaymentBatches(null, default)).Value)
            .EnumerateArray().Single(b => b.GetProperty("id").GetGuid() == w.Batch.Id);

        view.GetProperty("unresolvedReturns").GetProperty("count").GetInt32().Should().Be(1);
        view.GetProperty("unresolvedReturns").GetProperty("total").GetDecimal().Should().Be(200m);
        view.GetProperty("unresolvedRejections").GetProperty("count").GetInt32().Should().Be(1);
        view.GetProperty("unresolvedRejections").GetProperty("total").GetDecimal().Should().Be(300m);
        view.GetProperty("presentsCleanButIsNot").GetBoolean().Should().BeTrue("both still block the batch reading clean");
    }

    [Fact]
    public async Task Reconciled_IsRefused_AndTheMessageNamesReturnedAndRejectedApart()
    {
        using var db = NewDb();
        var w = Seed(db);

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(
            await Ctrl(db, w).UpdateWpsStatus(w.Batch.Id, new WpsStatusRequest(WpsStatuses.Reconciled, null), default));
        var body = Web(refused.Value);

        body.GetProperty("error").GetString().Should().Be("payments_not_reconciled");
        body.GetProperty("returnedCount").GetInt32().Should().Be(1);
        body.GetProperty("returnedTotal").GetDecimal().Should().Be(200m);
        body.GetProperty("rejectedCount").GetInt32().Should().Be(1);
        body.GetProperty("rejectedTotal").GetDecimal().Should().Be(300m);
        body.GetProperty("message").GetString().Should().Contain("returned by the bank").And.Contain("not paid: rejected by bank");
        db.PayrollPaymentBatches.Single().WpsStatus.Should().Be(WpsStatuses.Paid);
    }
}
