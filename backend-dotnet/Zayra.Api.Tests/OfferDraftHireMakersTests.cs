using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
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
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// F08 cross-stack wiring. An accepted offer's hire is made by two people on the recruitment side:
/// whoever SENT the offer and whoever recorded its acceptance (the draft's creator). The employee
/// module's maker-checker only knew the creator and the draft's editors, so the offer's sender could
/// activate the hire they had offered. The hire-makers seam now includes both.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OfferDraftHireMakersTests
{
    private readonly PostgresFixture _fixture;

    public OfferDraftHireMakersTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TheOffersSender_CannotActivateTheHire_ButAThirdHrUserCan()
    {
        var w = await SeedAcceptedOfferAsync();

        await using (var db = _fixture.CreateDb())
        {
            var bySender = await Employees(db, w.TenantId, w.Sender).ApproveDraft(w.DraftId, CancellationToken.None);
            (bySender.Result as IStatusCodeActionResult)?.StatusCode.Should().Be(StatusCodes.Status403Forbidden,
                "the person who sent the offer made this hire");
            JsonSerializer.Serialize((bySender.Result as ObjectResult)?.Value)
                .Should().Contain("A second user with employees.approve must activate this hire");
        }
        await using (var db = _fixture.CreateDb())
            (await CountEmployeesAsync(db, w.TenantId)).Should().Be(0);

        await using (var db = _fixture.CreateDb())
            (await Employees(db, w.TenantId, w.Checker).ApproveDraft(w.DraftId, CancellationToken.None))
                .Result.Should().BeOfType<OkObjectResult>("an HR user who neither sent nor accepted the offer activates it");
        await using var verify = _fixture.CreateDb();
        (await CountEmployeesAsync(verify, w.TenantId)).Should().Be(1);
    }

    [Fact]
    public async Task TheNewHiresList_TellsTheSenderTheyCannotApprove_AndTheCheckerThatTheyCan()
    {
        var w = await SeedAcceptedOfferAsync();

        await using (var db = _fixture.CreateDb())
        {
            var row = Ok(await Employees(db, w.TenantId, w.Sender).ListDrafts(cancellationToken: CancellationToken.None))
                .Items.Single(i => i.Id == w.DraftId);
            row.CanApprove.Should().BeFalse();
            row.ApproveBlockedReason.Should().Contain("A second user with employees.approve must activate this hire");
        }
        await using (var db = _fixture.CreateDb())
        {
            var row = Ok(await Employees(db, w.TenantId, w.Checker).ListDrafts(cancellationToken: CancellationToken.None))
                .Items.Single(i => i.Id == w.DraftId);
            row.CanApprove.Should().BeTrue();
            row.ApproveBlockedReason.Should().BeNull();
        }
    }

    [Fact]
    public async Task TheSenderCannotRejectTheHireEither()
    {
        var w = await SeedAcceptedOfferAsync();
        await using (var db = _fixture.CreateDb())
            ((await Employees(db, w.TenantId, w.Sender).RejectDraft(w.DraftId,
                    new EmployeeDraftDecisionRequest("Changed my mind about this hire"), CancellationToken.None))
                as IStatusCodeActionResult)!.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        await using var verify = _fixture.CreateDb();
        (await verify.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == w.DraftId)).Status.Should().Be("PendingHrApproval");
    }

    [Fact]
    public async Task TheProductionComposition_RegistersTheRecruitmentAwareHireMakers()
    {
        // The controller's fallback is not what production runs: Program.cs decides. Boot it.
        var connectionString = $"Data Source=file:hire-makers-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var host = new AuthorizationPipelineHost(connectionString);
        _ = host.CreateClient();
        using var scope = host.Services.CreateScope();
        var makers = scope.ServiceProvider.GetRequiredService<IDraftHireMakers>();
        makers.GetType().Name.Should().Be("OfferDraftHireMakers",
            "the registered implementation must add the offer's sender and acceptor to the draft's makers");
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    private sealed record World(Guid TenantId, Guid DraftId, Guid Sender, Guid Acceptor, Guid Checker);

    /// <summary>An offer generated and sent by one HR user, accepted by a second, and a third who did
    /// neither. The tenant has opted out of offer approval steps so the offer can be sent directly.</summary>
    private async Task<World> SeedAcceptedOfferAsync()
    {
        Guid tenantId, applicationId;
        var sender = Guid.NewGuid();
        var acceptor = Guid.NewGuid();
        var checker = Guid.NewGuid();
        await using (var db = _fixture.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            db.Departments.Add(new Department { TenantId = tenantId, Code = "PPL", NameEn = "People", IsActive = true });
            db.Designations.Add(new Designation { TenantId = tenantId, Code = "HRL", TitleEn = "Enterprise HR Lead", IsActive = true });
            db.SystemSettings.Add(new SystemSetting
            {
                TenantId = tenantId, Category = "Recruitment", SettingKey = OfferRules.PolicyKey, SettingValue = "false", DataType = "bool",
            });
            var candidate = new Candidate
            {
                TenantId = tenantId, FirstName = "Noura", LastName = "Al Mansoori",
                Email = $"noura-{Guid.NewGuid():N}@example.test", Nationality = "SA", Status = "Active",
            };
            var opening = new JobOpening
            {
                TenantId = tenantId, JobCode = $"JOB-{Guid.NewGuid():N}", Title = "Enterprise HR Lead",
                DepartmentName = "People", HeadCount = 2, Status = "InProgress",
            };
            var application = new JobApplication
            {
                TenantId = tenantId, JobOpeningId = opening.Id, JobTitle = opening.Title,
                CandidateId = candidate.Id, CandidateName = "Noura Al Mansoori", CandidateEmail = candidate.Email,
                Stage = "Offer", StageOrder = 5, Status = "Active",
            };
            db.AddRange(candidate, opening, application);
            await db.SaveChangesAsync();
            applicationId = application.Id;
        }

        Guid offerId;
        await using (var db = _fixture.CreateDb())
        {
            var created = await Applications(db, tenantId, sender).GenerateOffer(applicationId,
                new GenerateOfferRequest("People", DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)), 20_000, 5_000, 1_500, 0, 3),
                CancellationToken.None);
            offerId = ((OfferLetter)created.Should().BeOfType<CreatedResult>().Subject.Value!).Id;
        }
        await using (var db = _fixture.CreateDb())
            (await Applications(db, tenantId, sender).SendOffer(offerId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        await using (var db = _fixture.CreateDb())
            (await Applications(db, tenantId, acceptor).AcceptOffer(offerId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        Guid draftId;
        await using (var read = _fixture.CreateDb())
            draftId = (await read.JobApplications.AsNoTracking().SingleAsync(a => a.Id == applicationId)).OnboardingDraftId!.Value;
        // The draft must hold the identity its jurisdiction requires before approval — see
        // DraftStatutoryIdentity. These tests are about who may activate a hire, not about readiness.
        await using (var db = _fixture.CreateDb())
            await DraftStatutoryIdentity.ApplyAsync(db, draftId, "SA");
        return new World(tenantId, draftId, sender, acceptor, checker);
    }

    private static Task<int> CountEmployeesAsync(ZayraDbContext db, Guid tenantId) =>
        // IgnoreQueryFilters is intentional: the test reads every employee row the tenant owns.
        db.Employees.IgnoreQueryFilters().AsNoTracking().CountAsync(x => x.TenantId == tenantId);

    private static T Ok<T>(ActionResult<T> result) =>
        (T)result.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    // ── Controllers (constructed the way the other recruitment tests do: no DI, fallbacks apply) ──

    private static ApplicationsController Applications(ZayraDbContext db, Guid tenantId, Guid userId) =>
        WithPrincipal(new ApplicationsController(db, new RecruitmentService(db), new HireMakersNullNotifications()), tenantId, userId);

    private static EmployeesController Employees(ZayraDbContext db, Guid tenantId, Guid userId) =>
        WithPrincipal(new EmployeesController(
            db, new Pbkdf2PasswordHasher(), new AuditService(db), new NullDocumentStorage(), new HireMakersNullNotifications(),
            new HireMakersHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new HireMakersNullLetters()),
            tenantId, userId);

    private static T WithPrincipal<T>(T controller, Guid tenantId, Guid userId) where T : ControllerBase
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "HR"),
            new Claim(ClaimTypes.Role, "HR Manager"),
            new Claim("permission", "employees.write"),
            new Claim("permission", "employees.approve"),
            new Claim(EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }
}

file sealed class HireMakersNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName,
        string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class HireMakersNullLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class HireMakersHijri : IHijriDateService
{
    public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}
