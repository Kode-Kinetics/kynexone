using System.Text.Json;
using System.Text.Json.Serialization;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Engagement;

/// <summary>Question types a template may use.</summary>
public static class EngagementQuestionTypes
{
    /// <summary>5-point agreement scale: 1 Strongly disagree … 5 Strongly agree. Favourable = 4 or 5.</summary>
    public const string Likert5 = "likert5";
    /// <summary>0–10 likelihood to recommend. Promoter 9–10, passive 7–8, detractor 0–6.</summary>
    public const string Enps = "enps";
    public const string SingleChoice = "single";
    public const string MultiChoice = "multi";
    public const string FreeText = "text";
    public static readonly string[] All = [Likert5, Enps, SingleChoice, MultiChoice, FreeText];
}

/// <summary>A driver (category) questions roll up into, e.g. "Recognition".</summary>
public sealed record EngagementDriver(string Key, string LabelEn, string LabelAr);

public sealed record EngagementOption(string Key, string LabelEn, string LabelAr);

public sealed record EngagementQuestion(
    string Key,
    string Type,
    string TextEn,
    string TextAr,
    bool Required,
    string? Driver = null,
    IReadOnlyList<EngagementOption>? Options = null);

/// <summary>The template's content: drivers and ordered questions. Serialised to <c>survey_templates.content_json</c>.</summary>
public sealed record EngagementTemplateContent(
    IReadOnlyList<EngagementDriver> Drivers,
    IReadOnlyList<EngagementQuestion> Questions)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static EngagementTemplateContent Parse(string json) =>
        JsonSerializer.Deserialize<EngagementTemplateContent>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Json)
        ?? new EngagementTemplateContent([], []);

    /// <summary>Problems that block saving or publishing, in plain words. Empty = valid.</summary>
    public IReadOnlyList<string> Validate(bool forPublish)
    {
        var errors = new List<string>();
        var drivers = Drivers ?? [];
        var questions = Questions ?? [];
        if (forPublish && questions.Count == 0) errors.Add("Add at least one question before publishing.");
        if (questions.Count > 60) errors.Add("A survey may ask at most 60 questions.");
        var driverKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in drivers)
        {
            if (!ValidKey(d.Key)) errors.Add($"Driver key '{d.Key}' must be 1–40 letters, digits, '-' or '_'.");
            else if (!driverKeys.Add(d.Key)) errors.Add($"Driver '{d.Key}' is listed twice.");
            if (string.IsNullOrWhiteSpace(d.LabelEn) || string.IsNullOrWhiteSpace(d.LabelAr))
                errors.Add($"Driver '{d.Key}' needs an English and an Arabic name.");
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var enpsCount = 0;
        foreach (var q in questions)
        {
            var label = string.IsNullOrWhiteSpace(q.Key) ? "(no key)" : q.Key;
            if (!ValidKey(q.Key)) errors.Add($"Question key '{label}' must be 1–40 letters, digits, '-' or '_'.");
            else if (!keys.Add(q.Key)) errors.Add($"Question '{q.Key}' is listed twice.");
            if (!EngagementQuestionTypes.All.Contains(q.Type, StringComparer.Ordinal))
                errors.Add($"Question '{label}' has an unknown type '{q.Type}'.");
            if (string.IsNullOrWhiteSpace(q.TextEn) || string.IsNullOrWhiteSpace(q.TextAr))
                errors.Add($"Question '{label}' needs English and Arabic text.");
            if ((q.TextEn?.Length ?? 0) > 500 || (q.TextAr?.Length ?? 0) > 500)
                errors.Add($"Question '{label}' is longer than 500 characters.");
            if (q.Driver is not null && !driverKeys.Contains(q.Driver))
                errors.Add($"Question '{label}' belongs to driver '{q.Driver}', which the template does not define.");
            if (q.Type == EngagementQuestionTypes.Enps) enpsCount++;
            if (q.Type is EngagementQuestionTypes.SingleChoice or EngagementQuestionTypes.MultiChoice)
            {
                var options = q.Options ?? [];
                if (options.Count < 2) errors.Add($"Question '{label}' needs at least two options.");
                if (options.Count > 20) errors.Add($"Question '{label}' may offer at most 20 options.");
                var optionKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var o in options)
                {
                    // ≤ 16 characters: 20 options × 16 + separators fits survey_answers.choice_keys (400).
                    if (!ValidKey(o.Key) || o.Key.Length > 16) errors.Add($"Option key '{o.Key}' in '{label}' must be 1–16 letters, digits, '-' or '_'.");
                    else if (!optionKeys.Add(o.Key)) errors.Add($"Option '{o.Key}' in '{label}' is listed twice.");
                    if (string.IsNullOrWhiteSpace(o.LabelEn) || string.IsNullOrWhiteSpace(o.LabelAr))
                        errors.Add($"Option '{o.Key}' in '{label}' needs an English and an Arabic label.");
                }
            }
            else if (q.Options is { Count: > 0 })
            {
                errors.Add($"Question '{label}' is not a choice question, so it cannot have options.");
            }
        }
        if (enpsCount > 1) errors.Add("A survey may ask at most one eNPS question, so eNPS has one meaning.");
        return errors;
    }

    private static bool ValidKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= 40 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>Who is invited. Empty lists mean "no restriction on this attribute".</summary>
