using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;
using Zayra.Api.Application.Recruitment;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferAdapterContractTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ExistingOfferReadDoesNotAdvertiseDeprecatedBareSend()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId); offer.Status = "Draft";
        db.SystemSettings.Add(new() { TenantId = seed.Actor.TenantId, Category = OfferRules.PolicyCategory, SettingKey = OfferRules.PolicyKey, SettingValue = "false" });
        await db.SaveChangesAsync();
        var response = await Principal(new OffersController(db, null!, new RecruitmentService(db)), seed).Get(seed.OfferId, default);
        var json = JsonSerializer.SerializeToElement(response.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.GetProperty("approval").GetProperty("canSend").GetBoolean().Should().BeFalse();
        json.GetProperty("approval").GetProperty("sendBlockedReason").GetString().Should().Contain("workspace");
    }

    [Fact]
    public async Task ExistingDownloadMustNotReconstructMissingFrozenOriginal()
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture); await using var db = fixture.CreateDb();
        var storage = new OfferMemoryStorage(); var config = OfferLifecycleServiceTests.Config();
        var artifacts = new OfferArtifactService(db, storage, config);
        await artifacts.FreezeAsync(seed.Actor, seed.OfferId, 2); storage.Bytes.Clear();
        var result = await Principal(new OffersController(db, null!, new RecruitmentService(db), new(db, config, artifacts), artifacts), seed)
            .Download(seed.OfferId, default);
        var response = result.Should().BeAssignableTo<ObjectResult>().Subject; response.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(response.Value).Should().Contain("offer_artifact_missing");
    }

    private static T Principal<T>(T controller, OfferLifecycleSeed seed) where T : ControllerBase
    {
        controller.ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new(new ClaimsIdentity([
            new Claim("tenant_id", seed.Actor.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, seed.Actor.UserId.ToString()),
            new Claim(ClaimTypes.Role, "HR Manager"), new Claim("name", seed.Actor.Name) ], "Test")) } };
        return controller;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BareAcceptAdaptersRequireExplicitResponseAndDoNotHire(bool drawer)
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var result = drawer
            ? await Principal(new ApplicationsController(db, new RecruitmentService(db), new OfferNoNotifications()), seed).AcceptOffer(seed.OfferId, default)
            : await Principal(new OffersController(db, null!, new RecruitmentService(db)), seed).Accept(seed.OfferId, default);
        var response = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(response.Value).Should().Contain("explicit_response_required");
        (await db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == seed.ApplicationId)).Status.Should().Be("Active");
        (await db.EmployeeDrafts.CountAsync(d => d.TenantId == seed.Actor.TenantId)).Should().Be(0);
    }

    [Fact]
    public async Task ApplicationGenerationPreservesEarlierUnsubmittedDrafts()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var previous = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId); previous.Status = "Draft"; previous.SentAtUtc = null;
        db.Departments.Add(new() { TenantId = seed.Actor.TenantId, NameEn = "Engineering", Code = "ENG" });
        db.Designations.Add(new() { TenantId = seed.Actor.TenantId, TitleEn = "Engineer", Code = "ENG" });
        await db.SaveChangesAsync();
        var result = await Principal(new ApplicationsController(db, new RecruitmentService(db), new OfferNoNotifications()), seed)
            .GenerateOffer(seed.ApplicationId, new("Engineering", new(2027, 2, 1), 1000, 0, 0, 0, 3), default);
        result.Should().BeOfType<CreatedResult>();
        (await db.OfferLetters.AsNoTracking().AnyAsync(o => o.Id == seed.OfferId)).Should().BeTrue("history must not delete a previous unsubmitted draft");
        (await db.OfferLetters.CountAsync(o => o.ApplicationId == seed.ApplicationId)).Should().Be(2);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task OldSendAdaptersRequireExplicitDeliveryWithoutAnyStateMutation(bool drawer, bool managed)
    {
        var seed = managed ? await OfferArtifactPostgresTests.ManagedDraftAsync(fixture) : await OfferLifecycleTestData.SeedAsync(fixture);
        await using var db = fixture.CreateDb();
        if (!managed) { var legacy = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId); legacy.Status = "Draft"; legacy.SentAtUtc = null; }
        db.SystemSettings.Add(new() { TenantId = seed.Actor.TenantId, Category = OfferRules.PolicyCategory,
            SettingKey = OfferRules.PolicyKey, SettingValue = "false", DataType = "bool" }); await db.SaveChangesAsync();
        var result = drawer
            ? await Principal(new ApplicationsController(db, new RecruitmentService(db), new OfferNoNotifications()), seed).SendOffer(seed.OfferId, default)
            : await Principal(new OffersController(db, null!, new RecruitmentService(db)), seed).Send(seed.OfferId, default);
        var response = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(response.Value).Should().Contain("explicit_delivery_required");
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).Status.Should().Be("Draft");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManagedComponentCreationIsUnavailableUntilFeatureEnabled(bool drawer)
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        db.Departments.Add(new() { TenantId = seed.Actor.TenantId, NameEn = "Engineering", Code = "ENG" });
        db.Designations.Add(new() { TenantId = seed.Actor.TenantId, TitleEn = "Engineer", Code = "ENG" }); await db.SaveChangesAsync();
        OfferComponentInput[] components = [new("BASIC", "Basic", null, "Cash", 1000, null, "Monthly", true, null, null, 0)];
        var result = drawer
            ? await Principal(new ApplicationsController(db, new RecruitmentService(db), new OfferNoNotifications()), seed)
                .GenerateOffer(seed.ApplicationId, new("Engineering", new(2027, 2, 1), null, null, null, null, 3, Components: components), default)
            : await Principal(new OffersController(db, null!, new RecruitmentService(db)), seed)
                .Create(new(seed.ApplicationId, "Engineer", "Engineering", new(2027, 2, 1), null, null, null, null, 3, null, DateTime.UtcNow.AddDays(7), Components: components), default);
        var response = result.Should().BeAssignableTo<ObjectResult>().Subject; response.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(response.Value).Should().Contain("managed_offer_lifecycle_disabled");
        (await db.OfferLetters.CountAsync(o => o.ApplicationId == seed.ApplicationId)).Should().Be(1);
    }
}

file sealed class OfferNoNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken) => throw new InvalidOperationException("No email in lifecycle tests.");
}
