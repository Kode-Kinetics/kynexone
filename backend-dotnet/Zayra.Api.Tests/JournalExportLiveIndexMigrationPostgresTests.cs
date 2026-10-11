using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

/// <summary>
/// Upgrade rehearsal for AddErpCodesAndLiveExportIndex on real Postgres: a database that already holds two
/// live (Exported) exports of one GROUP scope — the state the race produced — must upgrade (older one
/// Superseded, index created), keep existing exports on format version 0 with Kind Full, and roll back cleanly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JournalExportLiveIndexMigrationPostgresTests
{
    private const string Previous = "20261010015737_ScopeBranchCodeByCompany";

    [Fact]
    public async Task Upgrade_SupersedesRacedDuplicates_CreatesTheIndex_AndRollsBack()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new ZayraDbContext(options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(Previous);

        // ExecuteSqlRaw formats its text, so JSON braces are doubled ('{{}}' is the literal '{}').
        // Two raced group-level exports (company_id and payroll_run_id NULL) and one other format of the same scope.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO gl_journal_exports (id,tenant_id,company_id,company_code,period,payroll_run_id,format_key,currency,file_name,file_hash,line_count,entry_count,total_debits,total_credits,include_unattributed,suppress_reversed_pairs,filter_json,exported_by_name,exported_at_utc,status,created_at_utc)
            VALUES
            ('a0000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000001',NULL,'GROUP','2026-07',NULL,'generic-csv','SAR','old.csv','h1',2,1,10,10,false,false,'{{}}','Maker','2026-08-01 09:00:00',  'Exported','2026-08-01 09:00:00'),
            ('a0000000-0000-0000-0000-000000000002','10000000-0000-0000-0000-000000000001',NULL,'GROUP','2026-07',NULL,'generic-csv','SAR','new.csv','h2',2,1,10,10,false,false,'{{}}','Maker','2026-08-01 09:00:01',  'Exported','2026-08-01 09:00:01'),
            ('a0000000-0000-0000-0000-000000000003','10000000-0000-0000-0000-000000000001',NULL,'GROUP','2026-07',NULL,'quickbooks-iif','SAR','q.iif','h3',2,1,10,10,false,false,'{{}}','Maker','2026-08-01 09:00:02','Exported','2026-08-01 09:00:02');
            """);

        await migrator.MigrateAsync();

        var rows = await db.GlJournalExports.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(new[] { "Superseded", "Exported", "Exported" }, rows.Select(r => r.Status).ToArray());
        Assert.All(rows, r => { Assert.Equal(0, r.FormatVersion); Assert.Equal("Full", r.Kind); Assert.Equal("[]", r.JournalConfirmationsJson); });
        Assert.True(await IndexExistsAsync(db));

        // The index now refuses a second live export of the same NULL-company, NULL-run scope.
        var dup = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("""
            INSERT INTO gl_journal_exports (id,tenant_id,company_id,company_code,period,payroll_run_id,format_key,currency,file_name,file_hash,line_count,entry_count,total_debits,total_credits,include_unattributed,suppress_reversed_pairs,filter_json,exported_by_name,exported_at_utc,status,created_at_utc,kind,format_options_json,journal_confirmations_json,format_version,confirmed_journal_count)
            VALUES ('a0000000-0000-0000-0000-000000000004','10000000-0000-0000-0000-000000000001',NULL,'GROUP','2026-07',NULL,'generic-csv','SAR','x.csv','h4',2,1,10,10,false,false,'{{}}','Maker',now(),'Exported',now(),'Full','{{}}','[]',2,0);
            """));
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, dup.SqlState);

        await migrator.MigrateAsync(Previous);
        Assert.False(await IndexExistsAsync(db));
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM gl_journal_exports").SingleAsync());
    }

    private static async Task<bool> IndexExistsAsync(ZayraDbContext db) =>
        await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname = 'ux_gl_journal_exports_live_scope'").SingleAsync() == 1;
}
