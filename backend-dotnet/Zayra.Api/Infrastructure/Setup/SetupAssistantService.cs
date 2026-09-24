using System.Diagnostics;
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
///
/// Every attempt — provider answered, provider refused, provider timed out, or no provider at all
/// — is written through <see cref="IAiCallRecorder"/>. This service used to call ILlmClient
/// directly and record nothing, which is why it could serve a built-in template on every request
/// for an unknown length of time with no row anywhere to show it.
/// </summary>
public sealed class SetupAssistantService : ISetupAssistantService
{
    private readonly ILlmClient _llm;
    private readonly AiOptions _options;
    private readonly IAiCallRecorder _recorder;
    private readonly ILogger<SetupAssistantService> _logger;

    public SetupAssistantService(ILlmClient llm, AiOptions options, IAiCallRecorder recorder, ILogger<SetupAssistantService> logger)
    {
        _llm = llm;
        _options = options;
        _recorder = recorder;
        _logger = logger;
    }

    public async Task<SetupPreviewResult> GenerateAsync(SetupRequester requester, CompanyProfile profile, CancellationToken ct)
    {
        var notes = new List<string>();
        var iso3 = NormalizeCountry(profile.CountryCode);

        // 1. Descriptive sections — LLM first, deterministic template as fallback.
        SetupDraft draft;
        string engine;
        var (llm, failureNote) = await TryLlmAsync(requester, profile, iso3, ct);
        if (llm is not null)
        {
            draft = llm;
            engine = $"{ResolveProvider()}+guardrails";
        }
        else
        {
            draft = DeterministicTemplate(profile, iso3, notes);
            // The badge names what the template actually keyed off, so nobody has to guess
            // whether their industry/size selections reached the draft.
            engine = $"deterministic template · {DescribeIndustry(profile.Industry)} · {DescribeSize(profile.CompanySize)}";
            // First, so the reader learns WHY this is a template before reading its caveats.
            notes.Insert(0, failureNote);
        }

        // 2. Always deterministic: working week + statutory (never trust an LLM with labour-law rates).
        if (profile.Sections.Shifts)
            draft = draft with { WorkingWeek = WorkingWeekFor(iso3) };
        if (profile.Sections.Governance)
            draft = draft with
            {
                EmployeeIdRule = EmployeeIdRuleFor(profile, iso3),
                HrConfig = HrConfigFor(profile)
            };
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

    /// <summary>How long the assistant will wait for a model before falling back. Deliberately
    /// shorter than the HttpClient ceiling so the degrade is ours and explicable, not a socket
    /// timeout surfacing as a generic error.</summary>
    private static readonly TimeSpan LlmBudget = TimeSpan.FromSeconds(75);

    /// <summary>Budget for the draft. A reasoning model spends part of this thinking before it
    /// emits a token of JSON, so 4000 (the old value) could be consumed before the object began.</summary>
    private const int LlmOutputTokens = 8000;

    /// <returns>The parsed draft, or null plus a note that says which of the four things went
    /// wrong. "AI provider unavailable" used to cover all of them, including a provider that was
    /// up and answering — which sent anyone debugging this straight to the wrong place.</returns>
    private async Task<(SetupDraft? Draft, string FailureNote)> TryLlmAsync(SetupRequester who, CompanyProfile p, string iso3, CancellationToken ct)
    {
        const string suffix = "Used a built-in starter template you can edit.";
        var provider = ResolveProvider();
        var summary = PromptSummaryFor(p, iso3);
        var timer = Stopwatch.StartNew();

        if (provider == "fallback")
        {
            // Recorded too. "No provider configured" is the degrade that is hardest to notice from
            // the outside and the one most worth being able to count.
            var note = $"No AI provider is configured. {suffix}";
            await RecordAsync(who, summary, null, null, timer, note, ct);
            return (null, note);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(LlmBudget);
        LlmRequest? req = null;
        try
        {
            req = new LlmRequest(provider, ResolveModel(provider), SystemPrompt(), UserPrompt(p, iso3),
                LlmOutputTokens, RequireJson: true);
            var res = await _llm.CompleteAsync(req, budget.Token);
            if (!res.Success || string.IsNullOrWhiteSpace(res.Text))
            {
                _logger.LogWarning("Setup LLM call failed ({Provider}/{Model}): {Error}", provider, req.Model, res.Error);
                var note = $"The AI provider ({provider}) did not return a usable draft: {Truncate(res.Error)}. {suffix}";
                await RecordAsync(who, summary, req, res, timer, note, ct);
                return (null, note);
            }
            var parsed = ParseDraft(res.Text);
            if (parsed is null)
            {
                var note = $"The AI provider ({provider}) replied, but the draft could not be read as valid configuration. {suffix}";
                // The response stays truthful: the provider really did answer and really did
                // spend those tokens. The FailureReason is what makes the recorder count this as
                // a fallback, so the answer-rate stays honest without the cost going missing.
                await RecordAsync(who, summary, req, res, timer, note, ct);
                return (null, note);
            }
            await RecordAsync(who, summary, req, res, timer, null, ct);
            return (parsed, string.Empty);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Setup LLM call exceeded the {Seconds}s budget ({Provider}).", LlmBudget.TotalSeconds, provider);
            var note = $"The AI provider ({provider}) did not respond within {LlmBudget.TotalSeconds:0}s. {suffix}";
            await RecordAsync(who, summary, req, null, timer, note, ct);
            return (null, note);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup LLM generation threw; using deterministic template.");
            var note = $"The AI provider ({provider}) could not be reached: {Truncate(ex.Message)}. {suffix}";
            await RecordAsync(who, summary, req, null, timer, note, ct);
            return (null, note);
        }
    }

    // ── Usage recording (Contract C) ────────────────────────────────────────

    private const string RecordModule = "setup";
    private const string RecordIntent = "starter_configuration";

    /// <summary>
    /// Writes the usage/cost record for one attempt.
    ///
    /// <see cref="AiCallRecorder"/> already promises not to throw, so this try/catch is defence
    /// against any other implementation rather than a duplicate of that promise: a failed audit
    /// row must never cost a tenant their setup draft.
    /// </summary>
    private async Task RecordAsync(SetupRequester who, string summary, LlmRequest? request, LlmResponse? response,
                                   Stopwatch timer, string? failureReason, CancellationToken ct)
    {
        try
        {
            // If the caller walked away we still owe the row — tokens may already have been spent —
            // so the write is not cancelled along with the request it describes.
            var writeToken = ct.IsCancellationRequested ? CancellationToken.None : ct;
            await _recorder.RecordAsync(new AiCallRecord(
                who.TenantId, who.UserId, who.UserRole, RecordModule, RecordIntent, summary,
                request, response, (int)timer.ElapsedMilliseconds, failureReason), writeToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the {Module}/{Intent} AI call for tenant {TenantId}.",
                RecordModule, RecordIntent, who.TenantId);
        }
    }

    /// <summary>
    /// The row's description of the request — never the prompt.
    ///
    /// Built ONLY from values this service derived itself: the industry pack label and ladder label
    /// (each one of a fixed handful of strings) plus code-shaped country and currency. So the
    /// profile's free-text Notes and the legal entity name cannot reach an audit row by any route,
    /// including AI_LOG_PROMPTS, which AiAuditService honours for the chat assistant.
    /// </summary>
    private static string PromptSummaryFor(CompanyProfile p, string iso3)
        => string.Join(" · ",
            "Starter configuration draft",
            DescribeIndustry(p.Industry),
            DescribeSize(p.CompanySize),
            CodeOrUnspecified(iso3),
            CodeOrUnspecified(p.CurrencyCode),
            $"sections: {DescribeSections(p.Sections)}");

    /// <summary>Country and currency arrive as free text from the form. Anything that is not a
    /// short alphabetic code is dropped rather than copied into the row.</summary>
    private static string CodeOrUnspecified(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToUpperInvariant();
        return v.Length is >= 2 and <= 3 && v.All(char.IsAsciiLetterUpper) ? v : "unspecified";
    }

    private static string DescribeSections(SetupSections s)
    {
        var on = new List<string>();
        if (s.Org) on.Add("org");
        if (s.Entity) on.Add("entity");
        if (s.Leave) on.Add("leave");
        if (s.Shifts) on.Add("shifts");
        if (s.Payroll) on.Add("payroll");
        if (s.Governance) on.Add("governance");
        return on.Count == 0 ? "none" : string.Join(",", on);
    }

    /// <summary>Provider error bodies are echoed to an Admin/HR Manager screen — keep them short.</summary>
    private static string Truncate(string? value, int max = 180)
    {
        if (string.IsNullOrWhiteSpace(value)) return "no detail reported";
        var flat = Regex.Replace(value.Trim(), @"\s+", " ");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static string SystemPrompt() =>
        "You are an HR systems configuration expert. Output ONLY a single JSON object, no prose. Schema: " +
        "{\"departments\":[{\"code\":\"\",\"nameEn\":\"\"}]," +
        "\"branches\":[{\"code\":\"\",\"nameEn\":\"\",\"city\":\"\",\"isHeadOffice\":true}]," +
        "\"costCenters\":[{\"code\":\"\",\"name\":\"\",\"departmentCode\":\"\"}]," +
        "\"designations\":[{\"code\":\"\",\"titleEn\":\"\",\"departmentCode\":\"\",\"gradeCode\":\"\",\"jobLevel\":\"\",\"isManagerRole\":false,\"levelRank\":1}]," +
        "\"grades\":[{\"code\":\"\",\"name\":\"\",\"band\":\"\",\"level\":1,\"minSalary\":0,\"midSalary\":0,\"maxSalary\":0,\"currency\":\"\"}]," +
        "\"gradePayComponents\":[{\"gradeCode\":\"\",\"componentCode\":\"\",\"componentName\":\"\",\"componentType\":\"Earning|Benefit|Deduction\",\"calculationType\":\"Fixed|PercentOfBasic\",\"amount\":0,\"percentage\":0,\"isTaxable\":false,\"frequency\":\"Monthly\"}]," +
        "\"leaveTypes\":[{\"code\":\"\",\"nameEn\":\"\",\"category\":\"\",\"isPaid\":true,\"maxConsecutiveDays\":0,\"requiresAttachment\":false,\"colorCode\":\"#2F6BFF\"}]," +
        "\"shifts\":[{\"code\":\"\",\"name\":\"\",\"start\":\"HH:mm\",\"end\":\"HH:mm\",\"breakMinutes\":60,\"color\":\"#2F6BFF\"}]," +
        "\"payComponents\":[{\"code\":\"\",\"name\":\"\",\"componentType\":\"Earning|Deduction\",\"calculationType\":\"Fixed|Percentage\",\"amount\":0,\"percentage\":0,\"isTaxable\":false}]}. " +
        "Codes must be SHORT UPPER_SNAKE and unique within their list. designation.departmentCode must match a department code; designation.gradeCode and gradePayComponents.gradeCode must match a grade code. Tailor counts/names to the industry, size and country.";

    private static string UserPrompt(CompanyProfile p, string iso3)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Country: {iso3}. Industry: {p.Industry}. Company size: {p.CompanySize}. Currency: {p.CurrencyCode}.");
        if (!string.IsNullOrWhiteSpace(p.Notes)) sb.AppendLine($"Extra context: {p.Notes}");
        var want = new List<string>();
        if (p.Sections.Org) want.Add("departments, designations, grades");
        if (p.Sections.Entity) want.Add("branches and costCenters");
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
                Arr<DraftBranch>("branches"),
                Arr<DraftDepartment>("departments"),
                Arr<DraftCostCenter>("costCenters"),
                Arr<DraftDesignation>("designations"),
                Arr<DraftGrade>("grades"),
                Arr<DraftGradePayComponent>("gradePayComponents"),
                Arr<DraftLeaveType>("leaveTypes"),
                Arr<DraftShift>("shifts"),
                null,
                Arr<DraftPayComponent>("payComponents"),
                new(),
                null,
                null);
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
        var branches = !s.Entity ? new() : Dedup(d.Branches.Where(x => !string.IsNullOrWhiteSpace(x.NameEn))
            .Select(x => x with { Code = Code(x.Code, x.NameEn), City = (x.City ?? "").Trim() }), x => x.Code);

        var depts = !s.Org ? new() : Dedup(d.Departments.Where(x => !string.IsNullOrWhiteSpace(x.NameEn))
            .Select(x => x with { Code = Code(x.Code, x.NameEn) }), x => x.Code);
        var deptCodes = depts.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var grades = !s.Org ? new() : Dedup(d.Grades.Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => x with
            {
                Code = Code(x.Code, x.Name),
                Level = Math.Clamp(x.Level, 1, 20),
                MinSalary = Math.Max(0, x.MinSalary),
                MidSalary = Math.Max(0, x.MidSalary),
                MaxSalary = Math.Max(0, Math.Max(x.MaxSalary, x.MidSalary)),
                Currency = string.IsNullOrWhiteSpace(x.Currency) ? "SAR" : x.Currency.Trim().ToUpperInvariant(),
            }), x => x.Code);
        var gradeCodes = grades.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desigs = !s.Org ? new() : Dedup(d.Designations.Where(x => !string.IsNullOrWhiteSpace(x.TitleEn))
            .Select(x => x with
            {
                Code = Code(x.Code, x.TitleEn),
                DepartmentCode = deptCodes.Contains(x.DepartmentCode ?? "") ? x.DepartmentCode!.Trim().ToUpperInvariant() : "",
                GradeCode = gradeCodes.Contains(x.GradeCode ?? "") ? x.GradeCode!.Trim().ToUpperInvariant() : "",
                LevelRank = Math.Clamp(x.LevelRank, 1, 20),
            }), x => x.Code);
        var costCenters = !s.Entity ? new() : Dedup(d.CostCenters.Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => x with
            {
                Code = Code(x.Code, x.Name),
                DepartmentCode = deptCodes.Contains(x.DepartmentCode ?? "") ? x.DepartmentCode!.Trim().ToUpperInvariant() : "",
            }), x => x.Code);
        var gradePay = !s.Payroll ? new() : Dedup(d.GradePayComponents.Where(x => !string.IsNullOrWhiteSpace(x.ComponentName))
            .Select(x => x with
            {
                GradeCode = gradeCodes.Contains(x.GradeCode ?? "") ? x.GradeCode!.Trim().ToUpperInvariant() : "",
                ComponentCode = Code(x.ComponentCode, x.ComponentName),
                ComponentType = string.Equals(x.ComponentType, "Deduction", StringComparison.OrdinalIgnoreCase) ? "Deduction" :
                    string.Equals(x.ComponentType, "Benefit", StringComparison.OrdinalIgnoreCase) ? "Benefit" : "Earning",
                CalculationType = string.Equals(x.CalculationType, "PercentOfBasic", StringComparison.OrdinalIgnoreCase) ? "PercentOfBasic" :
                    string.Equals(x.CalculationType, "Percentage", StringComparison.OrdinalIgnoreCase) ? "PercentOfBasic" : "Fixed",
                Amount = Math.Max(0, x.Amount),
                Percentage = Math.Clamp(x.Percentage, 0, 100),
                Frequency = string.IsNullOrWhiteSpace(x.Frequency) ? "Monthly" : x.Frequency.Trim(),
            }).Where(x => !string.IsNullOrWhiteSpace(x.GradeCode)), x => $"{x.GradeCode}:{x.ComponentCode}");

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
                Start = TimeRx.IsMatch(x.Start ?? "") ? x.Start! : "09:00",
                End = TimeRx.IsMatch(x.End ?? "") ? x.End! : "17:00",
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
            Branches = branches, Departments = depts, CostCenters = costCenters, Designations = desigs, Grades = grades,
            GradePayComponents = gradePay,
            LeaveTypes = leaves, Shifts = shifts, PayComponents = pay,
            WorkingWeek = s.Shifts ? d.WorkingWeek : null,
            StatutoryRules = s.Payroll ? d.StatutoryRules : new(),
            EmployeeIdRule = s.Governance ? d.EmployeeIdRule : null,
            HrConfig = s.Governance ? d.HrConfig : null,
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

