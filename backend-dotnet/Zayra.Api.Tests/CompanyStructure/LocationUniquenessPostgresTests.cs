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

    // Only a violation of the CODE index means "code taken". Any other unique violation is a different problem and must surface as itself.
    [Theory]
    [InlineData("IX_locations_tenant_id_code", true)]
    [InlineData("some_other_unique_constraint", false)]
    public async Task OnlyAViolationOfTheCodeIndex_IsAnsweredAsCodeTaken(string constraint, bool isCodeTaken)
    {
        var tenant = Guid.NewGuid();
        var trigger = $"trg_loc_{tenant:N}";
        await using (var ddl = fixture.CreateDb())
            await ddl.Database.ExecuteSqlRawAsync(
                $"CREATE FUNCTION {trigger}() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'duplicate key' USING ERRCODE = '23505', CONSTRAINT = '{constraint}'; END $$ LANGUAGE plpgsql; " +
                $"CREATE TRIGGER {trigger} BEFORE INSERT ON locations FOR EACH ROW WHEN (NEW.tenant_id = '{tenant}') EXECUTE FUNCTION {trigger}();");
        Exception? thrown;
        try
        {
            await using var db = fixture.CreateDb();
            thrown = await Record.ExceptionAsync(() => new LocationService(db, new AuditService(db)).CreateAsync(tenant,
                new LocationInput(null, "ONE", "One", null, null, null, null, null, null, null, null),
                new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenant), _ => true, default));
        }
        finally
        {
            await using var ddl = fixture.CreateDb();
            await ddl.Database.ExecuteSqlRawAsync($"DROP TRIGGER IF EXISTS {trigger} ON locations; DROP FUNCTION IF EXISTS {trigger}();");
        }
        if (isCodeTaken) thrown.Should().BeOfType<LocationRuleException>().Which.Status.Should().Be(409);
        else thrown.Should().BeOfType<DbUpdateException>("a unique violation of anything but the code index is not 'code taken'");
    }
}
