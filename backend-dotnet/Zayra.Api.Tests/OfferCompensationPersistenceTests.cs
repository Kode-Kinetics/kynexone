using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferCompensationPersistenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ThreeDecimalOfferAndApplicationProjectionsRoundTripWithoutRounding()
    {
        await using var db = fixture.CreateDb(); var tid = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tid, DefaultCurrency = "KWD" }; db.Companies.Add(company);
        db.OfferCompanyPolicies.Add(new() { TenantId = tid, CompanyId = company.Id, Precision = 3 });
        await db.SaveChangesAsync();
        var snapshot = await OfferComponentOptions.CalculateAsync(db, tid, company.Id, new(2027, 1, 1),
            [OfferCompensationTests.Cash("BASIC", 1000) with { LabelEn = "Basic salary" }, OfferCompensationTests.Cash("EXTRA", 0.001m)], null, null, null, null, default);
        var app = new JobApplication { TenantId = tid, CompanyId = company.Id, JobOpeningId = Guid.NewGuid(), CandidateId = Guid.NewGuid(), OfferedSalary = snapshot.MonthlyCash };
        var offer = new OfferLetter { TenantId = tid, CompanyId = company.Id, ApplicationId = app.Id, StartDate = new(2027, 1, 1) };
        OfferCompensation.ApplySnapshot(offer, snapshot); db.JobApplications.Add(app); db.OfferLetters.Add(offer); await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await db.OfferLetters.SingleAsync(o => o.Id == offer.Id)).GrossSalary.Should().Be(1000.001m);
        (await db.JobApplications.SingleAsync(a => a.Id == app.Id)).OfferedSalary.Should().Be(1000.001m);
    }

    [Fact]
    public async Task PolicyVersionRejectsConcurrentUpdateAndCompanyKeyRejectsDuplicates()
    {
        await using var db = fixture.CreateDb(); var tid = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tid }; db.Companies.Add(company);
        var policy = new OfferCompanyPolicy { TenantId = tid, CompanyId = company.Id }; db.OfferCompanyPolicies.Add(policy); await db.SaveChangesAsync();
        await using var other = fixture.CreateDb(); var stale = await other.OfferCompanyPolicies.SingleAsync(p => p.Id == policy.Id);
        policy.Precision = 3; policy.Version++; await db.SaveChangesAsync();
        stale.Precision = 0; stale.Version++;
        await FluentActions.Awaiting(() => other.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>();
        db.OfferCompanyPolicies.Add(new() { TenantId = tid, CompanyId = company.Id });
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task PolicyCannotReferenceCompanyFromAnotherTenant()
    {
        await using var db = fixture.CreateDb(); var firstTenant = await PostgresFixture.SeedMinimalTenant(db); var secondTenant = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = firstTenant }; db.Companies.Add(company); await db.SaveChangesAsync();
        db.OfferCompanyPolicies.Add(new() { TenantId = secondTenant, CompanyId = company.Id });
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }
}
