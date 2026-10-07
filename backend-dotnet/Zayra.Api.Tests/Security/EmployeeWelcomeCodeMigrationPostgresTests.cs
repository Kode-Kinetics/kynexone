using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// 20261008000300_AddEmployeeWelcomeCodes on the real migration chain: applies to a fresh database, applies as an
/// upgrade over existing employee links (they get no code and zero attempts), rolls back and re-applies.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EmployeeWelcomeCodeMigrationPostgresTests
{
    private const string ThisMigration = "20261008000300_AddEmployeeWelcomeCodes";

    [Fact]
    public async Task FreshAndUpgrade_AddOnlyTheWelcomeCodeColumns_AndDownRemovesThem()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        all.Last().Should().Be(ThisMigration, "it is the newest migration");
        var before = all[all.IndexOf(ThisMigration) - 1];

        // Upgrade: an existing link is there before the columns are.
        await migrator.MigrateAsync(before);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO employee_user_accounts (id, tenant_id, employee_id, access_mode, is_primary, status, requires_password_setup, invitation_token_hash, login_disabled_reason, created_at_utc, is_deleted) " +
            "VALUES (gen_random_uuid(), gen_random_uuid(), 1, 'ESSOnly', true, 'Active', false, '', '', now(), false)");
        await migrator.MigrateAsync();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        var columns = await Columns(db);
        columns.Should().Contain(["welcome_code_hash", "welcome_code_issued_at_utc", "welcome_code_expires_at_utc",
            "welcome_code_issued_by", "welcome_code_failed_attempts", "welcome_code_redeemed_at_utc"]);
        (await db.Database.SqlQueryRaw<int>("SELECT welcome_code_failed_attempts AS \"Value\" FROM employee_user_accounts").SingleAsync()).Should().Be(0);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM employee_user_accounts WHERE welcome_code_hash IS NULL").SingleAsync()).Should().Be(1);

        await migrator.MigrateAsync(before);
        (await Columns(db)).Should().NotContain(c => c.StartsWith("welcome_code_"));
        await migrator.MigrateAsync();
        (await Columns(db)).Count(c => c.StartsWith("welcome_code_")).Should().Be(6);
    }

    private static Task<List<string>> Columns(ZayraDbContext db) =>
        db.Database.SqlQueryRaw<string>(
                "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'employee_user_accounts'")
            .ToListAsync();
}
