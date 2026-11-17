using System.Text.RegularExpressions;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Tests.WpsReturnedFile;

/// <summary>
/// The reader of a BANK-PRODUCED, bank-signed WPS file. It only reads and compares: it never writes, repairs or strips
/// anything. Every rule carries a basis: VerifiedFromRepoFact (encoded in WpsConformance / KsaWageFileRules /
/// WpsSifValidator) or Unverified (not proven by the repo). An Unverified rule never applies or confirms anything; it warns,
/// except the fail-closed ones listed in WpsReturnedFileValidator.FailClosedUnverified (pinned below).
/// All data here is synthetic.
/// </summary>
public class WpsReturnedFileValidatorTests
{
    private static readonly WpsReturnedFileExpectation Expect = new("7-1234567");

    private static WpsReturnedFileReadResult Read(string content, WpsReturnedFileExpectation? e = null) =>
        WpsReturnedFileValidator.Read(content, e ?? Expect);

    private static IEnumerable<string> ErrorCodes(WpsReturnedFileReadResult r) =>
        r.Findings.Where(f => f.Severity == WpsFindingSeverity.Error).Select(f => f.Code);

    private static IEnumerable<string> WarningCodes(WpsReturnedFileReadResult r) =>
        r.Findings.Where(f => f.Severity == WpsFindingSeverity.Warning).Select(f => f.Code);

    // ── Self-consistent (built by the test oracle, which shares the reader's layout assumption) ─────────

    [Fact]
    public void Self_consistent_synthetic_file_has_no_errors_and_reads_every_field()
    {
        var rows = WpsReturnedFileOracle.Rows(3);
        var r = Read(WpsReturnedFileOracle.Build(rows));

        ErrorCodes(r).Should().BeEmpty();
        r.Rows.Should().HaveCount(3);
        r.Rows.Should().OnlyContain(x => !x.Blocked);
        r.Header!.FileRef.Should().Be("WPS2026090001");
        r.Header.MolEstablishmentId.Should().Be("7-1234567");
        r.Header.Currency.Should().Be("SAR");
        r.Header.DeclaredTotal.Should().Be(rows.Sum(x => x.Net));
        r.Rows[0].MolId.Should().Be(rows[0].MolId);
        r.Rows[0].Net.Should().Be(rows[0].Net);
        r.Rows[0].Basic.Should().Be(rows[0].Basic);
        r.Rows[0].Deductions.Should().Be(rows[0].Deductions);
    }

    /// <summary>
    /// NOT built by the oracle: typed out by hand, so it does not share the oracle's idea of the layout. It still follows the same
    /// UNVERIFIED layout assumption (that is the most any synthetic file can do); what it proves is that the reader returns the values
    /// a human wrote, not the values a writer encoded.
    /// </summary>
    [Fact]
    public void A_hand_typed_literal_file_is_read_to_exactly_the_values_written_in_it()
    {
        const string file =
            "[DEST-ID]\tXXXX\n[ESTB-ID]\t9000001\n[BANK-ACC]\tSYNTHETIC-ACCOUNT\n[32A-CCY]\tSAR\n[32A-VAL]\t20260930\n[32A-AMT]\t15400,00\n"
          + "[FILE-REF]\tFILE0001\n[MOL-ESTBID]\t7-1234567\n"
          + "[32B-AMT]\t6500,00\t[59-ACC]\tSA0380000000608010167519\t[59-NAME]\tTest Person One\t[MOL-ID]\t1098765432\t[MOL-BAS]\t5000,00\t[MOL-HAL]\t1250,00\t[MOL-OEA]\t500,00\t[MOL-DED]\t250,00\n"
          + "[32B-AMT]\t4900,00\t[59-ACC]\tSA4420000001234567891234\t[59-NAME]\tTest Person Two\t[MOL-ID]\t2123456789\t[MOL-BAS]\t4000,00\t[MOL-HAL]\t1000,00\t[MOL-OEA]\t0,00\t[MOL-DED]\t100,00\n"
          + "[32B-AMT]\t4000,00\t[59-ACC]\tSA2760000009876543210001\t[59-NAME]\tأحمد علي الاختبار\t[MOL-ID]\t1234567890\t[MOL-BAS]\t3000,00\t[MOL-HAL]\t750,00\t[MOL-OEA]\t250,00\t[MOL-DED]\t0,00\n";

        var r = Read(file);

        ErrorCodes(r).Should().BeEmpty();
        r.Header!.DeclaredTotal.Should().Be(15400.00m);
        r.Header.FileRef.Should().Be("FILE0001");
        r.Header.ValueDate.Should().Be(new DateOnly(2026, 9, 30));
        r.Rows.Select(x => (x.MolId, x.Net)).Should().Equal(("1098765432", 6500.00m), ("2123456789", 4900.00m), ("1234567890", 4000.00m));
        r.Rows[2].Name.Should().Be("أحمد علي الاختبار");
        r.Rows[0].Deductions.Should().Be(250.00m);
        r.Rows[1].Iban.Should().Be("SA4420000001234567891234");
    }

