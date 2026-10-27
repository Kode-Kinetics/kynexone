using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

/// <summary>
/// AddWorkspaceOwner on the real migration chain and real PostgreSQL: each tenant's earliest-created ACTIVE Admin
/// becomes its owner (inactive Admins and non-Admins are passed over; a tenant with no active Admin gets none), the
/// database refuses a second owner in one tenant, and Down removes the column.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AddWorkspaceOwnerMigrationPostgresTests
{
    private const string MigrationSuffix = "_AddWorkspaceOwner";

    [Fact]
    public async Task BackfillsTheEarliestActiveAdmin_RefusesASecondOwner_AndDownRemovesTheColumn()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var thisMigration = all.Single(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        var previous = all[all.IndexOf(thisMigration) - 1];

        await migrator.MigrateAsync(previous);
        const string t1 = "'10000000-0000-0000-0000-0000000000a1'", t2 = "'10000000-0000-0000-0000-0000000000a2'";
        const string adminRole = "'20000000-0000-0000-0000-0000000000a1'", staffRole = "'20000000-0000-0000-0000-0000000000a2'";
        const string inactiveAdmin = "'30000000-0000-0000-0000-000000000001'", staffFirst = "'30000000-0000-0000-0000-000000000002'";
        const string firstAdmin = "'30000000-0000-0000-0000-000000000003'", laterAdmin = "'30000000-0000-0000-0000-000000000004'";
        const string t2Staff = "'30000000-0000-0000-0000-000000000005'";
        await Exec(container, await InsertRowSql(container, "tenants", new() { ["id"] = t1, ["slug"] = "'t1'" }));
        await Exec(container, await InsertRowSql(container, "tenants", new() { ["id"] = t2, ["slug"] = "'t2'" }));
        await Exec(container, await InsertRowSql(container, "roles", new() { ["id"] = adminRole, ["tenant_id"] = t1, ["normalized_name"] = "'ADMIN'", ["is_active"] = "true", ["is_deleted"] = "false" }));
        await Exec(container, await InsertRowSql(container, "roles", new() { ["id"] = staffRole, ["tenant_id"] = t1, ["normalized_name"] = "'EMPLOYEE'", ["is_active"] = "true", ["is_deleted"] = "false" }));
        async Task AddUser(string id, string tenant, string email, string created, bool active, string? role)
        {
            await Exec(container, await InsertRowSql(container, "users", new()
            {
                ["id"] = id, ["tenant_id"] = tenant, ["email"] = $"'{email}'", ["normalized_email"] = $"'{email.ToUpperInvariant()}'",
                ["status"] = "'Active'", ["is_active"] = active ? "true" : "false", ["is_deleted"] = "false", ["created_at_utc"] = $"'{created}'",
            }));
            if (role is not null) await Exec(container, $"INSERT INTO user_roles (user_id, role_id) VALUES ({id}, {role})");
        }
        await AddUser(inactiveAdmin, t1, "inactive@t1", "2026-01-01T00:00:00Z", active: false, adminRole);
        await AddUser(staffFirst, t1, "staff@t1", "2026-01-02T00:00:00Z", active: true, staffRole);
        await AddUser(firstAdmin, t1, "first@t1", "2026-01-03T00:00:00Z", active: true, adminRole);
        await AddUser(laterAdmin, t1, "later@t1", "2026-01-04T00:00:00Z", active: true, adminRole);
        await AddUser(t2Staff, t2, "staff@t2", "2026-01-01T00:00:00Z", active: true, null);

        await migrator.MigrateAsync(thisMigration);

        (await Scalar<string>(container, $"SELECT string_agg(id::text, ',') FROM users WHERE is_workspace_owner"))
            .Should().Be(firstAdmin.Trim('\''), "the earliest ACTIVE Admin of t1 is its owner, and t2 has no Admin to own it");

        var second = () => Exec(container, $"UPDATE users SET is_workspace_owner = true WHERE id = {laterAdmin}");
        (await second.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);

        await migrator.MigrateAsync(previous);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'is_workspace_owner'"))
            .Should().Be(0);
        await migrator.MigrateAsync(thisMigration);
        (await Scalar<long>(container, "SELECT count(*) FROM users WHERE is_workspace_owner")).Should().Be(1, "re-running Up re-derives it");
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
