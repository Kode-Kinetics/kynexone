using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>
/// A location code is unique among a tenant's LIVE locations: the index ignores deleted rows, so a code freed by a delete can be used again.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class LocationUniquenessPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TheUniqueIndex_IgnoresDeletedRows()
    {
        await using var db = fixture.CreateDb();
        var def = await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'locations' AND indexdef ILIKE '%UNIQUE%' AND indexdef ILIKE '%code%'").ToListAsync();
        def.Should().ContainSingle().Which.Should().ContainEquivalentOf("is_deleted");
    }

    [Fact]
    public async Task ADeletedCode_CanBeSavedAgain_AndTwoLiveRowsCannotShareOne()
    {
        var tenant = Guid.NewGuid();
        await using (var db = fixture.CreateDb())
        {
            db.Locations.Add(new Location { TenantId = tenant, Code = "HQ", NameEn = "Gone", IsDeleted = true });
            db.Locations.Add(new Location { TenantId = tenant, Code = "HQ", NameEn = "Live" });
            await db.SaveChangesAsync();
        }
        await using var again = fixture.CreateDb();
        again.Locations.Add(new Location { TenantId = tenant, Code = "HQ", NameEn = "Second live" });
        await FluentActions.Awaiting(() => again.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ConcurrentCreatesOfOneCode_YieldOneWinner_AndEveryLoserA409_NeverADatabaseError()
    {
        var tenant = Guid.NewGuid();
        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 12).Select(async _ =>
        {
            await start.Task;
            await using var db = fixture.CreateDb();
            try
            {
                await new LocationService(db, new AuditService(db)).CreateAsync(tenant, new LocationInput(null, "RACE", "Race", null, null, null, null, null, null, null, null),
                    new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenant), _ => true, default);
                return (Exception?)null;
            }
            catch (Exception ex) { return ex; }
        }).ToList();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        results.Count(r => r is null).Should().Be(1);
        results.Where(r => r is not null).Should().OnlyContain(r => r is LocationRuleException && ((LocationRuleException)r).Status == 409);
    }
}
