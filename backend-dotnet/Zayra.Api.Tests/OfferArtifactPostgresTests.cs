using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferArtifactPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task FailedFinalizationKeepsPendingBytesAndRetryNeverUploadsAgain()
    {
        var seed = await ManagedDraftAsync(fixture); var storage = new OfferMemoryStorage();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.EnableRetryOnFailure())
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance, new FailFinalizationOnce()).Options);
        var service = new OfferArtifactService(db, storage, OfferLifecycleServiceTests.Config());
        await FluentActions.Awaiting(() => service.FreezeAsync(seed.Actor, seed.OfferId, 2)).Should().ThrowAsync<IOException>();
        var row = await db.OfferArtifacts.AsNoTracking().SingleAsync(a => a.OfferId == seed.OfferId);
        row.State.Should().Be("Uploaded"); storage.Bytes.Should().ContainSingle();
        var unavailable = await FluentActions.Awaiting(() => service.OriginalAsync(seed.Actor, seed.OfferId)).Should().ThrowAsync<OfferLifecycleException>();
        unavailable.Which.Code.Should().Be("offer_original_unavailable");
        var result = await service.FreezeAsync(seed.Actor, seed.OfferId, 2);
        result.Id.Should().Be(row.Id); storage.Bytes.Should().ContainSingle();
        (await db.OfferLifecycleEvents.CountAsync(e => e.OfferId == seed.OfferId && e.EventType == "Frozen")).Should().Be(1);
    }

    [Fact]
    public async Task PolicyChangeDuringUploadReleasesPendingPointerForExplicitDraftReview()
    {
        var seed = await ManagedDraftAsync(fixture);
        var storage = new OfferMemoryStorage { AfterPut = async () =>
        {
            await using var policyDb = fixture.CreateDb();
            policyDb.OfferCompanyPolicies.Add(new() { TenantId = seed.Actor.TenantId, CompanyId = seed.Actor.CompanyId });
            await policyDb.SaveChangesAsync();
        }};
        await using var db = fixture.CreateDb(); var service = new OfferArtifactService(db, storage, OfferLifecycleServiceTests.Config());
        var changed = await FluentActions.Awaiting(() => service.FreezeAsync(seed.Actor, seed.OfferId, 2)).Should().ThrowAsync<OfferLifecycleException>();
        changed.Which.Code.Should().Be("offer_policy_changed");
        var offer = await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId);
        offer.ArtifactId.Should().BeNull("policy review must be possible after a conclusively refused finalization");
        offer.FrozenAtUtc.Should().BeNull();
        (await db.OfferArtifacts.AsNoTracking().SingleAsync(a => a.OfferId == seed.OfferId)).State.Should().Be("DeletionPending");
    }

    internal static async Task<OfferLifecycleSeed> ManagedDraftAsync(PostgresFixture fixture)
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using var db = fixture.CreateDb();
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId); offer.Status = "Draft"; offer.SentAtUtc = null;
        db.Departments.Add(new() { TenantId = seed.Actor.TenantId, NameEn = "Engineering", Code = "ENG" });
        db.Designations.Add(new() { TenantId = seed.Actor.TenantId, TitleEn = "Engineer", Code = "ENG" });
        await db.SaveChangesAsync();
        await new OfferLifecycleService(db, OfferLifecycleServiceTests.Config()).SaveDraftAsync(seed.Actor, seed.OfferId,
            new(1, 0, new(2027, 1, 1), DateTime.UtcNow.AddDays(7), 3, "candidate@example.invalid",
                "Engineer", "Engineering", BasicSalary: 1000, AdoptLegacy: true));
        return seed;
    }

    [Fact]
    public async Task FrozenOriginalReturnsExactStoredBytesDespiteCompanyAndCandidateChanges()
    {
        var seed = await ManagedDraftAsync(fixture); var storage = new OfferMemoryStorage();
        await using var db = fixture.CreateDb();
        var service = new OfferArtifactService(db, storage, OfferLifecycleServiceTests.Config());
        var artifact = await service.FreezeAsync(seed.Actor, seed.OfferId, 2);
        storage.Bytes.Should().ContainSingle("freeze must persist its original document before returning");
        var stored = storage.Bytes.Values.Single().ToArray();
        artifact.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(stored)).ToLowerInvariant());
        artifact.SizeBytes.Should().Be(stored.Length); artifact.Provenance.Should().Be("FrozenOriginal");
        var company = await db.Companies.SingleAsync(c => c.Id == seed.Actor.CompanyId); company.LegalNameEn = "Changed after freeze";
        var candidate = await db.Candidates.SingleAsync(c => c.TenantId == seed.Actor.TenantId); candidate.FirstName = "Changed"; candidate.Email = "changed@example.invalid";
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        (await service.OriginalAsync(seed.Actor, seed.OfferId)).Should().Equal(stored);
        var frozen = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
        frozen.CandidateName.Should().Be("Test Candidate"); frozen.RecipientEmail.Should().Be("candidate@example.invalid");
        (await service.FreezeAsync(seed.Actor, seed.OfferId, 2)).Id.Should().Be(artifact.Id);
        (await db.OfferArtifacts.CountAsync(a => a.OfferId == seed.OfferId)).Should().Be(1);
    }

    [Theory]
    [InlineData(false, "offer_artifact_integrity")]
    [InlineData(true, "offer_artifact_missing")]
    public async Task OriginalRefusesTamperedOrMissingBlob(bool missing, string code)
    {
        var seed = await ManagedDraftAsync(fixture); var storage = new OfferMemoryStorage();
        await using var db = fixture.CreateDb(); var service = new OfferArtifactService(db, storage, OfferLifecycleServiceTests.Config());
        await service.FreezeAsync(seed.Actor, seed.OfferId, 2);
        storage.Bytes.Should().ContainSingle("the tampering scenario needs a persisted original");
        var key = storage.Bytes.Keys.Single();
        if (missing) storage.Bytes.Remove(key); else storage.Bytes[key] = [1, 2, 3];
        var failure = await FluentActions.Awaiting(() => service.OriginalAsync(seed.Actor, seed.OfferId)).Should().ThrowAsync<OfferLifecycleException>();
        failure.Which.Code.Should().Be(code);
    }

    [Fact]
    public async Task UploadFailureRetainsDurableDeletionObligationAndNeverFreezesOffer()
    {
        var seed = await ManagedDraftAsync(fixture); var storage = new OfferMemoryStorage { FailUpload = true };
        await using var db = fixture.CreateDb(); var service = new OfferArtifactService(db, storage, OfferLifecycleServiceTests.Config());
        await FluentActions.Awaiting(() => service.FreezeAsync(seed.Actor, seed.OfferId, 2)).Should().ThrowAsync<IOException>();
        db.ChangeTracker.Clear();
        var artifact = await db.OfferArtifacts.SingleAsync(a => a.OfferId == seed.OfferId);
        artifact.State.Should().Be("Pending"); artifact.StorageKey.Should().NotBeNullOrWhiteSpace();
        (await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId)).FrozenAtUtc.Should().BeNull();
        (await service.CleanupOrphansAsync(seed.Actor, 100)).Should().Be(0);
        (await db.OfferArtifacts.AsNoTracking().SingleAsync(a => a.Id == artifact.Id)).State.Should().Be("Pending");
        var retry = await FluentActions.Awaiting(() => service.FreezeAsync(seed.Actor, seed.OfferId, 2)).Should().ThrowAsync<OfferLifecycleException>();
        retry.Which.Code.Should().Be("offer_upload_unresolved");
        storage.Bytes.Should().ContainSingle("an upload exception cannot prove that remote writing has finished");
    }

    [Fact]
    public async Task CleanupNeverDeletesReferencedOriginalAndChangedPolicyBlocksFreeze()
    {
        var seed = await ManagedDraftAsync(fixture); var storage = new OfferMemoryStorage();
        await using var db = fixture.CreateDb(); var service = new OfferArtifactService(db, storage, OfferLifecycleServiceTests.Config());
        db.OfferCompanyPolicies.Add(new() { TenantId = seed.Actor.TenantId, CompanyId = seed.Actor.CompanyId }); await db.SaveChangesAsync();
        var changed = await FluentActions.Awaiting(() => service.FreezeAsync(seed.Actor, seed.OfferId, 2)).Should().ThrowAsync<OfferLifecycleException>();
        changed.Which.Code.Should().Be("offer_policy_changed");
        (await db.OfferArtifacts.CountAsync(a => a.OfferId == seed.OfferId)).Should().Be(0);
        var policy = await db.OfferCompanyPolicies.SingleAsync(p => p.TenantId == seed.Actor.TenantId); db.OfferCompanyPolicies.Remove(policy); await db.SaveChangesAsync();
        await service.FreezeAsync(seed.Actor, seed.OfferId, 2); var original = storage.Bytes.Values.Single().ToArray();
        await service.CleanupOrphansAsync(seed.Actor, 100);
        (await service.OriginalAsync(seed.Actor, seed.OfferId)).Should().Equal(original);
    }
}

