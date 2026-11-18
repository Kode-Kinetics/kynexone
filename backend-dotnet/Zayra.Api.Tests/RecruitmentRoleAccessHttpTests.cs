using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// Who may do what in recruitment, through the REAL pipeline (real JWT built from the seeded role, real role and permission gates, real
/// company filters). A line manager may raise requisitions for their own department and give feedback on the interviews they are on,
/// and nothing else in recruitment. An HR Officer works the pipeline (read and write) but decides nothing and deletes nothing.
/// </summary>
public sealed class RecruitmentRoleAccessHttpTests(RoleAccessFixture f) : IClassFixture<RoleAccessFixture>
{
    private static object RequisitionBody(Guid? departmentId) => new
    {
        departmentId, departmentName = "Dept", designationId = (Guid?)null, designationTitle = "Analyst", headCount = 1, employmentType = "Full-Time",
        priority = "Medium", justification = "Growth", requiredSkills = "x", minExperienceYears = 1, maxExperienceYears = 5, budgetFrom = 1000m, budgetTo = 2000m,
        targetJoiningDate = "2027-01-01", requestedByName = "Manager", companyId = (Guid?)null,
    };

    private static object FeedbackBody() => new
    {
        interviewerName = "Panel", interviewerRole = "Manager", communicationScore = 4, technicalScore = 4, cultureFitScore = 4,
        problemSolvingScore = 4, leadershipScore = 4, overallScore = 4, strengths = "s", concerns = "c", notes = "n", recommendation = "Hire",
    };

