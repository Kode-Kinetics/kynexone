using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferDraftLifecycleTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ExplicitLegacyAdoptionRecordsTheReviewerAsAuthorWhenNoHistoricalAuthorExists()
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture);
        await using var db = fixture.CreateDb();
        (await OfferRules.AuthorAsync(db, seed.Actor.TenantId, seed.OfferId, default)).Should().Be(seed.Actor.UserId);
    }

    private static OfferDraftInput Input(long version, long? policy = 0) => new(version, policy,
        new(2027, 2, 1), DateTime.UtcNow.AddDays(10), 3, "reviewed@example.invalid", "Engineer", "Engineering",
        BasicSalary: 1000, HousingAllowance: 200, TransportAllowance: 0, OtherAllowances: 0,
        AdoptLegacy: true, RecipientChangeReason: "Candidate confirmed email");

    [Fact]
    public async Task AnotherAuthorWhoChangesTermsCannotApproveTheirOwnChanges()
    {
        var seed = await DraftAsync(); await using var db = fixture.CreateDb();
        db.RecruitmentAuditLogs.Add(OfferRules.AuditRow(seed.Actor.TenantId, seed.OfferId, OfferRules.CreatedAction, seed.Actor.UserId, seed.Actor.Name));
        var role = new Zayra.Api.Domain.Entities.Role { TenantId = seed.Actor.TenantId, Name = "HR Manager", NormalizedName = "HR MANAGER" };
        var user = new Zayra.Api.Domain.Entities.User { TenantId = seed.Actor.TenantId, Email = $"editor-{Guid.NewGuid():N}@example.invalid", NormalizedEmail = Guid.NewGuid().ToString(), FullName = "Second writer" };
        user.UserRoles.Add(new() { UserId = user.Id, RoleId = role.Id }); db.Roles.Add(role); db.Users.Add(user);
        await db.SaveChangesAsync(); var editor = seed.Actor with { UserId = user.Id, Name = "Second writer" };
        await new OfferLifecycleService(db, OfferLifecycleServiceTests.Config()).SaveDraftAsync(editor, seed.OfferId, Input(1));
        (await OfferRules.EvaluateApproverAsync(db, seed.Actor.TenantId, seed.OfferId, editor.UserId, default))
            .Should().Be(OfferApproverVerdict.ApproverIsAuthor);
        (await OfferRules.EvaluateApproverAsync(db, seed.Actor.TenantId, seed.OfferId, seed.Actor.UserId, default))
            .Should().Be(OfferApproverVerdict.ApproverIsAuthor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdoptionRequiresARealCandidateInTheApplicationsCompany(bool wrongCompany)
    {
        var seed = await DraftAsync(); await using var db = fixture.CreateDb();
        var app = await db.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId);
        if (wrongCompany)
        {
            var other = new Company { TenantId = seed.Actor.TenantId }; db.Companies.Add(other);
            (await db.Candidates.SingleAsync(c => c.Id == app.CandidateId)).CompanyId = other.Id;
        }
        else app.CandidateId = Guid.Empty;
        await db.SaveChangesAsync();
        var refusal = await FluentActions.Awaiting(() => new OfferLifecycleService(db, OfferLifecycleServiceTests.Config())
            .SaveDraftAsync(seed.Actor, seed.OfferId, Input(1))).Should().ThrowAsync<OfferLifecycleException>();
        refusal.Which.Code.Should().Be("offer_candidate_not_found");
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).SeriesId.Should().BeNull();
    }

    private async Task<OfferLifecycleSeed> DraftAsync()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using var db = fixture.CreateDb();
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId); offer.Status = "Draft"; offer.SentAtUtc = null;
        db.Departments.Add(new() { TenantId = seed.Actor.TenantId, NameEn = "Engineering", Code = "ENG" });
        db.Designations.Add(new() { TenantId = seed.Actor.TenantId, TitleEn = "Engineer", Code = "ENG" });
        await db.SaveChangesAsync(); return seed;
    }

    [Fact]
    public async Task ExplicitAdoptionSavesReviewedTermsIdentityAndRejectsStaleEdit()
    {
        var seed = await DraftAsync(); await using var db = fixture.CreateDb();
        var service = new OfferLifecycleService(db, OfferLifecycleServiceTests.Config());
        var offer = await service.SaveDraftAsync(seed.Actor, seed.OfferId, Input(1));
        offer.SeriesId.Should().NotBeNull(); offer.Version.Should().Be(2); offer.GrossSalary.Should().Be(1200);
        offer.RecipientEmail.Should().Be("reviewed@example.invalid"); offer.CandidateId.Should().NotBeNull();
        offer.OfferPolicyVersion.Should().Be(0); offer.CompensationSnapshotJson.Should().NotBeNullOrEmpty();
        var stale = await FluentActions.Awaiting(() => service.SaveDraftAsync(seed.Actor, seed.OfferId, Input(1)))
            .Should().ThrowAsync<OfferLifecycleException>();
        stale.Which.Code.Should().Be("offer_version_conflict");
    }

    [Fact]
    public async Task ChangedPolicyRequiresExplicitCurrentVersionReview()
    {
        var seed = await DraftAsync(); await using var db = fixture.CreateDb();
        var service = new OfferLifecycleService(db, OfferLifecycleServiceTests.Config());
        await service.SaveDraftAsync(seed.Actor, seed.OfferId, Input(1));
        db.OfferCompanyPolicies.Add(new() { TenantId = seed.Actor.TenantId, CompanyId = seed.Actor.CompanyId, Version = 1 });
        await db.SaveChangesAsync();
        var changed = await FluentActions.Awaiting(() => service.SaveDraftAsync(seed.Actor, seed.OfferId, Input(2, null)))
            .Should().ThrowAsync<OfferLifecycleException>();
        changed.Which.Code.Should().Be("offer_policy_changed");
        (await service.SaveDraftAsync(seed.Actor, seed.OfferId, Input(2, 1))).OfferPolicyVersion.Should().Be(1);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("KWD")]
    public async Task ChangedCurrencyRequiresExplicitReviewEvenWhenPolicyVersionUnchanged(string currency)
    {
        var seed = await DraftAsync(); await using var db = fixture.CreateDb();
        var service = new OfferLifecycleService(db, OfferLifecycleServiceTests.Config());
        await service.SaveDraftAsync(seed.Actor, seed.OfferId, Input(1));
        (await db.Companies.SingleAsync(c => c.Id == seed.Actor.CompanyId)).DefaultCurrency = currency;
        await db.SaveChangesAsync();
        var refusal = await FluentActions.Awaiting(() => service.SaveDraftAsync(seed.Actor, seed.OfferId, Input(2, null)))
            .Should().ThrowAsync<OfferLifecycleException>();
        refusal.Which.Code.Should().Be("offer_policy_changed");
        (await service.SaveDraftAsync(seed.Actor, seed.OfferId, Input(2, 0))).CompensationCurrency.Should().Be(currency);
    }

    [Theory]
    [InlineData("PendingApproval")]
    [InlineData("Approved")]
    [InlineData("Sent")]
    [InlineData("Accepted")]
    [InlineData("Draft")]
    public async Task LegacySubmittedAndFrozenRejectedTermsCannotBeEdited(string state)
    {
        var seed = await DraftAsync(); await using var db = fixture.CreateDb();
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId); offer.Status = state;
        db.OfferApprovals.Add(new() { TenantId = seed.Actor.TenantId, ApplicationId = seed.ApplicationId,
            OfferLetterId = seed.OfferId, StepOrder = 1, Status = state == "Draft" ? "Rejected" : "Pending" });
        await db.SaveChangesAsync();
        var result = await FluentActions.Awaiting(() => new OfferLifecycleService(db, OfferLifecycleServiceTests.Config())
            .SaveDraftAsync(seed.Actor, seed.OfferId, Input(1))).Should().ThrowAsync<OfferLifecycleException>();
        result.Which.Code.Should().Be("offer_terms_immutable");
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).BasicSalary.Should().Be(1000);
    }
}
