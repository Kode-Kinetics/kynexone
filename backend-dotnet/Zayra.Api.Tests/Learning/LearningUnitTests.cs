using FluentAssertions;
using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Learning;

/// <summary>Pure rules of the LRN module: lesson sanitising, question-bank validation and scoring, moveOn.</summary>
public sealed class LearningUnitTests
{
    [Theory]
    [InlineData("<p>Hi<script>alert(1)</script></p>", "<p>Hi</p>")]
    [InlineData("<p onclick=\"steal()\">Hi</p>", "<p>Hi</p>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "x")]
    [InlineData("<a href=\"java&#09;script:alert(1)\">x</a>", "x")]
    [InlineData("<img src=x onerror=alert(1)>", "")]
    [InlineData("<iframe src=\"https://evil.test\"></iframe><p>ok</p>", "<p>ok</p>")]
    [InlineData("<style>body{display:none}</style>text", "text")]
    [InlineData("<svg><script>alert(1)</script></svg>after", "after")]
    [InlineData("a < b and c > d", "a &lt; b and c &gt; d")]
    [InlineData("<b>bold<i>both</b>", "<b>bold<i>both</i></b>")]
    [InlineData("&lt;script&gt;", "&lt;script&gt;")]
    public void Sanitizer_RebuildsOnlyAllowedMarkup(string input, string expected) =>
        LearningHtmlSanitizer.Sanitize(input).Should().Be(expected);

    [Fact]
    public void Sanitizer_KeepsSafeLinks_AndForcesANewTabWithoutOpener()
    {
        var html = LearningHtmlSanitizer.Sanitize("<p>See <a href=\"https://policies.example.com/fire?x=1&y=2\" style=\"x\">the policy</a></p>");
        html.Should().Be("<p>See <a href=\"https://policies.example.com/fire?x=1&amp;y=2\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">the policy</a></p>");
    }

    [Fact]
    public void QuestionBank_ValidatesAndScoresOnTheServer()
    {
        var (bank, problems) = LearningAssessment.Build(LearningTestWorld.Questions());
        problems.Should().BeEmpty();
        bank!.Questions.Should().HaveCount(2);
        LearningAssessment.Score(bank, Answers(("q1", ["q1o2"]), ("q2", ["true"])), 70m).Should().Be(new AssessmentScore(100m, 2, 2, true));
        LearningAssessment.Score(bank, Answers(("q1", ["q1o2"])), 70m).Should().Be(new AssessmentScore(50m, 1, 2, false));
        LearningAssessment.Score(bank, Answers(("q1", ["q1o2", "q1o1"]), ("q2", ["true"])), 50m).Passed.Should().BeTrue("50% meets a 50% pass mark");
        LearningAssessment.Score(bank, null, 0m).ScorePercent.Should().Be(0m);

        var (_, bad) = LearningAssessment.Build([new AssessmentQuestionInput("Single", "Pick", null,
            [new AssessmentOptionInput("a", null), new AssessmentOptionInput("b", null)], [0, 1], null)]);
        bad.Should().ContainSingle().Which.Should().Contain("exactly one correct answer");
    }

    [Fact]
    public void ThreeOfThreeRoundsToTwoDecimals()
    {
        var (bank, _) = LearningAssessment.Build(Enumerable.Range(0, 3).Select(i => new AssessmentQuestionInput("TrueFalse", $"S{i}", null, null, null, true)).ToList());
        LearningAssessment.Score(bank!, Answers(("q1", ["true"])), 33.33m).Should().Be(new AssessmentScore(33.33m, 1, 3, true));
    }

    [Theory]
    [InlineData(Cmi5MoveOnValues.Completed, "c", true)]
    [InlineData(Cmi5MoveOnValues.Completed, "p", false)]
    [InlineData(Cmi5MoveOnValues.Passed, "p", true)]
    [InlineData(Cmi5MoveOnValues.CompletedAndPassed, "cp", true)]
    [InlineData(Cmi5MoveOnValues.CompletedAndPassed, "p", false)]
    [InlineData(Cmi5MoveOnValues.CompletedOrPassed, "c", true)]
    [InlineData(Cmi5MoveOnValues.CompletedOrPassed, "", false)]
    [InlineData(Cmi5MoveOnValues.NotApplicable, "", true)]
    public void MoveOnRules(string moveOn, string seen, bool satisfied)
    {
        var verbs = new HashSet<string>();
        if (seen.Contains('c')) verbs.Add(Cmi5Verbs.Completed);
        if (seen.Contains('p')) verbs.Add(Cmi5Verbs.Passed);
        LearningCmi5Service.MoveOnSatisfied(moveOn, verbs).Should().Be(satisfied);
    }

    [Fact]
    public void ModelChecks_AreBuiltFromTheVocabularies()
    {
        LearningModelConfiguration.ActiveAssignmentFilter.Should().Be("status IN ('Assigned','InProgress')");
        LearningAssignmentStatuses.Active.Should().BeSubsetOf(LearningAssignmentStatuses.All);
        LearningSessionStatuses.Open.Should().BeSubsetOf(LearningSessionStatuses.All);
    }

    [Theory]
    [InlineData("/api/learning/courses", true)]
    [InlineData("/api/learning/xapi/statements", true)]
    [InlineData("/api/ess/learning/enrollments/1/launch", true)]
    [InlineData("/api/ess/package", false)]
    [InlineData("/api/learningx", false)]
    public void LearningRoutes_AreOptIn_AndOffByDefault(string path, bool owned)
    {
        (Zayra.Api.Infrastructure.Modules.OptInFeatures.ResolveApiPath(path) == FeatureKeys.Learning).Should().Be(owned);
        var off = Zayra.Api.Infrastructure.Modules.TenantModuleService.Build([], "SA");
        off.IsEnabled(FeatureKeys.Learning).Should().BeFalse("an absent flag row means OFF for an opt-in feature");
        var on = Zayra.Api.Infrastructure.Modules.TenantModuleService.Build([], "SA", [FeatureKeys.Learning]);
        on.IsEnabled(FeatureKeys.Learning).Should().BeTrue();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Answers(params (string Q, string[] A)[] answers) =>
        answers.ToDictionary(a => a.Q, a => (IReadOnlyList<string>)a.A);
}
