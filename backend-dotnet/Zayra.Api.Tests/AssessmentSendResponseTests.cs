using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Sending an assessment answers the staff member with the same shape the assessment list uses: the candidate's invitation token is
/// stored for the candidate and is not part of the staff response.
/// </summary>
public sealed class AssessmentSendResponseTests
{
    [Fact]
    public async Task Send_ReturnsTheAssessmentWithoutTheInvitationToken()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var application = new JobApplication
        {
            TenantId = tenantId, CompanyId = Guid.NewGuid(), JobOpeningId = Guid.NewGuid(), JobTitle = "Analyst", CandidateId = Guid.NewGuid(),
            CandidateName = "Candidate", CandidateEmail = "c@example.test", Stage = "Assessment", Status = "Active",
        };
        var template = new AssessmentTemplate { TenantId = tenantId, Code = "APT", Title = "Aptitude", AssessmentType = "Technical", TotalMarks = 10 };
        db.JobApplications.Add(application);
        db.AssessmentTemplates.Add(template);
        await db.SaveChangesAsync();
        var controller = new AssessmentsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    }, "test")),
                },
            },
        };

        var result = await controller.Send(new SendAssessmentRequest(application.Id, template.Id, 7), CancellationToken.None);

        var body = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        var stored = await db.CandidateAssessments.AsNoTracking().SingleAsync();
        stored.InvitationToken.Should().NotBeNullOrEmpty("the token is still issued for the candidate");
        body.Should().Contain(stored.Id.ToString());
        body.Should().NotContain(stored.InvitationToken).And.NotContainEquivalentOf("invitationToken");
    }

    [Fact]
    public async Task RecordResult_ReturnsTheAssessmentWithoutTheInvitationToken()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var template = new AssessmentTemplate { TenantId = tenantId, Code = "APT", Title = "Aptitude", AssessmentType = "Technical", TotalMarks = 10, PassingScore = 50 };
        var assessment = new CandidateAssessment
        {
            TenantId = tenantId, CompanyId = Guid.NewGuid(), ApplicationId = Guid.NewGuid(), CandidateId = Guid.NewGuid(), TemplateId = template.Id,
            TemplateName = template.Title, Status = "Sent", TotalMarks = 10,
        };
        db.AssessmentTemplates.Add(template);
        db.CandidateAssessments.Add(assessment);
        await db.SaveChangesAsync();
        var controller = new AssessmentsController(db) { ControllerContext = StaffContext(tenantId) };

        var result = await controller.RecordResult(assessment.Id, new RecordResultRequest(8), CancellationToken.None);

        var body = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        body.Should().Contain(assessment.Id.ToString()).And.Contain("\"Passed\":true");
        body.Should().NotContain(assessment.InvitationToken).And.NotContainEquivalentOf("invitationToken");
    }

    private static ControllerContext StaffContext(Guid tenantId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            }, "test")),
        },
    };
}
