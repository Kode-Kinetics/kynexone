using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Tests.WpsReturnedFile;

/// <summary>
/// Every guard that refuses a file AS A WHOLE has its own test, asserting that the reader flags a file-level error and that
/// the parser then applies nothing (no rows). Each case is built so that only the guard under test can fire. Synthetic data only.
/// </summary>
public class WpsReturnedFileRefusalTests
{
    private const string Est = "7-1234567";
    private static readonly DateOnly BatchCreated = new(2026, 9, 20);
    private const string Scope = "CO1";

    internal sealed record Case(
        string Code, string Content, string? Establishment = Est, IReadOnlyList<SynthRow>? ExpectedRows = null,
        decimal? BatchTotal = null, bool ImportedRef = false, bool ParserLevel = false);

    // Every line carries a PAID status, so if a refusal guard were removed the parser WOULD hand rows to the import.
    private static readonly IReadOnlyList<SynthRow> Rows3 = WpsReturnedFileOracle.Rows(3).Select(r => r with { Status = "PAID" }).ToList();

    private static string Build(Func<WpsReturnedFileOracle.Header, WpsReturnedFileOracle.Header>? header = null, IReadOnlyList<SynthRow>? rows = null,
        string[]? omit = null, Func<SynthRow, string>? rowOverride = null) =>
        WpsReturnedFileOracle.Build(rows ?? Rows3, (header ?? (h => h))(new WpsReturnedFileOracle.Header()), omitHeaderTags: omit, rowOverride: rowOverride);

    private static string RowLine(SynthRow x, string net, string? extra = null) =>
        $"[32B-AMT]\t{net}\t[59-ACC]\t{x.Iban}\t[59-NAME]\t{x.Name}\t[MOL-ID]\t{x.MolId}\t[MOL-BAS]\t{WpsReturnedFileOracle.Amount(x.Basic)}\t[MOL-HAL]\t{WpsReturnedFileOracle.Amount(x.Housing)}\t[MOL-OEA]\t{WpsReturnedFileOracle.Amount(x.Other)}\t[MOL-DED]\t{WpsReturnedFileOracle.Amount(x.Deductions)}{extra}";

    public static IEnumerable<object[]> Names() => All.Keys.Select(k => new object[] { k });

    private static readonly IReadOnlyDictionary<string, Case> All = BuildCases();

