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
/// The draft's government ID column is additive: drafts written before it read as "no ID" (empty text, no ciphertext), the
/// envelope CHECK on the ciphertext refuses junk and accepts a real envelope, and the migration reverses cleanly. Real
/// migration chain, real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EmployeeDraftIdNumberMigrationPostgresTests
{
    private const string MigrationSuffix = "_AddEmployeeDraftIdNumber";

    [Fact]
    public async Task ExistingDraftsReadAsNoId_TheEnvelopeCheckHolds_AndItRollsBackAndReapplies()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var thisMigration = all.SingleOrDefault(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        thisMigration.Should().NotBeNull("the migration that adds the draft's ID number exists");
        var previous = all[all.IndexOf(thisMigration!) - 1];

        await migrator.MigrateAsync(previous);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'employee_drafts' AND column_name IN ('id_number', 'id_number_enc')"))
            .Should().Be(0);
        await Exec(container, await InsertRowSql(container, "employee_drafts", new() { ["english_name"] = "'Before'" }));

        await migrator.MigrateAsync(thisMigration);
        (await Scalar<string>(container, "SELECT id_number FROM employee_drafts WHERE english_name = 'Before'")).Should().BeEmpty();
        (await Scalar<long>(container, "SELECT count(*) FROM employee_drafts WHERE english_name = 'Before' AND id_number_enc IS NULL")).Should().Be(1);
        (await Scalar<string>(container, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'employee_drafts' AND column_name = 'id_number_enc'")).Should().Be("YES");
        (await Scalar<string>(container, "SELECT column_default FROM information_schema.columns WHERE table_name = 'employee_drafts' AND column_name = 'id_number'")).Should().Be("''::text");
        // The constraint was added NOT VALID and then validated: it is in force on the existing rows too.
        (await Scalar<bool>(container, "SELECT convalidated FROM pg_constraint WHERE conname = 'ck_employee_drafts_id_number_enc_shape'")).Should().BeTrue();

        // Junk is refused; an envelope of the documented shape (format 1, DEK version 1, long enough) is accepted.
        var junk = async () => await Exec(container, "UPDATE employee_drafts SET id_number_enc = '\\x0001'::bytea WHERE english_name = 'Before'");
        await junk.Should().ThrowAsync<PostgresException>().Where(e => e.ConstraintName == "ck_employee_drafts_id_number_enc_shape");
        await Exec(container, $"UPDATE employee_drafts SET id_number_enc = '\\x0101{new string('a', 80)}'::bytea WHERE english_name = 'Before'");

        await migrator.MigrateAsync(previous);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'employee_drafts' AND column_name IN ('id_number', 'id_number_enc')"))
            .Should().Be(0);
        (await Scalar<long>(container, "SELECT count(*) FROM employee_drafts WHERE english_name = 'Before'")).Should().Be(1, "rolling back removes the columns, not the drafts");

        await migrator.MigrateAsync(thisMigration);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'employee_drafts' AND column_name IN ('id_number', 'id_number_enc')"))
            .Should().Be(2);
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
}
