using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// 20261008000200_ReleaseAR2DependantsSoftDelete on the real migration chain: expand-only on the way up, and a Down that
/// refuses while a removed dependant exists (dropping the column would make them covered again), then rolls back cleanly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DependantsSoftDeleteMigrationPostgresTests
{
    private const string ThisMigration = "20261008000200_ReleaseAR2DependantsSoftDelete";

    [Fact]
    public async Task Down_RefusesWhileARemovedDependantExists_ThenRollsBackAndReapplies()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.IndexOf(ThisMigration) - 1];
        await migrator.MigrateAsync();

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO employee_dependents (id, tenant_id, employee_id, full_name, relationship, national_id, is_deleted, deleted_at_utc) " +
            "VALUES (gen_random_uuid(), gen_random_uuid(), 1, 'Removed', 'Child', '', true, now())");
        var refused = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(before));
        Assert.Contains("R2_DEPENDANTS_SOFT_DELETED", refused.MessageText);
        Assert.Contains(ThisMigration, await db.Database.GetAppliedMigrationsAsync());

        await db.Database.ExecuteSqlRawAsync("DELETE FROM employee_dependents WHERE is_deleted");
        await migrator.MigrateAsync(before);
        Assert.DoesNotContain(ThisMigration, await db.Database.GetAppliedMigrationsAsync());
        await migrator.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