public sealed record EngagementAudience(
    IReadOnlyList<Guid>? CompanyIds = null,
    IReadOnlyList<Guid>? DepartmentIds = null,
    IReadOnlyList<Guid>? DesignationIds = null,
    IReadOnlyList<Guid>? GradeIds = null,
    int ExcludeJoinedWithinDays = 0)
{
    public string Serialize() => JsonSerializer.Serialize(this, EngagementTemplateContent.Json);

    public static EngagementAudience Parse(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new EngagementAudience()
            : JsonSerializer.Deserialize<EngagementAudience>(json, EngagementTemplateContent.Json) ?? new EngagementAudience();
}

/// <summary>One submitted answer, as the respondent sends it.</summary>
public sealed record EngagementAnswerInput(string QuestionKey, int? Value = null, IReadOnlyList<string>? Choices = null, string? Text = null);

/// <summary>
/// What each mode promises, in the respondent's language. The respondent sees this text VERBATIM before answering,
/// and it describes exactly what the implementation does — no more. ENG_DESIGN.md §4 is the long form.
/// </summary>
public static class EngagementPrivacyNotices
{
    public static (string En, string Ar) For(string mode, int k) => mode switch
    {
        EngagementSurveyModes.Identified => (
            "This survey is identified. Your answers are saved with your name. People your organisation has authorised " +
            "to read identified survey answers can see them. Results are also shown as group totals.",
            "هذا الاستبيان معرَّف. تُحفظ إجاباتك مع اسمك، ويمكن للأشخاص الذين تفوضهم مؤسستك بقراءة إجابات الاستبيانات المعرَّفة الاطلاع عليها. وتُعرض النتائج أيضاً كمجاميع للمجموعات."),
        EngagementSurveyModes.Confidential => (
            $"This survey is confidential. Your answers are stored without your name or employee number. HR can see " +
            $"whether you have responded, so that reminders go only to people who have not. Results appear only after the " +
            $"survey closes, and only for groups of at least {k} people; comments are shown without names. People with " +
            $"direct access to the database or servers are outside this promise.",
            $"هذا الاستبيان سري. تُحفظ إجاباتك بدون اسمك أو رقمك الوظيفي. يمكن للموارد البشرية معرفة ما إذا كنت قد أجبت، " +
            $"حتى تُرسل التذكيرات فقط لمن لم يجب. لا تظهر النتائج إلا بعد إغلاق الاستبيان، وفقط للمجموعات المكونة من {k} " +
            $"أشخاص على الأقل، وتُعرض التعليقات بدون أسماء. لا يشمل هذا الالتزام من لديهم وصول مباشر إلى قاعدة البيانات أو الخوادم."),
        _ => (
            $"This survey is anonymous. Your answers are stored without your name, your employee number or the date you " +
            $"answered, and HR sees only how many people have responded, not who. Reminders go to everyone invited. " +
            $"Results appear only after the survey closes, and only for groups of at least {k} people; comments are shown " +
            $"without names. People with direct access to the database or servers are outside this promise.",
            $"هذا الاستبيان مجهول الهوية. تُحفظ إجاباتك بدون اسمك أو رقمك الوظيفي أو تاريخ إجابتك، ولا ترى الموارد البشرية " +
            $"إلا عدد من أجابوا، وليس هويتهم. تُرسل التذكيرات إلى جميع المدعوين. لا تظهر النتائج إلا بعد إغلاق الاستبيان، " +
            $"وفقط للمجموعات المكونة من {k} أشخاص على الأقل، وتُعرض التعليقات بدون أسماء. لا يشمل هذا الالتزام من لديهم وصول مباشر إلى قاعدة البيانات أو الخوادم."),
    };
}

/// <summary>Stable error codes returned by the engagement endpoints.</summary>
public static class EngagementErrors
{
    public const string NotFound = "not_found";
    public const string InvalidRequest = "invalid_request";
    public const string InvalidState = "invalid_state";
    public const string Conflict = "concurrency_conflict";
    public const string AlreadySubmitted = "survey_already_submitted";
    public const string SurveyClosed = "survey_closed";
    public const string SessionExpired = "survey_session_expired";
    public const string NotInvited = "not_invited";
    public const string GroupScopeRequired = "group_scope_required";
    public const string NoPersonView = "anonymous_no_person_view";
    public const string NotIdentified = "survey_not_identified";
    public const string NoEmployeeLink = "no_employee_record";
    public const string ResultsAfterClose = "results_after_close";
    public const string TooManyFilters = "too_many_filters";
    /// <summary>A company- or team-scoped reader asked for a filter AND a breakdown (their scope is one of the two).</summary>
    public const string ScopeCountsAsFilter = "scope_counts_as_filter";
    public const string CommentFilterNotAllowed = "comment_filter_not_allowed";
}

/// <summary>A service outcome a controller maps onto HTTP: OK, or a status with a stable code and plain message.</summary>
public sealed record EngagementResult<T>(T? Value, int Status, string? Code, string? Message)
{
    public bool Ok => Code is null;
    public static EngagementResult<T> Success(T value) => new(value, 200, null, null);
    public static EngagementResult<T> Fail(int status, string code, string message) => new(default, status, code, message);
}