    // Industry packs. Each adds departments and roles on top of the core four, and declares
    // whether the industry normally runs around the clock (which decides the shift pattern).
    private enum GradeSlot { Low, Mid, Upper, Top }

    private sealed record IndustryPack(
        string Label,
        List<DraftDepartment> Departments,
        List<(string Code, string Title, string Dept, GradeSlot Slot, bool IsManager, int Rank)> Designations,
        bool ContinuousOperations);

    private static IndustryPack PackFor(string? industry)
    {
        var key = (industry ?? string.Empty).ToLowerInvariant();
        bool Has(params string[] needles) => needles.Any(key.Contains);

        if (Has("health", "hospital", "clinic", "medical", "pharma"))
            return new("Healthcare",
                new() { new("CLIN", "Clinical"), new("NURS", "Nursing"), new("PHARM", "Pharmacy") },
                new() { ("PHYS", "Physician", "CLIN", GradeSlot.Upper, false, 3),
                        ("NURSE", "Staff Nurse", "NURS", GradeSlot.Low, false, 5),
                        ("PHARMACIST", "Pharmacist", "PHARM", GradeSlot.Mid, false, 5) },
                true);

        if (Has("construct", "contract", "civil", "infrastructure"))
            return new("Construction",
                new() { new("PROJ", "Projects"), new("HSE", "Health & Safety"), new("SITE", "Site Operations") },
                new() { ("PROJ_MGR", "Project Manager", "PROJ", GradeSlot.Upper, true, 3),
                        ("SITE_ENG", "Site Engineer", "SITE", GradeSlot.Mid, false, 5),
                        ("HSE_OFF", "HSE Officer", "HSE", GradeSlot.Low, false, 5) },
                false);

        if (Has("logistic", "transport", "shipping", "freight", "courier", "supply chain"))
            return new("Logistics",
                new() { new("FLEET", "Fleet"), new("WHSE", "Warehouse"), new("DISP", "Dispatch") },
                new() { ("FLEET_SUP", "Fleet Supervisor", "FLEET", GradeSlot.Mid, true, 4),
                        ("WH_KEEPER", "Warehouse Keeper", "WHSE", GradeSlot.Low, false, 5),
                        ("DRIVER", "Driver", "FLEET", GradeSlot.Low, false, 6) },
                true);

        if (Has("retail", "trading", "wholesale", "ecommerce", "e-commerce"))
            return new("Retail",
                new() { new("STORE", "Stores"), new("MERCH", "Merchandising"), new("SALES", "Sales") },
                new() { ("STORE_MGR", "Store Manager", "STORE", GradeSlot.Upper, true, 3),
                        ("MERCHANDISER", "Merchandiser", "MERCH", GradeSlot.Low, false, 5),
                        ("CASHIER", "Cashier", "STORE", GradeSlot.Low, false, 6) },
                false);

        if (Has("hospitality", "hotel", "restaurant", "catering", "f&b", "tourism"))
            return new("Hospitality",
                new() { new("FNB", "Food & Beverage"), new("HK", "Housekeeping"), new("FO", "Front Office") },
                new() { ("HK_SUP", "Housekeeping Supervisor", "HK", GradeSlot.Mid, true, 4),
                        ("CHEF", "Chef", "FNB", GradeSlot.Mid, false, 5),
                        ("FO_AGENT", "Front Office Agent", "FO", GradeSlot.Low, false, 5) },
                true);

        if (Has("manufact", "factory", "industrial", "production", "plant"))
            return new("Manufacturing",
                new() { new("PROD", "Production"), new("QA", "Quality Assurance"), new("MAINT", "Maintenance") },
                new() { ("PROD_SUP", "Production Supervisor", "PROD", GradeSlot.Mid, true, 4),
                        ("QA_INSP", "Quality Inspector", "QA", GradeSlot.Low, false, 5),
                        ("MAINT_TECH", "Maintenance Technician", "MAINT", GradeSlot.Low, false, 5) },
                true);

        if (Has("tech", "software", "saas", "digital", "fintech"))
            return new("Technology",
                new() { new("ENG", "Engineering"), new("PRODUCT", "Product"), new("SUPPORT", "Customer Support") },
                new() { ("PROD_MGR", "Product Manager", "PRODUCT", GradeSlot.Upper, true, 3),
                        ("SW_ENG", "Software Engineer", "ENG", GradeSlot.Mid, false, 5),
                        ("SUP_AGENT", "Support Agent", "SUPPORT", GradeSlot.Low, false, 5) },
                false);

        if (Has("education", "school", "university", "training", "academy"))
            return new("Education",
                new() { new("ACAD", "Academics"), new("ADMISSIONS", "Admissions"), new("STUDENT", "Student Affairs") },
                new() { ("REGISTRAR", "Registrar", "ADMISSIONS", GradeSlot.Upper, true, 3),
                        ("TEACHER", "Teacher", "ACAD", GradeSlot.Mid, false, 5),
                        ("COUNSELLOR", "Student Counsellor", "STUDENT", GradeSlot.Low, false, 5) },
                false);

        return new("General",
            new() { new("OPS", "Operations"), new("SALES", "Sales") },
            new() { ("OPS_OFF", "Operations Officer", "OPS", GradeSlot.Low, false, 5),
                    ("SALES_EXEC", "Sales Executive", "SALES", GradeSlot.Low, false, 5) },
            false);
    }