    // ── Line manager: requisitions ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AManager_RaisesARequisition_ForTheirOwnDepartment_AndForADepartmentTheyHead()
    {
        using var own = await f.Send(HttpMethod.Post, "/api/recruitment/requisitions", f.ManagerA, RequisitionBody(f.DepartmentOwn));
        own.StatusCode.Should().Be(HttpStatusCode.Created, await own.Content.ReadAsStringAsync());
        using var headed = await f.Send(HttpMethod.Post, "/api/recruitment/requisitions", f.ManagerA, RequisitionBody(f.DepartmentHeaded));
        headed.StatusCode.Should().Be(HttpStatusCode.Created, await headed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AManager_CannotRaiseARequisition_ForSomeoneElsesDepartment_OrAnotherCompanys_OrWithNoDepartment()
    {
        (await f.Status(HttpMethod.Post, "/api/recruitment/requisitions", f.ManagerA, RequisitionBody(f.DepartmentElse))).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, "/api/recruitment/requisitions", f.ManagerA, RequisitionBody(f.DepartmentOtherCompany))).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, "/api/recruitment/requisitions", f.ManagerA, RequisitionBody(null))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AManager_SeesOnlyTheRequisitionsTheyRaised_AndCanEditAndSubmitOnlyThose()
    {
        var ids = await f.Ids("/api/recruitment/requisitions", f.ManagerA);
        ids.Should().Contain(f.RequisitionOwn).And.NotContain(f.RequisitionHr);

        (await f.Status(HttpMethod.Get, $"/api/recruitment/requisitions/{f.RequisitionOwn}", f.ManagerA)).Should().Be(HttpStatusCode.OK);
        (await f.Status(HttpMethod.Get, $"/api/recruitment/requisitions/{f.RequisitionHr}", f.ManagerA)).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Put, $"/api/recruitment/requisitions/{f.RequisitionHr}", f.ManagerA, RequisitionBody(f.DepartmentOwn))).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/requisitions/{f.RequisitionHr}/submit", f.ManagerA)).Should().Be(HttpStatusCode.NotFound);

        (await f.Status(HttpMethod.Put, $"/api/recruitment/requisitions/{f.RequisitionOwn}", f.ManagerA, RequisitionBody(f.DepartmentOwn))).Should().Be(HttpStatusCode.OK);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/requisitions/{f.RequisitionOwn}/submit", f.ManagerA)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AManager_IsOfferedOnlyTheirOwnAndHeadedDepartments_ForTheRaiseForm()
    {
        var departments = await f.Ids("/api/recruitment/requisitions/my-departments", f.ManagerA);
        departments.Should().BeEquivalentTo([f.DepartmentOwn, f.DepartmentHeaded]);
    }

    [Fact]
    public async Task AManager_CannotDecideARequisition()
    {
        (await f.Status(HttpMethod.Post, $"/api/recruitment/requisitions/{f.RequisitionHr}/approve", f.ManagerA, new { comments = "ok" })).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/requisitions/{f.RequisitionHr}/reject", f.ManagerA, new { reason = "no" })).Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Line manager: interviews ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AManager_GivesTheirOwnScorecard_OnAnInterviewTheyAreAssignedTo()
    {
        using var viaInterviews = await f.Send(HttpMethod.Post, $"/api/recruitment/interviews/{f.InterviewAssigned}/feedback", f.ManagerA, FeedbackBody());
        viaInterviews.StatusCode.Should().Be(HttpStatusCode.OK, await viaInterviews.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AManager_CannotOverwriteTheSharedInterviewVerdict_ThroughTheApplicationsRoute()
    {
        (await f.Status(HttpMethod.Post, $"/api/recruitment/applications/interviews/{f.InterviewAssigned}/feedback", f.ManagerA,
            new { overallRating = 1, recommendation = "Proceed", feedbackNotes = "Overwritten by a panellist" })).Should().Be(HttpStatusCode.Forbidden);
        var row = await f.Read(db => db.InterviewSchedules.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == f.InterviewAssigned));
        row.FeedbackNotes.Should().Be("HR verdict: do not hire");
        row.OverallRating.Should().Be(5);
        row.Recommendation.Should().Be("Reject");
        row.Status.Should().Be("Scheduled");
        // HR coordinators still record the shared verdict there.
        (await f.Status(HttpMethod.Post, $"/api/recruitment/applications/interviews/{f.InterviewAssigned}/feedback", f.HrA,
            new { overallRating = 5, recommendation = "Reject", feedbackNotes = "HR verdict: do not hire" })).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AManager_CannotGiveFeedback_OnAnotherTeamsInterview_ByEitherRoute()
    {
        (await f.Status(HttpMethod.Post, $"/api/recruitment/interviews/{f.InterviewOther}/feedback", f.ManagerA, FeedbackBody())).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/applications/interviews/{f.InterviewOther}/feedback", f.ManagerA,
            new { overallRating = 1, recommendation = "Reject", feedbackNotes = "x" })).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AManager_SeesOnlyTheirOwnInterviews_AndNoPeerScorecards()
    {
        var mine = await f.Ids("/api/recruitment/interviews/mine", f.ManagerA);
        mine.Should().Contain(f.InterviewAssigned).And.NotContain([f.InterviewOther, f.InterviewOtherCompany]);

        var detail = await f.Json(HttpMethod.Get, $"/api/recruitment/interviews/{f.InterviewAssigned}", f.ManagerA);
        var feedbacks = detail.GetProperty("feedbacks").EnumerateArray().ToArray();
        feedbacks.Select(x => x.GetProperty("interviewerName").GetString()).Should().NotContain("Peer Panelist", "a manager never reads a peer's scorecard");
        feedbacks.Select(x => x.GetProperty("interviewerUserId").GetGuid()).Distinct().Should().Equal(f.ManagerUserId);
        // The interview row itself carries HR's overall verdict: logistics only for a panellist.
        var interview = detail.GetProperty("interview");
        foreach (var verdict in new[] { "overallRating", "recommendation", "feedbackNotes" })
            interview.TryGetProperty(verdict, out _).Should().BeFalse($"{verdict} is the shared verdict, not the manager's own");
        interview.GetProperty("meetingLink").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Undefined);
        (await f.Json(HttpMethod.Get, $"/api/recruitment/interviews/{f.InterviewAssigned}", f.HrA)).GetProperty("interview")
            .GetProperty("feedbackNotes").GetString().Should().Be("HR verdict: do not hire", "HR still sees the verdict");
        (await f.Status(HttpMethod.Get, $"/api/recruitment/interviews/{f.InterviewOther}", f.ManagerA)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CompanyScopeHolds_ForAManagerNamedOnAnotherCompanysInterview()
    {
        (await f.Status(HttpMethod.Get, $"/api/recruitment/interviews/{f.InterviewOtherCompany}", f.ManagerA)).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/interviews/{f.InterviewOtherCompany}/feedback", f.ManagerA, FeedbackBody())).Should().Be(HttpStatusCode.NotFound);
    }

    // ── Line manager: nothing else ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/recruitment/candidates")]
    [InlineData("GET", "/api/recruitment/interviews")]
    [InlineData("GET", "/api/recruitment/offers")]
    [InlineData("POST", "/api/recruitment/offers")]
    [InlineData("POST", "/api/recruitment/openings")]
    [InlineData("POST", "/api/recruitment/applications")]
    [InlineData("GET", "/api/recruitment/assessments")]
    [InlineData("POST", "/api/employees/drafts/00000000-0000-0000-0000-000000000001/approve")]
    [InlineData("POST", "/api/employees/drafts/00000000-0000-0000-0000-000000000001/reject")]
    public async Task AManager_IsRefusedEverythingElseInRecruitmentAndTheHire(string method, string path)
    {
        var body = method == "POST" ? new { } : null;
        (await f.Status(new HttpMethod(method), path, f.ManagerA, body)).Should().Be(HttpStatusCode.Forbidden, $"{method} {path}");
    }

    [Fact]
    public async Task AManager_CannotAcceptAnOfferOrHire()
    {
        (await f.Status(HttpMethod.Post, $"/api/recruitment/applications/offers/{f.OfferId}/accept", f.ManagerA, new { })).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Patch, $"/api/recruitment/offers/{f.OfferId}/accept", f.ManagerA, new { })).Should().Be(HttpStatusCode.Forbidden);
    }

    // ── HR Officer ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/recruitment/candidates")]
    [InlineData("/api/recruitment/openings")]
    [InlineData("/api/recruitment/requisitions")]
    [InlineData("/api/recruitment/interviews")]
    [InlineData("/api/recruitment/assessments")]
    public async Task AnHrOfficer_ReadsTheRecruitmentPipeline(string path) =>
        (await f.Status(HttpMethod.Get, path, f.HrOfficerA)).Should().Be(HttpStatusCode.OK, path);

    [Fact]
    public async Task AnHrOfficer_WorksThePipeline_CreatesACandidate_AndRaisesARequisition()
    {
        using var candidate = await f.Send(HttpMethod.Post, "/api/recruitment/candidates", f.HrOfficerA,
            new { firstName = "Sara", lastName = "Officer", email = $"sara-{Guid.NewGuid():N}@example.test", phone = "+966500000009", nationality = "SA",
                currentJobTitle = "Analyst", currentCompany = "Acme", totalExperienceYears = 3, educationLevel = "Bachelor", linkedInUrl = "https://example.test/sara", source = "Direct", tags = "pipeline" });
        candidate.StatusCode.Should().BeOneOf([HttpStatusCode.Created, HttpStatusCode.OK], await candidate.Content.ReadAsStringAsync());
        using var requisition = await f.Send(HttpMethod.Post, "/api/recruitment/requisitions", f.HrOfficerA, RequisitionBody(f.DepartmentOwn));
        requisition.StatusCode.Should().Be(HttpStatusCode.Created, await requisition.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnHrOfficer_DecidesNothingAndDeletesNothing()
    {
        (await f.Status(HttpMethod.Post, $"/api/recruitment/requisitions/{f.RequisitionHr}/approve", f.HrOfficerA, new { comments = "ok" })).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/requisitions/{f.RequisitionHr}/reject", f.HrOfficerA, new { reason = "no" })).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/applications/{f.ApplicationId}/reject", f.HrOfficerA, new { reason = "no" })).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/applications/offers/{f.OfferId}/accept", f.HrOfficerA, new { })).Should().Be(HttpStatusCode.Forbidden, "recording an offer response is HR Manager's");
        (await f.Status(HttpMethod.Patch, $"/api/recruitment/offers/{f.OfferId}/accept", f.HrOfficerA, new { })).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/applications/{f.ApplicationId}/offer", f.HrOfficerA, new { })).Should().Be(HttpStatusCode.Forbidden, "authoring an offer is not HR Officer's");
        (await f.Status(HttpMethod.Post, "/api/employees/drafts/00000000-0000-0000-0000-000000000001/approve", f.HrOfficerA)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnHrOfficer_CannotChangeOfferLetterTemplatesOrCreateWorkforcePlans_ButHrManagerAndDirectorCan()
    {
        var plan = new { planName = "P", planYear = 2028, departmentId = f.DepartmentOwn, currentHeadcount = 1, plannedHeadcount = 2, budgetAllocated = 10, currencyCode = "SAR" };
        var template = new { name = "Letter", design = new { } };
        (await f.Status(HttpMethod.Post, "/api/recruitment/workforce-planning", f.HrOfficerA, plan)).Should().Be(HttpStatusCode.Forbidden, "planning stays HR Manager and HR Director");
        (await f.Status(HttpMethod.Post, "/api/recruitment/offer-templates", f.HrOfficerA, template)).Should().Be(HttpStatusCode.Forbidden, "it changes the legal text of letters");
        (await f.Status(HttpMethod.Put, $"/api/recruitment/offer-templates/{Guid.NewGuid()}", f.HrOfficerA, template)).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/offer-templates/{Guid.NewGuid()}/archive", f.HrOfficerA, new { expectedVersion = 1, reason = "x" })).Should().Be(HttpStatusCode.Forbidden);

        (await f.Status(HttpMethod.Post, "/api/recruitment/workforce-planning", f.HrA, plan)).Should().Be(HttpStatusCode.Created);
        (await f.Status(HttpMethod.Post, "/api/recruitment/workforce-planning", f.HrDirectorA, plan)).Should().Be(HttpStatusCode.Created);
        foreach (var user in new[] { f.HrA, f.HrDirectorA })
        {
            // Past the gate: an unknown template is a 404/409/422, never a 403. (Create is not asserted: with no company selected the handler itself forbids.)
            (await f.Status(HttpMethod.Put, $"/api/recruitment/offer-templates/{Guid.NewGuid()}", user, template)).Should().NotBe(HttpStatusCode.Forbidden);
            (await f.Status(HttpMethod.Post, $"/api/recruitment/offer-templates/{Guid.NewGuid()}/archive", user, new { expectedVersion = 1, reason = "x" })).Should().NotBe(HttpStatusCode.Forbidden);
        }
    }

    // ── Exact panel membership ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APanellistIsMatchedByExactNameOrId_NeverBySubstring()
    {
        var mine = await f.Ids("/api/recruitment/interviews/mine", f.ManagerA);
        mine.Should().Contain(f.InterviewAssigned, "stored login id").And.Contain(f.InterviewLegacyName, "exact name, spacing and case ignored");
        mine.Should().NotContain(f.InterviewSimilarName, "'Mona Manager Jr' is not 'Mona Manager'");
        (await f.Status(HttpMethod.Get, $"/api/recruitment/interviews/{f.InterviewSimilarName}", f.ManagerA)).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/interviews/{f.InterviewSimilarName}/feedback", f.ManagerA, FeedbackBody())).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/interviews/{f.InterviewLegacyName}/feedback", f.ManagerA, FeedbackBody())).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task APanellistWhoseNameIsSharedIsRefused_WithAClearMessage()
    {
        (await f.Ids("/api/recruitment/interviews/mine", f.SamA)).Should().NotContain(f.InterviewSharedName);
        (await f.Status(HttpMethod.Get, $"/api/recruitment/interviews/{f.InterviewSharedName}", f.SamA)).Should().Be(HttpStatusCode.NotFound);
        using var refused = await f.Send(HttpMethod.Post, $"/api/recruitment/interviews/{f.InterviewSharedName}/feedback", f.SamA, FeedbackBody());
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await refused.Content.ReadAsStringAsync();
        body.Should().Contain("interviewer_name_ambiguous").And.Contain("messageAr");
    }

    [Fact]
    public async Task SchedulingStoresTheLoginIdOfEveryPanelNameThatMatchesOneLogin()
    {
        using var created = await f.Send(HttpMethod.Post, "/api/recruitment/interviews", f.HrA, new
        {
            applicationId = f.ApplicationId, interviewType = "Technical", interviewerNames = "Mona Manager, Sam Sharedname, Someone External",
            scheduledAt = DateTime.UtcNow.AddDays(6), durationMinutes = 30, mode = "Video",
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = System.Text.Json.JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var stored = await f.Read(db => db.InterviewSchedules.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == id));
        stored.InterviewerUserIds.Should().Be(f.ManagerUserId.ToString(), "Mona matches exactly one login; Sam's name is shared and the external has none");
    }

    // ── Planning, hire withdrawal, blacklist ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnHrOfficer_CannotChangeOrDeleteAPlan_ButHrManagerCanChangeItsStatus()
    {
        (await f.Status(HttpMethod.Patch, $"/api/recruitment/workforce-planning/{f.PlanId}/status", f.HrOfficerA, new { status = "Approved" })).Should().Be(HttpStatusCode.Forbidden);
        (await f.Status(HttpMethod.Delete, $"/api/recruitment/workforce-planning/{f.PlanId}", f.HrOfficerA)).Should().Be(HttpStatusCode.Forbidden);
        (await f.Read(db => db.WorkforcePlans.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == f.PlanId))).Status.Should().Be("Draft");
        (await f.Status(HttpMethod.Patch, $"/api/recruitment/workforce-planning/{f.PlanId}/status", f.HrA, new { status = "Approved" })).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnHrOfficer_CannotWithdrawTheHireOfAnAcceptedOffer_ButItsMakerAndAnApproverCan()
    {
        var refused = f.HireForOfficerRefusal;
        using var response = await f.Send(HttpMethod.Post, $"/api/employees/drafts/{refused.Draft}/cancel", f.HrOfficerA, new { reason = "Changed my mind" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("offer_hire_withdraw_needs_approver").And.Contain("messageAr");
        (await f.Read(db => db.OfferLetters.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == refused.Offer))).Status.Should().Be("Accepted");
        (await f.Read(db => db.JobOpenings.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == refused.Opening))).FilledCount.Should().Be(1);
        (await f.Read(db => db.EmployeeDrafts.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == refused.Draft))).Status.Should().Be("PendingHrApproval");

        // An approver (HR Director holds employees.approve and did not make the hire) may.
        var approverCase = f.HireForApproverCancel;
        (await f.Status(HttpMethod.Post, $"/api/employees/drafts/{approverCase.Draft}/cancel", f.HrDirectorA, new { reason = "Candidate withdrew" })).Should().Be(HttpStatusCode.NoContent);
        (await f.Read(db => db.JobOpenings.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == approverCase.Opening))).FilledCount.Should().Be(0);
        (await f.Read(db => db.OfferLetters.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == approverCase.Offer))).Status.Should().Be("Sent");

        // The draft's own maker (here the HR Officer, who recorded that acceptance) may too.
        var makerCase = f.HireForMakerCancel;
        (await f.Status(HttpMethod.Post, $"/api/employees/drafts/{makerCase.Draft}/cancel", f.HrOfficerA, new { reason = "Candidate withdrew" })).Should().Be(HttpStatusCode.NoContent);
        (await f.Read(db => db.JobApplications.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == makerCase.App))).Status.Should().Be("Active");
    }

    [Fact]
    public async Task Blacklisting_NeedsAWrittenReason_IsAudited_AndIsRefusedToTheHrOfficer()
    {
        var targets = f.BlacklistTargets;
        (await f.Status(HttpMethod.Post, $"/api/recruitment/candidates/{targets[0]}/blacklist", f.HrOfficerA, new { reason = "Misconduct at a previous interview" })).Should().Be(HttpStatusCode.Forbidden);
        (await f.Read(db => db.Candidates.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == targets[0]))).Status.Should().NotBe("Blacklisted");

        (await f.Status(HttpMethod.Post, $"/api/recruitment/candidates/{targets[1]}/blacklist", f.HrA)).Should().Be(HttpStatusCode.BadRequest, "no reason");
        (await f.Status(HttpMethod.Post, $"/api/recruitment/candidates/{targets[1]}/blacklist", f.HrA, new { reason = "too short" })).Should().Be(HttpStatusCode.BadRequest);
        (await f.Read(db => db.Candidates.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == targets[1]))).Status.Should().NotBe("Blacklisted");

        const string reason = "Falsified a reference during screening";
        (await f.Status(HttpMethod.Post, $"/api/recruitment/candidates/{targets[1]}/blacklist", f.HrA, new { reason })).Should().Be(HttpStatusCode.OK);
        (await f.Read(db => db.Candidates.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == targets[1]))).Status.Should().Be("Blacklisted");
        var audit = await f.Read(db => db.RecruitmentAuditLogs.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(a => a.EntityType == "Candidate" && a.EntityId == targets[1].ToString() && a.Action == "Blacklisted"));
        audit.PerformedByName.Should().Be("Hana HR");
        audit.PerformedByUserId.Should().NotBeNull();
        audit.NewValuesJson.Should().Contain(reason);
        audit.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));

        // Never in what lists or reads the candidate.
        (await f.Send(HttpMethod.Get, "/api/recruitment/candidates", f.HrA)).Content.ReadAsStringAsync().Result.Should().NotContain(reason);
        (await f.Send(HttpMethod.Get, $"/api/recruitment/candidates/{targets[1]}", f.HrA)).Content.ReadAsStringAsync().Result.Should().NotContain(reason);

        foreach (var (token, target) in new[] { (f.HrDirectorA, targets[2]), (f.RecruiterA, targets[3]) })
            (await f.Status(HttpMethod.Post, $"/api/recruitment/candidates/{target}/blacklist", token, new { reason = "Verified identity fraud" })).Should().Be(HttpStatusCode.OK, "HR Director and Recruiter keep it");
    }

    [Fact]
    public async Task AnHrOfficer_StaysInsideTheirCompany()
    {
        (await f.Status(HttpMethod.Get, $"/api/recruitment/interviews/{f.InterviewOtherCompany}", f.HrOfficerA)).Should().Be(HttpStatusCode.NotFound);
        (await f.Ids("/api/recruitment/interviews", f.HrOfficerA)).Should().NotContain(f.InterviewOtherCompany);
    }

    // ── The key is only the key ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnHrManager_IsUnchanged_StillReadsAndDecides()
    {
        (await f.Status(HttpMethod.Get, "/api/recruitment/candidates", f.HrA)).Should().Be(HttpStatusCode.OK);
        (await f.Status(HttpMethod.Get, $"/api/recruitment/requisitions/{f.RequisitionHr}", f.HrA)).Should().Be(HttpStatusCode.OK);
    }
}