internal sealed class OfferMemoryStorage : IDocumentStorage
{
    internal readonly Dictionary<string, byte[]> Bytes = [];
    internal bool FailUpload;
    internal bool FailDelete;
    internal Func<Task>? AfterPut;
    internal Func<Task>? BeforePut;
    internal Func<Task>? BeforeDelete;
    public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) => throw new NotSupportedException();
    public string ResolvePath(string storageUrl) => throw new NotSupportedException();
    public string TenantKey(Guid tenantId, string relativeName) => $"{tenantId:N}/{relativeName}";
    public async Task PutAtAsync(Guid tenantId, string key, byte[] content, string contentType, CancellationToken ct = default)
    {
        if (!key.StartsWith($"{tenantId:N}/")) throw new UnauthorizedAccessException();
        if (BeforePut is not null) await BeforePut();
        Bytes.Add(key, content.ToArray()); // Refuse accidental overwrite even in the test store.
        if (FailUpload) throw new IOException("Simulated partial upload failure");
        if (AfterPut is not null) await AfterPut();
    }
    public Task<byte[]> GetBytesAsync(Guid tenantId, string key, CancellationToken ct = default) =>
        !key.StartsWith($"{tenantId:N}/") ? throw new UnauthorizedAccessException()
        : Task.FromResult(Bytes.TryGetValue(key, out var bytes) ? bytes.ToArray() : throw new FileNotFoundException());
    public async Task DeleteStrictAsync(Guid tenantId, string key, CancellationToken ct = default)
    {
        if (!key.StartsWith($"{tenantId:N}/")) throw new UnauthorizedAccessException();
        if (BeforeDelete is not null) await BeforeDelete();
        if (FailDelete) throw new IOException("Simulated delete failure");
        Bytes.Remove(key);
    }
}

file sealed class FailFinalizationOnce : SaveChangesInterceptor
{
    private bool failed;
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!failed && eventData.Context!.ChangeTracker.Entries<OfferArtifact>().Any(a => a.Entity.State == "Finalized" && a.State == EntityState.Modified))
        { failed = true; throw new IOException("Simulated finalization persistence failure"); }
        return ValueTask.FromResult(result);
    }
}
