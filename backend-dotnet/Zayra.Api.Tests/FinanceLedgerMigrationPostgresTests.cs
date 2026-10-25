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
/// M2 (<c>*_FinanceGlEntriesAppendOnly</c>, v1-spec 1.10) on the real migration chain: SQL only, applies over a database whose
/// ledger already holds rows that break every new rule (the triggers bind future changes only), re-runs cleanly, gives up
/// fast instead of queueing behind a long transaction, and rolls back to a mutable ledger.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FinanceLedgerMigrationPostgresTests
{
    private const string Suffix = "_FinanceGlEntriesAppendOnly";
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-0000000000e1");

    private const string TriggerCatalogue =
        "SELECT c.relname || '.' || t.tgname FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid " +
        "WHERE NOT t.tgisinternal AND t.tgname LIKE 'trg\\_%\\_\\_%' ESCAPE '\\' AND t.tgenabled = 'A'";

    private static readonly string[] Triggers =
    [
        "finance_gl_entries.trg_finance_gl_entries__append_only", "finance_gl_entries.trg_finance_gl_entries__no_truncate",
        "finance_gl_entries.trg_finance_gl_entries__reversal_valid", "finance_gl_entries.trg_finance_gl_entries__reversed_needs_contra",
        "finance_gl_entries.trg_finance_gl_entries__erp_evidence",
        "cost_centers.trg_cost_centers__guard", "cost_centers.trg_cost_centers__parent_cycle",
        "gl_journal_export_lines.trg_gl_journal_export_lines__immutable",
        "payroll_runs.trg_payroll_runs__stamp_freeze", "payroll_earnings.trg_payroll_earnings__stamp_freeze",
        "payroll_deductions.trg_payroll_deductions__stamp_freeze", "payroll_slips.trg_payroll_slips__stamp_freeze",
    ];

    [Fact]
    public async Task Migration_AppliesOverLegacyLedgerRows_RerunsCleanly_AndRollsBackToAMutableLedger()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var (migrator, m2, before) = Chain(container);
        await migrator.MigrateAsync(before);

        // Legacy rows that break every new rule: reversed without a contra, Posted without evidence, a contra whose original
        // is gone, an unattributed line. None of them may stop the upgrade.
        var reversed = Guid.NewGuid(); var posted = Guid.NewGuid(); var orphanContra = Guid.NewGuid(); var unattributed = Guid.NewGuid();
        var fx = new Seeder(container);
        await fx.Entry(reversed, reversedFlag: true);
        await fx.Entry(posted, status: "Posted");
        await fx.Entry(orphanContra, reversalOf: Guid.NewGuid());
        await fx.Entry(unattributed, company: false);
        await fx.Exec($"UPDATE finance_gl_entries SET amount = 7, description = 'edited before M2' WHERE id = '{reversed}'"); // mutable today

        await migrator.MigrateAsync(m2);

        (await fx.Scalar<long>($"SELECT count(*) FROM finance_gl_entries WHERE id IN ('{reversed}','{posted}','{orphanContra}','{unattributed}')")).Should().Be(4, "no legacy row is touched");
        var present = await fx.Column(TriggerCatalogue);
        present.Should().Contain(Triggers, "every trigger is enabled ALWAYS");
        (await fx.Column("SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_finance_gl_entries_reversal_of'"))
            .Should().ContainSingle().Which.Should().Contain("(tenant_id, reversal_of_entry_id)").And.Contain("WHERE (reversal_of_entry_id IS NOT NULL)",
                "the reversal triggers look contras up by their original on every insert and every flag; an unindexed scan per contra would make a big void quadratic");

        await fx.Expect("23001", $"UPDATE finance_gl_entries SET amount = 8 WHERE id = '{reversed}'");
        await fx.Expect("23001", $"DELETE FROM finance_gl_entries WHERE id = '{unattributed}'");
        (await fx.Exec($"UPDATE finance_gl_entries SET erp_status_changed_at_utc = now() WHERE id = '{posted}'")).Should().Be(1, "a legacy Posted line is not re-judged until its ERP state actually changes");

        // Re-runnable: the whole of Up's SQL, run again over the migrated database, changes nothing and fails nothing.
        var ddl = (string[]?)typeof(ZayraDbContext).Assembly.GetType("Zayra.Api.Migrations.FinanceGlEntriesAppendOnly")
            ?.GetField("PostgresOnlyDdl")?.GetValue(null);
        ddl.Should().NotBeNullOrEmpty("M2 publishes its SQL so the integration fixture runs production's text");
        for (var pass = 0; pass < 2; pass++)
            foreach (var statement in ddl!) await fx.Exec(statement);
        (await fx.Column(TriggerCatalogue)).Should().Contain(Triggers).And.HaveCount(present.Count);

        // Down: no ledger rule left, the ledger is mutable again, nothing else changed.
        await migrator.MigrateAsync(before);
        (await fx.Column(
            "SELECT t.tgname FROM pg_trigger t WHERE NOT t.tgisinternal AND (t.tgname LIKE 'trg\\_finance\\_gl\\_entries\\_\\_%' ESCAPE '\\' " +
            "OR t.tgname LIKE 'trg\\_cost\\_centers\\_\\_%' ESCAPE '\\' OR t.tgname LIKE 'trg\\_gl\\_journal\\_export\\_lines\\_\\_%' ESCAPE '\\' " +
            "OR t.tgname LIKE 'trg\\_payroll\\_%\\_\\_stamp\\_freeze' ESCAPE '\\')")).Should().BeEmpty();
        (await fx.Scalar<long>("SELECT count(*) FROM pg_proc WHERE proname LIKE 'finance\\_gl\\_entries\\_%' ESCAPE '\\' OR proname = 'finance_ledger_trigger_bypass_roles'"))
            .Should().Be(0, "its functions go with it");
        (await fx.Column("SELECT indexname FROM pg_indexes WHERE indexname = 'ix_finance_gl_entries_reversal_of'")).Should().BeEmpty();
        (await fx.Exec($"UPDATE finance_gl_entries SET amount = 9 WHERE id = '{reversed}'")).Should().Be(1);

        await migrator.MigrateAsync(m2);
        await fx.Expect("23001", $"UPDATE finance_gl_entries SET amount = 10 WHERE id = '{reversed}'");
        await using var db = Context(container);
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Migration_GivesUpAfterLockTimeout_InsteadOfQueueingBehindALongTransaction_AndLeavesNothing()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var (migrator, m2, before) = Chain(container);
        await migrator.MigrateAsync(before);

        await using (var blocker = new NpgsqlConnection(container.GetConnectionString()))
        {
            await blocker.OpenAsync();
            await using var tx = await blocker.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("LOCK TABLE finance_gl_entries IN ROW EXCLUSIVE MODE", blocker, tx)) await hold.ExecuteNonQueryAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var started = System.Diagnostics.Stopwatch.StartNew();
            var failure = await Record.ExceptionAsync(() => migrator.MigrateAsync(m2, cts.Token));
            started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "lock_timeout must end the wait, not the test's own cancellation");
            failure.Should().NotBeNull();
            Causes(failure!).OfType<PostgresException>().Should().Contain(e => e.SqlState == "55P03", failure!.ToString());
            await tx.RollbackAsync();
        }
        var fx = new Seeder(container);
        (await fx.Scalar<long>("SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'trg\\_finance\\_gl\\_entries\\_\\_%' ESCAPE '\\'"))
            .Should().Be(0, "the timed-out migration left nothing behind");
        await migrator.MigrateAsync(m2);
    }

    private static IEnumerable<Exception> Causes(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException) yield return x;
    }

    private static ZayraDbContext Context(PostgreSqlContainer container) =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);

    private static (IMigrator Migrator, string M2, string Before) Chain(PostgreSqlContainer container)
    {
        var db = Context(container);
        var all = db.Database.GetMigrations().ToList();
        var m2 = all.SingleOrDefault(m => m.EndsWith(Suffix, StringComparison.Ordinal));
        m2.Should().NotBeNull($"a migration named *{Suffix} must exist (v1-spec 1.10, M2)");
        return (db.Database.GetInfrastructure().GetRequiredService<IMigrator>(), m2!, all[all.IndexOf(m2!) - 1]);
    }

    private sealed class Seeder(PostgreSqlContainer container)
    {
        public async Task Entry(Guid id, bool reversedFlag = false, string status = "NotReady", Guid? reversalOf = null, bool company = true)
        {
            var companyId = Guid.NewGuid();
            await Exec(
                "INSERT INTO finance_gl_entries (id, tenant_id, company_id, source_module, source_entity_id, source_entity_ref, event_type, debit_account, credit_account, amount, currency, entry_date, period, description, posted_by_name, is_reversed, reversal_of_entry_id, created_at_utc, erp_posting_status) VALUES " +
                $"('{id}', '{Tenant}', {(company ? $"'{companyId}'" : "NULL")}, 'Payroll', '{Guid.NewGuid()}', 'RUN-1', 'PayrollAccrual', '5001', '2100', 100, 'SAR', DATE '2026-09-15', '2026-09', 'legacy', 'x', {(reversedFlag ? "true" : "false")}, {(reversalOf is null ? "NULL" : $"'{reversalOf}'")}, now(), '{status}')");
        }

        public async Task<int> Exec(string sql)
        {
            await using var conn = new NpgsqlConnection(container.GetConnectionString());
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            return await cmd.ExecuteNonQueryAsync();
        }

        public async Task<T> Scalar<T>(string sql)
        {
            await using var conn = new NpgsqlConnection(container.GetConnectionString());
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            return (T)(await cmd.ExecuteScalarAsync())!;
        }

        public async Task<List<string>> Column(string sql)
        {
            await using var conn = new NpgsqlConnection(container.GetConnectionString());
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            var list = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) list.Add(reader.GetString(0));
            return list;
        }

        public async Task Expect(string sqlState, string sql)
        {
            var act = async () => await Exec(sql);
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(sqlState, sql);
        }
    }
}
