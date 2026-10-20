using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zayra.Api.Application.Setup;

namespace Zayra.Api.Infrastructure.Setup;

/// <summary>Evidence-bound review output. This class never writes configuration or workflow records.</summary>
internal sealed class PolicyExtractionReview(Guid documentId, string hash, string source, int sourceOffset, string? documentSource = null)
{
    // These are existing operational surfaces, not company-scoped Setup Apply destinations.
    // Onboarding checklist templates are tenant-wide; probation reviews are employee-specific.
    internal static readonly string[] Capabilities = ["onboarding.checklist", "onboarding.orientation",
        "employment.probation", "employment.confirmation", "employment.recovery", "workflow.approvals", "policy.other"];
    private static readonly HashSet<string> SeparateWorkflows = ["onboarding.checklist", "onboarding.orientation",
        "employment.probation", "employment.confirmation"];
    public List<PolicyExtractionIssue> Issues { get; } = [];
    public List<PolicyReviewRequirement> Requirements { get; } = [];
    private List<(int Start, bool Proposed)>? headingContexts;
    private List<(int Start, bool Proposed)>? numberedHeadingContexts;
    private List<(int Start, bool? Onboarding)>? sourceTopicContexts;

    public bool AllowsOnboardingTask(string quote)
    {
        var at = Location(quote);
        if (at < 0) return false;
        sourceTopicContexts ??= PolicyOnboardingDrafts.SourceTopicContexts(documentSource ?? source);
        var absoluteAt = at + (documentSource is null ? 0 : sourceOffset);
        return sourceTopicContexts.LastOrDefault(item => item.Start <= absoluteAt).Onboarding != false;
    }

    private bool UnderProposedHeading(int at)
    {
        // Classification reads the document's enclosing headings even when an extraction chunk
        // begins inside a table. Evidence validation still requires an exact quote in this chunk.
        var context = documentSource ?? source;
        var absoluteAt = at + (documentSource is null ? 0 : sourceOffset);
        if (headingContexts is null)
        {
            headingContexts = [];
            var stack = new List<(int Level, bool Proposed)>();
            foreach (Match heading in Regex.Matches(context,
                @"(?m)^[ \t]*(?:(?<level>#{1,6})[ \t]+[^\r\n]+|[^\r\n]{0,100}\bTable[ \t]+\d+[ \t]*:[^\r\n]+|(?:Proposed|Draft)[ \t]+(?:configuration|policy|requirements)[^\r\n]*)\r?$",
                RegexOptions.IgnoreCase))
            {
                var proposed = Regex.IsMatch(heading.Value, @"\b(proposed|draft)\b", RegexOptions.IgnoreCase);
                if (heading.Groups["level"].Success)
                {
                    var level = heading.Groups["level"].Length;
                    stack.RemoveAll(item => item.Level >= level);
                    stack.Add((level, proposed));
                }
                headingContexts.Add((heading.Index, proposed || stack.Any(item => item.Proposed)));
            }
        }
        if (numberedHeadingContexts is null)
        {
            numberedHeadingContexts = [];
            var scopes = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (Match heading in Regex.Matches(context,
                @"(?m)^[ \t]*(?:(?<number>\d+(?:\.\d+)*)(?:[.)])?[ \t]+(?<title>[^\r\n]+)|#{1,6}[ \t]+[^\r\n]+|[^\r\n]{0,100}\bTable[ \t]+\d+[ \t]*:[^\r\n]+)\r?$",
                RegexOptions.IgnoreCase))
            {
                if (heading.Groups["number"].Success)
                {
                    var number = heading.Groups["number"].Value;
                    // Retain only numbered ancestors. A child cannot promote a proposed
                    // parent, while a new sibling policy section ends that parent's scope.
                    foreach (var key in scopes.Keys.Where(key => !number.StartsWith(key + ".", StringComparison.Ordinal)).ToArray())
                        scopes.Remove(key);
                    scopes[number] = Regex.IsMatch(heading.Groups["title"].Value, @"\b(proposed|draft)\b", RegexOptions.IgnoreCase);
                }
                else scopes.Clear(); // An explicit Markdown/table heading starts a new hierarchy.
                numberedHeadingContexts.Add((heading.Index, scopes.Values.Any(proposed => proposed)));
            }
        }
        return headingContexts.LastOrDefault(item => item.Start <= absoluteAt).Proposed
            || numberedHeadingContexts.LastOrDefault(item => item.Start <= absoluteAt).Proposed;
    }

    private static string Read(JsonElement row, string name, int max = 700) => row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? (value.GetString() ?? "")[..Math.Min(value.GetString()!.Length, max)].Trim() : "";

    private int Location(string? quote) => string.IsNullOrWhiteSpace(quote) || quote.Length > 4000
        ? -1 : source.IndexOf(quote, StringComparison.Ordinal);

