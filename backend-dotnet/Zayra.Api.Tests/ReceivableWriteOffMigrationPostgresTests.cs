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
/// The receivable write-off migration is additive. The request kind CHECK is widened (existing rows still satisfy it), account 5114
/// and its posting driver are seeded idempotently for tenants that already carry a tenant-default chart, a tenant that already uses
/// 5114 for something else gets nothing pointed at it, and the migration reverses cleanly. Real migration chain, real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReceivableWriteOffMigrationPostgresTests
{
    private const string MigrationSuffix = "_AddReceivableWriteOff";

    [Fact]
    public async Task SeedsAccountDriverAndMapping_LeavesACollidingChartAlone_WidensTheKindCheck_AndRollsBack()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var thisMigration = all.SingleOrDefault(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        thisMigration.Should().NotBeNull("the receivable write-off migration exists");
        var previous = all[all.IndexOf(thisMigration!) - 1];

        await migrator.MigrateAsync(previous);
        var plain = Guid.NewGuid();
        var colliding = Guid.NewGuid();
        // Both tenants already have a tenant-default chart and the system driver set; one of them uses 5114 for something else.
        foreach (var tenant in new[] { plain, colliding })
        {
            await Exec(container, await InsertRowSql(container, "gl_accounts", new() { ["tenant_id"] = $"'{tenant}'", ["company_id"] = "NULL", ["code"] = "'1000'", ["name"] = "'Cash/Bank'", ["account_type"] = "'Asset'", ["is_active"] = "true" }));
            await Exec(container, await InsertRowSql(container, "gl_drivers", new() { ["tenant_id"] = $"'{tenant}'", ["company_id"] = "NULL", ["key"] = "'CASH_BANK'", ["is_system"] = "true" }));
        }
        await Exec(container, await InsertRowSql(container, "gl_accounts", new() { ["tenant_id"] = $"'{colliding}'", ["company_id"] = "NULL", ["code"] = "'5114'", ["name"] = "'Something else'", ["account_type"] = "'Expense'", ["is_active"] = "true" }));
        var kindCheck = async () => await Exec(container, await InsertRowSql(container, "gl_adjustment_requests", new() { ["kind"] = "'ReceivableWriteOff'", ["status"] = "'Pending'", ["period"] = "'2026-11'" }));
        await kindCheck.Should().ThrowAsync<PostgresException>().Where(e => e.ConstraintName == "ck_gl_adjustment_requests__kind", "the old CHECK does not know the new kind");

        await migrator.MigrateAsync(thisMigration);

        (await Scalar<long>(container, $"SELECT count(*) FROM gl_accounts WHERE tenant_id = '{plain}' AND code = '5114' AND name = 'Employee Receivable Write-off Expense' AND account_type = 'Expense'")).Should().Be(1);
        (await Scalar<long>(container, $"SELECT count(*) FROM gl_drivers WHERE tenant_id = '{plain}' AND key = 'RECEIVABLE_WRITE_OFF_EXPENSE' AND category = 'Balancing' AND is_system")).Should().Be(1);
        (await Scalar<long>(container, $"SELECT count(*) FROM gl_account_mappings WHERE tenant_id = '{plain}' AND driver_key = 'RECEIVABLE_WRITE_OFF_EXPENSE'")).Should().Be(1);
        (await Scalar<long>(container, $"SELECT count(*) FROM gl_drivers WHERE tenant_id = '{colliding}' AND key = 'RECEIVABLE_WRITE_OFF_EXPENSE'")).Should().Be(0, "a tenant that uses 5114 for something else is left for manual mapping");
        (await Scalar<long>(container, $"SELECT count(*) FROM gl_account_mappings WHERE tenant_id = '{colliding}' AND driver_key = 'RECEIVABLE_WRITE_OFF_EXPENSE'")).Should().Be(0);
        (await Scalar<string>(container, $"SELECT name FROM gl_accounts WHERE tenant_id = '{colliding}' AND code = '5114'")).Should().Be("Something else", "the tenant's own account is untouched");
        await Exec(container, await InsertRowSql(container, "gl_adjustment_requests", new() { ["kind"] = "'ReceivableWriteOff'", ["status"] = "'Pending'", ["period"] = "'2026-11'", ["subject_id"] = "gen_random_uuid()" }));

        // Re-applying after a rollback seeds nothing twice.
        await migrator.MigrateAsync(previous);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'gl_adjustment_requests' AND column_name = 'subject_id'")).Should().Be(0);
        (await Scalar<long>(container, $"SELECT count(*) FROM gl_accounts WHERE tenant_id = '{plain}' AND code = '5114'")).Should().Be(0, "the rollback removes only what the migration seeded");
        (await Scalar<long>(container, $"SELECT count(*) FROM gl_accounts WHERE tenant_id = '{colliding}' AND code = '5114'")).Should().Be(1);
        await migrator.MigrateAsync(thisMigration);
        (await Scalar<long>(container, $"SELECT count(*) FROM gl_account_mappings WHERE tenant_id = '{plain}' AND driver_key = 'RECEIVABLE_WRITE_OFF_EXPENSE'")).Should().Be(1);
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
                    "date" => "current_date",
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
