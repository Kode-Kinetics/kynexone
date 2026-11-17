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
    public async Task AManager_GivesFeedback_OnAnInterviewTheyAreAssignedTo_ByEitherRoute()
    {
        using var viaInterviews = await f.Send(HttpMethod.Post, $"/api/recruitment/interviews/{f.InterviewAssigned}/feedback", f.ManagerA, FeedbackBody());
        viaInterviews.StatusCode.Should().Be(HttpStatusCode.OK, await viaInterviews.Content.ReadAsStringAsync());
        using var viaApplications = await f.Send(HttpMethod.Post, $"/api/recruitment/applications/interviews/{f.InterviewAssigned}/feedback", f.ManagerA,
            new { overallRating = 4, recommendation = "Proceed", feedbackNotes = "Strong" });
        viaApplications.StatusCode.Should().Be(HttpStatusCode.OK, await viaApplications.Content.ReadAsStringAsync());
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
        var feedbackAuthors = detail.GetProperty("feedbacks").EnumerateArray().Select(x => x.GetProperty("interviewerName").GetString()).ToArray();
        feedbackAuthors.Should().NotContain("Peer Panelist", "a manager never reads a peer's scorecard");
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
        db.InterviewSchedules.Add(new InterviewSchedule { Id = InterviewAssigned, TenantId = TenantId, CompanyId = CompanyA, ApplicationId = applicationA.Id, InterviewerNames = "Ivy Interviewer, Mona Manager", ScheduledAt = DateTime.UtcNow.AddDays(1) });
        db.InterviewSchedules.Add(new InterviewSchedule { Id = InterviewOther, TenantId = TenantId, CompanyId = CompanyA, ApplicationId = applicationA.Id, InterviewerNames = "Ivy Interviewer", ScheduledAt = DateTime.UtcNow.AddDays(2) });
        db.InterviewSchedules.Add(new InterviewSchedule { Id = InterviewOtherCompany, TenantId = TenantId, CompanyId = CompanyB, ApplicationId = applicationB.Id, InterviewerNames = "Mona Manager", ScheduledAt = DateTime.UtcNow.AddDays(2) });
        db.InterviewFeedbacks.Add(new InterviewFeedback { TenantId = TenantId, CompanyId = CompanyA, InterviewScheduleId = InterviewAssigned, ApplicationId = applicationA.Id, InterviewerUserId = officerUser.Id, InterviewerName = "Peer Panelist", Notes = "peer notes" });
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

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Host is not null) await Host.DisposeAsync();
        if (_anchor is not null) await _anchor.DisposeAsync();
    }
}
