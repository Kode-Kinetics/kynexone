using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
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
        "wpsReference,amount,valueDate,status,reasonCode\n"
        + string.Concat(rows.Select(r => $"{r.Ref},{r.Amount},2026-07-30,{r.Status},{r.Reason}\n"));

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
            new("is_group_scope", "true"),
        }, "Test"));
        return new BankConfirmationsController(db, Service(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
    }

    private static async Task<JsonElement> History(ZayraDbContext db, World w) =>
        Element(Assert.IsType<OkObjectResult>(await Controller(db, w).History(w.Batch.Id, default)).Value!);

    private static readonly BankConfirmationImportOptions PaidToRejected =
        new(AllowPaidToRejected: true, OverrideReason: "Bank letter: the credit was refused after posting", OverrideBankReference: "BNK-77812");

    private static readonly BankConfirmationImportOptions FailedOutcomeChange =
        new(AllowFailedOutcomeChange: true, OverrideReason: "Bank confirmed by phone which outcome is right");

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

    [Fact]
    public async Task ASettlementPostedAfterTheRejection_DoesNotMakeItNeedACorrection()
    {
        using var db = NewDb();
        var w = Seed(db);
        await Import(db, w, "RJCT", "AM04");
        Settle(db, w, DateTime.UtcNow.AddDays(1));

        var h = await Handoff(db, w);

        h.GetProperty("deltas").GetProperty("rejectedAfterSettlementAwaitingGlCorrection").GetProperty("amount").GetDecimal().Should().Be(0m);
        (await History(db, w)).GetProperty("rejectedAfterSettlementAwaitingGlCorrection").GetDecimal().Should().Be(0m);
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

    [Fact]
    public async Task PaidThenRejectedHold_StaysOpen_WhenALaterFileOnlySaysPending()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);
        await Import(db, w, "RJCT", "AC04");

        await Import(db, w, "PENDING");

        Blockers(await Handoff(db, w)).Should().Contain("rejection_after_paid_held", "an informational line resolves nothing");
    }

    [Fact]
    public async Task PaidThenRejected_ExplicitExit_MovesToRejected_Audited_AndClearsTheHold()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);
        Settle(db, w, DateTime.UtcNow.AddDays(-1));
        await Import(db, w, "RJCT", "AC04");

        // A different file (the bank's later letter), so it is not refused as the same file imported twice.
        var result = await Import(db, w, PaidToRejected, ("W1", 100m, "REJECTED", "AC04"));

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Rejected);
        result.RejectedApplied.Should().Be(1);
        result.Overrides.Should().ContainSingle();
        Json(result.Overrides).Should().Contain("\"from\":\"Paid\"").And.Contain("\"to\":\"Rejected\"");
        result.LateReturns.Should().BeEmpty();
        result.ReturnedAmountAwaitingGlCorrection.Should().Be(0m);
        result.RejectedAfterSettlementAwaitingGlCorrection.Should().Be(100m);

        var audit = db.PayrollAuditLogs.AsEnumerable().Single(a => a.MetadataJson!.Contains("paid_to_rejected"));
        audit.MetadataJson.Should().Contain("Bank letter: the credit was refused after posting").And.Contain("BNK-77812")
            .And.Contain("paid_to_rejected").And.Contain("\"from\":\"Paid\"").And.Contain("\"to\":\"Rejected\"");

        var blockers = Blockers(await Handoff(db, w));
        blockers.Should().NotContain("rejection_after_paid_held").And.Contain("unpaid_rejected_payments").And.Contain("gl_correction_awaiting");
    }

    [Theory]
    [InlineData(null, "BNK-1")]
    [InlineData("  ", "BNK-1")]
    [InlineData("Bank letter", null)]
    [InlineData("Bank letter", " ")]
    public async Task PaidThenRejected_Exit_NeedsAReasonAndABankReference(string? reason, string? bankReference)
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);

        var (result, refusal) = await TryImport(db, w,
            new BankConfirmationImportOptions(AllowPaidToRejected: true, OverrideReason: reason, OverrideBankReference: bankReference),
            Csv(("W1", 100m, "RJCT", null)));

        result.Should().BeNull();
        refusal!.Status.Should().Be(400);
        Json(refusal.Payload).Should().Contain("override_reason_required");
        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
    }

    [Fact]
    public async Task ImportEndpoint_RefusesTheExitWithoutAReason()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid);
        var ctrl = Controller(db, w);
        ctrl.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Csv(("W1", 100m, "RJCT", null))));
        ctrl.ControllerContext.HttpContext.Request.ContentType = "text/csv";

        var bad = Assert.IsType<BadRequestObjectResult>(await ctrl.Import(w.Batch.Id, allowPaidToRejected: true, ct: default));

        Json(bad.Value!).Should().Contain("override_reason_required");
        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid);
    }

    // ── B2: Returned <-> Rejected ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Returned", "RJCT", "Rejected")]
    [InlineData("Rejected", "RETURNED", "Returned")]
    public async Task FailedOutcomeChange_ExplicitExit_AppliesTheNewOutcome_Audited(string previous, string word, string expected)
    {
        using var db = NewDb();
        var w = Seed(db, previous);

        var held = await Import(db, w, word);
        w.R(1).Status.Should().Be(previous);
        held.HeldForPerson.Should().ContainSingle();

        var result = await Import(db, w, FailedOutcomeChange, ("W1", 100m, word, "AC04"));

        w.R(1).Status.Should().Be(expected);
        result.Overrides.Should().ContainSingle();
        result.HeldForPerson.Should().BeEmpty();
        db.PayrollAuditLogs.AsEnumerable().Single(a => a.MetadataJson!.Contains("failed_outcome_override")).MetadataJson
            .Should().Contain("Bank confirmed by phone which outcome is right").And.Contain("failed_outcome_override")
            .And.Contain($"\"from\":\"{previous}\"").And.Contain($"\"to\":\"{expected}\"");
    }

    [Fact]
    public async Task FailedOutcomeChange_ReleasesALegacyRejectionStoredAsReturned()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Returned);
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

        await Import(db, w, FailedOutcomeChange, ("W1", 100m, "RJCT", "AC01"));

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Rejected);
        (await Handoff(db, w)).GetProperty("deltas").GetProperty("returnedAmountAwaitingGlCorrection").GetProperty("amount").GetDecimal().Should().Be(0m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task FailedOutcomeChange_Exit_NeedsAReason(string? reason)
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Returned);

        var (_, refusal) = await TryImport(db, w, new BankConfirmationImportOptions(AllowFailedOutcomeChange: true, OverrideReason: reason),
            Csv(("W1", 100m, "RJCT", null)));

        refusal!.Status.Should().Be(400);
        Json(refusal.Payload).Should().Contain("override_reason_required");
    }

    [Fact]
    public async Task TheExits_DoNotOpenEachOther()
    {
        using var db = NewDb();
        var w = Seed(db, (100m, PaymentRecordStatuses.Paid), (100m, PaymentRecordStatuses.Returned));

        await Import(db, w, PaidToRejected, ("W2", 100m, "RJCT", null));
        await Import(db, w, FailedOutcomeChange, ("W1", 100m, "RJCT", null));

        w.R(1).Status.Should().Be(PaymentRecordStatuses.Paid, "the failed-outcome exit does not apply a rejection after Paid");
        w.R(2).Status.Should().Be(PaymentRecordStatuses.Returned, "the paid-to-rejected exit does not swap Returned for Rejected");
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
}
