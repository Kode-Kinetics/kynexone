using System.Text;
using System.Text.Json;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Shifts;
using Zayra.Api.Infrastructure.AI;

namespace Zayra.Api.Infrastructure.Shifts;

/// <summary>
/// Hybrid roster planner: the configured LLM (Ollama by default) proposes an intelligent plan,
/// then deterministic guardrails enforce the hard rules (gender→shift, one shift/day, rest hours,
/// max consecutive days, weekend/holiday demand) and repair anything the model got wrong. If the
/// LLM is unavailable or returns garbage, it degrades gracefully to a fully deterministic plan.
/// </summary>
public sealed class RosterPlannerService : IRosterPlannerService
{
    private readonly ILlmClient _llm;
    private readonly AiOptions _options;
    private readonly ILogger<RosterPlannerService> _logger;

    public RosterPlannerService(ILlmClient llm, AiOptions options, ILogger<RosterPlannerService> logger)
    {
        _llm = llm;
        _options = options;
        _logger = logger;
    }

    public async Task<RosterPlanResult> PlanAsync(RosterPlanInput input, CancellationToken ct)
    {
        var warnings = new List<string>();

        // 1. Ask the LLM for hints (best effort). Map of (employeeId, date) -> shiftCode.
        var (hints, engine) = await TryLlmPlanAsync(input, ct);

        // 2. Build the authoritative plan deterministically, seeded by the LLM hints.
        var assignments = BuildDeterministicPlan(input, hints, warnings);

        var summary =
            $"{assignments.Count} shift(s) planned for {input.Employees.Count} employee(s) " +
            $"over {(input.DateTo.DayNumber - input.DateFrom.DayNumber + 1)} day(s) " +
            $"using {engine}.";

        return new RosterPlanResult(assignments, warnings, engine, summary);
    }

    // ── LLM step ──────────────────────────────────────────────────────────────

    private async Task<(Dictionary<(int, DateOnly), string> Hints, string Engine)> TryLlmPlanAsync(
        RosterPlanInput input, CancellationToken ct)
    {
        var empty = new Dictionary<(int, DateOnly), string>();
        var provider = ResolveProvider();
        if (provider == "fallback")
            return (empty, "deterministic (no LLM configured)");

        try
        {
            var model = ResolveModel(provider);
            var request = new LlmRequest(provider, model, BuildSystemPrompt(), BuildUserPrompt(input), 4000);
            var response = await _llm.CompleteAsync(request, ct);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
            {
                _logger.LogWarning("Roster LLM call failed ({Provider}): {Error}", provider, response.Error);
                return (empty, "deterministic (LLM unavailable)");
            }

            var parsed = ParseHints(response.Text, input);
            if (parsed.Count == 0)
                return (empty, "deterministic (LLM returned no usable plan)");

            return (parsed, $"{provider}+guardrails");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Roster LLM planning threw; using deterministic fallback.");
            return (empty, "deterministic (LLM error)");
        }
    }

    private static string BuildSystemPrompt() =>
        "You are a workforce shift-rostering planner. You output ONLY a JSON array, no prose. " +
        "Each element is {\"employeeId\": <int>, \"date\": \"yyyy-MM-dd\", \"shiftCode\": \"<code>\"}. " +
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
        sb.AppendLine("EMPLOYEES (id | name | gender | department):");
        foreach (var e in input.Employees)
            sb.AppendLine($"- {e.Id} | {e.FullName} | {e.Gender} | {e.Department}");
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
        sb.AppendLine("Return ONLY the JSON array.");
        return sb.ToString();
    }

    private sealed record HintDto(int employeeId, string date, string shiftCode);

    private static Dictionary<(int, DateOnly), string> ParseHints(string text, RosterPlanInput input)
    {
        var result = new Dictionary<(int, DateOnly), string>();
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return result;
        var json = text.Substring(start, end - start + 1);

        List<HintDto>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<HintDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return result; }
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
        if (configured == "ollama" && !string.IsNullOrWhiteSpace(_options.OllamaBaseUrl)) return "ollama";
        if (!string.IsNullOrWhiteSpace(_options.AnthropicApiKey)) return "anthropic";
        if (!string.IsNullOrWhiteSpace(_options.OpenAIApiKey)) return "openai";
        if (!string.IsNullOrWhiteSpace(_options.OllamaBaseUrl)) return "ollama";
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
