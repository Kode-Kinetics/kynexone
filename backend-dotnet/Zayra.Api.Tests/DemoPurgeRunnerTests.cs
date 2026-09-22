using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests;

public class DemoPurgeRunnerTests
{
    // "evostel" was once a demo slug and is now a live client pilot tenant. `--purge-demo` must
    // deactivate the remaining demo tenants and never touch it.
    [Fact]
    public async Task PurgeDemo_DeactivatesDemoTenants_ButNeverTheEvostelPilot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var pilot = new Tenant { Id = Guid.NewGuid(), Name = "Evostel", Slug = "evostel" };
        var demo = new Tenant { Id = Guid.NewGuid(), Name = "IntelliFlow", Slug = "intelliflow" };
        db.Tenants.AddRange(pilot, demo);
        await db.SaveChangesAsync();

        await DemoPurgeRunner.RunAsync(db, protectedSlug: null, NullLogger.Instance);

        db.ChangeTracker.Clear();
        Assert.True((await db.Tenants.SingleAsync(x => x.Id == pilot.Id)).IsActive);
        Assert.False((await db.Tenants.SingleAsync(x => x.Id == demo.Id)).IsActive);
    }
}