    private static IReadOnlyDictionary<string, Case> BuildCases()
    {
        var other = WpsReturnedFileOracle.Rows(6).Skip(3).ToList();   // a different set of employees
        var cases = new List<(string Name, Case Case)>
        {
            ("file_rejected", new("file_rejected", Build(h => h with { FileRejectCode = "E017" }))),
            ("currency_not_sar", new("currency_not_sar", Build(h => h with { Currency = "AED" }))),
            ("file_reference_missing", new("file_reference_missing", Build(h => h with { FileRef = "" }))),
            ("file_reference_too_long", new("file_reference_too_long", Build(h => h with { FileRef = "ABCDEFGHIJKLMNOPQ" }))),
            ("header_after_rows", new("header_after_rows", Build() + "[FILE-REF]\tOTHER\n")),
            ("header_tag_duplicated", new("header_tag_duplicated", Build().Replace("[DEST-ID]\tXXXX\n", "[DEST-ID]\tXXXX\n[DEST-ID]\tOTHR\n"))),
            ("header_total_invalid", new("header_total_invalid", System.Text.RegularExpressions.Regex.Replace(Build(), @"\[32A-AMT\]\t[0-9,]+", "[32A-AMT]\tabc"))),
            ("header_total_unverifiable", new("header_total_unverifiable", 
                // only the FIRST line lacks its [32B-AMT]; the other two are clean and paid, so they would be applied if the refusal were lost
                WpsReturnedFileOracle.Build(Rows3, rowOverride: x => x.MolId == Rows3[0].MolId
                    ? RowLine(x, "", "\t[TRN-STATUS]\tPAID").Replace("[32B-AMT]\t\t", "")
                    : RowLine(x, WpsReturnedFileOracle.Amount(x.Net), "\t[TRN-STATUS]\tPAID")))),
            ("mol_establishment_id_invalid", new("mol_establishment_id_invalid", Build(h => h with { MolEstablishmentId = "ABC-DEF" }))),
            ("mol_establishment_id_placeholder", new("mol_establishment_id_placeholder", Build(h => h with { MolEstablishmentId = "0-0000000" }))),
            ("stray_value", new("stray_value", Build().Replace("[DEST-ID]\tXXXX\n", "[DEST-ID]\tXXXX\tEXTRA\n"))),
            ("control_character", new("control_character", Build().Replace("[DEST-ID]\tXXXX\n", "[DEST-ID]\tRJ\u0007HI\n"))),
            ("establishment_mismatch", new("establishment_mismatch", Build(h => h with { MolEstablishmentId = "7-7654321" }))),
            ("establishment_not_configured", new("establishment_not_configured", Build(), Establishment: null)),
            ("no_rows", new("no_rows", Build(rows: new List<SynthRow>()))),
            ("no_tagged_lines", new("no_tagged_lines", "hello\nworld\n")),
            ("file_empty", new("file_empty", "")),
            ("header_tag_missing", new("header_tag_missing", Build(omit: new[] { "DEST-ID" }))),
            ("duplicate_mol_id", new("duplicate_mol_id", WpsReturnedFileOracle.Build(Rows3.Append(Rows3[0] with { Iban = WpsReturnedFileOracle.SaIban(80, 999_999_999_999L) }).ToList()))),
            ("duplicate_iban", new("duplicate_iban", WpsReturnedFileOracle.Build(new[] { Rows3[0], Rows3[1], Rows3[2] with { Iban = Rows3[0].Iban } }))),
            ("net_identity_failed", new("net_identity_failed",
                // only the FIRST line's net is off by 1,00; the header total allows for it, so nothing else is wrong with the file
                WpsReturnedFileOracle.Build(Rows3, declaredTotal: Rows3.Sum(r => r.Net) + 1m, rowOverride: x => RowLine(x,
                    WpsReturnedFileOracle.Amount(x.MolId == Rows3[0].MolId ? x.Net + 1m : x.Net), "\t[TRN-STATUS]\tPAID")),
                BatchTotal: 1_000_000m)),
            ("file_too_large", new("file_too_large", new string('x', WpsReturnedFileValidator.MaxContentChars + 1))),
            ("line_too_long", new("line_too_long", "[DEST-ID]\t" + new string('x', WpsReturnedFileValidator.MaxLineChars + 1) + "\n")),
            ("too_many_rows", new("too_many_rows", WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(150).Select(r => r with { Status = "PAID" }).ToList()), ExpectedRows: Rows3, BatchTotal: 100_000_000m)),
            ("too_many_findings", new("too_many_findings", string.Concat(Enumerable.Repeat("[MOL-ID]\t\n", 400)) + "[DEST-ID]\tX\n")),
            ("file_date_missing", new("file_date_missing", Build(omit: new[] { "32A-VAL" }))),
            ("file_date_before_batch", new("file_date_before_batch", Build(h => h with { ValueDate = "20250101" }))),
            ("file_total_exceeds_batch", new("file_total_exceeds_batch", Build(), BatchTotal: Rows3.Sum(r => r.Net) - 1m)),
            ("file_ref_already_imported", new("file_ref_already_imported", Build(), ImportedRef: true)),
            ("no_line_matches_batch", new("no_line_matches_batch", Build(), ExpectedRows: other, ParserLevel: true)),
            ("file_refused_account_mismatch", new("file_refused_account_mismatch", Build(),
                ExpectedRows: new[] { Rows3[0] with { Iban = WpsReturnedFileOracle.SaIban(10, 424_242_424_242L) }, Rows3[1], Rows3[2] }, ParserLevel: true)),
            ("header_total_mismatch", new("header_total_mismatch", WpsReturnedFileOracle.Build(Rows3, declaredTotal: Rows3.Sum(r => r.Net) + 0.01m))),
        };
        return cases.ToDictionary(c => c.Name, c => c.Case);
    }

    private static BankConfirmationBatchContext Ctx(Case c)
    {
        var rows = c.ExpectedRows ?? Rows3;
        var expected = rows.Select((r, i) => new WpsExpectedLine(i + 1, $"EMP{i + 1:D4}", r.MolId, r.Iban, r.Net)).ToList();
        var imported = c.ImportedRef ? new HashSet<string> { Scope + "|WPS2026090001" } : new HashSet<string>();
        return new BankConfirmationBatchContext(c.Establishment, expected, BatchCreated, c.BatchTotal ?? expected.Sum(e => e.Amount), Scope, imported);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_file_level_fault_refuses_the_whole_file_in_the_reader(string name)
    {
        var c = All[name];
        if (c.ParserLevel) return;     // these two are decided after the lines are matched to the batch (see the next test)
        var ctx = Ctx(c);
        var r = WpsReturnedFileValidator.Read(c.Content, new WpsReturnedFileExpectation(
            ctx.MolEstablishmentId, true, ctx.BatchCreatedOn, ctx.BatchTotal, ctx.Expected.Count, ctx.FileRefScope, ctx.ImportedFileRefKeys));

        r.HasFileLevelError.Should().BeTrue($"{name} must refuse the whole file");
        r.Findings.Should().Contain(f => f.Code == c.Code && f.Severity == WpsFindingSeverity.Error, name);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_file_level_fault_means_the_parser_hands_the_import_no_rows(string name)
    {
        var c = All[name];
        var parse = new WpsReturnedFileBankConfirmationParser(statusColumnVerified: true).Parse(c.Content, Ctx(c));

        parse.Rows.Should().BeEmpty($"{name} must apply nothing");
        parse.Errors.Should().Contain(e => e.Error == c.Code, name);
        parse.ImportKey.Should().BeNull("a refused file must not burn its FILE-REF");
        parse.Errors.Where(e => e.Error == c.Code).Should().OnlyContain(e => !string.IsNullOrEmpty(e.DetailAr));
    }

    // ── input bounds ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("[MOL-ID]\t\n")]
    [InlineData("[DEST-ID]\tX\n")]
    [InlineData("[32B-AMT]\n")]
    public void A_pathological_file_is_refused_quickly_with_a_bounded_list_of_findings(string line)
    {
        var content = new StringBuilder().Insert(0, line, 150_000).ToString();   // about 1.5 MB, 150k lines, all faulty
        var sw = Stopwatch.StartNew();
        var r = WpsReturnedFileValidator.Read(content, new WpsReturnedFileExpectation(Est));
        sw.Stop();

        r.HasFileLevelError.Should().BeTrue();
        r.Findings.Count.Should().BeLessThanOrEqualTo(WpsReturnedFileValidator.MaxFindings + 1);
        r.Rows.Count.Should().BeLessThanOrEqualTo(WpsReturnedFileValidator.MaxRows);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void The_parser_lists_at_most_200_faults_and_summarises_the_rest()
    {
        var all = WpsReturnedFileOracle.Rows(3400);
        var expectedRows = all.Take(3000).ToList();
        // one line that matches the batch and 400 employees the batch does not have
        var file = WpsReturnedFileOracle.Build(new[] { all[0] }.Concat(all.Skip(3000)).Select(r => r with { Status = "PAID" }).ToList());
        var ctx = new BankConfirmationBatchContext(Est,
            expectedRows.Select((r, i) => new WpsExpectedLine(i + 1, $"EMP{i + 1:D4}", r.MolId, r.Iban, r.Net)).ToList(),
            BatchCreated, expectedRows.Sum(r => r.Net), Scope, new HashSet<string>());

        var parse = new WpsReturnedFileBankConfirmationParser(statusColumnVerified: true).Parse(file, ctx);

        parse.Errors.Count.Should().Be(202, "200 listed + one summary + the standing notice");
        parse.Errors.Should().Contain(e => e.Error == "more_errors");
        parse.Errors.Should().Contain(e => e.Error == "scope_notice");
    }

    // ── canonical form (replay guard) ────────────────────────────────────────

    private static string Canon(string s) => WpsReturnedFileValidator.CanonicalForm(s);

    [Fact]
    public void A_byte_level_tweak_of_the_same_file_has_the_same_canonical_form()
    {
        var rows = WpsReturnedFileOracle.Rows(4);
        var baseline = Canon(WpsReturnedFileOracle.Build(rows));

        Canon(WpsReturnedFileOracle.Build(rows, newline: "\r\n", bom: true)).Should().Be(baseline);
        Canon(WpsReturnedFileOracle.Build(rows) + "\n\n   \n").Should().Be(baseline);
        Canon(WpsReturnedFileOracle.Build(rows, headerOnOneLine: true)).Should().Be(baseline);
        Canon(WpsReturnedFileOracle.Build(rows).Replace("\n", "  \n")).Should().Be(baseline, "trailing whitespace");
        Canon(WpsReturnedFileOracle.Build(rows, trailerLines: new[] { "-----BEGIN SIGNATURE-----", "QUJD" })).Should().Be(baseline, "signature lines are not content");
        Canon(WpsReturnedFileOracle.Build(rows.Reverse().ToList())).Should().Be(baseline, "line order");
        // tag order inside a line
        var reordered = WpsReturnedFileOracle.Build(rows, rowOverride: x =>
            $"[MOL-ID]\t{x.MolId}\t[32B-AMT]\t{WpsReturnedFileOracle.Amount(x.Net)}\t[59-ACC]\t{x.Iban}\t[59-NAME]\t{x.Name}\t[MOL-BAS]\t{WpsReturnedFileOracle.Amount(x.Basic)}\t[MOL-HAL]\t{WpsReturnedFileOracle.Amount(x.Housing)}\t[MOL-OEA]\t{WpsReturnedFileOracle.Amount(x.Other)}\t[MOL-DED]\t{WpsReturnedFileOracle.Amount(x.Deductions)}");
        Canon(reordered).Should().Be(baseline, "tag order");
    }

    [Fact]
    public void A_change_of_content_changes_the_canonical_form()
    {
        var rows = WpsReturnedFileOracle.Rows(4);
        var baseline = Canon(WpsReturnedFileOracle.Build(rows));
        Canon(WpsReturnedFileOracle.Build(rows.Take(3).ToList())).Should().NotBe(baseline);
        Canon(WpsReturnedFileOracle.Build(rows.Select((r, i) => i == 0 ? r with { Name = "Changed" } : r).ToList())).Should().NotBe(baseline);
        Canon(WpsReturnedFileOracle.Build(rows, new WpsReturnedFileOracle.Header(FileRef: "OTHERREF"))).Should().NotBe(baseline);
    }

    // ── stored text ──────────────────────────────────────────────────────────

    [Fact]
    public void Bidi_zero_width_and_formula_characters_are_removed_from_the_stored_reference_and_status()
    {
        var rows = WpsReturnedFileOracle.Rows(1).Select(r => r with { TrnRef = "\u202E=cmd|\u200Bref", Status = "\u2066PA\u200BID\u2069" }).ToList();
        var r = WpsReturnedFileValidator.Read(WpsReturnedFileOracle.Build(rows), new WpsReturnedFileExpectation(Est));

        r.Rows[0].TrnRef.Should().Be("cmd|ref");
        r.Rows[0].TrnStatus.Should().Be("PAID");
    }
}
