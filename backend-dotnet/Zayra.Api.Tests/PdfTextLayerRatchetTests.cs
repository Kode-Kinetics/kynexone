using System.Text.RegularExpressions;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// RATCHET: every QuestPDF document in Zayra.Api sets its base text style through
/// <c>DocumentFonts.TextLayerSafe()</c> (or disables <c>FontFeatures.StandardLigatures</c> directly) in a
/// <c>page.DefaultTextStyle(...)</c>. Without it Lato's "ti" ligature has no Unicode mapping and the PDF's
/// text layer reads "Designaon". A new <c>Document.Create(...)</c> that skips it fails the build.
/// </summary>
public sealed class PdfTextLayerRatchetTests
{
    private static readonly Regex Safe = new(
        @"DefaultTextStyle\((?:(?!\bPage\(|Document\.Create\().)*?(TextLayerSafe\(|DisableFontFeature\(\s*FontFeatures\.StandardLigatures)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void EveryQuestPdfDocument_DisablesLigatures()
    {
        var root = SourceRoot();
        Assert.True(root is not null, "Zayra.Api source not found next to the test binaries: the ratchet cannot run, so it fails.");

        var bad = new List<string>();
        var documents = 0;
        foreach (var file in Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root!, file).Replace('\\', '/');
            if (rel.StartsWith("Migrations/") || rel.StartsWith("obj/") || rel.StartsWith("bin/")) continue;
            var src = File.ReadAllText(file);
            var parts = Regex.Split(src, @"(?<![A-Za-z])Document\.Create\(");
            for (var i = 1; i < parts.Length; i++)
            {
                documents++;
                // the segment up to the next document: the first DefaultTextStyle must be the safe one
                var segment = parts[i];
                var ds = segment.IndexOf("DefaultTextStyle(", StringComparison.Ordinal);
                var ok = ds >= 0 && Safe.IsMatch(segment[ds..Math.Min(segment.Length, ds + 600)]);
                if (!ok) bad.Add($"{rel}: Document.Create #{i}");
            }
        }
        Assert.True(documents >= 8, $"Expected to find the QuestPDF documents, found {documents}: the scan is broken.");
        Assert.True(bad.Count == 0,
            "QuestPDF documents without DocumentFonts.TextLayerSafe() in their page.DefaultTextStyle (ligature glyphs have no " +
            "Unicode mapping, so the PDF text layer drops letters):\n  " + string.Join("\n  ", bad));
    }

    private static string? SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
