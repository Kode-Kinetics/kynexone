using FluentAssertions;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Employees;

namespace Zayra.Api.Tests;

/// <summary>
/// The product's own export must survive the product's own importer.
///
/// <para>Two defects made that false. (1) <see cref="Csv.Escape"/> neutralises CSV/formula injection
/// (CWE-1236) by prefixing an apostrophe to any cell starting with <c>= + - @ \t \r</c>, and the parse
/// side never removed it: a <c>+966…</c> phone re-imported as the literal string <c>'+966…</c>, and a
/// <c>-500</c> fixed deduction re-imported as <c>'-500</c>, failed money-cell parsing, and took the
/// employee's ENTIRE salary structure down with it. (2) A UTF-8 BOM was never stripped — U+FEFF is
/// Unicode category Cf, not whitespace, so <c>Trim()</c> left it welded to the first header and every
/// file produced by Excel's "Save as CSV UTF-8" was refused outright.</para>
///
/// <para>These are property-style tests of ONE rule: over every value the exporter can emit,
/// <c>Escape</c> and the parse side are exact inverses — and only that apostrophe is removed, so a
/// value that legitimately begins with one (the Arabic transliteration <c>'Abdullah</c>) is preserved.</para>
/// </summary>
public class CsvRoundTripTests
{
    /// <summary>Every cell the exporter can emit, read back through the importer's own splitter.</summary>
    [Theory]
    // The two field values that actually broke in the employee module.
    [InlineData("+966501234567")]          // Phone / EmergencyContactPhone
    [InlineData("-500")]                   // a negative FixedDeduction
    [InlineData("-1250.75")]
    // The formula-injection payloads the guard exists for: neutralised out, restored in.
    [InlineData("=SUM(A1:A2)")]
    [InlineData("@SUM(A1)")]
    [InlineData("=cmd|'/c calc'!A1")]
    // RFC-4180 quoting, which must compose with the guard rather than fight it.
    [InlineData("Riyadh, Saudi Arabia")]
    [InlineData("she said \"hi\"")]
    [InlineData("Line one\nLine two")]
    [InlineData("=a,b")]                   // trigger AND a delimiter: guard + quoting together
    [InlineData("\"=danger\"")]            // leading quote, trigger inside the quotes
    // Ordinary values, which must be untouched.
    [InlineData("Mohammed Ali")]
    [InlineData("محمد عبدالله")]
    [InlineData("عبدالله بن عبدالعزيز")]
    [InlineData("EMP-001")]
    [InlineData("2026-09-24")]
    [InlineData("O'Brien")]                // apostrophe present but NOT leading
    // The value the conditional strip exists to protect: a genuine leading apostrophe.
    [InlineData("'Abdullah")]
    [InlineData("'Aisha bint Abu Bakr")]
    public void Escape_and_parse_are_exact_inverses(string value)
    {
        var cells = Csv.SplitRow(Csv.Escape(value));

        cells.Should().ContainSingle("the escaped value is ONE cell however it was quoted");
        cells[0].Should().Be(value);
    }

    /// <summary>
    /// A leading apostrophe is removed ONLY when the next character is a formula trigger — the exact
    /// condition under which <see cref="Csv.Escape"/> added one. An unconditional strip would silently
    /// rename every <c>'Abdullah</c> in a GCC payroll file to <c>Abdullah</c>.
    /// </summary>
    [Theory]
    [InlineData("'Abdullah", "'Abdullah")]
    [InlineData("'Aisha", "'Aisha")]
    [InlineData("'quoted note'", "'quoted note'")]
    [InlineData("''", "''")]
    [InlineData("'", "'")]
    [InlineData("'+966501234567", "+966501234567")]
    [InlineData("'-500", "-500")]
    [InlineData("'=SUM(A1)", "=SUM(A1)")]
    [InlineData("'@handle", "@handle")]
    public void Only_the_formula_guard_apostrophe_is_stripped(string onDisk, string expected) =>
        Csv.SplitRow(onDisk).Single().Should().Be(expected);

    /// <summary>
    /// Exactly ONE apostrophe is removed per parse, so a value that is exported, imported, exported and
    /// imported again is not eroded a character per lap.
    /// </summary>
    [Theory]
    [InlineData("+966501234567")]
    [InlineData("-500")]
    [InlineData("=SUM(A1:A2)")]
    public void Repeated_export_import_laps_are_stable(string value)
    {
        var once = Csv.SplitRow(Csv.Escape(value)).Single();
        var twice = Csv.SplitRow(Csv.Escape(once)).Single();
        var thrice = Csv.SplitRow(Csv.Escape(twice)).Single();

        once.Should().Be(value);
        twice.Should().Be(value);
        thrice.Should().Be(value);
    }

    /// <summary>The whole-file path: <see cref="Csv.Build"/> out, <see cref="Csv.Parse"/> back in.</summary>
    [Fact]
    public void A_built_file_reimports_to_the_values_it_was_built_from()
    {
        string[] headers = { "EmployeeCode", "FullName", "Phone", "EmergencyContactPhone", "FixedDeduction", "Address" };
        var row = new object?[] { "EMP-001", "محمد عبدالله", "+966501234567", "+973 1234 5678", "-500", "Riyadh, Saudi Arabia" };

        var parsed = Csv.Parse(Csv.Build(headers, new[] { (IReadOnlyList<object?>)row }));

        parsed.Should().ContainSingle();
        for (var i = 0; i < headers.Length; i++)
            parsed[0][headers[i]].Should().Be((string)row[i]!, $"'{headers[i]}' must survive its own export");
    }

