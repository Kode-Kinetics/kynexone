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
/// 20261105020000_LoanBatchApproverMode on the real migration chain and real PostgreSQL: existing policies upgrade in place
/// to the strict mode with no cap, the CHECK refuses an unknown mode, a negative cap and a cap without self-approval, Down
/// removes both columns and the constraint, and it re-applies.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LoanBatchApproverModeMigrationPostgresTests
{
    private const string ThisMigration = "20261105020000_LoanBatchApproverMode";
    private const string CheckViolation = "23514";

    [Fact]
    public async Task Migration_UpgradesInPlaceToStrict_EnforcesItsCheck_RollsBack_AndReapplies()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        all.Should().Contain(ThisMigration);
        var previous = all[all.IndexOf(ThisMigration) - 1];

        await migrator.MigrateAsync(previous);
        const string typeId = "'30000000-0000-0000-0000-0000000000d1'", tenantId = "'10000000-0000-0000-0000-0000000000d1'";
        await Exec(container, await InsertRowSql(container, "loan_types", new() { ["id"] = typeId, ["tenant_id"] = tenantId, ["is_interest_free"] = "true", ["interest_rate"] = "0", ["max_installments"] = "12" }));
        await Exec(container, await InsertRowSql(container, "loan_policies", new() { ["tenant_id"] = tenantId, ["loan_type_id"] = typeId, ["max_installments"] = "12", ["is_offered"] = "true", ["created_by_offering_switch"] = "false" }));

        await migrator.MigrateAsync(ThisMigration);
        (await Scalar<string>(container, "SELECT batch_approver_mode FROM loan_policies")).Should().Be("SeparateApprover", "an existing policy stays strict");
        (await Scalar<decimal>(container, "SELECT self_approval_max_amount FROM loan_policies")).Should().Be(0m);

        foreach (var bad in new[]
                 {
                     "UPDATE loan_policies SET batch_approver_mode = 'Anyone'",
                     "UPDATE loan_policies SET self_approval_max_amount = 100",                                       // cap without self-approval
                     "UPDATE loan_policies SET batch_approver_mode = 'SingleOperator', self_approval_max_amount = -1",
                 })
        {
            var act = async () => await Exec(container, bad);
            (await act.Should().ThrowAsync<PostgresException>(bad)).Which.SqlState.Should().Be(CheckViolation);
        }
        await Exec(container, "UPDATE loan_policies SET batch_approver_mode = 'SingleOperator', self_approval_max_amount = 2500");

        await migrator.MigrateAsync(previous);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'loan_policies' AND column_name IN ('batch_approver_mode','self_approval_max_amount')"))
            .Should().Be(0);
        (await Scalar<long>(container, "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_loan_policies__batch_approver'")).Should().Be(0);

        await migrator.MigrateAsync(ThisMigration);
        (await Scalar<string>(container, "SELECT batch_approver_mode FROM loan_policies")).Should().Be("SeparateApprover", "a re-apply is strict again");
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
