using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Infrastructure.Retention;
using Zayra.Api.Infrastructure.Retention.Rules;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferTenantErasureTests(PostgresFixture fixture) : PlatformTestBase
{
    private async Task SoftDeleteAsync(OfferLifecycleSeed seed)
    {
        await using var db = fixture.CreateDb();
        var tenant = await db.Tenants.SingleAsync(t => t.Id == seed.Actor.TenantId);
        tenant.IsActive = false; tenant.Slug += SoftDeletedTenantRule.DeletedSlugMarker + "upload-fence";
        tenant.SoftDeletedAtUtc = DateTime.UtcNow.AddDays(-200); await db.SaveChangesAsync();
    }

    private async Task WaitForCompletionOrApplicationLockAsync(Task operation)
    {
        await using var observe = fixture.CreateDb();
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!operation.IsCompleted && DateTime.UtcNow < until)
        {
            var blocked = await observe.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%job_applications%' AND pid<>pg_backend_pid()").SingleAsync();
            if (blocked > 0) return;
            await Task.Delay(20);
        }
        operation.IsCompleted.Should().BeTrue("the competing operation must finish or demonstrably wait for the application lock");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadWinningFenceCannotWriteAnOrphanAfterEitherPurge(bool manual)
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new OfferMemoryStorage { BeforePut = async () => { entered.TrySetResult(); await resume.Task; } };
        await using var uploadDb = fixture.CreateDb();
        var upload = new OfferArtifactService(uploadDb, storage, OfferLifecycleServiceTests.Config()).FreezeAsync(seed.Actor, seed.OfferId, 2);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await SoftDeleteAsync(seed);
        var purge = EraseAsync(seed, storage, manual);
        try { await WaitForCompletionOrApplicationLockAsync(purge); }
        finally { resume.TrySetResult(); }
        try { await upload; } catch (OfferLifecycleException) { /* Purge may remove the parent before finalization. */ }
        var result = await purge;
        if (manual) result.Should().BeOfType<OkObjectResult>(); else result.Should().NotBeAssignableTo<Exception>();
        storage.Bytes.Should().BeEmpty("purge must not forget a PDF whose upload can still complete");
        await using var verify = fixture.CreateDb();
        (await verify.OfferArtifacts.CountAsync(a => a.TenantId == seed.Actor.TenantId)).Should().Be(0);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task CrashLikePendingBlocksPurgeRegardlessOfCurrentBlobPresence(bool manual, bool blobPresent)
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture); var storage = new OfferMemoryStorage();
        await using (var setup = fixture.CreateDb())
        {
            var row = new Zayra.Api.Models.OfferArtifact { TenantId = seed.Actor.TenantId, CompanyId = seed.Actor.CompanyId,
                ApplicationId = seed.ApplicationId, OfferId = seed.OfferId, StorageKey = storage.TenantKey(seed.Actor.TenantId, "unresolved.pdf") };
            setup.OfferArtifacts.Add(row); await setup.SaveChangesAsync();
            if (blobPresent) storage.Bytes.Add(row.StorageKey, [1, 2, 3]);
        }
        await SoftDeleteAsync(seed); var result = await EraseAsync(seed, storage, manual);
        if (manual) result.Should().BeOfType<ConflictObjectResult>();
        else result.Should().BeAssignableTo<IOException>().Which.Message.Should().Contain("unresolved");
        await using var verify = fixture.CreateDb();
        (await verify.OfferArtifacts.CountAsync(a => a.TenantId == seed.Actor.TenantId && a.State == "Pending")).Should().Be(1);
        (await verify.OfferLetters.CountAsync(o => o.Id == seed.OfferId)).Should().Be(1);
        storage.Bytes.Count.Should().Be(blobPresent ? 1 : 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PurgeWinningFencePreventsAWaitingFreezeFromUploading(bool manual)
    {
        var storage = new OfferMemoryStorage(); var seed = await FrozenAsync(storage); Guid revision;
        await using (var setup = fixture.CreateDb())
        {
            var app = await setup.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId);
            app.OfferReservationId = null; app.OfferReservationOfferId = null; app.OfferReservationState = null; app.OfferReservationVersion = null;
            await setup.SaveChangesAsync();
            revision = (await new OfferLifecycleService(setup, OfferLifecycleServiceTests.Config()).ReviseAsync(seed.Actor, seed.OfferId, new(4, "Replacement before erasure"))).Id;
        }
        await SoftDeleteAsync(seed);
        var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.BeforeDelete = async () => { deleting.TrySetResult(); await resume.Task; };
        var purge = EraseAsync(seed, storage, manual); await deleting.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await using var uploadDb = fixture.CreateDb();
        var upload = new OfferArtifactService(uploadDb, storage, OfferLifecycleServiceTests.Config()).FreezeAsync(seed.Actor, revision, 1);
        try { await WaitForCompletionOrApplicationLockAsync(upload); }
        finally { resume.TrySetResult(); }
        var result = await purge;
        if (manual) result.Should().BeOfType<OkObjectResult>(); else result.Should().NotBeAssignableTo<Exception>();
        await FluentActions.Awaiting(async () => await upload).Should().ThrowAsync<OfferLifecycleException>();
        storage.Bytes.Should().BeEmpty();
    }

    private async Task<OfferLifecycleSeed> FrozenAsync(OfferMemoryStorage storage)
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture); await using var db = fixture.CreateDb();
        await new OfferArtifactService(db, storage, OfferLifecycleServiceTests.Config()).FreezeAsync(seed.Actor, seed.OfferId, 2);
        var app = await db.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId);
        app.CurrentIssuedOfferId = seed.OfferId; app.OfferReservationId = Guid.NewGuid(); app.OfferReservationOfferId = seed.OfferId;
        app.OfferReservationState = "Unknown"; app.OfferReservationVersion = app.Version;
        await db.SaveChangesAsync(); return seed;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothTenantErasurePathsRetainArtifactObligationsOnFailureAndDeleteOnlyTheirTenantOnRetry(bool manual)
    {
        var storage = new OfferMemoryStorage(); var seed = await FrozenAsync(storage); var other = await FrozenAsync(storage);
        await using (var db = fixture.CreateDb())
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == seed.Actor.TenantId);
            tenant.IsActive = false; tenant.Slug += SoftDeletedTenantRule.DeletedSlugMarker + "offer-test";
            tenant.SoftDeletedAtUtc = DateTime.UtcNow.AddDays(-200); await db.SaveChangesAsync();
        }
        storage.FailDelete = true;
        var failed = await EraseAsync(seed, storage, manual);
        if (manual) failed.Should().BeOfType<ConflictObjectResult>();
        else failed.Should().BeOfType<IOException>();
        await using (var verify = fixture.CreateDb())
        {
            (await verify.OfferArtifacts.CountAsync(a => a.TenantId == seed.Actor.TenantId)).Should().Be(1);
            (await verify.Companies.CountAsync(c => c.Id == seed.Actor.CompanyId)).Should().Be(1);
            (await verify.OfferLetters.CountAsync(o => o.Id == seed.OfferId)).Should().Be(1);
        }
        storage.Bytes.Keys.Count(k => k.StartsWith(seed.Actor.TenantId.ToString("N") + "/")).Should().Be(1);
        storage.FailDelete = false;
        var succeeded = await EraseAsync(seed, storage, manual);
        if (manual) succeeded.Should().BeOfType<OkObjectResult>(); else succeeded.Should().NotBeAssignableTo<Exception>();
        await using var after = fixture.CreateDb();
        (await after.OfferArtifacts.CountAsync(a => a.TenantId == seed.Actor.TenantId)).Should().Be(0);
        (await after.OfferSeries.CountAsync(a => a.TenantId == seed.Actor.TenantId)).Should().Be(0);
        (await after.JobApplications.CountAsync(a => a.TenantId == seed.Actor.TenantId)).Should().Be(0);
        (await after.OfferArtifacts.CountAsync(a => a.TenantId == other.Actor.TenantId)).Should().Be(1);
        storage.Bytes.Should().ContainSingle(); storage.Bytes.Keys.Single().Should().StartWith(other.Actor.TenantId.ToString("N"));
    }

    private async Task<object?> EraseAsync(OfferLifecycleSeed seed, IDocumentStorage storage, bool manual)
    {
        await using var db = fixture.CreateDb(); using var services = new ServiceCollection().AddSingleton(storage).BuildServiceProvider();
        try
        {
            if (manual)
            {
                var controller = CreateController(db); controller.ControllerContext.HttpContext.RequestServices = services;
                return await controller.PurgeTenant(seed.Actor.TenantId, "PURGE", default);
            }
            object? result = null;
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var context = new JobExecutionContext(new(Guid.NewGuid(), seed.Actor.TenantId, "data-retention", "{}", 1, 1, Guid.NewGuid(), null),
                    db, services, [], TimeSpan.FromMinutes(2), default, default);
                var rule = new SoftDeletedTenantRule(new() { ApplyDeletions = true, AllowTenantErasure = true });
                result = await rule.ApplyAsync(context, new("offer-erasure-test", "Tenant", seed.Actor.TenantId.ToString(), "HardDelete", "Expired recovery window"), DateTime.UtcNow, default);
                await db.SaveChangesAsync(); await tx.CommitAsync();
            });
            return result;
        }
        catch (Exception e) { return e; }
    }
}
