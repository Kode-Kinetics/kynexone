using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// Company scope for recruitment scheduling records, through the REAL pipeline (real JWT, real role and permission gates, real
/// company query filters): a one-company HR Manager reaches interviews, feedback, assessments, requisitions, openings and
/// workforce plans of their own company only; a group user reaches every company; a create is stamped with the right company;
/// another tenant's rows are never reachable.
/// </summary>
public sealed class RecruitmentSchedulingCompanyScopeHttpTests(SchedulingScopeFixture f) : IClassFixture<SchedulingScopeFixture>
{
    // ── Interviews ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InterviewList_OneCompanyHr_SeesOnlyItsOwnCompanysInterviews()
    {
        await f.OnlyCompany("/api/recruitment/interviews", f.HrA, f.CompanyA, own: f.InterviewA, other: f.InterviewB);
        await f.OnlyCompany("/api/recruitment/interviews", f.HrB, f.CompanyB, own: f.InterviewB, other: f.InterviewA);
    }

    [Fact]
    public async Task InterviewList_GroupUser_SeesBothCompanies_ButNeverAnotherTenant()
    {
        (await f.Ids("/api/recruitment/interviews", f.HrGroup)).Should().Contain([f.InterviewA, f.InterviewB]).And.NotContain(f.OtherTenantInterview);
    }

