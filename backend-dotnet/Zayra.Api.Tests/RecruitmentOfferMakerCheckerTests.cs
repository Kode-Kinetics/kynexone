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
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F08 review — who may approve, send and decline an offer, and where the hire lands. Each test is a
/// hole an adversarial review proved on #132: a rejected offer re-sent through a revision, one HR
/// user approving their own offer, anyone deciding anyone's approval step, an approver releasing the
/// offer they approved, the drawer declining an accepted offer, and a hire activated in a different
/// legal entity from the one that made the offer.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class RecruitmentOfferMakerCheckerTests
{
    private readonly PostgresFixture _fixture;

    public RecruitmentOfferMakerCheckerTests(PostgresFixture fixture) => _fixture = fixture;

    // ── Approval policy ───────────────────────────────────────────────────────

    [Fact]
    public async Task AnOfferWithNoApprovalSteps_IsNotSent_WhenTheTenantHasNotOptedOut()
    {
        var w = await SeedWorldAsync();
        var offerId = await GenerateAsync(w, w.Author);

        await using var db = _fixture.CreateDb();
        var sent = await Applications(db, w.TenantId, w.Author).SendOffer(offerId, CancellationToken.None);

        ErrorOf(sent.Should().BeOfType<ConflictObjectResult>().Subject).Should().Be("offer_approval_required");
        (await StatusOf(offerId)).Should().Be("Draft");
    }

    [Fact]
    public async Task WithThePolicySwitchedOff_AnUnapprovedOffer_CanBeSent()
    {
        var w = await SeedWorldAsync(policy: "false");
        var offerId = await GenerateAsync(w, w.Author);

        await using var db = _fixture.CreateDb();
        (await Applications(db, w.TenantId, w.Author).SendOffer(offerId, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ARevisionOfARejectedOffer_StillNeedsApproval_EvenWithThePolicySwitchedOff()
    {
        // The rejection message says "generate a revised offer". The revision had zero approval rows,
        // and zero rows was sendable, so the rejected terms went to the candidate unapproved.
        var w = await SeedWorldAsync(policy: "false");
        var rejectedId = await GenerateAsync(w, w.Author);
        await using (var db = _fixture.CreateDb())
        {
            db.OfferApprovals.Add(new OfferApproval
            {
                TenantId = w.TenantId, OfferLetterId = rejectedId, ApplicationId = w.ApplicationId, StepOrder = 1,
                ApproverName = "Checker", ApproverUserId = w.Checker, Status = "Rejected", DecidedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var revisedId = await GenerateAsync(w, w.Author);

        await using (var db = _fixture.CreateDb())
            ErrorOf((await Applications(db, w.TenantId, w.Author).SendOffer(revisedId, CancellationToken.None))
                .Should().BeOfType<ConflictObjectResult>().Subject).Should().Be("offer_approval_required");
        await using (var db = _fixture.CreateDb())
            ErrorOf((await Offers(db, w.TenantId, w.Author).Send(revisedId, CancellationToken.None))
                .Should().BeOfType<ConflictObjectResult>().Subject).Should().Be("offer_approval_required");
        (await StatusOf(revisedId)).Should().Be("Draft");
    }

    // ── Maker-checker ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OneHrUser_CannotApproveTheirOwnOffer_OrSendItWithoutAnotherApprover()
    {
        var w = await SeedWorldAsync();
        var offerId = await GenerateAsync(w, w.Author);

        await using (var db = _fixture.CreateDb())
        {
            var self = await Offers(db, w.TenantId, w.Author).AddApproval(
                offerId, new AddOfferApprovalRequest("Me", w.Author, "HR Manager"), CancellationToken.None);
            ErrorOf((ObjectResult)self).Should().Be("offer_maker_checker");
        }
        await using (var db = _fixture.CreateDb())
            ErrorOf((await Applications(db, w.TenantId, w.Author).SendOffer(offerId, CancellationToken.None))
                .Should().BeOfType<ConflictObjectResult>().Subject).Should().Be("offer_approval_required");

        (await StatusOf(offerId)).Should().Be("Draft");
        await using var verify = _fixture.CreateDb();
        (await verify.OfferApprovals.CountAsync(a => a.OfferLetterId == offerId)).Should().Be(0);
    }

    [Fact]
    public async Task AnApprovalStep_MustNameAnActiveHrManagerOrAdmin()
    {
        var w = await SeedWorldAsync();
        var offerId = await GenerateAsync(w, w.Author);

        await using (var db = _fixture.CreateDb())
            ErrorOf((ObjectResult)await Offers(db, w.TenantId, w.Author).AddApproval(
                offerId, new AddOfferApprovalRequest("Anyone", null, "Finance"), CancellationToken.None))
                .Should().Be("offer_approver_required");
        await using (var db = _fixture.CreateDb())
            ErrorOf((ObjectResult)await Offers(db, w.TenantId, w.Author).AddApproval(
                offerId, new AddOfferApprovalRequest("Staff", w.Staff, "Employee"), CancellationToken.None))
                .Should().Be("offer_approver_ineligible");

        await using var verify = _fixture.CreateDb();
        (await verify.OfferApprovals.CountAsync(a => a.OfferLetterId == offerId)).Should().Be(0);
    }

    [Fact]
    public async Task OnlyTheNamedApprover_DecidesTheirStep_AndTheDecisionIsRecorded()
    {
        var w = await SeedWorldAsync();
        var offerId = await GenerateAsync(w, w.Author);
        var stepId = await AddStepAsync(w, offerId, w.Checker);

        // Anyone else — another HR manager, or the author — is refused, and nothing changes.
        foreach (var intruder in new[] { w.OtherHr, w.Author })
        {
            await using var db = _fixture.CreateDb();
            var refused = await Offers(db, w.TenantId, intruder).DecideApproval(
                offerId, stepId, new DecideApprovalRequest("Approved", "looks fine"), CancellationToken.None);
            ((ObjectResult)refused).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        }
        (await StatusOf(offerId)).Should().Be("PendingApproval");

        await using (var db = _fixture.CreateDb())
            (await Offers(db, w.TenantId, w.Checker).DecideApproval(
                offerId, stepId, new DecideApprovalRequest("Approved", "within band"), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();

        (await StatusOf(offerId)).Should().Be("Approved");
        await using var verify = _fixture.CreateDb();
        (await verify.RecruitmentAuditLogs.AsNoTracking().SingleAsync(l =>
                l.EntityId == offerId.ToString() && l.Action == "ApprovalDecided"))
            .PerformedByUserId.Should().Be(w.Checker);
    }

    [Fact]
    public async Task WhoeverApprovedTheOffer_CannotAlsoSendIt()
    {
        var w = await SeedWorldAsync();
        var offerId = await GenerateAsync(w, w.Author);
        var stepId = await AddStepAsync(w, offerId, w.Checker);
        await using (var db = _fixture.CreateDb())
            (await Offers(db, w.TenantId, w.Checker).DecideApproval(
                offerId, stepId, new DecideApprovalRequest("Approved", null), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();

        await using (var db = _fixture.CreateDb())
            ErrorOf((await Applications(db, w.TenantId, w.Checker).SendOffer(offerId, CancellationToken.None))
                .Should().BeOfType<ConflictObjectResult>().Subject).Should().Be("offer_maker_checker");
        await using (var db = _fixture.CreateDb())
            ErrorOf((await Offers(db, w.TenantId, w.Checker).Send(offerId, CancellationToken.None))
                .Should().BeOfType<ConflictObjectResult>().Subject).Should().Be("offer_maker_checker");
        (await StatusOf(offerId)).Should().Be("Approved");

        await using (var db = _fixture.CreateDb())
            (await Applications(db, w.TenantId, w.Author).SendOffer(offerId, CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();
        (await StatusOf(offerId)).Should().Be("Sent");
    }

    // ── Decline ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDrawerCannotDeclineAnAcceptedOffer()
    {
        // The Offers tab refused this; the drawer's endpoint had no state check, so an accepted
        // offer could be marked Declined while its application stayed Hired and its draft live.
        var w = await SeedWorldAsync(policy: "false");
        var offerId = await GenerateAsync(w, w.Author);
        await using (var db = _fixture.CreateDb())
            (await Applications(db, w.TenantId, w.Author).SendOffer(offerId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        await using (var db = _fixture.CreateDb())
            (await Applications(db, w.TenantId, w.OtherHr).AcceptOffer(offerId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        await using (var db = _fixture.CreateDb())
        {
            var declined = await Applications(db, w.TenantId, w.Author)
                .DeclineOffer(offerId, new DeclineOfferRequest("changed mind"), CancellationToken.None);
            ErrorOf(declined.Should().BeOfType<ConflictObjectResult>().Subject).Should().Be("invalid_offer_state");
        }

        (await StatusOf(offerId)).Should().Be("Accepted");
    }

    [Fact]
    public async Task AnAcceptedOffersDraft_NamesItsAcceptorAndSender_AsHireMakers()
    {
        // Draft activation must be done by someone other than whoever sent or accepted the offer.
        // The rule is enforced where drafts are activated (EmployeesController, #137); this pins the
        // set it reads, from the offer's own audit rows.
        var w = await SeedWorldAsync(policy: "false");
        var offerId = await GenerateAsync(w, w.Author);
        await using (var db = _fixture.CreateDb())
            (await Applications(db, w.TenantId, w.Author).SendOffer(offerId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        await using (var db = _fixture.CreateDb())
            (await Applications(db, w.TenantId, w.OtherHr).AcceptOffer(offerId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        await using var verify = _fixture.CreateDb();
        var draftId = (await verify.JobApplications.AsNoTracking().SingleAsync(x => x.Id == w.ApplicationId)).OnboardingDraftId!.Value;
        var makers = await OfferRules.HireMakersForDraftAsync(verify, w.TenantId, draftId, CancellationToken.None);
        makers.Should().BeEquivalentTo(new[] { w.Author, w.OtherHr });
        makers.Should().NotContain(w.Checker);
        (await OfferRules.HireMakersForDraftAsync(verify, w.TenantId, Guid.NewGuid(), CancellationToken.None)).Should().BeEmpty();
    }

    // ── Legal entity ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AHire_CannotBeActivatedInADifferentLegalEntityFromTheOffer()
    {
        // Company A made the offer. Its department name matches a department that hangs off a
        // branch of Company B, so activation resolved the hire into B.
        var w = await SeedWorldAsync(policy: "false", companyCurrency: "SAR", departmentInOtherEntity: true);
        var offerId = await GenerateAsync(w, w.Author, department: "Operations");
        await using (var db = _fixture.CreateDb())
            (await Applications(db, w.TenantId, w.Author).SendOffer(offerId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        await using (var db = _fixture.CreateDb())
            (await Applications(db, w.TenantId, w.OtherHr).AcceptOffer(offerId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        Guid draftId;
        await using (var db = _fixture.CreateDb())
            draftId = (await db.JobApplications.AsNoTracking().SingleAsync(x => x.Id == w.ApplicationId)).OnboardingDraftId!.Value;

        await using (var db = _fixture.CreateDb())
        {
            var activate = async () =>
            {
                var result = await Employees(db, w.TenantId, w.Checker).ApproveDraft(draftId, CancellationToken.None);
                result.Result.Should().NotBeOfType<OkObjectResult>();
            };
            await activate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*legal entity*");
        }

        await using var verify = _fixture.CreateDb();
        (await verify.Employees.CountAsync(e => e.TenantId == w.TenantId)).Should().Be(0);
        (await verify.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId)).Status.Should().NotBe("Activated");
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    private sealed record World(
        Guid TenantId, Guid ApplicationId, Guid? CompanyId,
        Guid Author, Guid Checker, Guid OtherHr, Guid Staff);

    private async Task<World> SeedWorldAsync(string? policy = null, string? companyCurrency = null, bool departmentInOtherEntity = false)
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        Guid? companyId = null;
        if (companyCurrency is not null)
        {
            var company = NewCompany(tenantId, "SA", companyCurrency);
            db.Companies.Add(company);
            companyId = company.Id;
        }

        db.Departments.Add(new Department { TenantId = tenantId, Code = "PPL", NameEn = "People", IsActive = true });
        db.Designations.Add(new Designation { TenantId = tenantId, Code = "HRL", TitleEn = "Enterprise HR Lead", IsActive = true });
        if (departmentInOtherEntity)
        {
            var other = NewCompany(tenantId, "AE", "AED");
            var branch = new Branch { TenantId = tenantId, CompanyId = other.Id, Code = "DXB", NameEn = "Dubai", CountryCode = "AE", IsActive = true };
            db.Companies.Add(other);
            db.Branches.Add(branch);
            db.Departments.Add(new Department { TenantId = tenantId, Code = "OPS", NameEn = "Operations", BranchId = branch.Id, IsActive = true });
        }
        if (policy is not null)
            db.SystemSettings.Add(new SystemSetting
            {
                TenantId = tenantId, Category = "Recruitment", SettingKey = "OfferApprovalRequired",
                SettingValue = policy, DataType = "bool",
            });

        var hrRole = new Role { Id = Guid.NewGuid(), TenantId = tenantId, Name = "HR Manager", NormalizedName = "HR MANAGER", IsActive = true };
        db.Roles.Add(hrRole);
        var employeeRole = await db.Roles.SingleAsync(r => r.TenantId == tenantId && r.NormalizedName == "EMPLOYEE");
        var author = NewUser(tenantId, "author", hrRole);
        var checker = NewUser(tenantId, "checker", hrRole);
        var otherHr = NewUser(tenantId, "otherhr", hrRole);
        var staff = NewUser(tenantId, "staff", employeeRole);
        db.Users.AddRange(author, checker, otherHr, staff);

        var candidate = new Candidate
        {
            TenantId = tenantId, CompanyId = companyId, FirstName = "Noura", LastName = "Al Mansoori",
            Email = $"noura-{Guid.NewGuid():N}@example.test", Phone = "+966500000001", Nationality = "SA", Status = "Active",
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
        await db.SaveChangesAsync();
        return new World(tenantId, application.Id, companyId, author.Id, checker.Id, otherHr.Id, staff.Id);
    }

    private static Company NewCompany(Guid tenantId, string country, string currency) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = $"{country} Co {Guid.NewGuid():N}",
        CountryCode = country, Jurisdiction = country == "SA" ? "KSA-mainland" : "UAE-mainland",
        RegistrationNumber = $"REG-{Guid.NewGuid():N}", DefaultCurrency = currency, IsActive = true,
    };

    private static User NewUser(Guid tenantId, string name, Role role)
    {
        var email = $"{name}-{Guid.NewGuid():N}@example.test";
        var user = new User
        {
            TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = $"Test {name}",
            Status = "Active", IsActive = true,
        };
        user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        return user;
    }

    private async Task<Guid> GenerateAsync(World w, Guid userId, string department = "People")
    {
        await using var db = _fixture.CreateDb();
        var created = await Applications(db, w.TenantId, userId).GenerateOffer(
            w.ApplicationId,
            new GenerateOfferRequest(department, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)), 20_000, 5_000, 1_500, 0, 3),
            CancellationToken.None);
        return ((OfferLetter)created.Should().BeOfType<CreatedResult>().Subject.Value!).Id;
    }

    private async Task<Guid> AddStepAsync(World w, Guid offerId, Guid approver)
    {
        await using var db = _fixture.CreateDb();
        var added = await Offers(db, w.TenantId, w.Author).AddApproval(
            offerId, new AddOfferApprovalRequest("Checker", approver, "HR Manager"), CancellationToken.None);
        return ((OfferApproval)added.Should().BeOfType<OkObjectResult>().Subject.Value!).Id;
    }

    private async Task<string> StatusOf(Guid offerId)
    {
        await using var db = _fixture.CreateDb();
        return (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == offerId)).Status;
    }

    private static string? ErrorOf(ObjectResult result) =>
        JsonSerializer.SerializeToElement(result.Value).TryGetProperty("error", out var e) ? e.GetString() : null;

    // ── Controllers ───────────────────────────────────────────────────────────

    private static ApplicationsController Applications(ZayraDbContext db, Guid tenantId, Guid userId) =>
        WithPrincipal(new ApplicationsController(db, new RecruitmentService(db), new MakerCheckerNullNotifications()), tenantId, userId);

    private static OffersController Offers(ZayraDbContext db, Guid tenantId, Guid userId) =>
        WithPrincipal(new OffersController(db, new MakerCheckerNullLetters(), new RecruitmentService(db)), tenantId, userId);

    private static EmployeesController Employees(ZayraDbContext db, Guid tenantId, Guid userId) =>
        WithPrincipal(new EmployeesController(
            db, new Pbkdf2PasswordHasher(), new AuditService(db), new NullDocumentStorage(), new MakerCheckerNullNotifications(),
            new MakerCheckerHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new MakerCheckerNullLetters()),
            tenantId, userId);

    private static T WithPrincipal<T>(T controller, Guid tenantId, Guid userId) where T : ControllerBase
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "HR"),
            new Claim(ClaimTypes.Role, "HR Manager"),
            new Claim(EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }
}

file sealed class MakerCheckerNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName,
        string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class MakerCheckerNullLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class MakerCheckerHijri : IHijriDateService
{
    public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}
