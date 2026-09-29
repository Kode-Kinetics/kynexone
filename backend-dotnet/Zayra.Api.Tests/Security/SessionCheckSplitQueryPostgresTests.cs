using System.Data;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Real-PostgreSQL regression for the 2026-09-20/21 OOM kills: the per-request session check on
/// a production-sized admin must stay correct AND read the user graph as split statements, each
/// inside the check's REPEATABLE READ snapshot (split queries are only allowed inside the explicit
/// transaction that holds the snapshot, so every statement sees one coherent graph).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SessionCheckSplitQueryPostgresTests
{
    private readonly PostgresFixture _fixture;

    public SessionCheckSplitQueryPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SessionCheck_ProductionSizedAdmin_UsesSplitQueriesInsideRepeatableReadSnapshot()
    {
        ProductionSizedAdminSeed.Seeded seeded;
        await using (var seed = _fixture.CreateRetryingDb())
            seeded = await ProductionSizedAdminSeed.SeedAsync(seed);

        var recorder = new ReaderCommandRecorder();
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, provider => provider.EnableRetryOnFailure(
                maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null))
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance, recorder)
            .Options;
        await using var db = new ZayraDbContext(options);
        var principal = await ProductionSizedAdminSeed.PrincipalAsync(db, seeded);

        recorder.Reset();
        Assert.True(await TenantSessionSecurity.IsCurrentAsync(principal, db, CancellationToken.None));

        Assert.False(recorder.AnyCartesianUserGraphCommand,
            "A single statement joined role permissions with overrides/entity grants (cartesian product).");
        Assert.True(recorder.UserGraphCommands.Count >= 5,
            $"Expected split user-graph statements, saw {recorder.UserGraphCommands.Count}.");
        Assert.All(recorder.Commands, c => Assert.True(c.InTransaction,
            $"Statement ran outside the snapshot transaction: {c.Sql[..Math.Min(120, c.Sql.Length)]}"));
        Assert.All(recorder.Isolations, i => Assert.Equal(IsolationLevel.RepeatableRead, i));

        // Negative control on the same large graph: one dropped permission claim must fail closed.
        var missingOne = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            principal.Claims.Where(c => !(c.Type == "permission" && c.Value == seeded.PermissionKeys[^1])), "test"));
        Assert.False(await TenantSessionSecurity.IsCurrentAsync(missingOne, db, CancellationToken.None));
    }
}
