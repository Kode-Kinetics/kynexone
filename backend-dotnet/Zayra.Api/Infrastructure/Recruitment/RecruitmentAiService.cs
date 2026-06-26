using System.Text;
using System.Text.Json;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.AI;

namespace Zayra.Api.Infrastructure.Recruitment;

/// <summary>
/// AI helpers for recruitment: job-description generation, candidate screening/ranking (advisory),
/// and interview-question generation. LLM-backed with deterministic fallbacks, and guardrails so the
/// output is always well-formed. Screening NEVER takes an action — it only scores and recommends.
/// </summary>
public sealed class RecruitmentAiService : IRecruitmentAiService
{
    private readonly ILlmClient _llm;
    private readonly AiOptions _options;
    private readonly ILogger<RecruitmentAiService> _logger;

    public RecruitmentAiService(ILlmClient llm, AiOptions options, ILogger<RecruitmentAiService> logger)
    {
        _llm = llm;
        _options = options;
        _logger = logger;
    }

    // ── Job description ─────────────────────────────────────────────────────

    public async Task<JobDescriptionResult> GenerateJobDescriptionAsync(JobDescriptionRequest req, CancellationToken ct)
    {
        var sys = "You are an expert HR recruiter. Output ONLY JSON: " +
                  "{\"summary\":\"\",\"responsibilities\":[\"\"],\"requirements\":[\"\"]}. " +
                  "Summary is 2-3 sentences. 5-8 responsibilities, 5-8 requirements, each a concise phrase.";
        var sb = new StringBuilder();
        sb.AppendLine($"Write a job description for: {req.Title}.");
        if (!string.IsNullOrWhiteSpace(req.DepartmentName)) sb.AppendLine($"Department: {req.DepartmentName}.");
        if (!string.IsNullOrWhiteSpace(req.DesignationTitle)) sb.AppendLine($"Designation: {req.DesignationTitle}.");
        if (!string.IsNullOrWhiteSpace(req.SeniorityLevel)) sb.AppendLine($"Seniority: {req.SeniorityLevel}.");
        sb.AppendLine($"Employment type: {req.EmploymentType}.");
        if (!string.IsNullOrWhiteSpace(req.CountryCode)) sb.AppendLine($"Country: {req.CountryCode}.");
        if (!string.IsNullOrWhiteSpace(req.Notes)) sb.AppendLine($"Notes: {req.Notes}.");
        sb.AppendLine("Return ONLY the JSON object.");

        var (json, engine) = await CallAsync(sys, sb.ToString(), ct);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                var summary = r.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                var resp = StrList(r, "responsibilities");
                var reqs = StrList(r, "requirements");
                if (!string.IsNullOrWhiteSpace(summary) || resp.Count > 0 || reqs.Count > 0)
                    return new JobDescriptionResult(summary, resp, reqs, $"{engine}+guardrails");
            }
            catch (Exception ex) { _logger.LogWarning(ex, "JD parse failed; using template."); }
        }

        // Deterministic fallback
        return new JobDescriptionResult(
            $"We are seeking a {req.Title}{(string.IsNullOrWhiteSpace(req.DepartmentName) ? "" : $" in {req.DepartmentName}")} to join our team on a {req.EmploymentType} basis.",
            new() { "Deliver core responsibilities of the role to a high standard",
                    "Collaborate with cross-functional teams",
                    "Ensure compliance with company policies and local regulations",
                    "Report progress and outcomes to the line manager" },
            new() { $"Relevant experience as a {req.Title} or similar",
                    "Strong communication and teamwork skills",
                    "Eligibility to work in the specified location" },
            "template");
    }

    // ── Screening (advisory) ────────────────────────────────────────────────

    public async Task<ScreeningResult> ScreenAsync(ScreeningInput input, CancellationToken ct)
    {
        var notes = new List<string> { "Advisory only — scores are heuristic estimates; no candidate is auto-rejected." };
        if (input.Candidates.Count == 0)
            return new ScreeningResult(new(), "n/a", new() { "No active candidates to screen for this opening." });

        var byId = input.Candidates.ToDictionary(c => c.CandidateId);
        var scores = new Dictionary<Guid, CandidateScore>();

        var sys = "You are a recruiter screening candidates against a role. Output ONLY a JSON array: " +
                  "[{\"candidateId\":\"<guid>\",\"score\":<0-100>,\"recommendation\":\"Shortlist|Maybe|Reject\",\"rationale\":\"<1 sentence>\"}]. " +
                  "Score on fit to the role's requirements. Be fair and objective.";
        var sb = new StringBuilder();
        sb.AppendLine($"ROLE: {input.JobTitle}");
        if (!string.IsNullOrWhiteSpace(input.Description)) sb.AppendLine($"DESCRIPTION: {input.Description}");
        if (!string.IsNullOrWhiteSpace(input.Requirements)) sb.AppendLine($"REQUIREMENTS: {input.Requirements}");
        sb.AppendLine("CANDIDATES:");
        foreach (var c in input.Candidates)
            sb.AppendLine($"- id={c.CandidateId} | {c.Name} | {c.CurrentJobTitle} | {c.ExperienceYears}y exp | {c.EducationLevel} | skills: {c.Tags}");
        sb.AppendLine("Return ONLY the JSON array, one object per candidate.");

        var (json, engine) = await CallAsync(sys, sb.ToString(), ct);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("candidateId", out var idEl)) continue;
                    if (!Guid.TryParse(idEl.GetString(), out var id) || !byId.ContainsKey(id)) continue;
                    var score = Math.Clamp(el.TryGetProperty("score", out var sc) && sc.TryGetInt32(out var v) ? v : 0, 0, 100);
                    var rec = NormalizeRec(el.TryGetProperty("recommendation", out var re) ? re.GetString() : null, score);
                    var rationale = el.TryGetProperty("rationale", out var ra) ? ra.GetString() ?? "" : "";
                    scores[id] = new CandidateScore(id, byId[id].Name, score, rec, rationale);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Screening parse failed; using heuristic."); }
        }

        // Ensure every candidate is represented — fill any the LLM omitted with a heuristic score.
        foreach (var c in input.Candidates)
        {
            if (scores.ContainsKey(c.CandidateId)) continue;
            var score = HeuristicScore(c, input.Requirements);
            scores[c.CandidateId] = new CandidateScore(c.CandidateId, c.Name, score, NormalizeRec(null, score),
                "Heuristic estimate (experience & education).");
        }
        if (scores.Values.All(s => s.Rationale.StartsWith("Heuristic")))
            { engine = "heuristic"; }

        var ranked = scores.Values.OrderByDescending(s => s.Score).ToList();
        return new ScreeningResult(ranked, engine ?? "heuristic", notes);
    }

    private static int HeuristicScore(CandidateForScreening c, string requirements)
    {
        var score = 40.0;
        score += Math.Min(30, (double)c.ExperienceYears * 4);              // up to +30 for experience
        score += c.EducationLevel.ToLowerInvariant() switch
        {
            var e when e.Contains("phd") => 15,
            var e when e.Contains("master") => 12,
            var e when e.Contains("bachelor") => 8,
            var e when e.Contains("diploma") => 4,
            _ => 0,
        };
        // crude skills overlap with requirement keywords
        if (!string.IsNullOrWhiteSpace(requirements) && !string.IsNullOrWhiteSpace(c.Tags))
        {
            var reqWords = requirements.ToLowerInvariant().Split(new[] { ' ', ',', '\n', ';', '/' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var hits = c.Tags.ToLowerInvariant().Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Count(t => reqWords.Contains(t));
            score += Math.Min(15, hits * 5);
        }
        return (int)Math.Clamp(score, 0, 100);
    }

    private static string NormalizeRec(string? raw, int score)
    {
        var r = (raw ?? "").Trim().ToLowerInvariant();
        if (r is "shortlist" or "maybe" or "reject")
            return char.ToUpper(r[0]) + r[1..];
        return score >= 70 ? "Shortlist" : score >= 50 ? "Maybe" : "Reject";
    }

    // ── Interview questions ─────────────────────────────────────────────────

    public async Task<InterviewQuestionsResult> GenerateInterviewQuestionsAsync(InterviewQuestionsRequest req, CancellationToken ct)
    {
        var sys = "You are an interview panel lead. Output ONLY JSON: " +
                  "{\"categories\":[{\"category\":\"\",\"questions\":[\"\"]}]}. " +
                  "Use 3-4 categories (e.g. Technical, Behavioural, Role-specific, Culture-fit), 3-5 questions each.";
        var sb = new StringBuilder();
        sb.AppendLine($"Generate interview questions for: {req.Title}.");
        if (!string.IsNullOrWhiteSpace(req.SeniorityLevel)) sb.AppendLine($"Seniority: {req.SeniorityLevel}.");
        if (!string.IsNullOrWhiteSpace(req.Notes)) sb.AppendLine($"Notes: {req.Notes}.");
        sb.AppendLine("Return ONLY the JSON object.");

        var (json, engine) = await CallAsync(sys, sb.ToString(), ct);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<QuestionCategory>();
                    foreach (var c in cats.EnumerateArray())
                    {
                        var name = c.TryGetProperty("category", out var n) ? n.GetString() ?? "General" : "General";
                        var qs = StrList(c, "questions");
                        if (qs.Count > 0) list.Add(new QuestionCategory(name, qs));
                    }
                    if (list.Count > 0) return new InterviewQuestionsResult(list, $"{engine}+guardrails");
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Interview-question parse failed; using template."); }
        }

        return new InterviewQuestionsResult(new()
        {
            new("Role-specific", new() { $"Walk me through your experience relevant to a {req.Title} role.", "Describe a challenging problem you solved in this area.", "What tools or methods do you rely on day-to-day?" }),
            new("Behavioural", new() { "Tell me about a time you handled conflicting priorities.", "Describe a situation where you had to learn something quickly.", "How do you handle feedback?" }),
            new("Culture-fit", new() { "What kind of work environment helps you do your best work?", "Why are you interested in this role?" }),
        }, "template");
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    private static List<string> StrList(JsonElement el, string name)
    {
        var list = new List<string>();
        if (el.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    list.Add(item.GetString()!.Trim());
        return list;
    }

    /// <summary>Calls the LLM and returns the first JSON snippet ([..] or {..}) plus the engine label,
    /// or (null, "fallback") when no provider is configured or the call fails.</summary>
    private async Task<(string? Json, string Engine)> CallAsync(string system, string user, CancellationToken ct)
    {
        var provider = ResolveProvider();
        if (provider == "fallback") return (null, "fallback");
        try
        {
            var res = await _llm.CompleteAsync(new LlmRequest(provider, ResolveModel(provider), system, user, 2000), ct);
            if (!res.Success || string.IsNullOrWhiteSpace(res.Text)) return (null, provider);
            var text = res.Text;
            var a = text.IndexOf('['); var o = text.IndexOf('{');
            int start = (a < 0) ? o : (o < 0) ? a : Math.Min(a, o);
            char close = (start == a) ? ']' : '}';
            var end = text.LastIndexOf(close);
            if (start < 0 || end <= start) return (null, provider);
            return (text.Substring(start, end - start + 1), provider);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recruitment LLM call threw.");
            return (null, provider);
        }
    }

    private string ResolveProvider()
    {
        var c = _options.EffectiveProvider;
        if (c == "anthropic" && !string.IsNullOrWhiteSpace(_options.AnthropicApiKey)) return "anthropic";
        if (c == "openai" && !string.IsNullOrWhiteSpace(_options.OpenAIApiKey)) return "openai";
        if (c == "ollama" && !string.IsNullOrWhiteSpace(_options.OllamaBaseUrl)) return "ollama";
        if (!string.IsNullOrWhiteSpace(_options.AnthropicApiKey)) return "anthropic";
        if (!string.IsNullOrWhiteSpace(_options.OpenAIApiKey)) return "openai";
        if (!string.IsNullOrWhiteSpace(_options.OllamaBaseUrl)) return "ollama";
        return "fallback";
    }

    private string ResolveModel(string provider)
    {
        if (!string.IsNullOrWhiteSpace(_options.Model)) return _options.Model;
        return provider switch { "anthropic" => "claude-sonnet-4-20250514", "openai" => "gpt-5", "ollama" => "llama3.1", _ => "" };
    }
}
