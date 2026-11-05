using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests.Recon;

/// <summary>
/// 20261104090000_AddPayrollManpowerReconSignoff against a real migrated database: the column lands with its default on existing
/// runs, the two tables carry their keys, checks and the partial unique index with its predicate, and Down removes all of it.
/// Rows are written with raw SQL naming only columns that exist at that migration.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ManpowerReconMigrationPostgresTests
{
    private const string MigrationId = "20261104090000_AddPayrollManpowerReconSignoff";

    [Fact]
    public async Task Up_AddsTheColumnAndTheTables_WithTheirGuards_AndDownRemovesThem()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToList();
        migrations.Should().Contain(MigrationId);
        var previous = migrations[migrations.IndexOf(MigrationId) - 1];
        await migrator.MigrateAsync(previous);

        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();

        // An existing run, written before the column exists.
        var tenant = Guid.NewGuid();
        var run = Guid.NewGuid();
        await Exec(conn, @"INSERT INTO payroll_runs (id, tenant_id, year, month, status, run_type, created_at_utc, employee_count, erp_posting_status,
                               nets_prior_receivable, settles_arrears, settles_final_settlements, total_gross_salary, total_deductions,
                               total_net_salary, total_employer_statutory_cost)
                           VALUES (@r, @t, 2026, 9, 'Locked', 'Regular', now(), 0, 'NotReady', FALSE, TRUE, FALSE, 0, 0, 0, 0)",
            ("r", run), ("t", tenant));
        (await Scalar(conn, "SELECT to_regclass('payroll_run_recon_signoffs')::text")).Should().BeNull("not yet");

        await migrator.MigrateAsync(MigrationId);

        // The new column is non-null with a default of 0, and the existing run took it.
        (await Scalar(conn, $"SELECT process_version::text FROM payroll_runs WHERE id = '{run}'")).Should().Be("0");
        (await Scalar(conn, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'payroll_runs' AND column_name = 'process_version'")).Should().Be("NO");
        (await Scalar(conn, "SELECT to_regclass('payroll_run_recon_signoffs')::text")).Should().Be("payroll_run_recon_signoffs");
        (await Scalar(conn, "SELECT to_regclass('payroll_run_recon_explanations')::text")).Should().Be("payroll_run_recon_explanations");

        // The partial unique index keeps its predicate.
        var def = await Scalar(conn, "SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_payroll_run_recon_signoffs__run_active'");
        def.Should().Contain("UNIQUE").And.Contain("WHERE").And.Contain("Active");

        string Insert(string status, string? reason, string? at) =>
            $@"INSERT INTO payroll_run_recon_signoffs (id, tenant_id, company_id, payroll_run_id, process_version, snapshot_hash, snapshot_json,
                   signed_off_by_name, signed_off_at_utc, status, superseded_reason, superseded_at_utc)
               VALUES ('{Guid.NewGuid()}', '{tenant}', NULL, '{run}', 0, '{new string('e', 64)}', '{{}}', 'x', now(), '{status}',
                   {(reason is null ? "NULL" : $"'{reason}'")}, {(at is null ? "NULL" : at)})";
        await Exec(conn, Insert("Active", null, null));
        await Exec(conn, Insert("Superseded", "Reopened", "now()"));
        await Exec(conn, Insert("Superseded", "Voided", "now()"));
        (await Throws(conn, Insert("Active", null, null))).Should().Be("23505");
        (await Throws(conn, Insert("Active", "Voided", "now()"))).Should().Be("23514");
        (await Throws(conn, Insert("Superseded", null, null))).Should().Be("23514");
        (await Throws(conn, Insert("Superseded", "Because", "now()"))).Should().Be("23514");
        (await Throws(conn, Insert("Gone", null, null))).Should().Be("23514");

        // The run cannot be deleted while a sign-off references it.
        (await Throws(conn, $"DELETE FROM payroll_runs WHERE id = '{run}'")).Should().Be("23503");

        // Down: the tables and the column go; the run survives.
        await migrator.MigrateAsync(previous);
        (await Scalar(conn, "SELECT to_regclass('payroll_run_recon_signoffs')::text")).Should().BeNull();
        (await Scalar(conn, "SELECT to_regclass('payroll_run_recon_explanations')::text")).Should().BeNull();
        (await Scalar(conn, "SELECT count(*)::text FROM information_schema.columns WHERE table_name = 'payroll_runs' AND column_name = 'process_version'")).Should().Be("0");
        (await Scalar(conn, $"SELECT count(*)::text FROM payroll_runs WHERE id = '{run}'")).Should().Be("1");
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : Convert.ToString(v);
    }

    private static async Task<string> Throws(NpgsqlConnection conn, string sql)
    {
        try
        {
            await Exec(conn, sql);
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
        return "no error";
    }
}
