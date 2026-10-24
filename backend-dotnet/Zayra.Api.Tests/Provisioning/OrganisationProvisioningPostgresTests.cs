using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Provisioning;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests.Provisioning;

/// <summary>
/// WP2: two setup runs against one empty database must create ONE organisation. The check-then-insert
/// is serialised by a transaction-scoped advisory lock taken inside the provisioning transaction, which
/// only real PostgreSQL can prove. Runs on its own empty database in the shared container: the fixture's
/// database already holds other tests' tenants, and any row there would make every setup a refusal.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OrganisationProvisioningPostgresTests
{
    private readonly PostgresFixture _fx;
    public OrganisationProvisioningPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task TwoConcurrentSetups_CreateExactlyOneOrganisation()
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(connectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance)
            .Options);

        async Task<OrganisationProvisioningResult> SetupAsync(string name)
        {
            await using var db = Db();
            var service = new OrganisationProvisioningService(db, new AuthSeeder(db), new Pbkdf2PasswordHasher());
            return await service.ProvisionAsync(new OrganisationProvisioningRequest
            {
                OrganisationName = name,
                HomeCountry = "SA",
                AdminEmail = $"admin@{name.ToLowerInvariant()}.example",
                AdminPassword = "A-Real-Admin-Pass-42",
            }, OrganisationProvisioningService.SetupActor, CancellationToken.None);
        }

        var results = await Task.WhenAll(SetupAsync("First"), SetupAsync("Second"));

        results.Count(r => r.Succeeded).Should().Be(1, "the second setup must see the first one's organisation");
        results.Single(r => !r.Succeeded).Refusal.Should().Contain("already set up");

        await using var verify = Db();
        (await verify.Tenants.CountAsync()).Should().Be(1);
        (await verify.Companies.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await verify.Users.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    /// <summary>A fresh database in the fixture's container, with the full schema and no rows.</summary>
    private async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = $"org_setup_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(_fx.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{name}\"";
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_fx.ConnectionString) { Database = name }.ConnectionString;
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(connectionString, PostgresFixture.ProductionProviderOptions).Options);
        await db.Database.EnsureCreatedAsync();
        return connectionString;
    }
}
