using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

/// <summary>
/// Upgrade/rollback rehearsal for the evidence AddErpCodesAndLiveExportIndex must never lose, on real Postgres:
/// <list type="bullet">
/// <item>a raced duplicate export it retires says, on its own row, which export was kept and that both files may
/// have reached the ERP (before: silently relabelled Superseded, with no trail at all);</item>
/// <item>Down REFUSES once any export holds per-journal ERP outcomes (before: it dropped the only record of which
/// journals the ERP posted), and still rolls back cleanly when there is nothing to lose;</item>
/// <item>companies.erp_company_code is unique per tenant, case-insensitively, among live companies.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ErpCodesMigrationEvidencePostgresTests
{
    private const string Previous = "20261010201817_RouteAbsenceDeductionsToContraExpense";
    private const string Tenant = "10000000-0000-0000-0000-000000000009";

    [Fact]
    public async Task RetiredDuplicatesCarryTheirReason_AndDownRefusesToDropPerJournalEvidence()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new ZayraDbContext(options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(Previous);

        // ExecuteSqlRaw formats its text, so JSON braces are doubled ('{{}}' is the literal '{}').
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO gl_journal_exports (id,tenant_id,company_id,company_code,period,payroll_run_id,format_key,currency,file_name,file_hash,line_count,entry_count,total_debits,total_credits,include_unattributed,suppress_reversed_pairs,filter_json,exported_by_name,exported_at_utc,status,created_at_utc)
            VALUES
            ('b0000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000009',NULL,'GROUP','2026-07',NULL,'generic-csv','SAR','old.csv','h1',2,1,10,10,false,false,'{{}}','Maker','2026-08-01 09:00:00','Exported','2026-08-01 09:00:00'),
            ('b0000000-0000-0000-0000-000000000002','10000000-0000-0000-0000-000000000009',NULL,'GROUP','2026-07',NULL,'generic-csv','SAR','new.csv','h2',2,1,10,10,false,false,'{{}}','Maker','2026-08-01 09:00:01','Exported','2026-08-01 09:00:01');
            """);

        await migrator.MigrateAsync();

        var retired = await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse("b0000000-0000-0000-0000-000000000001"));
        Assert.Equal("Superseded", retired.Status);
        Assert.Equal(Guid.Parse("b0000000-0000-0000-0000-000000000002"), retired.SupersededByExportId);
        Assert.Contains("Retired by the upgrade", retired.SupersedeReason);
        Assert.Contains("b0000000-0000-0000-0000-000000000002", retired.SupersedeReason);
        Assert.Contains("Check whether your ERP imported both files", retired.SupersedeReason);
        var kept = await db.GlJournalExports.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse("b0000000-0000-0000-0000-000000000002"));
        Assert.Equal("Exported", kept.Status);
        Assert.Null(kept.SupersedeReason);

        // ERP company code: unique per tenant, case-insensitively, among live companies (a deleted company's code is free).
        var tenantId = Guid.Parse(Tenant);
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "ERP migration", Slug = "erp-migration-evidence" });
        await db.SaveChangesAsync();
        await InsertCompanyAsync(db, tenantId, "Old Co", "SA01", deleted: true);
        await InsertCompanyAsync(db, tenantId, "Alpha KSA", "SA01", deleted: false);
        var dup = await Assert.ThrowsAsync<DbUpdateException>(() => InsertCompanyAsync(db, tenantId, "Beta Services", "sa01", deleted: false));
        var pg = Assert.IsType<Npgsql.PostgresException>(dup.InnerException);
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, pg.SqlState);
        Assert.Equal(Zayra.Api.Migrations.AddErpCodesAndLiveExportIndex.ErpCompanyCodeIndexName, pg.ConstraintName);
        db.ChangeTracker.Clear();

        // A journal confirmed on its own: Down must refuse rather than drop that evidence.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE gl_journal_exports SET confirmed_journal_count = 1, journal_confirmations_json = '[{{\"DocumentRef\":\"J1\",\"Status\":\"Confirmed\"}}]' " +
            "WHERE id = 'b0000000-0000-0000-0000-000000000002'");
        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync(Previous));
        Assert.Contains("cannot be rolled back", refused.MessageText);
        Assert.Contains("per-journal ERP outcomes", refused.MessageText);
        Assert.True(await ColumnExistsAsync(db, "journal_confirmations_json"), "a refused Down leaves the schema and the evidence intact");

        // Nothing to lose: Down rolls back cleanly.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE gl_journal_exports SET confirmed_journal_count = 0, journal_confirmations_json = '[]'");
        await migrator.MigrateAsync(Previous);
        Assert.False(await ColumnExistsAsync(db, "journal_confirmations_json"));
        Assert.False(await ColumnExistsAsync(db, "supersede_reason"));
    }

    private static async Task InsertCompanyAsync(ZayraDbContext db, Guid tenantId, string name, string erpCode, bool deleted)
    {
        db.Companies.Add(new Zayra.Api.Models.Company
        {
            TenantId = tenantId, LegalNameEn = name, TradeName = name, CountryCode = "SA", Jurisdiction = "SA",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}"[..20], DefaultCurrency = "SAR", IsActive = true,
            ErpCompanyCode = erpCode, IsDeleted = deleted,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<bool> ColumnExistsAsync(ZayraDbContext db, string column) =>
        await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'gl_journal_exports' AND column_name = {0}", column)
            .SingleAsync() == 1;
}
