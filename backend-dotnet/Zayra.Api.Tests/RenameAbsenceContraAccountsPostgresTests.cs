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
/// 20261113010000_RenameAbsenceContraAccounts against a real migrated database (launch item L1.2).
/// Account 5008 carries late/early AND loss-of-pay deductions and 5009 carries approved unpaid leave, but the
/// seeded names said "Attendance &amp; Late" and "Unpaid Leave &amp; LOP". The names now match the routing; the
/// routing itself and every balance stay exactly where they are. The migration renames only rows that still
/// hold the seeded name, so a tenant's own wording survives, and it never rewrites posted history.
///
/// <para>Rows are written and read with RAW SQL naming only long-standing columns, so the seed does not depend
/// on columns later migrations add.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class RenameAbsenceContraAccountsPostgresTests
{
    private const string MigrationId = "20261113010000_RenameAbsenceContraAccounts";
    private const string OldAttendance = "Attendance & Late Deductions (contra)";
    private const string OldLeave = "Unpaid Leave & LOP Deductions (contra)";
    private const string NewAttendance = "Absence, Late & Early Deductions (contra)";
    private const string NewLeave = "Unpaid Leave Deductions (contra)";

    [Fact]
    public async Task Up_RenamesOnlySeededNames_KeepsCodesBalancesAndHistory_AndDownRestores()
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

        var a = Guid.NewGuid(); // untouched seed, with a company-level copy of the same seeded account
        var b = Guid.NewGuid(); // customised: renamed accounts and an edited driver default name
        var c = Guid.NewGuid(); // a different account that happens to be called by the old names but is a Liability
        var companyA = Guid.NewGuid();

        async Task Acct(Guid t, Guid? co, string code, string name, string type) =>
            await Exec(conn, $@"INSERT INTO gl_accounts (id, tenant_id, company_id, code, name, account_type, is_active, created_at_utc)
                               VALUES (@id, @t, {(co is null ? "NULL" : "@co")}, @code, @name, @type, TRUE, now())",
                ("id", Guid.NewGuid()), ("t", t), ("co", co ?? Guid.Empty), ("code", code), ("name", name), ("type", type));
        async Task Drv(Guid t, string key, string code, string name, string source) =>
            await Exec(conn, @"INSERT INTO gl_drivers (id, tenant_id, company_id, key, label, category, posting_side, account_type,
                                   default_code, default_name, match_source, match_mode, match_component_code,
                                   emits_employer_expense_pair, paired_expense_driver_key, is_system, is_active, sort_order, created_at_utc)
                               VALUES (@id, @t, NULL, @key, @key, 'Deduction', 'CR', 'Expense', @code, @name, @src, 'Any', NULL,
                                   FALSE, NULL, TRUE, TRUE, 24, now())",
                ("id", Guid.NewGuid()), ("t", t), ("key", key), ("code", code), ("name", name), ("src", source));

        await Acct(a, null, "5008", OldAttendance, "Expense");
        await Acct(a, null, "5009", OldLeave, "Expense");
        await Acct(a, companyA, "5008", OldAttendance, "Expense");
        await Drv(a, "DED:ATTENDANCE", "5008", OldAttendance, "Attendance");
        await Drv(a, "DED:LEAVE", "5009", OldLeave, "Leave");

        await Acct(b, null, "5008", "Absence and lateness (our wording)", "Expense");
        await Acct(b, null, "5009", OldLeave + " ", "Expense");
        await Drv(b, "DED:ATTENDANCE", "5008", "Absence and lateness (our wording)", "Attendance");
        await Drv(b, "DED:LEAVE", "5009", "Unpaid Leave & LOP Deductions (contra) - ours", "Leave");

        await Acct(c, null, "5008", OldAttendance, "Liability");

        var historyId = Guid.NewGuid();
        await Exec(conn, @"INSERT INTO finance_gl_entries (id, tenant_id, company_id, source_module, source_entity_id, source_entity_ref,
                               event_type, debit_account, credit_account, amount, currency, period, entry_date, description,
                               erp_posting_status, is_reversed, posted_by_name, created_at_utc)
                           VALUES (@id, @t, NULL, 'Payroll', @src, '2026-09', 'Accrual', '', '5008 - Attendance & Late Deductions (contra)',
                               420, 'SAR', '2026-09', DATE '2026-09-30', 'Payroll deduction: LOP_DEDUCTION', 'NotReady', FALSE, '', now())",
            ("id", historyId), ("t", a), ("src", Guid.NewGuid()));

        await migrator.MigrateAsync(MigrationId);

        Task<string?> AcctName(Guid t, Guid? co, string code) => Scalar<string>(conn,
            $"SELECT name FROM gl_accounts WHERE tenant_id = @t AND {(co is null ? "company_id IS NULL" : "company_id = @co")} AND code = @code",
            ("t", t), ("co", co ?? Guid.Empty), ("code", code));
        Task<string?> DriverName(Guid t, string key) => Scalar<string>(conn,
            "SELECT default_name FROM gl_drivers WHERE tenant_id = @t AND company_id IS NULL AND key = @key", ("t", t), ("key", key));
        Task<string?> DriverCode(Guid t, string key) => Scalar<string>(conn,
            "SELECT default_code FROM gl_drivers WHERE tenant_id = @t AND company_id IS NULL AND key = @key", ("t", t), ("key", key));

        (await AcctName(a, null, "5008")).Should().Be(NewAttendance);
        (await AcctName(a, null, "5009")).Should().Be(NewLeave);
        (await AcctName(a, companyA, "5008")).Should().Be(NewAttendance, "a company copy that still holds the seeded name is seeded too");
        (await DriverName(a, "DED:ATTENDANCE")).Should().Be(NewAttendance);
        (await DriverName(a, "DED:LEAVE")).Should().Be(NewLeave);
        (await DriverCode(a, "DED:ATTENDANCE")).Should().Be("5008", "late/early and LOP stay on 5008: the names move, the routing does not");
        (await DriverCode(a, "DED:LEAVE")).Should().Be("5009");

        (await AcctName(b, null, "5008")).Should().Be("Absence and lateness (our wording)", "a tenant's own wording is never overwritten");
        (await AcctName(b, null, "5009")).Should().Be(OldLeave + " ", "only an exact match of the seeded name is renamed");
        (await DriverName(b, "DED:ATTENDANCE")).Should().Be("Absence and lateness (our wording)");
        (await DriverName(b, "DED:LEAVE")).Should().Be("Unpaid Leave & LOP Deductions (contra) - ours");

        (await AcctName(c, null, "5008")).Should().Be(OldAttendance, "a Liability with the old name is not the seeded Expense account");

        (await Scalar<string>(conn, "SELECT credit_account FROM finance_gl_entries WHERE id = @id", ("id", historyId)))
            .Should().Be("5008 - Attendance & Late Deductions (contra)", "posted history is never rewritten");

        await migrator.MigrateAsync(previous);
        (await AcctName(a, null, "5008")).Should().Be(OldAttendance);
        (await AcctName(a, null, "5009")).Should().Be(OldLeave);
        (await AcctName(a, companyA, "5008")).Should().Be(OldAttendance);
        (await DriverName(a, "DED:ATTENDANCE")).Should().Be(OldAttendance);
        (await AcctName(b, null, "5008")).Should().Be("Absence and lateness (our wording)");

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
