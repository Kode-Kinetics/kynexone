using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Setup;
using Zayra.Api.Infrastructure.AI;

namespace Zayra.Api.Infrastructure.Setup;

/// <summary>
/// AI Setup Assistant: turns a short company profile into a complete starter configuration draft.
/// The LLM proposes the descriptive parts (org structure, leave types, shifts, pay components);
/// deterministic code supplies the risk-sensitive parts (working week + statutory rules by country)
/// and normalises everything. Falls back to a deterministic template if the LLM is unavailable.
/// </summary>
public sealed class SetupAssistantService : ISetupAssistantService
{
    private readonly ILlmClient _llm;
    private readonly AiOptions _options;
    private readonly ILogger<SetupAssistantService> _logger;

    public SetupAssistantService(ILlmClient llm, AiOptions options, ILogger<SetupAssistantService> logger)
    {
        _llm = llm;
        _options = options;
        _logger = logger;
    }

    public async Task<SetupPreviewResult> GenerateAsync(CompanyProfile profile, CancellationToken ct)
    {
        var notes = new List<string>();
        var iso3 = NormalizeCountry(profile.CountryCode);

        // 1. Descriptive sections — LLM first, deterministic template as fallback.
        SetupDraft draft;
        string engine;
        var llm = await TryLlmAsync(profile, iso3, ct);
        if (llm is not null)
        {
            draft = llm;
            engine = $"{ResolveProvider()}+guardrails";
        }
        else
        {
            draft = DeterministicTemplate(profile, iso3);
            engine = "deterministic template";
            notes.Add("AI provider unavailable — used a built-in starter template you can edit.");
        }

        // 2. Always deterministic: working week + statutory (never trust an LLM with labour-law rates).
        if (profile.Sections.Shifts)
            draft = draft with { WorkingWeek = WorkingWeekFor(iso3) };
        if (profile.Sections.Payroll)
        {
            draft = draft with { StatutoryRules = StatutoryFor(iso3) };
            if (draft.StatutoryRules.Count > 0)
                notes.Add("Statutory rules are country defaults — verify rates against current regulation before relying on them.");
        }

        // 3. Honour section toggles + normalise.
        draft = ApplySectionsAndNormalise(draft, profile.Sections);
        return new SetupPreviewResult(draft, notes, engine);
    }

    // ── LLM ─────────────────────────────────────────────────────────────────

