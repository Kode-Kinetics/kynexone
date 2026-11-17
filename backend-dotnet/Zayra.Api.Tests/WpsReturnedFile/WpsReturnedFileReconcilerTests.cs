using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Tests.WpsReturnedFile;

/// <summary>
/// Matches each returned row to OUR payment record by employee (MOL-ID) and amount, and says in plain English and
/// Arabic what happened to each salary. Synthetic data only.
/// </summary>
public class WpsReturnedFileReconcilerTests
{
    private static readonly Regex Arabic = new("[\u0600-\u06FF]");

    private static IReadOnlyList<WpsExpectedLine> Expected(IReadOnlyList<SynthRow> rows) =>
        rows.Select((r, i) => new WpsExpectedLine(i + 1, $"EMP{i + 1:D4}", r.MolId, r.Iban, r.Net)).ToList();

    private static WpsReconciliation Run(IReadOnlyList<SynthRow> fileRows, IReadOnlyList<WpsExpectedLine> expected) =>
        WpsReturnedFileReconciler.Reconcile(
            WpsReturnedFileValidator.Read(WpsReturnedFileOracle.Build(fileRows), new WpsReturnedFileExpectation("7-1234567")),
            expected);

    private static IReadOnlyList<SynthRow> WithStatus(IReadOnlyList<SynthRow> rows, string status) =>
        rows.Select(r => r with { Status = status }).ToList();

