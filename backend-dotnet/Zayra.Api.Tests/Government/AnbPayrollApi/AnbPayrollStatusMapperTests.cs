using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Government.AnbPayrollApi.AnbApiTestKit;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// ANB's published V2 statuses mapped into what the EXISTING bank-confirmation import accepts
/// (<see cref="BankConfirmationRow"/> and the <c>generic-csv</c> parser). Unknown words fail loudly;
/// lines are matched to what we sent (national ID AND account), never guessed.
/// </summary>
public class AnbPayrollStatusMapperTests
{
    [Theory]
    [InlineData("ACCEPTED", BankPayrollPhase.Accepted)]
    [InlineData("INITIATED", BankPayrollPhase.Initiated)]
    [InlineData("VALIDATED", BankPayrollPhase.Validated)]
    [InlineData("IN_PROGRESS", BankPayrollPhase.InProgress)]
    [InlineData("IN PROGRESS", BankPayrollPhase.InProgress)]
    [InlineData("COMPLETED", BankPayrollPhase.Completed)]
    [InlineData("FAILED", BankPayrollPhase.Failed)]
    [InlineData("REJECTED", BankPayrollPhase.Rejected)]
    [InlineData(" completed ", BankPayrollPhase.Completed)]
    public void PayrollStatuses_MapToPhases(string raw, BankPayrollPhase expected) =>
        AnbPayrollStatusMapper.MapPayrollStatus(raw).Should().Be(expected);

    [Theory]
    [InlineData("INITIATED", AnbTransactionPhase.Initiated, BankConfirmationOutcomes.Pending)]
    [InlineData("PENDING", AnbTransactionPhase.Pending, BankConfirmationOutcomes.Pending)]
    [InlineData("IN PROGRESS", AnbTransactionPhase.InProgress, BankConfirmationOutcomes.Pending)]
    [InlineData("IN_PROGRESS", AnbTransactionPhase.InProgress, BankConfirmationOutcomes.Pending)]
    [InlineData("PAID", AnbTransactionPhase.Paid, BankConfirmationOutcomes.Paid)]
    [InlineData("PAID ", AnbTransactionPhase.Paid, BankConfirmationOutcomes.Paid)]
    [InlineData("RETURNED", AnbTransactionPhase.Returned, BankConfirmationOutcomes.Returned)]
    [InlineData("FAILED", AnbTransactionPhase.Failed, BankConfirmationOutcomes.Rejected)]   // refused by the bank: not paid, never "money came back" (CTO gate 8.4.2)
    public void TransactionStatuses_MapToTheExistingOutcomeVocabulary(string raw, AnbTransactionPhase phase, string outcome)
    {
        var mapped = AnbPayrollStatusMapper.MapTransactionStatus(raw);
        mapped.Should().Be(phase);
        AnbPayrollStatusMapper.ToConfirmationOutcome(mapped).Should().Be(outcome);

        // Where the shared vocabulary already knows ANB's word, the two must agree.
        var shared = BankOutcomeVocabulary.Canonicalise(raw);
        if (shared is not null) shared.Should().Be(outcome);
    }

    [Theory]
    [InlineData("SETTLED")]
    [InlineData("PROCESSED")]
    [InlineData("SUCCESS")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("COMPLETED")]
    [InlineData("REJECTED")]
    [InlineData("ACCEPTED")]
    [InlineData("pa\u0131d")] // dotless i: upper-cases to PAID, must not read as Paid
    public void UnknownTransactionWords_FailLoudly(string? raw)
    {
        var act = () => AnbPayrollStatusMapper.MapTransactionStatus(raw);
        act.Should().Throw<AnbUnknownStatusException>().Which.Kind.Should().Be("transaction");
    }

    [Theory]
    [InlineData("PAID")]       // a transaction word, not a payroll word
    [InlineData("DONE")]
    [InlineData("")]
    public void UnknownPayrollWords_FailLoudly(string raw)
    {
        var act = () => AnbPayrollStatusMapper.MapPayrollStatus(raw);
        act.Should().Throw<AnbUnknownStatusException>().Which.Kind.Should().Be("payroll");
    }

    [Fact]
    public void AnUnknownWordCarryingDigits_IsNotEchoed()
    {
        var act = () => AnbPayrollStatusMapper.MapTransactionStatus("PAID 1012345678");
        var ex = act.Should().Throw<AnbUnknownStatusException>().Which;
        ex.Message.Should().NotContain("1012345678");
        ex.Token.Should().Be("<withheld>");
    }

