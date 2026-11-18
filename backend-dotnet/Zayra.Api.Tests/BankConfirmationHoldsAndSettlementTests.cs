using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// CTO fix round on F1 (rulings 1 to 8 on the independent audit).
///
/// <list type="bullet">
/// <item>B1: the period handoff agrees with the batch history. Returned money alone awaits the returned-money GL
/// correction; a rejection is listed on its own with "Not paid: rejected by bank" and blocks with its own code.</item>
/// <item>B2: a rejection after Paid, and a Returned/Rejected swap, are held for a person, block the handoff while open,
/// and have an explicit, reasoned, audited exit (the second approver is wave 2, Setting 3).</item>
/// <item>C1: a rejection imported AFTER the batch's settlement journal needs the same GL correction (cash was credited for
/// it) and is reported as its own figure, in the GL-correction blocker. Before settlement it needs none.</item>
/// <item>Ruling 4: a DUPL refusal is never "not paid": it is held for a person to check the original payment.</item>
/// <item>Ruling 5: "the bank gave no reason code" only when the bank really gave none.</item>
/// </list>
/// </summary>
public class BankConfirmationHoldsAndSettlementTests
{
    private const string Iban = "SA0380000000608010167519";

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record World(Guid TenantId, Guid CompanyId, PayrollRun Run, PayrollPaymentBatch Batch, List<PayrollPaymentRecord> Records)
    {
        public PayrollPaymentRecord R(int i) => Records[i - 1];
    }

