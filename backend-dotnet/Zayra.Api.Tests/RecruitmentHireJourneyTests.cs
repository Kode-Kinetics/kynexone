using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F08 — the hire-to-active journey: candidate -> assessment -> offer approval -> offer sent and
/// accepted -> employee draft -> onboarding tasks -> activation. Each test pins one link that was
/// broken on main, against the real Postgres provider the product runs on.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class RecruitmentHireJourneyTests
{
    private readonly PostgresFixture _fixture;

    public RecruitmentHireJourneyTests(PostgresFixture fixture) => _fixture = fixture;

    // ── Offer approval -> send ────────────────────────────────────────────────

    [Fact]
    public async Task ApplicationSendOffer_SendsAnOfferWhoseApprovalChainIsComplete()
    {
        // The application drawer sends through ApplicationsController. It accepted only Draft, so an
        // offer that had been through approval (Status = Approved) could never be sent from there.
        var seeded = await SeedAsync(offerStatus: "Approved");
        await using (var db = _fixture.CreateDb())
        {
            db.OfferApprovals.Add(Approval(seeded, 1, "Approved"));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDb())
        {
            var result = await Applications(db, seeded.TenantId, Guid.NewGuid())
                .SendOffer(seeded.OfferId, CancellationToken.None);
            result.Should().BeOfType<OkObjectResult>();
        }

        await using var verify = _fixture.CreateDb();
        (await verify.OfferLetters.AsNoTracking().SingleAsync(x => x.Id == seeded.OfferId))
            .Status.Should().Be("Sent");
    }

    [Fact]
    public async Task BothSendEndpoints_RefuseAnOfferStillAwaitingApproval()
    {
        var seeded = await SeedAsync(offerStatus: "PendingApproval");
        await using (var db = _fixture.CreateDb())
        {
            db.OfferApprovals.Add(Approval(seeded, 1, "Pending"));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDb())
            (await Offers(db, seeded.TenantId).Send(seeded.OfferId, CancellationToken.None))
                .Should().NotBeOfType<OkObjectResult>();
        await using (var db = _fixture.CreateDb())
            (await Applications(db, seeded.TenantId, Guid.NewGuid()).SendOffer(seeded.OfferId, CancellationToken.None))
                .Should().NotBeOfType<OkObjectResult>();

        await using var verify = _fixture.CreateDb();
        (await verify.OfferLetters.AsNoTracking().SingleAsync(x => x.Id == seeded.OfferId))
            .Status.Should().Be("PendingApproval");
    }

    [Fact]
    public async Task AddApproval_AfterAStepWasRejected_IsRefusedInsteadOfParkingTheOfferForever()
    {
        // A rejection returns the offer to Draft with the Rejected row still on it. Adding another
        // step used to move it to PendingApproval, where "every step Approved" could never become
        // true again: the offer sat in PendingApproval with no way to send, decline or re-approve it.
        var seeded = await SeedAsync(offerStatus: "Draft");
        await using (var db = _fixture.CreateDb())
        {
            db.OfferApprovals.Add(Approval(seeded, 1, "Rejected"));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDb())
        {
            var result = await Offers(db, seeded.TenantId).AddApproval(
                seeded.OfferId, new AddOfferApprovalRequest("Finance", null, "Finance Approver"), CancellationToken.None);
            var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
            JsonSerializer.Serialize(conflict.Value).Should().Contain("offer_approval_rejected");
        }

        await using var verify = _fixture.CreateDb();
        (await verify.OfferLetters.AsNoTracking().SingleAsync(x => x.Id == seeded.OfferId))
            .Status.Should().Be("Draft");
        (await verify.OfferApprovals.CountAsync(x => x.OfferLetterId == seeded.OfferId)).Should().Be(1);
    }

    [Fact]
    public async Task RegeneratingAfterARejection_KeepsTheRejectedOfferOnRecord()
    {
        // The rejection's remedy is a revised offer. Generating one deleted every Draft offer on the
        // application, including the rejected one its approval rows still point at.
        var seeded = await SeedAsync(offerStatus: "Draft");
        await SeedOfferPlacementAsync(seeded.TenantId);
        await using (var db = _fixture.CreateDb())
        {
            db.OfferApprovals.Add(Approval(seeded, 1, "Rejected"));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDb())
            (await Applications(db, seeded.TenantId, Guid.NewGuid()).GenerateOffer(
                    seeded.ApplicationId,
                    new GenerateOfferRequest("People", DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)), 18_000, 5_000, 1_500, 0, 3),
                    CancellationToken.None))
                .Should().BeOfType<CreatedResult>();

        await using var verify = _fixture.CreateDb();
        var offers = await verify.OfferLetters.AsNoTracking().Where(x => x.ApplicationId == seeded.ApplicationId).ToListAsync();
        offers.Should().HaveCount(2);
        offers.Should().Contain(x => x.Id == seeded.OfferId && x.GrossSalary == 26_500);
    }

    // ── Offer acceptance -> employee draft -> activation ─────────────────────

    [Fact]
    public async Task AcceptedOffer_ProducesADraftHrCanActivate_AndLinksItsOnboardingTasks()
    {
        var seeded = await SeedAsync(offerStatus: "Sent");
        await using (var db = _fixture.CreateDb())
        {
            // Master data the offer names; activation resolves department/designation by name.
            db.Departments.Add(new Department { TenantId = seeded.TenantId, Code = "PPL", NameEn = "People", IsActive = true });
            db.Designations.Add(new Designation { TenantId = seeded.TenantId, Code = "HRL", TitleEn = "Enterprise HR Lead", IsActive = true });
            // A pre-joining task raised against the application before the person exists.
            db.OnboardingTasks.Add(new OnboardingTask
            {
                TenantId = seeded.TenantId,
                ApplicationId = seeded.ApplicationId,
                TaskTitle = "Prepare laptop",
                IsMandatory = true,
            });
            await db.SaveChangesAsync();
        }

        var recruiter = Guid.NewGuid();
        await using (var db = _fixture.CreateDb())
            (await Applications(db, seeded.TenantId, recruiter).AcceptOffer(seeded.OfferId, CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();

        Guid draftId;
        await using (var db = _fixture.CreateDb())
            draftId = (await db.JobApplications.AsNoTracking().SingleAsync(x => x.Id == seeded.ApplicationId))
                .OnboardingDraftId!.Value;

        // A different, group-level HR user activates the hire. There is no screen or endpoint that
        // "submits" a recruitment draft, so it has to arrive in a state approval accepts.
        await using (var db = _fixture.CreateDb())
        {
            var approval = await Employees(db, seeded.TenantId, Guid.NewGuid()).ApproveDraft(draftId, CancellationToken.None);
            approval.Result.Should().BeOfType<OkObjectResult>(
                "an accepted offer's draft must be approvable without a hidden resubmit step");
        }

        await using var verify = _fixture.CreateDb();
        var activatedDraft = await verify.EmployeeDrafts.AsNoTracking().SingleAsync(x => x.Id == draftId);
        activatedDraft.Status.Should().Be("Activated");
        activatedDraft.SubmittedAtUtc.Should().NotBeNull("acceptance is recorded as the submission");
        var employee = await verify.Employees.AsNoTracking().SingleAsync(x => x.TenantId == seeded.TenantId);
        employee.Status.Should().Be(EmployeeStatuses.Active);
        employee.FullName.Should().Be("Noura Al Mansoori");
        (await verify.OnboardingTasks.AsNoTracking().SingleAsync(x => x.TenantId == seeded.TenantId))
            .EmployeeId.Should().Be(employee.PublicId, "pre-joining tasks follow the hire onto the employee");
    }

    [Fact]
    public async Task AdvanceFromOfferToHired_IsRefused_BecauseOnlyOfferAcceptanceCreatesTheEmployeeDraft()
    {
        // "Move to Hired" marked the application Hired and consumed a seat, but created no employee
        // draft; the offer could then never be accepted (the application was no longer Active).
        var seeded = await SeedAsync(offerStatus: "Sent");

        await using (var db = _fixture.CreateDb())
        {
            var result = await Applications(db, seeded.TenantId, Guid.NewGuid())
                .Advance(seeded.ApplicationId, new AdvanceRequest(null, "HR"), CancellationToken.None);
            var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
            JsonSerializer.Serialize(conflict.Value).Should().Contain("hire_requires_offer_acceptance");
        }

        await using var verify = _fixture.CreateDb();
        var app = await verify.JobApplications.AsNoTracking().SingleAsync(x => x.Id == seeded.ApplicationId);
        app.Status.Should().Be("Active");
        app.Stage.Should().Be("Offer");
        (await verify.JobOpenings.AsNoTracking().SingleAsync(x => x.Id == seeded.OpeningId)).FilledCount.Should().Be(0);
        (await verify.EmployeeDrafts.CountAsync(x => x.TenantId == seeded.TenantId)).Should().Be(0);
    }

    // ── Assessment scoring ───────────────────────────────────────────────────

    [Fact]
    public async Task AssessmentWithoutAQuestionBank_ScoresTheEnteredPercentage()
    {
        // Templates created without questions have TotalMarks = 0. The result used to be forced to
        // 0%, so every such assessment failed whatever score HR recorded.
        var (tenantId, assessmentId) = await SeedAssessmentAsync(templateTotalMarks: 0, passingScore: 70);

        await using (var db = _fixture.CreateDb())
            (await Assessments(db, tenantId).RecordResult(assessmentId, new RecordResultRequest(85), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();

        await using var verify = _fixture.CreateDb();
        var saved = await verify.CandidateAssessments.AsNoTracking().SingleAsync(x => x.Id == assessmentId);
        saved.Status.Should().Be("Completed");
        saved.ScorePercentage.Should().Be(85m);
        saved.Passed.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 101)]    // no question bank: the entry is a percentage
    [InlineData(0, -1)]
    [InlineData(20, 21)]    // question bank of 20 marks: the entry is raw marks
    [InlineData(20, -1)]
    public async Task AssessmentScoreOutsideItsScale_IsRejectedAndNothingIsRecorded(int totalMarks, int score)
    {
        var (tenantId, assessmentId) = await SeedAssessmentAsync(templateTotalMarks: totalMarks, passingScore: 50);

        await using (var db = _fixture.CreateDb())
            (await Assessments(db, tenantId).RecordResult(assessmentId, new RecordResultRequest(score), CancellationToken.None))
                .Should().BeOfType<BadRequestObjectResult>();

        await using var verify = _fixture.CreateDb();
        var saved = await verify.CandidateAssessments.AsNoTracking().SingleAsync(x => x.Id == assessmentId);
        saved.Status.Should().Be("Sent");
        saved.ScorePercentage.Should().BeNull();
    }

    [Fact]
    public async Task AssessmentWithAQuestionBank_StillScoresRawMarksAgainstTheTotal()
    {
        var (tenantId, assessmentId) = await SeedAssessmentAsync(templateTotalMarks: 20, passingScore: 70);

        await using (var db = _fixture.CreateDb())
            (await Assessments(db, tenantId).RecordResult(assessmentId, new RecordResultRequest(15), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();

        await using var verify = _fixture.CreateDb();
        var saved = await verify.CandidateAssessments.AsNoTracking().SingleAsync(x => x.Id == assessmentId);
        saved.ScorePercentage.Should().Be(75m);
        saved.Passed.Should().BeTrue();
    }

    // ── Offer currency ───────────────────────────────────────────────────────

    [Fact]
    public async Task GeneratedOffer_NamesTheLegalEntitysCurrency_NotAHardCodedAed()
    {
        var seeded = await SeedAsync(offerStatus: null, companyCurrency: "SAR");
        await SeedOfferPlacementAsync(seeded.TenantId);

        await using (var db = _fixture.CreateDb())
            (await Applications(db, seeded.TenantId, Guid.NewGuid()).GenerateOffer(
                    seeded.ApplicationId,
                    new GenerateOfferRequest("People", DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)), 20_000, 5_000, 1_500, 0, 3),
                    CancellationToken.None))
                .Should().BeOfType<CreatedResult>();

        await using var verify = _fixture.CreateDb();
        var evt = await verify.ApplicationEvents.AsNoTracking()
            .SingleAsync(x => x.ApplicationId == seeded.ApplicationId && x.EventType == "OfferGenerated");
        evt.Notes.Should().Contain("SAR").And.NotContain("AED");
        var offer = await verify.OfferLetters.AsNoTracking().SingleAsync(x => x.ApplicationId == seeded.ApplicationId);
        offer.ContentHtml.Should().Contain("Monthly (SAR)").And.NotContain("AED");
    }

    [Fact]
    public async Task OfferLetterPdf_UsesTheOffersLegalEntityCurrency_InAMultiEntityTenant()
    {
        // The PDF took the tenant's "most active company" currency. In a group with a UAE and a
        // Saudi entity, a Saudi offer could print in AED.
        var seeded = await SeedAsync(offerStatus: "Draft", companyCurrency: "SAR", companyActive: false);
        await using (var db = _fixture.CreateDb())
        {
            db.Companies.Add(new Company
            {
                Id = Guid.NewGuid(), TenantId = seeded.TenantId, LegalNameEn = $"UAE Co {Guid.NewGuid():N}",
                CountryCode = "AE", Jurisdiction = "UAE-mainland", RegistrationNumber = $"REG-{Guid.NewGuid():N}",
                DefaultCurrency = "AED", IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        var letters = new JourneyRecordingLetters();
        await using (var db = _fixture.CreateDb())
        {
            var controller = WithPrincipal(new OffersController(db, letters, new RecruitmentService(db)), seeded.TenantId, Guid.NewGuid());
            (await controller.Download(seeded.OfferId, CancellationToken.None)).Should().BeOfType<FileContentResult>();
        }

        letters.LastOffer.Should().NotBeNull();
        letters.LastOffer!.Currency.Should().Be("SAR");
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    /// <summary>The department and designation the seeded offer names. Generating an offer now resolves
    /// both against the organisation's records (OfferPlacement), so they have to exist.</summary>
    private async Task SeedOfferPlacementAsync(Guid tenantId)
    {
        await using var db = _fixture.CreateDb();
        db.Departments.Add(new Department { TenantId = tenantId, Code = "PPL", NameEn = "People", IsActive = true });
        db.Designations.Add(new Designation { TenantId = tenantId, Code = "HRL", TitleEn = "Enterprise HR Lead", IsActive = true });
        await db.SaveChangesAsync();
    }

    private async Task<Seeded> SeedAsync(string? offerStatus, string? companyCurrency = null, bool companyActive = true)
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        Guid? companyId = null;
        if (companyCurrency is not null)
        {
            var company = new Company
            {
                Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = $"Journey Co {Guid.NewGuid():N}",
                CountryCode = "SA", Jurisdiction = "KSA-mainland", RegistrationNumber = $"REG-{Guid.NewGuid():N}",
                DefaultCurrency = companyCurrency, IsActive = companyActive,
            };
            db.Companies.Add(company);
            companyId = company.Id;
        }

        var candidate = new Candidate
        {
            TenantId = tenantId, CompanyId = companyId,
            FirstName = "Noura", LastName = "Al Mansoori",
            Email = $"noura-{Guid.NewGuid():N}@example.test", Phone = "+966500000001",
            Nationality = "SA", Status = "Active",
        };
        var opening = new JobOpening
        {
            TenantId = tenantId, JobCode = $"JOB-{Guid.NewGuid():N}", Title = "Enterprise HR Lead",
            DepartmentName = "People", HeadCount = 3, Status = "InProgress",
        };
        var application = new JobApplication
        {
            TenantId = tenantId, CompanyId = companyId, JobOpeningId = opening.Id, JobTitle = opening.Title,
            CandidateId = candidate.Id, CandidateName = "Noura Al Mansoori", CandidateEmail = candidate.Email,
            Stage = "Offer", StageOrder = 5, Status = "Active",
        };
        db.Candidates.Add(candidate);
        db.JobOpenings.Add(opening);
        db.JobApplications.Add(application);

        Guid offerId = Guid.Empty;
        if (offerStatus is not null)
        {
            var offer = new OfferLetter
            {
                TenantId = tenantId, CompanyId = companyId, ApplicationId = application.Id,
                CandidateName = "Noura Al Mansoori", OfferedJobTitle = "Enterprise HR Lead", OfferedDepartment = "People",
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)),
                BasicSalary = 20_000, HousingAllowance = 5_000, TransportAllowance = 1_500, GrossSalary = 26_500,
                ProbationMonths = 3, ContentHtml = "<p>Offer</p>", Status = offerStatus,
                SentAtUtc = offerStatus == "Sent" ? DateTime.UtcNow : null,
            };
            db.OfferLetters.Add(offer);
            offerId = offer.Id;
        }

        await db.SaveChangesAsync();
        return new Seeded(tenantId, opening.Id, application.Id, offerId);
    }

    private async Task<(Guid TenantId, Guid AssessmentId)> SeedAssessmentAsync(int templateTotalMarks, int passingScore)
    {
        var seeded = await SeedAsync(offerStatus: null);
        await using var db = _fixture.CreateDb();
        var application = await db.JobApplications.SingleAsync(x => x.Id == seeded.ApplicationId);
        var template = new AssessmentTemplate
        {
            TenantId = seeded.TenantId, Code = $"T-{Guid.NewGuid():N}", Title = "Integration Engineering",
            AssessmentType = "Technical", PassingScore = passingScore, TotalMarks = templateTotalMarks,
        };
        var assessment = new CandidateAssessment
        {
            TenantId = seeded.TenantId, ApplicationId = application.Id, CandidateId = application.CandidateId,
            TemplateId = template.Id, TemplateName = template.Title, Status = "Sent",
            SentAtUtc = DateTime.UtcNow, TotalMarks = templateTotalMarks,
        };
        db.AssessmentTemplates.Add(template);
        db.CandidateAssessments.Add(assessment);
        await db.SaveChangesAsync();
        return (seeded.TenantId, assessment.Id);
    }

    private static OfferApproval Approval(Seeded seeded, int step, string status) => new()
    {
        TenantId = seeded.TenantId, OfferLetterId = seeded.OfferId, ApplicationId = seeded.ApplicationId,
        StepOrder = step, ApproverName = "Finance", ApproverRole = "Finance Approver", Status = status,
        DecidedAtUtc = status == "Pending" ? null : DateTime.UtcNow,
    };

    // ── Controllers ───────────────────────────────────────────────────────────

    private static ApplicationsController Applications(ZayraDbContext db, Guid tenantId, Guid userId) =>
        WithPrincipal(new ApplicationsController(db, new RecruitmentService(db), new JourneyNullNotifications()), tenantId, userId);

    private static OffersController Offers(ZayraDbContext db, Guid tenantId) =>
        WithPrincipal(new OffersController(db, new JourneyNullLetters(), new RecruitmentService(db)), tenantId, Guid.NewGuid());

    private static AssessmentsController Assessments(ZayraDbContext db, Guid tenantId) =>
        WithPrincipal(new AssessmentsController(db), tenantId, Guid.NewGuid());

    private static EmployeesController Employees(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var audit = new AuditService(db);
        return WithPrincipal(new EmployeesController(
            db, new Pbkdf2PasswordHasher(), audit, new NullDocumentStorage(), new JourneyNullNotifications(),
            new JourneyHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new JourneyNullLetters()),
            tenantId, userId);
    }

    private static T WithPrincipal<T>(T controller, Guid tenantId, Guid userId) where T : ControllerBase
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Journey HR Manager"),
            new Claim(ClaimTypes.Role, "HR Manager"),
            new Claim(EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }

    private sealed record Seeded(Guid TenantId, Guid OpeningId, Guid ApplicationId, Guid OfferId);
}

file sealed class JourneyNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName,
        string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class JourneyNullLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class JourneyRecordingLetters : ILetterService
{
    public OfferLetterData? LastOffer { get; private set; }
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default)
    {
        LastOffer = data;
        return Task.FromResult(new byte[] { 1 });
    }
}

file sealed class JourneyHijri : IHijriDateService
{
    public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}