    [Theory]
    [InlineData(null, BankPayrollPhase.Accepted, BankPayrollPhase.Accepted, false)]
    [InlineData(BankPayrollPhase.Validated, BankPayrollPhase.InProgress, BankPayrollPhase.InProgress, false)]
    [InlineData(BankPayrollPhase.InProgress, BankPayrollPhase.Validated, BankPayrollPhase.InProgress, true)]
    [InlineData(BankPayrollPhase.Completed, BankPayrollPhase.InProgress, BankPayrollPhase.Completed, true)]
    [InlineData(BankPayrollPhase.Completed, BankPayrollPhase.Rejected, BankPayrollPhase.Completed, true)]
    [InlineData(BankPayrollPhase.Rejected, BankPayrollPhase.Completed, BankPayrollPhase.Rejected, true)]
    [InlineData(BankPayrollPhase.Accepted, BankPayrollPhase.Rejected, BankPayrollPhase.Rejected, false)]
    [InlineData(BankPayrollPhase.InProgress, BankPayrollPhase.Failed, BankPayrollPhase.Failed, false)]
    [InlineData(BankPayrollPhase.Completed, BankPayrollPhase.Completed, BankPayrollPhase.Completed, false)]
    public void PayrollPhase_IsMonotonic_AndTerminalsNeverFlip(BankPayrollPhase? previous, BankPayrollPhase next,
        BankPayrollPhase expected, bool held)
    {
        AnbPayrollStatusMapper.AdvancePayroll(previous, next, out var wasHeld).Should().Be(expected);
        wasHeld.Should().Be(held);
    }

    [Fact]
    public void Map_ResolvesEachLineByNationalIdAndAccount_ToTheWpsReference_AndCarriesNoPersonalData()
    {
        var batch = BuildBatch(4);
        var lines = new[]
        {
            Bank(batch.Rows[0], "PAID", "PMT-1", "TRX-1"),
            Bank(batch.Rows[1], "FAILED", null, null, "Beneficiary account closed"),
            Bank(batch.Rows[2], "INITIATED", null, null),
            Bank(batch.Rows[3], "RETURNED", "PMT-4", "TRX-4", "Returned by beneficiary bank"),
        };

        var mapped = AnbPayrollStatusMapper.Map(Snapshot("IN_PROGRESS", lines, 4), Binding(batch));

        mapped.PayrollPhase.Should().Be(BankPayrollPhase.InProgress);
        mapped.Unresolved.Should().BeEmpty();
        mapped.Complete.Should().BeTrue();
        mapped.Rows.Select(r => (r.WpsReference, r.Outcome, r.RawOutcome, r.Amount)).Should().Equal(
            (WpsReference(1), BankConfirmationOutcomes.Paid, "PAID", batch.Rows[0].SalaryAmount),
            (WpsReference(2), BankConfirmationOutcomes.Rejected, "FAILED", batch.Rows[1].SalaryAmount),
            (WpsReference(3), BankConfirmationOutcomes.Pending, "INITIATED", batch.Rows[2].SalaryAmount),
            (WpsReference(4), BankConfirmationOutcomes.Returned, "RETURNED", batch.Rows[3].SalaryAmount));
        mapped.Rows[0].BankReference.Should().Be("TRX-1");
        mapped.Rows[1].ReasonText.Should().Be("Beneficiary account closed");
        mapped.Rows[2].BankReference.Should().BeNull("no paymentId before payment");
        mapped.Rows.Should().OnlyContain(r => r.EmployeeCode == null && r.Iban == null,
            "the bank's employeeId is a national ID, never our employee code; matching uses our own WPS reference");
    }

    [Fact]
    public void Map_ALineWeDidNotSend_IsUnresolved_NeverMatchedByGuess()
    {
        var batch = BuildBatch(2);
        var stranger = batch.Rows[1] with { AccountNumber = Iban(999) };
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot("COMPLETED", new[] { Bank(batch.Rows[0], "PAID", "P1", "T1"), Bank(stranger, "PAID", "P2", "T2") }, 2),
            Binding(batch));

