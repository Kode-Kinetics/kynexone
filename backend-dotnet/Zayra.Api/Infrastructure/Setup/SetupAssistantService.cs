using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Setup;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Payroll;

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
    private readonly ISetupStatutoryDefaults _statutory;
    private readonly ILogger<SetupAssistantService> _logger;

    public SetupAssistantService(ILlmClient llm, AiOptions options, IAiCallRecorder recorder,
        ISetupStatutoryDefaults statutory, ILogger<SetupAssistantService> logger)
    {
        _llm = llm;
        _options = options;
        _recorder = recorder;
        _statutory = statutory;
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

        // 2. Always deterministic: everything a labour law, a calendar or a country decides.
        //    The model is never asked for an entitlement, a multiplier or a holiday date — it is
        //    asked for names and structure, which is the only part of this it can be wrong about
        //    cheaply. Anything with no built-in figure is left empty and said out loud in a note.
        var rules = await _statutory.LoadAsync(iso3, requester.TenantId, ct);

        if (profile.Sections.Shifts)
            draft = draft with { WorkingWeek = WorkingWeekFor(profile, iso3) };
        if (profile.Sections.Governance)
            draft = draft with
            {
                EmployeeIdRule = EmployeeIdRuleFor(profile, iso3),
                HrConfig = HrConfigFor(profile)
            };
        if (profile.Sections.Payroll)
        {
            draft = draft with { StatutoryRules = StatutoryFor(profile, iso3) };
            if (draft.StatutoryRules.Count > 0)
                notes.Add("Statutory rules are country defaults — verify rates against current regulation before relying on them.");
        }
        if (profile.Sections.Localization)
            draft = draft with { Localization = LocalizationFor(profile, iso3) };
        if (profile.Sections.Attendance)
            draft = draft with
            {
                AttendancePolicy = AttendancePolicyFor(profile, notes),
                OvertimePolicy = OvertimePolicyFor(profile, iso3, rules, notes),
            };
        if (profile.Sections.Holidays)
            draft = draft with { HolidayCalendar = HolidayCalendarFor(iso3, notes) };
        // Last, because an entitlement is meaningless without the leave type it is attached to:
        // this reads whichever leave types step 1 produced, the model's or the template's.
        if (profile.Sections.Leave && profile.Sections.LeavePolicies)
            draft = draft with { LeavePolicies = LeavePoliciesFor(profile, iso3, draft.LeaveTypes, rules, notes) };
        else if (profile.Sections.LeavePolicies && !profile.Sections.Leave)
            notes.Add("Leave entitlements were skipped: an entitlement belongs to a leave type, and leave types are not part of this draft. Turn on \"Leave types\" to include them.");

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
        // The operating choices the form collects. The model shapes shifts and pay components
        // around them; it is NOT asked for entitlements, multipliers, holidays or localization,
        // which GenerateAsync fills from the country's own rules after this returns.
        sb.AppendLine($"Work pattern: {ResolveWorkPattern(p)}. Workforce: {ResolveWorkforceMix(p)}. " +
                      $"Overtime: {ResolveOvertime(p)}. Time capture: {ResolveCapture(p)}. " +
                      $"Pay cycle: {Choice(p.PayCycle, "Monthly", "Monthly", "SemiMonthly", "Biweekly", "Weekly")}.");
        if (!string.IsNullOrWhiteSpace(p.Notes)) sb.AppendLine($"Extra context: {p.Notes}");
        var want = new List<string>();
        if (p.Sections.Org) want.Add("departments, designations, grades");
        if (p.Sections.Entity) want.Add("branches and costCenters");
        if (p.Sections.Leave) want.Add("leaveTypes (align entitlements/categories to the country's labour law)");
        if (p.Sections.Shifts) want.Add("shifts (match the work pattern above: a single day shift, two shifts, a three-shift rotation, or a field roster)");
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
                null,
                // Not parsed from the model by design — see GenerateAsync step 2. These are filled
                // from the country's own rules, so a model that invents them is ignored.
                new(),
                null,
                null,
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

        // Leave entitlement. A policy whose leave type did not survive normalisation is dropped
        // rather than kept pointing at nothing — the apply step resolves the type by code, and an
        // unresolvable one would be silently skipped there instead of visibly missing here.
        var leaveCodes = leaves.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leavePolicies = !(s.Leave && s.LeavePolicies) ? new() : Dedup(
            d.LeavePolicies
                .Select(x => x with { LeaveTypeCode = Code(x.LeaveTypeCode, string.Empty) })
                .Where(x => leaveCodes.Contains(x.LeaveTypeCode))
                .Select(x => x with
                {
                    Name = string.IsNullOrWhiteSpace(x.Name) ? $"{x.LeaveTypeCode} Policy" : x.Name.Trim(),
                    AnnualEntitlementDays = Math.Clamp(x.AnnualEntitlementDays, 0m, 365m),
                    AccrualMethod = string.Equals(x.AccrualMethod, "Monthly", StringComparison.OrdinalIgnoreCase) ? "Monthly" : "Yearly",
                    EncashmentMaxDays = Math.Clamp(x.EncashmentMaxDays, 0m, 365m),
                    MinimumDaysPerRequest = Math.Clamp(x.MinimumDaysPerRequest, 0m, 365m),
                    MaximumDaysPerRequest = Math.Clamp(x.MaximumDaysPerRequest, 0m, 365m),
                    NoticeRequiredDays = Math.Clamp(x.NoticeRequiredDays, 0, 365),
                    PayrollImpact = string.Equals(x.PayrollImpact, "Unpaid", StringComparison.OrdinalIgnoreCase) ? "Unpaid" : "Full",
                }),
            x => x.LeaveTypeCode);

        // Holidays. A row whose date will not parse is dropped: attendance and payroll read this
        // calendar by date, so a malformed one is not a holiday, it is a crash waiting for the run.
        var calendar = !s.Holidays ? null : d.HolidayCalendar is null ? null : d.HolidayCalendar with
        {
            Holidays = Dedup(
                d.HolidayCalendar.Holidays
                    .Where(h => !string.IsNullOrWhiteSpace(h.NameEn)
                                && DateOnly.TryParseExact(h.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    .Select(h => h with
                    {
                        NameEn = h.NameEn.Trim(),
                        HolidayType = string.IsNullOrWhiteSpace(h.HolidayType) ? "National" : h.HolidayType.Trim(),
                    }),
                h => h.Date),
        };

        var attendance = !s.Attendance ? null : d.AttendancePolicy is null ? null : d.AttendancePolicy with
        {
            Code = Code(d.AttendancePolicy.Code, "STD_ATT"),
            GraceMinutes = Math.Clamp(d.AttendancePolicy.GraceMinutes, 0, 120),
            LateThresholdMinutes = Math.Clamp(d.AttendancePolicy.LateThresholdMinutes, 0, 480),
            EarlyExitThresholdMinutes = Math.Clamp(d.AttendancePolicy.EarlyExitThresholdMinutes, 0, 480),
            HalfDayThresholdMinutes = Math.Clamp(d.AttendancePolicy.HalfDayThresholdMinutes, 0, 960),
            AbsentThresholdMinutes = Math.Clamp(d.AttendancePolicy.AbsentThresholdMinutes, 0, 960),
            StandardWorkMinutes = Math.Clamp(d.AttendancePolicy.StandardWorkMinutes, 60, 960),
            BreakMinutes = Math.Clamp(d.AttendancePolicy.BreakMinutes, 0, 240),
            // Only the two rules OvertimeController can actually evaluate; a third would be stored
            // and ignored, which is worse than being corrected here.
            RoundingRule = string.Equals(d.AttendancePolicy.RoundingRule, "NearestMinute", StringComparison.OrdinalIgnoreCase)
                ? "NearestMinute" : "Nearest15",
        };

        var overtime = !s.Attendance ? null : d.OvertimePolicy is null ? null : d.OvertimePolicy with
        {
            Code = Code(d.OvertimePolicy.Code, "STD_OT"),
            StandardMonthlyHours = Math.Clamp(d.OvertimePolicy.StandardMonthlyHours, 1, 400),
            MinimumMinutes = Math.Clamp(d.OvertimePolicy.MinimumMinutes, 0, 480),
            MaximumMinutesPerDay = Math.Clamp(d.OvertimePolicy.MaximumMinutesPerDay, 0, 960),
            MonthlyCapMinutes = Math.Clamp(d.OvertimePolicy.MonthlyCapMinutes, 0, 30000),
            RoundingRule = string.Equals(d.OvertimePolicy.RoundingRule, "NearestMinute", StringComparison.OrdinalIgnoreCase)
                ? "NearestMinute" : "Nearest15",
            // A multiplier below 1 pays an overtime hour less than an ordinary one. It is never a
            // rounding artefact; it is either a typo or an unlawful rate, and both are dropped.
            Multipliers = Dedup(
                d.OvertimePolicy.Multipliers.Where(m => m.Multiplier >= 1m && m.Multiplier <= 5m),
                m => m.DayCategory ?? string.Empty),
        };

        var localization = !s.Localization ? null : d.Localization is null ? null : d.Localization with
        {
            DefaultLanguage = string.Equals(d.Localization.DefaultLanguage, "ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en",
            CalendarSystem = string.Equals(d.Localization.CalendarSystem, "Hijri", StringComparison.OrdinalIgnoreCase) ? "Hijri" : "Gregorian",
            DefaultTimezone = string.IsNullOrWhiteSpace(d.Localization.DefaultTimezone) ? "UTC" : d.Localization.DefaultTimezone.Trim(),
            DateFormat = string.Equals(d.Localization.DateFormat, "MM/DD/YYYY", StringComparison.OrdinalIgnoreCase) ? "MM/DD/YYYY"
                : string.Equals(d.Localization.DateFormat, "YYYY-MM-DD", StringComparison.OrdinalIgnoreCase) ? "YYYY-MM-DD"
                : "DD/MM/YYYY",
        };

        return d with
        {
            Branches = branches, Departments = depts, CostCenters = costCenters, Designations = desigs, Grades = grades,
            GradePayComponents = gradePay,
            LeaveTypes = leaves, Shifts = shifts, PayComponents = pay,
            WorkingWeek = s.Shifts ? d.WorkingWeek : null,
            StatutoryRules = s.Payroll ? d.StatutoryRules : new(),
            EmployeeIdRule = s.Governance ? d.EmployeeIdRule : null,
            HrConfig = s.Governance ? d.HrConfig : null,
            LeavePolicies = leavePolicies,
            HolidayCalendar = calendar,
            AttendancePolicy = attendance,
            OvertimePolicy = overtime,
            Localization = localization,
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

    /// <summary>The country's usual working week, used when the customer leaves the weekend on
    /// "country default".</summary>
    private static DraftWorkingWeek WorkingWeekFor(string iso3) => iso3 switch
    {
        "SAU" or "QAT" or "KWT" or "BHR" or "OMN" => new DraftWorkingWeek("Sun-Thu", "Sunday"),
        "ARE" => new DraftWorkingWeek("Mon-Fri", "Monday"),
        _ => new DraftWorkingWeek("Mon-Fri", "Monday"),
    };

    /// <summary>
    /// The working week, from the REST days the customer chose.
    ///
    /// <para>The form asks for the weekend because that is how a business describes itself ("we're
    /// off Friday and Saturday"); the column stores the complement, which is what
    /// <c>WorkWeekParser</c> reads — it parses a WORKING-week range and returns the rest days. A
    /// company off only Friday is a real GCC pattern that the country default cannot express, and
    /// it changes every overtime and leave-day count in the product.</para>
    /// </summary>
    private static DraftWorkingWeek WorkingWeekFor(CompanyProfile p, string iso3)
        => (p.WeekendPattern ?? "").Trim().ToUpperInvariant() switch
        {
            "FRI-SAT" => new DraftWorkingWeek("Sun-Thu", "Sunday"),
            "SAT-SUN" => new DraftWorkingWeek("Mon-Fri", "Monday"),
            "FRI" => new DraftWorkingWeek("Sat-Thu", "Saturday"),
            "SUN" => new DraftWorkingWeek("Mon-Sat", "Monday"),
            _ => WorkingWeekFor(iso3),
        };

    /// <summary>
    /// The country's contribution rules, plus the four employment terms the form now collects.
    ///
    /// <para>Probation, notice, pay cycle and leave-year basis have no column of their own on any
    /// entity — they are tenant rules, and <c>statutory_rules</c> is where this product keeps
    /// tenant rules. Each is written only when the customer actually chose a value, so an untouched
    /// field does not become a rule that says "0 days' notice".</para>
    /// </summary>
    private static List<DraftStatutoryRule> StatutoryFor(CompanyProfile p, string iso3)
    {
        var rules = CountryStatutoryFor(iso3);

        if (p.ProbationMonths > 0)
            rules.Add(new("employment.probation_months", p.ProbationMonths.ToString(CultureInfo.InvariantCulture), "int",
                "Default probation length for new hires, in months."));
        if (p.NoticePeriodDays > 0)
            rules.Add(new("employment.notice_period_days", p.NoticePeriodDays.ToString(CultureInfo.InvariantCulture), "int",
                "Default notice period on termination, in days."));

        var cycle = Choice(p.PayCycle, "", "Monthly", "SemiMonthly", "Biweekly", "Weekly");
        if (cycle.Length > 0)
            rules.Add(new("payroll.pay_cycle", cycle, "string", "How often payroll runs."));

        var basis = Choice(p.LeaveYearBasis, "", "Calendar", "JoiningDate", "Fiscal");
        if (basis.Length > 0)
            rules.Add(new("leave.year_basis", basis, "string",
                "What a leave year is measured from: Calendar (1 January), JoiningDate (each employee's anniversary) or Fiscal."));

        return rules;
    }

    private static List<DraftStatutoryRule> CountryStatutoryFor(string iso3) => iso3 switch
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

    // ── Operating choices → configuration the product actually reads ────────
    //
    // Every helper below fills a column that existed before this form did and that the setup
    // previously left at its construction-time default. A Saudi tenant was written
    // America/New_York and MM/DD/YYYY; its leave types were created granting nobody any days.

    /// <summary>Case-insensitive pick from a closed list. Anything the client invents falls back
    /// to <paramref name="fallback"/> rather than reaching a column.</summary>
    private static string Choice(string? raw, string fallback, params string[] allowed)
        => allowed.FirstOrDefault(a => string.Equals(a, (raw ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
           ?? fallback;

    /// <summary>The shift pattern. An explicit choice wins; otherwise this is the inference the
    /// service has always made — industry, then a regex over the free-text note.</summary>
    private static string ResolveWorkPattern(CompanyProfile p)
    {
        var chosen = Choice(p.WorkPattern, string.Empty,
            "SingleDayShift", "TwoShifts", "ContinuousThreeShifts", "FieldRoster");
        if (chosen.Length > 0) return chosen;
        return PackFor(p.Industry).ContinuousOperations || MentionsContinuousOps(p.Notes)
            ? "ContinuousThreeShifts"
            : "SingleDayShift";
    }

    private static string ResolveCapture(CompanyProfile p)
        => Choice(p.AttendanceCapture, "WebCheckIn", "BiometricDevice", "MobileGeofence", "WebCheckIn", "Manual");

    private static string ResolveOvertime(CompanyProfile p)
        => Choice(p.OvertimeHandling, "PaidOvertime", "PaidOvertime", "CompensatoryOff", "NotApplicable");

    private static string ResolveWorkforceMix(CompanyProfile p)
        => Choice(p.WorkforceMix, "Mixed", "MostlyNational", "Mixed", "MostlyExpat");

    private static List<DraftShift> ShiftsFor(string pattern) => pattern switch
    {
        "ContinuousThreeShifts" => new()
        {
            new("MORNING", "Morning Shift", "06:00", "14:00", 60, "#00C896"),
            new("EVENING", "Evening Shift", "14:00", "22:00", 60, "#8b5cf6"),
            new("NIGHT", "Night Shift", "22:00", "06:00", 60, "#2F6BFF"),
        },
        "TwoShifts" => new()
        {
            new("DAY", "Day Shift", "08:00", "16:00", 60, "#2F6BFF"),
            new("EVENING", "Evening Shift", "16:00", "00:00", 60, "#8b5cf6"),
        },
        "FieldRoster" => new()
        {
            new("FIELD", "Field Shift", "06:00", "18:00", 90, "#f59e0b"),
            new("OFFICE", "Office Shift", "08:00", "17:00", 60, "#2F6BFF"),
        },
        _ => new()
        {
            new("DAY", "Day Shift", "08:00", "17:00", 60, "#2F6BFF"),
        },
    };

    // ── Attendance & overtime ───────────────────────────────────────────────

    /// <summary>
    /// Thresholds a policy can honestly claim for the way this company actually records time.
    ///
    /// <para>Grace and rounding follow the capture method because the method sets the precision:
    /// a biometric reader is accurate to the minute, so a 5-minute grace is a real allowance; a
    /// manually keyed timesheet is not, so a 5-minute grace would be arithmetic performed on a
    /// number nobody measured. <c>RoundingRule</c> is limited to the two values
    /// <c>OvertimeController</c> can evaluate — a third would be stored and ignored.</para>
    /// </summary>
    private static DraftAttendancePolicy AttendancePolicyFor(CompanyProfile p, List<string> notes)
    {
        var capture = ResolveCapture(p);
        var pattern = ResolveWorkPattern(p);
        var field = pattern == "FieldRoster";

        var grace = capture switch
        {
            "BiometricDevice" => 5,
            "MobileGeofence" => 10,
            "WebCheckIn" => 15,
            _ => 30,
        };
        var rounding = capture == "BiometricDevice" ? "NearestMinute" : "Nearest15";
        var standard = field ? 540 : 480;
        var breakMinutes = field ? 90 : 60;

        if (capture == "Manual")
            notes.Add("Attendance is set to manual entry, so lateness thresholds are advisory: they measure what someone typed, not when anyone arrived. Grace is 30 minutes and times are rounded to the quarter-hour to avoid implying a precision the source does not have.");
        if (capture == "MobileGeofence")
            notes.Add("Mobile check-in needs a geofence per work location before it can reject an off-site punch. The draft creates the policy; the locations and their radii are not part of it.");

        return new DraftAttendancePolicy(
            Code: "STD_ATT",
            Name: field ? "Field Attendance Policy" : "Standard Attendance Policy",
            GraceMinutes: grace,
            LateThresholdMinutes: grace + 5,
            EarlyExitThresholdMinutes: 15,
            HalfDayThresholdMinutes: standard / 2,
            AbsentThresholdMinutes: 120,
            StandardWorkMinutes: standard,
            BreakMinutes: breakMinutes,
            RoundingRule: rounding,
            RequiresOvertimeApproval: ResolveOvertime(p) != "NotApplicable",
            AllowAbsenceToLeaveConversion: true);
    }

    /// <summary>
    /// The overtime policy, with its multipliers taken from the platform's own statutory rules.
    ///
    /// <para>The rates are not written here. <c>ot.standard_multiplier</c> and its rest-day and
    /// holiday siblings are seeded per jurisdiction, effective-dated and flagged for labour-law
    /// sign-off; a second copy in this file would be a second set of legal figures to keep in step
    /// with the first. Where the platform has no rate the multipliers are left empty and the note
    /// says which country has none — a drafted 1.5 for a jurisdiction nobody has checked is worse
    /// than no number, because it would be approved and then paid.</para>
    /// </summary>
    private static DraftOvertimePolicy? OvertimePolicyFor(
        CompanyProfile p, string iso3, IReadOnlyDictionary<string, string> rules, List<string> notes)
    {
        var handling = ResolveOvertime(p);
        if (handling == "NotApplicable")
        {
            notes.Add("No overtime policy was drafted, because you said overtime does not apply. Hours beyond the standard day will still be recorded by attendance; nothing will cost them.");
            return null;
        }

        decimal? Rate(string key)
            => rules.TryGetValue(key, out var raw)
               && decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
               && value > 0
                ? value : null;

        var standard = Rate(OvertimeStatutoryContextResolver.RuleStandardMultiplier);
        var multipliers = new List<DraftOvertimeMultiplier>();
        if (standard is { } std)
        {
            multipliers.Add(new(OvertimeStatutoryCalculator.DayCategoryRegular, std));
            multipliers.Add(new(OvertimeStatutoryCalculator.DayCategoryWeekend, Rate(OvertimeStatutoryContextResolver.RuleRestDayMultiplier) ?? std));
            multipliers.Add(new(OvertimeStatutoryCalculator.DayCategoryPublicHoliday, Rate(OvertimeStatutoryContextResolver.RuleHolidayMultiplier) ?? std));
        }
        else
        {
            notes.Add($"Overtime multipliers were left empty: this build has no statutory overtime rate on file for {iso3}. Set the ordinary, rest-day and public-holiday rates from local law before running payroll — the policy will not cost an overtime hour without them.");
        }

        var monthlyHours = Rate(OvertimeStatutoryContextResolver.RuleStandardMonthlyHours) is { } h and >= 1 and <= 400 ? (int)h : 240;

        return new DraftOvertimePolicy(
            Code: "STD_OT",
            Name: "Standard Overtime Policy",
            HourlyRateBasis: OvertimeStatutoryCalculator.BasisBasicSalary,
            StandardMonthlyHours: monthlyHours,
            MinimumMinutes: 30,
            MaximumMinutesPerDay: 240,
            MonthlyCapMinutes: 3600,
            RoundingRule: "Nearest15",
            RequiresApproval: true,
            // The whole point of choosing time off in lieu; still true for paid overtime, where
            // an employee may prefer the day back, which is why this is not the same question.
            AllowCompOffConversion: handling == "CompensatoryOff",
            Multipliers: multipliers);
    }

    // ── Leave entitlement ───────────────────────────────────────────────────

    /// <summary>
    /// One policy per leave type, because <c>LeaveType</c> carries no entitlement: it is
    /// <c>LeavePolicy.AnnualEntitlementDays</c> that the leave engine reads. Applying a draft
    /// without these produced a tenant whose leave types existed and granted nobody any days.
    ///
    /// <para>Annual and sick days come from the platform's statutory rules for the country
    /// (<c>leave.annual_base_days</c>, <c>leave.sick_band1_days</c>). Where there is no rule the
    /// entitlement is left at zero and named in a note, the same way an unpegged currency leaves
    /// the salary bands at zero rather than inventing them.</para>
    /// </summary>
    private static List<DraftLeavePolicy> LeavePoliciesFor(
        CompanyProfile p, string iso3, List<DraftLeaveType> types,
        IReadOnlyDictionary<string, string> rules, List<string> notes)
    {
        if (types.Count == 0) return new();

        decimal? Days(string key)
            => rules.TryGetValue(key, out var raw)
               && decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
               && value > 0
                ? value : null;

        var annual = Days("leave.annual_base_days");
        var sick = Days("leave.sick_band1_days");
        var probation = p.ProbationMonths > 0;
        var policies = new List<DraftLeavePolicy>();
        var unsourced = new List<string>();

        foreach (var t in types)
        {
            var key = $"{t.Code} {t.NameEn} {t.Category}".ToUpperInvariant();
            bool Is(params string[] needles) => needles.Any(key.Contains);

            var isUnpaid = !t.IsPaid || Is("UNPAID");
            var isAnnual = !isUnpaid && Is("ANNUAL", "VACATION");
            var isSick = !isUnpaid && Is("SICK", "MEDICAL");

            // The per-request ceiling the leave type already carries is the entitlement for the
            // fixed-span leaves (maternity, paternity, Hajj, bereavement): the span IS the grant.
            // It is not a stand-in for annual or sick leave, where the two are different numbers.
            decimal entitlement;
            if (isUnpaid) entitlement = 0m;
            else if (isAnnual) entitlement = annual ?? 0m;
            else if (isSick) entitlement = sick ?? 0m;
            else entitlement = t.MaxConsecutiveDays;

            if (entitlement <= 0m && !isUnpaid) unsourced.Add(t.NameEn);

            policies.Add(new DraftLeavePolicy(
                Name: $"{t.NameEn} Policy",
                LeaveTypeCode: t.Code,
                AnnualEntitlementDays: entitlement,
                // Annual leave builds up over the year; a fixed-span leave is granted whole when
                // the event happens, which is what "Yearly" means in this column.
                AccrualMethod: isAnnual ? "Monthly" : "Yearly",
                EncashmentAllowed: isAnnual,
                EncashmentMaxDays: 0m,
                MinimumDaysPerRequest: 1m,
                MaximumDaysPerRequest: t.MaxConsecutiveDays,
                // Sick leave cannot be booked in advance, so requiring notice for it would block
                // the request it is meant to govern.
                NoticeRequiredDays: isAnnual ? 7 : 0,
                WeekendsIncluded: false,
                PublicHolidaysIncluded: false,
                AppliesOnProbation: probation && !isAnnual,
                PayrollImpact: isUnpaid ? "Unpaid" : "Full"));
        }

        if (unsourced.Count > 0)
            notes.Add($"These leave types were drafted with an entitlement of 0 days because this build has no statutory figure on file for {iso3}: {string.Join(", ", unsourced)}. Set the days before applying, or the leave type will exist and grant nobody anything.");
        if (annual is not null)
            notes.Add($"Annual leave is drafted at {annual.Value:0.##} days — the statutory floor for {iso3}, not a recommendation. Raise it if your contracts promise more; it cannot lawfully go lower.");
        if (sick is not null)
            notes.Add($"Sick leave is drafted at {sick.Value:0.##} days, which is the FULL-PAY band only. Longer sickness continues at reduced or nil pay under the statutory scale, which payroll applies separately — the entitlement here is not the whole picture.");

        notes.Add("Leave days are counted in working days: weekends and public holidays inside a request are not deducted. Check this against your own contracts — some entitlements are expressed in calendar days, which is a different number.");
        notes.Add("Carry-forward is not part of the draft. This build has no year-end rollover, so a cap would be stored and never consulted; unused balance is handled through encashment.");
        if (probation)
            notes.Add($"Probation is set to {p.ProbationMonths} month(s). Annual leave is drafted as not available during probation; sick and the fixed-span leaves are.");

        return policies;
    }

    // ── Public holidays ─────────────────────────────────────────────────────

    private static DateOnly NthWeekday(int year, int month, DayOfWeek day, int nth)
    {
        var d = new DateOnly(year, month, 1);
        while (d.DayOfWeek != day) d = d.AddDays(1);
        return d.AddDays(7 * (nth - 1));
    }

    /// <summary>
    /// The current year's public holidays that have a fixed Gregorian date.
    ///
    /// <para>Eid al-Fitr, Eid al-Adha, the Islamic New Year and the Prophet's Birthday move with
    /// the lunar calendar and are announced locally, often by moon sighting days beforehand. A
    /// drafted date for them would be wrong most years, and wrong by a day is enough to misprice
    /// every overtime hour worked over the holiday and to deduct leave that was never taken. They
    /// are named in a note instead, so the gap is visible rather than filled.</para>
    /// </summary>
    private static DraftHolidayCalendar? HolidayCalendarFor(string iso3, List<string> notes)
    {
        var year = DateTime.UtcNow.Year;
        string D(int month, int day) => new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        DraftHoliday H(string en, string ar, string date, string type = "National", bool optional = false)
            => new(en, ar, date, IsRecurring: true, IsOptional: optional, HolidayType: type, Notes: string.Empty);

        var newYear = H("New Year's Day", "رأس السنة الميلادية", D(1, 1));

        var holidays = iso3 switch
        {
            "SAU" => new List<DraftHoliday>
            {
                H("Founding Day", "يوم التأسيس", D(2, 22)),
                H("Saudi National Day", "اليوم الوطني السعودي", D(9, 23)),
            },
            "ARE" => new List<DraftHoliday>
            {
                newYear,
                H("Commemoration Day", "يوم الشهيد", D(12, 1)),
                H("UAE National Day", "اليوم الوطني للإمارات", D(12, 2)),
                H("UAE National Day Holiday", "عطلة اليوم الوطني", D(12, 3)),
            },
            "QAT" => new List<DraftHoliday>
            {
                H("National Sports Day", "اليوم الرياضي للدولة",
                  NthWeekday(year, 2, DayOfWeek.Tuesday, 2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                H("Qatar National Day", "اليوم الوطني لقطر", D(12, 18)),
            },
            "KWT" => new List<DraftHoliday>
            {
                newYear,
                H("Kuwait National Day", "العيد الوطني الكويتي", D(2, 25)),
                H("Liberation Day", "يوم التحرير", D(2, 26)),
            },
            "BHR" => new List<DraftHoliday>
            {
                newYear,
                H("Bahrain National Day", "العيد الوطني البحريني", D(12, 16)),
                H("Bahrain National Day Holiday", "عطلة العيد الوطني", D(12, 17)),
            },
            "OMN" => new List<DraftHoliday>
            {
                newYear,
                H("Oman National Day", "العيد الوطني العماني", D(11, 18)),
                H("Oman National Day Holiday", "عطلة العيد الوطني", D(11, 19)),
            },
            _ => new List<DraftHoliday>(),
        };

        if (holidays.Count == 0)
        {
            notes.Add($"No public holiday calendar was drafted: this build has no built-in holiday list for {iso3}. Add the year's holidays before running attendance or payroll over them — an unlisted holiday is treated as an ordinary working day.");
            return null;
        }

        notes.Add($"The {year} holiday calendar contains only the fixed-date holidays. Eid al-Fitr, Eid al-Adha, the Islamic New Year and the Prophet's Birthday follow the lunar calendar and are confirmed locally close to the date, so they are deliberately not drafted — add them once announced.");
        return new DraftHolidayCalendar($"{iso3} Public Holidays {year}", year, holidays);
    }

    // ── Localization ────────────────────────────────────────────────────────

    /// <summary>
    /// Language, calendar, timezone and date format for the country.
    ///
    /// <para><c>TenantLocalizationSetting</c> is constructed with America/New_York, MM/DD/YYYY and
    /// a Monday week start. The setup wrote only the work week and currency over that, so every
    /// Gulf tenant this assistant has ever configured has been sitting on a New York clock and a
    /// US date format — visible on every timestamp in the product.</para>
    /// </summary>
    private static DraftLocalization LocalizationFor(CompanyProfile p, string iso3)
    {
        var gulf = iso3 is "SAU" or "ARE" or "QAT" or "KWT" or "BHR" or "OMN";
        var language = Choice(p.DefaultLanguage, gulf ? "en" : "en", "en", "ar", "bilingual");

        var timezone = !string.IsNullOrWhiteSpace(p.TimeZone) ? p.TimeZone!.Trim() : iso3 switch
        {
            "SAU" => "Asia/Riyadh",
            "ARE" => "Asia/Dubai",
            "QAT" => "Asia/Qatar",
            "KWT" => "Asia/Kuwait",
            "BHR" => "Asia/Bahrain",
            "OMN" => "Asia/Muscat",
            "EGY" => "Africa/Cairo",
            "IND" => "Asia/Kolkata",
            "GBR" => "Europe/London",
            "USA" => "America/New_York",
            _ => "UTC",
        };

        return new DraftLocalization(
            // "bilingual" is not a value the column holds: the product ships both languages and the
            // setting picks which one a new user starts in. English is the safer start for a mixed
            // workforce, with right-to-left support on so Arabic is one click away.
            DefaultLanguage: language == "ar" ? "ar" : "en",
            RtlEnabled: gulf || language is "ar" or "bilingual",
            CalendarSystem: "Gregorian",
            DefaultTimezone: timezone,
            DateFormat: iso3 == "USA" ? "MM/DD/YYYY" : "DD/MM/YYYY",
            // Hijri dates appear alongside Gregorian ones; the working calendar stays Gregorian,
            // which is what payroll periods and contracts are dated in.
            HijriDatesEnabled: gulf);
    }

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

        // The ladder's figures are monthly; a shorter pay cycle pays a fraction of each one, so the
        // frequency has to travel with the component or the first run pays a month's salary weekly.
        var frequency = Choice(p.PayCycle, "Monthly", "Monthly", "SemiMonthly", "Biweekly", "Weekly");
        var cycleDivisor = frequency switch { "SemiMonthly" => 2m, "Biweekly" => 26m / 12m, "Weekly" => 52m / 12m, _ => 1m };
        decimal PerRun(decimal monthly) => Math.Round(monthly / cycleDivisor, 2);
        if (frequency != "Monthly")
            notes.Add($"Pay is set to {frequency.ToLowerInvariant()}, so each component holds the per-run amount rather than the monthly one. The grade bands above stay monthly, which is how they are compared to the market.");

        var gradePay = grades.SelectMany(g => new[]
        {
            new DraftGradePayComponent(g.Code, "BASIC", "Basic Salary", "Earning", "Fixed", PerRun(g.MidSalary * 0.60m), 0, false, frequency),
            new DraftGradePayComponent(g.Code, "HOUSING", "Housing Allowance", "Earning", "PercentOfBasic", 0, 25, false, frequency),
            new DraftGradePayComponent(g.Code, "TRANSPORT", "Transport Allowance", "Earning", "Fixed", PerRun(g.MidSalary * 0.10m), 0, false, frequency),
            new DraftGradePayComponent(g.Code, "OTHER", "Other Allowance", "Earning", "Fixed", PerRun(g.MidSalary * 0.05m), 0, false, frequency),
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

        // A three-shift rotation is a real operational commitment, not a default. The customer now
        // states the pattern directly; where they left it unset, ResolveWorkPattern falls back to
        // the old inference (industry, then a regex over the free-text note).
        var pattern = ResolveWorkPattern(p);
        var continuous = pattern == "ContinuousThreeShifts";
        var shifts = ShiftsFor(pattern);

        var pay = new List<DraftPayComponent>
        {
            new("BASIC", "Basic Salary", "Earning", "Fixed", 0, 0, false),
            new("HRA", "Housing Allowance", "Earning", "Percentage", 0, 25, false),
            new("TRA", "Transport Allowance", "Earning", "Fixed", 0, 0, false),
        };
        // An expatriate workforce carries contractual entitlements a national one does not. The
        // amount is left at zero deliberately: an annual ticket accrual is a route and a class,
        // not a figure this form can know.
        var mix = ResolveWorkforceMix(p);
        if (mix is "Mixed" or "MostlyExpat")
        {
            pay.Add(new("AIR_TICKET", "Annual Air Ticket Accrual", "Earning", "Fixed", 0, 0, false));
            notes.Add("An annual air-ticket accrual was added for your expatriate staff with no amount — set the monthly accrual from the routes and cabin class your contracts promise.");
        }
        var statutory = StatutoryDeductionFor(iso3);
        if (statutory is not null) pay.Add(statutory);
        else if (p.Sections.Payroll)
            notes.Add($"No statutory payroll deduction was added — there is no built-in contribution rule for {iso3}. Add the local one before running payroll.");
        if (mix == "MostlyExpat" && iso3 is "SAU" or "ARE")
            notes.Add($"The {(iso3 == "SAU" ? "GOSI" : "GPSSA")} deduction in this draft is the rate for nationals. It does not apply to expatriate staff, who are on a different (usually smaller) contribution — check which of your employees it should be attached to before running payroll.");

        if (!string.IsNullOrWhiteSpace(p.Notes) && !continuous)
            notes.Add("Your free-text note was not applied. Without AI the template only recognises shift-pattern hints such as \"24/7\" or \"field crews\"; everything else in that box needs the AI provider.");

        // The five trailing members are filled by GenerateAsync from country data, never here:
        // entitlements, holidays, attendance, overtime and localization are not industry guesses.
        return new SetupDraft(branches, depts, costCenters, desigs, grades, gradePay, leaves, shifts, null, pay, new(),
            EmployeeIdRuleFor(p, iso3), HrConfigFor(p), new(), null, null, null, null);
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