    [Fact]
    public void Self_consistent_synthetic_file_with_the_header_on_one_line_reads_the_same()
    {
        var rows = WpsReturnedFileOracle.Rows(2);
        var r = Read(WpsReturnedFileOracle.Build(rows, headerOnOneLine: true));
        ErrorCodes(r).Should().BeEmpty();
        r.Rows.Should().HaveCount(2);
    }

    [Fact]
    public void A_3000_row_file_is_read_with_no_errors_and_the_header_total_ties()
    {
        var rows = WpsReturnedFileOracle.Rows(3000);
        var r = Read(WpsReturnedFileOracle.Build(rows));

        ErrorCodes(r).Should().BeEmpty();
        r.Rows.Should().HaveCount(3000);
        r.Rows.Sum(x => x.Net!.Value).Should().Be(r.Header!.DeclaredTotal!.Value);
    }

    [Fact]
    public void A_3000_row_file_with_35_character_arabic_names_is_accepted()
    {
        var name35 = new string('م', 35);
        var r = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(3000, arabicName: name35)));

        ErrorCodes(r).Should().BeEmpty();
        r.Rows.Should().OnlyContain(x => x.Name == name35);
    }

    // ── 59-NAME ───────────────────────────────────────────────────────────────

    [Fact]
    public void An_arabic_name_of_36_characters_blocks_that_row()
    {
        var rows = WpsReturnedFileOracle.Rows(2, arabicName: new string('م', 36));
        var r = Read(WpsReturnedFileOracle.Build(rows));
        ErrorCodes(r).Should().Contain("name_too_long");
        r.Rows.Should().OnlyContain(x => x.Blocked);
    }

    [Fact]
    public void A_name_mixing_arabic_and_english_blocks_the_row()
    {
        var rows = WpsReturnedFileOracle.Rows(1, arabicName: "أحمد Ahmed");
        ErrorCodes(Read(WpsReturnedFileOracle.Build(rows))).Should().Contain("name_mixed_script");
    }

    [Fact]
    public void A_tab_injected_into_a_name_blocks_the_row_and_does_not_shift_the_other_fields()
    {
        var good = WpsReturnedFileOracle.Rows(3).ToList();
        var bad = good[1] with { Name = "Ali\tSmith" };
        var r = Read(WpsReturnedFileOracle.Build(new[] { good[0], bad, good[2] }));

        ErrorCodes(r).Should().Contain("stray_value");
        r.HasFileLevelError.Should().BeFalse("a single faulty line only blocks that line");
        r.Rows.Should().HaveCount(3);
        r.Rows[1].Blocked.Should().BeTrue();
        r.Rows[0].Blocked.Should().BeFalse();
        r.Rows[2].Blocked.Should().BeFalse();
        r.Rows[2].MolId.Should().Be(good[2].MolId);
    }

    [Fact]
    public void A_tab_that_smuggles_a_second_tag_pair_into_a_name_is_a_duplicate_tag_and_blocks_the_row()
    {
        var good = WpsReturnedFileOracle.Rows(2).ToList();
        var bad = good[0] with { Name = "Ali\t[MOL-ID]\t2999999999" };
        var r = Read(WpsReturnedFileOracle.Build(new[] { bad, good[1] }));

        ErrorCodes(r).Should().Contain("duplicate_tag");
        r.Rows[0].Blocked.Should().BeTrue();
        r.Rows[0].MolId.Should().NotBe("2999999999", "the smuggled ID must never be read as the employee's ID");
    }

    [Fact]
    public void A_comma_in_a_name_is_kept_intact_and_does_not_disturb_the_amount_columns()
    {
        var rows = WpsReturnedFileOracle.Rows(2).ToList();
        rows[0] = rows[0] with { Name = "Smith, John" };
        var r = Read(WpsReturnedFileOracle.Build(rows));

        ErrorCodes(r).Should().BeEmpty();
        r.Rows[0].Name.Should().Be("Smith, John");
        r.Rows[0].Net.Should().Be(rows[0].Net);
        r.Findings.Should().Contain(f => f.Code == "name_contains_comma" && f.Basis == WpsRuleBasis.Unverified);
    }

    [Fact]
    public void A_name_that_starts_with_a_formula_character_is_flagged()
    {
        var rows = WpsReturnedFileOracle.Rows(1, arabicName: "=SUM(A1)");
        WarningCodes(Read(WpsReturnedFileOracle.Build(rows))).Should().Contain("name_formula_prefix");
    }

    // ── Totals, rows, duplicates ─────────────────────────────────────────────

    [Fact]
    public void A_header_total_that_differs_from_the_row_sum_blocks_the_whole_file()
    {
        var rows = WpsReturnedFileOracle.Rows(5);
        var r = Read(WpsReturnedFileOracle.Build(rows, declaredTotal: rows.Sum(x => x.Net) + 0.01m));

        var f = r.Findings.Single(x => x.Code == "header_total_mismatch");
        f.Severity.Should().Be(WpsFindingSeverity.Error);
        f.Line.Should().BeNull("a total mismatch is a property of the file, not of one line");
        f.Basis.Should().Be(WpsRuleBasis.Unverified, "WpsSifValidator reconciles OUR run total, not a bank file's header total");
        r.HasFileLevelError.Should().BeTrue();
        f.Message.Should().NotMatchRegex("[0-9]+[,.][0-9]{2}", "no wage figure appears in message text");
    }

    [Fact]
    public void A_row_dropped_after_the_header_total_was_written_is_caught_by_the_total()
    {
        var rows = WpsReturnedFileOracle.Rows(10);
        var declared = rows.Sum(x => x.Net);
        var r = Read(WpsReturnedFileOracle.Build(rows.Take(9).ToList(), declaredTotal: declared));
        ErrorCodes(r).Should().Contain("header_total_mismatch");
    }

    [Fact]
    public void The_same_employee_on_two_rows_blocks_both_rows()
    {
        var rows = WpsReturnedFileOracle.Rows(3).ToList();
        rows.Add(rows[0] with { Iban = WpsReturnedFileOracle.SaIban(80, 999_999_999_999L) });
        var r = Read(WpsReturnedFileOracle.Build(rows));

        ErrorCodes(r).Should().Contain("duplicate_mol_id");
        r.HasFileLevelError.Should().BeTrue("a duplicated employee is a sign the file was altered");
        r.Rows.Where(x => x.MolId == rows[0].MolId).Should().OnlyContain(x => x.Blocked).And.HaveCount(2);
    }

    [Fact]
    public void The_same_account_on_two_rows_blocks_both_rows()
    {
        var rows = WpsReturnedFileOracle.Rows(3).ToList();
        rows[2] = rows[2] with { Iban = rows[0].Iban };
        var r = Read(WpsReturnedFileOracle.Build(rows));

        ErrorCodes(r).Should().Contain("duplicate_iban");
        r.HasFileLevelError.Should().BeTrue();
        r.Rows.Count(x => x.Blocked).Should().Be(2);
    }

    // ── Header ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_file_for_another_establishment_is_refused()
    {
        var r = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(2),
            new WpsReturnedFileOracle.Header(MolEstablishmentId: "7-7654321")));

        var f = r.Findings.Single(x => x.Code == "establishment_mismatch");
        f.Severity.Should().Be(WpsFindingSeverity.Error);
        r.HasFileLevelError.Should().BeTrue();
        f.Message.Should().NotContain("7654321", "establishment ids are masked in messages");
    }

    [Fact]
    public void The_same_establishment_written_without_the_hyphen_still_matches()
    {
        var r = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(2),
            new WpsReturnedFileOracle.Header(MolEstablishmentId: "71234567")));
        ErrorCodes(r).Should().BeEmpty();
    }

    [Theory]
    [InlineData("71-234567", "7-1234567", false)]   // two hyphenated ids with different parts are different establishments
    [InlineData("7-1234567", "7-1234567", true)]
    [InlineData("71234567", "7-1234567", true)]     // one side without a hyphen: digits compared
    [InlineData("7-1234567", "71234567", true)]
    [InlineData("7-1234568", "7-1234567", false)]
    public void Establishments_are_compared_by_their_hyphen_aware_parts(string inFile, string company, bool same)
    {
        WpsReturnedFileValidator.SameEstablishmentStrict(inFile, company).Should().Be(same);
        var r = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(2), new WpsReturnedFileOracle.Header(MolEstablishmentId: inFile)),
            new WpsReturnedFileExpectation(company));
        ErrorCodes(r).Contains("establishment_mismatch").Should().Be(!same);
    }

    [Fact]
    public void If_the_company_has_no_establishment_configured_the_file_cannot_be_trusted()
    {
        var r = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(2)), new WpsReturnedFileExpectation(null));
        ErrorCodes(r).Should().Contain("establishment_not_configured");
    }

    [Fact]
    public void Without_any_expectation_the_establishment_is_not_checked_and_says_so()
    {
        var r = WpsReturnedFileValidator.Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(2)), null);
        WarningCodes(r).Should().Contain("establishment_not_checked");
    }

    [Theory]
    [InlineData("FILE-REF")]
    [InlineData("DEST-ID")]
    [InlineData("BANK-ACC")]
    [InlineData("32A-CCY")]
    [InlineData("32A-AMT")]
    [InlineData("MOL-ESTBID")]
    public void A_missing_mandatory_header_tag_blocks_the_file(string tag)
    {
        var r = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(2), omitHeaderTags: new[] { tag }));
        r.Findings.Should().Contain(f => f.Severity == WpsFindingSeverity.Error && f.Message.Contains(tag));
        r.HasFileLevelError.Should().BeTrue();
    }

    [Fact]
    public void An_empty_FILE_REF_is_reported_as_missing_and_one_over_16_characters_is_refused()
    {
        var empty = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(1), new WpsReturnedFileOracle.Header(FileRef: "")));
        ErrorCodes(empty).Should().Contain("file_reference_missing");

        var tooLong = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(1), new WpsReturnedFileOracle.Header(FileRef: "ABCDEFGHIJKLMNOPQ")));
        ErrorCodes(tooLong).Should().Contain("file_reference_too_long");
    }

    [Fact]
    public void A_header_currency_other_than_SAR_is_refused()
    {
        var r = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(1), new WpsReturnedFileOracle.Header(Currency: "AED")));
        ErrorCodes(r).Should().Contain("currency_not_sar");
    }

    [Fact]
    public void A_file_level_reject_code_is_surfaced_and_blocks_applying_any_row()
    {
        var r = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(2),
            new WpsReturnedFileOracle.Header(FileRejectCode: "E017")));
        var f = r.Findings.Single(x => x.Code == "file_rejected");
        f.Severity.Should().Be(WpsFindingSeverity.Error);
        f.Basis.Should().Be(WpsRuleBasis.Unverified);
        f.Message.Should().Contain("E017");
        r.Header!.FileRejectCode.Should().Be("E017");
    }

    // ── Dates ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-09-30")]
    [InlineData("30092026")]
    [InlineData("20260230")]
    public void A_date_not_in_YYYYMMDD_is_a_warning_marked_unverified_never_a_block(string date)
    {
        var r = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(2), new WpsReturnedFileOracle.Header(ValueDate: date)));
        ErrorCodes(r).Should().BeEmpty();
        r.Findings.Should().Contain(f => f.Code == "date_format_invalid" && f.Severity == WpsFindingSeverity.Warning
                                         && f.Basis == WpsRuleBasis.Unverified);
        r.Header!.ValueDate.Should().BeNull();
    }

    [Fact]
    public void A_valid_YYYYMMDD_value_date_is_parsed()
    {
        var r = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(1)));
        r.Header!.ValueDate.Should().Be(new DateOnly(2026, 9, 30));
    }

    // ── Amounts and the wage breakdown ───────────────────────────────────────

    [Fact]
    public void A_missing_wage_breakdown_field_blocks_that_row()
    {
        var rows = WpsReturnedFileOracle.Rows(2);
        var r = Read(WpsReturnedFileOracle.Build(rows, rowOverride: x =>
            $"[32B-AMT]\t{WpsReturnedFileOracle.Amount(x.Net)}\t[59-ACC]\t{x.Iban}\t[59-NAME]\t{x.Name}\t[MOL-ID]\t{x.MolId}\t[MOL-BAS]\t{WpsReturnedFileOracle.Amount(x.Basic)}\t[MOL-OEA]\t{WpsReturnedFileOracle.Amount(x.Other)}\t[MOL-DED]\t{WpsReturnedFileOracle.Amount(x.Deductions)}"));

        ErrorCodes(r).Should().Contain("wage_breakdown_missing");
        r.Findings.First(f => f.Code == "wage_breakdown_missing").Message.Should().Contain("MOL-HAL");
        r.Rows.Should().OnlyContain(x => x.Blocked);
    }

    [Fact]
    public void Net_that_is_not_basic_plus_housing_plus_other_minus_deductions_blocks_the_row()
    {
        var rows = WpsReturnedFileOracle.Rows(1);
        var r = Read(WpsReturnedFileOracle.Build(rows, rowOverride: x =>
            $"[32B-AMT]\t{WpsReturnedFileOracle.Amount(x.Net + 1m)}\t[59-ACC]\t{x.Iban}\t[59-NAME]\t{x.Name}\t[MOL-ID]\t{x.MolId}\t[MOL-BAS]\t{WpsReturnedFileOracle.Amount(x.Basic)}\t[MOL-HAL]\t{WpsReturnedFileOracle.Amount(x.Housing)}\t[MOL-OEA]\t{WpsReturnedFileOracle.Amount(x.Other)}\t[MOL-DED]\t{WpsReturnedFileOracle.Amount(x.Deductions)}"));
        ErrorCodes(r).Should().Contain("net_identity_failed");
        r.HasFileLevelError.Should().BeTrue("a net that does not follow from its breakdown is a sign of tampering");
    }

    [Fact]
    public void A_point_decimal_separator_is_refused_because_the_spec_uses_a_comma()
    {
        var rows = WpsReturnedFileOracle.Rows(1);
        var content = WpsReturnedFileOracle.Build(rows).Replace(
            $"[MOL-BAS]\t{WpsReturnedFileOracle.Amount(rows[0].Basic)}", $"[MOL-BAS]\t{rows[0].Basic:0.00}");
        ErrorCodes(Read(content)).Should().Contain("amount_point_decimal");
    }

    [Fact]
    public void More_than_two_decimals_is_refused()
    {
        var rows = WpsReturnedFileOracle.Rows(1);
        var content = WpsReturnedFileOracle.Build(rows).Replace(
            $"[MOL-BAS]\t{WpsReturnedFileOracle.Amount(rows[0].Basic)}", $"[MOL-BAS]\t{rows[0].Basic:0},123");
        ErrorCodes(Read(content)).Should().Contain("amount_precision");
    }

    [Fact]
    public void A_zero_or_negative_net_is_refused()
    {
        var rows = new[] { WpsReturnedFileOracle.Rows(1)[0] with { Basic = 100m, Housing = 0m, Other = 0m, Deductions = 100m } };
        ErrorCodes(Read(WpsReturnedFileOracle.Build(rows))).Should().Contain("net_not_positive");
    }

    [Fact]
    public void An_invalid_iban_or_mol_id_blocks_the_row_and_the_message_never_repeats_them()
    {
        var rows = WpsReturnedFileOracle.Rows(2).ToList();
        rows[0] = rows[0] with { Iban = rows[0].Iban[..23] + "9" };        // bad checksum
        rows[1] = rows[1] with { MolId = "12345" };                       // not 10 digits
        var r = Read(WpsReturnedFileOracle.Build(rows));

        ErrorCodes(r).Should().Contain("iban_invalid").And.Contain("mol_id_invalid");
        r.Findings.Should().OnlyContain(f => !f.Message.Contains(rows[0].Iban) && !f.Message.Contains("12345")
                                             && !f.MessageAr.Contains(rows[0].Iban));
    }

    // ── Encoding and line endings ────────────────────────────────────────────

    [Theory]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    [InlineData("\r", false)]
    public void A_BOM_and_CRLF_or_CR_line_endings_read_exactly_like_plain_LF(string newline, bool bom)
    {
        var rows = WpsReturnedFileOracle.Rows(4);
        var baseline = Read(WpsReturnedFileOracle.Build(rows));
        var r = Read(WpsReturnedFileOracle.Build(rows, newline: newline, bom: bom));

        ErrorCodes(r).Should().BeEmpty();
        r.Header.Should().BeEquivalentTo(baseline.Header);
        r.Rows.Should().BeEquivalentTo(baseline.Rows);
    }

    [Fact]
    public void A_bank_signature_block_or_trailing_text_is_ignored_with_a_warning_and_never_altered()
    {
        var rows = WpsReturnedFileOracle.Rows(2);
        var content = WpsReturnedFileOracle.Build(rows, trailerLines: new[] { "-----BEGIN SIGNATURE-----", "QUJDRA==", "-----END SIGNATURE-----" });
        var copy = new string(content.AsSpan());

        var r = Read(content);

        ErrorCodes(r).Should().BeEmpty();
        WarningCodes(r).Should().Contain("unrecognised_line");
        content.Should().Be(copy, "the reader never modifies the bank's file");
    }

    [Fact]
    public void An_empty_file_or_one_with_no_tagged_lines_is_refused()
    {
        ErrorCodes(Read("")).Should().Contain("file_empty");
        ErrorCodes(Read("hello\nworld\n")).Should().Contain("no_tagged_lines");
    }

    [Fact]
    public void A_header_without_any_employee_row_is_refused()
    {
        ErrorCodes(Read(WpsReturnedFileOracle.Build(new List<SynthRow>()))).Should().Contain("no_rows");
    }

    [Fact]
    public void A_header_tag_repeated_after_the_first_employee_row_blocks_the_file()
    {
        var content = WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(2)) + "[FILE-REF]\tOTHER\n";
        ErrorCodes(Read(content)).Should().Contain("header_after_rows");
    }

    [Fact]
    public void A_control_character_in_a_row_blocks_it()
    {
        var rows = WpsReturnedFileOracle.Rows(1, arabicName: "Ali\u0007Bob");
        ErrorCodes(Read(WpsReturnedFileOracle.Build(rows))).Should().Contain("control_character");
    }

    [Fact]
    public void A_tag_this_reader_does_not_know_is_ignored_with_a_warning()
    {
        var rows = WpsReturnedFileOracle.Rows(1);
        var content = WpsReturnedFileOracle.Build(rows).TrimEnd('\n') + "\t[XYZ-NEW]\tvalue\n";
        var r = Read(content);
        ErrorCodes(r).Should().BeEmpty();
        WarningCodes(r).Should().Contain("unknown_tag");
    }

    // ── Rule catalogue: honesty about what is proven ─────────────────────────

    [Fact]
    public void Every_rule_is_labelled_verified_or_unverified_with_a_source_and_both_kinds_exist()
    {
        var rules = WpsReturnedFileValidator.Rules;
        rules.Should().NotBeEmpty();
        rules.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Source) && !string.IsNullOrWhiteSpace(r.Code));
        rules.Select(r => r.Code).Should().OnlyHaveUniqueItems();
        rules.Should().Contain(r => r.Basis == WpsRuleBasis.VerifiedFromRepoFact);
        rules.Should().Contain(r => r.Basis == WpsRuleBasis.Unverified);

        RuleBasis("header_total_mismatch").Should().Be(WpsRuleBasis.Unverified);
        foreach (var code in new[] { "header_total_unverifiable", "stray_value", "duplicate_tag", "mixed_line", "header_tag_duplicated",
                                     "header_after_rows", "control_character", "establishment_not_checked", "row_status" })
            RuleBasis(code).Should().Be(WpsRuleBasis.Unverified, $"{code} is not proven by the file it used to cite");
        RuleBasis("net_identity_failed").Should().Be(WpsRuleBasis.VerifiedFromRepoFact);
        RuleBasis("wage_breakdown_missing").Should().Be(WpsRuleBasis.VerifiedFromRepoFact);
        RuleBasis("currency_not_sar").Should().Be(WpsRuleBasis.VerifiedFromRepoFact);
        RuleBasis("name_too_long").Should().Be(WpsRuleBasis.VerifiedFromRepoFact);
        RuleBasis("date_format_invalid").Should().Be(WpsRuleBasis.Unverified);
        RuleBasis("file_rejected").Should().Be(WpsRuleBasis.Unverified);
        RuleBasis("name_contains_comma").Should().Be(WpsRuleBasis.Unverified);
        RuleBasis("line_layout").Should().Be(WpsRuleBasis.Unverified);
    }

    [Fact]
    public void Exactly_these_unverified_rules_refuse_and_every_other_unverified_rule_only_warns()
    {
        WpsReturnedFileValidator.FailClosedUnverified.Should().Equal(
            "control_character", "duplicate_tag", "file_date_after_batch_window", "file_date_before_batch", "file_date_missing", "file_ref_already_imported",
            // Deliberate (final gate P3-2): the FILE-REF is the ledger key, so one that is not plain letters and digits refuses the file.
            "file_reference_unreadable",
            "file_refused_account_mismatch", "file_rejected", "file_too_large", "file_total_exceeds_batch", "header_after_rows",
            "header_tag_duplicated", "header_total_mismatch", "header_total_unverifiable", "line_too_long", "mixed_line",
            "no_line_matches_batch", "status_too_long", "status_unsafe_characters", "stray_value", "too_many_findings", "too_many_rows", "trn_ref_too_long");
        foreach (var rule in WpsReturnedFileValidator.Rules.Where(r => r.Basis == WpsRuleBasis.Unverified))
            rule.Severity.Should().Be(WpsReturnedFileValidator.FailClosedUnverified.Contains(rule.Code) ? WpsFindingSeverity.Error : WpsFindingSeverity.Warning);
    }

    [Fact]
    public void No_arabic_message_contains_a_bracketed_tag_or_the_non_glossary_terms()
    {
        foreach (var code in WpsReturnedFileValidator.MessageCodes)
        {
            var (_, ar) = WpsReturnedFileValidator.MessageFor(code, "MOL-ID", "MOL-ID");
            ar.Should().NotContain("[", $"{code}: a Latin tag in an Arabic sentence is isolated, not bracketed");
            ar.Should().NotContain("كشف الرواتب").And.NotContain("الإيرادات").And.NotContain("رمز موظف").And.NotContain("علامات الجدولة");
        }
    }

    private static WpsRuleBasis RuleBasis(string code) => WpsReturnedFileValidator.Rules.Single(r => r.Code == code).Basis;

    [Fact]
    public void Every_finding_code_the_reader_can_emit_has_an_english_and_an_arabic_message()
    {
        var arabic = new Regex("[\u0600-\u06FF]");
        WpsReturnedFileValidator.MessageCodes.Should().NotBeEmpty();
        foreach (var code in WpsReturnedFileValidator.MessageCodes)
        {
            var (en, ar) = WpsReturnedFileValidator.MessageFor(code);
            en.Should().NotBeNullOrWhiteSpace(code);
            arabic.IsMatch(ar).Should().BeTrue($"{code} needs an Arabic message");
        }
    }

    [Fact]
    public void Findings_carry_both_languages()
    {
        var r = Read(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(2),
            new WpsReturnedFileOracle.Header(Currency: "AED")));
        r.Findings.Should().OnlyContain(f => Regex.IsMatch(f.MessageAr, "[\u0600-\u06FF]") && f.Message.Length > 0);
    }
}
