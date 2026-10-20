using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zayra.Api.Application.Setup;

namespace Zayra.Api.Infrastructure.Setup;

/// <summary>Pure source-to-form parsing. No employee, checklist or workflow is written here.</summary>
internal static class PolicyOnboardingDrafts
{
    private static readonly HashSet<string> Categories = ["General", "Document", "IT", "Training", "Policy", "Access"];
    private const string StartAnchor = @"(?:the\s+)?(?:employee(?:'s)?\s+)?(?:joining(?:\s+date)?|start\s+date)\b";
    private static string? Text(JsonElement row, string key, int max)
        => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) && text.Length <= max ? text.Trim() : null;

    internal static int ReceivedCount(JsonElement root) => root.TryGetProperty("onboardingTaskDrafts", out var rows)
        && rows.ValueKind == JsonValueKind.Array ? rows.GetArrayLength() : 0;

    internal sealed record SourceSpan(string SourceQuote, int SourceStart, int SourceLength, string SourceId = "");

    internal static List<SourceSpan> OriginalSources(Guid documentId, string hash, string source, int sourceOffset,
        string documentSource, List<PolicyOnboardingTaskDraft>? existing)
    {
        List<SourceSpan> spans = [];
        var scopes = new Dictionary<string, bool>();
        var restrictedScopes = new Dictionary<string, bool>();
        var active = false;
        string? pendingNumber = null;
        string? sectionNumber = null;
        // DOCX preserves paragraph boundaries and pasted tables preserve tabs. Read context
        // before this chunk, but send only whole source cells/paragraphs inside this chunk.
        foreach (Match part in Regex.Matches(documentSource, @"[^\r\n\t]+"))
        {
            var text = part.Value.Trim();
            var start = part.Index + part.Value.Length - part.Value.TrimStart().Length;
            if (start >= sourceOffset + source.Length) break;
            if (text.Length == 0) continue;
            var numbered = Regex.Match(text, @"^(?<number>\d+(?:\.\d+)*)(?:[.)])?\s+(?<title>.+)$");
            var requirement = Regex.Match(text, @"^\[(?<number>\d+(?:\.\d+)*)-[A-Z]+\d+\]", RegexOptions.IgnoreCase);
            var title = Regex.Replace(text, @"^#{1,6}\s+", "");
            if (Regex.IsMatch(text, @"\bTable\s+\d+\s*:", RegexOptions.IgnoreCase)) { active = false; continue; }
            if (Regex.IsMatch(text, @"^\d+(?:\.\d+)*$")) { pendingNumber = text; continue; }
            if (numbered.Success)
            {
                var number = numbered.Groups["number"].Value;
                var name = numbered.Groups["title"].Value;
                active = OnboardingTopic(name) && !OtherTopic(name);
                scopes[number] = active;
                foreach (var nested in restrictedScopes.Keys.Where(k => k.StartsWith(number + ".", StringComparison.Ordinal)).ToList()) restrictedScopes.Remove(nested);
                restrictedScopes[number] = Regex.IsMatch(name, @"\b(proposed|draft)\b", RegexOptions.IgnoreCase);
                sectionNumber = number;
                pendingNumber = null;
                continue;
            }
            if (requirement.Success)
            {
                var number = requirement.Groups["number"].Value;
                sectionNumber = number;
                if (OnboardingTopic(text) && !OtherTopic(text)) scopes[number] = true;
                active = scopes.Where(s => number == s.Key || number.StartsWith(s.Key + ".", StringComparison.Ordinal))
                    .OrderByDescending(s => s.Key.Length).Select(s => s.Value).FirstOrDefault();
                continue;
            }
            if (text.StartsWith('#') || text.Length <= 120 && !Actionable(text)
                && (OnboardingTopic(title) || Regex.IsMatch(title, @"^(probation|employment confirmation|appraisals?|termination|payroll|leave|benefits|proposed|draft)\b", RegexOptions.IgnoreCase)))
            {
                active = OnboardingTopic(title) && !OtherTopic(title);
                if (pendingNumber != null)
                {
                    scopes[pendingNumber] = active;
                    restrictedScopes[pendingNumber] = Regex.IsMatch(title, @"\b(proposed|draft)\b", RegexOptions.IgnoreCase);
                    sectionNumber = pendingNumber;
                }
                pendingNumber = null;
                continue;
            }
            pendingNumber = null;
            if (!active || start < sourceOffset || start + text.Length > sourceOffset + source.Length
                || text.Length > 4000 || !Actionable(text)) continue;
            if (sectionNumber != null && restrictedScopes.Any(s => s.Value
                && (sectionNumber == s.Key || sectionNumber.StartsWith(s.Key + ".", StringComparison.Ordinal)))) continue;
            // Classify the actual occurrence, not IndexOf of a duplicate quote elsewhere.
            var authority = new PolicyExtractionReview(documentId, hash, text, start, documentSource);
            if (authority.Classification(text) != "Policy" || !authority.AllowsOnboardingTask(text)) continue;
            var matching = (existing ?? []).Where(d => d.SourceStart >= start && d.SourceStart + d.SourceLength <= start + text.Length).ToList();
            var covered = matching.Any(d => d.SourceStart == start && d.SourceLength == text.Length);
            if (covered && (!Compound(text) || matching.Count > 1)) continue;
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{documentId}:{hash.ToLowerInvariant()}:{start}:{text.Length}")))[..24].ToLowerInvariant();
            spans.Add(new(text, start, text.Length, id));
            if (spans.Count == 80) break;
        }
        return spans;
    }

    private static bool OnboardingTopic(string text) => Regex.IsMatch(text, @"\b(onboarding|orientation|induction|pre-employment)\b|التهيئة|التوجيه|الانضمام", RegexOptions.IgnoreCase);
    private static bool OtherTopic(string text) => Regex.IsMatch(text, @"\b(probation|confirmation|appraisal|termination|payroll|leave|benefits|proposed|draft)\b", RegexOptions.IgnoreCase);
    private static bool Compound(string text) => Regex.IsMatch(text.TrimEnd('.', '!', '?'), @"[:,;.!?\r\n]|\band\b", RegexOptions.IgnoreCase);

    // Nullable context distinguishes a known other workflow from an unheaded source excerpt.
    // These are document headings, never the model's task title or capability suggestion.
    internal static List<(int Start, bool? Onboarding)> SourceTopicContexts(string documentSource)
    {
        List<(int Start, bool? Onboarding)> contexts = [];
        var scopes = new Dictionary<string, bool?>();
        bool? active = null;
        string? pendingNumber = null;
        bool? Topic(string title) => OtherTopic(title) ? false : OnboardingTopic(title) ? true : null;
        foreach (Match part in Regex.Matches(documentSource, @"[^\r\n\t]+"))
        {
            var text = part.Value.Trim();
            var numbered = Regex.Match(text, @"^(?<number>\d+(?:\.\d+)*)(?:[.)])?\s+(?<title>.+)$");
            var requirement = Regex.Match(text, @"^\[(?<number>\d+(?:\.\d+)*)-[A-Z]+\d+\]", RegexOptions.IgnoreCase);
            var title = Regex.Replace(text, @"^#{1,6}\s+", "");
            if (Regex.IsMatch(text, @"\bTable\s+\d+\s*:", RegexOptions.IgnoreCase)) active = null;
            else if (Regex.IsMatch(text, @"^\d+(?:\.\d+)*$")) { pendingNumber = text; continue; }
            else if (numbered.Success)
            {
                var number = numbered.Groups["number"].Value;
                active = Topic(numbered.Groups["title"].Value)
                    ?? scopes.Where(s => number.StartsWith(s.Key + ".", StringComparison.Ordinal))
                        .OrderByDescending(s => s.Key.Length).Select(s => s.Value).FirstOrDefault();
                scopes[number] = active;
            }
            else if (requirement.Success)
            {
                var number = requirement.Groups["number"].Value;
                active = scopes.Where(s => number == s.Key || number.StartsWith(s.Key + ".", StringComparison.Ordinal))
                    .OrderByDescending(s => s.Key.Length).Select(s => s.Value).FirstOrDefault();
            }
            else if (text.StartsWith('#') || text.Length <= 120 && !Actionable(text)
                && (OnboardingTopic(title) || Regex.IsMatch(title, @"^(probation|employment confirmation|appraisals?|termination|payroll|leave|benefits|proposed|draft)\b", RegexOptions.IgnoreCase)))
            {
                active = Topic(title) ?? active;
                if (pendingNumber != null) scopes[pendingNumber] = active;
            }
            else { pendingNumber = null; continue; }
            pendingNumber = null;
            contexts.Add((part.Index, active));
        }
        return contexts;
    }

    internal static List<PolicyOnboardingTaskDraft> ReadBound(JsonElement root, Guid documentId, string hash,
        List<SourceSpan> sources, string documentSource, out string diagnostics)
    {
        List<PolicyOnboardingTaskDraft> drafts = [];
        var unknown = 0;
        var rejected = 0;
        var duplicate = 0;
        if (root.TryGetProperty("onboardingTaskDrafts", out var rows) && rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray().Take(80))
            {
                var id = row.ValueKind == JsonValueKind.Object ? Text(row, "sourceId", 100) : null;
                var span = sources.FirstOrDefault(s => s.SourceId == id);
                if (span is null) { unknown++; continue; }
                var authority = new PolicyExtractionReview(documentId, hash, span.SourceQuote, span.SourceStart, documentSource);
                if (authority.Classification(span.SourceQuote) != "Policy") { rejected++; continue; }
                var bound = JsonNode.Parse(row.GetRawText())!.AsObject();
                bound["sourceQuote"] = span.SourceQuote;
                bound["sourceClassification"] = "Policy";
                // A paragraph ID proves source lineage, not which of its tasks owns a scalar.
                if (Compound(span.SourceQuote))
                {
                    bound["assignedToName"] = null;
                    bound["dueOffsetDays"] = null;
                    bound["isMandatory"] = null;
                }
                using var document = JsonDocument.Parse(new JsonObject { ["onboardingTaskDrafts"] = new JsonArray(bound) }.ToJsonString());
                var values = Read(document.RootElement, documentId, hash, span.SourceQuote, span.SourceStart, authority, out _);
                if (values.Count == 0) { rejected++; continue; }
                var draft = values[0];
                if (drafts.Any(d => d.SourceStart == draft.SourceStart && d.SourceLength == draft.SourceLength && NormalizeTitle(d.TaskTitle) == NormalizeTitle(draft.TaskTitle))) duplicate++;
                else drafts.Add(draft);
            }
        diagnostics = $"received {ReceivedCount(root)} task rows; validated {drafts.Count}. unknownSourceId={unknown}; rejectedBoundValues={rejected}; duplicate={duplicate}.";
        return drafts;
    }

    internal static string NormalizeTitle(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    internal static List<SourceSpan> UncoveredSources(JsonElement root, PolicyExtractionResult result,
        string source, int sourceOffset, PolicyExtractionReview review, out string diagnostics)
    {
        var absent = 0;
        var evidence = 0;
        var unapproved = 0;
        var noAction = 0;
        var covered = 0;
        var quotes = (result.Requirements ?? []).Where(r => r.Capability is "onboarding.checklist" or "onboarding.orientation")
            .Select(r => r.SourceQuote).ToList();
        // A malformed draft can still identify exact source evidence for a fresh projection.
        // Its proposed values/classification are never accepted by this recovery gate.
        if (root.TryGetProperty("onboardingTaskDrafts", out var rows) && rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray().Take(80))
            {
                var quote = row.ValueKind == JsonValueKind.Object ? Text(row, "sourceQuote", 4000) : null;
                if (quote is null) absent++;
                else quotes.Add(quote);
            }
        List<SourceSpan> missing = [];
        foreach (var quote in quotes.Distinct(StringComparer.Ordinal))
        {
            var at = source.IndexOf(quote, StringComparison.Ordinal);
            if (at < 0) { evidence++; continue; }
            if (review.Classification(quote) != "Policy") { unapproved++; continue; }
            if (!Actionable(quote)) { noAction++; continue; }
            var absoluteAt = at + sourceOffset;
            // Coverage is evidence-range coverage, not a claim of semantic completeness.
            var coveredThrough = absoluteAt;
            foreach (var task in (result.OnboardingTaskDrafts ?? []).OrderBy(d => d.SourceStart))
                if (task.SourceStart <= coveredThrough && task.SourceStart + task.SourceLength > coveredThrough)
                    coveredThrough = task.SourceStart + task.SourceLength;
            // One broad quote can mask a list that was projected into just one task. Give the
            // focused pass one opportunity to split it; never claim semantic completeness.
            var matching = (result.OnboardingTaskDrafts ?? []).Count(d => d.SourceStart >= absoluteAt
                && d.SourceStart + d.SourceLength <= absoluteAt + quote.Length);
            var compound = Regex.IsMatch(quote, @"[:,;]|\band\b", RegexOptions.IgnoreCase);
            if (coveredThrough < absoluteAt + quote.Length || compound && matching <= 1)
                missing.Add(new(quote, absoluteAt, quote.Length));
            else covered++;
        }
        // Avoid multiplying a chunk's source budget when model requirements quote overlapping
        // clauses. Only merge overlapping spans, preserving exact original text and offsets.
        List<SourceSpan> bounded = [];
        foreach (var span in missing.OrderBy(s => s.SourceStart).ThenByDescending(s => s.SourceLength))
        {
            if (bounded.Count == 0 || span.SourceStart > bounded[^1].SourceStart + bounded[^1].SourceLength)
                bounded.Add(span);
            else
            {
                var prior = bounded[^1];
                var length = Math.Max(prior.SourceStart + prior.SourceLength, span.SourceStart + span.SourceLength) - prior.SourceStart;
                bounded[^1] = new(source.Substring(prior.SourceStart - sourceOffset, length), prior.SourceStart, length);
            }
        }
        diagnostics = $"Recovery eligibility: absentEvidence={absent}; evidence={evidence}; sourceUnapproved={unapproved}; noAction={noAction}; covered={covered}; selected={bounded.Count}.";
        return bounded;
    }

    private static bool Actionable(string quote) => Regex.IsMatch(quote,
        @"\b(arrang(?:e|es|ed|ing)|sign(?:s|ed|ing)?|provid(?:e|es|ed|ing)|issu(?:e|es|ed|ing)|verif(?:y|ies|ied|ication)|complet(?:e|es|ed|ing)|conduct(?:s|ed|ing)?|attend(?:s|ed|ing)?|acknowledg(?:e|es|ed|ing|ement|ment)|collect(?:s|ed|ing)?|assign(?:s|ed|ing)?|prepare(?:s|d)?|submit(?:s|ted|ting)?|grant(?:s|ed|ing)?|introduc(?:e|es|ed|ing)|train(?:s|ed|ing)?|explain(?:s|ed|ing)?|archiv(?:e|es|ed|ing))\b|توقيع|إجراء|استكمال|توفير|تسليم|حضور", RegexOptions.IgnoreCase);

    internal static List<PolicyOnboardingTaskDraft> Read(JsonElement root, Guid documentId, string hash,
        string source, int sourceOffset, PolicyExtractionReview review, out string diagnostics)
    {
        var malformed = 0;
        var missingFields = 0;
        var invalidCategory = 0;
        var evidence = 0;
        var classification = 0;
        var sourceTopic = 0;
        var duplicate = 0;
        var modelClasses = ClassCounts();
        var sourceClasses = ClassCounts();
        List<PolicyOnboardingTaskDraft> drafts = [];
        diagnostics = "received 0 task rows; validated 0.";
        if (!root.TryGetProperty("onboardingTaskDrafts", out var rows) || rows.ValueKind != JsonValueKind.Array) return drafts;
        foreach (var row in rows.EnumerateArray().Take(80))
        {
            if (row.ValueKind != JsonValueKind.Object) { malformed++; continue; }
            var modelClass = Text(row, "sourceClassification", 30);
            modelClasses[SafeClass(modelClass)]++;
            var title = Text(row, "taskTitle", 200);
            var description = Text(row, "taskDescription", 2000);
            var category = Text(row, "category", 30);
            var quote = Text(row, "sourceQuote", 4000);
            if (title is null || description is null || category is null || quote is null) { missingFields++; continue; }
            if (!Categories.Contains(category)) { invalidCategory++; continue; }
            var at = source.IndexOf(quote, StringComparison.Ordinal);
            if (at < 0) { evidence++; continue; }
            sourceClasses[SafeClass(review.Classification(quote))]++;
            if (review.Classification(quote, modelClass) != "Policy") { classification++; continue; }
            if (!review.AllowsOnboardingTask(quote))
            {
                sourceTopic++;
                var absoluteAt = at + sourceOffset;
                if (!review.Requirements.Any(r => r.SourceStart <= absoluteAt
                    && r.SourceStart + r.SourceLength >= absoluteAt + quote.Length))
                    review.AddRequirement("Separate workflow",
                        "Review this source requirement in its separate workflow; it is not an onboarding checklist task.",
                        "policy.other", quote, "Policy");
                continue;
            }

            var owner = Text(row, "assignedToName", 200);
            if (owner is not null && !Regex.IsMatch(quote, @"(?<![\p{L}\p{N}])" + Regex.Escape(owner) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)) owner = null;
            int? offset = row.TryGetProperty("dueOffsetDays", out var rawOffset) && rawOffset.ValueKind == JsonValueKind.Number
                && rawOffset.TryGetInt32(out var days) && ExplicitOffset(quote, days) ? days : null;
            bool? mandatory = null;
            if (row.TryGetProperty("isMandatory", out var rawMandatory) && rawMandatory.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var optional = Regex.IsMatch(quote, @"\b(optional|not required|not mandatory)\b", RegexOptions.IgnoreCase);
                var required = !optional && !Regex.IsMatch(quote, @"\b(must|shall)\s+not\b", RegexOptions.IgnoreCase)
                    && Regex.IsMatch(quote, @"\b(must|shall|required|mandatory)\b", RegexOptions.IgnoreCase);
                if (rawMandatory.GetBoolean() ? required : optional) mandatory = rawMandatory.GetBoolean();
            }
            var condition = Text(row, "condition", 1000);
            if (condition is not null && !quote.Contains(condition, StringComparison.OrdinalIgnoreCase)) condition = null;
            // Do not erase an applicability condition if the model omitted/paraphrased it.
            // This is visible source text for human review, not an executable eligibility rule.
            if (condition is null && Regex.IsMatch(quote,
                @"\b(if|unless|where|when|only|except|applicable|subject to|for|as needed|outside|inside|overseas|first[- ]time|non[- ]\p{L}+)\b|إذا|عند الحاجة|حسب الحاجة", RegexOptions.IgnoreCase)) condition = quote;

            var identity = JsonSerializer.Serialize(new object?[] { documentId, hash.ToLowerInvariant(), at + sourceOffset,
                quote.Length, title, description, category, owner, offset, mandatory, condition });
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            if (drafts.All(d => d.Id != id)) drafts.Add(new(id, title, description, category, owner, offset, mandatory,
                condition, quote, at + sourceOffset, quote.Length));
            else duplicate++;
        }
        diagnostics = $"received {rows.GetArrayLength()} task rows; validated {drafts.Count}. Rejected: malformed={malformed}; missingFields={missingFields}; category={invalidCategory}; evidence={evidence}; classification={classification}; sourceTopic={sourceTopic}; duplicate={duplicate}; capped={Math.Max(0, rows.GetArrayLength() - 80)}. "
            + "modelClasses: " + string.Join(",", modelClasses.Select(c => $"{c.Key}={c.Value}"))
            + "; sourceClasses: " + string.Join(",", sourceClasses.Select(c => $"{c.Key}={c.Value}")) + ".";
        return drafts;
    }

    // Closed keys only: arbitrary model labels may contain policy text and must never enter diagnostics.
    private static Dictionary<string, int> ClassCounts() => new() { ["Policy"] = 0, ["REC"] = 0,
        ["ASM"] = 0, ["TBC"] = 0, ["PROP"] = 0, ["Unset"] = 0, ["Other"] = 0 };
    private static string SafeClass(string? value) => value?.Split('-')[0].ToUpperInvariant() switch
    {
        "POLICY" => "Policy", "REC" => "REC", "ASM" => "ASM", "TBC" => "TBC", "PROP" => "PROP",
        null or "" => "Unset", _ => "Other",
    };

    private static bool ExplicitOffset(string quote, int proposed)
    {
        if (proposed is < -3650 or > 3650) return false;
        // A deadline in a broad multi-task quote may belong to a different task. Require narrow
        // evidence; retaining null is safer than transferring that other task's deadline.
        if (Regex.IsMatch(quote.Trim().TrimEnd('.', '!', '?'), @"[.,;!?\r\n\t]|\band\b", RegexOptions.IgnoreCase)) return false;
        if (proposed == 0 && Regex.IsMatch(quote, @"\b(on|at)\s+" + StartAnchor, RegexOptions.IgnoreCase)) return true;
        // Offsets feed AddDays in the existing form: working days, months, unrelated dates and
        // inferred day-one conventions cannot safely be represented by this integer.
        foreach (Match match in Regex.Matches(quote, @"\b(?<days>\d{1,4})\s+(?:calendar\s+)?days?\s+(?<direction>before|after)\s+" + StartAnchor, RegexOptions.IgnoreCase))
        {
            var days = int.Parse(match.Groups["days"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (match.Groups["direction"].Value.Equals("before", StringComparison.OrdinalIgnoreCase)) days = -days;
            if (days == proposed) return true;
        }
        return false;
    }
}
