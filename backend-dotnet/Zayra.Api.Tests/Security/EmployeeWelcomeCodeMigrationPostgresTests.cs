using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// 20261008000700_AddEmployeeWelcomeCodes on the real migration chain: applies to a fresh database, applies as an
/// upgrade over existing employee links (they get no code and zero attempts), rolls back and re-applies.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EmployeeWelcomeCodeMigrationPostgresTests
{
    private const string ThisMigration = "20261008000700_AddEmployeeWelcomeCodes";
    private const string PreviousMigration = "20261008000600_MakeWaiverConsumptionAConcurrencyToken";

    [Fact]
    public async Task FreshAndUpgrade_AddOnlyTheWelcomeCodeColumns_AndDownRemovesThem()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        // Contained and ordered after the selfie hardening it was rebased onto. NOT "the newest": the next migration
        // anyone adds must not break this test.
        all.Should().Contain(ThisMigration);
        all.IndexOf(ThisMigration).Should().BeGreaterThan(all.IndexOf(PreviousMigration))
            .And.BeGreaterThan(0);
        all.Should().Contain(PreviousMigration);
        var before = all[all.IndexOf(ThisMigration) - 1];

        // Upgrade: an existing link is there before the columns are.
        await migrator.MigrateAsync(before);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO employee_user_accounts (id, tenant_id, employee_id, access_mode, is_primary, status, requires_password_setup, invitation_token_hash, login_disabled_reason, created_at_utc, is_deleted) " +
            "VALUES (gen_random_uuid(), gen_random_uuid(), 1, 'ESSOnly', true, 'Active', false, '', '', now(), false)");

        // A long transaction holding companies: the DDL gives up after lock_timeout (55P03) instead of queueing
        // behind it (and blocking every later reader of companies). The failed migration leaves nothing behind.
        await using (var blocker = new Npgsql.NpgsqlConnection(container.GetConnectionString()))
        {
            await blocker.OpenAsync();
            await using var tx = await blocker.BeginTransactionAsync();
            await using (var hold = new Npgsql.NpgsqlCommand("SELECT count(*) FROM companies", blocker, tx)) await hold.ExecuteScalarAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var started = System.Diagnostics.Stopwatch.StartNew();
            var failure = await Record.ExceptionAsync(() => migrator.MigrateAsync(null, cts.Token));
            started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "lock_timeout must end the wait, not the test's own cancellation");
            failure.Should().NotBeNull();
            Chain(failure!).OfType<Npgsql.PostgresException>().Should().Contain(e => e.SqlState == "55P03", failure!.ToString());
            await tx.RollbackAsync();
        }
        (await Columns(db)).Should().NotContain(c => c.StartsWith("welcome_code_"), "the timed-out migration rolled back whole");

        // Existing roles, before the new keys exist: built-in HR Manager / HR Officer, a custom security.manage role,
        // and a built-in role that gets nothing.
        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenant, Name = "Grant Tenant", Slug = $"grant-{tenant:N}"[..20], IsActive = true });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO permissions (id, permission_key, module, description, created_at_utc) VALUES (gen_random_uuid(), 'security.manage', 'Security', 'x', now()) ON CONFLICT DO NOTHING");
        var hrManager = await AddRole(db, tenant, "HR Manager", system: true);
        var hrOfficer = await AddRole(db, tenant, "HR Officer", system: true);
        var consoleAdmin = await AddRole(db, tenant, "Console Admin", system: false, "security.manage");
        var payrollOfficer = await AddRole(db, tenant, "Payroll Officer", system: true);

        await migrator.MigrateAsync();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        var columns = await Columns(db);
        columns.Should().Contain(["welcome_code_hash", "welcome_code_issued_at_utc", "welcome_code_expires_at_utc",
            "welcome_code_issued_by", "welcome_code_failed_attempts", "welcome_code_redeemed_at_utc"]);
        (await db.Database.SqlQueryRaw<int>("SELECT welcome_code_failed_attempts AS \"Value\" FROM employee_user_accounts").SingleAsync()).Should().Be(0);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM employee_user_accounts WHERE welcome_code_hash IS NULL").SingleAsync()).Should().Be(1);

        // An ACTIVE email domain belongs to one tenant: a second tenant is refused; the same tenant may share it.
        string Insert(Guid tenant, string name, bool active = true) =>
            "INSERT INTO companies (id, tenant_id, legal_name_en, legal_name_ar, trade_name, country_code, jurisdiction, registration_number, " +
            "tax_number, wps_employer_id, gosi_employer_id, qiwa_establishment_id, default_currency, email_domain, work_email_pattern, is_active, " +
            "approval_status, created_at_utc, is_deleted) VALUES (gen_random_uuid(), '" + tenant + "', '" + name + "', '', '', 'SA', 'SA', '" + name +
            "', '', '', '', '', 'SAR', 'Shared.Test', 'first.last', " + (active ? "true" : "false") + ", 'Active', now(), false)";
        var a = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(Insert(a, "A1"));
        await db.Database.ExecuteSqlRawAsync(Insert(a, "A2"));
        await db.Database.ExecuteSqlRawAsync(Insert(Guid.NewGuid(), "B0", active: false));
        var clash = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(Insert(Guid.NewGuid(), "B1")));
        clash.SqlState.Should().Be("23P01");

        // The one-shot grant (PR #210 contract: issue = HR Manager + HR Officer, reset = HR Manager; plus every
        // security.manage role, so PrivilegeCeiling keeps its reach over those roles).
        (await AccessKeys(db, hrManager)).Should().BeEquivalentTo(["employees.access.issue", "employees.access.reset"]);
        (await AccessKeys(db, hrOfficer)).Should().BeEquivalentTo(["employees.access.issue"]);
        (await AccessKeys(db, consoleAdmin)).Should().BeEquivalentTo(["employees.access.issue", "employees.access.reset"]);
        (await AccessKeys(db, payrollOfficer)).Should().BeEmpty();

        // The boot seeder does NOT re-grant: an Admin's later removal of a key from a built-in role stands.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM role_permissions WHERE role_id = {0} AND permission_id = (SELECT id FROM permissions WHERE permission_key = 'employees.access.issue')", hrOfficer);
        await new Zayra.Api.Infrastructure.Seed.AuthSeeder(db).SeedAsync();
        (await AccessKeys(db, hrOfficer)).Should().BeEmpty();

        await migrator.MigrateAsync(before);
        (await Columns(db)).Should().NotContain(c => c.StartsWith("welcome_code_"));
        await migrator.MigrateAsync();
        (await Columns(db)).Count(c => c.StartsWith("welcome_code_")).Should().Be(6);
        // Re-applied: add-only and idempotent (no duplicates; HR Manager unchanged).
        (await AccessKeys(db, hrManager)).Should().BeEquivalentTo(["employees.access.issue", "employees.access.reset"]);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM permissions WHERE permission_key LIKE 'employees.access.%'").SingleAsync()).Should().Be(2);
    }

    private static IEnumerable<Exception> Chain(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException) yield return x;
    }

    private static async Task<Guid> AddRole(ZayraDbContext db, Guid tenant, string name, bool system, params string[] keys)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO roles (id, tenant_id, name, normalized_name, description, is_system, is_editable, is_active, is_deleted, authority_level, created_at_utc) " +
            "VALUES ({0}, {1}, {2}, {3}, '', {4}, true, true, false, 10, now())", id, tenant, name, name.ToUpperInvariant(), system);
        foreach (var key in keys)
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO role_permissions (role_id, permission_id) SELECT {0}, id FROM permissions WHERE permission_key = {1}", id, key);
        return id;
    }

    private static Task<List<string>> AccessKeys(ZayraDbContext db, Guid roleId) =>
        db.Database.SqlQueryRaw<string>(
                "SELECT p.permission_key AS \"Value\" FROM role_permissions rp JOIN permissions p ON p.id = rp.permission_id " +
                "WHERE rp.role_id = {0} AND p.permission_key LIKE 'employees.access.%'", roleId)
            .ToListAsync();

    private static Task<List<string>> Columns(ZayraDbContext db) =>
        db.Database.SqlQueryRaw<string>(
                "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'employee_user_accounts'")
            .ToListAsync();
}
