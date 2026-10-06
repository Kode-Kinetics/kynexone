using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The AddGradeLoanLimits (20261006000100) and AddGradeNameArAndLoanOffering (20261006000200) migrations themselves, on a fresh PostgreSQL 16 built by the real migration chain (the
/// shared fixture uses EnsureCreated, which cannot prove the migration). Upgrades a database holding
/// pre-slice rows, then proves: btree_gist is installed, the EXCLUDE exists and matches the DDL the test
/// fixture applies, old rows read as unclassified / not grade-limited, the shape CHECKs reject bad cells, and
/// a rollback over published limits is refused.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GradeLoanLimitMigrationPostgresTests
{
    private const string Previous = "20261004191027_AddLoanJournalEvidenceAndJawazatPolicy";
    /// <summary>The migration immediately before this slice's two (20261006000100, 20261006000200): rolling back to it
    /// runs exactly their Down() methods. Leave (#176) sorts first, matching production's apply order.</summary>
    private const string BeforeGradeLimits = "20261005204934_AddKsaStatutoryLeaveFields";
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

        // loan_policies offering markers: each constraint rejects the row it exists to stop.
        const string policyCols = "id,tenant_id,company_id,loan_type_id,policy_name,max_concurrent_loans,max_multiplier_of_salary,cooldown_months_after_repayment,allow_early_settlement,allow_rescheduling,is_active,created_at_utc";
        const string policyVals = "'Legacy policy',2,3,2,true,true,true,CURRENT_TIMESTAMP";
        await db.Database.ExecuteSqlRawAsync($"INSERT INTO loan_policies ({policyCols}) VALUES ('40000000-0000-0000-0000-0000000000a1','{Tenant}',NULL,'30000000-0000-0000-0000-0000000000a1',{policyVals})");
        // copied_from without the switch flag
        await AssertRejectedAsync(db, $"INSERT INTO loan_policies ({policyCols},copied_from_policy_id) VALUES (gen_random_uuid(),'{Tenant}',NULL,'30000000-0000-0000-0000-0000000000a1',{policyVals},'40000000-0000-0000-0000-0000000000a1')", "23514");
        // a switch stub that says "offered"
        await AssertRejectedAsync(db, $"INSERT INTO loan_policies ({policyCols},created_by_offering_switch,is_offered) VALUES (gen_random_uuid(),'{Tenant}',NULL,'30000000-0000-0000-0000-0000000000a1',{policyVals},true,true)", "23514");
        // copied from a policy that does not exist in this tenant (another tenant's id, or none at all)
        await AssertRejectedAsync(db, $"INSERT INTO loan_policies ({policyCols},created_by_offering_switch,is_offered,copied_from_policy_id) VALUES (gen_random_uuid(),'10000000-0000-0000-0000-0000000000ff',NULL,'30000000-0000-0000-0000-0000000000a1',{policyVals},true,false,'40000000-0000-0000-0000-0000000000a1')", "23503");
        // the valid shape: a not-offered stub pointing at a policy of the same tenant
        await db.Database.ExecuteSqlRawAsync($"INSERT INTO loan_policies ({policyCols},created_by_offering_switch,is_offered,copied_from_policy_id) VALUES ('40000000-0000-0000-0000-0000000000a2','{Tenant}',NULL,'30000000-0000-0000-0000-0000000000a1',{policyVals},true,false,'40000000-0000-0000-0000-0000000000a1')");
        // a retired stub keeps its markers and stays "not offered"
        await db.Database.ExecuteSqlRawAsync("UPDATE loan_policies SET is_active = false WHERE id = '40000000-0000-0000-0000-0000000000a2'");
        await AssertRejectedAsync(db, "DELETE FROM loan_policies WHERE id = '40000000-0000-0000-0000-0000000000a1'", "23503");   // RESTRICT
        await db.Database.ExecuteSqlRawAsync("DELETE FROM loan_policies WHERE tenant_id = '" + Tenant + "' AND created_by_offering_switch");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM loan_policies WHERE tenant_id = '" + Tenant + "'");

        var rollback = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync(BeforeGradeLimits));
        Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException, rollback.SqlState);
        Assert.Equal(2L, await ScalarAsync<long>(db, "SELECT count(*) FROM grade_entitlements"));

        // With the evidence gone, both Down()s run cleanly back to the leave migration, and the slice reapplies.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM grade_entitlements");
        await migrator.MigrateAsync(BeforeGradeLimits);
        Assert.Equal(0L, await ScalarAsync<long>(db, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'grade_entitlements'"));
        Assert.Equal("20261005204934_AddKsaStatutoryLeaveFields", await ScalarAsync<string>(db,
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1"));
        await migrator.MigrateAsync();
        // The slice is back in the history. Not "is the newest": later migrations (e.g. the MFA ones,
        // 20261006000300/0400) legitimately follow it, and MigrateAsync() reapplies those too.
        Assert.Equal(1L, await ScalarAsync<long>(db,
            "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20261006000200_AddGradeNameArAndLoanOffering'"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(1L, await ScalarAsync<long>(db, "SELECT count(*) FROM pg_constraint WHERE conname = 'ex_grade_entitlements__no_overlap'"));
    }

    /// <summary>
    /// The migration carries a FROZEN copy of the interest-free DDL so a later edit to <see cref="LoanTypeSql"/> cannot
    /// rewrite history; the Postgres test fixture applies the constant. They must stay identical, or the fixture would
    /// test a constraint production never got.
    /// </summary>
    [Fact]
    public void InterestFreeCheckFrozenInMigration_MatchesTheConstantTheFixtureApplies()
    {
        Assert.Equal(LoanTypeSql.AddInterestFreeCheck, Zayra.Api.Migrations.AddGradeNameArAndLoanOffering.AddInterestFreeCheckSql);
        Assert.Equal(LoanTypeSql.DropInterestFreeCheck, Zayra.Api.Migrations.AddGradeNameArAndLoanOffering.DropInterestFreeCheckSql);
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
