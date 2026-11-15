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
/// The salary-advance payout migration is expand-only: five nullable columns on <c>salary_advances</c> (when the advance was paid, the
/// payment reference, who paid it, when it was cancelled and why). An advance that already exists, including an Active one created by
/// the old approve-and-disburse flow, keeps every value it had and reads NULL in the new columns, which is how a legacy Active advance
/// is recognised as paid. Real migration chain, real PostgreSQL, applied, rolled back and re-applied.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SalaryAdvancePayoutMigrationPostgresTests
{
    private const string MigrationSuffix = "_AddSalaryAdvancePayoutFields";

    private static readonly (string Column, string Type)[] Added =
    {
        ("paid_at_utc", "timestamp with time zone"),
        ("payment_reference", "character varying"),
        ("paid_by_user_id", "uuid"),
        ("cancelled_at_utc", "timestamp with time zone"),
        ("cancel_reason", "character varying"),
    };

    [Fact]
    public async Task AddsFiveNullableColumns_KeepsALegacyActiveAdvanceIntact_AndRollsBackCleanly()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var thisMigration = all.SingleOrDefault(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        thisMigration.Should().NotBeNull("the salary-advance payout migration exists");
        var previous = all[all.IndexOf(thisMigration!) - 1];

        await migrator.MigrateAsync(previous);
        var tenant = Guid.NewGuid();
        var legacy = Guid.NewGuid();
        // An advance approved under the old flow: Active, balance set, journal already posted at approval.
        await Exec(container,
            "INSERT INTO salary_advances (id, tenant_id, employee_id, employee_name, advance_number, requested_amount, approved_amount, " +
            "repayment_type, installments, installment_amount, total_repaid, outstanding_balance, reason, status, is_locked_by_payroll, " +
            "is_deleted, created_at_utc) VALUES " +
            $"('{legacy}', '{tenant}', gen_random_uuid(), 'Legacy', 'ADV-2026-00001', 2000, 2000, 'Installments', 2, 1000, 0, 2000, '', " +
            "'Active', false, false, now())");

        await migrator.MigrateAsync(thisMigration);

        foreach (var (column, type) in Added)
        {
            (await Scalar<string>(container,
                $"SELECT data_type FROM information_schema.columns WHERE table_name = 'salary_advances' AND column_name = '{column}'"))
                .Should().Be(type, $"{column} is added with its declared type");
            (await Scalar<string>(container,
                $"SELECT is_nullable FROM information_schema.columns WHERE table_name = 'salary_advances' AND column_name = '{column}'"))
                .Should().Be("YES", $"{column} is nullable, so the old code and existing rows are unaffected");
        }
        (await Scalar<int>(container,
            "SELECT character_maximum_length FROM information_schema.columns WHERE table_name = 'salary_advances' AND column_name = 'payment_reference'"))
            .Should().Be(100);
        (await Scalar<int>(container,
            "SELECT character_maximum_length FROM information_schema.columns WHERE table_name = 'salary_advances' AND column_name = 'cancel_reason'"))
            .Should().Be(1000);

        (await Scalar<string>(container, $"SELECT status FROM salary_advances WHERE id = '{legacy}'")).Should().Be("Active");
        (await Scalar<decimal>(container, $"SELECT outstanding_balance FROM salary_advances WHERE id = '{legacy}'")).Should().Be(2000m);
        (await Scalar<long>(container,
            $"SELECT count(*) FROM salary_advances WHERE id = '{legacy}' AND paid_at_utc IS NULL AND payment_reference IS NULL " +
            "AND paid_by_user_id IS NULL AND cancelled_at_utc IS NULL AND cancel_reason IS NULL"))
            .Should().Be(1, "a legacy Active advance carries no payout evidence and is not backfilled");

        await migrator.MigrateAsync(previous);
        (await Scalar<long>(container,
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'salary_advances' AND column_name IN " +
            "('paid_at_utc','payment_reference','paid_by_user_id','cancelled_at_utc','cancel_reason')"))
            .Should().Be(0, "the rollback removes exactly the five columns");
        (await Scalar<string>(container, $"SELECT status FROM salary_advances WHERE id = '{legacy}'")).Should().Be("Active");

        await migrator.MigrateAsync(thisMigration);
        (await Scalar<long>(container,
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'salary_advances' AND column_name IN " +
            "('paid_at_utc','payment_reference','paid_by_user_id','cancelled_at_utc','cancel_reason')"))
            .Should().Be(5, "the migration re-applies after a rollback");
    }

    /// <summary>Review P2-2: one bank reference evidences one payout in a company, enforced by the database and not only by a check
    /// the controller makes before it writes. Two partial unique indexes, created and dropped with the columns: one for an advance
    /// that has a company, one for the legacy advance that has none (NULL is distinct in a unique index).</summary>
    [Fact]
    public async Task AddsThePartialUniqueReferenceIndexes_AndRollsThemBackWithTheColumns()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var thisMigration = all.Single(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        var previous = all[all.IndexOf(thisMigration) - 1];

        await migrator.MigrateAsync(thisMigration);

        var company = await Scalar<string>(container,
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'salary_advances' AND indexname = 'ux_salary_advances_payment_reference'");
        company.Should().Contain("UNIQUE").And.Contain("(tenant_id, company_id, payment_reference)")
            .And.Contain("WHERE (payment_reference IS NOT NULL)");
        var legacy = await Scalar<string>(container,
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'salary_advances' AND indexname = 'ux_salary_advances_payment_reference_unattributed'");
        legacy.Should().Contain("UNIQUE").And.Contain("(tenant_id, payment_reference)")
            .And.Contain("company_id IS NULL").And.Contain("payment_reference IS NOT NULL");

        await migrator.MigrateAsync(previous);
        (await Scalar<long>(container,
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'salary_advances' AND indexname LIKE 'ux_salary_advances_payment_reference%'"))
            .Should().Be(0, "the rollback drops both indexes");
        await migrator.MigrateAsync(thisMigration);
        (await Scalar<long>(container,
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'salary_advances' AND indexname LIKE 'ux_salary_advances_payment_reference%'"))
            .Should().Be(2, "the migration re-applies after a rollback");
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
        return r is null or DBNull ? default! : (T)Convert.ChangeType(r, typeof(T));
    }
}
