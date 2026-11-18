using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;
using Zayra.Api.Tests.WpsReturnedFile;

namespace Zayra.Api.Tests.OfficerWording;

/// <summary>
/// Government wave 1, package F5: what the officer reads from the returned-file reader, its validator and its parsers.
/// Wording only: no classification, outcome, code or applied status changes here (the reader's own tests pin those).
/// </summary>
public class ReturnedFileWordingTests
{
    private static IReadOnlyList<WpsExpectedLine> Expected(IReadOnlyList<SynthRow> rows) =>
        rows.Select((r, i) => new WpsExpectedLine(i + 1, $"EMP{i + 1:D4}", r.MolId, r.Iban, r.Net)).ToList();

    private static WpsReconciliation Run(IReadOnlyList<SynthRow> fileRows, IReadOnlyList<WpsExpectedLine> expected) =>
        WpsReturnedFileReconciler.Reconcile(
            WpsReturnedFileValidator.Read(WpsReturnedFileOracle.Build(fileRows), new WpsReturnedFileExpectation("7-1234567")),
            expected);

    private static IReadOnlyList<SynthRow> WithStatus(int count, string status) =>
        WpsReturnedFileOracle.Rows(count).Select(r => r with { Status = status }).ToList();

    // ── B1: ACSC / ACCC are "settled by the bank", never "paid" ─────────────────────────────────

    [Theory]
    [InlineData("ACSC")]
    [InlineData("ACCC")]
    [InlineData("acsc")]
    public void ASettlementWord_ReadsSettledByTheBank_AndSendsTheOfficerToTheStatement(string word)
    {
        var rows = WithStatus(2, word);

        var line = Run(rows, Expected(rows)).Lines.First();

        line.Classification.Should().Be(WpsReconciliationClass.MatchedPaid, "the classification and outcome do not change; only the words do");
        line.Outcome.Should().Be(BankConfirmationOutcomes.Paid);
        line.ReasonEn.Should().Contain("settled by the bank").And.Contain("confirm on the bank statement").And.NotContain("reports it paid");
        line.ReasonAr.Should().Contain("تمت تسويتها لدى البنك").And.Contain("تأكد من كشف حساب البنك").And.NotContain("دُفع");
    }

    [Theory]
    [InlineData("PAID")]
    [InlineData("CREDITED")]
    [InlineData("SETTLED")]
    public void APaidWord_KeepsTheExistingPaidText(string word)
    {
        var rows = WithStatus(1, word);

        var line = Run(rows, Expected(rows)).Lines.Single();

        line.ReasonEn.Should().Contain("the bank reports it paid");
        line.ReasonAr.Should().Contain("دُفع");
    }

    // ── F7a: a rejected line says what to do next ───────────────────────────────────────────────

    [Fact]
    public void AReturnedLine_SaysCorrectTheBankDetails_ThenPayInANewPaymentBatch_InBothLanguages()
    {
        var rows = WithStatus(2, "PAID").ToList();
        rows[1] = rows[1] with { Status = "RETURNED" };

        var line = Run(rows, Expected(rows)).Lines.Single(l => l.Classification == WpsReconciliationClass.Returned);

        line.ReasonEn.Should().Contain("pay it in a new payment batch linked to the original batch").And.NotContain("pay again");
        line.ReasonAr.Should().Contain("ادفعه في دفعة دفع جديدة مرتبطة بالدفعة الأصلية").And.NotContain("أعد الصرف");
    }

    [Fact]
    public void ARejectedLine_SaysCheckTheStatement_ThenPayInANewPaymentBatch_InBothLanguages()
    {
        var rows = WithStatus(2, "PAID").ToList();
        rows[1] = rows[1] with { Status = "REJECTED" };

        var line = Run(rows, Expected(rows)).Lines.Single(l => l.Classification == WpsReconciliationClass.Rejected);

        line.ReasonEn.Should().Contain("Check the bank statement to confirm no money left")
            .And.Contain("this salary is still owed to the employee").And.Contain("new payment batch linked to the original batch").And.NotContain("supplementary");
        line.ReasonAr.Should().Contain("راجع كشف حساب البنك").And.Contain("ما زال مستحقًا للموظف").And.Contain("دفعة دفع جديدة مرتبطة بالدفعة الأصلية").And.NotContain("مسير دفع إضافي");
        line.ReasonEn.Length.Should().BeLessThan(1000, "the English text is stored on the ledger row, which holds 1000 characters");
    }

    // ── F7b: the amount text, with no legal claim about deductions ──────────────────────────────

    [Fact]
    public void ALowerAndAHigherAmount_ReadDifferently_AndMakeNoDeductionLimitClaim()
    {
        var rows = WithStatus(3, "PAID");
        var expected = Expected(rows).ToList();
        expected[0] = expected[0] with { Amount = expected[0].Amount + 50m };   // file is lower
        expected[1] = expected[1] with { Amount = expected[1].Amount - 50m };   // file is higher

        var lines = Run(rows, expected).Lines.Where(l => l.Classification == WpsReconciliationClass.AmountMismatch).OrderBy(l => l.FileLine).ToList();
        var lower = lines[0];
        var higher = lines[1];

        lower.ReasonEn.Should().Contain("lower").And.Contain("may have received less than they are owed").And.Contain("held until someone reviews it");
        higher.ReasonEn.Should().Contain("higher").And.Contain("may have been overpaid")
            .And.Contain("Do not recover it from pay until Finance and HR decide how");
        lower.ReasonAr.Should().NotBe(higher.ReasonAr);
        lower.ReasonAr.Should().Contain("أقل من مبلغ مسير الرواتب").And.Contain("يبقى السطر معلقًا");
        higher.ReasonAr.Should().Contain("أكبر من مبلغ مسير الرواتب").And.Contain("لا تسترد الفرق من الراتب");
        foreach (var line in lines)
        {
            line.ReasonEn.Should().NotContainEquivalentOf("law").And.NotContainEquivalentOf("limit").And.NotContainEquivalentOf("deduct");
            line.ReasonAr.Should().NotContain("نظام").And.NotContain("مقيّد").And.NotContain("خصم");
        }
    }

