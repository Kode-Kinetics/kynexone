using Zayra.Api.Application.Setup;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Zayra.Api.Infrastructure.Setup;

public static class LocalOnboardingPolicyMapper
{
    private const string Actions = @"\b(?:arranges?|signs?|signing|provides?|issues?|verif(?:y|ies)|completes?|conducts?|attends?|acknowledges?|collects?|assigns?|prepares?|submits?|grants?|introduces?|trains?|explains?|archives?|creates?|activates?|schedules?|reviews?|registers?|develops?)\b";
    private const string Onboarding = @"\b(?:onboarding|orientation|induction|pre-employment|new (?:hire|employee|joiner)s?)\b";
    private const string OtherWorkflow = @"\b(?:probation|confirmation|appraisal|termination|payroll|annual leave|sick leave|leave entitlement|benefits?|calculations?|calculates?|compensation|reimbursement|onboarding costs|accounting)\b";
    private const string NonPolicy = @"\[(?:REC|ASM|TBC|PROP)(?:\b|[-_])|\b(?:proposed|proposals?|not approved|pending approval|draft|recommended|recommendations?)\b|\bgaps?\s*:|\bCD-\d";
    private const string Conditions = @"\b(?:if|unless|where|when|only|except|applicable|subject to|as needed|outside|overseas|first-time|non-Saudi)\b";
    private const int TaskLimit = 1000;
    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static bool Has(string text, string pattern) => Rx(pattern).IsMatch(text);
    private sealed record Scope(int Depth, bool Blocked, bool? Topic);