    [Fact]
    public async Task OtherCompanysInterview_IsNotFound_ForGetCompleteCancelAndFeedback()
    {
        (await f.Status(HttpMethod.Get, $"/api/recruitment/interviews/{f.InterviewB}", f.HrA)).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Patch, $"/api/recruitment/interviews/{f.InterviewB}/complete", f.HrA,
            new { overallRating = 1, recommendation = "Reject", feedbackNotes = "x" })).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Patch, $"/api/recruitment/interviews/{f.InterviewB}/cancel", f.HrA)).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/interviews/{f.InterviewB}/feedback", f.HrA, FeedbackBody())).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Get, $"/api/recruitment/interviews/{f.InterviewA}", f.HrA)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OtherCompanysInterviewFeedback_IsNotListed()
    {
        var own = await f.Json(HttpMethod.Get, $"/api/recruitment/interviews/{f.InterviewA}/feedback", f.HrA);
        own.GetArrayLength().Should().Be(1);
        var other = await f.Json(HttpMethod.Get, $"/api/recruitment/interviews/{f.InterviewB}/feedback", f.HrA);
        other.GetArrayLength().Should().Be(0, "feedback notes and ratings of another company's interview must not be readable");
    }

    [Fact]
    public async Task ApplicationDetail_DoesNotCarryAnotherCompanysInterviews()
    {
        (await f.Status(HttpMethod.Get, $"/api/recruitment/applications/{f.ApplicationB}", f.HrA)).Should().Be(HttpStatusCode.NotFound);
        var own = await f.Json(HttpMethod.Get, $"/api/recruitment/applications/{f.ApplicationA}", f.HrA);
        own.GetProperty("interviews").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ScheduleInterview_StampsTheParentApplicationsCompany()
    {
        using var created = await f.Send(HttpMethod.Post, "/api/recruitment/interviews", f.HrGroup, new
        {
            applicationId = f.ApplicationB, interviewType = "Technical", interviewerNames = "Panel", scheduledAt = DateTime.UtcNow.AddDays(3),
            durationMinutes = 45, mode = "Video",
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await f.CompanyOf<InterviewSchedule>(id)).Should().Be(f.CompanyB);

        using var viaApplication = await f.Send(HttpMethod.Post, $"/api/recruitment/applications/{f.ApplicationA}/interviews", f.HrA, new
        {
            interviewType = "HR Screening", interviewerNames = "HR", scheduledAt = DateTime.UtcNow.AddDays(4), durationMinutes = 30, mode = "Phone",
        });
        viaApplication.StatusCode.Should().Be(HttpStatusCode.Created, await viaApplication.Content.ReadAsStringAsync());
        var id2 = JsonDocument.Parse(await viaApplication.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await f.CompanyOf<InterviewSchedule>(id2)).Should().Be(f.CompanyA);
    }

    [Fact]
    public async Task ScheduleInterview_ForAnotherCompanysApplication_IsRefused()
    {
        (await f.Status(HttpMethod.Post, "/api/recruitment/interviews", f.HrA, new
        {
            applicationId = f.ApplicationB, interviewType = "Technical", interviewerNames = "x", scheduledAt = DateTime.UtcNow.AddDays(3),
            durationMinutes = 45, mode = "Video",
        })).Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SubmitFeedback_StampsTheParentInterviewsCompany()
    {
        using var r = await f.Send(HttpMethod.Post, $"/api/recruitment/interviews/{f.InterviewB}/feedback", f.HrGroup, FeedbackBody());
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await f.CompanyOf<InterviewFeedback>(id)).Should().Be(f.CompanyB);
    }

    // ── Assessments ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AssessmentList_OneCompanyHr_SeesOnlyItsOwn_AndCannotRecordAnotherCompanysResult()
    {
        await f.OnlyCompany("/api/recruitment/assessments", f.HrA, f.CompanyA, own: f.AssessmentA, other: f.AssessmentB);
        (await f.Ids("/api/recruitment/assessments", f.HrGroup)).Should().Contain([f.AssessmentA, f.AssessmentB]);
        (await f.Status(HttpMethod.Patch, $"/api/recruitment/assessments/{f.AssessmentB}/result", f.HrA, new { scoreObtained = 50 }))
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SendAssessment_StampsTheParentApplicationsCompany()
    {
        using var r = await f.Send(HttpMethod.Post, "/api/recruitment/assessments/send", f.HrGroup,
            new { applicationId = f.ApplicationB, templateId = f.SecondTemplateId, expiryDays = 5 });
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await f.CompanyOf<CandidateAssessment>(id)).Should().Be(f.CompanyB);
    }

    // ── Requisitions ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequisitionList_OneCompanyHr_SeesOnlyItsOwn_AndStatsCountOnlyItsOwn()
    {
        var own = await f.OnlyCompany("/api/recruitment/requisitions", f.HrA, f.CompanyA, own: f.RequisitionA, other: f.RequisitionB);
        (await f.Ids("/api/recruitment/requisitions", f.HrGroup)).Should().Contain([f.RequisitionA, f.RequisitionB]);
        var stats = await f.Json(HttpMethod.Get, "/api/recruitment/requisitions/stats", f.HrA);
        stats.GetProperty("total").GetInt32().Should().Be(own.Length, "the counts cover the caller's company only");
    }

    [Fact]
    public async Task OtherCompanysRequisition_IsNotFound_ForGetUpdateSubmitAndDecide()
    {
        var id = f.RequisitionB;
        (await f.Status(HttpMethod.Get, $"/api/recruitment/requisitions/{id}", f.HrA)).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Put, $"/api/recruitment/requisitions/{id}", f.HrA, RequisitionBody(null, null))).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/requisitions/{id}/submit", f.HrA)).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/requisitions/{id}/approve", f.HrA, new { comments = "ok" })).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/requisitions/{id}/reject", f.HrA, new { reason = "no" })).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateRequisition_StampsTheDepartmentsCompany_OrTheOneCompanyTheActorHas()
    {
        using var viaDepartment = await f.Send(HttpMethod.Post, "/api/recruitment/requisitions", f.HrGroup, RequisitionBody(f.DepartmentB, null));
        viaDepartment.StatusCode.Should().Be(HttpStatusCode.Created, await viaDepartment.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await viaDepartment.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await f.CompanyOf<ManpowerRequisition>(id)).Should().Be(f.CompanyB);

        using var oneCompanyActor = await f.Send(HttpMethod.Post, "/api/recruitment/requisitions", f.HrA, RequisitionBody(null, null));
        oneCompanyActor.StatusCode.Should().Be(HttpStatusCode.Created, await oneCompanyActor.Content.ReadAsStringAsync());
        var id2 = JsonDocument.Parse(await oneCompanyActor.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await f.CompanyOf<ManpowerRequisition>(id2)).Should().Be(f.CompanyA);

        using var explicitCompany = await f.Send(HttpMethod.Post, "/api/recruitment/requisitions", f.HrGroup, RequisitionBody(null, f.CompanyA));
        explicitCompany.StatusCode.Should().Be(HttpStatusCode.Created, await explicitCompany.Content.ReadAsStringAsync());
        var id3 = JsonDocument.Parse(await explicitCompany.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await f.CompanyOf<ManpowerRequisition>(id3)).Should().Be(f.CompanyA);
    }

    [Fact]
    public async Task CreateRequisition_GroupUserWithNoCompanyAndNoDepartment_IsAskedForOne()
    {
        (await f.Status(HttpMethod.Post, "/api/recruitment/requisitions", f.HrGroup, RequisitionBody(null, null))).Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateRequisition_InAnotherCompany_IsRefused_ForAOneCompanyUser()
    {
        (await f.Status(HttpMethod.Post, "/api/recruitment/requisitions", f.HrA, RequisitionBody(null, f.CompanyB))).Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Openings ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpeningList_OneCompanyHr_SeesOnlyItsOwn_AndStatsAndExportAndReportsAgree()
    {
        var own = await f.OnlyCompany("/api/recruitment/openings", f.HrA, f.CompanyA, own: f.OpeningA, other: f.OpeningB);
        (await f.Ids("/api/recruitment/openings", f.HrGroup)).Should().Contain([f.OpeningA, f.OpeningB]);

        var stats = await f.Json(HttpMethod.Get, "/api/recruitment/openings/stats", f.HrA);
        stats.GetProperty("totalOpenings").GetInt32().Should().Be(own.Length, "the counts cover the caller's company only");

        var csv = await (await f.Send(HttpMethod.Get, "/api/recruitment/openings/export", f.HrA)).Content.ReadAsStringAsync();
        csv.Should().Contain("Opening A").And.NotContain("Opening B");

        var openPositions = await f.Json(HttpMethod.Get, "/api/recruitment/reports/open-positions", f.HrA);
        openPositions.GetProperty("total").GetInt32().Should().Be(own.Length);
        var pipeline = await f.Json(HttpMethod.Get, "/api/recruitment/reports/pipeline-summary", f.HrA);
        pipeline.GetProperty("byOpening").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task OtherCompanysOpening_IsNotFound_ForGetUpdateAndClose()
    {
        var id = f.OpeningB;
        (await f.Status(HttpMethod.Get, $"/api/recruitment/openings/{id}", f.HrA)).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Put, $"/api/recruitment/openings/{id}", f.HrA, OpeningBody(null, null))).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Post, $"/api/recruitment/openings/{id}/close", f.HrA)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateOpening_StampsTheSourceRequisitionsCompany_AndRefusesAnotherCompanysRequisition()
    {
        using var fromRequisition = await f.Send(HttpMethod.Post, "/api/recruitment/openings", f.HrGroup, OpeningBody(f.RequisitionB, null));
        fromRequisition.StatusCode.Should().Be(HttpStatusCode.Created, await fromRequisition.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await fromRequisition.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await f.CompanyOf<JobOpening>(id)).Should().Be(f.CompanyB);

        (await f.Status(HttpMethod.Post, "/api/recruitment/openings", f.HrA, OpeningBody(f.RequisitionB, null))).Should().Be(HttpStatusCode.BadRequest);

        using var oneCompanyActor = await f.Send(HttpMethod.Post, "/api/recruitment/openings", f.HrA, OpeningBody(null, null));
        oneCompanyActor.StatusCode.Should().Be(HttpStatusCode.Created, await oneCompanyActor.Content.ReadAsStringAsync());
        var id2 = JsonDocument.Parse(await oneCompanyActor.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await f.CompanyOf<JobOpening>(id2)).Should().Be(f.CompanyA);
    }

    // ── Workforce plans ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkforcePlanList_OneCompanyHr_SeesOnlyItsOwn_AndCannotTouchAnotherCompanysPlan()
    {
        await f.OnlyCompany("/api/recruitment/workforce-planning", f.HrA, f.CompanyA, own: f.PlanA, other: f.PlanB);
        (await f.Ids("/api/recruitment/workforce-planning", f.HrGroup)).Should().Contain([f.PlanA, f.PlanB]);
        var summary = await f.Json(HttpMethod.Get, "/api/recruitment/workforce-planning/summary", f.HrA);
        summary.GetProperty("totalPlans").GetInt32().Should().Be(1);
        (await f.Status(HttpMethod.Get, $"/api/recruitment/workforce-planning/{f.PlanB}", f.HrA)).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Patch, $"/api/recruitment/workforce-planning/{f.PlanB}/status", f.HrA, new { status = "Approved" })).Should().Be(HttpStatusCode.NotFound);
        // Deleting is a recruitment.delete act (HR Manager does not hold it), so it is shown with a one-company Admin.
        (await f.Status(HttpMethod.Delete, $"/api/recruitment/workforce-planning/{f.PlanB}", f.Token("AdminA"))).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateWorkforcePlan_StampsTheDepartmentsCompany()
    {
        using var r = await f.Send(HttpMethod.Post, "/api/recruitment/workforce-planning", f.HrGroup,
            new { planName = "Plan", planYear = 2027, departmentId = f.DepartmentB, currentHeadcount = 3, plannedHeadcount = 5, budgetAllocated = 1000, currencyCode = "SAR" });
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await f.CompanyOf<WorkforcePlan>(id)).Should().Be(f.CompanyB);
    }

    // ── Numbers are per tenant, whichever company's rows the caller can see ──────────────────────────────────────────────

    [Fact]
    public async Task RequisitionOpeningAndPlanNumbers_AreNeverReusedAcrossCompanies()
    {
        async Task<string> Create(string path, string bearer, object body, string numberProperty)
        {
            using var r = await f.Send(HttpMethod.Post, path, bearer, body);
            r.StatusCode.Should().Be(HttpStatusCode.Created, path + " " + await r.Content.ReadAsStringAsync());
            return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty(numberProperty).GetString()!;
        }

        var inB = await Create("/api/recruitment/requisitions", f.HrGroup, RequisitionBody(f.DepartmentB, null), "requisitionNumber");
        var inA = await Create("/api/recruitment/requisitions", f.HrA, RequisitionBody(null, null), "requisitionNumber");
        inA.Should().NotBe(inB, "the one-company caller cannot see the other company's number, but it is still taken");

        var openingB = await Create("/api/recruitment/openings", f.HrGroup, OpeningBody(null, f.CompanyB), "jobCode");
        var openingA = await Create("/api/recruitment/openings", f.HrA, OpeningBody(null, null), "jobCode");
        openingA.Should().NotBe(openingB);

        object Plan(Guid? department) => new { planName = "P", planYear = 2028, departmentId = department, currentHeadcount = 1, plannedHeadcount = 2, budgetAllocated = 10, currencyCode = "SAR" };
        var planB = await Create("/api/recruitment/workforce-planning", f.HrGroup, Plan(f.DepartmentB), "planCode");
        var planA = await Create("/api/recruitment/workforce-planning", f.HrA, Plan(null), "planCode");
        planA.Should().NotBe(planB);
    }

    // ── Compliance renewals ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Renewals_AreCompanyScoped_ForListAndStatusUpdate()
    {
        var list = await f.Json(HttpMethod.Get, "/api/compliance/renewals", f.Token("ComplianceA"));
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().BeEquivalentTo(new[] { f.RenewalA });
        (await f.Status(HttpMethod.Patch, $"/api/compliance/renewals/{f.RenewalB}/status", f.Token("ComplianceA"),
            new { status = "Renewed", renewalDate = "2027-01-01" })).Should().Be(HttpStatusCode.NotFound);
        (await f.Status(HttpMethod.Patch, $"/api/compliance/renewals/{f.RenewalA}/status", f.Token("ComplianceA"),
            new { status = "InProgress" })).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ACreatedRenewal_IsStampedWithTheEmployeesCompany()
    {
        using var r = await f.Send(HttpMethod.Post, "/api/compliance/renewals", f.Token("ComplianceGroup"),
            new { employeeId = f.OtherCompanyPublicId, documentType = "Visa", expiryDate = "2027-02-01" });
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        (await f.CompanyOf<ComplianceRenewal>(id)).Should().Be(f.CompanyB);
    }

    // ── The model itself ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSchedulingRecords_AreCompanyScopedOperational_SoTheGlobalFilterApplies()
    {
        foreach (var type in new[]
        {
            typeof(InterviewSchedule), typeof(InterviewFeedback), typeof(CandidateAssessment), typeof(ManpowerRequisition),
            typeof(JobOpening), typeof(WorkforcePlan), typeof(ComplianceRenewal),
        })
            typeof(ICompanyScopedOperational).IsAssignableFrom(type).Should().BeTrue(type.Name);
    }

    private static object FeedbackBody() => new
    {
        interviewerName = "Panel", interviewerRole = "HR", communicationScore = 4, technicalScore = 4, cultureFitScore = 4,
        problemSolvingScore = 4, leadershipScore = 4, overallScore = 4, strengths = "s", concerns = "c", notes = "n", recommendation = "Hire",
    };

    private static object RequisitionBody(Guid? departmentId, Guid? companyId) => new
    {
        departmentId, departmentName = "Dept", designationId = (Guid?)null, designationTitle = "Engineer", headCount = 1, employmentType = "Full-Time",
        priority = "Medium", justification = "Growth", requiredSkills = "x", minExperienceYears = 1, maxExperienceYears = 5, budgetFrom = 1000m, budgetTo = 2000m,
        targetJoiningDate = "2027-01-01", requestedByName = "HR", companyId,
    };

    private static object OpeningBody(Guid? requisitionId, Guid? companyId) => new
    {
        requisitionId, title = "Engineer", departmentName = "Dept", designationTitle = "Engineer", employmentType = "Full-Time", headCount = 1,
        description = "d", requirements = "r", responsibilities = "p", salaryFrom = 1000m, salaryTo = 2000m, location = "HQ", assignedHrName = "HR", companyId,
    };
}