    public string Classification(string quote, string? proposed = null)
    {
        var at = Location(quote);
        if (at < 0) return "TBC";
        // Pasted tables preserve tabs; DOCX extraction emits each cell paragraph on a new
        // line. A recommendation in the control cell does not relabel the policy-rule cell.
        // Inspect the containing cell/paragraph (including omitted inline labels), using the
        // full document so an extraction chunk boundary cannot hide that same-cell label.
        var annotationSource = documentSource ?? source;
        var quoteStart = at + (documentSource is null ? 0 : sourceOffset);
        char[] boundaries = ['\t', '\r', '\n'];
        var start = annotationSource.LastIndexOfAny(boundaries, quoteStart);
        var end = annotationSource.IndexOfAny(boundaries, quoteStart + quote.Length);
        var context = annotationSource[Math.Max(start + 1, quoteStart - 1000)..Math.Min(end < 0 ? annotationSource.Length : end, quoteStart + quote.Length + 1000)];
        var annotation = Regex.Match(context, @"\[(REC|ASM|TBC|PROP)(?:-[A-Z0-9]+)*\]", RegexOptions.IgnoreCase);
        if (annotation.Success) return annotation.Groups[1].Value.ToUpperInvariant();
        if (UnderProposedHeading(at)) return "PROP";
        var requested = (proposed ?? "Policy").Split('-')[0].ToUpperInvariant();
        return requested switch { "REC" or "ASM" or "TBC" or "PROP" => requested, "POLICY" or "" => "Policy", _ => "TBC" };
    }

    private string Identity(string kind, string section, string target, string field, int start, int length, string meaning)
    {
        var identity = JsonSerializer.Serialize(new[] { documentId.ToString(), hash.ToLowerInvariant(), kind, section,
            target, field, (start + sourceOffset).ToString(), length.ToString(), Regex.Replace(meaning.Trim(), @"\s+", " ") });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static (string Target, string Field) Destination(string? target, string? field, string capability)
        => target is not null && field is not null && PolicyExtractionService.IsSupportedField(target, field)
            ? (target, field) : ("requirement", Capabilities.Contains(capability) ? capability : "policy.other");

    public void AddRequirement(string section, string summary, string capability, string quote,
        string? classification = null, string? target = null, string? field = null, List<string>? missing = null)
    {
        var at = Location(quote);
        if (at < 0 || string.IsNullOrWhiteSpace(summary)) return;
        capability = Capabilities.Contains(capability) ? capability : "policy.other";
        var category = Classification(quote, classification);
        var destination = Destination(target, field, capability);
        var id = Identity("requirement:" + category, section, destination.Target, destination.Field, at, quote.Length, summary);
        if (Requirements.Any(r => r.Id == id)) return;
        Requirements.Add(new(id, section, summary, capability,
            category == "Policy" && SeparateWorkflows.Contains(capability) ? "ManualConfigurationRequired" : "ReviewRequired",
            category, quote, at + sourceOffset, quote.Length, destination.Target, destination.Field, missing ?? []));
    }

    public void AddIssue(string section, string kind, string message, string code, string? quote = null,
        string? target = null, string? field = null, string capability = "policy.other")
    {
        var at = Location(quote);
        if (at < 0)
        {
            // A chapter's silence on another topic is not a missing rule. Unverified operational
            // warnings cannot become blockers; retain only explicit informational diagnostics.
            if (kind != "info") return;
            var infoId = Identity("info:" + code, section, "", "", 0, 0, message);
            if (Issues.All(i => i.Id != infoId)) Issues.Add(new(section, "info", message, infoId, code));
            return;
        }
        var destination = Destination(target, field, capability);
        var normalizedKind = kind is "missing" or "unsupported" or "review" ? kind : "review";
        var id = Identity("issue:" + code, section, destination.Target, destination.Field, at, quote!.Length, message);
        if (Issues.All(i => i.Id != id)) Issues.Add(new(section, normalizedKind, message, id, code,
            destination.Target, destination.Field, quote, at + sourceOffset, quote.Length));
    }

    public void ReadModel(JsonElement root)
    {
        if (root.TryGetProperty("requirements", out var requirements) && requirements.ValueKind == JsonValueKind.Array)
            foreach (var row in requirements.EnumerateArray().Take(80))
            {
                List<string> missing = [];
                if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("missingFields", out var fields) && fields.ValueKind == JsonValueKind.Array)
                    missing = fields.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => v.GetString()!).Where(v => v.Length is > 0 and <= 120).Distinct().Take(30).ToList();
                AddRequirement(Read(row, "section", 100), Read(row, "summary"), Read(row, "capability", 100),
                    Read(row, "sourceQuote", 4001), Read(row, "sourceClassification", 30), Read(row, "target", 30), Read(row, "field", 100), missing);
            }
        if (root.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
            foreach (var row in issues.EnumerateArray().Take(80))
            {
                var kind = Read(row, "kind", 30);
                if (kind is not ("missing" or "unsupported" or "review" or "info")) continue;
                var message = Read(row, "message");
                if (string.IsNullOrWhiteSpace(message)) continue;
                var code = Read(row, "code", 80);
                if (code is not ("policy_conflict" or "policy_incomplete" or "policy_unsupported" or "source_evidence_invalid" or "invalid_proposal" or "unapproved_source"))
                    code = kind switch { "missing" => "policy_incomplete", "unsupported" => "policy_unsupported", _ => "policy_conflict" };
                AddIssue(Read(row, "section", 100), kind, message, code, Read(row, "sourceQuote", 4001),
                    Read(row, "target", 30), Read(row, "field", 100), Read(row, "capability", 100));
            }
    }
}
