using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Wave 1 money-path fixes on the holds for a person (final gate):
/// <list type="bullet">
/// <item>A Returned/Rejected swap hold closes only through its exit (allow_failed_outcome_change) or an applied reasoned
/// reversal to Paid, never because a later file repeats the earlier word (or any other later line).</item>
/// <item>An open amount-mismatch hold blocks settle and Reconciled (422 bank_lines_held_for_person) and counts in the
/// "presents clean" flags (CTO-GATE 8.4 ruling 11). It may close on a later applied line; a later line whose amount
/// still differs never closes it.</item>
/// <item>The period handoff's returns read the same applied rows as the import and the batch history.</item>
/// </list>
/// Synthetic data only.
/// </summary>
public class BankOutcomeHoldClosureTests
{
    private const string Iban = "SA0380000000608010167519";

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record World(Guid TenantId, Guid CompanyId, PayrollRun Run, PayrollPaymentBatch Batch, PayrollPaymentRecord Record);

    /// <summary>One record of 100 (WPS reference W1) in a batch with its bank file at <paramref name="wpsStatus"/>.</summary>
    private static World Seed(ZayraDbContext db, string recordStatus, string wpsStatus = WpsStatuses.Accepted, bool settled = false)
    {
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var run = new PayrollRun { TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = 7, Status = "Locked" };
        var batch = new PayrollPaymentBatch
        {
            TenantId = tenantId, PayrollRunId = run.Id, BatchNumber = "PB-2026-07-021", Currency = "SAR", TotalAmount = 100m,
            WpsStatus = wpsStatus,
        };
        var record = new PayrollPaymentRecord
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = 1, Amount = 100m, Status = recordStatus, Iban = Iban,
            WpsReference = "W1",
        };
        db.AddRange(run, batch, record);
        db.WPSFileBatches.Add(new WPSFileBatch
        {
            TenantId = tenantId, CompanyId = companyId, PaymentBatchId = batch.Id, Status = wpsStatus, SifFileName = "f.sif",
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
        return new World(tenantId, companyId, run, batch, record);
    }

    private static BankConfirmationService Service(ZayraDbContext db) =>
        new(db, new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser() });

    /// <summary>One bank line for W1, imported and saved. Each call is a different file (its value date and a unique row).</summary>
    private static async Task<BankConfirmationImportResult> Import(ZayraDbContext db, World w, string status, string? reason = null,
        decimal amount = 100m, string valueDate = "2026-07-30", BankConfirmationImportOptions? options = null)
    {
        var csv = "wpsReference,amount,valueDate,status,reasonCode,bankReference\n"
                + $"W1,{amount},{valueDate},{status},{reason},REF-{Guid.NewGuid():N}\n";
        var (result, refusal) = await Service(db).ImportAsync(w.TenantId, w.Batch.Id, $"bank-{Guid.NewGuid():N}.csv", csv, "generic-csv",
            options ?? new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);
        refusal.Should().BeNull();
        await db.SaveChangesAsync();
        return result!;
    }

    private static JsonElement Element(object? o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

    private static async Task<JsonElement> Handoff(ZayraDbContext db, World w)
    {
        var exports = new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters());
        return Element(await new PeriodHandoffReconciler(db, exports)
            .ReconcileAsync(w.TenantId, new JournalExportFilter(PayrollRunId: w.Run.Id), groupScopedCaller: true, default));
    }

    private static List<string> Blockers(JsonElement handoff) =>
        handoff.GetProperty("blockers").EnumerateArray().Select(b => b.GetString()!).ToList();

