using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests.CompanyStructure;

/// <summary>The location-code index migration rolls back over data the live-only index allowed: a deleted row sharing a code with a live one.</summary>
[Trait("Category", "Integration")]
public sealed class LocationCodeMigrationPostgresTests
{
    private const string Suffix = "_LocationCodeUniqueAmongLiveRows";
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-0000000000f1");

    private static async Task Exec(PostgreSqlContainer c, string sql)
    {
        await using var conn = new NpgsqlConnection(c.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<(string Code, bool Deleted)>> Rows(PostgreSqlContainer c)
    {
        await using var conn = new NpgsqlConnection(c.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT code, is_deleted FROM locations WHERE tenant_id = '{Tenant}' ORDER BY name_en", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, bool)>();
        while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetBoolean(1)));
        return rows;
    }

    private static string Insert(string name, string code, bool deleted) =>
        "INSERT INTO locations (id, tenant_id, code, name_en, name_ar, address_line1, address_line2, city, country_code, postal_code, is_active, is_deleted, created_at_utc) " +
        $"VALUES (gen_random_uuid(), '{Tenant}', '{code}', '{name}', '', '', '', '', '', '', true, {(deleted ? "true" : "false")}, now())";

    [Fact]
    public async Task Down_OverDeletedAndLiveRowsSharingACode_Succeeds_AndKeepsEveryRow_AndTheLiveCode()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var all = db.Database.GetMigrations().ToList();
        var thisMigration = all.Single(m => m.EndsWith(Suffix, StringComparison.Ordinal));
        var before = all[all.IndexOf(thisMigration) - 1];
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();

        await migrator.MigrateAsync(thisMigration);
        await Exec(container, Insert("a-gone-1", "HQ", true));
        await Exec(container, Insert("b-gone-2", "HQ", true));
        await Exec(container, Insert("c-live", "HQ", false));
        await Exec(container, Insert("d-gone-alone", "ONLY", true));

        var down = await Record.ExceptionAsync(() => migrator.MigrateAsync(before));
        down.Should().BeNull("rolling back must not fail because the data the newer rule allowed exists");

        var rows = await Rows(container);
        rows.Should().HaveCount(4, "no row is lost");
        rows.Single(r => !r.Deleted).Code.Should().Be("HQ", "the live row keeps its code");
        rows.Select(r => r.Code).Should().OnlyHaveUniqueItems("the full unique index is back");
        rows.Single(r => r.Code == "ONLY").Deleted.Should().BeTrue("a deleted row that collides with nothing is left alone");

        var again = await Record.ExceptionAsync(() => migrator.MigrateAsync(thisMigration));
        again.Should().BeNull("and the migration re-applies");
    }
}
