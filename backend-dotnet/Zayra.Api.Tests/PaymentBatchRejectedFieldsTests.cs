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

    // ── N4: an open hold on a Paid record blocks Reconciled AND settle, and never reads clean ───

    /// <summary>A batch whose only record is Paid, with a rejection held after Paid (an open hold for a person).</summary>
    private static (Guid TenantId, PayrollPaymentBatch Batch, PayrollRun Run) SeedHeldPaid(ZayraDbContext db, string wpsStatus, bool settled)
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var run = new PayrollRun { TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = 7, Status = "Locked" };
        var batch = new PayrollPaymentBatch
        {
            TenantId = tenantId, PayrollRunId = run.Id, BatchNumber = "PB-2026-07-010", Currency = "SAR", TotalAmount = 100m,
            WpsStatus = wpsStatus,
        };
        var record = new PayrollPaymentRecord
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = 1, Amount = 100m, Status = PaymentRecordStatuses.Paid,
            Iban = "SA0380000000608010167519", WpsReference = "W1",
        };
        db.AddRange(run, batch, record);
        db.WPSFileBatches.Add(new WPSFileBatch
        {
            TenantId = tenantId, CompanyId = companyId, PaymentBatchId = batch.Id, Status = wpsStatus, SifFileName = "f.sif",
        });
        db.BankPaymentConfirmations.Add(new BankPaymentConfirmation
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, PaymentRecordId = record.Id, EmployeeId = 1,
            Outcome = BankConfirmationOutcomes.Rejected, RawOutcome = "RJCT", PreviousStatus = PaymentRecordStatuses.Paid,
            Applied = false, HoldReason = Zayra.Api.Infrastructure.Finance.BankOutcomeHolds.PaidThenRejected,
            ConfirmedAmount = 100m, RecordAmount = 100m, MatchedBy = BankConfirmationMatchModes.WpsReference,
            ImportBatchId = Guid.NewGuid(), SourceFileName = "b.csv", SourceFileHash = "h", ParserKey = "generic-csv",
        });
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tenantId, CompanyId = companyId, SourceModule = "Payroll", SourceEntityId = run.Id,
            SourceEntityRef = settled ? batch.BatchNumber : "RUN-2026-07", EventType = settled ? GlEventTypes.NetSettlement : GlEventTypes.Accrual,
            DebitAccount = settled ? "2100 - Salaries Payable" : "", CreditAccount = settled ? "1000 - Cash/Bank" : "2100 - Salaries Payable",
            Description = settled ? "Net pay settlement" : "Net payable", Amount = 100m, Currency = "SAR",
            EntryDate = new DateOnly(2026, 7, 31), Period = "2026-07",
        });
        db.SaveChanges();
        return (tenantId, batch, run);
    }

    [Fact]
    public async Task AnOpenHoldOnAPaidRecord_BlocksReconciled()
    {
        using var db = NewDb();
        var (tenantId, batch, _) = SeedHeldPaid(db, WpsStatuses.Paid, settled: true);

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(
            await KsaWpsReviewFixesTests.Payroll(db, tenantId, Guid.NewGuid())
                .UpdateWpsStatus(batch.Id, new WpsStatusRequest(WpsStatuses.Reconciled, null), default));

        Web(refused.Value).GetProperty("error").GetString().Should().Be("bank_lines_held_for_person");
        db.PayrollPaymentBatches.Single().WpsStatus.Should().Be(WpsStatuses.Paid);
    }

    [Fact]
    public async Task AnOpenHoldOnAPaidRecord_BlocksSettle()
    {
        using var db = NewDb();
        var (tenantId, batch, _) = SeedHeldPaid(db, WpsStatuses.Accepted, settled: false);

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(
            await KsaWpsReviewFixesTests.Payroll(db, tenantId, Guid.NewGuid())
                .SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest("settle", null), default));

        Web(refused.Value).GetProperty("error").GetString().Should().Be("bank_lines_held_for_person");
        db.FinanceGlEntries.Count(e => e.EventType == GlEventTypes.NetSettlement).Should().Be(0);
    }

    [Fact]
    public async Task AnOpenHoldOnAPaidRecord_NeverPresentsClean_OnTheListTheHistoryOrTheHandoff()
    {
        using var db = NewDb();
        var (tenantId, batch, run) = SeedHeldPaid(db, WpsStatuses.Paid, settled: true);
        var ctrl = KsaWpsReviewFixesTests.Payroll(db, tenantId, Guid.NewGuid());

        var listed = Web(Assert.IsType<OkObjectResult>(await ctrl.ListPaymentBatches(null, default)).Value)
            .EnumerateArray().Single(b => b.GetProperty("id").GetGuid() == batch.Id);
        listed.GetProperty("presentsCleanButIsNot").GetBoolean().Should().BeTrue();

        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim("tenant_id", tenantId.ToString()),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new System.Security.Claims.Claim("permission", "payroll.export"),
            new System.Security.Claims.Claim("is_group_scope", "true"),
        }, "Test"));
        var history = new Zayra.Api.Controllers.Finance.BankConfirmationsController(db,
            new Zayra.Api.Infrastructure.Finance.BankConfirmationService(db, new Zayra.Api.Infrastructure.Finance.IBankConfirmationParser[]
                { new Zayra.Api.Infrastructure.Finance.GenericCsvBankConfirmationParser() }))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = principal },
            },
        };
        var historyBody = Web(Assert.IsType<OkObjectResult>(await history.History(batch.Id, default)).Value);
        historyBody.GetProperty("presentsCleanButIsNot").GetBoolean().Should().BeTrue();

        var exports = new Zayra.Api.Infrastructure.Finance.JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        var handoff = Web(await new Zayra.Api.Infrastructure.Finance.PeriodHandoffReconciler(db, exports)
            .ReconcileAsync(tenantId, new Zayra.Api.Infrastructure.Finance.JournalExportFilter(PayrollRunId: run.Id), groupScopedCaller: true, default));
        handoff.GetProperty("payments").GetProperty("batches").EnumerateArray().Single()
            .GetProperty("presentsCleanButIsNot").GetBoolean().Should().BeTrue();
    }

    // ── Package F5: the two refusals read as instructions, in both languages (wording only) ──────

    [Fact]
    public async Task TheHeldBatchRefusal_TellsTheOfficerToCheckTheStatementAndRecordADecision_InBothLanguages()
    {
        using var db = NewDb();
        var (tenantId, batch, _) = SeedHeldPaid(db, WpsStatuses.Paid, settled: true);

        var refused = Web(Assert.IsType<UnprocessableEntityObjectResult>(
            await KsaWpsReviewFixesTests.Payroll(db, tenantId, Guid.NewGuid())
                .UpdateWpsStatus(batch.Id, new WpsStatusRequest(WpsStatuses.Reconciled, null), default)).Value);

        refused.GetProperty("error").GetString().Should().Be("bank_lines_held_for_person", "the machine key does not change");
        refused.GetProperty("action").GetString().Should().Be("reconcile");
        var en = refused.GetProperty("message").GetString()!;
        var ar = refused.GetProperty("messageAr").GetString()!;
        en.Should().Contain("This batch cannot be reconciled yet. 1 salary line(s)").And.Contain("check the bank statement")
            .And.Contain("record your decision with a reason and the bank's reference").And.NotContainEquivalentOf("exit");
        ar.Should().Contain("لا يمكن مطابقة هذه الدفعة بعد").And.Contain("كشف حساب البنك").And.Contain("سجّل قرارك مع السبب ومرجع البنك");
    }

    [Fact]
    public async Task TheHeldBatchRefusal_OnSettle_SaysPostedToTheLedger_NotSettled()
    {
        using var db = NewDb();
        var (tenantId, batch, _) = SeedHeldPaid(db, WpsStatuses.Accepted, settled: false);

        var refused = Web(Assert.IsType<UnprocessableEntityObjectResult>(
            await KsaWpsReviewFixesTests.Payroll(db, tenantId, Guid.NewGuid())
                .SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest("settle", null), default)).Value);

        refused.GetProperty("action").GetString().Should().Be("ledger_settle");
        refused.GetProperty("message").GetString().Should().Contain("This batch cannot be posted to the ledger yet").And.NotContain("settled");
        refused.GetProperty("messageAr").GetString().Should().Contain("لا يمكن ترحيل هذه الدفعة إلى الدفاتر بعد");
    }

    [Fact]
    public async Task TheNotReconciledRefusal_CarriesAnArabicMessage_AndNeverClaimsTheMoneyNeverLeft()
    {
        using var db = NewDb();
        var w = Seed(db);

        var refused = Web(Assert.IsType<UnprocessableEntityObjectResult>(
            await Ctrl(db, w).UpdateWpsStatus(w.Batch.Id, new WpsStatusRequest(WpsStatuses.Reconciled, null), default)).Value);

        refused.GetProperty("error").GetString().Should().Be("payments_not_reconciled");
        refused.GetProperty("message").GetString().Should().Contain("check the bank statement before paying again").And.NotContainEquivalentOf("never left");
        refused.GetProperty("messageAr").GetString().Should().Contain("راجع كشف حساب البنك قبل إعادة الدفع");
    }
}