    /// <summary>
    /// A file saved by Excel as "CSV UTF-8" carries a BOM. It must parse to the same headers, and the
    /// same rows, as the identical file without one.
    /// </summary>
    [Fact]
    public void A_bom_prefixed_file_parses_exactly_like_one_without_a_bom()
    {
        const string content = "EmployeeCode,FullName,Phone\nEMP-001,Mohammed Ali,+966501234567\n";

        var plain = Csv.Parse(content);
        var withBom = Csv.Parse("﻿" + content);

        withBom.Should().HaveCount(plain.Count);
        withBom[0].Keys.Should().BeEquivalentTo(plain[0].Keys, "the BOM is not part of the first column's name");
        withBom[0]["EmployeeCode"].Should().Be("EMP-001");
        withBom[0].Should().BeEquivalentTo(plain[0]);
    }

    /// <summary>The header row alone, through the splitter the header validator uses.</summary>
    [Fact]
    public void A_bom_prefixed_header_row_splits_to_the_same_headers()
    {
        const string header = "EmployeeCode,FullName,Phone";

        Csv.SplitRow("﻿" + header).Should().BeEquivalentTo(Csv.SplitRow(header), o => o.WithStrictOrdering());
        Csv.SplitRow("﻿" + header)[0].Should().Be("EmployeeCode");
    }

    /// <summary>
    /// The operator-facing consequence of the BOM defect: the employee header validator rejected the
    /// first column of every Excel-produced file, with the nonsense message "Column 'EmployeeCode' is not
    /// an employee import column. Did you mean 'EmployeeCode'?" — because the near-match helper folds the
    /// BOM away before comparing while the validator does not.
    /// </summary>
    [Fact]
    public void The_employee_header_validator_accepts_a_bom_prefixed_file()
    {
        const string content = "EmployeeCode,FullName,Phone\nEMP-001,Mohammed Ali,+966501234567\n";

        EmployeeCsvHeaderValidator.Validate(content).Should().BeEmpty("the control case must be clean");
        EmployeeCsvHeaderValidator.Validate("﻿" + content)
            .Should().BeEmpty("a BOM is an encoding marker, not a mistyped column name");
    }

    /// <summary>
    /// A genuine leading apostrophe survives the whole-file path too — the case an unconditional strip
    /// would have quietly corrupted across every name column in the product.
    /// </summary>
    [Fact]
    public void A_legitimate_leading_apostrophe_survives_a_whole_file_import()
    {
        var parsed = Csv.Parse("EmployeeCode,FullName\nEMP-002,'Abdullah Al-Qahtani\n");

        parsed.Should().ContainSingle();
        parsed[0]["FullName"].Should().Be("'Abdullah Al-Qahtani");
    }

    /// <summary>
    /// Known limitation, asserted so it stays known: a cell containing a newline round-trips at the CELL
    /// level (above), but <c>Csv.Parse</c> splits the file on physical newlines BEFORE honouring quotes,
    /// so a multi-line cell is REFUSED as a shape error rather than silently mis-imported. Refusal is the
    /// safe half of the behaviour and is what this file's <see cref="CsvShapeException"/> contract
    /// promises; making it parse would change line splitting for every importer and is out of scope here.
    /// </summary>
    [Fact]
    public void A_cell_containing_a_newline_is_refused_by_the_whole_file_parser_not_mis_imported()
    {
        var csv = Csv.Build(
            new[] { "EmployeeCode", "Notes" },
            new[] { (IReadOnlyList<object?>)new object?[] { "EMP-003", "Line one\nLine two" } });

        Assert.Throws<CsvShapeException>(() => Csv.Parse(csv));
    }

    /// <summary>
    /// Every mis-shaped row is named in ONE refusal (row number, its cell count, the header's), so a real file
    /// with the same unquoted "8,000" on many salary cells is fixed in one pass — and no row of it is returned.
    /// </summary>
    [Fact]
    public void Every_row_with_the_wrong_cell_count_is_named_with_both_counts_and_nothing_is_returned()
    {
        var csv = "EmployeeCode,FullName,BasicSalary\n"
                  + "E1,Fine,5000\n"
                  + "E2,Shifted,8,000\n"
                  + "E3,Fine,\"25,000\"\n"
                  + "E4,Short\n"
                  + "E5,Shifted,1,500\n";

        var ex = Assert.Throws<CsvShapeException>(() => Csv.Parse(csv));

        ex.RowNumber.Should().Be(3);
        ex.CellCount.Should().Be(4);
        ex.HeaderCount.Should().Be(3);
        ex.Mismatches.Should().Equal(new CsvShapeMismatch(3, 4), new CsvShapeMismatch(5, 2), new CsvShapeMismatch(6, 4));
        ex.Message.Should().Contain("CSV row 3 has 4 cell(s) but the header declares 3 column(s)")
            .And.Contain("Also wrong: row 5 (2), row 6 (4)");
    }
}