        mapped.Rows.Should().ContainSingle().Which.WpsReference.Should().Be(WpsReference(1));
        var unresolved = mapped.Unresolved.Should().ContainSingle().Subject;
        unresolved.Reason.Should().Be("not_in_submitted_batch");
        unresolved.MaskedEmployeeId.Should().NotContain(NationalId(2)).And.EndWith(NationalId(2)[^4..]);
    }

    [Fact]
    public void Map_PaidWithoutAPaymentId_IsKept_ButFlagged()
    {
        var batch = BuildBatch(1);
        var mapped = AnbPayrollStatusMapper.Map(Snapshot("COMPLETED", new[] { Bank(batch.Rows[0], "PAID", null, null) }, 1), Binding(batch));
        mapped.Rows.Single().Outcome.Should().Be(BankConfirmationOutcomes.Paid);
        mapped.Anomalies.Should().ContainSingle().Which.Should().Be(new BankLineAnomaly(WpsReference(1), "paid_without_payment_id"));
    }

    [Fact]
    public void Map_AnUnknownLineStatus_IsHeld_AndTheOtherLinesStillMap()
    {
        var batch = BuildBatch(2);
        var mapped = AnbPayrollStatusMapper.Map(
            Snapshot("IN_PROGRESS", new[] { Bank(batch.Rows[0], "PAID", "P", "T"), Bank(batch.Rows[1], "QUEUED") }, 2), Binding(batch));

        mapped.Rows.Should().ContainSingle().Which.Outcome.Should().Be(BankConfirmationOutcomes.Paid);
        var held = mapped.Unresolved.Should().ContainSingle().Subject;
        held.Reason.Should().Be("unknown_status");
        held.RawStatus.Should().Be("QUEUED");
        mapped.Complete.Should().BeFalse("a held line is not a settled line");
    }

    [Fact]
    public void Map_RowOrderIsStable_SoTheSameStateGivesTheSameFileHash()
    {
        var batch = BuildBatch(30);
        var lines = batch.Rows.Select(r => Bank(r, r.EmployeeRef % 2 == 0 ? "PAID" : "PENDING", r.EmployeeRef % 2 == 0 ? "P" + r.EmployeeRef : null, null)).ToList();
        var a = BankChannelConfirmationFile.Render(PayrollId, AnbPayrollStatusMapper.Map(Snapshot("IN_PROGRESS", lines, 30), Binding(batch)).Rows);
        var shuffled = lines.OrderBy(l => l.EmployeeId.GetHashCode()).ToList();
        var b = BankChannelConfirmationFile.Render(PayrollId, AnbPayrollStatusMapper.Map(Snapshot("IN_PROGRESS", shuffled, 30), Binding(batch)).Rows);
        a.Should().Be(b);
        BankConfirmationService.Sha256(a).Should().Be(BankConfirmationService.Sha256(b),
            "the existing import's tenant-wide file-hash dedupe makes a repeated poll a no-op");
    }

    [Fact]
    public void ConfirmationCsv_RoundTripsThroughTheExistingGenericParser_At3000Lines()
    {
        var batch = BuildBatch(3000);
        var statuses = new[] { "PAID", "RETURNED", "FAILED", "PENDING", "IN PROGRESS", "INITIATED" };
        var lines = batch.Rows.Select((r, i) => Bank(r, statuses[i % statuses.Length],
            statuses[i % statuses.Length] is "PAID" or "RETURNED" ? "P" + i : null,
            statuses[i % statuses.Length] is "PAID" or "RETURNED" ? "T" + i : null,
            i % 5 == 0 ? "Reason, with comma \"quoted\"" : null)).ToList();
        var mapped = AnbPayrollStatusMapper.Map(Snapshot("IN_PROGRESS", lines, 3000), Binding(batch));

        var csv = BankChannelConfirmationFile.Render(PayrollId, mapped.Rows);
        var parsed = new GenericCsvBankConfirmationParser().Parse(csv);

        BankChannelConfirmationFile.ParserKey.Should().Be(new GenericCsvBankConfirmationParser().Key);
        parsed.Errors.Should().BeEmpty();
        parsed.Rows.Should().HaveCount(3000);
        parsed.Rows.Select(r => (r.WpsReference, r.Outcome, r.Amount, r.ReasonCode, r.BankReference, r.ReasonText))
            .Should().Equal(mapped.Rows.Select(r => (r.WpsReference, r.Outcome, r.Amount, r.ReasonCode, r.BankReference, r.ReasonText)));
        parsed.Rows.Sum(r => r.Amount).Should().Be(batch.Total);
        csv.Should().NotContain(NationalId(1)).And.NotContain(Iban(1)).And.NotContain(ArabicName35(1));
    }

    [Fact]
    public void ConfirmationCsv_MasksIbanAndIdShapesInTheBankDescription()
    {
        var batch = BuildBatch(1);
        var mapped = AnbPayrollStatusMapper.Map(Snapshot("COMPLETED",
            new[] { Bank(batch.Rows[0], "FAILED", null, null, $"Account {Iban(1)} of {NationalId(1)} closed") }, 1), Binding(batch));
        mapped.Rows.Single().ReasonText.Should().NotContain(Iban(1)).And.NotContain(NationalId(1)).And.Contain("closed");
    }

    private static AnbStatusLine Bank(Zayra.Api.Infrastructure.Payroll.SaudiBankExports.AnbPaymentInput r, string status,
        string? paymentId = null, string? trx = null, string? description = null) =>
        new(r.EmployeeNationalId!, r.AccountNumber!, r.SalaryAmount, r.BicCode, r.EmployeeName, paymentId, status,
            description, trx, IntentAt.AddHours(1), IntentAt.AddHours(1));

    private static AnbPayrollStatusSnapshot Snapshot(string status, IReadOnlyList<AnbStatusLine> lines, int itemCount) =>
        new(PayrollId, status, null, lines, itemCount, 1, 0, lines.Select(l => (l.EmployeeId, l.EmployeeAccountNumber)).Distinct().Count() == itemCount);
}
