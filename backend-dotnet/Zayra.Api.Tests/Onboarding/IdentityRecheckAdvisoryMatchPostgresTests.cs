using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// Pins the cause of <c>EmployeeAccessTests.Forty_six_hires_...</c> failing in the CI integration shard (and only there)
/// with "A possible existing employee was detected for this identity" while every person it hires has a distinct Iqama.
///
/// <para>All 46 hires normalise to the same name ("recovery person": digits are not letters). The name-only pass returns
/// them as <c>possible</c> matches, capped at ten and, before this fix, with NO ORDER BY. PostgreSQL then returns whichever
/// ten rows its scan meets first, and that set shifts with the physical layout of the shared test database. The
/// transaction re-check inside the identity lock compares its read against the pre-check's read and treats every id it
/// did not see before as a "concurrent create" - so a different arbitrary ten refused a create that nothing had raced.
/// Two defects, two tests: a possible match must never refuse (its documented contract), and a capped read must be a
/// stable one.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class IdentityRecheckAdvisoryMatchPostgresTests
{
    private readonly PostgresFixture _fx;
    public IdentityRecheckAdvisoryMatchPostgresTests(PostgresFixture fx) => _fx = fx;

    private static readonly DuplicateProbe Probe = new("Recheck Person", null, null, "Saudi", null,
        new Dictionary<string, string> { ["iqama_number"] = "2000000001" }, null);

    [Fact]
    public async Task Recheck_DoesNotRefuse_OnAFreshPossibleMatch()
    {
        var tenantId = await SeedTenantAsync();
        var fresh = new[] { Match(41, DuplicateMatchTypes.Possible) };
        await using var db = _fx.CreateDb();
        await using var tx = await db.Database.BeginTransactionAsync();
        var act = () => EmployeeIdentityLock.AcquireAndRecheckAsync(
            db, new StubDetector(fresh), tenantId, Probe, alreadySeen: [1, 2, 3], acknowledged: false, default);
        await act.Should().NotThrowAsync("a same-name-only match is advisory and never refuses a commit");
    }

    [Theory]
    [InlineData(DuplicateMatchTypes.Probable)]
    [InlineData(DuplicateMatchTypes.Strong)]
    public async Task Recheck_StillRefuses_AFreshProbableOrStrongMatch(string matchType)
    {
        var tenantId = await SeedTenantAsync();
        await using var db = _fx.CreateDb();
        await using var tx = await db.Database.BeginTransactionAsync();
        var act = () => EmployeeIdentityLock.AcquireAndRecheckAsync(
            db, new StubDetector([Match(41, matchType)]), tenantId, Probe, alreadySeen: [1, 2, 3], acknowledged: false, default);
        await act.Should().ThrowAsync<DuplicatePersonException>();
    }

    [Fact]
    public async Task NameOnlyPass_KeepsTheLowestIds_WhateverTheHeapOrder()
    {
        var tenantId = await SeedTenantAsync();
        // Insert with DESCENDING ids so the physical (heap) order is the reverse of the id order: an unordered Take(10)
        // returns the ten HIGHEST ids, an ordered one the ten lowest. Deterministic stand-in for the order shifting
        // after updates in a long-lived shared database.
        var baseId = 1_500_000_000 + Random.Shared.Next(0, 400_000_000);
        var ids = Enumerable.Range(0, 15).Select(i => baseId + i).ToList();
        await using (var seed = _fx.CreateDb())
        {
            foreach (var id in Enumerable.Range(0, 15).Select(i => baseId + 14 - i))
            {
                seed.Employees.Add(new Employee
                {
                    Id = id, TenantId = tenantId, EmployeeCode = $"ORD-{id}", FullName = "Recovery Person", EnglishName = "Recovery Person",
                    Status = "Draft", JoiningDate = DateTime.UtcNow.Date,
                });
                await seed.SaveChangesAsync();
            }
        }
        await using var db = _fx.CreateDb();
        var found = await new EmployeeDuplicateDetector(db).FindAsync(tenantId,
            new DuplicateProbe("Recovery Person", null, null, "Saudi", null, new Dictionary<string, string>(), null), default);
        found.Should().HaveCount(10).And.OnlyContain(m => m.MatchType == DuplicateMatchTypes.Possible);
        found.Select(m => m.EmployeeId).Should().BeEquivalentTo(ids.Take(10), "the same data must always yield the same capped set");
    }

    private async Task<Guid> SeedTenantAsync()
    {
        await using var db = _fx.CreateDb();
        return await PostgresFixture.SeedMinimalTenant(db);
    }

    private static DuplicateMatch Match(int id, string type) =>
        new(id, $"E-{id}", "Someone", null, null, "Active", type, ["test"]);

    private sealed class StubDetector(IReadOnlyList<DuplicateMatch> matches) : IEmployeeDuplicateDetector
    {
        public Task<IReadOnlyList<DuplicateMatch>> FindAsync(Guid tenantId, DuplicateProbe probe, CancellationToken ct) => Task.FromResult(matches);
    }
}