    [Fact]
    public void Every_row_that_agrees_with_the_run_is_matched()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(5), "PAID");
        var rec = Run(rows, Expected(rows));

        rec.Lines.Should().HaveCount(5).And.OnlyContain(l => l.Classification == WpsReconciliationClass.MatchedPaid);
        rec.Count(WpsReconciliationClass.MatchedPaid).Should().Be(5);
    }

    [Fact]
    public void A_different_amount_is_an_amount_mismatch_with_both_figures()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(3), "PAID");
        var expected = Expected(rows).ToList();
        expected[1] = expected[1] with { Amount = expected[1].Amount + 50m };

        var line = Run(rows, expected).Lines.Single(l => l.Classification == WpsReconciliationClass.AmountMismatch);

        line.EmployeeCode.Should().Be("EMP0002");
        line.ExpectedAmount.Should().Be(expected[1].Amount);
        line.FileAmount.Should().Be(rows[1].Net);
        line.ReasonEn.Should().Contain("amount");
    }

    [Fact]
    public void A_returned_employee_we_do_not_have_in_the_run_is_an_unknown_employee()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(3), "PAID");
        var expected = Expected(rows).Take(2).ToList();

        var line = Run(rows, expected).Lines.Single(l => l.Classification == WpsReconciliationClass.UnknownEmployee);
        line.FileLine.Should().NotBeNull();
        line.EmployeeCode.Should().BeNull();
        line.MaskedMolId.Should().Be("***" + rows[2].MolId[^4..]);
    }

    [Fact]
    public void An_employee_we_paid_who_is_absent_from_the_file_is_missing_from_file()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(4), "PAID");
        var file = rows.Take(3).ToList();

        var rec = Run(file, Expected(rows));
        var line = rec.Lines.Single(l => l.Classification == WpsReconciliationClass.MissingFromFile);
        line.EmployeeCode.Should().Be("EMP0004");
        line.ExpectedAmount.Should().Be(rows[3].Net);
        line.FileLine.Should().BeNull();
    }

    [Fact]
    public void A_line_the_bank_rejected_reads_not_paid_rejected_by_bank_in_both_languages_and_says_no_reason_code_was_given()
    {
        var rows = WpsReturnedFileOracle.Rows(3).ToList();
        rows = WithStatus(rows, "PAID").ToList();
        rows[1] = rows[1] with { Status = "REJECTED" };

        var line = Run(rows, Expected(rows)).Lines.Single(l => l.Classification == WpsReconciliationClass.Rejected);

        line.EmployeeCode.Should().Be("EMP0002");
        line.Outcome.Should().Be("Rejected", "a rejection is not money that came back");
        line.ReasonCode.Should().BeNull("the file has no reason column; the status word is not a reason code");
        line.ReasonEn.Should().Contain(BankOutcomeLabels.NotPaidRejectedByBank.English).And.Contain("REJECTED")
            .And.Contain(BankStatusReasonCatalogue.Describe(null).English).And.Contain("bank statement");
        line.ReasonEn.Should().NotContain("came back");
        Arabic.IsMatch(line.ReasonAr).Should().BeTrue();
        line.ReasonAr.Should().Contain(BankOutcomeLabels.NotPaidRejectedByBank.Arabic).And.Contain("REJECTED")
            .And.Contain(BankStatusReasonCatalogue.Describe(null).Arabic).And.Contain("كشف حساب البنك");
    }

    [Fact]
    public void A_line_the_bank_returned_reads_the_money_came_back_in_both_languages()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(2), "PAID").ToList();
        rows[0] = rows[0] with { Status = "RETURNED" };

        var line = Run(rows, Expected(rows)).Lines.Single(l => l.Classification == WpsReconciliationClass.Returned);

        line.Outcome.Should().Be("Returned");
        line.ReasonCode.Should().BeNull();
        line.ReasonEn.Should().Contain(BankOutcomeLabels.ReturnedMoneyCameBack.English).And.NotContain("Not paid");
        line.ReasonAr.Should().Contain(BankOutcomeLabels.ReturnedMoneyCameBack.Arabic);
    }

    [Fact]
    public void A_duplicate_reason_is_held_for_a_person_and_never_reads_as_not_paid_or_still_owed()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(2), "PAID").ToList();
        rows[0] = rows[0] with { Status = "DUPL" };

        var line = Run(rows, Expected(rows)).Lines[0];

        line.Classification.Should().Be(WpsReconciliationClass.HeldForPerson);
        line.Outcome.Should().BeNull("nothing is recorded until a person checks whether the earlier payment arrived");
        line.ReasonCode.Should().Be("DUPL");
        line.ReasonEn.Should().Contain(BankStatusReasonCatalogue.Describe("DUPL").English).And.Contain("earlier payment")
            .And.NotContainAny("Not paid", "owed", "pay it again");
        line.ReasonAr.Should().Contain(BankStatusReasonCatalogue.Describe("DUPL").Arabic).And.NotContain("لم يُدفع");
    }

    [Theory]
    [InlineData("AC01")]
    [InlineData("AC04")]
    [InlineData("AM04")]
    [InlineData("BE01")]
    public void A_bank_reason_code_in_the_status_field_is_described_plainly_and_held_for_a_person(string code)
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(1), code);
        var line = Run(rows, Expected(rows)).Lines.Single();
        line.Classification.Should().Be(WpsReconciliationClass.HeldForPerson);
        line.Outcome.Should().BeNull();
        line.ReasonEn.Should().Contain(BankStatusReasonCatalogue.Describe(code).English);
        line.ReasonAr.Should().Contain(BankStatusReasonCatalogue.Describe(code).Arabic);
    }

    [Fact]
    public void ACWC_accepted_with_change_is_held_for_a_person()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(1), "ACWC");
        var line = Run(rows, Expected(rows)).Lines.Single();
        line.Classification.Should().Be(WpsReconciliationClass.HeldForPerson);
        line.Outcome.Should().BeNull();
        line.ReasonEn.Should().Contain(BankOutcomeLabels.HeldAcceptedWithChange.English);
        line.ReasonAr.Should().Contain(BankOutcomeLabels.HeldAcceptedWithChange.Arabic);
    }

    [Fact]
    public void Every_word_the_reader_gives_an_outcome_reads_the_same_in_the_shared_vocabulary()
    {
        foreach (var word in WpsReturnedFileStatus.OutcomeWords)
            WpsReturnedFileStatus.Classify(word).Should().Be(BankOutcomeVocabulary.Read(word).Outcome, word);
        // The reader stays narrower: these give an outcome in the shared vocabulary but confirm nothing here.
        foreach (var word in new[] { "SUBMITTED", "QUEUED", "AWAITING", "INPROGRESS", "TRANSFERRED" })
            WpsReturnedFileStatus.Classify(word).Should().BeNull(word);
    }

    [Fact]
    public void A_status_word_outside_the_known_vocabulary_is_never_guessed_into_paid_or_rejected()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(2), "PAID").ToList();
        rows[0] = rows[0] with { Status = "ZZ91" };

        var rec = Run(rows, Expected(rows));
        var line = rec.Lines.Single(l => l.Classification == WpsReconciliationClass.StatusNotRecognised);
        line.EmployeeCode.Should().Be("EMP0001");
        line.ReasonEn.Should().Contain("ZZ91");
    }

    [Fact]
    public void A_file_with_no_status_column_matches_on_employee_and_amount_but_confirms_nothing()
    {
        var rows = WpsReturnedFileOracle.Rows(2);
        var rec = Run(rows, Expected(rows));
        rec.Lines.Should().OnlyContain(l => l.Classification == WpsReconciliationClass.MatchedFiguresOnly && l.Outcome == null);
        rec.Count(WpsReconciliationClass.MatchedPaid).Should().Be(0, "a file with no status shows no paid line");
    }

    [Fact]
    public void The_same_employee_paid_into_a_different_account_is_an_account_mismatch()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(2), "PAID");
        var expected = Expected(rows).ToList();
        expected[0] = expected[0] with { Iban = WpsReturnedFileOracle.SaIban(10, 424_242_424_242L) };

        var line = Run(rows, expected).Lines.Single(l => l.Classification == WpsReconciliationClass.AccountMismatch);
        line.EmployeeCode.Should().Be("EMP0001");
        line.ReasonEn.Should().Contain("account");
    }

    [Fact]
    public void A_row_that_failed_the_format_rules_is_an_invalid_row_and_not_counted_as_paid()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(3), "PAID").ToList();
        rows[1] = rows[1] with { Name = "Ali\tSmith" };

        var rec = Run(rows, Expected(rows));
        rec.Lines.Single(l => l.Classification == WpsReconciliationClass.InvalidRow).FileLine.Should().NotBeNull();
        rec.Count(WpsReconciliationClass.MatchedPaid).Should().Be(2);
    }

    [Fact]
    public void Two_of_our_records_with_the_same_government_id_are_reported_ambiguous_not_guessed()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(2), "PAID");
        var expected = Expected(rows).ToList();
        expected[1] = expected[1] with { MolId = expected[0].MolId };

        var rec = Run(rows.Take(1).ToList(), expected);
        rec.Lines.Should().Contain(l => l.Classification == WpsReconciliationClass.Ambiguous);
    }

    [Fact]
    public void A_record_of_ours_with_no_government_id_cannot_be_matched_and_is_missing_from_file()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(2), "PAID");
        var expected = Expected(rows).ToList();
        expected[1] = expected[1] with { MolId = null };

        var rec = Run(rows, expected);
        rec.Lines.Should().Contain(l => l.Classification == WpsReconciliationClass.MissingFromFile && l.EmployeeCode == "EMP0002");
        rec.Lines.Should().Contain(l => l.Classification == WpsReconciliationClass.UnknownEmployee);
    }

    [Fact]
    public void Nothing_in_the_output_carries_a_full_iban_or_national_id_and_every_line_has_arabic()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(6), "PAID").ToList();
        rows[0] = rows[0] with { Status = "REJECTED" };
        var expected = Expected(rows).ToList();
        expected[1] = expected[1] with { Amount = 1m };
        expected[2] = expected[2] with { Iban = WpsReturnedFileOracle.SaIban(10, 424_242_424_242L) };
        var rec = Run(rows.Take(5).ToList(), expected.Take(6).ToList());

        var json = JsonSerializer.Serialize(rec);
        foreach (var r in rows)
        {
            json.Should().NotContain(r.MolId);
            json.Should().NotContain(r.Iban);
        }
        foreach (var e in expected) json.Should().NotContain(e.Iban!);
        rec.Lines.Should().OnlyContain(l => Arabic.IsMatch(l.ReasonAr) && l.ReasonEn.Length > 0);
        rec.Lines.Where(l => l.MaskedIban != null).Should().OnlyContain(l => l.MaskedIban!.StartsWith("***"));
    }

    [Fact]
    public void A_3000_line_run_reconciles_with_one_rejected_one_missing_and_one_mismatch()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(3000, arabicName: new string('س', 35)), "PAID").ToList();
        rows[10] = rows[10] with { Status = "REJECTED" };
        rows[11] = rows[11] with { Status = "RETURNED" };
        var expected = Expected(rows).ToList();
        expected[20] = expected[20] with { Amount = expected[20].Amount + 1m };
        var file = rows.Where((_, i) => i != 30).ToList();

        var rec = Run(file, expected);

        rec.Count(WpsReconciliationClass.MatchedPaid).Should().Be(2996);
        rec.Count(WpsReconciliationClass.Rejected).Should().Be(1);
        rec.Count(WpsReconciliationClass.Returned).Should().Be(1);
        rec.Count(WpsReconciliationClass.AmountMismatch).Should().Be(1);
        rec.Count(WpsReconciliationClass.MissingFromFile).Should().Be(1);
        rec.Lines.Should().HaveCount(3000);
    }

    // ── status safety: only explicit words, ACSP is pending, no single letters ─────

    [Theory]
    [InlineData("PAID")]
    [InlineData("paid")]
    [InlineData("SETTLED")]
    [InlineData("CREDITED")]
    [InlineData("ACSC")]
    [InlineData("ACCC")]
    public void Only_an_explicit_paid_word_counts_as_paid(string word)
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(1), word);
        Run(rows, Expected(rows)).Lines.Single().Classification.Should().Be(WpsReconciliationClass.MatchedPaid);
    }

    [Theory]
    [InlineData("P")]
    [InlineData("Y")]
    [InlineData("YES")]
    [InlineData("OK")]
    [InlineData("R")]
    [InlineData("N")]
    [InlineData("NO")]
    [InlineData("UNPAID")]
    [InlineData("REVERSED")]
    public void A_single_letter_or_loose_word_confirms_nothing(string word)
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(1), word);
        var line = Run(rows, Expected(rows)).Lines.Single();
        line.Classification.Should().Be(WpsReconciliationClass.StatusNotRecognised);
        line.Outcome.Should().BeNull();
    }

    [Theory]
    [InlineData("RETURN")]
    [InlineData("RETURNED")]
    [InlineData("RTRN")]
    [InlineData("BOUNCE")]
    [InlineData("BOUNCED")]
    public void Only_an_unambiguous_return_word_means_the_money_came_back(string word)
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(1), word);
        var line = Run(rows, Expected(rows)).Lines.Single();
        line.Classification.Should().Be(WpsReconciliationClass.Returned);
        line.Outcome.Should().Be("Returned");
    }

    [Theory]
    [InlineData("RJCT")]
    [InlineData("REJECT")]
    [InlineData("REJECTED")]
    [InlineData("DECLINED")]
    [InlineData("FAIL")]
    [InlineData("FAILED")]
    [InlineData("FAILURE")]
    [InlineData("CANCELLED_BY_BANK")]
    public void A_rejection_word_is_not_paid_rejected_by_bank_and_never_returned(string word)
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(1), word);
        var line = Run(rows, Expected(rows)).Lines.Single();
        line.Classification.Should().Be(WpsReconciliationClass.Rejected);
        line.Outcome.Should().Be("Rejected");
        line.ReasonEn.Should().Contain(BankOutcomeLabels.NotPaidRejectedByBank.English);
    }

    [Theory]
    [InlineData("SUCCESS")]
    [InlineData("SUCCESSFUL")]
    [InlineData("COMPLETED")]
    [InlineData("EXECUTED")]
    [InlineData("ACCEPTED")]
    public void A_success_word_means_the_bank_accepted_the_file_and_confirms_no_credit(string word)
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(1), word);
        var line = Run(rows, Expected(rows)).Lines.Single();
        line.Classification.Should().Be(WpsReconciliationClass.MatchedAccepted);
        line.Outcome.Should().BeNull("no status change");
        line.ReasonEn.Should().Contain("does not confirm");
    }

    [Fact]
    public void ACWP_and_ACSP_are_in_progress()
    {
        foreach (var word in new[] { "ACWP", "ACSP" })
            WpsReturnedFileStatus.Classify(word).Should().Be("Pending");
    }

    [Fact]
    public void ACSP_settlement_in_process_is_pending_never_paid()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(1), "ACSP");
        var rec = Run(rows, Expected(rows));
        rec.Lines.Single().Classification.Should().Be(WpsReconciliationClass.MatchedPending);
        rec.Lines.Single().Outcome.Should().Be("Pending");
        WpsReturnedFileStatus.Classify("ACSP").Should().Be("Pending");
        WpsReturnedFileStatus.Classify("RJCT").Should().Be("Rejected");
        WpsReturnedFileStatus.Classify("RETURNED").Should().Be("Returned");
        WpsReturnedFileStatus.Classify("P").Should().BeNull();
    }

    // ── returned lines are checked like any other ────────────────────────────

    [Fact]
    public void A_returned_line_that_names_a_different_account_is_an_account_mismatch_not_a_rejection()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(2), "REJECTED");
        var expected = Expected(rows).ToList();
        expected[0] = expected[0] with { Iban = WpsReturnedFileOracle.SaIban(10, 424_242_424_242L) };
        Run(rows, expected).Lines[0].Classification.Should().Be(WpsReconciliationClass.AccountMismatch);
    }

    [Theory]
    [InlineData("REJECTED", WpsReconciliationClass.Rejected)]
    [InlineData("RETURNED", WpsReconciliationClass.Returned)]
    public void A_rejected_or_returned_line_with_a_different_amount_keeps_its_outcome(string word, string classification)
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(1), word);
        var expected = Expected(rows).ToList();
        expected[0] = expected[0] with { Amount = expected[0].Amount + 50m };
        Run(rows, expected).Lines.Single().Classification.Should().Be(classification);
    }

    [Fact]
    public void A_payment_record_with_no_iban_is_never_matched_automatically()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(2), "PAID");
        var expected = Expected(rows).ToList();
        expected[1] = expected[1] with { Iban = "" };
        var rec = Run(rows, expected);
        rec.Lines[1].Classification.Should().Be(WpsReconciliationClass.AccountMissing);
        rec.Count(WpsReconciliationClass.MatchedPaid).Should().Be(1);

        var returned = WithStatus(WpsReturnedFileOracle.Rows(1), "REJECTED");
        Run(returned, new[] { Expected(returned)[0] with { Iban = null } }).Lines.Single().Classification.Should().Be(WpsReconciliationClass.AccountMissing);
    }

    // ── three matched states, wording, no figures in text ────────────────────

    [Fact]
    public void Matched_is_split_into_paid_pending_and_figures_only_with_separate_counts()
    {
        var rows = WpsReturnedFileOracle.Rows(3).ToList();
        rows[0] = rows[0] with { Status = "PAID" };
        rows[1] = rows[1] with { Status = "PENDING" };
        var rec = Run(rows, Expected(rows));

        rec.Count(WpsReconciliationClass.MatchedPaid).Should().Be(1);
        rec.Count(WpsReconciliationClass.MatchedPending).Should().Be(1);
        rec.Count(WpsReconciliationClass.MatchedFiguresOnly).Should().Be(1);
    }

    [Fact]
    public void No_reason_text_contains_a_wage_figure_but_the_direction_of_an_amount_difference_is_stated()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(2), "PAID");
        var expected = Expected(rows).ToList();
        expected[0] = expected[0] with { Amount = expected[0].Amount + 50m };      // file is lower
        expected[1] = expected[1] with { Amount = expected[1].Amount - 50m };      // file is higher
        var rec = Run(rows, expected);

        rec.Lines[0].ReasonEn.Should().Contain("lower");
        rec.Lines[1].ReasonEn.Should().Contain("higher");
        foreach (var l in rec.Lines)
        {
            l.ReasonEn.Should().NotMatchRegex(@"[0-9]{3,}[,.][0-9]{2}");
            l.ReasonAr.Should().NotMatchRegex(@"[0-9]{3,}[,.][0-9]{2}");
            l.FileAmount.Should().NotBeNull("the amounts stay available as structured fields");
        }
    }

    [Fact]
    public void An_invalid_line_shows_the_plain_message_not_the_internal_code_and_arabic_uses_the_glossary()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(3), "PAID").ToList();
        rows[1] = rows[1] with { Name = "Ali\tSmith" };
        var rec = Run(rows, Expected(rows));
        var invalid = rec.Lines.Single(l => l.Classification == WpsReconciliationClass.InvalidRow);
        invalid.ReasonEn.Should().NotContain("stray_value").And.Contain("does not belong to any");
        invalid.ReasonAr.Should().NotContain("stray_value").And.NotContain("[");

        var missing = Run(rows.Take(2).ToList(), Expected(rows)).Lines.Single(l => l.Classification == WpsReconciliationClass.MissingFromFile);
        missing.ReasonAr.Should().Contain("مسير الرواتب");
        foreach (var l in rec.Lines.Concat(Run(rows.Take(2).ToList(), Expected(rows)).Lines))
            l.ReasonAr.Should().NotContain("كشف الرواتب").And.NotContain("رمز موظف").And.NotContain("[");
    }

    [Fact]
    public void A_cancelled_record_that_is_absent_from_the_file_is_not_reported_missing()
    {
        var rows = WithStatus(WpsReturnedFileOracle.Rows(3), "PAID");
        var expected = Expected(rows).ToList();
        expected[2] = expected[2] with { Cancelled = true };
        Run(rows.Take(2).ToList(), expected).Count(WpsReconciliationClass.MissingFromFile).Should().Be(0);
    }

    [Fact]
    public void The_standing_notice_says_a_clean_match_is_not_mudad_acceptance_in_both_languages()
    {
        var rec = Run(WithStatus(WpsReturnedFileOracle.Rows(1), "PAID"), Expected(WpsReturnedFileOracle.Rows(1)));
        rec.NoticeEn.Should().Contain("does not show whether Mudad accepted");
        Arabic.IsMatch(rec.NoticeAr).Should().BeTrue();
    }
}
