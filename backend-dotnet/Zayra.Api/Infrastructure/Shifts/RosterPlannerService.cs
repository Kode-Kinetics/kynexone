using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Shifts;
using Zayra.Api.Infrastructure.AI;

namespace Zayra.Api.Infrastructure.Shifts;

/// <summary>
/// Hybrid roster planner: the configured LLM (Ollama by default) proposes an intelligent plan,
/// then deterministic guardrails enforce the hard rules (gender→shift, one shift/day, rest hours,
/// max consecutive days, weekend/holiday demand) and repair anything the model got wrong. If the
/// LLM is unavailable or returns garbage, it degrades gracefully to a fully deterministic plan.
///
/// <para>Two defects that broke the setup assistant against the same reasoning model applied here
/// verbatim: the reply is parsed as JSON but no JSON constraint was sent, and there was no timeout,
/// so an unreachable Ollama host stalled the request for HttpClient's 100s default. A third was
/// worse: nothing was recorded, so a permanently degraded planner would have looked identical to a
/// working one from every log, dashboard and usage total. Every exit from the model step now writes
/// exactly one <see cref="AiCallRecord"/> and names which failure it was.</para>
/// </summary>
public sealed class RosterPlannerService : IRosterPlannerService
{
    private readonly ILlmClient _llm;
    private readonly AiOptions _options;
    private readonly IAiCallRecorder _recorder;
    private readonly ILogger<RosterPlannerService> _logger;

    private const string Module = "roster";
    private const string Intent = "shift_roster_plan";

    /// <summary>How long the planner waits for a model before falling back. Deliberately shorter
    /// than HttpClient's 100s ceiling so the degrade is ours and explicable rather than a socket
    /// timeout surfacing as a generic error — and short enough that an HR Manager staring at the
    /// roster screen is not left waiting on a host that will never answer.</summary>
    private static readonly TimeSpan LlmBudget = TimeSpan.FromSeconds(75);

    /// <summary>Output budget. Was 4000. A reasoning model (deepseek-v4-pro, the production model)
    /// spends part of the budget thinking before it emits a token of the answer, and when the
    /// budget runs out mid-reasoning the provider returns an empty completion — the exact failure
    /// that made the setup assistant fall back on every single request. A roster is also long:
    /// employees × days objects. 8000 leaves room for both.</summary>
    private const int LlmOutputTokens = 8000;

    public RosterPlannerService(
        ILlmClient llm,
        AiOptions options,
        IAiCallRecorder recorder,
        ILogger<RosterPlannerService> logger)
    {
        _llm = llm;
        _options = options;
        _recorder = recorder;
        _logger = logger;
    }

    public async Task<RosterPlanResult> PlanAsync(RosterPlanInput input, CancellationToken ct)
    {
        var warnings = new List<string>();

        // 1. Ask the LLM for hints (best effort). Map of (employeeId, date) -> shiftCode.
        var (hints, engine, degradeWarning) = await TryLlmPlanAsync(input, ct);

        // First, so the reader learns WHY the plan is rules-only before reading its caveats.
        if (!string.IsNullOrEmpty(degradeWarning))
            warnings.Add(degradeWarning);

        // 2. Build the authoritative plan deterministically, seeded by the LLM hints.
        var assignments = BuildDeterministicPlan(input, hints, warnings);

        var summary =
            $"{assignments.Count} shift(s) planned for {input.Employees.Count} employee(s) " +
            $"over {(input.DateTo.DayNumber - input.DateFrom.DayNumber + 1)} day(s) " +
            $"using {engine}.";

        return new RosterPlanResult(assignments, warnings, engine, summary);
    }

    // ── LLM step ──────────────────────────────────────────────────────────────

