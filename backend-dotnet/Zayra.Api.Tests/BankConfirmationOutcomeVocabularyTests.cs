using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Which bank status codes may mark a salary as Paid.
///
/// <para>THE DEFECT. The Paid vocabulary held <c>ACSP</c> and three ambiguous short codes (<c>P</c>, <c>Y</c>,
/// <c>OK</c>). ACSP is ISO 20022 "AcceptedSettlementInProcess": the bank has accepted the instruction but
/// settlement has NOT completed, so a salary read as Paid before the money moved. <c>P</c> can as easily mean
/// Pending or Processed, and <c>Y</c>/<c>OK</c> say only that the bank answered.</para>
///
/// <para>THE RULE. ACSP is the non-terminal Pending outcome (recorded as evidence, changes no status). The
/// three short codes are unrecognised, which the import already treats as an error that confirms nothing.
/// Every other token keeps its meaning. This changes how FUTURE files are read; stored confirmations keep the
/// canonical outcome written at import time and are never re-canonicalised.</para>
/// </summary>
public class BankConfirmationOutcomeVocabularyTests
{
    public static readonly TheoryData<string> AmbiguousShortCodes = new() { "P", "Y", "OK", "p", "y", "ok", " OK " };

    public static readonly TheoryData<string> UntouchedPaidTokens = new()
    {
        "PAID", "ACSC", "ACCC", "SETTLED", "SETTLE", "SUCCESS", "SUCCESSFUL", "SUCCEEDED",
        "CREDITED", "CREDIT", "COMPLETED", "COMPLETE", "EXECUTED", "YES", "TRANSFERRED", "paid", "acsc",
    };

    public static readonly TheoryData<string> UntouchedReturnedTokens = new()
    {
        "RJCT", "REJECT", "REJECTED", "RETURN", "RETURNED", "RTRN", "BOUNCE", "BOUNCED", "FAIL", "FAILED",
        "FAILURE", "UNPAID", "R", "DECLINED", "REVERSED", "CANCELLED_BY_BANK", "CANCELLED BY BANK", "NOK", "N", "NO",
    };

    // ── Vocabulary ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ACSP")]
    [InlineData("acsp")]
    [InlineData(" ACSP ")]
    public void Acsp_IsSettlementInProcess_NotPaid(string raw) =>
        BankOutcomeVocabulary.Canonicalise(raw).Should().Be(BankConfirmationOutcomes.Pending,
            "ISO 20022 ACSP = AcceptedSettlementInProcess: settlement has not completed");

    [Theory]
    [MemberData(nameof(AmbiguousShortCodes))]
    public void AmbiguousShortCodes_AreUnrecognised(string raw) =>
        BankOutcomeVocabulary.Canonicalise(raw).Should().BeNull(
            "a one-letter or 'OK' status does not say the money settled — guessing is how a salary reads as paid early");

    [Fact]
    public void KnownTokens_NoLongerAdvertiseTheAmbiguousCodes() =>
        BankOutcomeVocabulary.KnownTokens.Should().NotContain(new[] { "P", "Y", "OK" });

    [Theory]
    [MemberData(nameof(UntouchedPaidTokens))]
    public void UntouchedPaidTokens_StillMeanPaid(string raw) =>
        BankOutcomeVocabulary.Canonicalise(raw).Should().Be(BankConfirmationOutcomes.Paid);

    [Theory]
    [MemberData(nameof(UntouchedReturnedTokens))]
    public void ReturnedTokens_AreUnchanged(string raw) =>
        BankOutcomeVocabulary.Canonicalise(raw).Should().Be(BankConfirmationOutcomes.Returned);

    // ── Generic CSV parser ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void GenericCsv_Acsp_ParsesAsPending_WithNoError()
    {
        var parse = new GenericCsvBankConfirmationParser().Parse("wpsReference,amount,status\nW1,100,ACSP\n");

        parse.Errors.Should().BeEmpty();
        parse.Rows.Single().Outcome.Should().Be(BankConfirmationOutcomes.Pending);
    }

    [Theory]
    [MemberData(nameof(AmbiguousShortCodes))]
    public void GenericCsv_AmbiguousShortCode_IsAnUnknownOutcomeError(string raw)
    {
        var parse = new GenericCsvBankConfirmationParser().Parse($"wpsReference,amount,status\nW1,100,{raw}\n");

        parse.Rows.Single().Outcome.Should().BeNull();
        parse.Errors.Should().ContainSingle(e => e.Error == "unknown_outcome" && e.RowNumber == 2);
    }

    [Theory]
    [InlineData("PAID")]
    [InlineData("ACSC")]
    public void GenericCsv_SettledTokens_StillParseAsPaid(string raw)
    {
        var parse = new GenericCsvBankConfirmationParser().Parse($"wpsReference,amount,status\nW1,100,{raw}\n");

        parse.Errors.Should().BeEmpty();
        parse.Rows.Single().Outcome.Should().Be(BankConfirmationOutcomes.Paid);
    }

    // ── WPS acknowledgement parser ───────────────────────────────────────────────────────────────

