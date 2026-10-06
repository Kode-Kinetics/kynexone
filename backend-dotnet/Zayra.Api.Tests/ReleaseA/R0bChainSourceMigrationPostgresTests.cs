using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The R0b migration (20261007000200_ReleaseAContractChainSource) on a fresh PostgreSQL 16 built by the real migration
/// chain: it applies over a row that already breaks a new rule (NOT VALID: no scan, no failed deploy), every new row is
/// checked, the runbook pre-check finds the bad row, Down refuses over HR-recorded history, and with that gone it rolls
/// back and re-applies cleanly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class R0bChainSourceMigrationPostgresTests
{
    private const string ThisMigration = "20261007000200_ReleaseAContractChainSource";
    private static readonly string[] Checks =
    [
        "ck_employee_contracts__chain_source", "ck_employee_contracts__chain_pair",
        "ck_employee_contracts__renewed_from_counts", "ck_employee_contracts__chain_starts_by_term_start",
    ];

    [Fact]
    public async Task R0b_AppliesNotValid_ChecksNewRows_RefusesRollbackOverRecordedHistory_AndReapplies()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new ZayraDbContext(options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        Assert.Equal(ThisMigration, all[^1]);
        var before = all[all.IndexOf(ThisMigration) - 1];
        Assert.Equal("20261007000100_ReleaseAEntitlementsAndRenewals", before);

        // A row the previous release could write that breaks chain_pair (a number without a chain start).
        await migrator.MigrateAsync(before);
        var tenant = Guid.NewGuid();
        var legacy = Guid.NewGuid();
        await Sql(db, Insert(legacy, tenant, "renewal_number = 1"));

        await migrator.MigrateAsync();
        await migrator.MigrateAsync(); // re-run is a no-op
        foreach (var check in Checks)
            Assert.False(await Scalar<bool>(db, $"SELECT convalidated FROM pg_constraint WHERE conname = '{check}'"), $"{check} is NOT VALID");
        Assert.Equal(1L, await Scalar<long>(db, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'employee_contracts' AND column_name = 'chain_source'"));

        // Every NEW row is checked.
        await Rejected(db, Insert(Guid.NewGuid(), tenant, "renewal_number = 2"));                                         // chain_pair
        await Rejected(db, Insert(Guid.NewGuid(), tenant, "chain_source = 'Guessed'"));                                   // value set
        await Rejected(db, Insert(Guid.NewGuid(), tenant, "renewal_number = 0, chain_started_on = '2026-06-01'"));         // chain starts after term
        await Rejected(db, Insert(Guid.NewGuid(), tenant, $"renewed_from_contract_id = '{legacy}', renewal_number = 0, chain_started_on = '2025-01-01'"));
        var recorded = Guid.NewGuid();
        await Sql(db, Insert(recorded, tenant, "renewal_number = 0, chain_started_on = '2025-01-01', chain_source = 'Recorded'"));
        // A provisional holdover successor carries renewed_from before Apply gives it a number (R6).
        await Sql(db, Insert(Guid.NewGuid(), tenant, $"renewed_from_contract_id = '{recorded}', provisional_basis = 'DeemedRenewal'"));

        // The runbook's read-only pre-check finds exactly the legacy row's tenant.
        Assert.Equal(1L, await Scalar<long>(db, """
            SELECT count(*) FROM (
              SELECT tenant_id FROM employee_contracts GROUP BY tenant_id
              HAVING count(*) FILTER (WHERE NOT ((renewal_number IS NULL) = (chain_started_on IS NULL))) > 0
                  OR count(*) FILTER (WHERE NOT (renewed_from_contract_id IS NULL OR renewal_number >= 1 OR provisional_basis IS NOT NULL)) > 0
                  OR count(*) FILTER (WHERE NOT (chain_started_on IS NULL OR chain_started_on <= start_date)) > 0
                  OR count(*) FILTER (WHERE NOT (chain_source IS NULL OR chain_source IN ('Derived','Recorded'))) > 0) t
            """));

        // Down refuses while HR-recorded history exists; without it, it rolls back and re-applies.
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(before));
        Assert.Contains("R0b Down refused", refused.ToString());
        Assert.Equal(1L, await Scalar<long>(db, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{ThisMigration}'"));
        await Sql(db, "UPDATE employee_contracts SET chain_source = 'Derived' WHERE chain_source = 'Recorded'");
        await migrator.MigrateAsync(before);
        // A stamped chain survives the Down without its source (the column is gone) ...
        Assert.Equal(0L, await Scalar<long>(db, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'employee_contracts' AND column_name = 'chain_source'"));
        Assert.Equal(0L, await Scalar<long>(db, $"SELECT count(*) FROM pg_constraint WHERE conname IN ('{string.Join("','", Checks)}')"));
        await migrator.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        // ... and the re-Up marks it Derived again (runbook); the unstamped rows stay NULL.
        Assert.Equal("Derived", await Scalar<string>(db, $"SELECT chain_source FROM employee_contracts WHERE id = '{recorded}'"));
        Assert.Equal(0L, await Scalar<long>(db, "SELECT count(*) FROM employee_contracts WHERE chain_source IS NULL AND renewal_number IS NOT NULL AND chain_started_on IS NOT NULL"));
        Assert.Equal(4L, await Scalar<long>(db, $"SELECT count(*) FROM pg_constraint WHERE conname IN ('{string.Join("','", Checks)}')"));
    }

    private static string Insert(Guid id, Guid tenant, string set)
    {
        var columns = new Dictionary<string, string>
        {
            ["id"] = $"'{id}'", ["tenant_id"] = $"'{tenant}'", ["employee_id"] = "'00000000-0000-0000-0000-0000000000e1'",
            ["employee_name"] = "'R0b'", ["contract_number"] = $"'C-{id:N}'", ["contract_type"] = "'Employment'", ["status"] = "'Active'",
            ["start_date"] = "'2026-01-01'", ["end_date"] = "'2026-12-31'", ["basic_salary"] = "1", ["currency_code"] = "'SAR'",
            ["content_html_en"] = "''", ["content_html_ar"] = "''", ["language"] = "'en'", ["version"] = "1",
            ["signed_by_employee_name"] = "''", ["signed_by_hr_name"] = "''", ["file_url"] = "''", ["created_at_utc"] = "CURRENT_TIMESTAMP",
            ["is_deleted"] = "false",
        };
        foreach (var pair in set.Split(", "))
        {
            var (k, v) = (pair[..pair.IndexOf(" = ", StringComparison.Ordinal)], pair[(pair.IndexOf(" = ", StringComparison.Ordinal) + 3)..]);
            columns[k] = v;
        }
        return $"INSERT INTO employee_contracts ({string.Join(",", columns.Keys)}) VALUES ({string.Join(",", columns.Values)})";
    }

    private static Task Sql(ZayraDbContext db, string sql) => db.Database.ExecuteSqlRawAsync(sql);

    private static async Task Rejected(ZayraDbContext db, string sql)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal("23514", error.SqlState);
    }

    private static async Task<T> Scalar<T>(ZayraDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
