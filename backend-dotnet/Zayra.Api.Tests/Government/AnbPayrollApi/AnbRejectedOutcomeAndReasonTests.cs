using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// CTO gate 8.4.2 and 8.4.7 for the ANB status read.
///
/// <para>8.4.2: ANB FAILED (for example not enough balance) and the unsettled lines of a REJECTED or FAILED payroll mean
/// the bank refused the instruction: "Not paid: rejected by bank" (outcome Rejected), never Returned ("money came
/// back"). Only ANB RETURNED is Returned. Proved end to end: the mapped snapshot, rendered for the shared
/// <c>generic-csv</c> parser and imported, sets the payment status Rejected and stays out of the returned-money GL
/// correction.</para>
///
/// <para>8.4.7: the bank's reason reaches the shared catalogue (<see cref="BankStatusReasonCatalogue.Describe"/>) as a
/// code, never as raw text. A duplicate-payment reason (DUPL) is held for a person: it may mean the first payment went
/// through, so it is never shown as "not paid, still owed".</para>
/// </summary>
public class AnbRejectedOutcomeAndReasonTests
{
    // ── 8.4.2: FAILED and payroll-level refusals are Rejected; RETURNED stays Returned ─────────────────

    [Fact]
    public void AnbFailed_IsRejected_AndAnbReturned_StaysReturned()
    {
        AnbPayrollStatusMapper.ToConfirmationOutcome(AnbTransactionPhase.Failed).Should().Be(BankConfirmationOutcomes.Rejected);
        AnbPayrollStatusMapper.ToConfirmationOutcome(AnbTransactionPhase.Returned).Should().Be(BankConfirmationOutcomes.Returned);
    }

    [Theory]
    [InlineData("REJECTED")]
    [InlineData("FAILED")]
    public void TheUnsettledLinesOfARefusedPayroll_AreRejected_NeverReturned(string payrollStatus)
    {
        var batch = BuildBatch(3);
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot(payrollStatus, new[] { Bank(batch.Rows[0], "INITIATED"), Bank(batch.Rows[1], "PENDING"), Bank(batch.Rows[2], "RETURNED", "P3") }, 3),
            Binding(batch));