public sealed class SchedulingScopeFixture : IAsyncLifetime
{
    private SqliteConnection _anchor = null!;
    private readonly Dictionary<string, string> _tokens = new();
    private readonly ErrorCapture _errors = new();
    public AuthorizationPipelineHost Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid OtherTenantId { get; } = Guid.NewGuid();
    public Guid CompanyA { get; } = Guid.NewGuid();
    public Guid CompanyB { get; } = Guid.NewGuid();
    public Guid OtherTenantCompany { get; } = Guid.NewGuid();

    public Guid DepartmentA { get; } = Guid.NewGuid();
    public Guid DepartmentB { get; } = Guid.NewGuid();
    public Guid OpeningA { get; } = Guid.NewGuid();
    public Guid OpeningB { get; } = Guid.NewGuid();
    public Guid RequisitionA { get; } = Guid.NewGuid();
    public Guid RequisitionB { get; } = Guid.NewGuid();
    public Guid ApplicationA { get; } = Guid.NewGuid();
    public Guid ApplicationB { get; } = Guid.NewGuid();
    public Guid InterviewA { get; } = Guid.NewGuid();
    public Guid InterviewB { get; } = Guid.NewGuid();
    public Guid AssessmentA { get; } = Guid.NewGuid();
    public Guid AssessmentB { get; } = Guid.NewGuid();
    public Guid TemplateId { get; } = Guid.NewGuid();
    public Guid SecondTemplateId { get; } = Guid.NewGuid();
    public Guid PlanA { get; } = Guid.NewGuid();
    public Guid PlanB { get; } = Guid.NewGuid();
    public Guid OtherTenantInterview { get; } = Guid.NewGuid();

