using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zayra.Api.Infrastructure.Learning;

public static class LearningQuestionKinds
{
    public const string Single = "Single";
    public const string Multi = "Multi";
    public const string TrueFalse = "TrueFalse";
    public static readonly string[] All = [Single, Multi, TrueFalse];
}

/// <summary>One option as stored (server only).</summary>
public sealed record AssessmentOption(string Id, string Text, string? TextAr);

/// <summary>One question as stored WITH its answer key. Never serialised to an employee — see <see cref="ClientQuestion"/>.</summary>
public sealed record AssessmentQuestion(string Id, string Kind, string Prompt, string? PromptAr, IReadOnlyList<AssessmentOption> Options,
    IReadOnlyList<string> CorrectOptionIds);

public sealed record AssessmentBank(IReadOnlyList<AssessmentQuestion> Questions);

/// <summary>What an employee sees: the question and its options, and nothing that reveals the key.</summary>
public sealed record ClientQuestion(string Id, string Kind, string Prompt, string? PromptAr, IReadOnlyList<ClientOption> Options);
public sealed record ClientOption(string Id, string Text, string? TextAr);

/// <summary>HR authoring input. Ids are assigned by the server; the client names correct options by index.</summary>
public sealed record AssessmentQuestionInput(string? Kind, string? Prompt, string? PromptAr, IReadOnlyList<AssessmentOptionInput>? Options,
    IReadOnlyList<int>? CorrectOptionIndexes, bool? TrueFalseAnswer);
public sealed record AssessmentOptionInput(string? Text, string? TextAr);

public sealed record AssessmentScore(decimal ScorePercent, int Correct, int Total, bool Passed);

/// <summary>
/// The question bank of a course version: validation, the immutable stored snapshot, the employee-safe projection and
/// server-side scoring. Scoring is all-or-nothing per question (the selected set must equal the key) and the score is
/// a decimal percentage rounded half-away-from-zero to 2 places, compared with the pass mark held on the attempt.
/// </summary>
public static class LearningAssessment
{
    public const int MaxQuestions = 100;
    public const int MaxOptions = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Validates HR's input and builds the bank. Returns the problems instead when it is not valid.</summary>
    public static (AssessmentBank? Bank, IReadOnlyList<string> Problems) Build(IReadOnlyList<AssessmentQuestionInput>? input)
    {
        var problems = new List<string>();
        if (input is null || input.Count == 0) return (null, ["An assessment needs at least one question."]);
        if (input.Count > MaxQuestions) return (null, [$"An assessment can have at most {MaxQuestions} questions."]);

        var questions = new List<AssessmentQuestion>();
        for (var q = 0; q < input.Count; q++)
        {
            var item = input[q];
            var label = $"Question {q + 1}";
            var kind = LearningQuestionKinds.All.FirstOrDefault(k => string.Equals(k, item.Kind, StringComparison.OrdinalIgnoreCase));
            if (kind is null) { problems.Add($"{label}: choose single choice, multiple choice or true/false."); continue; }
            if (string.IsNullOrWhiteSpace(item.Prompt)) { problems.Add($"{label}: the question text is required."); continue; }
            var id = $"q{q + 1}";

            if (kind == LearningQuestionKinds.TrueFalse)
            {
                if (item.TrueFalseAnswer is not bool answer) { problems.Add($"{label}: say whether the statement is true or false."); continue; }
                questions.Add(new AssessmentQuestion(id, kind, item.Prompt.Trim(), Clean(item.PromptAr),
                    [new AssessmentOption("true", "True", "صح"), new AssessmentOption("false", "False", "خطأ")],
                    [answer ? "true" : "false"]));
                continue;
            }

            var options = (item.Options ?? []).Where(o => !string.IsNullOrWhiteSpace(o.Text)).ToList();
            if (options.Count < 2 || options.Count > MaxOptions) { problems.Add($"{label}: give between 2 and {MaxOptions} answer options."); continue; }
            var correct = (item.CorrectOptionIndexes ?? []).Distinct().ToList();
            if (correct.Any(i => i < 0 || i >= options.Count)) { problems.Add($"{label}: a correct answer points at an option that does not exist."); continue; }
            if (kind == LearningQuestionKinds.Single && correct.Count != 1) { problems.Add($"{label}: a single-choice question has exactly one correct answer."); continue; }
            if (kind == LearningQuestionKinds.Multi && correct.Count < 1) { problems.Add($"{label}: mark at least one correct answer."); continue; }

            var stored = options.Select((o, i) => new AssessmentOption($"{id}o{i + 1}", o.Text!.Trim(), Clean(o.TextAr))).ToList();
            questions.Add(new AssessmentQuestion(id, kind, item.Prompt.Trim(), Clean(item.PromptAr), stored,
                correct.OrderBy(i => i).Select(i => stored[i].Id).ToList()));
        }
        return problems.Count > 0 ? (null, problems) : (new AssessmentBank(questions), problems);
    }

    public static string Serialize(AssessmentBank bank) => JsonSerializer.Serialize(bank, Json);

    public static AssessmentBank Parse(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new AssessmentBank([])
            : JsonSerializer.Deserialize<AssessmentBank>(json, Json) ?? new AssessmentBank([]);

    /// <summary>The order questions are shown in for one attempt: the bank order, or a fresh random order.</summary>
    public static IReadOnlyList<string> AttemptOrder(AssessmentBank bank, bool shuffle)
    {
        var ids = bank.Questions.Select(q => q.Id).ToArray();
        if (shuffle) RandomNumberGenerator.Shuffle(ids.AsSpan());
        return ids;
    }

    /// <summary>The employee-safe view, in attempt order. Carries no correct answers.</summary>
    public static IReadOnlyList<ClientQuestion> ForClient(AssessmentBank bank, IReadOnlyList<string> order)
    {
        var byId = bank.Questions.ToDictionary(q => q.Id);
        return order.Where(byId.ContainsKey).Select(id => byId[id])
            .Select(q => new ClientQuestion(q.Id, q.Kind, q.Prompt, q.PromptAr,
                q.Options.Select(o => new ClientOption(o.Id, o.Text, o.TextAr)).ToList()))
            .ToList();
    }

    /// <summary>The HR view of a bank (authoring): includes the key, because HR wrote it.</summary>
    public static IReadOnlyList<AssessmentQuestion> ForAuthor(AssessmentBank bank) => bank.Questions;

    /// <summary>Scores a submission. Unknown question or option ids are ignored; an unanswered question is wrong.</summary>
    public static AssessmentScore Score(AssessmentBank bank, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers, decimal passMarkPercent)
    {
        var total = bank.Questions.Count;
        if (total == 0) return new AssessmentScore(0m, 0, 0, false);
        var correct = 0;
        foreach (var question in bank.Questions)
        {
            if (answers is null || !answers.TryGetValue(question.Id, out var chosen) || chosen is null) continue;
            var selected = chosen.Where(id => question.Options.Any(o => o.Id == id)).ToHashSet(StringComparer.Ordinal);
            if (question.Kind != LearningQuestionKinds.Multi && selected.Count > 1) continue;
            if (selected.SetEquals(question.CorrectOptionIds)) correct++;
        }
        var score = Math.Round(correct * 100m / total, 2, MidpointRounding.AwayFromZero);
        return new AssessmentScore(score, correct, total, score >= passMarkPercent);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