    private async Task<SetupDraft?> TryLlmAsync(CompanyProfile p, string iso3, CancellationToken ct)
    {
        var provider = ResolveProvider();
        if (provider == "fallback") return null;
        try
        {
            var req = new LlmRequest(provider, ResolveModel(provider), SystemPrompt(), UserPrompt(p, iso3), 4000);
            var res = await _llm.CompleteAsync(req, ct);
            if (!res.Success || string.IsNullOrWhiteSpace(res.Text))
            {
                _logger.LogWarning("Setup LLM call failed ({Provider}): {Error}", provider, res.Error);
                return null;
            }
            return ParseDraft(res.Text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup LLM generation threw; using deterministic template.");
            return null;
        }
    }

    private static string SystemPrompt() =>
        "You are an HR systems configuration expert. Output ONLY a single JSON object, no prose. Schema: " +
        "{\"departments\":[{\"code\":\"\",\"nameEn\":\"\"}]," +
        "\"designations\":[{\"code\":\"\",\"titleEn\":\"\",\"departmentCode\":\"\",\"jobLevel\":\"\",\"isManagerRole\":false,\"levelRank\":1}]," +
        "\"grades\":[{\"code\":\"\",\"name\":\"\",\"band\":\"\",\"level\":1}]," +
        "\"leaveTypes\":[{\"code\":\"\",\"nameEn\":\"\",\"category\":\"\",\"isPaid\":true,\"maxConsecutiveDays\":0,\"requiresAttachment\":false,\"colorCode\":\"#2F6BFF\"}]," +
        "\"shifts\":[{\"code\":\"\",\"name\":\"\",\"start\":\"HH:mm\",\"end\":\"HH:mm\",\"breakMinutes\":60,\"color\":\"#2F6BFF\"}]," +
        "\"payComponents\":[{\"code\":\"\",\"name\":\"\",\"componentType\":\"Earning|Deduction\",\"calculationType\":\"Fixed|Percentage\",\"amount\":0,\"percentage\":0,\"isTaxable\":false}]}. " +
        "Codes must be SHORT UPPER_SNAKE and unique within their list. designation.departmentCode must match a department code. Tailor counts/names to the industry, size and country.";

    private static string UserPrompt(CompanyProfile p, string iso3)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Country: {iso3}. Industry: {p.Industry}. Company size: {p.CompanySize}. Currency: {p.CurrencyCode}.");
        if (!string.IsNullOrWhiteSpace(p.Notes)) sb.AppendLine($"Extra context: {p.Notes}");
        var want = new List<string>();
        if (p.Sections.Org) want.Add("departments, designations, grades");
        if (p.Sections.Leave) want.Add("leaveTypes (align entitlements/categories to the country's labour law)");
        if (p.Sections.Shifts) want.Add("shifts");
        if (p.Sections.Payroll) want.Add("payComponents (typical earnings & deductions for the country)");
        sb.AppendLine($"Generate only these sections: {string.Join("; ", want)}. Leave other arrays empty.");
        sb.AppendLine("Return ONLY the JSON object.");
        return sb.ToString();
    }

    private SetupDraft? ParseDraft(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text.Substring(start, end - start + 1));
            var root = doc.RootElement;
            var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            List<T> Arr<T>(string name) =>
                root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array
                    ? (el.Deserialize<List<T>>(opt) ?? new()) : new();