    // ── F8: no jargon ("repository") in an officer-facing validator message ─────────────────────

    [Fact]
    public void NoValidatorMessage_SaysRepository_InEnglishOrArabic()
    {
        foreach (var code in WpsReturnedFileValidator.MessageCodes)
        {
            var (en, ar) = WpsReturnedFileValidator.MessageFor(code);
            en.Should().NotContainEquivalentOf("repository", code);
            en.Should().NotContainEquivalentOf("repo ", code);
            ar.Should().NotContain("المستودع", code);
        }
    }

    [Fact]
    public void TheUnverifiedLayoutDateAndHeaderMessages_SayWhatIsNotConfirmed_InPlainWords()
    {
        var layout = WpsReturnedFileValidator.MessageFor("line_layout");
        layout.En.Should().Be("The tab and tag layout of this file is assumed and has not yet been checked against a real bank file.");
        layout.Ar.Should().Contain("مفترض").And.Contain("ولم يُتحقق منه بعد على ملف بنك حقيقي");

        var header = WpsReturnedFileValidator.MessageFor("header_tag_missing_unverified", "ESTB-ID");
        header.En.Should().Be("The header field [ESTB-ID] is not present. It is not treated as required, because it is not confirmed to be mandatory.");
        header.Ar.Should().Contain("لأنه لم يثبت أنه إلزامي");

        var date = WpsReturnedFileValidator.MessageFor("date_format_invalid", "32A-VAL");
        date.En.Should().EndWith("This is a warning only because the date format has not been confirmed.");
        date.Ar.Should().EndWith("هذا تحذير فقط لأن صيغة التاريخ لم يتم التأكد منها.");
    }

    // ── F7c: a file-level rejection code makes every salary in the batch unconfirmed ────────────

    [Fact]
    public void AFileRejectedCode_SaysEverySalaryInTheBatchIsUnconfirmed()
    {
        var (en, ar) = WpsReturnedFileValidator.MessageFor("file_rejected", "E42");

        en.Should().Contain("Treat every salary in this batch as unconfirmed").And.Contain("check the bank statement")
            .And.Contain("ask the bank what this code means").And.Contain("E42");
        ar.Should().Contain("اعتبر رواتب هذه الدفعة جميعها غير مؤكدة").And.Contain("راجع كشف حساب البنك").And.Contain("E42");
    }

    // ── F9: parser name, disclaimer and unknown-outcome errors ──────────────────────────────────

    [Fact]
    public void TheReturnedFileParser_IsNamedForTheOfficer_WithAnArabicDisclaimer()
    {
        var parser = new WpsReturnedFileBankConfirmationParser();

        parser.DisplayName.Should().Be("Bank-returned payroll file (read-only)");
        parser.Disclaimer.Should().StartWith("Unverified layout.").And.NotContain("UNVERIFIED LAYOUT");
        parser.DisclaimerAr.Should().NotBeNullOrWhiteSpace()
            .And.Contain("ليس تقديمًا إلى نظام حماية الأجور ولا قبولًا من مدد")
            .And.Contain("لن تُطبَّق أي حالة دفع على أي سجل قبل التحقق منه وتفعيله");
    }

    [Fact]
    public void EveryErrorTheGenericAndAckParsersReturn_HasArabic()
    {
        var generic = new GenericCsvBankConfirmationParser();
        var ack = new WpsAckBankConfirmationParser();
        var errors = new List<BankConfirmationParseError>();
        errors.AddRange(generic.Parse("").Errors);
        errors.AddRange(generic.Parse("status\nPAID\n").Errors);
        errors.AddRange(generic.Parse("wpsReference,status\nW1,PROCESSED\n").Errors);
        errors.AddRange(ack.Parse("ACK|B1|3\nZZZ|1\nREC|W1\nREC|W1|SA0380000000608010167519|100|WEIRD\nEOF|5\n").Errors);

        errors.Select(e => e.Error).Should().Contain(new[]
        {
            "empty_file", "no_identifier", "unknown_outcome", "unknown_record_tag", "malformed_record", "trailer_count_mismatch",
            "header_count_mismatch",
        });
        foreach (var error in errors)
        {
            error.DetailAr.Should().NotBeNullOrWhiteSpace(error.Error);
            error.DetailAr!.Any(ch => ch is >= '؀' and <= 'ۿ').Should().BeTrue(error.Error);
        }
    }

    [Fact]
    public void AnUnknownBankStatus_SaysNothingWasRecorded_AndSendsTheOfficerToTheStatement()
    {
        var error = new GenericCsvBankConfirmationParser().Parse("wpsReference,status\nW1,PROCESSED\n").Errors.Single(e => e.Error == "unknown_outcome");

        error.Detail.Should().Contain("The bank's status 'PROCESSED' is not one we recognise")
            .And.Contain("nothing was recorded for this line and the salary stays unconfirmed")
            .And.Contain("Check the bank statement for this employee").And.NotContain("map it explicitly");
        error.DetailAr.Should().Contain("غير معروفة لدينا").And.Contain("ويبقى الراتب غير مؤكد").And.Contain("PROCESSED");
    }
}