    /// <summary>
    /// A bounded, English-language drafting pass over already-authorized parsed text. No policy is
    /// accepted, no task is saved, and completion means the local pass ended, not full policy coverage.
    /// </summary>
    public static PolicyExtractionResult Prepare(Guid documentId, string hash, string text)
    {
        var tasks = new List<PolicyOnboardingTaskDraft>();
        var issues = new List<PolicyExtractionIssue>
        {
            new("Onboarding", "info", "Local English-language rules prepare reviewable task drafts only. Review the original document for unsupported or ambiguous clauses, conditions, owners, dates and obligations; this is not complete policy coverage.")
        };
        var numeric = new List<Scope>();
        var markdown = new List<Scope>();
        bool plainBlocked = false, capped = false, oversized = false;
        bool? topic = null;

        bool Heading(string value)
        {
            var md = Rx(@"^(#{1,6})\s+(.+)$").Match(value);
            var number = Rx(@"^(\d+(?:\.\d+)*)(?:[.)])?\s+(.+)$").Match(value);
            bool table = Has(value, @"^Policy\b.*\bTable\s+\d+\s*:");
            bool plain = Has(value, @"^(?:(?:employee |draft )?(?:onboarding|orientation|induction)|probation(?: and confirmation)?|annual leave|payroll|(?:proposed|draft|recommended|recommendations?|proposals?)(?:\s+[\w -]+)?|policy requirements)\.?$");
            if ((!md.Success && !number.Success && !table && !plain) || Has(value, Actions) || value.Length > 180)
                return false;
            var label = md.Success ? md.Groups[2].Value : number.Success ? number.Groups[2].Value : value;
            bool blocked = Has(label, NonPolicy);
            bool? ownTopic = Has(label, OtherWorkflow) ? false : Has(label, Onboarding) ? true : null;
            if (table)
            {
                numeric.Clear(); markdown.Clear(); plainBlocked = blocked; topic = ownTopic;
                return true;
            }
            if (md.Success || number.Success)
            {
                var stack = md.Success ? markdown : numeric;
                var depth = md.Success ? md.Groups[1].Length : number.Groups[1].Value.Count(c => c == '.') + 1;
                while (stack.Count > 0 && stack[^1].Depth >= depth) stack.RemoveAt(stack.Count - 1);
                if (depth == 1 && Has(label, @"\bpolicy requirements\b")) plainBlocked = false;
                topic = ownTopic ?? stack.LastOrDefault()?.Topic;
                stack.Add(new(depth, blocked, topic));
            }
            else
            {
                // A plain proposed ancestor is deliberately sticky: an Onboarding child cannot approve it.
                if (Has(label, @"^policy requirements\.?$")) { plainBlocked = false; numeric.Clear(); markdown.Clear(); }
                plainBlocked |= blocked;
                topic = ownTopic;
            }
            return true;
        }

        void Add(string quote, int start, bool checklistItem = false)
        {
            if (quote.Length == 0 || Has(quote, NonPolicy) || Has(quote, OtherWorkflow)
                || Has(quote, @"\b(?:must|shall|should|do|does)\s+not\b|\bnever\b")
                || (checklistItem && Has(quote, @"^(?:no|not)\b"))) return;
            if (!checklistItem && !Has(quote, Actions)) return;
            if (quote.Length > 2000) { oversized = true; return; }
            if (tasks.Count >= TaskLimit) { capped = true; return; }
            var conditional = Has(quote, Conditions);
            var compound = Rx(Actions).Matches(quote).Count > 1 || Has(quote, @"\b(?:HR|IT|Admin)\s+and\s+(?:HR|IT|Admin)\b");
            string? owner = null;
            int? due = null;
            bool? mandatory = null;
            if (!checklistItem && !compound && !conditional)
            {
                var subject = Rx(@"^(HR|IT|Admin|Human Resources|Employees?|Line Manager|Hiring Manager|Direct Senior|Manager|Finance)\s+(?:(?:must|shall|should|will)\s+)?" + Actions).Match(quote);
                if (subject.Success) owner = subject.Groups[1].Value;
                if (Has(quote, @"\b(?:optional|not mandatory|not required)\b")) mandatory = false;
                else if (Has(quote, @"\b(?:must|shall|mandatory|required)\b")) mandatory = true;
                var offset = Rx(@"\b(\d{1,4})\s+(?:calendar\s+)?days?\s+(before|after)\s+(?:the\s+)?(?:employee['’]s\s+)?(?:joining(?:\s+date)?|start date)\b").Match(quote);
                if (offset.Success && int.TryParse(offset.Groups[1].Value, out var days) && days <= 3650)
                    due = offset.Groups[2].Value.Equals("before", StringComparison.OrdinalIgnoreCase) ? -days : days;
                else if (Has(quote, @"\b(?:on|at)\s+(?:the\s+)?(?:joining|start) date\b")) due = 0;
            }
            var categories = new List<string>();
            if (Has(quote, @"\b(?:contract|passport|documents?|identity|personal file)\b")) categories.Add("Document");
            if (Has(quote, @"\b(?:laptop|computer|e-?mail|IT equipment|system access|credentials)\b")) categories.Add("IT");
            if (Has(quote, @"\b(?:access badge|access card|ID card|keys)\b")) categories.Add("Access");
            if (Has(quote, @"\b(?:orientation|induction|training)\b")) categories.Add("Training");
            if (Has(quote, @"\b(?:policies|policy acknowledgement|code of conduct)\b")) categories.Add("Policy");
            var title = quote.Length <= 200 ? quote : quote[..200];
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"local-rules-v1|{documentId:D}|{hash}|{start}|{quote.Length}"))).ToLowerInvariant();
            tasks.Add(new(id, title, quote, categories.Count == 1 ? categories[0] : "General", owner, due,
                mandatory, conditional ? quote : null, quote, start, quote.Length));
        }

        foreach (Match cell in Rx(@"[^\r\n\t]+").Matches(text))
        {
            // Keep original offsets; never normalize or reconstruct evidence.
            var cellValue = cell.Value.Trim();
            var cellStart = cell.Index + cell.Value.IndexOf(cellValue, StringComparison.Ordinal);
            if (cellValue.Length == 0 || Heading(cellValue)) continue;
            var prefix = Rx(@"^(?:[•*\-]\s*|\d+[.)]\s+|\[\d+(?:\.\d+)*-R\d+\]\s*)").Match(cellValue);
            if (prefix.Success) { cellStart += prefix.Length; cellValue = cellValue[prefix.Length..]; }
            // Separate sentences in flattened PDF text, but retain conditional paragraphs intact.
            var boundaries = Has(cellValue, Conditions) ? Array.Empty<Match>() : Rx(@"[.!?]\s+(?=[A-Z])").Matches(cellValue).Cast<Match>().ToArray();
            int cursor = 0;
            foreach (var end in boundaries.Select(match => match.Index + 1).Append(cellValue.Length))
            {
                var raw = cellValue[cursor..end];
                var value = raw.Trim();
                var start = cellStart + cursor + raw.IndexOf(value, StringComparison.Ordinal);
                cursor = end;
                if (value.Length == 0 || Heading(value)) continue;
                if (plainBlocked || numeric.Any(scope => scope.Blocked) || markdown.Any(scope => scope.Blocked)
                    || topic == false || (topic != true && !Has(value, Onboarding)) || Has(value, NonPolicy)) continue;
                var list = Rx(@"^(?:HR|IT|Admin|Human Resources|Manager)\s+(?:arranges?|provides?|coordinates?|prepares?)\s*:\s*").Match(value);
                if (list.Success)
                {
                    if (Has(value[list.Length..], @"(?:^|,)\s*(?:if|unless|when|where|only if|except)\b"))
                    {
                        issues.Add(new("Onboarding", "warning", "A conditional checklist was left intact for manual review because comma-separated items could lose their shared condition.",
                            SourceQuote: value, SourceStart: start, SourceLength: value.Length));
                        continue;
                    }
                    foreach (Match item in Rx(@"[^,]+").Matches(value[list.Length..]))
                    {
                        var itemValue = item.Value.Trim();
                        Add(itemValue, start + list.Length + item.Index + item.Value.IndexOf(itemValue, StringComparison.Ordinal), true);
                    }
                }
                else Add(value, start);
            }
        }
        if (capped) issues.Add(new("Onboarding", "warning", "Local preparation reached 1000 task drafts. Review all remaining source content manually; no full-coverage claim is made."));
        if (oversized) issues.Add(new("Onboarding", "warning", "One or more source clauses exceed the task editor's description limit and were left for manual review."));
        return new(documentId, hash, "local-rules", new(), issues,
            new() { new("Onboarding", tasks.Count > 0 ? "Task drafts to review" : "Manual review required") },
            "Local preparation finished. Review and edit source-backed task drafts before saving; nothing was saved or published.",
            new(0, 1, 0, text.Length, text.Length, null, true), new(), new(), tasks);
    }
}