    private static async Task<JsonElement> History(ZayraDbContext db, World w)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", w.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("permission", "payroll.export"), new Claim("is_group_scope", "true"),
        }, "Test"));
        var ctrl = new BankConfirmationsController(db, Service(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
        return Element(Assert.IsType<OkObjectResult>(await ctrl.History(w.Batch.Id, default)).Value);
    }

    private static PayrollController Payroll(ZayraDbContext db, World w) => KsaWpsReviewFixesTests.Payroll(db, w.TenantId, Guid.NewGuid());

    private static async Task<bool> ListedPresentsCleanButIsNot(ZayraDbContext db, World w) =>
        Element(Assert.IsType<OkObjectResult>(await Payroll(db, w).ListPaymentBatches(null, default)).Value)
            .EnumerateArray().Single(b => b.GetProperty("id").GetGuid() == w.Batch.Id).GetProperty("presentsCleanButIsNot").GetBoolean();

    // ── P1-1: a Returned/Rejected swap hold closes only through its exit ─────────────────────────

    [Theory]
    [InlineData(PaymentRecordStatuses.Returned, "RJCT", "RETURNED")]   // Returned, held REJECTED, then RETURNED again
    [InlineData(PaymentRecordStatuses.Rejected, "RETURNED", "RJCT")]   // Rejected, held RETURNED, then REJECTED again
    public async Task ASwapHold_IsNotClosed_WhenALaterFileRepeatsTheEarlierWord(string recordStatus, string swapWord, string earlierWord)
    {
        using var db = NewDb();
        var w = Seed(db, recordStatus);
        (await Import(db, w, swapWord, "AC04")).HeldForPerson.Should().ContainSingle();

        var later = await Import(db, w, earlierWord, "AC04", valueDate: "2026-08-02");

        db.BankPaymentConfirmations.OrderBy(c => c.ImportedAtUtc).Last().Applied.Should().BeTrue("the repeated word itself applies (unchanged)");
        Element(later.HeldForPerson).ToString().Should().Contain(BankOutcomeHolds.FailedOutcomeChanged);
        Blockers(await Handoff(db, w)).Should().Contain("bank_outcome_change_held");
        (await History(db, w)).GetProperty("heldForPerson").ToString().Should().Contain(BankOutcomeHolds.FailedOutcomeChanged);
    }

    [Fact]
    public async Task ASwapHold_ClosesThroughItsExit()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Returned);
        await Import(db, w, "RJCT", "AC04");

        var exit = new BankConfirmationImportOptions(Exits: new[]
        {
            new BankLineExit(BankLineExitKinds.AllowFailedOutcomeChange, w.Record.Id, null, "The bank's letter says it never left", null),
        });
        var resolved = await Import(db, w, "REJECTED", "AC04", valueDate: "2026-08-02", options: exit);

        resolved.HeldForPerson.Should().BeEmpty();
        w.Record.Status.Should().Be(PaymentRecordStatuses.Rejected);
        Blockers(await Handoff(db, w)).Should().NotContain("bank_outcome_change_held");
    }

    [Fact]
    public async Task ASwapHold_ClosesThroughAReasonedReversalToPaid()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Returned);
        await Import(db, w, "RJCT", "AC04");

        var reversed = await Import(db, w, "PAID", valueDate: "2026-08-02",
            options: new BankConfirmationImportOptions(AllowReturnReversal: true, ReturnReversalReason: "Statement shows the credit", CallerMayRecordPaymentStatus: true));

        w.Record.Status.Should().Be(PaymentRecordStatuses.Paid);
        reversed.HeldForPerson.Should().BeEmpty();
    }

    // ── P1-2: an open amount-mismatch hold blocks settle and Reconciled, and never presents clean ──

    [Fact]
    public async Task AnOpenMismatchHold_BlocksReconciled_AndPresentsNotClean_OnTheListTheHistoryAndTheHandoff()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid, WpsStatuses.Paid, settled: true);
        (await Import(db, w, "RETURNED", "AC04", amount: 40m)).AmountMismatchesHeld.Should().ContainSingle("a partial return is held");
        w.Record.Status.Should().Be(PaymentRecordStatuses.Paid);

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(
            await Payroll(db, w).UpdateWpsStatus(w.Batch.Id, new WpsStatusRequest(WpsStatuses.Reconciled, null), default));
        Element(refused.Value).GetProperty("error").GetString().Should().Be("bank_lines_held_for_person");
        db.PayrollPaymentBatches.Single().WpsStatus.Should().Be(WpsStatuses.Paid);

        (await ListedPresentsCleanButIsNot(db, w)).Should().BeTrue();
        (await History(db, w)).GetProperty("presentsCleanButIsNot").GetBoolean().Should().BeTrue();
        var handoff = await Handoff(db, w);
        handoff.GetProperty("payments").GetProperty("batches").EnumerateArray().Single()
            .GetProperty("presentsCleanButIsNot").GetBoolean().Should().BeTrue();
        Blockers(handoff).Should().Contain("amount_mismatch_holdbacks");
    }

    [Fact]
    public async Task AnOpenMismatchHold_BlocksSettle()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid, WpsStatuses.Accepted);
        await Import(db, w, "RETURNED", "AC04", amount: 40m);

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(
            await Payroll(db, w).SettlePaymentBatch(w.Batch.Id, new SettlePaymentBatchRequest("settle", null), default));

        Element(refused.Value).GetProperty("error").GetString().Should().Be("bank_lines_held_for_person");
        db.FinanceGlEntries.Count(e => e.EventType == GlEventTypes.NetSettlement).Should().Be(0);
    }

    [Fact]
    public async Task ALaterLineWhoseAmountStillDiffers_DoesNotCloseTheMismatchHold()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid, WpsStatuses.Accepted);
        await Import(db, w, "RETURNED", "AC04", amount: 40m);

        await Import(db, w, "PAID", amount: 40m, valueDate: "2026-08-02");

        var handoff = await Handoff(db, w);
        Blockers(handoff).Should().Contain("amount_mismatch_holdbacks");
        handoff.GetProperty("payments").GetProperty("amountMismatchHoldbacks").GetInt32()
            .Should().Be(2, "the later differing line is held too, and it does not close the first one");
        var refused = Assert.IsType<UnprocessableEntityObjectResult>(
            await Payroll(db, w).SettlePaymentBatch(w.Batch.Id, new SettlePaymentBatchRequest("settle", null), default));
        Element(refused.Value).GetProperty("error").GetString().Should().Be("bank_lines_held_for_person");
    }

    [Fact]
    public async Task AMismatchHold_ClosesOnALaterAppliedLine_AndThenNoLongerBlocksSettle()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid, WpsStatuses.Accepted);
        await Import(db, w, "RETURNED", "AC04", amount: 40m);

        await Import(db, w, "PAID", amount: 100m, valueDate: "2026-08-02");

        Blockers(await Handoff(db, w)).Should().NotContain("amount_mismatch_holdbacks");
        var settle = await Payroll(db, w).SettlePaymentBatch(w.Batch.Id, new SettlePaymentBatchRequest("settle", null), default);
        if (settle is UnprocessableEntityObjectResult u)
            Element(u.Value).GetProperty("error").GetString().Should().NotBe("bank_lines_held_for_person");
    }

    // ── P2-1: the handoff's returns read the same applied rows as the import and the history ───────

    [Fact]
    public async Task HandoffReturns_ReadTheAppliedRow_LikeTheImportAndTheHistory()
    {
        using var db = NewDb();
        // Settled, so the return awaits the GL correction and its value date sets the target period (owner ruling 2026-11-17).
        var w = Seed(db, PaymentRecordStatuses.Pending, WpsStatuses.Accepted, settled: true);
        await Import(db, w, "RETURNED", "AC04", valueDate: "2026-07-30");
        // A later RETURNED line with a different amount and another reason: held, never applied.
        var later = await Import(db, w, "RETURNED", "AM04", amount: 50m, valueDate: "2026-08-15");
        later.AmountMismatchesHeld.Should().ContainSingle();

        var fromImport = Element(later.Returns).EnumerateArray().Single();
        var handoff = await Handoff(db, w);
        var fromHandoff = handoff.GetProperty("payments").GetProperty("returns").EnumerateArray().Single();

        fromHandoff.GetProperty("reasonCode").GetString().Should().Be("AC04").And.Be(fromImport.GetProperty("reasonCode").GetString());
        fromHandoff.GetProperty("valueDate").GetString().Should().Be(fromImport.GetProperty("valueDate").GetString());
        fromHandoff.GetProperty("label").GetProperty("en").GetString().Should().Be(BankOutcomeLabels.ReturnedMoneyCameBack.English);
        handoff.GetProperty("deltas").GetProperty("returnedAmountAwaitingGlCorrection").GetProperty("targetPeriod").GetString()
            .Should().Be("2026-07", "the applied return's value date, not the held line's");

        var applied = (await History(db, w)).GetProperty("confirmations").EnumerateArray()
            .Single(c => c.GetProperty("applied").GetBoolean());
        applied.GetProperty("reasonCode").GetString().Should().Be(fromHandoff.GetProperty("reasonCode").GetString());
    }

    // ── P1-4 (owner ruling 2026-11-17): no GL correction for returned money before settlement ──────

    /// <summary>The returned-money GL figure as the import, the batch history and the period handoff each report it.</summary>
    private static async Task<(decimal Import, decimal History, decimal Handoff, List<string> Blockers, JsonElement Handoff_)> ReturnedFigures(
        ZayraDbContext db, World w, BankConfirmationImportResult import)
    {
        var history = await History(db, w);
        var handoff = await Handoff(db, w);
        return (import.ReturnedAmountAwaitingGlCorrection, history.GetProperty("returnedAmountAwaitingGlCorrection").GetDecimal(),
            handoff.GetProperty("deltas").GetProperty("returnedAmountAwaitingGlCorrection").GetProperty("amount").GetDecimal(),
            Blockers(handoff), handoff);
    }

    [Fact]
    public async Task AReturnBeforeSettlement_NeedsNoGlCorrection_InAnyOfTheThreeReads_ButStillBlocksAndStaysReturned()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Pending, WpsStatuses.Accepted, settled: false);

        var result = await Import(db, w, "RETURNED", "AC04");

        w.Record.Status.Should().Be(PaymentRecordStatuses.Returned);
        var (import, history, handoff, blockers, h) = await ReturnedFigures(db, w, result);
        import.Should().Be(0m, "no settlement journal credited Cash/Bank for it");
        history.Should().Be(0m);
        handoff.Should().Be(0m);
        blockers.Should().NotContain("gl_correction_awaiting").And.Contain("unresolved_returned_payments");
        h.GetProperty("payments").GetProperty("returns").GetArrayLength().Should().Be(1, "it still reads returned, needing a re-pay");
        result.Returns.Should().ContainSingle();

        var settle = await Payroll(db, w).SettlePaymentBatch(w.Batch.Id, new SettlePaymentBatchRequest("settle", null), default);
        settle.Should().NotBeOfType<OkObjectResult>("a returned salary still blocks settlement");
        db.FinanceGlEntries.Count(e => e.EventType == GlEventTypes.NetSettlement).Should().Be(0);
    }

    [Fact]
    public async Task AReturnAfterSettlement_AwaitsTheGlCorrection_InAllThreeReads_AndBlocksTheHandoff()
    {
        using var db = NewDb();
        var w = Seed(db, PaymentRecordStatuses.Paid, WpsStatuses.Paid, settled: true);

        var result = await Import(db, w, "RETURNED", "AC04");

        w.Record.Status.Should().Be(PaymentRecordStatuses.Returned);
        var (import, history, handoff, blockers, _) = await ReturnedFigures(db, w, result);
        import.Should().Be(100m);
        history.Should().Be(100m);
        handoff.Should().Be(100m);
        blockers.Should().Contain("gl_correction_awaiting").And.Contain("unresolved_returned_payments");
    }
}
