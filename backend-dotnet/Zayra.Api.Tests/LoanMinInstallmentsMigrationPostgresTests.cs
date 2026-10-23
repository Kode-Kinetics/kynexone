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
/// 20261023155309_LoanMinInstallments on the real migration chain and real PostgreSQL: applies over an existing loan type
/// and company policies (all get 1 = no minimum) — including a legacy policy holding max_installments 0, the column's
/// database default — the CHECKs refuse a minimum below 1 or above the maximum, Down removes both columns and it re-applies.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LoanMinInstallmentsMigrationPostgresTests
{
    private const string ThisMigration = "20261023155309_LoanMinInstallments";
    private const string PreviousMigration = "20261022183516_AddMfaTrustedDevices";
    private const string CheckViolation = "23514";

    [Fact]
    public async Task Migration_UpgradesInPlace_EnforcesItsChecks_RollsBack_AndReapplies()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        all.Should().Contain(ThisMigration);
        all.IndexOf(ThisMigration).Should().Be(all.IndexOf(PreviousMigration) + 1);

        await migrator.MigrateAsync(PreviousMigration);
        // Existing rows, written before the column existed.
        const string typeId = "'30000000-0000-0000-0000-0000000000c1'", tenantId = "'10000000-0000-0000-0000-0000000000c1'";
        await Exec(container, await InsertRowSql(container, "loan_types", new() { ["id"] = typeId, ["tenant_id"] = tenantId, ["is_interest_free"] = "true", ["interest_rate"] = "0", ["max_installments"] = "12" }));
        await Exec(container, await InsertRowSql(container, "loan_policies", new() { ["tenant_id"] = tenantId, ["loan_type_id"] = typeId, ["max_installments"] = "12", ["is_offered"] = "true", ["created_by_offering_switch"] = "false" }));
        // A legacy policy holding the column's database default (max_installments 0) must not fail the upgrade.
        await Exec(container, await InsertRowSql(container, "loan_policies", new() { ["tenant_id"] = tenantId, ["loan_type_id"] = typeId, ["max_installments"] = "0", ["is_offered"] = "true", ["created_by_offering_switch"] = "false" }));

        await migrator.MigrateAsync(ThisMigration);
        (await Scalar<int>(container, "SELECT min_installments FROM loan_types")).Should().Be(1, "an existing type keeps no minimum");
        (await Scalar<long>(container, "SELECT count(*) FROM loan_policies WHERE min_installments = 1")).Should().Be(2, "existing policies keep no minimum");
        await Exec(container, "UPDATE loan_policies SET is_active = false WHERE max_installments = 0"); // the legacy row stays updatable

        var typeZero = async () => await Exec(container, "UPDATE loan_types SET min_installments = 0");
        (await typeZero.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(CheckViolation);
        var typeAboveMax = async () => await Exec(container, "UPDATE loan_types SET min_installments = 13");
        (await typeAboveMax.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(CheckViolation);
        await Exec(container, "UPDATE loan_types SET min_installments = 12");
        var policyAboveMax = async () => await Exec(container, "UPDATE loan_policies SET min_installments = 13 WHERE max_installments = 12");
        (await policyAboveMax.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(CheckViolation);
        var policyZero = async () => await Exec(container, "UPDATE loan_policies SET min_installments = 0 WHERE max_installments = 12");
        (await policyZero.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(CheckViolation);
        await Exec(container, "UPDATE loan_policies SET min_installments = 12 WHERE max_installments = 12");

        await migrator.MigrateAsync(PreviousMigration);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name IN ('loan_types','loan_policies') AND column_name = 'min_installments'"))
            .Should().Be(0);

        await migrator.MigrateAsync(ThisMigration);
        (await Scalar<long>(container, "SELECT count(*) FROM loan_policies WHERE min_installments = 1")).Should().Be(2);
    }

    /// <summary>An INSERT filling every NOT NULL column without a default with a neutral value of its type, plus overrides,
    /// so the test does not have to track every column the two tables have gained over time.</summary>
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
                    "date" => "current_date",
                    "jsonb" or "json" => "'{}'",
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
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