    /// <summary>Headcount decides how many rungs the ladder has. A 30-person company does not
    /// need seven grades, and a 2,000-person one cannot run on five.</summary>
    private static int GradeCountFor(string? size)
    {
        var digits = Regex.Matches(size ?? string.Empty, @"\d+").Select(m => int.Parse(m.Value)).ToList();
        var headcount = digits.Count > 0 ? digits.Max() : 0;
        if (headcount > 0 && (size ?? string.Empty).Contains('+')) headcount = Math.Max(headcount, 201);
        return headcount switch { 0 => 5, <= 50 => 3, <= 200 => 5, _ => 7 };
    }

    private static readonly (string Tier, string Band, decimal Min, decimal Mid, decimal Max)[] SarLadder =
    {
        ("Entry",            "Staff",         3000m,   4500m,   6000m),
        ("Junior",           "Staff",         6000m,   8000m,  10000m),
        ("Senior",           "Professional", 10000m,  13500m,  17000m),
        ("Lead",             "Management",   17000m,  22500m,  28000m),
        ("Executive",        "Executive",    28000m,  39000m,  50000m),
        ("Director",         "Executive",    50000m,  65000m,  80000m),
        ("Senior Executive", "Executive",    80000m, 105000m, 130000m),
    };

    // The ladder above is denominated in SAR, which holds a FIXED peg at 3.75/USD. The other
    // currencies here hold fixed USD pegs of their own, so converting between them is arithmetic
    // rather than a forecast. Everything absent from this table -- KWD (basket peg, not fixed)
    // and every non-GCC currency -- gets no bands at all and a note saying so. Inventing a number
    // and stamping a currency code on it is what produced "SAR 28000-50000" for USD tenants.
    private static readonly Dictionary<string, (decimal Factor, decimal Step)> SarPegs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SAR"] = (1m, 100m),
        ["USD"] = (1m / 3.75m, 50m),
        ["AED"] = (3.6725m / 3.75m, 100m),
        ["QAR"] = (3.64m / 3.75m, 100m),
        ["OMR"] = (0.3845m / 3.75m, 25m),
        ["BHD"] = (0.376m / 3.75m, 25m),
    };

    private static decimal RoundTo(decimal value, decimal step)
        => step <= 0 ? value : Math.Round(value / step, MidpointRounding.AwayFromZero) * step;

    /// <summary>The employee-side statutory deduction, where one is defined. Previously the Saudi
    /// GOSI line was appended for EVERY country, so a UAE or Kuwaiti tenant applying the draft
    /// wrote a 9.75% Saudi deduction into their own pay components.</summary>
    private static DraftPayComponent? StatutoryDeductionFor(string iso3) => iso3 switch
    {
        "SAU" => new("GOSI_DED", "GOSI Deduction", "Deduction", "Percentage", 0, 9.75m, false),
        "ARE" => new("GPSSA_DED", "GPSSA Pension Deduction", "Deduction", "Percentage", 0, 5m, false),
        _ => null,
    };

    private static bool MentionsContinuousOps(string? notes)
        => !string.IsNullOrWhiteSpace(notes)
           && Regex.IsMatch(notes, @"24\s*[/x-]?\s*7|round[- ]the[- ]clock|night\s*shift|rotating\s*shift|shift\s*work|field\s*crew",
                            RegexOptions.IgnoreCase);

    private static string DescribeIndustry(string? industry) => PackFor(industry).Label;

    private static string DescribeSize(string? size) => GradeCountFor(size) switch
    {
        3 => "small-org ladder",
        7 => "large-org ladder",
        _ => "standard ladder",
    };

    //
    // This is what ships whenever no model answers, so it has to stand on its own: keyed to the
    // inputs the form actually collects, and silent about nothing. Anything it cannot derive is
    // flagged and left empty rather than guessed.
    private static SetupDraft DeterministicTemplate(CompanyProfile p, string iso3, List<string> notes)
    {
        var pack = PackFor(p.Industry);

        // Grades — rung count from headcount, money from the currency peg table.
        var count = GradeCountFor(p.CompanySize);
        var picks = count switch
        {
            3 => new[] { 0, 2, 4 },            // entry / senior / executive — a compressed ladder
            7 => new[] { 0, 1, 2, 3, 4, 5, 6 },
            _ => new[] { 0, 1, 2, 3, 4 },
        };
        var currency = string.IsNullOrWhiteSpace(p.CurrencyCode) ? "SAR" : p.CurrencyCode.Trim().ToUpperInvariant();
        var pegged = SarPegs.TryGetValue(currency, out var peg);
        if (!pegged)
            notes.Add($"Salary bands were left at zero. The built-in ladder is denominated in SAR and {currency} has no fixed relationship to it, so any figure here would be invented. Set the bands before applying.");

        var grades = new List<DraftGrade>();
        for (var i = 0; i < picks.Length; i++)
        {
            var rung = SarLadder[picks[i]];
            var code = $"G{i + 1}";
            var name = $"Grade {i + 1} - {rung.Tier}";
            grades.Add(pegged
                ? new DraftGrade(code, name, rung.Band, i + 1,
                    RoundTo(rung.Min * peg.Factor, peg.Step),
                    RoundTo(rung.Mid * peg.Factor, peg.Step),
                    RoundTo(rung.Max * peg.Factor, peg.Step), currency)
                : new DraftGrade(code, name, rung.Band, i + 1, 0m, 0m, 0m, currency));
        }

        // Roles attach to a position on the ladder, not a hard-coded "G4" that may not exist.
        string Slot(GradeSlot slot)
        {
            var n = grades.Count;
            var idx = slot switch
            {
                GradeSlot.Top => n - 1,
                GradeSlot.Upper => n - 2,
                GradeSlot.Mid => n / 2,
                _ => n >= 5 ? 1 : 0,
            };
            return grades[Math.Clamp(idx, 0, n - 1)].Code;
        }

        var depts = new List<DraftDepartment>
        {
            new("HR", "Human Resources"), new("FIN", "Finance"),
            new("IT", "Information Technology"), new("ADMIN", "Administration"),
        };
        depts.AddRange(pack.Departments);

        var desigs = new List<DraftDesignation>
        {
            new("CEO", "Chief Executive Officer", "ADMIN", Slot(GradeSlot.Top), "Executive", true, 1),
            new("HR_MGR", "HR Manager", "HR", Slot(GradeSlot.Upper), "Management", true, 3),
            new("HR_OFF", "HR Officer", "HR", Slot(GradeSlot.Low), "Staff", false, 5),
            new("ACCOUNTANT", "Accountant", "FIN", Slot(GradeSlot.Low), "Staff", false, 5),
            new("IT_ENG", "IT Engineer", "IT", Slot(GradeSlot.Mid), "Staff", false, 5),
        };
        desigs.AddRange(pack.Designations.Select(d => new DraftDesignation(
            d.Code, d.Title, d.Dept, Slot(d.Slot), d.IsManager ? "Management" : "Staff", d.IsManager, d.Rank)));

        var gradePay = grades.SelectMany(g => new[]
        {
            new DraftGradePayComponent(g.Code, "BASIC", "Basic Salary", "Earning", "Fixed", Math.Round(g.MidSalary * 0.60m, 2), 0, false, "Monthly"),
            new DraftGradePayComponent(g.Code, "HOUSING", "Housing Allowance", "Earning", "PercentOfBasic", 0, 25, false, "Monthly"),
            new DraftGradePayComponent(g.Code, "TRANSPORT", "Transport Allowance", "Earning", "Fixed", Math.Round(g.MidSalary * 0.10m, 2), 0, false, "Monthly"),
            new DraftGradePayComponent(g.Code, "OTHER", "Other Allowance", "Earning", "Fixed", Math.Round(g.MidSalary * 0.05m, 2), 0, false, "Monthly"),
        }).ToList();

        var branches = new List<DraftBranch>
        {
            new("HQ", "Head Office", string.IsNullOrWhiteSpace(p.BranchCity) ? DefaultCityFor(iso3) : p.BranchCity!, true),
        };
        var costCenters = depts.Select(d => new DraftCostCenter($"CC_{d.Code}", $"{d.NameEn} Cost Center", d.Code)).ToList();

        var leaves = new List<DraftLeaveType>
        {
            new("ANNUAL", "Annual Leave", "Standard", true, 30, false, "#00C896"),
            new("SICK", "Sick Leave", "Medical", true, 30, true, "#f59e0b"),
            new("MAT", "Maternity Leave", "Parental", true, 70, true, "#ec4899"),
            new("PAT", "Paternity Leave", "Parental", true, 3, false, "#8b5cf6"),
            new("UNPAID", "Unpaid Leave", "Unpaid", false, 30, false, "#64748b"),
        };
        if (iso3 == "SAU") leaves.Add(new("HAJJ", "Hajj Leave", "Religious", true, 10, false, "#2F6BFF"));

        // A three-shift rotation is a real operational commitment, not a default. It appears only
        // when the industry runs continuously or the free-text note says the company does.
        var continuous = pack.ContinuousOperations || MentionsContinuousOps(p.Notes);
        var shifts = continuous
            ? new List<DraftShift>
            {
                new("MORNING", "Morning Shift", "06:00", "14:00", 60, "#00C896"),
                new("EVENING", "Evening Shift", "14:00", "22:00", 60, "#8b5cf6"),
                new("NIGHT", "Night Shift", "22:00", "06:00", 60, "#2F6BFF"),
            }
            : new List<DraftShift>
            {
                new("DAY", "Day Shift", "08:00", "17:00", 60, "#2F6BFF"),
            };

        var pay = new List<DraftPayComponent>
        {
            new("BASIC", "Basic Salary", "Earning", "Fixed", 0, 0, false),
            new("HRA", "Housing Allowance", "Earning", "Percentage", 0, 25, false),
            new("TRA", "Transport Allowance", "Earning", "Fixed", 0, 0, false),
        };
        var statutory = StatutoryDeductionFor(iso3);
        if (statutory is not null) pay.Add(statutory);
        else if (p.Sections.Payroll)
            notes.Add($"No statutory payroll deduction was added — there is no built-in contribution rule for {iso3}. Add the local one before running payroll.");

        if (!string.IsNullOrWhiteSpace(p.Notes) && !continuous)
            notes.Add("Your free-text note was not applied. Without AI the template only recognises shift-pattern hints such as \"24/7\" or \"field crews\"; everything else in that box needs the AI provider.");

        return new SetupDraft(branches, depts, costCenters, desigs, grades, gradePay, leaves, shifts, null, pay, new(), EmployeeIdRuleFor(p, iso3), HrConfigFor(p));
    }

    /// <summary>"Riyadh" was the hard-coded head-office city for every country on earth.</summary>
    private static string DefaultCityFor(string iso3) => iso3 switch
    {
        "SAU" => "Riyadh",
        "ARE" => "Dubai",
        "QAT" => "Doha",
        "KWT" => "Kuwait City",
        "BHR" => "Manama",
        "OMN" => "Muscat",
        _ => "Head Office",
    };

    private static DraftEmployeeIdRule EmployeeIdRuleFor(CompanyProfile profile, string iso3)
    {
        var prefix = string.IsNullOrWhiteSpace(profile.LegalEntityName)
            ? "EMP"
            : new string(profile.LegalEntityName.Where(char.IsLetterOrDigit).Take(3).ToArray()).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(prefix)) prefix = "EMP";
        return new DraftEmployeeIdRule(prefix, true, true, true, true, 4, 1, false);
    }

    private static DraftHrConfig HrConfigFor(CompanyProfile profile)
        => new(
            UseDeptHeadApproval: true,
            UseHrFinalApproval: true,
            UseSupervisorBeforeManager: string.Equals(profile.ApprovalModel, "SupervisorFirst", StringComparison.OrdinalIgnoreCase),
            AllowDottedLineApproval: string.Equals(profile.OperatingModel, "Matrix", StringComparison.OrdinalIgnoreCase),
            AutoCreateDeptOnImport: false,
            AutoCreateDesignationOnImport: false,
            RequireImportPreviewBeforeCommit: true,
            AllowCrossDeptManager: !profile.StrictEntityScope,
            AllowCrossLocationManager: !profile.StrictEntityScope,
            RequireCostCenterForPayroll: profile.RequireCostCenterForPayroll,
            RequireGradeForApprovalPolicy: profile.RequireGradeForApprovalPolicy);

    // ── Provider/model resolution (mirrors AiAdvisoryService) ────────────────

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
        return provider switch
        {
            "anthropic" => "claude-sonnet-4-20250514",
            "openai" => "gpt-5",
            "ollama" => "llama3.1",
            _ => string.Empty,
        };
    }
}
