using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// P0-1: recruitment PII exposure. Proves that Candidate/JobApplication/OfferLetter are now
/// company-scoped (invisible cross-company to scoped users), that reads are role-gated, that
/// creates stamp CompanyId from their parent, and that stored offer HTML is encoded on write.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class RecruitmentCompanyScopeTests
{
    private readonly PostgresFixture _fx;
    public RecruitmentCompanyScopeTests(PostgresFixture fx) => _fx = fx;

    // ── (1) Cross-company read returns none (not an exception) ──────────────────

    [Fact]
    public async Task ScopedUser_CannotReadOtherCompanyRecruitmentData()
    {
        await using var seedDb = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seedDb);
        var companyA = MakeCompany(tenantId, "Rec-Alpha");
        var companyB = MakeCompany(tenantId, "Rec-Beta");
        seedDb.Companies.AddRange(companyA, companyB);

        var candA = MakeCandidate(tenantId, companyA.Id, "a@x.com");
        var openingA = MakeOpening(tenantId);
        var appA = MakeApplication(tenantId, companyA.Id, openingA.Id, candA.Id);
        var offerA = MakeOffer(tenantId, companyA.Id, appA.Id);
        seedDb.Candidates.Add(candA);
        seedDb.JobOpenings.Add(openingA);
        seedDb.JobApplications.Add(appA);
        seedDb.OfferLetters.Add(offerA);
        await seedDb.SaveChangesAsync();

        var accessor = new RcSwitchableAccessor();
        await using var db = _fx.CreateDbWithAccessor(accessor);

        // User scoped to Company B only — must see zero of Company A's recruitment rows.
        accessor.HttpContext = ScopedContext(tenantId, companyB.Id);
        (await db.Candidates.Where(c => c.TenantId == tenantId).ToListAsync()).Should().BeEmpty();
        (await db.JobApplications.Where(a => a.TenantId == tenantId).ToListAsync()).Should().BeEmpty();
        (await db.OfferLetters.Where(o => o.TenantId == tenantId).ToListAsync()).Should().BeEmpty();

        // Same DbContext, switched to Company A scope → the rows are now visible (proves it was
        // a scope filter, not a data problem, and no stale pooled scope state).
        accessor.HttpContext = ScopedContext(tenantId, companyA.Id);
        (await db.Candidates.Where(c => c.TenantId == tenantId).ToListAsync()).Should().ContainSingle();
        (await db.OfferLetters.Where(o => o.TenantId == tenantId).ToListAsync()).Should().ContainSingle();
    }

    // ── (2) Read endpoints are role-gated (ESS/no-HR-role blocked) ──────────────

    [Fact]
    public void RecruitmentReadEndpoints_AreRoleGated_NotJustAuthenticated()
    {
        // The [Authorize(Roles=...)] attribute is enforced by MVC, not the method body, so we
        // assert its presence: every read that exposes candidate PII must carry an HR role gate.
        AssertRoleGated(typeof(ApplicationsController), nameof(ApplicationsController.List));
        AssertRoleGated(typeof(ApplicationsController), nameof(ApplicationsController.Kanban));
        AssertRoleGated(typeof(ApplicationsController), nameof(ApplicationsController.Get));
        AssertRoleGated(typeof(ApplicationsController), nameof(ApplicationsController.GetOfferHtml));
        AssertRoleGated(typeof(CandidatesController), nameof(CandidatesController.List));
        AssertRoleGated(typeof(CandidatesController), nameof(CandidatesController.Get));
        AssertRoleGated(typeof(OffersController), nameof(OffersController.List));
        AssertRoleGated(typeof(OffersController), nameof(OffersController.Get));
        // Sibling controllers flagged in the security review (B1/B2).
        AssertRoleGated(typeof(InterviewsController), nameof(InterviewsController.List));
        AssertRoleGated(typeof(InterviewsController), nameof(InterviewsController.GetFeedback));
        AssertRoleGated(typeof(InterviewsController), nameof(InterviewsController.SubmitFeedback));
        AssertRoleGated(typeof(AssessmentsController), nameof(AssessmentsController.List));
        AssertRoleGated(typeof(OnboardingController), nameof(OnboardingController.ListTasks));
        AssertRoleGated(typeof(OnboardingController), nameof(OnboardingController.Summary));
        AssertRoleGated(typeof(OnboardingController), nameof(OnboardingController.UpdateTaskStatus));
        AssertRoleGated(typeof(RecruitmentReportsController), nameof(RecruitmentReportsController.PipelineSummary));
    }

    private static void AssertRoleGated(Type controller, string method)
    {
        var mi = controller.GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!;
        var attr = mi.GetCustomAttribute<AuthorizeAttribute>();
        attr.Should().NotBeNull($"{controller.Name}.{method} must carry a method-level [Authorize(Roles=...)] gate");
        attr!.Roles.Should().NotBeNullOrWhiteSpace($"{controller.Name}.{method} must restrict by role, not just authenticate");
        attr.Roles!.Should().Contain("HR Manager", $"{controller.Name}.{method} role gate must include the HR role set");
    }

    // ── (3) Create stamps CompanyId from the parent (scoped user) ───────────────

    [Fact]
    public async Task Apply_StampsApplicationCompanyId_FromParentCandidate_UnderCompanyScope()
    {
        await using var seedDb = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seedDb);
        var companyA = MakeCompany(tenantId, "Stamp-Alpha");
        seedDb.Companies.Add(companyA);
        var cand = MakeCandidate(tenantId, companyA.Id, "stamp@x.com");
        var opening = MakeOpening(tenantId);
        seedDb.Candidates.Add(cand);
        seedDb.JobOpenings.Add(opening);
        await seedDb.SaveChangesAsync();

        var accessor = new RcSwitchableAccessor { HttpContext = ScopedContext(tenantId, companyA.Id) };
        await using var db = _fx.CreateDbWithAccessor(accessor);

        var ctrl = new ApplicationsController(db, new RecruitmentService(db), new RcNullNotifications());
        ctrl.ControllerContext = new ControllerContext { HttpContext = accessor.HttpContext! };

        var result = await ctrl.Apply(new ApplyRequest(opening.Id, cand.Id, "note"), CancellationToken.None);
        result.Should().BeOfType<CreatedResult>();

        var saved = await db.JobApplications.IgnoreQueryFilters()
            .FirstAsync(a => a.TenantId == tenantId && a.CandidateId == cand.Id);
        saved.CompanyId.Should().Be(companyA.Id, "the application must inherit the parent candidate's company");
    }

    // ── (4) OffersController.Create encodes stored ContentHtml (stored-XSS defense) ──

    [Fact]
    public async Task OffersCreate_EncodesStoredContentHtml()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = MakeCompany(tenantId, "Enc-Co");
        var opening = MakeOpening(tenantId);
        var cand = MakeCandidate(tenantId, company.Id, "enc@x.com");
        var app = MakeApplication(tenantId, company.Id, opening.Id, cand.Id);
        db.Companies.Add(company); db.JobOpenings.Add(opening); db.Candidates.Add(cand); db.JobApplications.Add(app);
        await db.SaveChangesAsync();

        var ctrl = new OffersController(db, new RcNullLetterService(), new RecruitmentService(db));
        ctrl.ControllerContext = new ControllerContext { HttpContext = ContextWithRole(tenantId, "Admin") };

        var req = new CreateOfferRequest(
            app.Id, "Engineer", "Tech", new DateOnly(2026, 9, 1),
            10000, 2000, 1000, 0, 3,
            "<script>alert(document.cookie)</script>", null);
        await ctrl.Create(req, CancellationToken.None);

        var stored = await db.OfferLetters.IgnoreQueryFilters().FirstAsync(o => o.ApplicationId == app.Id);
        stored.ContentHtml.Should().NotContain("<script>", "stored offer HTML must be encoded on write");
        stored.ContentHtml.Should().Contain("&lt;script&gt;");
    }

    // ── (5) RecruitmentService offer HTML encodes interpolated PII fields ────────

    [Fact]
    public void GenerateOfferLetterHtml_EncodesInjectedFields()
    {
        var svc = new RecruitmentService(_fx.CreateDb());
        var data = new OfferLetterTemplateData(
            "<script>alert(1)</script>", "Engineer<img src=x onerror=alert(2)>", "R&D",
            new DateOnly(2026, 9, 1), 10000, 2000, 1000, 0, 12000, 3);

        var html = svc.GenerateOfferLetterHtml(data);

        html.Should().NotContain("<script>alert(1)</script>", "candidate name must be HTML-encoded");
        html.Should().Contain("&lt;script&gt;");
        html.Should().NotContain("<img", "job title must be HTML-encoded so the tag cannot execute");
        html.Should().Contain("&lt;img", "the injected tag must appear only as inert encoded text");
    }

    // ── (6) GetOfferHtml forces attachment disposition (no inline render on SPA origin) ──

    [Fact]
    public async Task GetOfferHtml_SetsAttachmentDisposition()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = MakeCompany(tenantId, "Disp-Co");
        var opening = MakeOpening(tenantId);
        var cand = MakeCandidate(tenantId, company.Id, "disp@x.com");
        var app = MakeApplication(tenantId, company.Id, opening.Id, cand.Id);
        var offer = MakeOffer(tenantId, company.Id, app.Id);
        db.Companies.Add(company); db.JobOpenings.Add(opening); db.Candidates.Add(cand);
        db.JobApplications.Add(app); db.OfferLetters.Add(offer);
        await db.SaveChangesAsync();

        var ctrl = new ApplicationsController(db, new RecruitmentService(db), new RcNullNotifications());
        ctrl.ControllerContext = new ControllerContext { HttpContext = ContextWithRole(tenantId, "HR Manager") };

        await ctrl.GetOfferHtml(offer.Id, CancellationToken.None);
        ctrl.Response.Headers["Content-Disposition"].ToString().Should().StartWith("attachment");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static HttpContext ScopedContext(Guid tenantId, Guid companyId)
    {
        var accessJson = JsonSerializer.Serialize(new { c = companyId, r = "Viewer" });
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("entity_access", accessJson),
        }, "Test"));
        return ctx;
    }

    private static HttpContext ContextWithRole(Guid tenantId, string role)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, role),
        }, "Test"));
        return ctx;
    }

    private static Company MakeCompany(Guid tenantId, string name) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = name,
        CountryCode = "SAU", Jurisdiction = "KSA-mainland",
        RegistrationNumber = $"REG-{Guid.NewGuid():N}", DefaultCurrency = "SAR",
        IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static Candidate MakeCandidate(Guid tenantId, Guid? companyId, string email) => new()
    {
        TenantId = tenantId, CompanyId = companyId,
        FirstName = "Test", LastName = "Candidate", Email = email,
    };

    private static JobOpening MakeOpening(Guid tenantId) => new()
    {
        TenantId = tenantId, JobCode = $"JOB-{Guid.NewGuid():N}", Title = "Engineer", Status = "Open", HeadCount = 5,
    };

    private static JobApplication MakeApplication(Guid tenantId, Guid? companyId, Guid openingId, Guid candidateId) => new()
    {
        TenantId = tenantId, CompanyId = companyId, JobOpeningId = openingId, JobTitle = "Engineer",
        CandidateId = candidateId, CandidateName = "Test Candidate", Stage = "Offer", StageOrder = 5, Status = "Active",
    };

    private static OfferLetter MakeOffer(Guid tenantId, Guid? companyId, Guid appId) => new()
    {
        TenantId = tenantId, CompanyId = companyId, ApplicationId = appId,
        CandidateName = "Test Candidate", OfferedJobTitle = "Engineer",
        BasicSalary = 10000, GrossSalary = 13000, ContentHtml = "<p>offer</p>", Status = "Draft",
    };
}

file sealed class RcSwitchableAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }
}

file sealed class RcNullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? id, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string tpl, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class RcNullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