    public string HrA => _tokens[nameof(HrA)];
    public string HrB => _tokens[nameof(HrB)];
    public string HrGroup => _tokens[nameof(HrGroup)];

    public async Task InitializeAsync()
    {
        var connection = $"Data Source=file:rec-sched-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _anchor = new SqliteConnection(connection);
        await _anchor.OpenAsync();
        Host = new AuthorizationPipelineHost(connection, services => services.AddLogging(b => b.AddProvider(_errors)));
        Client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await db.Database.EnsureCreatedAsync();

        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Scope", Slug = $"scope-{TenantId:N}", IsActive = true, AccountType = TenantAccountTypes.Group });
        db.Tenants.Add(new Tenant { Id = OtherTenantId, Name = "Other", Slug = $"other-{OtherTenantId:N}", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyA, TenantId = TenantId, LegalNameEn = "Alpha", CountryCode = "SA", IsActive = true });
        db.Companies.Add(new Company { Id = CompanyB, TenantId = TenantId, LegalNameEn = "Beta", CountryCode = "SA", IsActive = true });
        db.Companies.Add(new Company { Id = OtherTenantCompany, TenantId = OtherTenantId, LegalNameEn = "Gamma", CountryCode = "SA", IsActive = true });
        db.Departments.Add(new Department { Id = DepartmentA, TenantId = TenantId, CompanyId = CompanyA, Code = "A1", NameEn = "Dept A", IsActive = true });
        db.Departments.Add(new Department { Id = DepartmentB, TenantId = TenantId, CompanyId = CompanyB, Code = "B1", NameEn = "Dept B", IsActive = true });
        db.AssessmentTemplates.Add(new AssessmentTemplate { Id = TemplateId, TenantId = TenantId, Code = "T1", Title = "Aptitude", IsActive = true });
        db.AssessmentTemplates.Add(new AssessmentTemplate { Id = SecondTemplateId, TenantId = TenantId, Code = "T2", Title = "Logic", IsActive = true });

        var candidateA = new Candidate { TenantId = TenantId, CompanyId = CompanyA, FirstName = "Ann", LastName = "A", Email = "ann@x.com" };
        var candidateB = new Candidate { TenantId = TenantId, CompanyId = CompanyB, FirstName = "Bob", LastName = "B", Email = "bob@x.com" };
        db.Candidates.AddRange(candidateA, candidateB);
        db.JobOpenings.Add(new JobOpening { Id = OpeningA, TenantId = TenantId, CompanyId = CompanyA, JobCode = "JOB-A", Title = "Opening A", Status = "Open", HeadCount = 2 });
        db.JobOpenings.Add(new JobOpening { Id = OpeningB, TenantId = TenantId, CompanyId = CompanyB, JobCode = "JOB-B", Title = "Opening B", Status = "Open", HeadCount = 2 });
        db.ManpowerRequisitions.Add(new ManpowerRequisition { Id = RequisitionA, TenantId = TenantId, CompanyId = CompanyA, RequisitionNumber = "MRQ-A", Status = "Draft", DepartmentId = DepartmentA });
        db.ManpowerRequisitions.Add(new ManpowerRequisition { Id = RequisitionB, TenantId = TenantId, CompanyId = CompanyB, RequisitionNumber = "MRQ-B", Status = "Submitted", DepartmentId = DepartmentB });
        db.JobApplications.Add(new JobApplication { Id = ApplicationA, TenantId = TenantId, CompanyId = CompanyA, JobOpeningId = OpeningA, CandidateId = candidateA.Id, CandidateName = "Ann A", JobTitle = "Opening A" });
        db.JobApplications.Add(new JobApplication { Id = ApplicationB, TenantId = TenantId, CompanyId = CompanyB, JobOpeningId = OpeningB, CandidateId = candidateB.Id, CandidateName = "Bob B", JobTitle = "Opening B" });
        db.InterviewSchedules.Add(new InterviewSchedule { Id = InterviewA, TenantId = TenantId, CompanyId = CompanyA, ApplicationId = ApplicationA, InterviewerNames = "Ivy", ScheduledAt = DateTime.UtcNow.AddDays(1), FeedbackNotes = "A notes" });
        db.InterviewSchedules.Add(new InterviewSchedule { Id = InterviewB, TenantId = TenantId, CompanyId = CompanyB, ApplicationId = ApplicationB, InterviewerNames = "Ian", ScheduledAt = DateTime.UtcNow.AddDays(2), FeedbackNotes = "B notes" });
        db.InterviewSchedules.Add(new InterviewSchedule { Id = OtherTenantInterview, TenantId = OtherTenantId, CompanyId = OtherTenantCompany, ApplicationId = Guid.NewGuid(), InterviewerNames = "Zed", ScheduledAt = DateTime.UtcNow.AddDays(2) });
        db.InterviewFeedbacks.Add(new InterviewFeedback { TenantId = TenantId, CompanyId = CompanyA, InterviewScheduleId = InterviewA, ApplicationId = ApplicationA, InterviewerName = "Ivy", Notes = "A fb" });
        db.InterviewFeedbacks.Add(new InterviewFeedback { TenantId = TenantId, CompanyId = CompanyB, InterviewScheduleId = InterviewB, ApplicationId = ApplicationB, InterviewerName = "Ian", Notes = "B fb" });
        db.CandidateAssessments.Add(new CandidateAssessment { Id = AssessmentA, TenantId = TenantId, CompanyId = CompanyA, ApplicationId = ApplicationA, CandidateId = candidateA.Id, TemplateId = TemplateId, TemplateName = "Aptitude", Status = "Sent" });
        db.CandidateAssessments.Add(new CandidateAssessment { Id = AssessmentB, TenantId = TenantId, CompanyId = CompanyB, ApplicationId = ApplicationB, CandidateId = candidateB.Id, TemplateId = TemplateId, TemplateName = "Aptitude", Status = "Sent" });
        db.WorkforcePlans.Add(new WorkforcePlan { Id = PlanA, TenantId = TenantId, CompanyId = CompanyA, PlanCode = "WFP-A", PlanYear = DateTime.UtcNow.Year, PlanName = "A", DepartmentId = DepartmentA });
        db.WorkforcePlans.Add(new WorkforcePlan { Id = PlanB, TenantId = TenantId, CompanyId = CompanyB, PlanCode = "WFP-B", PlanYear = DateTime.UtcNow.Year, PlanName = "B", DepartmentId = DepartmentB });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(TenantId, CancellationToken.None);

        await AddUser(db, scope, nameof(HrA), "HR Manager", groupScope: false, CompanyA);
        await AddUser(db, scope, nameof(HrB), "HR Manager", groupScope: false, CompanyB);
        await AddUser(db, scope, nameof(HrGroup), "HR Manager", groupScope: true, null);
        await AddUser(db, scope, "AdminA", "Admin", groupScope: false, CompanyA);
        await AddUser(db, scope, "HrOfficerA", "HR Officer", groupScope: false, CompanyA);
        await SeedComplianceAsync(db, scope);
    }

