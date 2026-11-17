using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
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

        rec.Lines.Should().HaveCount(5).And.OnlyContain(l => l.Classification == WpsReconciliationClass.Matched);
        rec.Count(WpsReconciliationClass.Matched).Should().Be(5);
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
    public void A_line_the_bank_rejected_is_reported_with_its_reason_code_in_both_languages()
    {
        var rows = WpsReturnedFileOracle.Rows(3).ToList();
        rows = WithStatus(rows, "PAID").ToList();
        rows[1] = rows[1] with { Status = "REJECTED" };

        var line = Run(rows, Expected(rows)).Lines.Single(l => l.Classification == WpsReconciliationClass.Rejected);

        line.EmployeeCode.Should().Be("EMP0002");
        line.ReasonCode.Should().Be("REJECTED");
        line.Outcome.Should().Be("Returned");
        line.ReasonEn.Should().Contain("REJECTED").And.Contain("not paid");
        Arabic.IsMatch(line.ReasonAr).Should().BeTrue();
        line.ReasonAr.Should().Contain("REJECTED");
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
        rec.Lines.Should().OnlyContain(l => l.Classification == WpsReconciliationClass.Matched && l.Outcome == null);
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
        rec.Count(WpsReconciliationClass.Matched).Should().Be(2);
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
        var expected = Expected(rows).ToList();
        expected[20] = expected[20] with { Amount = expected[20].Amount + 1m };
        var file = rows.Where((_, i) => i != 30).ToList();

        var rec = Run(file, expected);

        rec.Count(WpsReconciliationClass.Matched).Should().Be(2997);
        rec.Count(WpsReconciliationClass.Rejected).Should().Be(1);
        rec.Count(WpsReconciliationClass.AmountMismatch).Should().Be(1);
        rec.Count(WpsReconciliationClass.MissingFromFile).Should().Be(1);
        rec.Lines.Should().HaveCount(3000);
    }
}