    private static string Ack(string status) =>
        $"ACK|B1|1\nREC|W1|SA0380000000608010167519|100|{status}|||2026-07-31|BR1\nEOF|1\n";

    [Fact]
    public void WpsAck_Acsp_ParsesAsPending_WithNoError()
    {
        var parse = new WpsAckBankConfirmationParser().Parse(Ack("ACSP"));

        parse.Errors.Should().BeEmpty();
        parse.Rows.Single().Outcome.Should().Be(BankConfirmationOutcomes.Pending);
    }

    [Theory]
    [MemberData(nameof(AmbiguousShortCodes))]
    public void WpsAck_AmbiguousShortCode_IsAnUnknownOutcomeError(string raw)
    {
        var parse = new WpsAckBankConfirmationParser().Parse(Ack(raw));

        parse.Rows.Single().Outcome.Should().BeNull();
        parse.Errors.Should().ContainSingle(e => e.Error == "unknown_outcome");
    }

    [Theory]
    [InlineData("PAID")]
    [InlineData("ACSC")]
    public void WpsAck_SettledTokens_StillParseAsPaid(string raw)
    {
        var parse = new WpsAckBankConfirmationParser().Parse(Ack(raw));

        parse.Errors.Should().BeEmpty();
        parse.Rows.Single().Outcome.Should().Be(BankConfirmationOutcomes.Paid);
    }

    // ── Import service: the payment record must not move to Paid ────────────────────────────────

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (Guid TenantId, Guid BatchId, PayrollPaymentRecord Record) Seed(ZayraDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var run = new PayrollRun { TenantId = tenantId, CompanyId = Guid.NewGuid(), Year = 2026, Month = 7, Status = "Locked" };
        db.PayrollRuns.Add(run);
        var batch = new PayrollPaymentBatch { TenantId = tenantId, PayrollRunId = run.Id };
        db.PayrollPaymentBatches.Add(batch);
        var record = new PayrollPaymentRecord
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = 1, Amount = 100m,
            Iban = "SA0380000000608010167519", Status = "Pending", WpsReference = "W1",
        };
        db.PayrollPaymentRecords.Add(record);
        db.SaveChanges();
        return (tenantId, batch.Id, record);
    }

    private static BankConfirmationService Service(ZayraDbContext db) =>
        new(db, new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser(), new WpsAckBankConfirmationParser() });

    [Theory]
    [InlineData("generic-csv")]
    [InlineData("wps-ack")]
    public async Task Import_Acsp_IsReportedPending_AndTheRecordStaysUnpaid(string parserKey)
    {
        using var db = NewDb();
        var (tenantId, batchId, record) = Seed(db);
        var content = parserKey == "wps-ack" ? Ack("ACSP") : "wpsReference,amount,status\nW1,100,ACSP\n";

        var (result, refusal) = await Service(db).ImportAsync(tenantId, batchId, "bank.txt", content, parserKey,
            new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);

        refusal.Should().BeNull();
        result!.PaidApplied.Should().Be(0);
        result.PendingReported.Should().Be(1);
        record.Status.Should().Be("Pending", "settlement in process is not settlement");
        result.Coverage.PaidCount.Should().Be(0);
    }

    [Theory]
    [InlineData("generic-csv", "P")]
    [InlineData("generic-csv", "Y")]
    [InlineData("generic-csv", "OK")]
    [InlineData("wps-ack", "P")]
    [InlineData("wps-ack", "Y")]
    [InlineData("wps-ack", "OK")]
    public async Task Import_AmbiguousShortCode_ConfirmsNothing(string parserKey, string raw)
    {
        using var db = NewDb();
        var (tenantId, batchId, record) = Seed(db);
        var content = parserKey == "wps-ack" ? Ack(raw) : $"wpsReference,amount,status\nW1,100,{raw}\n";

        var (result, refusal) = await Service(db).ImportAsync(tenantId, batchId, "bank.txt", content, parserKey,
            new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);

        refusal.Should().BeNull();
        result!.PaidApplied.Should().Be(0);
        result.ParseErrors.Should().HaveCount(1);
        result.Unmatched.Should().HaveCount(1, "an unrecognised status is held back, never applied");
        record.Status.Should().Be("Pending");
        db.ChangeTracker.Entries<BankPaymentConfirmation>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("generic-csv", "PAID")]
    [InlineData("generic-csv", "ACSC")]
    [InlineData("wps-ack", "PAID")]
    [InlineData("wps-ack", "ACSC")]
    public async Task Import_SettledToken_StillMarksTheRecordPaid(string parserKey, string raw)
    {
        using var db = NewDb();
        var (tenantId, batchId, record) = Seed(db);
        var content = parserKey == "wps-ack" ? Ack(raw) : $"wpsReference,amount,status\nW1,100,{raw}\n";

        var (result, refusal) = await Service(db).ImportAsync(tenantId, batchId, "bank.txt", content, parserKey,
            new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);

        refusal.Should().BeNull();
        result!.PaidApplied.Should().Be(1);
        record.Status.Should().Be(PaymentRecordStatuses.Paid);
    }
}
