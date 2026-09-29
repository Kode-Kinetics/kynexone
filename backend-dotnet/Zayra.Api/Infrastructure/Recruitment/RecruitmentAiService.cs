using System.Diagnostics;
using System.Globalization;
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
///
/// <para>THREE THINGS THIS FILE GETS WRONG NO LONGER:</para>
/// <list type="number">
/// <item>It asked a reasoning model for JSON without setting the provider's JSON mode, then
/// recovered the object by scanning for the first '[' or '{'. That is the exact fragility that made
/// the setup assistant fall back on every single request against this same model
/// (deepseek-v4-pro:cloud). Every call now passes <c>RequireJson: true</c> (Contract A).</item>
/// <item>It asked for 2000 output tokens. A reasoning model spends its output budget on
/// chain-of-thought BEFORE the answer begins; when the budget runs out mid-reasoning the provider
/// returns empty content and the feature degrades forever, silently (Contract D).</item>
/// <item>It recorded nothing — no usage row, no cost record — for any of its three model calls,
/// which is why a permanently degraded feature could stay invisible. Every call, including the
/// "no provider configured" case where no call is made, now goes through
/// <see cref="IAiCallRecorder"/> exactly once (Contract C).</item>
/// </list>
///
/// <para>Degrade reasons are kept DISTINGUISHABLE (Contract B): "not configured", "answered badly",
/// "timed out" and "reply unreadable" are four different operational problems and collapsing them
/// into one generic banner is what cost two days of debugging.</para>
/// </summary>
public sealed class RecruitmentAiService : IRecruitmentAiService
{
    /// <summary>Stable module key shared with the other recorded call sites.</summary>
    private const string Module = "recruitment";

    private const string IntentJobDescription = "job_description";
    private const string IntentScreening = "candidate_screening";
    private const string IntentInterviewQuestions = "interview_questions";

    // ── Contract D: output budgets sized for a reasoning model ──────────────
    // The JSON these prompts ask for is small (roughly 400-700 tokens for a job description or a
    // question set). The budget is NOT sized for the answer; it is sized for the reasoning that
    // precedes it. deepseek-v4-pro:cloud emits thousands of characters of `thinking` first, and a
    // budget consumed there yields done_reason=length with empty content — a total, silent
    // degrade. The setup assistant moved 4000 -> 8000 for this reason; these prompts are smaller,
    // so 6000 buys the same headroom without inviting a runaway generation.
    private const int JsonBudgetTokens = 6000;

    // Screening is the one prompt whose OUTPUT grows with the input: one object per candidate,
    // ~60 tokens each with its rationale. Base headroom for reasoning plus per-candidate room,
    // capped so a 500-candidate opening cannot ask for an unbounded generation.
    private const int ScreeningBaseBudgetTokens = 4000;
    private const int ScreeningTokensPerCandidate = 250;
    private const int ScreeningMaxBudgetTokens = 16000;

    private readonly ILlmClient _llm;
    private readonly IAiCallRecorder _recorder;
    private readonly AiOptions _options;
    private readonly ILogger<RecruitmentAiService> _logger;

    public RecruitmentAiService(
        ILlmClient llm,
        IAiCallRecorder recorder,
        AiOptions options,
        ILogger<RecruitmentAiService> logger)
    {
        _llm = llm;
        _recorder = recorder;
        _options = options;
        _logger = logger;
    }

    // ── Job description ─────────────────────────────────────────────────────

    public async Task<JobDescriptionResult> GenerateJobDescriptionAsync(
        RecruitmentAiCaller caller, JobDescriptionRequest req, CancellationToken ct)
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

        var attempt = await CallAsync(sys, sb.ToString(), JsonBudgetTokens, ct);
        // Structural only. The prompt carries the recruiter's free-text notes, so no part of it is
        // echoed into the record (Contract C).
        var promptSummary = $"job description draft for 1 opening ({attempt.PromptChars} chars of role detail)";