public sealed class RoleAccessFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid CompanyA { get; } = Guid.NewGuid();
    public Guid CompanyB { get; } = Guid.NewGuid();
    public Guid DepartmentOwn { get; } = Guid.NewGuid();
    public Guid DepartmentHeaded { get; } = Guid.NewGuid();
    public Guid DepartmentElse { get; } = Guid.NewGuid();
    public Guid DepartmentOtherCompany { get; } = Guid.NewGuid();
    public Guid RequisitionOwn { get; } = Guid.NewGuid();
    public Guid RequisitionHr { get; } = Guid.NewGuid();
    public Guid InterviewAssigned { get; } = Guid.NewGuid();
    public Guid InterviewOther { get; } = Guid.NewGuid();
    public Guid InterviewOtherCompany { get; } = Guid.NewGuid();
    public Guid OfferId { get; } = Guid.NewGuid();
    public Guid ApplicationId { get; } = Guid.NewGuid();
    public Guid CandidateId { get; } = Guid.NewGuid();

    public string ManagerA => _tokens[nameof(ManagerA)];
    public string HrOfficerA => _tokens[nameof(HrOfficerA)];
    public string HrA => _tokens[nameof(HrA)];
    public string HrDirectorA => _tokens[nameof(HrDirectorA)];
    public string RecruiterA => _tokens[nameof(RecruiterA)];
    public string AdminA => _tokens[nameof(AdminA)];
    public string SamA => _tokens[nameof(SamA)];
    public Guid InterviewLegacyName { get; } = Guid.NewGuid();
    public Guid InterviewSimilarName { get; } = Guid.NewGuid();
    public Guid InterviewSharedName { get; } = Guid.NewGuid();
    public Guid PlanId { get; } = Guid.NewGuid();
    public Guid ManagerUserId { get; private set; }
    public (Guid Draft, Guid App, Guid Opening, Guid Offer) HireForOfficerRefusal { get; private set; }
    public (Guid Draft, Guid App, Guid Opening, Guid Offer) HireForApproverCancel { get; private set; }
    public (Guid Draft, Guid App, Guid Opening, Guid Offer) HireForMakerCancel { get; private set; }
    public Guid[] BlacklistTargets { get; private set; } = [];

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:rec-role-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection);
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();

        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Roles", Slug = $"roles-{TenantId:N}", IsActive = true, AccountType = TenantAccountTypes.Group });
        db.Companies.Add(new Company { Id = CompanyA, TenantId = TenantId, LegalNameEn = "Alpha", CountryCode = "SA", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyB, TenantId = TenantId, LegalNameEn = "Beta", CountryCode = "SA", IsActive = true });
        db.Departments.Add(new Department { Id = DepartmentOwn, TenantId = TenantId, CompanyId = CompanyA, Code = "OWN", NameEn = "Own dept", IsActive = true });
        db.Departments.Add(new Department { Id = DepartmentHeaded, TenantId = TenantId, CompanyId = CompanyA, Code = "HEAD", NameEn = "Headed dept", IsActive = true });
        db.Departments.Add(new Department { Id = DepartmentElse, TenantId = TenantId, CompanyId = CompanyA, Code = "ELSE", NameEn = "Someone else's dept", IsActive = true });
        db.Departments.Add(new Department { Id = DepartmentOtherCompany, TenantId = TenantId, CompanyId = CompanyB, Code = "OTH", NameEn = "Other company dept", IsActive = true });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);

        var managerEmployee = new Employee
        {
            TenantId = TenantId, CompanyId = CompanyA, EmployeeCode = "MGR-1", FullName = "Mona Manager", EnglishName = "Mona Manager",
            Status = EmployeeStatuses.Active, DepartmentId = DepartmentOwn,
        };
        db.Employees.Add(managerEmployee);
        await db.SaveChangesAsync();
        var headed = await db.Departments.SingleAsync(d => d.Id == DepartmentHeaded);
        headed.ManagerEmployeeId = managerEmployee.Id;
        await db.SaveChangesAsync();

        var managerUser = await AddUser(db, scope, nameof(ManagerA), "Mona Manager", "Manager", groupScope: false, CompanyA, managerEmployee.Id);
        var officerUser = await AddUser(db, scope, nameof(HrOfficerA), "Olive Officer", "HR Officer", groupScope: false, CompanyA);
        var hrUser = await AddUser(db, scope, nameof(HrA), "Hana HR", "HR Manager", groupScope: false, CompanyA);
        await AddUser(db, scope, "HrB", "Hamid HR", "HR Manager", groupScope: false, CompanyB);
        await AddUser(db, scope, nameof(HrDirectorA), "Dina Director", "HR Director", groupScope: false, CompanyA);
        await AddUser(db, scope, nameof(RecruiterA), "Rami Recruiter", "Recruiter", groupScope: false, CompanyA);
        await AddUser(db, scope, nameof(AdminA), "Adam Admin", "Admin", groupScope: false, CompanyA);
        ManagerUserId = managerUser.Id;
        // Two employees (and one login) who share a name: nobody can tell them apart by name alone.
        var sam1 = new Employee { TenantId = TenantId, CompanyId = CompanyA, EmployeeCode = "SAM-1", FullName = "Sam Sharedname", EnglishName = "Sam Sharedname", Status = EmployeeStatuses.Active, DepartmentId = DepartmentOwn };
        var sam2 = new Employee { TenantId = TenantId, CompanyId = CompanyA, EmployeeCode = "SAM-2", FullName = "Sam Sharedname", EnglishName = "Sam Sharedname", Status = EmployeeStatuses.Active, DepartmentId = DepartmentElse };
        db.Employees.AddRange(sam1, sam2);
        await db.SaveChangesAsync();
        await AddUser(db, scope, nameof(SamA), "Sam Sharedname", "Manager", groupScope: false, CompanyA, sam1.Id);

        var candidate = new Candidate { Id = CandidateId, TenantId = TenantId, CompanyId = CompanyA, FirstName = "Ann", LastName = "A", Email = "ann@example.test" };
        var otherCandidate = new Candidate { TenantId = TenantId, CompanyId = CompanyB, FirstName = "Bob", LastName = "B", Email = "bob@example.test" };
        db.Candidates.AddRange(candidate, otherCandidate);
        var openingA = new JobOpening { TenantId = TenantId, CompanyId = CompanyA, JobCode = "JOB-RA", Title = "Opening A", Status = "Open", HeadCount = 2 };
        var openingB = new JobOpening { TenantId = TenantId, CompanyId = CompanyB, JobCode = "JOB-RB", Title = "Opening B", Status = "Open", HeadCount = 2 };
        db.JobOpenings.AddRange(openingA, openingB);
        var applicationA = new JobApplication { Id = ApplicationId, TenantId = TenantId, CompanyId = CompanyA, JobOpeningId = openingA.Id, CandidateId = candidate.Id, CandidateName = "Ann A", JobTitle = "Opening A" };
        var applicationB = new JobApplication { TenantId = TenantId, CompanyId = CompanyB, JobOpeningId = openingB.Id, CandidateId = otherCandidate.Id, CandidateName = "Bob B", JobTitle = "Opening B" };
        db.JobApplications.AddRange(applicationA, applicationB);
        db.OfferLetters.Add(new OfferLetter
        {
            Id = OfferId, TenantId = TenantId, CompanyId = CompanyA, ApplicationId = applicationA.Id, CandidateName = "Ann A", OfferedJobTitle = "Analyst",
            OfferedDepartment = "Own dept", StartDate = new DateOnly(2027, 3, 1), BasicSalary = 1000, GrossSalary = 1000, Status = "Sent",
        });
        db.ManpowerRequisitions.Add(new ManpowerRequisition { Id = RequisitionOwn, TenantId = TenantId, CompanyId = CompanyA, RequisitionNumber = "MRQ-OWN", Status = "Draft", DepartmentId = DepartmentOwn, RequestedByUserId = managerUser.Id });
        db.ManpowerRequisitions.Add(new ManpowerRequisition { Id = RequisitionHr, TenantId = TenantId, CompanyId = CompanyA, RequisitionNumber = "MRQ-HR", Status = "Draft", DepartmentId = DepartmentElse, RequestedByUserId = hrUser.Id });
        db.InterviewSchedules.Add(new InterviewSchedule { Id = InterviewAssigned, TenantId = TenantId, CompanyId = CompanyA, ApplicationId = applicationA.Id, InterviewerNames = "Ivy Interviewer, Mona Manager", InterviewerUserIds = managerUser.Id.ToString(), ScheduledAt = DateTime.UtcNow.AddDays(1),
            OverallRating = 5, Recommendation = "Reject", FeedbackNotes = "HR verdict: do not hire" });
        // An interview from before panel ids were stored (exact name), one whose panel has a similar name, and one whose name two people share.
        db.InterviewSchedules.Add(new InterviewSchedule { Id = InterviewLegacyName, TenantId = TenantId, CompanyId = CompanyA, ApplicationId = applicationA.Id, InterviewerNames = "mona   manager", ScheduledAt = DateTime.UtcNow.AddDays(3) });
        db.InterviewSchedules.Add(new InterviewSchedule { Id = InterviewSimilarName, TenantId = TenantId, CompanyId = CompanyA, ApplicationId = applicationA.Id, InterviewerNames = "Mona Manager Jr, Ivy Interviewer", ScheduledAt = DateTime.UtcNow.AddDays(4) });
        db.InterviewSchedules.Add(new InterviewSchedule { Id = InterviewSharedName, TenantId = TenantId, CompanyId = CompanyA, ApplicationId = applicationA.Id, InterviewerNames = "Sam Sharedname", ScheduledAt = DateTime.UtcNow.AddDays(5) });
        db.InterviewFeedbacks.Add(new InterviewFeedback { TenantId = TenantId, CompanyId = CompanyA, InterviewScheduleId = InterviewAssigned, ApplicationId = applicationA.Id, InterviewerUserId = managerUser.Id, InterviewerName = "Mona Manager", Notes = "my own notes", OverallScore = 4 });
        db.WorkforcePlans.Add(new WorkforcePlan { Id = PlanId, TenantId = TenantId, CompanyId = CompanyA, PlanCode = "WFP-ROLE", PlanYear = DateTime.UtcNow.Year, PlanName = "Plan", DepartmentId = DepartmentOwn, Status = "Draft" });
        db.InterviewSchedules.Add(new InterviewSchedule { Id = InterviewOther, TenantId = TenantId, CompanyId = CompanyA, ApplicationId = applicationA.Id, InterviewerNames = "Ivy Interviewer", ScheduledAt = DateTime.UtcNow.AddDays(2) });
        db.InterviewSchedules.Add(new InterviewSchedule { Id = InterviewOtherCompany, TenantId = TenantId, CompanyId = CompanyB, ApplicationId = applicationB.Id, InterviewerNames = "Mona Manager", ScheduledAt = DateTime.UtcNow.AddDays(2) });
        db.InterviewFeedbacks.Add(new InterviewFeedback { TenantId = TenantId, CompanyId = CompanyA, InterviewScheduleId = InterviewAssigned, ApplicationId = applicationA.Id, InterviewerUserId = officerUser.Id, InterviewerName = "Peer Panelist", Notes = "peer notes" });
        (Guid Draft, Guid App, Guid Opening, Guid Offer) Hire(string tag, Guid maker)
        {
            var hireCandidate = new Candidate { TenantId = TenantId, CompanyId = CompanyA, FirstName = tag, LastName = "Hire", Email = $"{tag.ToLowerInvariant()}@example.test" };
            var opening = new JobOpening { TenantId = TenantId, CompanyId = CompanyA, JobCode = $"JOB-{tag}", Title = "Hired role", Status = "Closed", HeadCount = 1, FilledCount = 1 };
            var draftId = Guid.NewGuid(); var offerId = Guid.NewGuid();
            var hired = new JobApplication { TenantId = TenantId, CompanyId = CompanyA, JobOpeningId = opening.Id, CandidateId = hireCandidate.Id, CandidateName = tag, JobTitle = "Hired role",
                Stage = "Hired", StageOrder = 6, Status = "Hired", OnboardingDraftId = draftId, HiredAtUtc = DateTime.UtcNow };
            db.AddRange(hireCandidate, opening, hired,
                new OfferLetter { Id = offerId, TenantId = TenantId, CompanyId = CompanyA, ApplicationId = hired.Id, CandidateName = tag, OfferedJobTitle = "Hired role", OfferedDepartment = "Own dept",
                    StartDate = new DateOnly(2027, 3, 1), BasicSalary = 1000, GrossSalary = 1000, Status = "Accepted", AcceptedAtUtc = DateTime.UtcNow },
                new EmployeeDraft { Id = draftId, TenantId = TenantId, CreatedByUserId = maker, Status = "PendingHrApproval", CurrentStep = "HrApproval", EnglishName = tag,
                    AcceptedOfferId = offerId, AcceptedOfferCompanyId = CompanyA, SubmittedAtUtc = DateTime.UtcNow });
            return (draftId, hired.Id, opening.Id, offerId);
        }
        HireForOfficerRefusal = Hire("HireOfficerRefused", hrUser.Id);
        HireForApproverCancel = Hire("HireApproverCancel", hrUser.Id);
        HireForMakerCancel = Hire("HireMakerCancel", officerUser.Id);
        var blacklistTargets = Enumerable.Range(1, 4).Select(n => new Candidate { TenantId = TenantId, CompanyId = CompanyA, FirstName = $"Blk{n}", LastName = "Target", Email = $"blk{n}@example.test" }).ToArray();
        db.Candidates.AddRange(blacklistTargets);
        BlacklistTargets = blacklistTargets.Select(c => c.Id).ToArray();
        await db.SaveChangesAsync();
    }

    private async Task<User> AddUser(ZayraDbContext db, IServiceScope scope, string key, string fullName, string role, bool groupScope, Guid? company, int? employeeId = null)
    {
        var email = $"{key.ToLowerInvariant()}-{TenantId:N}@scope.local";
        var user = new User
        {
            TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = fullName, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = groupScope,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var roleRow = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == role);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
        if (company is Guid c) db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = TenantId, UserId = user.Id, CompanyId = c, Role = "HR", IsActive = true });
        if (employeeId is int e)
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = TenantId, EmployeeId = e, UserId = user.Id, AccessMode = AccessModes.FullPortal, Status = "Active", RequiresPasswordSetup = false });
        await db.SaveChangesAsync();

        var loaded = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Tenant)
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EmployeeUserAccounts).Include(u => u.EntityAccesses)
            .SingleAsync(u => u.TenantId == TenantId && u.Email == email);
        var grants = loaded.EntityAccesses.Where(a => a.IsActive).Select(a => new EntityAccessGrant(a.CompanyId, a.Role, a.GrantMode)).ToList();
        _tokens[key] = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(loaded, AuthService.GetRoles(loaded),
            AuthService.GetPermissions(loaded), loaded.Tenant!, grants,
            EntityScopeClaims.Resolve(loaded.IsGroupScope, grants, Array.Empty<Guid>()), out _);
        return user;
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await Client.SendAsync(request);
        if ((int)response.StatusCode >= 500)
            throw new Xunit.Sdk.XunitException($"{method} {path} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response;
    }

    public async Task<HttpStatusCode> Status(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var r = await Send(method, path, bearer, body);
        return r.StatusCode;
    }

    public async Task<JsonElement> Json(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var r = await Send(method, path, bearer, body);
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, $"{method} {path}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<Guid[]> Ids(string path, string bearer)
    {
        var json = await Json(HttpMethod.Get, path, bearer);
        var items = json.ValueKind == JsonValueKind.Array ? json : json.GetProperty("items");
        return items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
    }

    public async Task<T> Read<T>(Func<ZayraDbContext, Task<T>> query)
    {
        using var scope = Host.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ZayraDbContext>());
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
