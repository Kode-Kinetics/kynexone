using System.Text;
using System.Text.RegularExpressions;

namespace Zayra.Api.Infrastructure.Documents.Letters;

/// <summary>
/// Substitutes <c>{{token}}</c> merge fields into a letter template body.
///
/// <para>Before this class there was no merge engine anywhere in the product.
/// <c>ContractTemplate</c> had carried <c>ContentHtmlEn</c>, <c>ContentHtmlAr</c> and a
/// <c>Variables</c> CSV since the initial migration and <b>nothing substituted anything into
/// them</b> — the columns were storage, not a feature.</para>
///
/// <para><b>Fail shut.</b> A token with no value is NOT rendered as an empty string. A salary
/// certificate that reads "whose monthly salary is  " is worse than no certificate: it goes to a
/// bank, it is obviously wrong, and it costs the employee their loan appointment. Rendering
/// returns the unresolved tokens and the caller refuses the issuance.</para>
/// </summary>
public static partial class LetterTemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>Every token name that appears in <paramref name="template"/>, in order, deduplicated.</summary>
    public static IReadOnlyList<string> ExtractTokens(string? template)
    {
        if (string.IsNullOrEmpty(template)) return [];
        var seen = new List<string>();
        foreach (Match match in TokenPattern().Matches(template))
        {
            var name = match.Groups[1].Value;
            if (!seen.Contains(name, StringComparer.OrdinalIgnoreCase)) seen.Add(name);
        }
        return seen;
    }

    /// <summary>
    /// Renders <paramref name="template"/> against <paramref name="values"/>.
    /// <paramref name="unresolved"/> lists every token with no non-blank value; when it is
    /// non-empty the caller must refuse rather than issue.
    /// </summary>
    public static string Render(
        string? template,
        IReadOnlyDictionary<string, string> values,
        out IReadOnlyList<string> unresolved)
    {
        if (string.IsNullOrEmpty(template))
        {
            unresolved = [];
            return string.Empty;
        }

        var missing = new List<string>();
        var rendered = TokenPattern().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;

            if (!missing.Contains(name, StringComparer.OrdinalIgnoreCase)) missing.Add(name);
            // Leave the token intact. If a caller ignores `unresolved` the defect is visible on
            // the page as "{{basic_salary}}" rather than hiding as a plausible blank.
            return match.Value;
        });

        unresolved = missing;
        return rendered;
    }

    /// <summary>
    /// Splits a rendered body into paragraphs on blank lines, trimming each. QuestPDF gets one
    /// Text item per paragraph so spacing is the layout's job, not the template author's.
    /// </summary>
    public static IReadOnlyList<string> Paragraphs(string rendered)
    {
        if (string.IsNullOrWhiteSpace(rendered)) return [];
        var normalized = rendered.Replace("\r\n", "\n").Replace('\r', '\n');
        return [.. normalized
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => CollapseSingleNewlines(p).Trim())
            .Where(p => p.Length > 0)];
    }

    private static string CollapseSingleNewlines(string paragraph)
    {
        var builder = new StringBuilder(paragraph.Length);
        foreach (var line in paragraph.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(trimmed);
        }
        return builder.ToString();
    }
}