            return new SetupDraft(
                Arr<DraftDepartment>("departments"),
                Arr<DraftDesignation>("designations"),
                Arr<DraftGrade>("grades"),
                Arr<DraftLeaveType>("leaveTypes"),
                Arr<DraftShift>("shifts"),
                null,
                Arr<DraftPayComponent>("payComponents"),
                new());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse setup LLM JSON.");
            return null;
        }
    }

    // ── Normalisation & guardrails ──────────────────────────────────────────

    private static readonly Regex TimeRx = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

    private static SetupDraft ApplySectionsAndNormalise(SetupDraft d, SetupSections s)
    {
        static string Code(string? c, string fallback) =>
            (string.IsNullOrWhiteSpace(c) ? fallback : c).Trim().ToUpperInvariant().Replace(' ', '_');

        // Org
        var depts = !s.Org ? new() : Dedup(d.Departments.Where(x => !string.IsNullOrWhiteSpace(x.NameEn))
            .Select(x => x with { Code = Code(x.Code, x.NameEn) }), x => x.Code);
        var deptCodes = depts.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desigs = !s.Org ? new() : Dedup(d.Designations.Where(x => !string.IsNullOrWhiteSpace(x.TitleEn))
            .Select(x => x with
            {
                Code = Code(x.Code, x.TitleEn),
                DepartmentCode = deptCodes.Contains(x.DepartmentCode ?? "") ? x.DepartmentCode!.Trim().ToUpperInvariant() : "",
                LevelRank = Math.Clamp(x.LevelRank, 1, 20),
            }), x => x.Code);
        var grades = !s.Org ? new() : Dedup(d.Grades.Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => x with { Code = Code(x.Code, x.Name), Level = Math.Clamp(x.Level, 1, 20) }), x => x.Code);

        // Leave
        var leaves = !s.Leave ? new() : Dedup(d.LeaveTypes.Where(x => !string.IsNullOrWhiteSpace(x.NameEn))
            .Select(x => x with
            {
                Code = Code(x.Code, x.NameEn),
                Category = string.IsNullOrWhiteSpace(x.Category) ? "Standard" : x.Category.Trim(),
                MaxConsecutiveDays = Math.Clamp(x.MaxConsecutiveDays, 0, 365),
                ColorCode = string.IsNullOrWhiteSpace(x.ColorCode) ? "#2F6BFF" : x.ColorCode.Trim(),
            }), x => x.Code);

        // Shifts
        var shifts = !s.Shifts ? new() : Dedup(d.Shifts.Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => x with
            {
                Code = Code(x.Code, x.Name),
                Start = TimeRx.IsMatch(x.Start ?? "") ? x.Start : "09:00",
                End = TimeRx.IsMatch(x.End ?? "") ? x.End : "17:00",
                BreakMinutes = Math.Clamp(x.BreakMinutes, 0, 240),
                Color = string.IsNullOrWhiteSpace(x.Color) ? "#2F6BFF" : x.Color.Trim(),
            }), x => x.Code);

        // Payroll
        var pay = !s.Payroll ? new() : Dedup(d.PayComponents.Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => x with
            {
                Code = Code(x.Code, x.Name),
                ComponentType = string.Equals(x.ComponentType, "Deduction", StringComparison.OrdinalIgnoreCase) ? "Deduction" : "Earning",
                CalculationType = string.Equals(x.CalculationType, "Percentage", StringComparison.OrdinalIgnoreCase) ? "Percentage" : "Fixed",
                Amount = Math.Max(0, x.Amount),
                Percentage = Math.Clamp(x.Percentage, 0, 100),
            }), x => x.Code);

        return d with
        {
            Departments = depts, Designations = desigs, Grades = grades,
            LeaveTypes = leaves, Shifts = shifts, PayComponents = pay,
            WorkingWeek = s.Shifts ? d.WorkingWeek : null,
            StatutoryRules = s.Payroll ? d.StatutoryRules : new(),
        };
    }

    private static List<T> Dedup<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<T>();
        foreach (var i in items) if (!string.IsNullOrWhiteSpace(key(i)) && seen.Add(key(i))) result.Add(i);
        return result;
    }

    // ── Country deterministic data ──────────────────────────────────────────

    private static string NormalizeCountry(string raw)
    {
        var c = (raw ?? "").Trim().ToUpperInvariant();
        return c switch
        {
            "SA" or "KSA" or "SAU" => "SAU",
            "AE" or "UAE" or "ARE" => "ARE",
            "QA" or "QAT" => "QAT",
            "KW" or "KWT" => "KWT",
            "BH" or "BHR" => "BHR",
            "OM" or "OMN" => "OMN",
            _ => c.Length >= 3 ? c[..3] : c,
        };
    }

    private static DraftWorkingWeek WorkingWeekFor(string iso3) => iso3 switch
    {
        "SAU" or "QAT" or "KWT" or "BHR" or "OMN" => new DraftWorkingWeek("Sun-Thu", "Sunday"),
        "ARE" => new DraftWorkingWeek("Mon-Fri", "Monday"),
        _ => new DraftWorkingWeek("Mon-Fri", "Monday"),
    };

    private static List<DraftStatutoryRule> StatutoryFor(string iso3) => iso3 switch
    {
        "SAU" => new()
        {
            new("gosi.employee_rate", "0.0975", "decimal", "GOSI employee contribution (Saudi nationals)"),
            new("gosi.employer_rate", "0.1175", "decimal", "GOSI employer contribution (Saudi nationals)"),
            new("gosi.expat_employer_rate", "0.02", "decimal", "GOSI occupational hazard (non-Saudis), employer"),
            new("wps.enabled", "true", "bool", "Wage Protection System filing required"),
            new("eosb.enabled", "true", "bool", "End-of-service benefit applies"),
        },
        "ARE" => new()
        {
            new("gpssa.employee_rate", "0.05", "decimal", "GPSSA pension employee contribution (UAE nationals)"),
            new("gpssa.employer_rate", "0.125", "decimal", "GPSSA pension employer contribution (UAE nationals)"),
            new("wps.enabled", "true", "bool", "Wage Protection System filing required"),
            new("eosb.enabled", "true", "bool", "End-of-service gratuity applies"),
        },
        "QAT" or "KWT" or "BHR" or "OMN" => new()
        {
            new("wps.enabled", "true", "bool", "Wage Protection System filing required"),
            new("eosb.enabled", "true", "bool", "End-of-service benefit applies"),
        },
        _ => new(),
    };

    // ── Deterministic full template (LLM fallback) ──────────────────────────

    private static SetupDraft DeterministicTemplate(CompanyProfile p, string iso3)
    {
        var depts = new List<DraftDepartment>
        {
            new("HR", "Human Resources"), new("FIN", "Finance"), new("IT", "Information Technology"),
            new("OPS", "Operations"), new("SALES", "Sales"), new("ADMIN", "Administration"),
        };
        var desigs = new List<DraftDesignation>
        {
            new("CEO", "Chief Executive Officer", "ADMIN", "Executive", true, 1),
            new("HR_MGR", "HR Manager", "HR", "Management", true, 3),
            new("HR_OFF", "HR Officer", "HR", "Staff", false, 5),
            new("ACCOUNTANT", "Accountant", "FIN", "Staff", false, 5),
            new("IT_ENG", "IT Engineer", "IT", "Staff", false, 5),
            new("SALES_EXEC", "Sales Executive", "SALES", "Staff", false, 5),
        };
        var grades = new List<DraftGrade>
        {
            new("G1", "Grade 1 — Entry", "Staff", 1), new("G2", "Grade 2 — Junior", "Staff", 2),
            new("G3", "Grade 3 — Senior", "Professional", 3), new("G4", "Grade 4 — Lead", "Management", 4),
            new("G5", "Grade 5 — Executive", "Executive", 5),
        };
        var leaves = new List<DraftLeaveType>
        {
            new("ANNUAL", "Annual Leave", "Standard", true, 30, false, "#00C896"),
            new("SICK", "Sick Leave", "Medical", true, 30, true, "#f59e0b"),
            new("MAT", "Maternity Leave", "Parental", true, 70, true, "#ec4899"),
            new("PAT", "Paternity Leave", "Parental", true, 3, false, "#8b5cf6"),
            new("UNPAID", "Unpaid Leave", "Unpaid", false, 30, false, "#64748b"),
        };
        if (iso3 == "SAU") leaves.Add(new("HAJJ", "Hajj Leave", "Religious", true, 10, false, "#2F6BFF"));
        var shifts = new List<DraftShift>
        {
            new("MORNING", "Morning Shift", "06:00", "14:00", 60, "#00C896"),
            new("EVENING", "Evening Shift", "14:00", "22:00", 60, "#8b5cf6"),
            new("NIGHT", "Night Shift", "22:00", "06:00", 60, "#2F6BFF"),
        };
        var pay = new List<DraftPayComponent>
        {
            new("BASIC", "Basic Salary", "Earning", "Fixed", 0, 0, false),
            new("HRA", "Housing Allowance", "Earning", "Percentage", 0, 25, false),
            new("TRA", "Transport Allowance", "Earning", "Fixed", 0, 0, false),
            new("GOSI_DED", "GOSI Deduction", "Deduction", "Percentage", 0, 9.75m, false),
        };
        return new SetupDraft(depts, desigs, grades, leaves, shifts, null, pay, new());
    }

    // ── Provider/model resolution (mirrors AiAdvisoryService) ────────────────

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
        return provider switch
        {
            "anthropic" => "claude-sonnet-4-20250514",
            "openai" => "gpt-5",
            "ollama" => "llama3.1",
            _ => string.Empty,
        };
    }
}