    /// <returns>
    /// The hints, the engine badge, and a human-readable warning when a CONFIGURED provider failed.
    /// The five outcomes stay distinguishable on purpose: "LLM unavailable" previously covered a
    /// provider that was up and answering, a provider that was never configured, a host that never
    /// replied and a reply nobody could parse. That one string cost two days of debugging on the
    /// setup assistant, because it pointed at infrastructure when the fault was in the request.
    /// </returns>
    private async Task<(Dictionary<(int, DateOnly), string> Hints, string Engine, string Warning)> TryLlmPlanAsync(
        RosterPlanInput input, CancellationToken ct)
    {
        var empty = new Dictionary<(int, DateOnly), string>();
        var summary = DescribeRequest(input);
        var provider = ResolveProvider();

        if (provider == "fallback")
        {
            // Recorded even though no call was made: "AI was never asked" and "AI was asked and
            // failed" must be answerable from the same table, or a misconfigured tenant looks
            // exactly like a working one.
            await RecordAsync(input, summary, null, null, 0,
                "no AI provider configured", CancellationToken.None);
            return (empty, "deterministic (no AI provider configured)", string.Empty);
        }

        var request = new LlmRequest(
            provider, ResolveModel(provider), BuildSystemPrompt(), BuildUserPrompt(input),
            LlmOutputTokens, RequireJson: true);

        // No timeout at all was the third defect. A linked source keeps caller cancellation
        // authoritative while capping how long we wait on the provider.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(LlmBudget);
        var timer = Stopwatch.StartNew();

        try
        {
            var response = await _llm.CompleteAsync(request, budget.Token);
            timer.Stop();

            // Contract: Success==false (which now includes an empty completion, with a reason that
            // says whether the token budget went on reasoning) means degrade to the rules engine.
            if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
            {
                var reason = $"The AI provider ({provider}) did not return a usable plan: {Truncate(response.Error)}. The roster was planned by the rules engine.";
                _logger.LogWarning("Roster LLM call failed ({Provider}/{Model}): {Error}", provider, request.Model, response.Error);
                await RecordAsync(input, summary, request, response, (int)timer.ElapsedMilliseconds,
                    $"provider returned no usable plan: {Truncate(response.Error)}", CancellationToken.None);
                return (empty, $"deterministic ({provider} returned no usable plan)", reason);
            }

            var parsed = ParseHints(response.Text, input);
            if (parsed.Count == 0)
            {
                var reason = $"The AI provider ({provider}) replied, but the reply could not be read as roster assignments. The roster was planned by the rules engine.";
                _logger.LogWarning("Roster LLM reply was unparseable ({Provider}/{Model}), {Length} chars.",
                    provider, request.Model, response.Text.Length);
                await RecordAsync(input, summary, request, response, (int)timer.ElapsedMilliseconds,
                    "reply could not be parsed as roster assignments", CancellationToken.None);
                return (empty, $"deterministic ({provider} reply unreadable)", reason);
            }

            await RecordAsync(input, summary, request, response, (int)timer.ElapsedMilliseconds, null, CancellationToken.None);
            return (parsed, $"{provider}+guardrails", string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up (client disconnect / shutdown). That is not a provider failure and
            // must not be dressed up as a plan — but the call still happened, so it is still recorded.
            timer.Stop();
            await RecordAsync(input, summary, request, null, (int)timer.ElapsedMilliseconds,
                "cancelled by the caller before the provider replied", CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException)
        {
            timer.Stop();
            var reason = $"The AI provider ({provider}) did not respond within {LlmBudget.TotalSeconds:0}s. The roster was planned by the rules engine.";
            _logger.LogWarning("Roster LLM call exceeded the {Seconds}s budget ({Provider}).", LlmBudget.TotalSeconds, provider);
            await RecordAsync(input, summary, request, null, (int)timer.ElapsedMilliseconds,
                $"timed out after {LlmBudget.TotalSeconds:0}s", CancellationToken.None);
            return (empty, $"deterministic ({provider} timed out)", reason);
        }
        catch (Exception ex)
        {
            timer.Stop();
            var reason = $"The AI provider ({provider}) could not be reached: {Truncate(ex.Message)}. The roster was planned by the rules engine.";
            _logger.LogWarning(ex, "Roster LLM planning threw; using deterministic fallback.");
            await RecordAsync(input, summary, request, null, (int)timer.ElapsedMilliseconds,
                $"provider call threw: {Truncate(ex.Message)}", CancellationToken.None);
            return (empty, $"deterministic ({provider} unreachable)", reason);
        }
    }

    /// <summary>
    /// The usage record's description of the request. Counts only: the prompt itself carries the
    /// tenant's shift pattern and every selected employee's id, gender and department, none of
    /// which belongs in an audit row that admins and support can read.
    /// </summary>
    private static string DescribeRequest(RosterPlanInput input)
    {
        var days = input.DateTo.DayNumber - input.DateFrom.DayNumber + 1;
        return $"Roster plan: {input.Employees.Count} employee(s), {days} day(s), {input.Shifts.Count} shift(s), " +
               $"{input.Holidays.Count} holiday(s), {input.Policy.GenderRules.Count} gender rule(s), " +
               $"min rest {input.Policy.MinRestHours}h, max {input.Policy.MaxConsecutiveDays} consecutive day(s).";
    }

    private Task RecordAsync(
        RosterPlanInput input, string summary, LlmRequest? request, LlmResponse? response,
        int elapsedMs, string? failureReason, CancellationToken ct)
        => _recorder.RecordAsync(new AiCallRecord(
            input.Caller.TenantId,
            input.Caller.UserId,
            input.Caller.UserRole,
            Module,
            Intent,
            summary,
            request,
            response,
            elapsedMs,
            failureReason), ct);

    /// <summary>Provider error bodies reach an Admin/HR Manager screen — keep them short and flat.</summary>
    private static string Truncate(string? value, int max = 180)
    {
        if (string.IsNullOrWhiteSpace(value)) return "no detail reported";
        var flat = Regex.Replace(value.Trim(), @"\s+", " ");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    // A JSON OBJECT, not a bare array. `format:"json"` (Ollama) and `json_object` (OpenAI) both
    // constrain the model to a JSON object; asking a json_object-constrained model for a top-level
    // array gets you an object anyway, with the array buried under a name of its choosing.
    // ParseHints therefore accepts the wrapper, any single array property, or a bare array.
    private static string BuildSystemPrompt() =>
        "You are a workforce shift-rostering planner. You output ONLY a single JSON object, no prose. " +
        "Schema: {\"assignments\":[{\"employeeId\": <int>, \"date\": \"yyyy-MM-dd\", \"shiftCode\": \"<code>\"}]}. " +
        "Honour every rule you are given: gender→shift restrictions are mandatory, never assign more " +
        "than one shift to a person on a day, leave at least the stated rest hours between an employee's " +
        "consecutive shifts, never exceed the max consecutive working days, do not auto-assign voluntary " +
        "shifts, and on weekends/holidays only staff the stated demand. Distribute work fairly.";

    private static string BuildUserPrompt(RosterPlanInput input)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Plan from {input.DateFrom:yyyy-MM-dd} to {input.DateTo:yyyy-MM-dd}.");
        sb.AppendLine();
        sb.AppendLine("SHIFTS (code | name | start-end):");
        foreach (var s in input.Shifts)
            sb.AppendLine($"- {s.Code} | {s.Name} | {s.Start:HH\\:mm}-{s.End:HH\\:mm}");
        sb.AppendLine();
        // Deliberately WITHOUT names. The model answers with employeeId only, and gender and
        // department are the only attributes the rules key off, so a full staff roster of names
        // was being sent to an external provider for no gain. AGENTS.md: never send more than the
        // field the task needs.
        sb.AppendLine("EMPLOYEES (id | gender | department) — identified by id only:");
        foreach (var e in input.Employees)
            sb.AppendLine($"- {e.Id} | {e.Gender} | {e.Department}");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        foreach (var r in input.Policy.GenderRules)
            sb.AppendLine($"- {r.Mode} for gender {r.Gender}: shifts [{string.Join(", ", r.ShiftCodes)}]");
        if (input.Policy.VoluntaryShiftCodes.Count > 0)
            sb.AppendLine($"- voluntary (do not auto-assign): [{string.Join(", ", input.Policy.VoluntaryShiftCodes)}]");
        sb.AppendLine($"- minimum rest hours between shifts: {input.Policy.MinRestHours}");
        sb.AppendLine($"- max consecutive working days: {input.Policy.MaxConsecutiveDays}");
        if (input.Policy.WeekendDemand.Count > 0)
            sb.AppendLine($"- weekend demand: {string.Join(", ", input.Policy.WeekendDemand.Select(d => $"{d.ShiftCode}={d.Headcount}"))}");
        if (input.Policy.HolidayDemand.Count > 0)
            sb.AppendLine($"- holiday demand: {string.Join(", ", input.Policy.HolidayDemand.Select(d => $"{d.ShiftCode}={d.Headcount}"))}");
        if (input.Holidays.Count > 0)
            sb.AppendLine($"- holidays: {string.Join(", ", input.Holidays.OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd")))}");
        sb.AppendLine();
        sb.AppendLine("Return ONLY the JSON object described in the schema.");
        return sb.ToString();
    }

    private sealed record HintDto(int employeeId, string date, string shiftCode);

    private static readonly JsonSerializerOptions HintJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static Dictionary<(int, DateOnly), string> ParseHints(string text, RosterPlanInput input)
    {
        var result = new Dictionary<(int, DateOnly), string>();
        var items = ExtractHints(text);
        if (items is null) return result;

        var validEmployees = input.Employees.Select(e => e.Id).ToHashSet();
        var validCodes = input.Shifts.Select(s => s.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.shiftCode)) continue;
            if (!validEmployees.Contains(item.employeeId)) continue;
            if (!validCodes.Contains(item.shiftCode)) continue;
            if (!DateOnly.TryParse(item.date, out var d)) continue;
            if (d < input.DateFrom || d > input.DateTo) continue;
            result[(item.employeeId, d)] = validCodes.Comparer.Equals(item.shiftCode, item.shiftCode)
                ? input.Shifts.First(s => string.Equals(s.Code, item.shiftCode, StringComparison.OrdinalIgnoreCase)).Code
                : item.shiftCode;
        }
        return result;
    }

    /// <summary>
    /// Pulls the assignment list out of whatever shape the model produced: the documented
    /// {"assignments":[…]} object, an object that named the array something else, or a bare array
    /// (what an unconstrained model, or a provider without a JSON mode, tends to emit). Returns
    /// null when nothing in the reply is readable as a list of assignments — the caller reports
    /// that as "reply unreadable", which is a different fault from "the provider failed".
    /// </summary>
    private static List<HintDto>? ExtractHints(string text)
    {
        foreach (var json in CandidateSpans(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                JsonElement array;
                if (root.ValueKind == JsonValueKind.Array) array = root;
                else if (root.ValueKind != JsonValueKind.Object || !TryFindArray(root, out array)) continue;

                var items = JsonSerializer.Deserialize<List<HintDto>>(array.GetRawText(), HintJsonOptions);
                if (items is { Count: > 0 }) return items;
            }
            catch (JsonException)
            {
                // Try the next span rather than giving up: a fenced or prefaced reply often has a
                // parseable span even when the whole string is not JSON.
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateSpans(string text)
    {
        var objStart = text.IndexOf('{');
        var objEnd = text.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart) yield return text.Substring(objStart, objEnd - objStart + 1);

        var arrStart = text.IndexOf('[');
        var arrEnd = text.LastIndexOf(']');
        if (arrStart >= 0 && arrEnd > arrStart) yield return text.Substring(arrStart, arrEnd - arrStart + 1);
    }

    private static bool TryFindArray(JsonElement root, out JsonElement array)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Array && property.NameEquals("assignments"))
            {
                array = property.Value;
                return true;
            }
        foreach (var property in root.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                array = property.Value;
                return true;
            }
        array = default;
        return false;
    }

    // ── Deterministic guardrail builder ─────────────────────────────────────────

    private static List<ProposedAssignment> BuildDeterministicPlan(
        RosterPlanInput input,
        Dictionary<(int, DateOnly), string> hints,
        List<string> warnings)
    {
        var shiftByCode = input.Shifts.ToDictionary(s => s.Code, s => s, StringComparer.OrdinalIgnoreCase);
        var voluntary = input.Policy.VoluntaryShiftCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Per-employee scheduling state.
        var lastEnd = new Dictionary<int, DateTime>();      // end datetime of the employee's most recent shift
        var lastDay = new Dictionary<int, DateOnly>();      // most recent assigned day
        var streak = new Dictionary<int, int>();            // consecutive assigned days
        var load = new Dictionary<int, int>();              // total shifts assigned (fairness)
        foreach (var e in input.Employees) { load[e.Id] = 0; streak[e.Id] = 0; }

        var result = new List<ProposedAssignment>();
        var hintWarned = new HashSet<string>();

        // Allowed (non-voluntary) shifts for an employee, honouring gender rules.
        List<RosterPlanShift> AllowedShifts(RosterPlanEmployee emp)
        {
            var rule = input.Policy.GenderRules
                .FirstOrDefault(r => string.Equals(r.Gender, emp.Gender, StringComparison.OrdinalIgnoreCase));
            IEnumerable<RosterPlanShift> pool = input.Shifts.Where(s => !voluntary.Contains(s.Code));
            if (rule is not null && rule.ShiftCodes.Count > 0 &&
                string.Equals(rule.Mode, "required", StringComparison.OrdinalIgnoreCase))
            {
                var set = rule.ShiftCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
                pool = pool.Where(s => set.Contains(s.Code));
            }
            return pool.ToList();
        }

        bool PassesRest(int empId, DateOnly day, RosterPlanShift shift)
        {
            if (!lastEnd.TryGetValue(empId, out var prevEnd)) return true;
            var startDt = day.ToDateTime(shift.Start);
            return (startDt - prevEnd).TotalHours >= input.Policy.MinRestHours;
        }

        bool PassesConsecutive(int empId, DateOnly day)
        {
            if (!lastDay.TryGetValue(empId, out var prev)) return true;
            if (prev == day.AddDays(-1)) return streak[empId] < input.Policy.MaxConsecutiveDays;
            return true; // a gap resets the streak
        }

        DateTime EndDateTime(DateOnly day, RosterPlanShift shift)
        {
            // Night shifts whose end <= start roll over to the next day.
            var end = day.ToDateTime(shift.End);
            if (shift.End <= shift.Start) end = end.AddDays(1);
            return end;
        }

        void Commit(RosterPlanEmployee emp, DateOnly day, RosterPlanShift shift, string reason)
        {
            result.Add(new ProposedAssignment(emp.Id, emp.FullName, day, shift.Id, shift.Code, shift.Name, shift.Color, reason));
            lastEnd[emp.Id] = EndDateTime(day, shift);
            streak[emp.Id] = (lastDay.TryGetValue(emp.Id, out var prev) && prev == day.AddDays(-1)) ? streak[emp.Id] + 1 : 1;
            lastDay[emp.Id] = day;
            load[emp.Id]++;
        }

        // Try to assign a specific employee on a day, honouring the LLM hint first.
        bool TryAssign(RosterPlanEmployee emp, DateOnly day, IReadOnlyList<RosterPlanShift> allowed)
        {
            if (allowed.Count == 0) return false;
            if (!PassesConsecutive(emp.Id, day)) return false;

            // 1. Honour a valid LLM hint.
            if (hints.TryGetValue((emp.Id, day), out var hintCode) && shiftByCode.TryGetValue(hintCode, out var hinted))
            {
                if (allowed.Any(a => a.Id == hinted.Id) && PassesRest(emp.Id, day, hinted))
                {
                    Commit(emp, day, hinted, "AI suggestion");
                    return true;
                }
                var key = $"{emp.Id}:{day}";
                if (hintWarned.Add(key))
                    warnings.Add($"AI suggested a shift for {emp.FullName} on {day:yyyy-MM-dd} that violated a rule; the planner corrected it.");
            }

            // 2. Pick the least-loaded allowed shift that passes the rest rule.
            var pick = allowed
                .Where(s => PassesRest(emp.Id, day, s))
                .OrderBy(s => result.Count(r => r.Date == day && r.ShiftCode == s.Code)) // balance coverage across shifts
                .ThenBy(s => s.Start)
                .FirstOrDefault();
            if (pick is null) return false;
            Commit(emp, day, pick, "Auto-assigned");
            return true;
        }

        for (var day = input.DateFrom; day <= input.DateTo; day = day.AddDays(1))
        {
            var isHoliday = input.Holidays.Contains(day);
            var isWeekend = input.WeekendDays.Contains(day);

            if (isHoliday || isWeekend)
            {
                // Demand-driven: only staff the required headcount per shift.
                var demand = isHoliday ? input.Policy.HolidayDemand : input.Policy.WeekendDemand;
                foreach (var target in demand)
                {
                    if (!shiftByCode.TryGetValue(target.ShiftCode, out var shift)) continue;
                    var filled = 0;
                    // Candidates: allowed for this shift, fewest shifts so far first (fairness).
                    var candidates = input.Employees
                        .Where(e => AllowedShifts(e).Any(a => a.Id == shift.Id))
                        .OrderByDescending(e => hints.TryGetValue((e.Id, day), out var hc) && string.Equals(hc, shift.Code, StringComparison.OrdinalIgnoreCase))
                        .ThenBy(e => load[e.Id]);
                    foreach (var emp in candidates)
                    {
                        if (filled >= target.Headcount) break;
                        if (result.Any(r => r.Date == day && r.EmployeeId == emp.Id)) continue; // one shift/day
                        if (!PassesConsecutive(emp.Id, day) || !PassesRest(emp.Id, day, shift)) continue;
                        Commit(emp, day, shift, isHoliday ? "Holiday demand cover" : "Weekend demand cover");
                        filled++;
                    }
                    if (filled < target.Headcount)
                        warnings.Add($"{(isHoliday ? "Holiday" : "Weekend")} {day:yyyy-MM-dd}: only {filled}/{target.Headcount} staffed for {target.ShiftCode} (not enough eligible/rested staff).");
                }
                continue;
            }

            // Weekday: everyone gets an allowed shift (subject to rest / consecutive rules).
            foreach (var emp in input.Employees.OrderBy(e => load[e.Id]))
            {
                if (result.Any(r => r.Date == day && r.EmployeeId == emp.Id)) continue;
                var allowed = AllowedShifts(emp);
                if (allowed.Count == 0)
                {
                    warnings.Add($"{emp.FullName} has no eligible shift under the current gender rules — left unassigned on {day:yyyy-MM-dd}.");
                    continue;
                }
                TryAssign(emp, day, allowed); // a failed assign means a forced rest day, which is fine
            }
        }

        return result;
    }

    // ── Provider/model resolution (mirrors AiAdvisoryService) ────────────────────

    private string ResolveProvider()
    {
        var configured = _options.EffectiveProvider;
        if (configured == "anthropic" && !string.IsNullOrWhiteSpace(_options.AnthropicApiKey)) return "anthropic";
        if (configured == "openai" && !string.IsNullOrWhiteSpace(_options.OpenAIApiKey)) return "openai";
        // The base-URL check is load-bearing, not defensive tidiness: AI_PROVIDER=ollama with no
        // OLLAMA_BASE_URL makes LlmClient fall back to http://localhost:11434, which on a hosted
        // deployment is nothing at all. Trusting the provider name alone would stall the roster
        // screen until the connect attempt died instead of degrading to the rules engine. Pinned
        // by RosterPlannerServiceTests.Plan_TreatsOllamaWithoutABaseUrlAsNotConfigured.
        if (configured == "ollama" && !string.IsNullOrWhiteSpace(_options.OllamaBaseUrl)) return "ollama";
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
        return provider switch
        {
            "anthropic" => "claude-sonnet-4-20250514",
            "openai" => "gpt-5",
            "ollama" => "llama3.1",
            _ => string.Empty
        };
    }
}
