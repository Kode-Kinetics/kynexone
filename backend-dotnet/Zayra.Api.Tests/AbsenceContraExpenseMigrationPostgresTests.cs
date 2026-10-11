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
/// 20261010201817_RouteAbsenceDeductionsToContraExpense against a real migrated database: untouched
/// seeded rows move to the contra-expense accounts, anything a tenant customised is left alone, a tenant
/// that already uses 5008/5009/5199 for another account is left for manual mapping, switched-off system
/// drivers come back on, posted history is never rewritten, and Down restores the seeded liability routing.
///
/// <para>Every row is written and read with RAW SQL naming only the columns that exist at the previous
/// migration. Seeding through EF entities would emit the CURRENT model's columns (e.g. gl_accounts.
/// external_code, added by a later migration) into a database that does not have them yet (42703).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AbsenceContraExpenseMigrationPostgresTests
{
    private const string MigrationId = "20261010201817_RouteAbsenceDeductionsToContraExpense";

    [Fact]
    public async Task Up_MovesOnlyUntouchedSeededRows_NeverTouchesPostedHistory_AndDownRestores()
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

        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();

        // Tenant A: the pre-fix seed, untouched, with a system driver switched off on the old API.
        // Tenant B: customised — renamed 2105, re-coded the DED:LEAVE driver, and a company-level
        // DED:ATTENDANCE mapping onto the seeded 2104.
        // Tenant C: already uses 5008 for "Medical Insurance Expense" and 5199 as a liability.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var companyB = Guid.NewGuid();

        async Task<Guid> Acct(Guid t, string code, string name, string type)
        {
            var id = Guid.NewGuid();
            await Exec(conn, @"INSERT INTO gl_accounts (id, tenant_id, company_id, code, name, account_type, is_active, created_at_utc)
                               VALUES (@id, @t, NULL, @code, @name, @type, TRUE, now())",
                ("id", id), ("t", t), ("code", code), ("name", name), ("type", type));
            return id;
        }
        async Task Map(Guid t, Guid? co, string key, Guid accountId) =>
            await Exec(conn, $@"INSERT INTO gl_account_mappings (id, tenant_id, company_id, driver_key, account_id, is_active, created_at_utc)
                                VALUES (@id, @t, {(co is null ? "NULL" : "@co")}, @key, @acct, TRUE, now())",
                ("id", Guid.NewGuid()), ("t", t), ("co", co ?? Guid.Empty), ("key", key), ("acct", accountId));
        async Task Sys(Guid t, string key, string code, string name, bool active = true) =>
            await Exec(conn, @"INSERT INTO gl_drivers (id, tenant_id, company_id, key, label, category, posting_side, account_type,
                                   default_code, default_name, match_source, match_mode, match_component_code,
                                   emits_employer_expense_pair, paired_expense_driver_key, is_system, is_active, sort_order, created_at_utc)
                               VALUES (@id, @t, NULL, @key, @key, 'Deduction', 'CR', 'Liability', @code, @name, @src, 'Any', NULL,
                                   FALSE, NULL, TRUE, @active, 24, now())",
                ("id", Guid.NewGuid()), ("t", t), ("key", key), ("code", code), ("name", name),
                ("src", key == "DED:LEAVE" ? "Leave" : key == "DED:TAX" ? "Tax" : "Attendance"), ("active", active));

        var a2104 = await Acct(a, "2104", "Attendance Adjustment Payable", "Liability");
        var a2105 = await Acct(a, "2105", "Leave Deduction Payable", "Liability");
        var b2104 = await Acct(b, "2104", "Attendance Adjustment Payable", "Liability");
        var b2105 = await Acct(b, "2105", "Leave Deductions Owed To Fund", "Liability");
        var c2104 = await Acct(c, "2104", "Attendance Adjustment Payable", "Liability");
        await Acct(c, "5008", "Medical Insurance Expense", "Expense");
        await Acct(c, "5199", "Rounding Suspense", "Liability");
        await Map(a, null, "DED:ATTENDANCE", a2104);
        await Map(a, null, "DED:LEAVE", a2105);
        await Map(b, null, "DED:ATTENDANCE", b2104);
        await Map(b, companyB, "DED:ATTENDANCE", b2104);
        await Map(b, null, "DED:LEAVE", b2105);
        await Map(c, null, "DED:ATTENDANCE", c2104);
        await Sys(a, "DED:ATTENDANCE", "2104", "Attendance Adjustment Payable");
        await Sys(a, "DED:LEAVE", "2105", "Leave Deduction Payable");
        await Sys(a, "DED:TAX", "2102", "Income Tax Payable", active: false);
        await Sys(b, "DED:ATTENDANCE", "2104", "Attendance Adjustment Payable");
        await Sys(b, "DED:LEAVE", "2999", "Custom Leave");
        await Sys(c, "DED:ATTENDANCE", "2104", "Attendance Adjustment Payable");
        var historyId = Guid.NewGuid();
        await Exec(conn, @"INSERT INTO finance_gl_entries (id, tenant_id, company_id, source_module, source_entity_id, source_entity_ref,
                               event_type, debit_account, credit_account, amount, currency, period, entry_date, description,
                               erp_posting_status, is_reversed, posted_by_name, created_at_utc)
                           VALUES (@id, @t, NULL, 'Payroll', @src, '2026-08', 'Accrual', '', '2104 - Attendance Adjustment Payable',
                               120, 'SAR', '2026-08', DATE '2026-08-31', 'Payroll deduction: ATTENDANCE', 'NotReady', FALSE, '', now())",
            ("id", historyId), ("t", a), ("src", Guid.NewGuid()));

        await migrator.MigrateAsync(MigrationId);

        Task<string?> MappedCode(Guid t, Guid? co, string key) => Scalar<string>(conn,
            $@"SELECT x.code FROM gl_account_mappings m JOIN gl_accounts x ON x.id = m.account_id
               WHERE m.tenant_id = @t AND {(co is null ? "m.company_id IS NULL" : "m.company_id = @co")} AND m.driver_key = @key",
            ("t", t), ("co", co ?? Guid.Empty), ("key", key));
        Task<string?> DriverField(Guid t, string key, string column) => Scalar<string>(conn,
            $"SELECT {column}::text FROM gl_drivers WHERE tenant_id = @t AND company_id IS NULL AND key = @key",
            ("t", t), ("key", key));

        (await MappedCode(a, null, "DED:ATTENDANCE")).Should().Be("5008");
        (await MappedCode(a, null, "DED:LEAVE")).Should().Be("5009");
        (await DriverField(a, "DED:ATTENDANCE", "default_code")).Should().Be("5008");
        (await DriverField(a, "DED:ATTENDANCE", "account_type")).Should().Be("Expense");
        (await DriverField(a, "DED:LEAVE", "default_code")).Should().Be("5009");
        (await DriverField(a, "ROUNDING", "default_code")).Should().Be("5199");
        (await MappedCode(a, null, "ROUNDING")).Should().Be("5199");
        (await Scalar<string>(conn, "SELECT account_type FROM gl_accounts WHERE tenant_id = @t AND code = '5008'", ("t", a)))
            .Should().Be("Expense");
        (await DriverField(a, "DED:TAX", "is_active")).Should().Be("true",
            "a system rule switched off on the old API blocks Lock and the API can no longer switch it off, so Up switches it back on");

        (await MappedCode(b, null, "DED:ATTENDANCE")).Should().Be("5008", "B's group mapping still pointed at the untouched seeded 2104");
        (await MappedCode(b, companyB, "DED:ATTENDANCE")).Should().Be("2104", "a company-level mapping is a deliberate choice");
        (await MappedCode(b, null, "DED:LEAVE")).Should().Be("2105", "B renamed its 2105, so it is not the seeded row");
        (await DriverField(b, "DED:LEAVE", "default_code")).Should().Be("2999", "a re-coded driver is the tenant's decision");

        // C: 5008 and 5199 are other accounts — nothing is pointed at them and nothing is overwritten.
        (await MappedCode(c, null, "DED:ATTENDANCE")).Should().Be("2104", "C's 5008 is Medical Insurance, not the contra account");
        (await DriverField(c, "DED:ATTENDANCE", "default_code")).Should().Be("2104", "the driver default must not name C's medical-insurance account");
        (await DriverField(c, "DED:ATTENDANCE", "account_type")).Should().Be("Liability");
        (await Scalar<string>(conn, "SELECT name FROM gl_accounts WHERE tenant_id = @t AND company_id IS NULL AND code = '5008'", ("t", c)))
            .Should().Be("Medical Insurance Expense");
        (await Scalar<long>(conn, "SELECT count(*) FROM gl_accounts WHERE tenant_id = @t AND code IN ('5008', '5199')", ("t", c)))
            .Should().Be(2, "no duplicate or replacement account is created for a colliding code");
        (await DriverField(c, "ROUNDING", "default_code")).Should().BeNull("C's 5199 is a liability suspense account; ROUNDING is left for manual mapping");
        (await MappedCode(c, null, "ROUNDING")).Should().BeNull();
        (await Scalar<string>(conn, "SELECT code FROM gl_accounts WHERE tenant_id = @t AND code = '5009'", ("t", c)))
            .Should().Be("5009", "the code that does not collide is still added");

        (await Scalar<string>(conn, "SELECT credit_account FROM finance_gl_entries WHERE id = @id", ("id", historyId)))
            .Should().Be("2104 - Attendance Adjustment Payable", "posted history is never rewritten");
        (await Scalar<long>(conn, "SELECT count(*) FROM gl_accounts WHERE tenant_id = @t AND code IN ('2104', '2105')", ("t", a)))
            .Should().Be(2, "the old accounts stay in the chart; their balances are the accountant's to reclassify");

        await migrator.MigrateAsync(previous);
        (await MappedCode(a, null, "DED:ATTENDANCE")).Should().Be("2104");
        (await MappedCode(a, null, "DED:LEAVE")).Should().Be("2105");
        (await DriverField(a, "DED:ATTENDANCE", "default_code")).Should().Be("2104");
        (await DriverField(a, "DED:ATTENDANCE", "account_type")).Should().Be("Liability");
        (await DriverField(b, "DED:LEAVE", "default_code")).Should().Be("2999");
        (await DriverField(c, "DED:ATTENDANCE", "default_code")).Should().Be("2104");
        (await DriverField(a, "DED:TAX", "is_active")).Should().Be("true", "Down does not switch system rules back off");
        (await Scalar<long>(conn, "SELECT count(*) FROM gl_drivers WHERE key = 'ROUNDING'")).Should().Be(0);
        (await Scalar<long>(conn, "SELECT count(*) FROM gl_accounts WHERE tenant_id IN (@a, @b) AND code IN ('5008', '5009', '5199')", ("a", a), ("b", b)))
            .Should().Be(0, "Down removes the accounts it added once nothing maps to them");
        (await Scalar<long>(conn, "SELECT count(*) FROM gl_accounts WHERE tenant_id = @t AND code IN ('5008', '5199')", ("t", c)))
            .Should().Be(2, "Down never deletes a tenant's own account that merely shares a code");

        await migrator.MigrateAsync(MigrationId);
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(MigrationId);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T?> Scalar<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }
}
