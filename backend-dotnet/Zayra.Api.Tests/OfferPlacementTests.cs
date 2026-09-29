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
/// F08 — an offer's department and job title were free text, and nothing checked them until HR
/// approved the accepted offer's employee draft. Activation resolves both against the organisation's
/// department and designation records and refused the hire with a 422 when they did not match, long
/// after the candidate had accepted. The offer is now resolved against those records when it is
/// created, and stores the records' own spelling, so activation matches what the offer promised.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OfferPlacementTests
{
    private readonly PostgresFixture _fixture;

    public OfferPlacementTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GeneratingAnOffer_ForADepartmentTheOrganisationDoesNotHave_IsRefusedAtOfferTime()
    {
        var seeded = await SeedAsync();

        await using (var db = _fixture.CreateDb())
        {
            var result = await Applications(db, seeded.TenantId, Guid.NewGuid()).GenerateOffer(
                seeded.ApplicationId, Request("Growth Hacking"), CancellationToken.None);
            var refused = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
            var body = JsonSerializer.Serialize(refused.Value);
            body.Should().Contain("offer_placement_unresolved").And.Contain("Growth Hacking");
        }

        await using var verify = _fixture.CreateDb();
        (await verify.OfferLetters.CountAsync(x => x.ApplicationId == seeded.ApplicationId))
            .Should().Be(0, "an offer the organisation cannot place is not created");
    }

    [Fact]
    public async Task GeneratingAnOffer_StoresTheOrganisationsOwnDepartmentAndDesignation()
    {
        // The opening advertises "Senior HR Lead - Riyadh" but is filed against the HR Lead
        // designation. The offer used the advert title, which no designation matches.
        var seeded = await SeedAsync(advertTitle: "Senior HR Lead - Riyadh", openingDesignation: true);

        await using (var db = _fixture.CreateDb())
            (await Applications(db, seeded.TenantId, Guid.NewGuid()).GenerateOffer(
                    seeded.ApplicationId, Request("ppl"), CancellationToken.None))
                .Should().BeOfType<CreatedResult>();

        await using var verify = _fixture.CreateDb();
        var offer = await verify.OfferLetters.AsNoTracking().SingleAsync(x => x.ApplicationId == seeded.ApplicationId);
        offer.OfferedDepartment.Should().Be("People", "the department record's own name, not what was typed");
        offer.OfferedJobTitle.Should().Be("HR Lead", "the opening's designation, not its advert title");
    }

    [Fact]
    public async Task CreatingAnOffer_WithAJobTitleThatIsNotADesignation_IsRefused()
    {
        var seeded = await SeedAsync();

        await using (var db = _fixture.CreateDb())
        {
            var result = await Offers(db, seeded.TenantId).Create(new CreateOfferRequest(
                seeded.ApplicationId, "Chief Vibes Officer", "People",
                DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)), 18_000, 4_000, 1_000, 0, 3, null, null),
                CancellationToken.None);
            var refused = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
            JsonSerializer.Serialize(refused.Value).Should().Contain("Chief Vibes Officer");
        }

        await using var verify = _fixture.CreateDb();
        (await verify.OfferLetters.CountAsync(x => x.ApplicationId == seeded.ApplicationId)).Should().Be(0);
    }

    [Fact]
    public async Task AnAcceptedOffer_ActivatesWithoutAPlacementError()
    {
        // The whole chain: generate, send, accept, approve. On the base branch this ended in a 422 at
        // approval, because the offer carried the advert title as its job title.
        var seeded = await SeedAsync(advertTitle: "Senior HR Lead - Riyadh", openingDesignation: true);

        await using (var db = _fixture.CreateDb())
            (await Applications(db, seeded.TenantId, Guid.NewGuid()).GenerateOffer(
                    seeded.ApplicationId, Request("People"), CancellationToken.None))
                .Should().BeOfType<CreatedResult>();
        Guid offerId;
        await using (var db = _fixture.CreateDb())
            offerId = (await db.OfferLetters.AsNoTracking().SingleAsync(x => x.ApplicationId == seeded.ApplicationId)).Id;
        await using (var db = _fixture.CreateDb())
            (await Applications(db, seeded.TenantId, Guid.NewGuid()).SendOffer(offerId, CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();
        await using (var db = _fixture.CreateDb())
            (await Applications(db, seeded.TenantId, Guid.NewGuid()).AcceptOffer(offerId, CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();

        Guid draftId;
        await using (var db = _fixture.CreateDb())
            draftId = (await db.JobApplications.AsNoTracking().SingleAsync(x => x.Id == seeded.ApplicationId)).OnboardingDraftId!.Value;
        await using (var db = _fixture.CreateDb())
        {
            var approval = await Employees(db, seeded.TenantId, Guid.NewGuid()).ApproveDraft(draftId, CancellationToken.None);
            approval.Result.Should().BeOfType<OkObjectResult>(
                "an offer the system accepted must not fail at activation on a placement it could have checked: {0}",
                JsonSerializer.Serialize((approval.Result as ObjectResult)?.Value));
        }

        await using var verify = _fixture.CreateDb();
        // IgnoreQueryFilters is intentional: the test reads the one employee this tenant owns.
        var employee = await verify.Employees.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.TenantId == seeded.TenantId);
        employee.DepartmentId.Should().Be(seeded.DepartmentId);
        employee.DesignationId.Should().Be(seeded.DesignationId);
    }

    [Fact]
    public async Task APickedDepartmentAndDesignation_AreStoredByTheirRecordsNames()
    {
        var seeded = await SeedAsync(advertTitle: "Anything the advert says");

        await using (var db = _fixture.CreateDb())
            (await Applications(db, seeded.TenantId, Guid.NewGuid()).GenerateOffer(seeded.ApplicationId,
                    Request("") with { DepartmentId = seeded.DepartmentId, DesignationId = seeded.DesignationId },
                    CancellationToken.None))
                .Should().BeOfType<CreatedResult>();

        await using var verify = _fixture.CreateDb();
        var offer = await verify.OfferLetters.AsNoTracking().SingleAsync(x => x.ApplicationId == seeded.ApplicationId);
        offer.OfferedDepartment.Should().Be("People");
        offer.OfferedJobTitle.Should().Be("HR Lead");
    }

    [Fact]
    public async Task ARecordFromAnotherTenantOrAnInactiveOne_CannotBePicked()
    {
        var seeded = await SeedAsync();
        var other = await SeedAsync();
        Guid inactiveDesignation;
        await using (var db = _fixture.CreateDb())
        {
            var retired = new Designation { TenantId = seeded.TenantId, Code = "OLD", TitleEn = "Retired Title", IsActive = false };
            db.Designations.Add(retired);
            await db.SaveChangesAsync();
            inactiveDesignation = retired.Id;
        }

        await using (var db = _fixture.CreateDb())
            (await Applications(db, seeded.TenantId, Guid.NewGuid()).GenerateOffer(seeded.ApplicationId,
                    Request("") with { DepartmentId = other.DepartmentId }, CancellationToken.None))
                .Should().BeOfType<UnprocessableEntityObjectResult>("another tenant's department is not a record here");
        await using (var db = _fixture.CreateDb())
            (await Applications(db, seeded.TenantId, Guid.NewGuid()).GenerateOffer(seeded.ApplicationId,
                    Request("People") with { DesignationId = inactiveDesignation }, CancellationToken.None))
                .Should().BeOfType<UnprocessableEntityObjectResult>("an inactive designation would not resolve at activation");

        OfferPlacementOptions options;
        await using (var db = _fixture.CreateDb())
            options = (OfferPlacementOptions)(await Offers(db, seeded.TenantId).PlacementOptions(CancellationToken.None))
                .Should().BeOfType<OkObjectResult>().Subject.Value!;
        options.Departments.Select(d => d.Id).Should().Equal(seeded.DepartmentId);
        options.Designations.Select(d => d.Id).Should().Equal(seeded.DesignationId);
    }

    // ── Seeding and controllers ──────────────────────────────────────────────

    private static GenerateOfferRequest Request(string department) =>
        new(department, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)), 20_000, 5_000, 1_500, 0, 3);

    private async Task<Seeded> SeedAsync(string advertTitle = "HR Lead", bool openingDesignation = false)
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var department = new Department { TenantId = tenantId, Code = "PPL", NameEn = "People", IsActive = true };
        var designation = new Designation { TenantId = tenantId, Code = "HRL", TitleEn = "HR Lead", IsActive = true };
        var candidate = new Candidate
        {
            TenantId = tenantId, FirstName = "Noura", LastName = "Al Mansoori",
            Email = $"noura-{Guid.NewGuid():N}@example.test", Nationality = "SA", Status = "Active",
        };
        var opening = new JobOpening
        {
            TenantId = tenantId, JobCode = $"JOB-{Guid.NewGuid():N}", Title = advertTitle,
            DepartmentId = department.Id, DepartmentName = department.NameEn,
            DesignationId = openingDesignation ? designation.Id : null,
            DesignationTitle = openingDesignation ? designation.TitleEn : string.Empty,
            HeadCount = 2, Status = "InProgress",
        };
        var application = new JobApplication
        {
            TenantId = tenantId, JobOpeningId = opening.Id, JobTitle = opening.Title,
            CandidateId = candidate.Id, CandidateName = "Noura Al Mansoori", CandidateEmail = candidate.Email,
            Stage = "Offer", StageOrder = 5, Status = "Active",
        };
        db.AddRange(department, designation, candidate, opening, application);
        await db.SaveChangesAsync();
        return new Seeded(tenantId, application.Id, department.Id, designation.Id);
    }

    private static ApplicationsController Applications(ZayraDbContext db, Guid tenantId, Guid userId) =>
        WithPrincipal(new ApplicationsController(db, new RecruitmentService(db), new PlacementNullNotifications()), tenantId, userId);

    private static OffersController Offers(ZayraDbContext db, Guid tenantId) =>
        WithPrincipal(new OffersController(db, new PlacementNullLetters(), new RecruitmentService(db)), tenantId, Guid.NewGuid());

    private static EmployeesController Employees(ZayraDbContext db, Guid tenantId, Guid userId) =>
        WithPrincipal(new EmployeesController(
            db, new Pbkdf2PasswordHasher(), new AuditService(db), new NullDocumentStorage(), new PlacementNullNotifications(),
            new PlacementHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new PlacementNullLetters()),
            tenantId, userId);

    private static T WithPrincipal<T>(T controller, Guid tenantId, Guid userId) where T : ControllerBase
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Placement HR Manager"),
            new Claim(ClaimTypes.Role, "HR Manager"),
            new Claim(EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }

    private sealed record Seeded(Guid TenantId, Guid ApplicationId, Guid DepartmentId, Guid DesignationId);
}

file sealed class PlacementNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName,
        string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class PlacementNullLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class PlacementHijri : IHijriDateService
{
    public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}
