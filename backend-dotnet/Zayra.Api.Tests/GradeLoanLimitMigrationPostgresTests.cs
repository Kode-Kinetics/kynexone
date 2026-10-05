using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The AddGradeLoanLimits migration itself, on a fresh PostgreSQL 16 built by the real migration chain (the
/// shared fixture uses EnsureCreated, which cannot prove the migration). Upgrades a database holding
/// pre-slice rows, then proves: btree_gist is installed, the EXCLUDE exists and matches the DDL the test
/// fixture applies, old rows read as unclassified / not grade-limited, the shape CHECKs reject bad cells, and
/// a rollback over published limits is refused.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GradeLoanLimitMigrationPostgresTests
{
    private const string Previous = "20261004191027_AddLoanJournalEvidenceAndJawazatPolicy";
    private const string Tenant = "10000000-0000-0000-0000-0000000000a1";

    [Fact]
    public async Task Upgrade_AddsTheExclusion_KeepsOldRowsUnchanged_AndRefusesRollbackOverLimits()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new ZayraDbContext(options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(Previous);
        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO loan_types (id,tenant_id,code,name_en,name_ar,max_amount,max_installments,repayment_frequency,is_interest_free,interest_rate,min_service_months,requires_approval,is_active,is_deleted,created_at_utc)
            VALUES ('30000000-0000-0000-0000-0000000000a1','{Tenant}','LEGACY','Legacy','',5000,12,'Monthly',true,0,0,true,true,false,CURRENT_TIMESTAMP);
            INSERT INTO pay_components (id,tenant_id,company_id,code,name_en,name_ar,component_type,calc_method,structure_field,value,formula_expression,provider_key,is_taxable,gosi_subject,wps_included,eosb_included,gl_driver_key,emit_when_zero,is_family,display_order,is_system,is_statutory,is_active,is_deleted,created_at_utc,effective_from,effective_to)
            VALUES ('40000000-0000-0000-0000-0000000000a1','{Tenant}',NULL,'SHIFT','Shift','Shift','Earning','Fixed',NULL,100,NULL,NULL,false,false,true,false,'EARN:OTHER',false,false,500,false,false,true,false,CURRENT_TIMESTAMP,NULL,NULL);
            INSERT INTO loan_types (id,tenant_id,code,name_en,name_ar,max_amount,max_installments,repayment_frequency,is_interest_free,interest_rate,min_service_months,requires_approval,is_active,is_deleted,created_at_utc)
            VALUES ('30000000-0000-0000-0000-0000000000b2','{Tenant}','INTEREST','Legacy interest','',5000,12,'Monthly',false,3.5,0,true,true,false,CURRENT_TIMESTAMP);
            INSERT INTO grades (id,tenant_id,code,name,band,level,min_salary,mid_salary,max_salary,currency,is_active,created_at_utc,is_deleted)
            VALUES ('50000000-0000-0000-0000-0000000000a1','{Tenant}','G1','Grade 1','',1,0,0,0,'SAR',true,CURRENT_TIMESTAMP,false);
            """);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync(); // re-run is a no-op

        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT count(*) FROM pg_extension WHERE extname = 'btree_gist'"));
        var definition = await ScalarAsync<string>(db,
            $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = '{GradeEntitlementSql.ExclusionName}'");
        Assert.StartsWith("EXCLUDE USING gist (tenant_id WITH =, company_key WITH =, grade_id WITH =, pay_component_code WITH =, daterange(", definition);
        Assert.Contains("WITH &&", definition);
        Assert.Equal(("None", "None"), (await ScalarAsync<string>(db, "SELECT entitlement_class FROM pay_components WHERE code = 'SHIFT'"),
            await ScalarAsync<string>(db, "SELECT statutory_floor FROM pay_components WHERE code = 'SHIFT'")));
        Assert.False(await ScalarAsync<bool>(db, "SELECT grade_limited FROM loan_types WHERE code = 'LEGACY'"));

        // CHECK: a grade-limited type must name its Facility component.
        await AssertRejectedAsync(db, "UPDATE loan_types SET grade_limited = true WHERE code = 'LEGACY'", "23514");
        // CHECKs on the cell shape.
        await AssertRejectedAsync(db, Insert("'Amount'", "NULL", "NULL", "NULL", eligible: true, from: "2026-01-01"), "23514");      // Amount without amount
        await AssertRejectedAsync(db, Insert("'MultipleOfBasic'", "100", "2", "NULL", eligible: true, from: "2026-01-01"), "23514"); // both figures
        await AssertRejectedAsync(db, Insert("'EligibilityOnly'", "NULL", "NULL", "500", eligible: false, from: "2026-01-01"), "23514"); // ineligible with a figure
        // EXCLUDE: one cell per scope per day.
        await db.Database.ExecuteSqlRawAsync(Insert("'Amount'", "1000", "NULL", "NULL", eligible: true, from: "2026-01-01"));
        await AssertRejectedAsync(db, Insert("'Amount'", "2000", "NULL", "NULL", eligible: true, from: "2026-06-01"), "23P01");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE grade_entitlements SET effective_to = '2026-05-31' WHERE amount = 1000");
        await db.Database.ExecuteSqlRawAsync(Insert("'Amount'", "2000", "NULL", "NULL", eligible: true, from: "2026-06-01"));
        Assert.Equal(Guid.Empty, await ScalarAsync<Guid>(db, "SELECT company_key FROM grade_entitlements WHERE amount = 2000"));

        // Qard CHECK, NOT VALID: the legacy interest-bearing row survives the upgrade untouched, but no new or
        // updated row may carry interest — including an edit to that legacy row that leaves its interest in place.
        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT count(*) FROM loan_types WHERE code = 'INTEREST'"));
        Assert.False(await ScalarAsync<bool>(db, "SELECT convalidated FROM pg_constraint WHERE conname = 'ck_loan_types__interest_free'"));
        await AssertRejectedAsync(db, $"INSERT INTO loan_types (id,tenant_id,code,name_en,name_ar,max_amount,max_installments,repayment_frequency,is_interest_free,interest_rate,min_service_months,requires_approval,is_active,is_deleted,created_at_utc,grade_limited) VALUES (gen_random_uuid(),'{Tenant}','NEWINT','New interest','',1,12,'Monthly',true,1,0,true,true,false,CURRENT_TIMESTAMP,false)", "23514");
        await AssertRejectedAsync(db, $"INSERT INTO loan_types (id,tenant_id,code,name_en,name_ar,max_amount,max_installments,repayment_frequency,is_interest_free,interest_rate,min_service_months,requires_approval,is_active,is_deleted,created_at_utc,grade_limited) VALUES (gen_random_uuid(),'{Tenant}','NEWFLAG','Not free','',1,12,'Monthly',false,0,0,true,true,false,CURRENT_TIMESTAMP,false)", "23514");
        await AssertRejectedAsync(db, "UPDATE loan_types SET name_en = 'Renamed' WHERE code = 'INTEREST'", "23514");
        await db.Database.ExecuteSqlRawAsync("UPDATE loan_types SET is_interest_free = true, interest_rate = 0 WHERE code = 'INTEREST'");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM loan_types WHERE code = 'INTEREST'");

        var rollback = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync(Previous));
        Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException, rollback.SqlState);
        Assert.Equal(2L, await ScalarAsync<long>(db, "SELECT count(*) FROM grade_entitlements"));
    }

    private static string Insert(string valueType, string amount, string rate, string maxOutstanding, bool eligible, string from) => $"""
        INSERT INTO grade_entitlements (id,tenant_id,company_id,grade_id,pay_component_code,entitlement_class,eligible,value_type,amount,rate,max_outstanding_amount,effective_from,created_at_utc)
        VALUES (gen_random_uuid(),'{Tenant}',NULL,'50000000-0000-0000-0000-0000000000a1','LOAN_LEGACY','Facility',{(eligible ? "true" : "false")},{valueType},{amount},{rate},{maxOutstanding},'{from}',CURRENT_TIMESTAMP)
        """;

    private static async Task AssertRejectedAsync(ZayraDbContext db, string sql, string sqlState)
    {
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(sqlState, error.SqlState);
    }

    private static async Task<T> ScalarAsync<T>(ZayraDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
