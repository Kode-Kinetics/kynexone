using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Tests.WpsReturnedFile;

/// <summary>
/// Final gate security P3-1, P3-2 and P3-10 on the returned-file reader: a stored transaction reference never keeps a leading
/// formula character, even behind a space or a non-breaking space; the FILE-REF key is trimmed and upper-cased and must be
/// letters and digits, and a reference hiding format, bidi or zero-width characters refuses the file; the public rule list cites
/// no internal notes. Synthetic data.
/// </summary>
public class WpsReturnedFileInputHygieneTests
{
    private const string Est = "7-1234567";

    // ── P3-1: CleanStored trims first, then strips formula characters until none lead ──

    [Theory]
    [InlineData(" =HYPERLINK(\"x\")", "HYPERLINK(\"x\")")]          // a leading space
    [InlineData("\u00A0=HYPERLINK(\"x\")", "HYPERLINK(\"x\")")]     // a leading non-breaking space
    [InlineData("= =+-@cmd", "cmd")]
    [InlineData("   @SUM(1)", "SUM(1)")]
    [InlineData(" BNK-REF-1 ", "BNK-REF-1")]
    public void AStoredReference_NeverLeadsWithAFormulaCharacter(string raw, string expected) =>
        WpsReturnedFileValidator.CleanStored(raw).Should().Be(expected);

    [Theory]
    [InlineData(" ")]
    [InlineData("\u00A0=")]
    [InlineData("=+-@")]
    public void AReferenceOfOnlySpacesAndFormulaCharacters_IsNothing(string raw) =>
        WpsReturnedFileValidator.CleanStored(raw).Should().BeNull();

    // ── P3-2: the FILE-REF key ───────────────────────────────────────────────

    private static WpsReturnedFileReadResult Read(string fileRef)
    {
        var rows = WpsReturnedFileOracle.Rows(1).Select(r => r with { Status = "PAID" }).ToList();
        return WpsReturnedFileValidator.Read(WpsReturnedFileOracle.Build(rows, new WpsReturnedFileOracle.Header(FileRef: fileRef)),
            new WpsReturnedFileExpectation(Est, FileRefScope: "co"));
    }

    [Theory]
    [InlineData("wps2026090001", "co|WPS2026090001")]
    [InlineData("WPS2026090001", "co|WPS2026090001")]
    [InlineData("Wps2026090001", "co|WPS2026090001")]
    public void TheFileRefKey_IsUpperCased(string fileRef, string key)
    {
        var read = Read(fileRef);
        read.HasFileLevelError.Should().BeFalse(string.Join("; ", read.Findings.Select(f => f.Code)));
        read.FileRefKey.Should().Be(key);
    }

    [Theory]
    [InlineData("WPS\u200B2026090001")]   // zero-width space
    [InlineData("WPS2026\u202E090001")]   // right-to-left override
    [InlineData("\u2066WPS2026090001")]   // left-to-right isolate
    [InlineData("WPS2026\u00AD090001")]   // soft hyphen (a format character)
    [InlineData("WPS-2026/0901")]         // not letters and digits: cannot be a key
    public void AFileRefThatIsNotPlainLettersAndDigits_RefusesTheFile(string fileRef)
    {
        var read = Read(fileRef);

        read.HasFileLevelError.Should().BeTrue();
        read.FileRefKey.Should().BeNull();
        read.Findings.Should().Contain(f => f.Code == "file_reference_unreadable" && f.Severity == WpsFindingSeverity.Error);
    }

    [Fact]
    public void TheUnreadableFileRefRule_HasEnglishAndArabic_AndIsAnErrorInTheRuleList()
    {
        var (en, ar) = WpsReturnedFileValidator.MessageFor("file_reference_unreadable");
        en.Should().Contain("[FILE-REF]").And.Contain("Nothing was applied");
        ar.Should().MatchRegex("[\u0600-\u06FF]");
        WpsReturnedFileValidator.Rules.Single(r => r.Code == "file_reference_unreadable").Severity.Should().Be(WpsFindingSeverity.Error);
    }

    // ── P3-10: the public rule list cites no internal notes ──────────────────

    [Fact]
    public void ThePublicRuleList_CitesNoInternalNotes() =>
        WpsReturnedFileValidator.Rules.Should().NotContain(r => r.Source.Contains("demo-notes", StringComparison.OrdinalIgnoreCase));
}
