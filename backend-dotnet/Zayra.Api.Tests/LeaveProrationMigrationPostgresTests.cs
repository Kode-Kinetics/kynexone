using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
public sealed class LeaveProrationMigrationPostgresTests
{
    private const string MigrationId = "20261009185413_AddLeavePartialMonthProration";

    [Fact]
    public async Task Upgrade_PreservesLegacyPolicy_AndRollbackDoesNotDiscardConfiguredProration()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToList();
        migrations.Should().Contain(MigrationId);
        var previous = migrations[migrations.IndexOf(MigrationId) - 1];
        await migrator.MigrateAsync(previous);

        var policyId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO leave_policies
                (id, tenant_id, name, leave_type_id, country_code, department_name, grade, employment_type,
                 contract_type, gender, applies_on_probation, annual_entitlement_days, accrual_method,
                 carry_forward_max, carry_forward_expiry, encashment_allowed, encashment_max_days,
                 minimum_days_per_request, maximum_days_per_request, notice_required_days,
                 weekends_included, public_holidays_included, payroll_impact,
                 allows_hajj_beyond_statutory_eligibility, status, created_at_utc, updated_at_utc)
            VALUES
                ({policyId}, {tenantId}, 'Legacy full-month policy', {typeId}, 'AE', '', '', '', '', '',
                 false, 24, 'Monthly', 0, 0, false, 0, 1, 30, 0, false, false, 'Full', false,
                 'Active', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            """);

        await migrator.MigrateAsync(MigrationId);
        await migrator.MigrateAsync(MigrationId); // replay must be a no-op
        // Raw SQL, not db.LeavePolicies: the current model selects columns (company_id, legacy_group_default, ...)
        // that later migrations add and this migration point does not have yet.
        (await ProrateAsync()).Should().BeFalse();
        (await db.Database.SqlQuery<decimal>($"SELECT annual_entitlement_days AS \"Value\" FROM leave_policies WHERE id = {policyId}").SingleAsync()).Should().Be(24m);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE leave_policies SET prorate_partial_months = true WHERE id = {policyId}");
        (await ProrateAsync()).Should().BeTrue();

        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync(previous));
        refused.SqlState.Should().Be(Npgsql.PostgresErrorCodes.RaiseException);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE leave_policies SET prorate_partial_months = false WHERE id = {policyId}");
        await migrator.MigrateAsync(previous);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'leave_policies' AND column_name = 'prorate_partial_months'").SingleAsync()).Should().Be(0);
        await migrator.MigrateAsync(MigrationId);
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(MigrationId);
        (await ProrateAsync()).Should().BeFalse();

        Task<bool> ProrateAsync() => db.Database
            .SqlQuery<bool>($"SELECT prorate_partial_months AS \"Value\" FROM leave_policies WHERE id = {policyId}").SingleAsync();
    }
}
