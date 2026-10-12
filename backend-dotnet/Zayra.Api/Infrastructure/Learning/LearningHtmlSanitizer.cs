using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Zayra.Api.Infrastructure.Learning;

/// <summary>
/// Lesson rich text, made safe on the way IN (stored sanitised, rendered as-is).
///
/// <para><b>Rebuild, never filter.</b> The output is constructed from scratch: every run of text is HTML-encoded, and
/// the only markup in the output is a tag this class itself writes from a fixed allow-list, with no attributes except
/// a validated <c>href</c> on links (and <c>rel</c>/<c>target</c> that this class adds). A tag the tokenizer does not
/// recognise — or recognises and does not allow (<c>script</c>, <c>style</c>, <c>iframe</c>, any <c>on*</c> handler,
/// <c>javascript:</c> URLs) — is dropped, and the text inside <c>script</c>/<c>style</c> is dropped with it. Because
/// nothing the author typed is ever copied into the output unencoded, a parsing mistake can only cost formatting,
/// never safety.</para>
/// </summary>
public static class LearningHtmlSanitizer
{
    public const int MaxInputLength = 200_000;

    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "strong", "b", "em", "i", "u", "ul", "ol", "li", "h2", "h3", "h4", "blockquote", "code", "pre", "a", "hr",
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase) { "br", "hr" };

    /// <summary>Elements whose CONTENT is dropped too, not just the tags.</summary>
    private static readonly HashSet<string> DropWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "noscript", "template", "svg", "math", "textarea", "select", "title", "head",
    };

    private static readonly Regex Tag = new(@"<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>[^>]*)>", RegexOptions.Compiled);
    private static readonly Regex Href = new(@"\bhref\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    public static string Sanitize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        if (input.Length > MaxInputLength) input = input[..MaxInputLength];
        input = Comment.Replace(input, string.Empty);

        var output = new StringBuilder(input.Length);
        var open = new Stack<string>();
        string? dropping = null;
        var position = 0;

        foreach (Match match in Tag.Matches(input))
        {
            if (dropping is null) AppendText(output, input[position..match.Index]);
            position = match.Index + match.Length;

            var name = match.Groups["name"].Value.ToLowerInvariant();
            var closing = match.Groups["close"].Success;

            if (dropping is not null)
            {
                if (closing && name == dropping) dropping = null;
                continue;
            }
            if (DropWithContent.Contains(name))
            {
                if (!closing && !match.Value.TrimEnd('>').TrimEnd().EndsWith('/')) dropping = name;
                continue;
            }
            if (!AllowedTags.Contains(name)) continue;

            if (closing)
            {
                if (VoidTags.Contains(name) || !open.Contains(name)) continue;
                // Close everything opened after it, so the output is always well nested.
                while (open.Count > 0)
                {
                    var top = open.Pop();
                    output.Append("</").Append(top).Append('>');
                    if (top == name) break;
                }
                continue;
            }

            if (VoidTags.Contains(name))
            {
                output.Append('<').Append(name).Append('>');
                continue;
            }

            if (name == "a")
            {
                var href = SafeHref(match.Groups["attrs"].Value);
                if (href is null)
                {
                    // An anchor without a safe destination keeps its text and loses the link (its </a> is then
                    // ignored, because "a" is not open).
                    continue;
                }
                output.Append("<a href=\"").Append(WebUtility.HtmlEncode(href)).Append("\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">");
                open.Push("a");
                continue;
            }

            output.Append('<').Append(name).Append('>');
            open.Push(name);
        }

        if (dropping is null) AppendText(output, input[position..]);
        while (open.Count > 0) output.Append("</").Append(open.Pop()).Append('>');
        return output.ToString().Trim();
    }

    /// <summary>Text between tags: decode entities the author wrote, then encode everything once.</summary>
    private static void AppendText(StringBuilder output, string raw)
    {
        if (raw.Length == 0) return;
        output.Append(WebUtility.HtmlEncode(WebUtility.HtmlDecode(raw)));
    }

    /// <summary>http, https and mailto only; anything else (javascript:, data:, vbscript:, relative) is refused.</summary>
    internal static string? SafeHref(string attributes)
    {
        var match = Href.Match(attributes);
        if (!match.Success) return null;
        var value = WebUtility.HtmlDecode(match.Groups["v"].Value).Trim();
        // Control characters and whitespace inside a scheme are how "java\tscript:" gets past naive checks.
        if (value.Any(c => char.IsControl(c))) return null;
        if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return value.Length <= 500 ? value : null;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;
    }
}
