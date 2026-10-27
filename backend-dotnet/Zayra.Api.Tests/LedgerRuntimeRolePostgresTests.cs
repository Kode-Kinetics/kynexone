using FluentAssertions;
using Npgsql;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-E, S-049 and AM-12: the triggers only hold against a role that cannot switch them off. A role that is not the table
/// owner cannot ALTER ... DISABLE TRIGGER, DROP TRIGGER, TRUNCATE or SET session_replication_role, and every other write it
/// can make still runs through the triggers. The migration reports which login roles CAN switch them off (the owner, its
/// members, superusers), because on a deployment where the application connects as one of those the ledger is append-only
/// by convention only: that is a release blocker, not something a migration may repair by moving the application to another
/// role.
/// </summary>
[Collection("LedgerDb")]
[Trait("Category", "Integration")]
public sealed class LedgerRuntimeRolePostgresTests
{
    private const string Role = "kx_ledger_runtime", Member = "kx_ledger_ops";
    private readonly LedgerDbFixture _db;
    public LedgerRuntimeRolePostgresTests(LedgerDbFixture db) => _db = db;

    private async Task<string> RuntimeConnection()
    {
        await _db.Exec($"""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{Role}') THEN CREATE ROLE {Role} LOGIN PASSWORD 'pw'; END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{Member}') THEN CREATE ROLE {Member} LOGIN PASSWORD 'pw'; END IF;
            END $$;
            GRANT USAGE ON SCHEMA public TO {Role};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {Role};
            GRANT postgres TO {Member};
            """);
        return new NpgsqlConnectionStringBuilder(_db.ConnectionString) { Username = Role, Password = "pw", Pooling = false }.ConnectionString;
    }

    [Fact]
    public async Task ARuntimeRoleThatDoesNotOwnTheLedger_CannotSwitchOffTheTriggersOrEmptyTheTable()
    {
        var app = await RuntimeConnection();
        var id = await _db.Post(_db.CompanyA);

        await _db.Expect(LedgerDbFixture.Cc42501, "ALTER TABLE finance_gl_entries DISABLE TRIGGER ALL", "not the owner", app);
        await _db.Expect(LedgerDbFixture.Cc42501, "ALTER TABLE finance_gl_entries DISABLE TRIGGER trg_finance_gl_entries__append_only", "not the owner", app);
        await _db.Expect(LedgerDbFixture.Cc42501, "DROP TRIGGER trg_finance_gl_entries__append_only ON finance_gl_entries", "not the owner", app);
        await _db.Expect(LedgerDbFixture.Cc42501, "ALTER TABLE finance_gl_entries DISABLE TRIGGER trg_finance_gl_entries__erp_evidence", "the deferred checks are as protected as the immediate ones", app);
        await _db.Expect(LedgerDbFixture.Cc42501, "TRUNCATE finance_gl_entries", "no TRUNCATE privilege and not the owner", app);
        await _db.Expect(LedgerDbFixture.Cc42501, "SET session_replication_role = replica", "superuser only", app);
        await _db.Expect(LedgerDbFixture.Cc42501, "ALTER TABLE gl_journal_export_lines DISABLE TRIGGER ALL", "not the owner", app);
        await _db.Expect(LedgerDbFixture.Cc42501, "ALTER TABLE payroll_runs DISABLE TRIGGER trg_payroll_runs__stamp_freeze", "not the owner", app);
        (await _db.Scalar<long>($"SELECT count(*) FROM finance_gl_entries WHERE id = '{id}'")).Should().Be(1);
    }

    [Fact]
    public async Task ARuntimeRoleWithOrdinaryTableGrants_StillRunsThroughEveryTrigger()
    {
        var app = await RuntimeConnection();
        var id = Guid.NewGuid();
        (await _db.Exec(_db.InsertEntry(id, _db.CompanyA), app)).Should().Be(1, "posting is what the role is for");
        (await _db.Exec($"UPDATE finance_gl_entries SET erp_posting_status = 'ReadyForErp' WHERE id = '{id}'", app)).Should().Be(1);
        await _db.Expect(LedgerDbFixture.Cc23001, $"UPDATE finance_gl_entries SET amount = 1 WHERE id = '{id}'", "UPDATE privilege is not licence to rewrite a posted amount", app);
        await _db.Expect(LedgerDbFixture.Cc23001, $"DELETE FROM finance_gl_entries WHERE id = '{id}'", "DELETE privilege is not licence either", app);
    }

    [Fact]
    public async Task TheMigrationNames_EveryLoginRoleThatCouldSwitchTheTriggersOff()
    {
        await RuntimeConnection();
        var roles = await _db.Column("SELECT role_name || ' | ' || why FROM finance_ledger_trigger_bypass_roles() ORDER BY 1");
        roles.Should().Contain(r => r.StartsWith("postgres |"), "the owner (and superuser) can");
        roles.Should().Contain(r => r.StartsWith($"{Member} |") && r.Contains("member"), "a member of the owning role inherits ownership");
        roles.Should().NotContain(r => r.StartsWith($"{Role} |"), "an ordinary grant-holder cannot");
    }

    [Fact]
    public async Task TheMigration_RaisesAClearNotice_NamingThoseRoles_AndTheBlocker()
    {
        await RuntimeConnection();
        var field = typeof(Zayra.Api.Data.ZayraDbContext).Assembly.GetType("Zayra.Api.Migrations.FinanceGlEntriesAppendOnly")
            ?.GetField("RoleReportSql", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        field.Should().NotBeNull("migration M2 must publish the SQL that reports which roles can switch the triggers off");

        var notices = new List<string>();
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        conn.Notice += (_, e) => notices.Add(e.Notice.MessageText);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand((string)field!.GetValue(null)!, conn)) await cmd.ExecuteNonQueryAsync();

        notices.Should().ContainSingle(n => n.Contains("finance_gl_entries") && n.Contains("can switch off") && n.Contains("postgres"));
        notices.Single(n => n.Contains("can switch off")).Should().Contain("RELEASE BLOCKER", "the application must not connect as one of them");
    }
}
