using System.Text.RegularExpressions;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// RATCHET (CTO-GATE C4): the employer's BANK makes and signs the WPS file; KynexOne never produces a Mudad file and never
/// claims to. The tab-delimited, bracket-tagged layout is READ by <see cref="WpsReturnedFileValidator"/> and WRITTEN only by the
/// test oracle. So no production source may write a record in that layout, and no production type may be named a WPS/Mudad writer.
/// </summary>
public class WpsFileWriterRatchetTests
{
    // A known WPS field tag followed by an escaped TAB, either way round: that is how a record of the layout is written.
    private static readonly Regex TagThenTab = new(@"\[(32B-AMT|32A-AMT|32A-VAL|32A-CCY|59-ACC|59-NAME|MOL-ID|MOL-BAS|MOL-HAL|MOL-OEA|MOL-DED|MOL-ESTBID|DEST-ID|BANK-ACC|FILE-REF|TRN-STATUS|TRN-REF)\](\\t|\t)", RegexOptions.CultureInvariant);
    private static readonly Regex TabThenTag = new(@"(\\t|\t)\[(32B-AMT|32A-AMT|32A-VAL|32A-CCY|59-ACC|59-NAME|MOL-ID|MOL-BAS|MOL-HAL|MOL-OEA|MOL-DED|MOL-ESTBID|DEST-ID|BANK-ACC|FILE-REF|TRN-STATUS|TRN-REF)\]", RegexOptions.CultureInvariant);

    private static string ApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Zayra.Api.csproj"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Zayra.Api not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void No_production_source_writes_a_record_in_the_wps_tag_layout()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ApiRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}Migrations{sep}")) continue;
            var text = File.ReadAllText(file);
            if (TagThenTab.IsMatch(text) || TabThenTag.IsMatch(text)) offenders.Add(Path.GetRelativePath(ApiRoot(), file));
        }
        offenders.Should().BeEmpty("the bank writes this file; the product only reads it (see WpsConformance)");
    }

    [Fact]
    public void The_ratchet_would_catch_a_writer()
    {
        TagThenTab.IsMatch("sb.Append(\"[32B-AMT]\\t\" + amount)").Should().BeTrue();
        TabThenTag.IsMatch("sb.Append(\"\\t[59-ACC]\\t\")").Should().BeTrue();
        TagThenTab.IsMatch("\"The header field [FILE-REF] is missing\"").Should().BeFalse("prose that names a tag is not a record");
    }

    [Fact]
    public void No_production_type_is_a_wps_or_mudad_file_writer()
    {
        var names = typeof(WpsReturnedFileValidator).Assembly.GetTypes().Select(t => t.Name)
            .Where(n => Regex.IsMatch(n, "(Wps|Mudad)", RegexOptions.IgnoreCase) && Regex.IsMatch(n, "(Writer|Emitter|FileBuilder|TabFile)", RegexOptions.IgnoreCase))
            .ToList();
        names.Should().BeEmpty();
    }
}
