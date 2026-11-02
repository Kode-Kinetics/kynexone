using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Saves that join an outer transaction are invisible to other connections until commit, so the eviction must
/// repeat AT COMMIT for every save in the transaction (H1), and also on rollback (the writer's own connection may
/// have cached a verdict computed from its uncommitted state).
/// </summary>
public sealed class SessionVerdictCommitTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ZayraDbContext _db = null!;
    private MemorySessionVerdictCache _cache = null!;
    private Tenant _tenant = null!;
    private User _a = null!;
    private User _b = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _cache = new MemorySessionVerdictCache(new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 }),
            Options.Create(new SessionVerdictCacheOptions { TtlSeconds = 30 }));
        var interceptor = new SessionVerdictInvalidationInterceptor(_cache);
        _db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor, new SessionVerdictInvalidationInterceptor.CommitHook(interceptor))
            .Options);
        await _db.Database.EnsureCreatedAsync();
        _tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        _a = NewUser("a");
        _b = NewUser("b");
        _db.Tenants.Add(_tenant);
        _db.Users.AddRange(_a, _b);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private User NewUser(string n) => new()
    {
        Id = Guid.NewGuid(), TenantId = _tenant.Id, Tenant = _tenant, Email = $"{n}@t.local",
        NormalizedEmail = $"{n.ToUpperInvariant()}@T.LOCAL", FullName = n, PasswordHash = "x"
    };

    private SessionVerdictKey KeyOf(User u) => _cache.KeyFor(_tenant.Id, u.Id, "fp");

    [Fact]
    public async Task TwoSavesInOneTransaction_BothUsersAreEvictedAtCommit()
    {
        await using var tx = await _db.Database.BeginTransactionAsync();

        _a.IsActive = false;
        await _db.SaveChangesAsync();                // save #1 (A), uncommitted
        // A concurrent request on another connection still sees A's OLD committed row and caches a positive
        // under the already-bumped version.
        _cache.RememberCurrent(KeyOf(_a));

        _b.FullName = "b2";
        await _db.SaveChangesAsync();                // save #2 (B) must not drop A from the commit-time list

        await tx.CommitAsync();

        Assert.False(_cache.IsKnownCurrent(KeyOf(_a)), "A's stale positive survived the commit.");
    }

    [Fact]
    public async Task RolledBackTransaction_EvictsWhatTheWritersOwnConnectionCached_AndLaterCachingStillWorks()
    {
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            _a.IsActive = false;
            await _db.SaveChangesAsync();
            _cache.RememberCurrent(KeyOf(_a));       // computed from the writer's uncommitted state
            await tx.RollbackAsync();
        }

        Assert.False(_cache.IsKnownCurrent(KeyOf(_a)), "A verdict from rolled-back state must not be served.");

        // The pending list was cleared: caching afterwards works and an unrelated commit does not evict it.
        _cache.RememberCurrent(KeyOf(_a));
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            _db.ChangeTracker.Clear();
            await tx.CommitAsync();
        }
        Assert.True(_cache.IsKnownCurrent(KeyOf(_a)));
    }

    [Fact]
    public async Task SaveOutsideATransaction_EvictsImmediately()
    {
        _cache.RememberCurrent(KeyOf(_a));
        _a.IsActive = false;
        await _db.SaveChangesAsync();
        Assert.False(_cache.IsKnownCurrent(KeyOf(_a)));
    }
}
