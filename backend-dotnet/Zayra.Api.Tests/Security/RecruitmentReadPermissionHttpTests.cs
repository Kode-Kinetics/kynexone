using System.Net;
using FluentAssertions;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Recruitment read endpoints (openings, requisitions, workforce plans, assessment templates) require the recruitment permission,
/// through the real pipeline. A signed-in employee is refused; a recruiter, HR Manager and Admin are served. The assessment
/// template detail carries the answer field only for callers who can manage templates.
/// </summary>
public sealed class RecruitmentReadPermissionHttpTests(LaunchSecurityFixture f) : IClassFixture<LaunchSecurityFixture>
{
    private const string Secret = "SECRET-CORRECT-OPTION";

    private async Task<Guid> SeedTemplateAsync() => await f.OnceAsync("recruitment-read-template", async () =>
    {
        var templateId = Guid.NewGuid();
        await f.SeedAsync(db =>
        {
            db.AssessmentTemplates.Add(new AssessmentTemplate { Id = templateId, TenantId = f.TenantId, Code = "RR-1", Title = "Screening", TotalMarks = 1 });
            db.AssessmentQuestions.Add(new AssessmentQuestion
            {
                TenantId = f.TenantId, TemplateId = templateId, OrderIndex = 1, QuestionType = "MCQ", QuestionText = "Pick one",
                OptionsJson = "[\"A\",\"B\"]", CorrectAnswer = Secret, Marks = 1,
            });
            return Task.CompletedTask;
        });
        await f.EnsureUserAsync("RecruiterA", "Recruiter", groupScope: false, f.CompanyA);
        return templateId;
    });

    public static IEnumerable<object[]> ReadPaths() =>
    [
        ["/api/recruitment/openings"],
        ["/api/recruitment/openings/" + "00000000-0000-0000-0000-000000000001"],
        ["/api/recruitment/openings/stats"],
        ["/api/recruitment/openings/export"],
        ["/api/recruitment/requisitions"],
        ["/api/recruitment/requisitions/00000000-0000-0000-0000-000000000001"],
        ["/api/recruitment/requisitions/stats"],
        ["/api/recruitment/workforce-planning"],
        ["/api/recruitment/workforce-planning/00000000-0000-0000-0000-000000000001"],
        ["/api/recruitment/workforce-planning/summary"],
        ["/api/recruitment/assessments/templates"],
        ["/api/recruitment/assessments/templates/00000000-0000-0000-0000-000000000001"],
    ];

    [Theory]
    [MemberData(nameof(ReadPaths))]
    public async Task RecruitmentRead_ForEmployeeRole_IsForbidden(string path)
    {
        await SeedTemplateAsync();
        (await f.Status(HttpMethod.Get, path, f.Token("EmployeeA"))).Should().Be(HttpStatusCode.Forbidden, path);
    }

    [Theory]
    [InlineData("/api/recruitment/openings")]
    [InlineData("/api/recruitment/openings/stats")]
    [InlineData("/api/recruitment/openings/export")]
    [InlineData("/api/recruitment/requisitions")]
    [InlineData("/api/recruitment/requisitions/stats")]
    [InlineData("/api/recruitment/workforce-planning")]
    [InlineData("/api/recruitment/workforce-planning/summary")]
    [InlineData("/api/recruitment/assessments/templates")]
    public async Task RecruitmentRead_ForRecruiterHrManagerAndAdmin_IsServed(string path)
    {
        await SeedTemplateAsync();
        foreach (var persona in new[] { "RecruiterA", "HrManagerA", "AdminA" })
            (await f.Status(HttpMethod.Get, path, f.Token(persona))).Should().Be(HttpStatusCode.OK, $"{persona} {path}");
    }

    [Fact]
    public async Task AssessmentTemplateDetail_ForRecruiter_OmitsTheAnswerField()
    {
        var id = await SeedTemplateAsync();
        using var r = await f.Send(HttpMethod.Get, $"/api/recruitment/assessments/templates/{id}", f.Token("RecruiterA"));
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await r.Content.ReadAsStringAsync();
        body.Should().Contain("Pick one");
        body.Should().NotContain(Secret);
        body.ToLowerInvariant().Should().NotContain("correctanswer");
    }

    [Theory]
    [InlineData("HrManagerA")]
    [InlineData("AdminA")]
    public async Task AssessmentTemplateDetail_ForTemplateManagers_StillCarriesTheAnswerField(string persona)
    {
        var id = await SeedTemplateAsync();
        using var r = await f.Send(HttpMethod.Get, $"/api/recruitment/assessments/templates/{id}", f.Token(persona));
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).Should().Contain(Secret);
    }
}
