using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Recruitment;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferCreationLifecycleTests(PostgresFixture fixture)
{
    private static OfferCreateInput Input() => new(new(2027, 2, 1), 3, "Engineer", "Engineering",
        ResponseDeadline: DateTime.UtcNow.AddDays(7), BasicSalary: 1000, HousingAllowance: 0,
        TransportAllowance: 0, OtherAllowances: 0, ExpectedApplicationVersion: 1);

    [Fact]
    public async Task ManagedCreationFreezesNoBytesAndRetainsIndependentEarlierOffer()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        db.Departments.Add(new() { TenantId = seed.Actor.TenantId, NameEn = "Engineering", Code = "ENG" });
        db.Designations.Add(new() { TenantId = seed.Actor.TenantId, TitleEn = "Engineer", Code = "ENG" }); await db.SaveChangesAsync();
        var created = await new OfferLifecycleService(db, OfferLifecycleServiceTests.Config()).CreateAsync(seed.Actor, seed.ApplicationId, Input());
        created.SeriesId.Should().NotBeNull(); created.RevisionNumber.Should().Be(1); created.CompanyId.Should().Be(seed.Actor.CompanyId);
        created.RecipientEmail.Should().Be("candidate@example.invalid"); created.CandidateName.Should().Be("Test Candidate"); created.CandidateId.Should().NotBeNull();
        created.ArtifactId.Should().BeNull(); created.FrozenAtUtc.Should().BeNull();
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).Status.Should().Be("Sent");
        (await db.OfferLetters.CountAsync(o => o.ApplicationId == seed.ApplicationId)).Should().Be(2);
        (await db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == seed.ApplicationId)).Version.Should().Be(2);
        (await OfferRules.AuthorAsync(db, seed.Actor.TenantId, created.Id, default)).Should().Be(seed.Actor.UserId);
    }

    [Theory]
    [InlineData("Reserved", "offer_issuance_in_flight")]
    [InlineData("Hired", "application_not_active")]
    [InlineData("Stale", "application_version_conflict")]
    public async Task CreationSharesApplicationEligibilityAndVersionBoundary(string state, string error)
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var app = await db.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId);
        if (state == "Reserved") { app.OfferReservationId = Guid.NewGuid(); app.OfferReservationOfferId = seed.OfferId; app.OfferReservationState = "Unknown"; app.OfferReservationVersion = app.Version; }
        if (state == "Hired") app.Status = "Hired";
        if (state == "Stale") app.Version++;
        await db.SaveChangesAsync();
        var refusal = await FluentActions.Awaiting(() => new OfferLifecycleService(db, OfferLifecycleServiceTests.Config())
            .CreateAsync(seed.Actor, seed.ApplicationId, Input())).Should().ThrowAsync<OfferLifecycleException>();
        refusal.Which.Code.Should().Be(error);
        (await db.OfferLetters.CountAsync(o => o.ApplicationId == seed.ApplicationId)).Should().Be(1);
    }
}
