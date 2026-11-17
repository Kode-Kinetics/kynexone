using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Shared seeding for the hiring-fix integration tests: a tenant with an opening, a candidate, an application at the Offer stage and a Sent offer.</summary>
public abstract class HiringPostgresTestBase(PostgresFixture fixture)
{
    protected PostgresFixture Fixture { get; } = fixture;

    protected sealed record Seeded(Guid TenantId, Guid? CompanyId, Guid OpeningId, Guid ApplicationId, Guid OfferId, Guid CandidateId);

    protected async Task<Seeded> SeedAsync(Action<OfferLetter>? offer = null, Action<JobOpening>? opening = null,
        Action<Candidate>? candidate = null, Guid? companyId = null, Action<ZayraDbContext, Guid>? extra = null, int headCount = 1)
    {
        await using var db = Fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        if (companyId is Guid company)
            db.Companies.Add(new Company { Id = company, TenantId = tenantId, LegalNameEn = "Hiring Co", CountryCode = "SA", IsActive = true });
        var cand = new Candidate
        {
            TenantId = tenantId, CompanyId = companyId, FirstName = "Noura", LastName = "Al Mansoori",
            Email = $"noura-{Guid.NewGuid():N}@example.test", Phone = "+966500000001", Nationality = "SA", Status = "Active",
        };
        candidate?.Invoke(cand);
        var open = new JobOpening
        {
            TenantId = tenantId, CompanyId = companyId, JobCode = $"JOB-{Guid.NewGuid():N}", Title = "Enterprise HR Lead",
            DepartmentName = "People", HeadCount = headCount, Status = "InProgress",
        };
        opening?.Invoke(open);
        var app = new JobApplication
        {
            TenantId = tenantId, CompanyId = companyId, JobOpeningId = open.Id, JobTitle = open.Title, CandidateId = cand.Id,
            CandidateName = "Noura Al Mansoori", CandidateEmail = cand.Email, Stage = "Offer", StageOrder = 5, Status = "Active",
        };
        var letter = new OfferLetter
        {
            TenantId = tenantId, CompanyId = companyId, ApplicationId = app.Id, CandidateName = "Noura Al Mansoori",
            OfferedJobTitle = "Enterprise HR Lead", OfferedDepartment = "People", StartDate = new DateOnly(2027, 3, 1),
            BasicSalary = 20_000, HousingAllowance = 5_000, TransportAllowance = 1_500, GrossSalary = 26_500,
            ContentHtml = "<p>Offer</p>", Status = "Sent", SentAtUtc = DateTime.UtcNow,
        };
        offer?.Invoke(letter);
        db.Candidates.Add(cand);
        db.JobOpenings.Add(open);
        db.JobApplications.Add(app);
        db.OfferLetters.Add(letter);
        extra?.Invoke(db, tenantId);
        await db.SaveChangesAsync();
        return new Seeded(tenantId, companyId, open.Id, app.Id, letter.Id, cand.Id);
    }

    protected async Task<OfferAcceptanceResult> AcceptAsync(Seeded s, Guid? by = null)
    {
        await using var db = Fixture.CreateDb();
        return await new RecruitmentService(db).AcceptOfferAsync(s.TenantId, s.OfferId, by ?? Guid.NewGuid(), "HR Maker", CancellationToken.None);
    }

    protected async Task<EmployeeDraft> DraftOfAsync(Guid draftId)
    {
        await using var db = Fixture.CreateDb();
        return await db.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId);
    }

}

internal sealed class RecordingNotifications : INotificationService
{
    public List<(Guid? UserId, string Title, string Message, string EntityName, string? EntityId)> Calls { get; } = [];

    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName,
        string? entityId, CancellationToken cancellationToken)
    {
        Calls.Add((userId, title, message, entityName, entityId));
        return Task.CompletedTask;
    }

    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}