        string? parseFailure = null;
        if (attempt.Json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(attempt.Json);
                var r = doc.RootElement;
                var summary = Str(r, "summary");
                var resp = StrList(r, "responsibilities");
                var reqs = StrList(r, "requirements");
                if (!string.IsNullOrWhiteSpace(summary) || resp.Count > 0 || reqs.Count > 0)
                {
                    await RecordAsync(caller, attempt, IntentJobDescription, promptSummary, null, ct);
                    return new JobDescriptionResult(summary, resp, reqs, $"{attempt.Provider}+guardrails");
                }
                parseFailure = "the AI reply parsed as JSON but held no job-description fields";
            }
            // Deliberately broad: a model is free to answer {"summary": 42} and the reader must
            // degrade, not 500. No await runs inside the block, so no cancellation is swallowed.
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JD parse failed; using template.");
                parseFailure = $"the AI reply could not be read as a job description ({ex.Message})";
            }
        }

        await RecordAsync(caller, attempt, IntentJobDescription, promptSummary,
            parseFailure ?? attempt.FailureReason, ct);

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
            TemplateEngine(attempt, parseFailure));
    }

    // ── Screening (advisory) ────────────────────────────────────────────────

    public async Task<ScreeningResult> ScreenAsync(
        RecruitmentAiCaller caller, ScreeningInput input, CancellationToken ct)
    {
        var notes = new List<string> { "Advisory only — scores are heuristic estimates; no candidate is auto-rejected." };
        if (input.Candidates.Count == 0)
            return new ScreeningResult(new(), "n/a", new() { "No active candidates to screen for this opening." });

        var byId = input.Candidates.ToDictionary(c => c.CandidateId);
        var scores = new Dictionary<Guid, CandidateScore>();

        // Short opaque references instead of GUIDs: fewer tokens, and a reasoning model that
        // mis-transcribes one character of a GUID silently drops that candidate to a heuristic
        // score. Names are NOT sent — scoring does not need them, sending them would export
        // personal data to the provider for no gain, and withholding them removes a name-based
        // bias channel from an advisory that recruiters act on.
        var byRef = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var c in input.Candidates) byRef[$"c{++index}"] = c.CandidateId;

        var sys = "You are a recruiter screening candidates against a role. Output ONLY a JSON object: " +
                  "{\"candidates\":[{\"ref\":\"<ref>\",\"score\":<0-100>,\"recommendation\":\"Shortlist|Maybe|Reject\",\"rationale\":\"<1 sentence>\"}]}. " +
                  "Use the exact ref given for each candidate. Score on fit to the role's requirements. Be fair and objective.";
        var sb = new StringBuilder();
        sb.AppendLine($"ROLE: {input.JobTitle}");
        if (!string.IsNullOrWhiteSpace(input.Description)) sb.AppendLine($"DESCRIPTION: {input.Description}");
        if (!string.IsNullOrWhiteSpace(input.Requirements)) sb.AppendLine($"REQUIREMENTS: {input.Requirements}");
        sb.AppendLine("CANDIDATES:");
        foreach (var (reference, id) in byRef)
        {
            var c = byId[id];
            sb.AppendLine($"- ref={reference} | current role: {c.CurrentJobTitle} | " +
                          $"{c.ExperienceYears.ToString("0.#", CultureInfo.InvariantCulture)}y exp | " +
                          $"{c.EducationLevel} | skills: {c.Tags}");
        }
        sb.AppendLine("Return ONLY the JSON object, one entry per candidate ref.");

        var budget = Math.Min(
            ScreeningMaxBudgetTokens,
            ScreeningBaseBudgetTokens + (input.Candidates.Count * ScreeningTokensPerCandidate));
        var attempt = await CallAsync(sys, sb.ToString(), budget, ct);
        // Counts only: the prompt carries candidate experience, education and skills.
        var promptSummary = $"screening {input.Candidates.Count} candidate(s) against 1 opening (no names sent)";

        string? parseFailure = null;
        if (attempt.Json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(attempt.Json);
                var rows = ScreeningRows(doc.RootElement);
                if (rows is null)
                {
                    parseFailure = "the AI reply parsed as JSON but held no candidate array";
                }
                else
                {
                    foreach (var el in rows.Value.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object) continue;
                        var id = ResolveCandidate(el, byRef);
                        if (id is null || !byId.ContainsKey(id.Value)) continue;
                        var score = Math.Clamp(ReadScore(el), 0, 100);
                        var rec = NormalizeRec(Str(el, "recommendation"), score);
                        var rationale = Str(el, "rationale");
                        scores[id.Value] = new CandidateScore(id.Value, byId[id.Value].Name, score, rec, rationale);
                    }
                    if (scores.Count == 0)
                        parseFailure = "the AI reply scored none of the candidates sent";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Screening parse failed; using heuristic.");
                parseFailure = $"the AI reply could not be read as candidate scores ({ex.Message})";
            }
        }

        var aiScored = scores.Count;

        // Ensure every candidate is represented — fill any the LLM omitted with a heuristic score.
        foreach (var c in input.Candidates)
        {
            if (scores.ContainsKey(c.CandidateId)) continue;
            var score = HeuristicScore(c, input.Requirements);
            scores[c.CandidateId] = new CandidateScore(c.CandidateId, c.Name, score, NormalizeRec(null, score),
                "Heuristic estimate (experience & education).");
        }

        await RecordAsync(caller, attempt, IntentScreening, promptSummary,
            aiScored > 0 ? parseFailure : (parseFailure ?? attempt.FailureReason), ct);

        string engine;
        if (aiScored == 0)
        {
            engine = "heuristic";
            // Exception-first, and specific: which of the four problems actually happened.
            notes.Add(FallbackNote(attempt, parseFailure));
        }
        else
        {
            engine = $"{attempt.Provider}+guardrails";
            if (aiScored < input.Candidates.Count)
                notes.Add($"{input.Candidates.Count - aiScored} of {input.Candidates.Count} candidates were not returned by the AI; those rows are heuristic estimates.");
        }

        var ranked = scores.Values.OrderByDescending(s => s.Score).ToList();
        return new ScreeningResult(ranked, engine, notes);
    }

    private static JsonElement? ScreeningRows(JsonElement root)
    {
        // JSON mode returns an object, so the contract asks for {"candidates":[...]}. A bare array
        // is still accepted: that is what the previous prompt asked for, and a model that ignores
        // the wrapper should not cost the recruiter their screening.
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "candidates", "results", "scores" })
            if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr;
        return null;
    }

    private static Guid? ResolveCandidate(JsonElement el, IReadOnlyDictionary<string, Guid> byRef)
    {
        foreach (var name in new[] { "ref", "candidateId", "id" })
        {
            if (!el.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var raw = (value.GetString() ?? string.Empty).Trim();
            if (raw.Length == 0) continue;
            if (byRef.TryGetValue(raw, out var mapped)) return mapped;
            if (Guid.TryParse(raw, out var parsed)) return parsed;
        }
        return null;
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

    public async Task<InterviewQuestionsResult> GenerateInterviewQuestionsAsync(
        RecruitmentAiCaller caller, InterviewQuestionsRequest req, CancellationToken ct)
    {
        var sys = "You are an interview panel lead. Output ONLY JSON: " +
                  "{\"categories\":[{\"category\":\"\",\"questions\":[\"\"]}]}. " +
                  "Use 3-4 categories (e.g. Technical, Behavioural, Role-specific, Culture-fit), 3-5 questions each.";
        var sb = new StringBuilder();
        sb.AppendLine($"Generate interview questions for: {req.Title}.");
        if (!string.IsNullOrWhiteSpace(req.SeniorityLevel)) sb.AppendLine($"Seniority: {req.SeniorityLevel}.");
        if (!string.IsNullOrWhiteSpace(req.Notes)) sb.AppendLine($"Notes: {req.Notes}.");
        sb.AppendLine("Return ONLY the JSON object.");

        var attempt = await CallAsync(sys, sb.ToString(), JsonBudgetTokens, ct);
        var promptSummary = $"interview questions for 1 opening ({attempt.PromptChars} chars of role detail)";

        string? parseFailure = null;
        if (attempt.Json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(attempt.Json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("categories", out var cats)
                    && cats.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<QuestionCategory>();
                    foreach (var c in cats.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Object) continue;
                        var name = Str(c, "category");
                        if (string.IsNullOrWhiteSpace(name)) name = "General";
                        var qs = StrList(c, "questions");
                        if (qs.Count > 0) list.Add(new QuestionCategory(name, qs));
                    }
                    if (list.Count > 0)
                    {
                        await RecordAsync(caller, attempt, IntentInterviewQuestions, promptSummary, null, ct);
                        return new InterviewQuestionsResult(list, $"{attempt.Provider}+guardrails");
                    }
                }
                parseFailure ??= "the AI reply parsed as JSON but held no question categories";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Interview-question parse failed; using template.");
                parseFailure = $"the AI reply could not be read as interview questions ({ex.Message})";
            }
        }

        await RecordAsync(caller, attempt, IntentInterviewQuestions, promptSummary,
            parseFailure ?? attempt.FailureReason, ct);

        return new InterviewQuestionsResult(new()
        {
            new("Role-specific", new() { $"Walk me through your experience relevant to a {req.Title} role.", "Describe a challenging problem you solved in this area.", "What tools or methods do you rely on day-to-day?" }),
            new("Behavioural", new() { "Tell me about a time you handled conflicting priorities.", "Describe a situation where you had to learn something quickly.", "How do you handle feedback?" }),
            new("Culture-fit", new() { "What kind of work environment helps you do your best work?", "Why are you interested in this role?" }),
        }, TemplateEngine(attempt, parseFailure));
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    /// <summary>A string property, or "" — never a throw. <c>JsonElement.GetString()</c> throws on
    /// any other kind, and a model is entitled to answer {"rationale": 7}.</summary>
    private static string Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>A 0-100 score however the model spelled it: 85, 85.5 or "85".</summary>
    private static int ReadScore(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("score", out var sc)) return 0;
        if (sc.ValueKind == JsonValueKind.Number)
            return sc.TryGetInt32(out var whole) ? whole
                 : sc.TryGetDouble(out var fraction) ? (int)Math.Round(fraction) : 0;
        if (sc.ValueKind == JsonValueKind.String
            && double.TryParse(sc.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return (int)Math.Round(parsed);
        return 0;
    }

    private static List<string> StrList(JsonElement el, string name)
    {
        var list = new List<string>();
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    list.Add(item.GetString()!.Trim());
        return list;
    }

    /// <summary>Why the deterministic path was taken, in one of FOUR distinguishable shapes
    /// (Contract B). "AI provider unavailable" for all four is the defect this replaces.</summary>
    private enum AiOutcome
    {
        /// <summary>The provider answered and its reply yielded a JSON span.</summary>
        Success,
        /// <summary>No provider is configured, so no call was made.</summary>
        NotConfigured,
        /// <summary>The provider was called and failed — including an empty completion.</summary>
        ProviderFailed,
        /// <summary>The provider did not answer inside the HTTP timeout.</summary>
        TimedOut,
        /// <summary>The provider answered with text that holds no JSON at all.</summary>
        Unparseable,
    }

    /// <param name="Request">Null when no provider was configured and no call was attempted.</param>
    /// <param name="Response">Null when the call never returned (timeout, transport failure).</param>
    private sealed record LlmAttempt(
        string? Json,
        string Provider,
        AiOutcome Outcome,
        string? FailureReason,
        LlmRequest? Request,
        LlmResponse? Response,
        int ElapsedMs,
        int PromptChars);

    /// <summary>Calls the LLM and returns the first JSON snippet ([..] or {..}) plus everything the
    /// recorder needs. Never throws for a provider problem; only a caller cancellation propagates.</summary>
    private async Task<LlmAttempt> CallAsync(string system, string user, int maxOutputTokens, CancellationToken ct)
    {
        var provider = ResolveProvider();
        if (provider == "fallback")
        {
            // Still recorded by the caller: "no provider configured" is the one degrade that leaves
            // no provider-side trace at all, so the row is the only evidence it happened.
            return new LlmAttempt(null, "fallback", AiOutcome.NotConfigured,
                "No AI provider is configured for this deployment, so no model call was made.",
                null, null, 0, user.Length);
        }

        // Contract A: the provider constrains the output to JSON. Without it a reasoning model may
        // wrap the object in prose or a ```json fence and the brace-span scan below picks up a
        // fragment — or nothing.
        var request = new LlmRequest(provider, ResolveModel(provider), system, user, maxOutputTokens, RequireJson: true);
        var timer = Stopwatch.StartNew();
        try
        {
            var res = await _llm.CompleteAsync(request, ct);
            var elapsed = (int)timer.ElapsedMilliseconds;

            // Contract B: an empty completion now arrives as Success:false with a reason that names
            // the real cause (including a budget consumed mid-reasoning). Pass it through verbatim
            // rather than inventing a generic one.
            if (!res.Success || string.IsNullOrWhiteSpace(res.Text))
            {
                var why = string.IsNullOrWhiteSpace(res.Error)
                    ? "the AI provider returned no usable text"
                    : res.Error!;
                return new LlmAttempt(null, provider, AiOutcome.ProviderFailed, why, request, res, elapsed, user.Length);
            }

            var json = ExtractJson(res.Text);
            if (json is null)
            {
                return new LlmAttempt(null, provider, AiOutcome.Unparseable,
                    $"the AI reply contained no JSON object or array ({res.Text.Length} chars of text)",
                    request, res, elapsed, user.Length);
            }

            return new LlmAttempt(json, provider, AiOutcome.Success, null, request, res, elapsed, user.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The HTTP request or the user went away. Not a provider failure, and not ours to
            // swallow — a cancelled request must not be recorded as a degraded AI answer.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient's 120s timeout (configured in Program.cs) surfaces as a cancellation with
            // OUR token still un-cancelled. Distinguishable from a bad answer, per Contract B.
            _logger.LogWarning(ex, "Recruitment LLM call timed out after {ElapsedMs}ms.", timer.ElapsedMilliseconds);
            return new LlmAttempt(null, provider, AiOutcome.TimedOut,
                $"the AI provider did not answer within the request timeout ({timer.ElapsedMilliseconds}ms)",
                request, null, (int)timer.ElapsedMilliseconds, user.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recruitment LLM call threw.");
            return new LlmAttempt(null, provider, AiOutcome.ProviderFailed,
                $"the AI call failed: {ex.Message}", request, null, (int)timer.ElapsedMilliseconds, user.Length);
        }
    }

    /// <summary>Recovers the JSON span, tolerating a markdown fence around it.</summary>
    private static string? ExtractJson(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = t.IndexOf('\n');
            var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (firstBreak > 0 && lastFence > firstBreak) t = t[(firstBreak + 1)..lastFence].Trim();
        }

        var array = t.IndexOf('[');
        var obj = t.IndexOf('{');
        var start = (array < 0) ? obj : (obj < 0) ? array : Math.Min(array, obj);
        if (start < 0) return null;
        var close = t[start] == '[' ? ']' : '}';
        var end = t.LastIndexOf(close);
        return end <= start ? null : t.Substring(start, end - start + 1);
    }

    /// <summary>Contract C: exactly one record per model call, on success and on every failure,
    /// including the no-call case. Bookkeeping must never fail the recruiter's request.</summary>
    private async Task RecordAsync(
        RecruitmentAiCaller caller,
        LlmAttempt attempt,
        string intent,
        string promptSummary,
        string? failureReason,
        CancellationToken ct)
    {
        try
        {
            await _recorder.RecordAsync(new AiCallRecord(
                caller.TenantId,
                caller.UserId,
                caller.UserRole,
                Module,
                intent,
                promptSummary,
                attempt.Request,
                attempt.Response,
                attempt.ElapsedMs,
                failureReason), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record the {Module}/{Intent} AI call.", Module, intent);
        }
    }

    /// <summary>The badge the recruiter sees. Four outcomes, four labels — short enough for the
    /// pill the UI renders it in, specific enough to send an operator to the right place.</summary>
    private static string TemplateEngine(LlmAttempt attempt, string? parseFailure)
    {
        if (parseFailure is not null) return "template (AI reply unreadable)";
        return attempt.Outcome switch
        {
            AiOutcome.NotConfigured => "template (AI not configured)",
            AiOutcome.TimedOut => "template (AI timed out)",
            AiOutcome.Unparseable => "template (AI reply unreadable)",
            _ => "template (AI returned nothing usable)",
        };
    }

    /// <summary>The same four outcomes as a sentence, for the screening panel's notes list.</summary>
    private static string FallbackNote(LlmAttempt attempt, string? parseFailure)
    {
        if (parseFailure is not null)
            return "The AI reply could not be read as candidate scores, so every score below is a heuristic estimate.";
        return attempt.Outcome switch
        {
            AiOutcome.NotConfigured => "No AI provider is configured, so every score below is a heuristic estimate.",
            AiOutcome.TimedOut => "The AI provider did not answer in time, so every score below is a heuristic estimate.",
            AiOutcome.Unparseable => "The AI reply could not be read as candidate scores, so every score below is a heuristic estimate.",
            _ => "The AI provider did not return usable scores, so every score below is a heuristic estimate.",
        };
    }

    private string ResolveProvider()
    {
        var c = _options.EffectiveProvider;
        if (c == "anthropic" && !string.IsNullOrWhiteSpace(_options.AnthropicApiKey)) return "anthropic";
        if (c == "openai" && !string.IsNullOrWhiteSpace(_options.OpenAIApiKey)) return "openai";
        if (c == "ollama" && !string.IsNullOrWhiteSpace(_options.OllamaBaseUrl)) return "ollama";
        // NO CROSS-PROVIDER FALLBACK. If the configured provider is not usable, degrade to the
        // deterministic path — never quietly send this tenant's data to a different vendor.
        // This tail used to read "any key will do": AI_PROVIDER=ollama with an unset
        // OLLAMA_BASE_URL and a stray ANTHROPIC_API_KEY in the environment routed HR data to
        // Anthropic. No operator chose that, nothing recorded it, and the published privacy
        // policy names Ollama specifically — so it would also have made that page false.
        // AGENTS.md: "External AI is an exception, not the default"; no silent cloud fallback.
        return "fallback";
    }

    private string ResolveModel(string provider)
    {
        if (!string.IsNullOrWhiteSpace(_options.Model)) return _options.Model;
        return provider switch { "anthropic" => "claude-sonnet-4-20250514", "openai" => "gpt-5", "ollama" => "llama3.1", _ => "" };
    }
}
