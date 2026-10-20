using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferRevisionPostgresTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("Queued")]
    [InlineData("Sending")]
    [InlineData("Unknown")]
    public async Task ReservationBlocksAcceptanceWithoutConsumingHeadcount(string reservationState)
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using (var db = fixture.CreateDb())
        {
            var app = await db.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId);
            app.OfferReservationId = Guid.NewGuid(); app.OfferReservationState = reservationState;
            app.OfferReservationOfferId = seed.OfferId; app.OfferReservationVersion = app.Version;
            await db.SaveChangesAsync();
        }
        await using var acceptDb = fixture.CreateDb();
        var result = await new RecruitmentService(acceptDb).AcceptOfferAsync(seed.Actor.TenantId,
            seed.OfferId, seed.Actor.UserId, seed.Actor.Name);
        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("unresolved offer delivery");
        (await acceptDb.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).Status.Should().Be("Sent");
        (await acceptDb.JobOpenings.AsNoTracking().SingleAsync(o => o.Id == seed.OpeningId)).FilledCount.Should().Be(0);
        (await acceptDb.EmployeeDrafts.CountAsync(d => d.TenantId == seed.Actor.TenantId)).Should().Be(0);
    }

    [Fact]
    public async Task ManagedExpiredOfferCannotBeAccepted()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using (var db = fixture.CreateDb())
        {
            var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
            var series = new OfferSeries { TenantId = seed.Actor.TenantId, CompanyId = seed.Actor.CompanyId,
                ApplicationId = seed.ApplicationId };
            db.OfferSeries.Add(series); offer.SeriesId = series.Id; offer.RevisionNumber = 1;
            var artifact = new OfferArtifact { TenantId = seed.Actor.TenantId, CompanyId = seed.Actor.CompanyId,
                ApplicationId = seed.ApplicationId, OfferId = offer.Id, State = "Finalized", StorageKey = $"{seed.Actor.TenantId:N}/expiry-test.pdf",
                Sha256 = new string('a', 64), SizeBytes = 1, FinalizedAtUtc = DateTime.UtcNow.AddDays(-2) };
            db.OfferArtifacts.Add(artifact);
            offer.FrozenAtUtc = DateTime.UtcNow.AddDays(-2); offer.ArtifactId = artifact.Id;
            (await db.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId)).CurrentIssuedOfferId = offer.Id;
            offer.ResponseDeadline = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }
        await using var acceptDb = fixture.CreateDb();
        var result = await new RecruitmentService(acceptDb).AcceptOfferAsync(seed.Actor.TenantId,
            seed.OfferId, seed.Actor.UserId, seed.Actor.Name);
        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("response deadline has passed");
        (await acceptDb.JobApplications.AsNoTracking().SingleAsync(a => a.Id == seed.ApplicationId)).Status.Should().Be("Active");
    }
}

internal sealed record OfferLifecycleSeed(OfferActor Actor, Guid ApplicationId, Guid OfferId, Guid OpeningId);
internal static class OfferLifecycleTestData
{
    public static async Task<OfferLifecycleSeed> SeedAsync(PostgresFixture fixture)
    {
        await using var db = fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tenantId, DefaultCurrency = "SAR" };
        var opening = new JobOpening { TenantId = tenantId, Title = "Engineer", HeadCount = 3 };
        var candidate = new Candidate { TenantId = tenantId, CompanyId = company.Id,
            FirstName = "Test", LastName = "Candidate", Email = "candidate@example.invalid" };
        var application = new JobApplication { TenantId = tenantId, CompanyId = company.Id,
            CandidateId = candidate.Id, CandidateName = "Test Candidate", CandidateEmail = candidate.Email,
            JobOpeningId = opening.Id, JobTitle = opening.Title, Stage = "Offer", StageOrder = 5 };
        var offer = new OfferLetter { TenantId = tenantId, CompanyId = company.Id, ApplicationId = application.Id,
            CandidateName = application.CandidateName, OfferedJobTitle = opening.Title, OfferedDepartment = "Engineering",
            StartDate = new(2027, 1, 1), BasicSalary = 1000, GrossSalary = 1000, Status = "Sent",
            SentAtUtc = DateTime.UtcNow, ResponseDeadline = DateTime.UtcNow.AddDays(7) };
        db.Companies.Add(company); db.JobOpenings.Add(opening); db.Candidates.Add(candidate);
        db.JobApplications.Add(application); db.OfferLetters.Add(offer); await db.SaveChangesAsync();
        return new(new(tenantId, company.Id, Guid.NewGuid(), "Test recruiter"), application.Id, offer.Id, opening.Id);
    }
}
