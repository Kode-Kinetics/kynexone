using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>F9 - one CSV writer with one formula-injection guard, and every human download carries a UTF-8 BOM.</summary>
public sealed class CsvDownloadTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+966500000000", "+966500000000")]   // a plain number is never a formula
    [InlineData("-125.50", "-125.50")]
    [InlineData("-5", "-5")]
    [InlineData("-", "'-")]
    [InlineData("-5+A1", "'-5+A1")]
    [InlineData("+966 50 000", "'+966 50 000")]
    [InlineData("-1.2.3", "'-1.2.3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\t=cmd", "'\t=cmd")]
    [InlineData("\r=cmd", "'\r=cmd")]
    [InlineData("   =cmd", "'   =cmd")]          // Excel evaluates it after leading spaces
    [InlineData("  @x", "'  @x")]
    [InlineData("\t", "'\t")]
    [InlineData("plain", "plain")]
    [InlineData("O'Brien", "O'Brien")]
    [InlineData("a=b", "a=b")]
    [InlineData("", "")]
    public void Guard_NeutralisesEveryFormulaLead(string input, string expected) => Csv.Guard(input).Should().Be(expected);

    [Fact]
    public void Guard_IsAppliedByEscapeBuildAndQuote_AndNullIsEmpty()
    {
        Csv.Guard(null).Should().Be("");
        Csv.Escape("=A1").Should().Be("'=A1");
        Csv.Quote("=A1").Should().Be("\"'=A1\"");
        Csv.Quote("say \"hi\"").Should().Be("\"say \"\"hi\"\"\"");
        Csv.Build(new[] { "h" }, new[] { (IReadOnlyList<object?>)new object?[] { "  =x" } }).Should().Be("h\n'  =x\n");
    }

    [Theory]
    [InlineData("=1+1")] [InlineData("+966500000000")] [InlineData("  =cmd")] [InlineData("'Abdullah")] [InlineData("O'Brien")]
    public void ExportThenImport_RoundTripsEveryValue(string value)
    {
        var csv = Csv.Build(new[] { "Name" }, new[] { (IReadOnlyList<object?>)new object?[] { value } });
        Csv.Parse(csv).Single()["Name"].Should().Be(value);
    }

    [Fact]
    public void Download_StartsWithABom_AndDeclaresUtf8_AndKeepsArabic()
    {
        var result = CsvDownload.Result("Name\nمحمد عبدالله\n", "people.csv");
        result.ContentType.Should().Be("text/csv; charset=utf-8");
        result.FileDownloadName.Should().Be("people.csv");
        result.FileContents.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        new UTF8Encoding(false).GetString(result.FileContents, 3, result.FileContents.Length - 3).Should().Contain("محمد عبدالله");
        // The raw bytes of the Arabic text are UTF-8, not the ANSI code page Excel would otherwise assume.
        result.FileContents.Should().Contain(new byte[] { 0xD9, 0x85 });
    }

    [Fact]
    public void Download_WithoutAFileName_IsInline_ForActionsThatSetContentDispositionThemselves() =>
        CsvDownload.Result("a\n").FileDownloadName.Should().BeNullOrEmpty();

    [Fact]
    public void ToDownloadBytes_HasExactlyOneBom_AndEmptyIsJustTheBom()
    {
        Csv.ToDownloadBytes("").Should().Equal(0xEF, 0xBB, 0xBF);
        Csv.ToDownloadBytes("x").Should().Equal(0xEF, 0xBB, 0xBF, (byte)'x');
        // Parse still reads a BOM'd file (the exports round-trip into the importers).
        Csv.Parse(Encoding.UTF8.GetString(Csv.ToDownloadBytes("A,B\n1,2\n"))).Single()["A"].Should().Be("1");
    }

    // ── ratchet: nobody gets around the shared writer ─────────────────────────────────────────────────
    private static string ApiDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Zayra.Api", "Controllers"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Zayra.Api");
    }

    [Fact]
    public void NoControllerReturnsCsvOtherThanThroughCsvDownload()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(ApiDir(), "Controllers"), "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (!l.Contains("\"text/csv\"")) continue;
                if (l.Contains("ReportTabulator.ToCsv")) continue;          // the tabulator writes its own BOM (Csv.Quote guard)
                if (l.TrimStart().StartsWith("//") || l.TrimStart().StartsWith("///")) continue;
                offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {l.Trim()}");
            }
        }
        offenders.Should().BeEmpty("a CSV a person opens must go through CsvDownload.Result (UTF-8 BOM + the shared formula guard)");
    }

    [Fact]
    public void NoCodeHandRollsItsOwnFormulaGuard()
    {
        // The guard characters appear only in Csv.cs; a copy elsewhere drifts (the loan file's copy missed tab, CR and "@").
        var rx = new Regex(@"StartsWith\('='\)|""=\+-@""\.Contains|FormulaTriggers|\.TrimStart\(\)\.StartsWith\('-'\)", RegexOptions.Compiled);
        var offenders = Directory.EnumerateFiles(ApiDir(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != "Csv.cs")
            .Where(f => rx.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName).ToList();
        offenders.Should().BeEmpty("use Csv.Guard / Csv.Escape / Csv.Quote");
    }

    [Fact]
    public void ReportTabulator_UsesTheSharedGuard_IncludingTabsAndSpaces()
    {
        var table = new Zayra.Api.Infrastructure.Reports.ReportTable("T", new[] { "H" },
            new[] { (IReadOnlyList<string>)new[] { "\t=1" }, new[] { "  @x" } });
        var bytes = Zayra.Api.Infrastructure.Reports.ReportTabulator.ToCsv(new[] { table });
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var text = Encoding.UTF8.GetString(bytes);
        text.Should().Contain("\"'\t=1\"").And.Contain("\"'  @x\"");
    }
}

internal static class CsvTestHelpers
{
    /// <summary>F9: a CSV download is a UTF-8 file WITH a byte-order mark (so Excel reads Arabic) - returns the text without it.</summary>
    public static string Body(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        file.ContentType.Should().StartWith("text/csv");
        file.FileContents.Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF }, "an Excel-friendly CSV starts with a UTF-8 BOM");
        return Encoding.UTF8.GetString(file.FileContents, 3, file.FileContents.Length - 3);
    }
}