    /// <summary>A Locked run with its accrual on the ledger and one batch. Record i is WPS reference Wi.</summary>
    private static World Seed(ZayraDbContext db, params (decimal Amount, string Status)[] records)
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var run = new PayrollRun { TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = 7, Status = "Locked" };
        var batch = new PayrollPaymentBatch
        {
            TenantId = tenantId, PayrollRunId = run.Id, BatchNumber = "PB-2026-07-001", Currency = "SAR",
            TotalAmount = records.Sum(r => r.Amount),
        };
        db.PayrollRuns.Add(run);
        db.PayrollPaymentBatches.Add(batch);
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tenantId, CompanyId = companyId, SourceModule = "Payroll", SourceEntityId = run.Id,
            SourceEntityRef = "RUN-2026-07", EventType = GlEventTypes.Accrual, CreditAccount = "2100 - Salaries Payable",
            Description = "Net payable", Amount = records.Sum(r => r.Amount), Currency = "SAR",
            EntryDate = new DateOnly(2026, 7, 31), Period = "2026-07",
        });
        var list = records.Select((r, i) => new PayrollPaymentRecord
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = i + 1, Amount = r.Amount,
            Iban = Iban, Status = r.Status, WpsReference = $"W{i + 1}",
        }).ToList();
        db.PayrollPaymentRecords.AddRange(list);
        db.SaveChanges();
        return new World(tenantId, companyId, run, batch, list);
    }

    private static World Seed(ZayraDbContext db, string status = "Pending") => Seed(db, (100m, status));

    /// <summary>The batch's net settlement journal, posted at <paramref name="postedAtUtc"/>.</summary>
    private static void Settle(ZayraDbContext db, World w, DateTime postedAtUtc)
    {
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = w.TenantId, CompanyId = w.CompanyId, SourceModule = "Payroll", SourceEntityId = w.Run.Id,
            SourceEntityRef = w.Batch.BatchNumber, EventType = GlEventTypes.NetSettlement, DebitAccount = "2100 - Salaries Payable",
            CreditAccount = "1000 - Cash/Bank", Description = "Net pay settlement", Amount = w.Batch.TotalAmount, Currency = "SAR",
            EntryDate = new DateOnly(2026, 7, 31), Period = "2026-07", CreatedAtUtc = postedAtUtc,
        });
        db.SaveChanges();
    }

    private static string Csv(params (string Ref, decimal Amount, string Status, string? Reason)[] rows) =>
        CsvDated("2026-07-30", rows);

    private static string CsvDated(string valueDate, params (string Ref, decimal Amount, string Status, string? Reason)[] rows) =>
        "wpsReference,amount,valueDate,status,reasonCode\n"
        + string.Concat(rows.Select(r => $"{r.Ref},{r.Amount},{valueDate},{r.Status},{r.Reason}\n"));

    private static BankConfirmationService Service(ZayraDbContext db) =>
        new(db, new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser(), new WpsAckBankConfirmationParser() });

    private static async Task<(BankConfirmationImportResult? Result, JournalExportRefusal? Refusal)> TryImport(
        ZayraDbContext db, World w, BankConfirmationImportOptions options, string content)
    {
        var outcome = await Service(db).ImportAsync(w.TenantId, w.Batch.Id, $"bank-{Guid.NewGuid():N}.csv", content, "generic-csv",
            options, Guid.NewGuid(), "Payroll Officer", default);
        if (outcome.Refusal is null && !options.DryRun) await db.SaveChangesAsync();
        return outcome;
    }

    private static async Task<BankConfirmationImportResult> Import(
        ZayraDbContext db, World w, BankConfirmationImportOptions? options, params (string Ref, decimal Amount, string Status, string? Reason)[] rows)
    {
        var (result, refusal) = await TryImport(db, w, options ?? new BankConfirmationImportOptions(), Csv(rows));
        refusal.Should().BeNull();
        return result!;
    }

    private static Task<BankConfirmationImportResult> Import(ZayraDbContext db, World w, string status, string? reason = null) =>
        Import(db, w, null, ("W1", 100m, status, reason));

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private static JsonElement Element(object o) => JsonDocument.Parse(Json(o)).RootElement;

    private static async Task<JsonElement> Handoff(ZayraDbContext db, World w)
    {
        var exports = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        var report = await new PeriodHandoffReconciler(db, exports)
            .ReconcileAsync(w.TenantId, new JournalExportFilter(PayrollRunId: w.Run.Id), groupScopedCaller: true, default);
        return Element(report);
    }

    private static List<string> Blockers(JsonElement handoff) =>
        handoff.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()!).ToList();

    private static BankConfirmationsController Controller(ZayraDbContext db, World w)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Payroll Officer"),
            new("permission", "payroll.export"),
            // A request carrying exits also needs the manual-Paid permission (final gate security P2-D).
            new("permission", "payroll.wps_status.update"),
            new("is_group_scope", "true"),
        }, "Test"));
        return new BankConfirmationsController(db, Service(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
    }

    private static async Task<JsonElement> History(ZayraDbContext db, World w) =>
        Element(Assert.IsType<OkObjectResult>(await Controller(db, w).History(w.Batch.Id, default)).Value!);

    /// <summary>One reasoned exit naming one record (N2: no file-wide switches).</summary>
    private static BankConfirmationImportOptions ExitFor(string kind, PayrollPaymentRecord record, string? reason = Reason,
        string? reference = Reference) =>
        new(Exits: new[] { new BankLineExit(kind, record.Id, null, reason, reference) });

    /// <summary>The import endpoint with a one-row structured payload carrying one exit.</summary>
    private static async Task<ObjectResult> ImportThroughEndpoint(ZayraDbContext db, World w, string word, string kind, Guid recordId,
        string? reason)
    {
        var ctrl = Controller(db, w);
        var payload = JsonSerializer.Serialize(new
        {
            fileName = "letter.json",
            rows = new[] { new { wpsReference = "W1", amount = 100m, status = word, reasonCode = "AC04", valueDate = "2026-07-30" } },
            exits = new[] { new { kind, paymentRecordId = recordId, reason, bankReference = Reference } },
        });
        ctrl.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        ctrl.ControllerContext.HttpContext.Request.ContentType = "application/json";
        return Assert.IsAssignableFrom<ObjectResult>(await ctrl.Import(w.Batch.Id, ct: default));
    }

    // ── B1: the period handoff agrees with the batch history ─────────────────────────────────────

    [Fact]
    public async Task Handoff_Rejection_IsNotReturnedMoney_IsListedOnItsOwn_AndBlocksWithItsOwnCode()
    {
        using var db = NewDb();
        var w = Seed(db);
        await Import(db, w, "RJCT", "AC01");

        var h = await Handoff(db, w);

        h.GetProperty("deltas").GetProperty("returnedAmountAwaitingGlCorrection").GetProperty("amount").GetDecimal().Should().Be(0m);
        h.GetProperty("payments").GetProperty("returns").GetArrayLength().Should().Be(0);
        var rejections = h.GetProperty("payments").GetProperty("rejections");
        rejections.GetArrayLength().Should().Be(1);
        Json(rejections).Should().Contain(Json(BankOutcomeLabels.NotPaidRejectedByBank.English))
            .And.Contain(Json(BankStatusReasonCatalogue.Describe("AC01").English)).And.Contain("\"reasonCode\":\"AC01\"");
        Blockers(h).Should().Contain("unpaid_rejected_payments").And.NotContain("unresolved_returned_payments")
            .And.NotContain("gl_correction_awaiting");
        h.GetProperty("landed").GetBoolean().Should().BeFalse("an unpaid salary never lets the month land");

        var history = await History(db, w);
        history.GetProperty("returnedAmountAwaitingGlCorrection").GetDecimal().Should().Be(0m, "the two reports agree");
    }

    [Fact]
    public async Task Handoff_Return_StillAwaitsTheReturnedMoneyCorrection()
    {
        using var db = NewDb();
        var w = Seed(db);
        // Settled first: returned money needs the correction only once the settlement journal credited Cash/Bank (owner ruling
        // 2026-11-17). The unsettled case is BankOutcomeHoldClosureTests.AReturnBeforeSettlement_*.
        Settle(db, w, DateTime.UtcNow.AddDays(-1));
        await Import(db, w, "RETURNED", "AC04");

        var h = await Handoff(db, w);

        h.GetProperty("deltas").GetProperty("returnedAmountAwaitingGlCorrection").GetProperty("amount").GetDecimal().Should().Be(100m);
        h.GetProperty("payments").GetProperty("returns").GetArrayLength().Should().Be(1);
        h.GetProperty("payments").GetProperty("rejections").GetArrayLength().Should().Be(0);
        Blockers(h).Should().Contain("unresolved_returned_payments").And.Contain("gl_correction_awaiting")
            .And.NotContain("unpaid_rejected_payments");
    }

    // ── C1: a rejection after the settlement journal needs the GL correction ─────────────────────

    [Fact]
    public async Task RejectionBeforeSettlement_NeedsNoGlCorrection()
    {
        using var db = NewDb();
        var w = Seed(db);

        var result = await Import(db, w, "RJCT", "AM04");

        result.RejectedAmountNotPaid.Should().Be(100m);
        result.RejectedAfterSettlementAwaitingGlCorrection.Should().Be(0m);
        var h = await Handoff(db, w);
        h.GetProperty("deltas").GetProperty("rejectedAfterSettlementAwaitingGlCorrection").GetProperty("amount").GetDecimal().Should().Be(0m);
        Blockers(h).Should().NotContain("gl_correction_awaiting");
        (await History(db, w)).GetProperty("rejectedAfterSettlementAwaitingGlCorrection").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task RejectionAfterSettlement_NeedsTheGlCorrection_UnderTheNotPaidLabel()
    {
        using var db = NewDb();
        var w = Seed(db);
        Settle(db, w, DateTime.UtcNow.AddDays(-1));

        var result = await Import(db, w, "RJCT", "AM04");

        result.RejectedAmountNotPaid.Should().Be(100m);
        result.RejectedAfterSettlementAwaitingGlCorrection.Should().Be(100m, "cash was credited for a salary that never left");
        result.ReturnedAmountAwaitingGlCorrection.Should().Be(0m, "it is still not money that came back");

        var h = await Handoff(db, w);
        var figure = h.GetProperty("deltas").GetProperty("rejectedAfterSettlementAwaitingGlCorrection");
        figure.GetProperty("amount").GetDecimal().Should().Be(100m);
        Json(figure).Should().Contain(Json(BankOutcomeLabels.NotPaidRejectedByBank.English)).And.NotContainEquivalentOf("came back");
        h.GetProperty("deltas").GetProperty("returnedAmountAwaitingGlCorrection").GetProperty("amount").GetDecimal().Should().Be(0m);
        Blockers(h).Should().Contain("gl_correction_awaiting").And.Contain("unpaid_rejected_payments");

        (await History(db, w)).GetProperty("rejectedAfterSettlementAwaitingGlCorrection").GetDecimal().Should().Be(100m);
    }

    // N1: settlement is refused while any record is failed, so a Rejected record in a batch with a live settlement journal
    // always needs the correction, even when the import's timestamp is just before the journal's (an import racing the settle).
    [Fact]
    public async Task ARejectedRecordInASettledBatch_NeedsTheCorrection_EvenWhenTheImportRacedTheSettle()
    {
        using var db = NewDb();
        var w = Seed(db);
        await Import(db, w, "RJCT", "AM04");
        Settle(db, w, DateTime.UtcNow.AddMinutes(1));

        var h = await Handoff(db, w);

        h.GetProperty("deltas").GetProperty("rejectedAfterSettlementAwaitingGlCorrection").GetProperty("amount").GetDecimal().Should().Be(100m);
        Blockers(h).Should().Contain("gl_correction_awaiting");
        (await History(db, w)).GetProperty("rejectedAfterSettlementAwaitingGlCorrection").GetDecimal().Should().Be(100m);
    }

    // N1, Fable's sequence: RJCT applied, reversed to Paid, the batch settled, then a DUPL line confirmed not paid.
    [Fact]
    public async Task ARejectionDecidedByAnExitAfterSettlement_NeedsTheCorrection()
    {
        using var db = NewDb();
        var w = Seed(db);
        await Import(db, w, "RJCT", "AC01");
        await Import(db, w, new BankConfirmationImportOptions(AllowReturnReversal: true, ReturnReversalReason: "Bank re-ran the line"),
            ("W1", 100m, "PAID", null));
        Settle(db, w, DateTime.UtcNow);
        await Import(db, w, "FAILED", "DUPL");

        var result = await Import(db, w, ExitFor(BankLineExitKinds.ConfirmDuplicateNotPaid, w.R(1)), ("W1", 100m, "FAILED", "dupl"));

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Rejected);
        result.RejectedAfterSettlementAwaitingGlCorrection.Should().Be(100m, "the settlement credited cash for a salary that never left");
        Blockers(await Handoff(db, w)).Should().Contain("gl_correction_awaiting");
    }

    // T2: the settlement journal is matched to THIS batch by its number, not just by the run.
    [Fact]
    public async Task AnotherBatchOfTheSameRunBeingSettled_IsNotThisBatchsSettlement()
    {
        using var db = NewDb();
        var w = Seed(db);
        db.FinanceGlEntries.AddRange(
            new FinanceGlEntry
            {
                TenantId = w.TenantId, CompanyId = w.CompanyId, SourceModule = "Payroll", SourceEntityId = w.Run.Id,
                SourceEntityRef = "PB-2026-07-OTHER", EventType = GlEventTypes.NetSettlement, DebitAccount = "2100 - Salaries Payable",
                CreditAccount = "1000 - Cash/Bank", Description = "Net pay settlement", Amount = 50m, Currency = "SAR",
                EntryDate = new DateOnly(2026, 7, 31), Period = "2026-07", CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
            },
            new FinanceGlEntry
            {
                TenantId = w.TenantId, CompanyId = w.CompanyId, SourceModule = "Payroll", SourceEntityId = w.Run.Id,
                SourceEntityRef = "OUTSIDE-BANK/" + w.Batch.BatchNumber + "/1", EventType = GlEventTypes.NetSettlement,
                DebitAccount = "2100 - Salaries Payable", CreditAccount = "1000 - Cash/Bank", Description = "Paid outside the bank file",
                Amount = 10m, Currency = "SAR", EntryDate = new DateOnly(2026, 7, 31), Period = "2026-07", CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
            });
        db.SaveChanges();

        var result = await Import(db, w, "RJCT", "AM04");

        result.RejectedAfterSettlementAwaitingGlCorrection.Should().Be(0m, "this batch has no settlement journal of its own");
        Blockers(await Handoff(db, w)).Should().NotContain("gl_correction_awaiting");
    }

    [Fact]
    public async Task AReversedSettlement_IsNotASettlement()
    {
        using var db = NewDb();
        var w = Seed(db);
        Settle(db, w, DateTime.UtcNow.AddDays(-1));
        db.FinanceGlEntries.Single(e => e.EventType == GlEventTypes.NetSettlement).IsReversed = true;
        db.SaveChanges();

        var result = await Import(db, w, "RJCT", "AM04");

        result.RejectedAfterSettlementAwaitingGlCorrection.Should().Be(0m);
    }

    [Fact]
    public async Task ImportEndpoint_TheRejectedNoteNoLongerClaimsNoCashLeft()
    {
        using var db = NewDb();
        var w = Seed(db);
        Settle(db, w, DateTime.UtcNow.AddDays(-1));
        var ctrl = Controller(db, w);
        ctrl.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Csv(("W1", 100m, "RJCT", "AM04"))));
        ctrl.ControllerContext.HttpContext.Request.ContentType = "text/csv";

        var body = Element(Assert.IsType<OkObjectResult>(await ctrl.Import(w.Batch.Id, ct: default)).Value!);

        body.GetProperty("rejectedAfterSettlementAwaitingGlCorrection").GetProperty("amount").GetDecimal().Should().Be(100m);
        body.GetProperty("rejectedAmountNotPaid").GetProperty("note").GetString().Should().NotContain("no money left");
    }

    // ── B2: rejection after Paid ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PaidThenRejectedHold_BlocksTheHandoff_AndShowsOnTheHistory()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);

        await Import(db, w, "RJCT", "AC04");

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
        var h = await Handoff(db, w);
        Blockers(h).Should().Contain("rejection_after_paid_held");
        h.GetProperty("landed").GetBoolean().Should().BeFalse();
        Json(h.GetProperty("payments").GetProperty("heldForPerson")).Should().Contain("paid_then_rejected_held");

        var history = await History(db, w);
        Json(history.GetProperty("heldForPerson")).Should().Contain("paid_then_rejected_held");
    }

    // The guard: an informational Pending line is never "the latest word" on a record. Without it, a later PENDING line
    // would become the record's latest row and silently close the Returned <-> Rejected hold.
    [Fact]
    public async Task AHold_StaysOpen_WhenALaterFileOnlySaysPending()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Returned);
        await Import(db, w, "RJCT", "AC04");

        var later = await Import(db, w, "PENDING");

        later.HeldForPerson.Should().ContainSingle("an informational line resolves nothing");
        Blockers(await Handoff(db, w)).Should().Contain("bank_outcome_change_held");
    }

    [Fact]
    public async Task PaidThenRejected_ExitNamingTheRecord_MovesToRejected_Audited_AndClearsTheHold()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);
        Settle(db, w, DateTime.UtcNow.AddDays(-1));
        await Import(db, w, "RJCT", "AC04");

        // A different file (the bank's later letter), so it is not refused as the same file imported twice.
        var result = await Import(db, w, ExitFor(BankLineExitKinds.AllowPaidToRejected, w.R(1)), ("W1", 100m, "REJECTED", "AC04"));

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Rejected);
        result.RejectedApplied.Should().Be(1);
        result.Overrides.Should().ContainSingle();
        Json(result.Overrides).Should().Contain("\"from\":\"Paid\"").And.Contain("\"to\":\"Rejected\"")
            .And.Contain(Reason).And.Contain(Reference);
        result.LateReturns.Should().BeEmpty();
        result.ReturnedAmountAwaitingGlCorrection.Should().Be(0m);
        result.RejectedAfterSettlementAwaitingGlCorrection.Should().Be(100m);

        var audit = db.PayrollAuditLogs.AsEnumerable().Single(a => a.MetadataJson!.Contains(BankLineExitKinds.AllowPaidToRejected));
        audit.MetadataJson.Should().Contain(Reason).And.Contain(Reference)
            .And.Contain("\"from\":\"Paid\"").And.Contain("\"to\":\"Rejected\"").And.Contain(w.R(1).Id.ToString());

        var blockers = Blockers(await Handoff(db, w));
        blockers.Should().NotContain("rejection_after_paid_held").And.Contain("unpaid_rejected_payments").And.Contain("gl_correction_awaiting");
    }

    public static readonly TheoryData<string, string?, string?> ExitsMissingReasonOrReference = new()
    {
        { BankLineExitKinds.AllowPaidToRejected, null, Reference }, { BankLineExitKinds.AllowPaidToRejected, Reason, null },
        { BankLineExitKinds.AllowPaidToRejected, " ", Reference }, { BankLineExitKinds.AllowPaidToRejected, Reason, " " },
        { BankLineExitKinds.ConfirmPaidDespiteRejection, null, Reference }, { BankLineExitKinds.ConfirmPaidDespiteRejection, Reason, null },
        { BankLineExitKinds.ConfirmOriginalPaymentReceived, null, Reference }, { BankLineExitKinds.ConfirmOriginalPaymentReceived, Reason, null },
        { BankLineExitKinds.ConfirmDuplicateNotPaid, null, Reference }, { BankLineExitKinds.ConfirmDuplicateNotPaid, Reason, " " },
        { BankLineExitKinds.AllowFailedOutcomeChange, null, null }, { BankLineExitKinds.AllowFailedOutcomeChange, " ", null },
    };

    [Theory]
    [MemberData(nameof(ExitsMissingReasonOrReference))]
    public async Task EveryExit_NeedsAReason_AndABankReferenceWhereStated(string kind, string? reason, string? reference)
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);

        var (result, refusal) = await TryImport(db, w, ExitFor(kind, w.R(1), reason, reference), Csv(("W1", 100m, "RJCT", "DUPL")));

        result.Should().BeNull();
        refusal!.Status.Should().Be(400);
        Json(refusal.Payload).Should().Contain("override_reason_required");
        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
    }

    [Fact]
    public async Task ImportEndpoint_ReadsPerRecordExitsFromTheStructuredPayload_AndRefusesOneWithoutAReason()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);
        await Import(db, w, "RJCT", "AC04");

        var refused = await ImportThroughEndpoint(db, w, "REJECTED", BankLineExitKinds.AllowPaidToRejected, w.R(1).Id, reason: null);
        refused.StatusCode.Should().Be(400);
        Json(refused.Value!).Should().Contain("override_reason_required");
        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);

        var ok = await ImportThroughEndpoint(db, w, "REJECTED", BankLineExitKinds.AllowPaidToRejected, w.R(1).Id, Reason);
        ok.StatusCode.Should().Be(200);
        w.R(1).Status.Should().Be(PaymentRecordStatuses.Rejected);
    }

    // ── B2: Returned <-> Rejected ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Returned", "RJCT", "Rejected")]
    [InlineData("Rejected", "RETURNED", "Returned")]
    public async Task FailedOutcomeChange_ExitNamingTheRecord_AppliesTheNewOutcome_Audited(string previous, string word, string expected)
    {
        using var db = NewDb();
        var w = Seed(db, previous);

        var held = await Import(db, w, word);
        w.R(1).Status.Should().Be(previous);
        held.HeldForPerson.Should().ContainSingle();

        var result = await Import(db, w, ExitFor(BankLineExitKinds.AllowFailedOutcomeChange, w.R(1), reference: null), ("W1", 100m, word, "AC04"));

        w.R(1).Status.Should().Be(expected);
        result.Overrides.Should().ContainSingle();
        result.HeldForPerson.Should().BeEmpty();
        db.PayrollAuditLogs.AsEnumerable().Single(a => a.MetadataJson!.Contains(BankLineExitKinds.AllowFailedOutcomeChange)).MetadataJson
            .Should().Contain(Reason).And.Contain($"\"from\":\"{previous}\"").And.Contain($"\"to\":\"{expected}\"");
    }

    [Fact]
    public async Task FailedOutcomeChange_ReleasesALegacyRejectionStoredAsReturned()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Returned);
        Settle(db, w, DateTime.UtcNow.AddDays(-4));   // the correction figure exists only for a settled batch (owner ruling 2026-11-17)
        // What an import before F1 left behind: an RJCT line stored as Returned, counted as returned money.
        db.BankPaymentConfirmations.Add(new BankPaymentConfirmation
        {
            TenantId = w.TenantId, PaymentBatchId = w.Batch.Id, PaymentRecordId = w.R(1).Id, EmployeeId = 1,
            Outcome = BankConfirmationOutcomes.Returned, RawOutcome = "RJCT", PreviousStatus = "Pending", Applied = true,
            ConfirmedAmount = 100m, RecordAmount = 100m, MatchedBy = BankConfirmationMatchModes.WpsReference,
            ImportBatchId = Guid.NewGuid(), SourceFileName = "old.csv", SourceFileHash = "old", ParserKey = "generic-csv",
            ImportedAtUtc = DateTime.UtcNow.AddDays(-3),
        });
        db.SaveChanges();
        (await Handoff(db, w)).GetProperty("deltas").GetProperty("returnedAmountAwaitingGlCorrection").GetProperty("amount").GetDecimal().Should().Be(100m);
        await Import(db, w, "RJCT", "AC01");   // the bank's word now: held, Returned <-> Rejected

        await Import(db, w, ExitFor(BankLineExitKinds.AllowFailedOutcomeChange, w.R(1), reference: null), ("W1", 100m, "REJECTED", "AC01"));

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Rejected);
        (await Handoff(db, w)).GetProperty("deltas").GetProperty("returnedAmountAwaitingGlCorrection").GetProperty("amount").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task AnExitOfOneKind_DoesNotResolveAHoldOfAnother()
    {
        using var db = NewDb();
        var w = Seed(db, (100m, PaymentRecordStatuses.Paid), (100m, PaymentRecordStatuses.Returned));
        await Import(db, w, null, ("W1", 100m, "RJCT", null), ("W2", 100m, "RJCT", null));   // both held, different kinds

        var (_, wrongForPaid) = await TryImport(db, w, ExitFor(BankLineExitKinds.AllowFailedOutcomeChange, w.R(1), reference: null),
            Csv(("W1", 100m, "REJECTED", null)));
        var (_, wrongForReturned) = await TryImport(db, w, ExitFor(BankLineExitKinds.AllowPaidToRejected, w.R(2)),
            Csv(("W2", 100m, "REJECTED", null)));

        foreach (var refusal in new[] { wrongForPaid, wrongForReturned })
        {
            refusal!.Status.Should().Be(400);
            Json(refusal.Payload).Should().Contain("exit_matches_no_open_hold");
        }
        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
        w.R(2).Status.Should().Be(PaymentRecordStatuses.Returned);
    }

    // ── Ruling 4: DUPL is held, never "not paid" ─────────────────────────────────────────────────

    [Theory]
    [InlineData("RJCT")]
    [InlineData("FAILED")]
    [InlineData("RETURNED")]
    [InlineData("PAID")]
    public async Task DuplicateReason_IsHeldForAPerson_WhateverTheOutcome(string word)
    {
        using var db = NewDb();
        var w = Seed(db);

        var result = await Import(db, w, word, "DUPL");

        w.R(1).Status.Should().Be("Pending");
        result.Rejections.Should().BeEmpty();
        result.RejectedAmountNotPaid.Should().Be(0m);
        result.ReturnedAmountAwaitingGlCorrection.Should().Be(0m);
        var evidence = db.BankPaymentConfirmations.Single();
        evidence.Applied.Should().BeFalse();
        evidence.HoldReason.Should().Be("duplicate_rejection_held");
        var refused = Json(result.Refused);
        refused.Should().Contain("duplicate_rejection_held")
            .And.Contain(Json(BankOutcomeLabels.HeldDuplicateRejection.English))
            .And.Contain(Json(BankOutcomeLabels.HeldDuplicateRejection.Arabic))
            .And.NotContain(Json(BankOutcomeLabels.NotPaidRejectedByBank.English));
    }

    [Fact]
    public async Task DuplicateRejectionAfterPaid_BlocksTheHandoff()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);

        await Import(db, w, "RJCT", "dupl");

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
        Blockers(await Handoff(db, w)).Should().Contain("duplicate_rejection_held");
        Json((await History(db, w)).GetProperty("heldForPerson")).Should().Contain("duplicate_rejection_held");
    }

    // ── No one-way traps: every hold has a reasoned exit in both directions, per record (N2) ────

    private const string Reason = "Checked the bank statement line with the bank";
    private const string Reference = "STMT-2026-07-30-0042";

    [Fact]
    public async Task PaidThenRejected_ConfirmPaidDespiteRejection_KeepsPaid_ClearsTheHold_Audited()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);
        await Import(db, w, "RJCT", "AC04");
        Blockers(await Handoff(db, w)).Should().Contain("rejection_after_paid_held");

        var result = await Import(db, w, ExitFor(BankLineExitKinds.ConfirmPaidDespiteRejection, w.R(1)), ("W1", 100m, "REJECTED", "AC04"));

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
        result.HeldForPerson.Should().BeEmpty();
        result.Rejections.Should().BeEmpty();
        Json(result.Overrides).Should().Contain(BankLineExitKinds.ConfirmPaidDespiteRejection).And.Contain("\"from\":\"Paid\"").And.Contain("\"to\":\"Paid\"");
        db.PayrollAuditLogs.AsEnumerable().Single(a => a.MetadataJson!.Contains(BankLineExitKinds.ConfirmPaidDespiteRejection)).MetadataJson
            .Should().Contain(Reason).And.Contain(Reference);
        var blockers = Blockers(await Handoff(db, w));
        blockers.Should().NotContain("rejection_after_paid_held").And.NotContain("unpaid_rejected_payments");
    }

    [Fact]
    public async Task PaidThenRejected_ALaterFileSayingOnlyPaid_StillDoesNotClearTheHold()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);
        await Import(db, w, "RJCT", "AC04");

        await Import(db, w, "PAID");

        Blockers(await Handoff(db, w)).Should().Contain("rejection_after_paid_held");
    }

    [Theory]
    [InlineData("RJCT", "Pending")]
    [InlineData("FAILED", "Paid")]
    [InlineData("RETURNED", "Returned")]
    [InlineData("PAID", "Pending")]   // the bank's word agrees with the person: still recorded as the exit
    public async Task Duplicate_ConfirmOriginalPaymentReceived_MakesItPaid_ClearsTheHold(string word, string previous)
    {
        using var db = NewDb();
        var w = Seed(db, previous);
        await Import(db, w, word, "DUPL");
        Blockers(await Handoff(db, w)).Should().Contain("duplicate_rejection_held");

        var result = await Import(db, w, ExitFor(BankLineExitKinds.ConfirmOriginalPaymentReceived, w.R(1)), ("W1", 100m, word, "dupl"));

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
        result.HeldForPerson.Should().BeEmpty();
        Json(result.Overrides).Should().Contain(BankLineExitKinds.ConfirmOriginalPaymentReceived).And.Contain($"\"from\":\"{previous}\"").And.Contain("\"to\":\"Paid\"");
        db.PayrollAuditLogs.AsEnumerable().Single(a => a.MetadataJson!.Contains(BankLineExitKinds.ConfirmOriginalPaymentReceived)).MetadataJson
            .Should().Contain(Reason).And.Contain(Reference);
        Blockers(await Handoff(db, w)).Should().NotContain("duplicate_rejection_held");
    }

    [Theory]
    [InlineData("RJCT", "Pending")]
    [InlineData("PAID", "Pending")]
    [InlineData("FAILED", "Paid")]
    public async Task Duplicate_ConfirmDuplicateNotPaid_MakesItRejected_NotPaid_ClearsTheHold(string word, string previous)
    {
        using var db = NewDb();
        var w = Seed(db, previous);
        await Import(db, w, word, "DUPL");

        var result = await Import(db, w, ExitFor(BankLineExitKinds.ConfirmDuplicateNotPaid, w.R(1)), ("W1", 100m, word, "dupl"));

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Rejected);
        result.RejectedAmountNotPaid.Should().Be(100m);
        result.Rejections.Should().ContainSingle();
        Json(result.Rejections).Should().Contain(Json(BankOutcomeLabels.NotPaidRejectedByBank.English));
        result.HeldForPerson.Should().BeEmpty();
        Json(result.Overrides).Should().Contain(BankLineExitKinds.ConfirmDuplicateNotPaid).And.Contain("\"to\":\"Rejected\"");
        var blockers = Blockers(await Handoff(db, w));
        blockers.Should().NotContain("duplicate_rejection_held").And.Contain("unpaid_rejected_payments");
    }

    // CTO ruling: "when in doubt, hold". A DUPL hold clears ONLY through its two explicit exits, never through a later
    // bank line that happens to apply.
    [Theory]
    [InlineData("Pending", "PAID")]
    [InlineData("Pending", "RJCT")]
    [InlineData("Paid", "RETURNED")]
    public async Task DuplicateHold_IsNotClearedByALaterAppliedBankLine(string previous, string laterWord)
    {
        using var db = NewDb();
        var w = Seed(db, previous);
        await Import(db, w, "RJCT", "DUPL");

        var later = await Import(db, w, laterWord);

        db.BankPaymentConfirmations.OrderBy(c => c.ImportedAtUtc).Last().Applied.Should().BeTrue("the later line itself applies");
        later.HeldForPerson.Should().ContainSingle();
        Json(later.HeldForPerson).Should().Contain("duplicate_rejection_held");
        Blockers(await Handoff(db, w)).Should().Contain("duplicate_rejection_held");
        Json((await History(db, w)).GetProperty("heldForPerson")).Should().Contain("duplicate_rejection_held");
    }

    [Fact]
    public async Task AnExitResolvesOnlyTheRecordItNames_NeverTheWholeFile()
    {
        using var db = NewDb();
        var w = Seed(db, (100m, PaymentRecordStatuses.Paid), (100m, PaymentRecordStatuses.Paid));
        await Import(db, w, null, ("W1", 100m, "RJCT", "AC04"), ("W2", 100m, "RJCT", "AC04"));   // two open holds

        var result = await Import(db, w, ExitFor(BankLineExitKinds.AllowPaidToRejected, w.R(1)),
            ("W1", 100m, "REJECTED", "AC04"), ("W2", 100m, "REJECTED", "AC04"));

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Rejected);
        w.R(2).Status.Should().Be(PaymentRecordStatuses.Paid, "the exit named only the first record");
        result.Overrides.Should().ContainSingle();
        Json(result.HeldForPerson).Should().Contain(w.R(2).Id.ToString()).And.NotContain(w.R(1).Id.ToString());
    }

    [Fact]
    public async Task EachRecordCarriesItsOwnReason()
    {
        using var db = NewDb();
        var w = Seed(db, (100m, PaymentRecordStatuses.Paid), (100m, PaymentRecordStatuses.Paid));
        await Import(db, w, null, ("W1", 100m, "RJCT", "AC04"), ("W2", 100m, "RJCT", "AC04"));

        var options = new BankConfirmationImportOptions(Exits: new[]
        {
            new BankLineExit(BankLineExitKinds.AllowPaidToRejected, w.R(1).Id, null, "Letter A from the bank", "BNK-A"),
            new BankLineExit(BankLineExitKinds.ConfirmPaidDespiteRejection, null, "W2", "Statement line B", "STMT-B"),
        });
        var result = await Import(db, w, options, ("W1", 100m, "REJECTED", "AC04"), ("W2", 100m, "REJECTED", "AC04"));

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Rejected);
        w.R(2).Status.Should().Be(PaymentRecordStatuses.Paid);
        result.HeldForPerson.Should().BeEmpty();
        var overrides = Json(result.Overrides);
        overrides.Should().Contain("Letter A from the bank").And.Contain("BNK-A").And.Contain("Statement line B").And.Contain("STMT-B");
    }

    [Fact]
    public async Task AnExitOnARecordWithNoOpenHoldOfThatKind_IsRefused()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);

        var (_, refusal) = await TryImport(db, w, ExitFor(BankLineExitKinds.AllowPaidToRejected, w.R(1)), Csv(("W1", 100m, "RJCT", "AC04")));

        refusal!.Status.Should().Be(400);
        Json(refusal.Payload).Should().Contain("exit_matches_no_open_hold");
        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
        db.BankPaymentConfirmations.Should().BeEmpty();
    }

    [Fact]
    public async Task AnExitNamingARecordOutsideTheBatch_IsRefused()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);
        await Import(db, w, "RJCT", "AC04");

        var (_, refusal) = await TryImport(db, w,
            new BankConfirmationImportOptions(Exits: new[] { new BankLineExit(BankLineExitKinds.AllowPaidToRejected, Guid.NewGuid(), null, Reason, Reference) }),
            Csv(("W1", 100m, "REJECTED", "AC04")));

        refusal!.Status.Should().Be(400);
        Json(refusal.Payload).Should().Contain("exit_matches_no_open_hold");
    }

    [Fact]
    public async Task AnExitWhoseLineIsNotInTheFile_IsRefused()
    {
        using var db = NewDb();
        var w = Seed(db, (100m, PaymentRecordStatuses.Paid), (100m, "Pending"));
        await Import(db, w, null, ("W1", 100m, "RJCT", "AC04"));

        var (_, refusal) = await TryImport(db, w, ExitFor(BankLineExitKinds.AllowPaidToRejected, w.R(1)), Csv(("W2", 100m, "PAID", null)));

        refusal!.Status.Should().Be(400);
        Json(refusal.Payload).Should().Contain("exit_line_not_in_file");
        w.R(2).Status.Should().Be("Pending", "nothing in a refused import is applied");
    }

    [Theory]
    [InlineData(BankLineExitKinds.AllowPaidToRejected, "PAID", null)]           // the line no longer says rejected
    [InlineData(BankLineExitKinds.ConfirmDuplicateNotPaid, "RJCT", "AC01")]     // a DUPL exit on a line with no DUPL code
    public async Task AnExitWhoseLineDoesNotFit_IsRefused(string kind, string word, string? reason)
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);
        await Import(db, w, "RJCT", kind == BankLineExitKinds.ConfirmDuplicateNotPaid ? "DUPL" : "AC04");

        var (_, refusal) = await TryImport(db, w, ExitFor(kind, w.R(1)), Csv(("W1", 100m, word, reason)));

        refusal!.Status.Should().Be(400);
        Json(refusal.Payload).Should().Contain("exit_line_does_not_fit");
        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
    }

    [Fact]
    public async Task TwoExitsOnOneRecord_AreRefused()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);
        await Import(db, w, "RJCT", "AC04");

        var (_, refusal) = await TryImport(db, w, new BankConfirmationImportOptions(Exits: new[]
        {
            new BankLineExit(BankLineExitKinds.AllowPaidToRejected, w.R(1).Id, null, Reason, Reference),
            new BankLineExit(BankLineExitKinds.ConfirmPaidDespiteRejection, null, "W1", Reason, Reference),
        }), Csv(("W1", 100m, "REJECTED", "AC04")));

        refusal!.Status.Should().Be(400);
        Json(refusal.Payload).Should().Contain("opposing_exits");
        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
    }

    [Fact]
    public async Task AnExitOnTheSameFileAlreadyImported_IsRefused_NeverASilentNoOp()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);
        await Import(db, w, "RJCT", "AC04");

        var (_, refusal) = await TryImport(db, w, ExitFor(BankLineExitKinds.ConfirmPaidDespiteRejection, w.R(1)), Csv(("W1", 100m, "RJCT", "AC04")));

        refusal!.Status.Should().Be(409);
        Json(refusal.Payload).Should().Contain("exit_needs_the_line_again");
        Blockers(await Handoff(db, w)).Should().Contain("rejection_after_paid_held");
    }

    // T1: an exit never overrides the amount check.
    [Theory]
    [InlineData(BankLineExitKinds.AllowPaidToRejected, "Paid", "RJCT", "AC04", "REJECTED", "AC04")]
    [InlineData(BankLineExitKinds.ConfirmPaidDespiteRejection, "Paid", "RJCT", "AC04", "REJECTED", "AC04")]
    [InlineData(BankLineExitKinds.AllowFailedOutcomeChange, "Returned", "RJCT", "AC04", "REJECTED", "AC04")]
    [InlineData(BankLineExitKinds.ConfirmOriginalPaymentReceived, "Pending", "RJCT", "DUPL", "REJECTED", "dupl")]
    [InlineData(BankLineExitKinds.ConfirmDuplicateNotPaid, "Pending", "RJCT", "DUPL", "REJECTED", "dupl")]
    public async Task AnExitWithADifferentAmount_KeepsTheAmountMismatchHold(
        string kind, string previous, string heldWord, string heldReason, string exitWord, string exitReason)
    {
        using var db = NewDb();
        var w = Seed(db, previous);
        await Import(db, w, heldWord, heldReason);

        var result = await Import(db, w, ExitFor(kind, w.R(1), reference: kind == BankLineExitKinds.AllowFailedOutcomeChange ? null : Reference),
            ("W1", 90m, exitWord, exitReason));

        w.R(1).Status.Should().Be(previous, "a different figure is held, whatever exit is asked for");
        result.AmountMismatchesHeld.Should().ContainSingle();
        result.Overrides.Should().BeEmpty();
        result.HeldForPerson.Should().ContainSingle("the person-hold is still open");
    }

    // ── Ruling 5: the reason text never invents "no reason code" ─────────────────────────────────

    private const string NoCode = "The bank gave no reason code";

    [Fact]
    public async Task DryRun_ReadsTheStoredReason_AndNeverClaimsTheBankGaveNone()
    {
        using var db = NewDb();
        var w = Seed(db, (100m, "Pending"), (200m, "Pending"));
        await Import(db, w, null, ("W1", 100m, "RJCT", "AC01"));

        var dry = await Import(db, w, new BankConfirmationImportOptions(DryRun: true), ("W2", 200m, "RJCT", "AC06"));

        dry.DryRun.Should().BeTrue();
        dry.RejectedApplied.Should().Be(1, "the counters say what the import would do");
        w.R(2).Status.Should().Be("Pending", "a dry run changes nothing");
        db.BankPaymentConfirmations.Count().Should().Be(1);
        dry.Rejections.Should().ContainSingle();
        var listed = Json(dry.Rejections);
        listed.Should().Contain("\"reasonCode\":\"AC01\"").And.Contain(Json(BankStatusReasonCatalogue.Describe("AC01").English));
        listed.Should().NotContain(NoCode);
        dry.RejectedAmountNotPaid.Should().Be(100m);
    }

    [Fact]
    public async Task ARecordTheFileDidNotMention_KeepsItsStoredReason()
    {
        using var db = NewDb();
        var w = Seed(db, (100m, "Pending"), (200m, "Pending"));
        await Import(db, w, null, ("W1", 100m, "RJCT", "BE01"));

        var second = await Import(db, w, null, ("W2", 200m, "PAID", null));

        var listed = Json(second.Rejections);
        listed.Should().Contain("\"reasonCode\":\"BE01\"").And.Contain(Json(BankStatusReasonCatalogue.Describe("BE01").English)).And.NotContain(NoCode);
    }

    [Fact]
    public async Task ARejectionWithNoCode_SaysTheBankGaveNone()
    {
        using var db = NewDb();
        var w = Seed(db);

        var result = await Import(db, w, "RJCT");

        Json(result.Rejections).Should().Contain(NoCode);
    }

    [Fact]
    public async Task ARecordFailedWithNoStoredConfirmation_HasNoReasonDescription()
    {
        using var db = NewDb();
        var w = Seed(db, (100m, PaymentRecordStatuses.Rejected), (200m, "Pending"));

        var result = await Import(db, w, null, ("W2", 200m, "PAID", null));

        var listed = Json(result.Rejections);
        listed.Should().NotContain(NoCode, "nothing was looked up, so nothing is asserted");
        listed.Should().Contain("\"reasonDescription\":null");
    }

    [Fact]
    public async Task AnUnknownReasonCode_ThroughTheImport_SaysAskYourBank()
    {
        using var db = NewDb();
        var w = Seed(db);

        var result = await Import(db, w, "RJCT", "XY99");

        Json(result.Rejections).Should().Contain(Json(BankStatusReasonCatalogue.Describe("XY99").English));
    }

    // ── Totals, mismatch, repeats, audit ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MixedBatch_KeepsPaidReturnedAndRejectedAsSeparateSums()
    {
        using var db = NewDb();
        var w = Seed(db, (100m, "Pending"), (200m, "Pending"), (300m, "Pending"), (400m, "Pending"));
        Settle(db, w, DateTime.UtcNow.AddDays(-1));   // the returned figure is a GL correction only after settlement (owner ruling 2026-11-17)

        var result = await Import(db, w, null,
            ("W1", 100m, "PAID", null), ("W2", 200m, "RETURNED", "AC04"), ("W3", 300m, "RJCT", "AC01"));

        result.Coverage.PaidTotal.Should().Be(100m);
        result.Coverage.ReturnedTotal.Should().Be(200m);
        result.Coverage.RejectedTotal.Should().Be(300m);
        result.Coverage.UnconfirmedTotal.Should().Be(400m);
        result.Coverage.ReconciliationDelta.Should().Be(0m);
        result.ReturnedAmountAwaitingGlCorrection.Should().Be(200m);
        result.RejectedAmountNotPaid.Should().Be(300m);
        result.Returns.Should().ContainSingle();
        result.Rejections.Should().ContainSingle();

        var h = await Handoff(db, w);
        h.GetProperty("deltas").GetProperty("returnedAmountAwaitingGlCorrection").GetProperty("amount").GetDecimal().Should().Be(200m);
        h.GetProperty("payments").GetProperty("rejections").GetArrayLength().Should().Be(1);
        h.GetProperty("payments").GetProperty("returns").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task RejectedWithAnAmountMismatch_IsHeld_UnlessAcknowledged()
    {
        using var db = NewDb();
        var w = Seed(db);

        var held = await Import(db, w, null, ("W1", 90m, "RJCT", "AC01"));
        w.R(1).Status.Should().Be("Pending");
        held.AmountMismatchesHeld.Should().ContainSingle();
        held.Rejections.Should().BeEmpty();

        // The bank's next file (a different file: the same one would be refused as imported twice).
        var applied = await Import(db, w, new BankConfirmationImportOptions(AcknowledgeAmountMismatch: true), ("W1", 90m, "REJECTED", "AC01"));
        w.R(1).Status.Should().Be(PaymentRecordStatuses.Rejected);
        applied.RejectedAmountNotPaid.Should().Be(100m, "the record's amount is what was not paid");
        applied.Rejections.Should().ContainSingle();
        applied.Returns.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSameRejectionInALaterFile_IsUnchanged_NotCountedTwice()
    {
        using var db = NewDb();
        var w = Seed(db);
        await Import(db, w, "RJCT", "AC01");

        var again = await Import(db, w, null, ("W1", 100m, "REJECTED", "AC01"));

        again.Duplicate.Should().BeFalse();
        again.Unchanged.Should().Be(1);
        again.RejectedApplied.Should().Be(0);
        again.RejectedAmountNotPaid.Should().Be(100m);
        again.Rejections.Should().ContainSingle();
    }

    [Fact]
    public async Task SettledByBank_IsInTheAuditMetadata()
    {
        using var db = NewDb();
        var w = Seed(db);

        await Import(db, w, "ACSC");

        db.PayrollAuditLogs.Single().MetadataJson.Should().Contain("\"settledByBank\":1");
    }

    // ── An exit to Paid counts exactly like Paid everywhere (CTO, F1 close-out) ──────────────────

    private static async Task<string?> DashboardPayDate(ZayraDbContext db, Guid tenantId)
    {
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Exits", Slug = $"exits-{tenantId:N}" });
        db.SaveChanges();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("permission", "payroll.read"),
        }, "test"));
        var ctrl = new DashboardController(db,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new ExitsUnrestrictedScope())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
        var full = Assert.IsType<DashboardFullDto>(Assert.IsType<OkObjectResult>(await ctrl.Full(6)).Value);
        return full.Overview.PayrollSummary!.PayDate;
    }

    [Theory]
    [InlineData("RJCT")]   // the bank's word disagrees with the person
    [InlineData("PAID")]   // the bank's word agrees: still recorded as the person's decision
    public async Task AnExitToPaid_ShowsItsPayDate_AndCountsAsPaid_Everywhere(string word)
    {
        using var db = NewDb();
        var w = Seed(db);
        await Import(db, w, word, "DUPL");

        var (result, refusal) = await TryImport(db, w, ExitFor(BankLineExitKinds.ConfirmOriginalPaymentReceived, w.R(1)),
            CsvDated("2026-08-02", ("W1", 100m, word, "dupl")));
        refusal.Should().BeNull();
        result = result!;

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
        result.Coverage.PaidCount.Should().Be(1);
        result.Coverage.PaidTotal.Should().Be(100m);
        result.Coverage.UnconfirmedCount.Should().Be(0);

        var h = await Handoff(db, w);
        var coverage = h.GetProperty("payments").GetProperty("coverage");
        coverage.GetProperty("PaidCount").GetInt32().Should().Be(1);
        coverage.GetProperty("PaidTotal").GetDecimal().Should().Be(100m);
        h.GetProperty("payments").GetProperty("unmatchedImportRowsLogged").GetInt32()
            .Should().Be(1, "only the DUPL hold is a logged, unapplied line; the person's decision to Paid is not");

        var history = await History(db, w);
        var exitImport = history.GetProperty("imports").EnumerateArray().First();
        exitImport.GetProperty("applied").GetInt32().Should().Be(1);
        exitImport.GetProperty("held").GetInt32().Should().Be(0);

        (await DashboardPayDate(db, w.TenantId)).Should().Be("2026-08-02", "with no original Paid line, the exit line's date");
    }

    // Ruling 5 of the re-check: the ORIGINAL Paid line's date wins over the duplicate line's.
    [Fact]
    public async Task AnExitToPaid_KeepsTheOriginalPaidLinesDate_AsThePayDate()
    {
        using var db = NewDb();
        var w = Seed(db);
        (await TryImport(db, w, new BankConfirmationImportOptions(), CsvDated("2026-07-28", ("W1", 100m, "PAID", null)))).Refusal.Should().BeNull();
        (await TryImport(db, w, new BankConfirmationImportOptions(), CsvDated("2026-08-05", ("W1", 100m, "RJCT", "DUPL")))).Refusal.Should().BeNull();

        var (_, refusal) = await TryImport(db, w, ExitFor(BankLineExitKinds.ConfirmOriginalPaymentReceived, w.R(1)),
            CsvDated("2026-08-05", ("W1", 100m, "RJCT", "dupl")));

        refusal.Should().BeNull();
        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
        (await DashboardPayDate(db, w.TenantId)).Should().Be("2026-07-28");
    }

    // ── N3: a mismatch hold closes once a later line for the record is applied ──────────────────

    [Fact]
    public async Task AnAmountMismatchHold_ClosesWhenALaterLineIsApplied()
    {
        using var db = NewDb();
        var w = Seed(db);
        await Import(db, w, null, ("W1", 90m, "PAID", null));
        var before = await Handoff(db, w);
        Blockers(before).Should().Contain("amount_mismatch_holdbacks");
        before.GetProperty("payments").GetProperty("amountMismatchHoldbacks").GetInt32().Should().Be(1);

        await Import(db, w, new BankConfirmationImportOptions(AcknowledgeAmountMismatch: true), ("W1", 90m, "CREDITED", null));

        var after = await Handoff(db, w);
        Blockers(after).Should().NotContain("amount_mismatch_holdbacks");
        after.GetProperty("payments").GetProperty("amountMismatchHoldbacks").GetInt32().Should().Be(0);
    }

    // ── Amount-0 nit: a refusal with amount 0 is a refusal, not a different figure ──────────────

    [Fact]
    public async Task ARejectionWithAmountZero_IsRecordedRejected_NotHeldAsAMismatch()
    {
        using var db = NewDb();
        var w = Seed(db);

        var result = await Import(db, w, null, ("W1", 0m, "RJCT", "AM04"));

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Rejected);
        result.AmountMismatchesHeld.Should().BeEmpty();
        result.RejectedAmountNotPaid.Should().Be(100m);
    }

    // ── Package F5: what the officer reads in a refusal and in a Finance note (wording only) ─────────────

    private static readonly System.Text.RegularExpressions.Regex SnakeCaseExit = new("[a-z]+_[a-z_]+");
    private static readonly System.Text.RegularExpressions.Regex Developer = new(@"\b(pod|seam|driver)\b|NetSettlementReturn",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static JsonElement OnlyRefused(BankConfirmationImportResult result) => Element(result.Refused).EnumerateArray().Single();

    private static void ShouldBeAnInstruction(JsonElement refused, params string[] exitCodes)
    {
        var en = refused.GetProperty("message").GetString()!;
        var ar = refused.GetProperty("messageAr").GetString()!;
        foreach (var text in new[] { en, ar })
        {
            SnakeCaseExit.IsMatch(text).Should().BeFalse($"no raw exit or code word in: {text}");
            text.Should().NotContain("allowReturnReversal");
        }
        ar.Any(ch => ch is >= '\u0600' and <= '\u06FF').Should().BeTrue();
        if (exitCodes.Length > 0)
            refused.GetProperty("nextStepCodes").EnumerateArray().Select(x => x.GetString()).Should().BeEquivalentTo(exitCodes);
    }

    [Fact]
    public async Task PaidThenRejected_SaysWhatToCheckAndDecide_InBothLanguages_WithTheExitsInAMachineField()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);

        var refused = OnlyRefused(await Import(db, w, "RJCT", "AC04"));

        refused.GetProperty("reason").GetString().Should().Be("paid_then_rejected_held", "the machine key does not change");
        ShouldBeAnInstruction(refused, BankLineExitKinds.AllowPaidToRejected, BankLineExitKinds.ConfirmPaidDespiteRejection);
        refused.GetProperty("message").GetString().Should().Contain("Check the bank statement")
            .And.Contain("record 'paid confirmed' with the statement reference")
            .And.Contain("record 'not paid' with a reason and the reference");
        refused.GetProperty("messageAr").GetString().Should().Contain("راجع كشف حساب البنك").And.Contain("«تأكيد الدفع»").And.Contain("«لم يُدفع»")
            .And.NotBe(BankOutcomeLabels.HeldRejectionAfterPaid.Arabic);
    }

    [Fact]
    public async Task ADuplicate_NextStepIsAPlainInstruction_InBothLanguages_NeverSayingOwedOrAnExitName()
    {
        using var db = NewDb();
        var w = Seed(db);

        var refused = OnlyRefused(await Import(db, w, "RJCT", "DUPL"));

        ShouldBeAnInstruction(refused, BankLineExitKinds.ConfirmOriginalPaymentReceived, BankLineExitKinds.ConfirmDuplicateNotPaid);
        var en = refused.GetProperty("nextStep").GetString()!;
        var ar = refused.GetProperty("nextStepAr").GetString()!;
        SnakeCaseExit.IsMatch(en).Should().BeFalse();
        SnakeCaseExit.IsMatch(ar).Should().BeFalse();
        en.Should().Contain("Check the bank statement for the original payment").And.Contain("record 'original payment received'")
            .And.Contain("Both need a reason and the bank's reference").And.NotContainEquivalentOf("owed");
        ar.Should().Contain("راجع كشف حساب البنك بحثًا عن الدفعة الأصلية").And.NotContain("مستحق");
    }

    [Fact]
    public async Task AReturnedRejectedSwap_NamesTheTwoAnswersInWords_NotAsEnumValues()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Returned);

        var refused = OnlyRefused(await Import(db, w, "RJCT", "AC01"));

        refused.GetProperty("reason").GetString().Should().Be("failed_outcome_changed_held");
        ShouldBeAnInstruction(refused, BankLineExitKinds.AllowFailedOutcomeChange);
        refused.GetProperty("message").GetString().Should().Contain("The bank first reported this salary as returned and now as rejected")
            .And.Contain("Ask the bank which is correct");
        refused.GetProperty("messageAr").GetString().Should().Contain("مُرتجع").And.Contain("مرفوض").And.Contain("اسأل البنك أي الحالتين صحيحة");
    }

    [Fact]
    public async Task ReMarkingAFailedPaymentAsPaid_IsRefusedWithAnInstructionInBothLanguages()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Rejected);

        var refused = OnlyRefused(await Import(db, w, "PAID"));

        refused.GetProperty("reason").GetString().Should().Be("return_reversal_not_permitted");
        ShouldBeAnInstruction(refused);
        refused.GetProperty("message").GetString().Should().Be(
            "This salary was returned or rejected by the bank. To mark it paid again you must record a reason and the bank's reference.");
        refused.GetProperty("messageAr").GetString().Should().Be("رُدّ هذا الراتب أو رفضه البنك. لتسجيله مدفوعًا من جديد يجب تسجيل السبب ومرجع البنك.");
    }

    [Fact]
    public async Task AnExitWithoutItsReason_SaysSoInBothLanguages()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Returned);

        var (_, refusal) = await TryImport(db, w, ExitFor(BankLineExitKinds.AllowFailedOutcomeChange, w.R(1), reason: null), Csv(("W1", 100m, "RJCT", "AC01")));

        var body = Element(refusal!.Payload);
        body.GetProperty("error").GetString().Should().Be("override_reason_required");
        body.GetProperty("message").GetString().Should().Be("Record a reason: this decision changes what Finance must correct in the ledger.");
        body.GetProperty("messageAr").GetString().Should().Be("سجّل السبب: يغيّر هذا القرار ما يجب على الإدارة المالية تصحيحه في الدفاتر.");

        (_, refusal) = await TryImport(db, w, ExitFor(BankLineExitKinds.ConfirmPaidDespiteRejection, w.R(1), reference: null), Csv(("W1", 100m, "RJCT", "AC01")));
        body = Element(refusal!.Payload);
        body.GetProperty("message").GetString().Should().Contain("reason and the bank's reference");
        body.GetProperty("messageAr").GetString().Should().Contain("السبب ومرجع البنك");
    }

    [Fact]
    public async Task TheFinanceNotes_OnTheHandoffAndTheImportResult_UseNoDeveloperWords_AndCarryArabic()
    {
        using var db = NewDb();
        var w = Seed(db, (100m, PaymentRecordStatuses.Paid), (100m, PaymentRecordStatuses.Paid));
        Settle(db, w, DateTime.UtcNow.AddDays(-1));
        var ctrl = Controller(db, w);
        ctrl.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            fileName = "answer.json",
            rows = new[]
            {
                new { wpsReference = "W1", amount = 100m, status = "RETURNED", reasonCode = "AC04", valueDate = "2026-07-30" },
                new { wpsReference = "W2", amount = 100m, status = "RJCT", reasonCode = "AM04", valueDate = "2026-07-30" },
            },
        })));
        ctrl.ControllerContext.HttpContext.Request.ContentType = "application/json";
        var import = Element(Assert.IsAssignableFrom<ObjectResult>(await ctrl.Import(w.Batch.Id, ct: default)).Value!);
        var handoff = await Handoff(db, w);
        var notes = new List<(string Name, string Text)>();
        void Collect(JsonElement e, string name)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Collect(p.Value, p.Name); break;
                case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Collect(x, name); break;
                case JsonValueKind.String when name.StartsWith("note", StringComparison.Ordinal): notes.Add((name, e.GetString()!)); break;
            }
        }
        Collect(handoff, "");
        Collect(import, "");
        notes.Should().NotBeEmpty();
        foreach (var (name, text) in notes) Developer.IsMatch(text).Should().BeFalse($"{name}: {text}");

        var deltas = handoff.GetProperty("deltas");
        foreach (var figure in new[] { "returnedAmountAwaitingGlCorrection", "rejectedAfterSettlementAwaitingGlCorrection" })
        {
            deltas.GetProperty(figure).GetProperty("noteAr").GetString()!.Any(ch => ch is >= '\u0600' and <= '\u06FF').Should().BeTrue(figure);
            deltas.GetProperty(figure).GetProperty("note").GetString().Should().Contain("Finance");
        }
        deltas.GetProperty("returnedAmountAwaitingGlCorrection").GetProperty("note").GetString().Should()
            .Contain("Not yet posted. Finance must post the correction (debit Cash/Bank, credit Salaries Payable)");
    }
}

file sealed class ExitsUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}
