using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// The schema change behind designation-driven access (a link table from a designation to existing roles, and a nullable marker on
/// user_roles) must exist as a migration and must only ADD. During a rolling deploy the old code runs against the new schema, so nothing
/// may be dropped, renamed, retyped or made NOT NULL, and no existing row may be rewritten.
/// </summary>
public sealed class DesignationAccessMigrationTests
{
    private const string MigrationSuffix = "_AddDesignationAccessRoles";

    private static string MigrationFile()
    {
        var dir = Path.Combine(SourceScan.ResolveApiRoot(), "Migrations");
        return Directory.EnumerateFiles(dir, "*" + MigrationSuffix + ".cs").SingleOrDefault(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
               ?? throw new Xunit.Sdk.XunitException("No migration named *" + MigrationSuffix + " exists: the designation_access_roles table and user_roles.granted_by_designation_id have no migration.");
    }

    private static string UpBody(string code)
    {
        var up = code.IndexOf("void Up(", StringComparison.Ordinal);
        var down = code.IndexOf("void Down(", StringComparison.Ordinal);
        up.Should().BeGreaterThan(-1);
        return down > up ? code[up..down] : code[up..];
    }

    [Fact]
    public void The_designation_access_migration_only_adds_a_table_and_a_nullable_column()
    {
        var up = SourceScan.StripCSharpComments(UpBody(File.ReadAllText(MigrationFile())));

        up.Should().Contain("CreateTable").And.Contain("designation_access_roles");
        up.Should().Contain("AddColumn").And.Contain("granted_by_designation_id");
        var column = Regex.Match(up, @"AddColumn<[^>]+>\s*\([^;]*?granted_by_designation_id[^;]*?\);", RegexOptions.Singleline).Value;
        column.Should().Contain("nullable: true", "old code writes user_roles rows without the marker");
        column.Should().NotContain("defaultValue", "no default means no rewrite and every existing role stays 'given by hand'");
        up.Should().NotMatchRegex(@"\b(DropColumn|DropTable|RenameColumn|RenameTable|AlterColumn|DropIndex|DropForeignKey|DeleteData|UpdateData)\b");
        up.Should().NotMatchRegex(@"(?i)\b(DELETE\s+FROM|UPDATE\s+\w+\s+SET|DROP\s|TRUNCATE|ALTER\s+COLUMN|RENAME\s)\b");
    }

    [Fact]
    public void The_designation_access_migration_is_visible_to_ef_and_has_a_down_that_removes_what_it_added()
    {
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=u;Password=p").Options);
        db.Database.GetMigrations().Should().Contain(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        var code = SourceScan.StripCSharpComments(File.ReadAllText(MigrationFile()));
        var down = code[code.IndexOf("void Down(", StringComparison.Ordinal)..];
        down.Should().Contain("DropTable").And.Contain("designation_access_roles").And.Contain("DropColumn").And.Contain("granted_by_designation_id");
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task The_migration_keeps_existing_role_assignments_as_given_by_hand_and_down_removes_it()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var thisMigration = all.Single(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        var previous = all[all.IndexOf(thisMigration) - 1];

        await migrator.MigrateAsync(previous);
        const string tenant = "'10000000-0000-0000-0000-0000000000a1'", role = "'20000000-0000-0000-0000-0000000000a1'", user = "'30000000-0000-0000-0000-000000000001'";
        await Exec(container, await InsertRowSql(container, "tenants", new() { ["id"] = tenant, ["slug"] = "'t1'" }));
        await Exec(container, await InsertRowSql(container, "roles", new() { ["id"] = role, ["tenant_id"] = tenant, ["normalized_name"] = "'REVIEWER'", ["is_active"] = "true", ["is_deleted"] = "false" }));
        await Exec(container, await InsertRowSql(container, "users", new()
        {
            ["id"] = user, ["tenant_id"] = tenant, ["email"] = "'a@t1'", ["normalized_email"] = "'A@T1'", ["status"] = "'Active'",
            ["is_active"] = "true", ["is_deleted"] = "false", ["created_at_utc"] = "now()",
        }));
        await Exec(container, $"INSERT INTO user_roles (user_id, role_id) VALUES ({user}, {role})");

        await migrator.MigrateAsync(thisMigration);
        await migrator.MigrateAsync(thisMigration); // a re-run is a no-op

        (await Scalar<long>(container, "SELECT count(*) FROM user_roles")).Should().Be(1, "no existing assignment is touched");
        (await Scalar<long>(container, "SELECT count(*) FROM user_roles WHERE granted_by_designation_id IS NULL")).Should().Be(1, "every existing role stays 'given by hand'");
        (await Scalar<string>(container, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'user_roles' AND column_name = 'granted_by_designation_id'")).Should().Be("YES");
        (await Scalar<long>(container, "SELECT count(*) FROM designation_access_roles")).Should().Be(0);

        await migrator.MigrateAsync(previous);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'user_roles' AND column_name = 'granted_by_designation_id'")).Should().Be(0);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'designation_access_roles'")).Should().Be(0);
        (await Scalar<long>(container, "SELECT count(*) FROM user_roles")).Should().Be(1);
    }

    private static async Task Exec(PostgreSqlContainer container, string sql)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(PostgreSqlContainer container, string sql)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? default! : (T)r;
    }

    private static async Task<string> InsertRowSql(PostgreSqlContainer container, string table, Dictionary<string, string> overrides)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t " +
            "AND is_nullable = 'NO' AND column_default IS NULL AND is_generated = 'NEVER' ORDER BY ordinal_position", conn);
        cmd.Parameters.AddWithValue("t", table);
        var columns = new List<string>(); var values = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                columns.Add(name);
                values.Add(overrides.TryGetValue(name, out var v) ? v : reader.GetString(1) switch
                {
                    "uuid" => "gen_random_uuid()",
                    "integer" or "smallint" or "bigint" or "numeric" or "double precision" or "real" => "0",
                    "boolean" => "false",
                    "timestamp with time zone" or "timestamp without time zone" => "now()",
                    _ => "'x'",
                });
            }
        foreach (var (name, value) in overrides.Where(o => !columns.Contains(o.Key))) { columns.Add(name); values.Add(value); }
        return new StringBuilder($"INSERT INTO {table} (").AppendJoin(", ", columns).Append(") VALUES (").AppendJoin(", ", values).Append(')').ToString();
    }
}