    public int ManagerEmployeeId { get; private set; }
    public Guid InScopePublicId { get; private set; }
    public Guid OutOfScopePublicId { get; private set; }
    public Guid OtherCompanyPublicId { get; private set; }
    public Guid RenewalA { get; } = Guid.NewGuid();
    public Guid RenewalB { get; } = Guid.NewGuid();

    /// <summary>Compliance rows (visa expiries and renewals) and the personas that read them.</summary>
    private async Task SeedComplianceAsync(ZayraDbContext db, IServiceScope scope)
    {
        Employee Emp(string code, Guid company, int? manager = null) => new()
        {
            TenantId = TenantId, CompanyId = company, EmployeeCode = code, FullName = code, EnglishName = code,
            Status = EmployeeStatuses.Active, ManagerEmployeeId = manager,
        };
        var manager = Emp("M-1", CompanyA);
        db.Employees.Add(manager);
        await db.SaveChangesAsync();
        var inScope = Emp("E-IN", CompanyA, manager.Id);
        var outOfScope = Emp("E-OUT", CompanyA);
        var otherCompany = Emp("E-B", CompanyB);
        db.Employees.AddRange(inScope, outOfScope, otherCompany);
        await db.SaveChangesAsync();
        ManagerEmployeeId = manager.Id;
        InScopePublicId = inScope.PublicId;
        OutOfScopePublicId = outOfScope.PublicId;
        OtherCompanyPublicId = otherCompany.PublicId;

        var soon = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20));
        db.VisaRecords.Add(new VisaRecord { TenantId = TenantId, CompanyId = CompanyA, EmployeeId = inScope.PublicId, EmployeeName = "In scope", VisaType = "Residence", ExpiryDate = soon });
        db.VisaRecords.Add(new VisaRecord { TenantId = TenantId, CompanyId = CompanyA, EmployeeId = outOfScope.PublicId, EmployeeName = "Out of scope", VisaType = "Residence", ExpiryDate = soon });
        db.VisaRecords.Add(new VisaRecord { TenantId = TenantId, CompanyId = CompanyB, EmployeeId = otherCompany.PublicId, EmployeeName = "Other company", VisaType = "Residence", ExpiryDate = soon });
        db.ComplianceRenewals.Add(new ComplianceRenewal { Id = RenewalA, TenantId = TenantId, CompanyId = CompanyA, EmployeeId = inScope.PublicId, EmployeeName = "In scope", DocumentType = "Visa", ExpiryDate = soon });
        db.ComplianceRenewals.Add(new ComplianceRenewal { Id = RenewalB, TenantId = TenantId, CompanyId = CompanyB, EmployeeId = otherCompany.PublicId, EmployeeName = "Other company", DocumentType = "Visa", ExpiryDate = soon });
        await db.SaveChangesAsync();

        await AddUser(db, scope, "Ess", "Employee", groupScope: true, null);
        await AddUser(db, scope, "ComplianceGroup", "Compliance Officer", groupScope: true, null);
        await AddUser(db, scope, "ComplianceA", "Compliance Officer", groupScope: false, CompanyA);
        await AddUser(db, scope, "Admin", "Admin", groupScope: true, null);
        // A line manager who has been given compliance.read: employees.read + manager.read put them on TEAM data scope.
        await AddUser(db, scope, "TeamReader", "Manager", groupScope: true, null, employeeId: manager.Id, allow: ["compliance.read"]);
    }

    public async Task AddUser(ZayraDbContext db, IServiceScope scope, string key, string role, bool groupScope, Guid? company,
        int? employeeId = null, string[]? allow = null)
    {
        var email = $"{key.ToLowerInvariant()}-{TenantId:N}@scope.local";
        var user = new User
        {
            TenantId = TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = key, PasswordHash = "no-login-path",
            Status = "Active", AccessMode = "FullPortal", IsActive = true, IsEmailConfirmed = true, IsGroupScope = groupScope,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var roleRow = await db.Roles.SingleAsync(r => r.TenantId == TenantId && r.Name == role);
        db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = roleRow.Id });
        if (company is Guid c) db.UserEntityAccesses.Add(new UserEntityAccess { TenantId = TenantId, UserId = user.Id, CompanyId = c, Role = "HR", IsActive = true });
        foreach (var key2 in allow ?? [])
            db.Set<UserPermissionOverride>().Add(new UserPermissionOverride { TenantId = TenantId, UserId = user.Id, PermissionKey = key2, Effect = "Allow", Reason = "test" });
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
    }

    public string Token(string key) => _tokens[key];

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await Client.SendAsync(request);
        // A 5xx is never an expected answer: say what the server logged rather than a bare status.
        if ((int)response.StatusCode >= 500)
            throw new Xunit.Sdk.XunitException($"{method} {path} answered {(int)response.StatusCode}: {_errors.Last()}");
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

    /// <summary>
    /// Lists as <paramref name="bearer"/> and asserts the caller sees their own company's seeded row, not the other company's, and
    /// (where the rows carry it) nothing stamped with another company, whatever else other tests have created. Returns the ids.
    /// </summary>
    public async Task<Guid[]> OnlyCompany(string path, string bearer, Guid company, Guid own, Guid other)
    {
        var json = await Json(HttpMethod.Get, path, bearer);
        var items = (json.ValueKind == JsonValueKind.Array ? json : json.GetProperty("items")).EnumerateArray().ToList();
        var ids = items.Select(i => i.GetProperty("id").GetGuid()).ToArray();
        ids.Should().Contain(own).And.NotContain(other);
        var stamped = items.Where(i => i.TryGetProperty("companyId", out var c) && c.ValueKind == JsonValueKind.String)
            .Select(i => i.GetProperty("companyId").GetGuid()).ToList();
        stamped.Where(c => c != company).Should().BeEmpty("nothing of another company may be listed");
        return ids;
    }

    /// <summary>The ids on a paged list ({ items: [...] }) or a bare array.</summary>
    public async Task<Guid[]> Ids(string path, string bearer)
    {
        var json = await Json(HttpMethod.Get, path, bearer);
        var items = json.ValueKind == JsonValueKind.Array ? json : json.GetProperty("items");
        return items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
    }

    public async Task<Guid?> CompanyOf<T>(Guid id) where T : class
    {
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var row = await db.Set<T>().IgnoreQueryFilters().AsNoTracking().SingleAsync(e => EF.Property<Guid>(e, "Id") == id);
        return db.Entry(row).Property<Guid?>("CompanyId").CurrentValue;
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}

/// <summary>Keeps the last error-level log entry (with its exception) so a 5xx in a test names its cause.</summary>
internal sealed class ErrorCapture : Microsoft.Extensions.Logging.ILoggerProvider
{
    private string _last = "(nothing logged)";
    public string Last() => _last;
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capture(this);
    public void Dispose() { }

    private sealed class Capture(ErrorCapture owner) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Error;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error)
                owner._last = exception?.ToString() is { Length: > 0 } text ? text[..Math.Min(text.Length, 900)] : formatter(state, exception);
        }
    }
}
