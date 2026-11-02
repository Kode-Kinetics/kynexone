using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Query-count evidence on real PostgreSQL: the uncached check issues many statements per request; with the
/// verdict cache the second request issues none, and a write evicts it so the next request pays the full check again.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SessionVerdictQueryCountPostgresTests
{
    private readonly PostgresFixture _fixture;
    public SessionVerdictQueryCountPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CachedRequest_IssuesNoStatements_AndAWriteForcesRevalidation()
    {
        ProductionSizedAdminSeed.Seeded seeded;
        await using (var seed = _fixture.CreateRetryingDb())
            seeded = await ProductionSizedAdminSeed.SeedAsync(seed);

        var cache = new MemorySessionVerdictCache(new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new SessionVerdictCacheOptions { TtlSeconds = 30 }));
        var recorder = new ReaderCommandRecorder();
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, p => p.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null))
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance, recorder,
                new SessionVerdictInvalidationInterceptor(cache))
            .Options;
        await using var db = new ZayraDbContext(options);
        var principal = await ProductionSizedAdminSeed.PrincipalAsync(db, seeded);

        recorder.Reset();
        Assert.True((await CurrentSessionValidator.ValidateAsync(principal, db, cache, CancellationToken.None)).Current);
        var uncached = recorder.Commands.Count;
        Assert.True(uncached >= 8, $"Expected the full check to issue many statements, saw {uncached}.");

        recorder.Reset();
        Assert.True((await CurrentSessionValidator.ValidateAsync(principal, db, cache, CancellationToken.None)).Current);
        Assert.Empty(recorder.Commands);

        var user = await db.Users.SingleAsync(x => x.Id == seeded.UserId);
        user.FullName += "!";
        await db.SaveChangesAsync();
        recorder.Reset();
        await CurrentSessionValidator.ValidateAsync(principal, db, cache, CancellationToken.None);
        Assert.NotEmpty(recorder.Commands);
    }
}