        mapped.Rows.Single(r => r.WpsReference == WpsReference(1)).Outcome.Should().Be(BankConfirmationOutcomes.Rejected);
        mapped.Rows.Single(r => r.WpsReference == WpsReference(2)).Outcome.Should().Be(BankConfirmationOutcomes.Rejected);
        mapped.Rows.Single(r => r.WpsReference == WpsReference(3)).Outcome.Should().Be(BankConfirmationOutcomes.Returned,
            "a line the bank already called RETURNED keeps the bank's own word");
    }

    [Fact]
    public void TheRenderedFile_ReadsBackThroughTheSharedVocabulary_AsTheSameOutcomes()
    {
        var batch = BuildBatch(3);
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot("IN_PROGRESS", new[] { Bank(batch.Rows[0], "FAILED"), Bank(batch.Rows[1], "RETURNED", "P2"), Bank(batch.Rows[2], "PAID", "P3") }, 3),
            Binding(batch));

        var parsed = new GenericCsvBankConfirmationParser().Parse(BankChannelConfirmationFile.Render(PayrollId, mapped.Rows));

        parsed.Errors.Should().BeEmpty();
        parsed.Rows.Select(r => (r.WpsReference, r.Outcome)).Should().Equal(
            (WpsReference(1), BankConfirmationOutcomes.Rejected),
            (WpsReference(2), BankConfirmationOutcomes.Returned),
            (WpsReference(3), BankConfirmationOutcomes.Paid));
    }

    [Fact]
    public async Task EndToEnd_AnbFailed_ImportsAsRejected_KeptOutOfTheReturnedMoneyGlCorrection()
    {
        var batch = BuildBatch(3);
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot("IN_PROGRESS", new[]
            {
                Bank(batch.Rows[0], "FAILED", description: "AM04 insufficient balance"),
                Bank(batch.Rows[1], "RETURNED", "P2", "T2", "AC04"),
                Bank(batch.Rows[2], "PAID", "P3", "T3"),
            }, 3),
            Binding(batch));
        var csv = BankChannelConfirmationFile.Render(PayrollId, mapped.Rows);

        using var db = NewDb();
        var seeded = Seed(db, batch);
        SettlementJournalFixture.Settle(db, seeded.BatchId);   // the returned figure is a correction only after settlement (owner ruling 2026-11-17)
        var (result, refusal) = await new BankConfirmationService(db, new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser() })
            .ImportAsync(seeded.TenantId, seeded.BatchId, "anb-status.csv", csv, BankChannelConfirmationFile.ParserKey,
                new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);
        refusal.Should().BeNull();
        await db.SaveChangesAsync();

        Status(db, 1).Should().Be(PaymentRecordStatuses.Rejected, "ANB FAILED means the bank refused it: not paid");
        Status(db, 2).Should().Be(PaymentRecordStatuses.Returned);
        Status(db, 3).Should().Be(PaymentRecordStatuses.Paid);
        result!.RejectedApplied.Should().Be(1);
        result.ReturnedApplied.Should().Be(1);
        result.RejectedAmountNotPaid.Should().Be(batch.Rows[0].SalaryAmount);
        result.ReturnedAmountAwaitingGlCorrection.Should().Be(batch.Rows[1].SalaryAmount,
            "only the money that left and came back needs a GL correction; the rejected salary never left");

        var rejectedEvidence = db.BankPaymentConfirmations.Single(c => c.Outcome == BankConfirmationOutcomes.Rejected);
        rejectedEvidence.ReasonCode.Should().Be("AM04");
        BankStatusReasonCatalogue.Describe(rejectedEvidence.ReasonCode).English
            .Should().Be("There was not enough money in the paying account.");
        db.BankPaymentConfirmations.Single(c => c.Outcome == BankConfirmationOutcomes.Returned).ReasonCode.Should().Be("AC04");
    }

    // ── 8.4.7: the ANB reason reaches the shared catalogue as a code, never as raw text ───────────────

    [Theory]
    [InlineData("AC01", "AC01")]
    [InlineData("AC04 - Account closed", "AC04")]
    [InlineData("Beneficiary account blocked (AC06)", "AC06")]
    [InlineData("BE01: name does not match", "BE01")]
    [InlineData("FF01", "FF01")]
    [InlineData("AC03 invalid creditor account", "AC03")]   // an ISO-shaped code not in the catalogue: kept, described as unknown
    public void AReasonCodeInTheBankText_IsReadAsTheCode(string description, string expectedCode)
    {
        var batch = BuildBatch(1);
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot("IN_PROGRESS", new[] { Bank(batch.Rows[0], "FAILED", description: description) }, 1), Binding(batch));

        var row = mapped.Rows.Single();
        row.ReasonCode.Should().Be(expectedCode);
        var described = BankStatusReasonCatalogue.Describe(row.ReasonCode);
        described.IsKnown.Should().Be(BankStatusReasonCatalogue.IsKnown(expectedCode));
        if (!described.IsKnown) described.English.Should().Contain("ask your bank");
    }

    [Fact]
    public void APayrollLevelReasonCode_ReachesEveryLineItRefused()
    {
        var batch = BuildBatch(2);
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot("REJECTED", batch.Rows.Select(r => Bank(r, "INITIATED")).ToList(), 2, reason: "AM04 Insufficient balance in debit account"),
            Binding(batch));

        mapped.Rows.Should().HaveCount(2).And.OnlyContain(r => r.Outcome == BankConfirmationOutcomes.Rejected && r.ReasonCode == "AM04");
    }

    [Theory]
    [InlineData("Insufficient balance")]                       // prose, no code
    [InlineData("Your account is closed")]                     // a four-letter word is not a code
    [InlineData("=HYPERLINK(\"http://x.test\",\"AC01\")")]    // a formula is not a code even if it carries one
    [InlineData("AC01 AC04")]                                  // two codes: ambiguous, never pick one
    [InlineData("ac04 lower case")]
    [InlineData("FAILED")]
    public void TextThatIsNotExactlyOneCode_GivesNoCode_AndTheCatalogueSaysAskYourBank_WithoutEchoingIt(string description)
    {
        var batch = BuildBatch(1);
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot("IN_PROGRESS", new[] { Bank(batch.Rows[0], "FAILED", description: description) }, 1), Binding(batch));

        var row = mapped.Rows.Single();
        row.ReasonCode.Should().BeNull("only a code the bank clearly gave is a code; the status word is not a reason code");
        var described = BankStatusReasonCatalogue.Describe(row.ReasonCode);
        described.IsKnown.Should().BeFalse();
        described.English.Should().Contain("ask your bank").And.NotContain(description);
        described.Arabic.Should().NotContain(description);
    }

    [Fact]
    public void TheReasonCode_IsReadAfterMasking_SoNoIdOrIbanCanBecomeACode()
    {
        var batch = BuildBatch(1);
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot("IN_PROGRESS", new[] { Bank(batch.Rows[0], "FAILED", description: $"{Iban(1)} {NationalId(1)}") }, 1), Binding(batch));

        mapped.Rows.Single().ReasonCode.Should().BeNull();
    }

    // ── 8.4.7: DUPL is held for a person, never "not paid, still owed" ─────────────────────────────────
    // The ANB line goes INTO the import with reason code DUPL, so the shared import holds it exactly as for any bank file
    // (duplicate_rejection_held, the handoff blocker, and the two explicit exits). It is never applied as Rejected.

    [Theory]
    [InlineData("FAILED", "DUPL")]
    [InlineData("FAILED", "DUPL - duplicate payment")]
    [InlineData("FAILED", "Duplicate transaction")]
    [InlineData("RETURNED", "DUPL")]
    [InlineData("PAID", "DUPL")]
    [InlineData("FAILED", "عملية دفع مكررة")]
    public void ADuplicateReason_GoesToTheImport_WithReasonCodeDupl(string status, string description)
    {
        var batch = BuildBatch(2);
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot("IN_PROGRESS", new[] { Bank(batch.Rows[0], status, "P1", "T1", description), Bank(batch.Rows[1], "PAID", "P2", "T2") }, 2),
            Binding(batch));

        mapped.Rows.Should().HaveCount(2);
        mapped.Rows.Single(r => r.WpsReference == WpsReference(1)).ReasonCode.Should().Be("DUPL");
        mapped.Rows.Single(r => r.WpsReference == WpsReference(2)).ReasonCode.Should().BeNull();
        mapped.Unresolved.Should().BeEmpty();
        mapped.Anomalies.Should().ContainSingle(a => a.WpsReference == WpsReference(1) && a.Code == AnbPayrollStatusMapper.DuplicateHeldReason);
        mapped.Complete.Should().BeFalse("a held line is not settled");
    }

    [Fact]
    public void ARefusedPayrollWithADuplicateReason_SendsEveryLineItWouldHaveRejected_AsDupl()
    {
        var batch = BuildBatch(3);
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot("REJECTED", new[] { Bank(batch.Rows[0], "INITIATED"), Bank(batch.Rows[1], "PENDING"), Bank(batch.Rows[2], "PAID", "P3", "T3") }, 3,
                reason: "DUPL"),
            Binding(batch));

        mapped.Rows.Where(r => r.ReasonCode == "DUPL").Select(r => r.WpsReference).Should().BeEquivalentTo(new[] { WpsReference(1), WpsReference(2) });
        mapped.Rows.Single(r => r.WpsReference == WpsReference(3)).Outcome.Should().Be(BankConfirmationOutcomes.Paid);
        mapped.Unresolved.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, "DUPL duplicate payment")]
    [InlineData(false, "DUPL duplicate payment")]
    [InlineData(true, "عملية دفع مكررة")]       // the word only, in Arabic: no code in the bank's text
    [InlineData(false, "Duplicate transaction")] // the word only, in English
    public async Task EndToEnd_AnAnbDuplicate_IsHeldByTheImport_BlocksTheHandoff_AndEachExitWorks(bool originalReceived, string description)
    {
        var batch = BuildBatch(2);
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot("IN_PROGRESS", new[]
            {
                Bank(batch.Rows[0], "FAILED", "P1", "T1", description),
                Bank(batch.Rows[1], "PAID", "P2", "T2"),
            }, 2),
            Binding(batch));
        var csv = BankChannelConfirmationFile.Render(PayrollId, mapped.Rows);

        // The shared parser reads the reason code.
        var parsed = new GenericCsvBankConfirmationParser().Parse(csv);
        parsed.Errors.Should().BeEmpty();
        parsed.Rows.Single(r => r.WpsReference == WpsReference(1)).ReasonCode.Should().Be("DUPL");

        using var db = NewDb();
        var seeded = Seed(db, batch);
        var (result, refusal) = await ImportAsync(db, seeded, csv, new BankConfirmationImportOptions());
        refusal.Should().BeNull();

        Status(db, 1).Should().Be(PaymentRecordStatuses.Pending, "a duplicate is never applied as Rejected (not paid, still owed)");
        Status(db, 2).Should().Be(PaymentRecordStatuses.Paid);
        result!.RejectedApplied.Should().Be(0);
        result.RejectedAmountNotPaid.Should().Be(0m);
        JsonSerializer.Serialize(result.Refused).Should().Contain(BankOutcomeHolds.DuplicateRejection);
        JsonSerializer.Serialize(result.HeldForPerson).Should().Contain(BankOutcomeHolds.DuplicateRejection);
        (await Blockers(db, seeded)).Should().Contain(BankOutcomeHolds.DuplicateRejection);

        // The exit: the held line sent again, with the person's finding, a reason and the bank's reference.
        var line = BankChannelConfirmationFile.Render(PayrollId, mapped.Rows.Where(r => r.WpsReference == WpsReference(1)).ToList());
        var exit = new BankConfirmationImportOptions(Exits: new[]
        {
            new BankLineExit(originalReceived ? BankLineExitKinds.ConfirmOriginalPaymentReceived : BankLineExitKinds.ConfirmDuplicateNotPaid,
                EmployeeReference: WpsReference(1), Reason: "Checked with the bank", BankReference: "STMT-0042"),
        });
        var (exited, exitRefusal) = await ImportAsync(db, seeded, line, exit);
        exitRefusal.Should().BeNull();

        Status(db, 1).Should().Be(originalReceived ? PaymentRecordStatuses.Paid : PaymentRecordStatuses.Rejected);
        exited!.HeldForPerson.Should().BeEmpty();
        var blockers = await Blockers(db, seeded);
        blockers.Should().NotContain(BankOutcomeHolds.DuplicateRejection);
        if (!originalReceived) blockers.Should().Contain("unpaid_rejected_payments");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Seeded(Guid TenantId, Guid BatchId, Guid RunId);

    /// <summary>One payment record per synthetic line, carrying the line's WPS reference and amount.</summary>
    private static Seeded Seed(ZayraDbContext db, Batch batch)
    {
        var tenantId = Guid.NewGuid();
        var run = new PayrollRun { TenantId = tenantId, CompanyId = Guid.NewGuid(), Year = 2026, Month = 11, Status = "Locked" };
        db.PayrollRuns.Add(run);
        var paymentBatch = new PayrollPaymentBatch { TenantId = tenantId, PayrollRunId = run.Id };
        db.PayrollPaymentBatches.Add(paymentBatch);
        // The run's accrual on the ledger, so the period handoff finds the run and its batch.
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tenantId, CompanyId = run.CompanyId, SourceModule = "Payroll", SourceEntityId = run.Id, SourceEntityRef = "RUN-2026-11",
            EventType = GlEventTypes.Accrual, CreditAccount = "2100 - Salaries Payable", Description = "Net payable",
            Amount = batch.Rows.Sum(r => r.SalaryAmount), Currency = "SAR", EntryDate = new DateOnly(2026, 11, 30), Period = "2026-11",
        });
        foreach (var r in batch.Rows)
            db.PayrollPaymentRecords.Add(new PayrollPaymentRecord
            {
                TenantId = tenantId, PaymentBatchId = paymentBatch.Id, EmployeeId = r.EmployeeRef, Amount = r.SalaryAmount,
                Iban = r.AccountNumber!, Status = PaymentRecordStatuses.Pending, WpsReference = WpsReference(r.EmployeeRef),
            });
        db.SaveChanges();
        return new Seeded(tenantId, paymentBatch.Id, run.Id);
    }

    private static async Task<(BankConfirmationImportResult? Result, JournalExportRefusal? Refusal)> ImportAsync(
        ZayraDbContext db, Seeded seeded, string csv, BankConfirmationImportOptions options)
    {
        var outcome = await new BankConfirmationService(db, new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser() })
            .ImportAsync(seeded.TenantId, seeded.BatchId, $"anb-status-{Guid.NewGuid():N}.csv", csv, BankChannelConfirmationFile.ParserKey,
                options, Guid.NewGuid(), "Payroll Officer", default);
        if (outcome.Refusal is null) await db.SaveChangesAsync();
        return outcome;
    }

    private static async Task<List<string>> Blockers(ZayraDbContext db, Seeded seeded)
    {
        var exports = new JournalExportService(db, Zayra.Api.Tests.JournalExportFormatterGoldenTests.AllFormatters());
        var report = await new PeriodHandoffReconciler(db, exports)
            .ReconcileAsync(seeded.TenantId, new JournalExportFilter(PayrollRunId: seeded.RunId), groupScopedCaller: true, default);
        return JsonDocument.Parse(JsonSerializer.Serialize(report)).RootElement.GetProperty("blockers").EnumerateArray()
            .Select(b => b.GetString()!).ToList();
    }

    private static string Status(ZayraDbContext db, int employeeRef) =>
        db.PayrollPaymentRecords.Single(r => r.EmployeeId == employeeRef).Status;

    private static AnbStatusLine Bank(Zayra.Api.Infrastructure.Payroll.SaudiBankExports.AnbPaymentInput r, string status,
        string? paymentId = null, string? trx = null, string? description = null) =>
        new(r.EmployeeNationalId!, r.AccountNumber!, r.SalaryAmount, r.BicCode, r.EmployeeName, paymentId, status,
            description, trx, IntentAt.AddHours(1), IntentAt.AddHours(1));

    private static AnbPayrollStatusSnapshot Snapshot(string status, IReadOnlyList<AnbStatusLine> lines, int itemCount, string? reason = null) =>
        new(PayrollId, status, reason, lines, itemCount, 1, 0,
            lines.Select(l => (l.EmployeeId, l.EmployeeAccountNumber)).Distinct().Count() == itemCount);
}
