using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The benefit definition migration on a fresh PostgreSQL 16 built by the real migration chain: four additive nullable columns on
/// <c>pay_components</c>, every row written before it reads unchanged and stays writable, each CHECK, the foreign key to the legacy plan and the
/// unique source-plan index do their job, and the migration rolls back and re-applies cleanly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BenefitDefinitionMigrationPostgresTests
{
    private const string ThisMigration = "20261108134300_BenefitDefinition";
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-0000000000b0");
    private const string NewColumns = "('benefit_category','policy_class','requires_enrolment','source_benefit_plan_id')";

    [Fact]
    public async Task Upgrade_AddsOnlyNullableColumns_KeepsOldRowsValid_EnforcesTheChecks_AndRollsBackAndReapplies()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new ZayraDbContext(options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.IndexOf(ThisMigration) - 1];

        // ── Before: rows the previous release wrote (a customer benefit, a cash allowance, a catalogue row) ──
        await migrator.MigrateAsync(before);
        await Sql(db, OldRow("BEN_OLD", "Benefit", "Fixed") + OldRow("ALW_OLD", "Earning", "GradePackage") + OldRow("BASIC", "Earning", "StructureField"));

        await migrator.MigrateAsync();
        await migrator.MigrateAsync(); // re-run is a no-op
        Assert.Equal(1L, await Scalar<long>(db, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{ThisMigration}'"));

        // Additive and nullable only: the four columns exist, none is NOT NULL, none has a default.
        var columns = await Rows(db, "SELECT column_name||':'||is_nullable||':'||COALESCE(column_default,'-') FROM information_schema.columns " +
            $"WHERE table_name='pay_components' AND column_name IN {NewColumns} ORDER BY 1");
        Assert.Equal(["benefit_category:YES:-", "policy_class:YES:-", "requires_enrolment:YES:-", "source_benefit_plan_id:YES:-"], columns);
        // Old rows read unchanged (all four NULL) and stay writable (N-1 compatible).
        Assert.Equal(3L, await Scalar<long>(db, "SELECT count(*) FROM pay_components WHERE benefit_category IS NULL AND policy_class IS NULL AND requires_enrolment IS NULL AND source_benefit_plan_id IS NULL"));
        await Sql(db, "UPDATE pay_components SET name_en = 'Old benefit renamed' WHERE code = 'BEN_OLD'");

        // ── the CHECKs ──
        await Sql(db, OldRow("BEN_NEW", "Benefit", "Fixed", "benefit_category,policy_class,requires_enrolment", "'Facilities','Discretionary',false"));
        await Sql(db, OldRow("ALW_NEW", "Earning", "GradePackage", "policy_class", "'Contractual'"));
        await Refused(db, OldRow("BEN_BAD1", "Benefit", "Fixed", "policy_class", "'Mandatory'"), "ck_pay_components__policy_class");
        await Refused(db, OldRow("BEN_BAD2", "Benefit", "Fixed", "requires_enrolment", "true"), "ck_pay_components__requires_enrolment_unsupported");
        await Refused(db, OldRow("BEN_BAD3", "Benefit", "Fixed", "benefit_category", "'   '"), "ck_pay_components__benefit_category_not_blank");
        // A definition field on a row that is not a customer-added benefit or allowance.
        await Refused(db, OldRow("BASIC2", "Earning", "StructureField", "benefit_category", "'x'"), "ck_pay_components__benefit_fields_custom_only");
        await Refused(db, OldRow("BENEFIT_NOT_PREFIX", "Benefit", "Fixed", "policy_class", "'Contractual'"), "ck_pay_components__benefit_fields_custom_only");
        // The underscore in 'BEN_' is not a wildcard: 'BENXPARK' is not a custom code.
        await Refused(db, OldRow("BENXPARK", "Benefit", "Fixed", "policy_class", "'Contractual'"), "ck_pay_components__benefit_fields_custom_only");

        // ── the foreign key and the unique source plan ──
        // The foreign key is on the plan's id alone: the database does NOT check that the plan belongs to the row's tenant. Nothing writes the column yet;
        // any future import must load the plan under the caller's tenant and refuse another tenant's plan before setting it.
        var plan = new BenefitPlan { TenantId = Tenant, Code = "MED", Name = "Medical", PlanType = "Medical" };
        db.BenefitPlans.Add(plan);
        await db.SaveChangesAsync();
        await Sql(db, OldRow("BEN_FROMPLAN", "Benefit", "Fixed", "source_benefit_plan_id", $"'{plan.Id}'"));
        await Refused(db, OldRow("BEN_TWICE", "Benefit", "Fixed", "source_benefit_plan_id", $"'{plan.Id}'"), "source_benefit_plan_id");
        await Refused(db, OldRow("BEN_NOPLAN", "Benefit", "Fixed", "source_benefit_plan_id", $"'{Guid.NewGuid()}'"), "FK_pay_components_benefit_plans_source_benefit_plan_id");
        await Refused(db, $"DELETE FROM benefit_plans WHERE id = '{plan.Id}'", "FK_pay_components_benefit_plans_source_benefit_plan_id");
        // Two rows with no source plan do not collide (the index is partial).
        await Sql(db, OldRow("BEN_FREE1", "Benefit", "Fixed") + OldRow("BEN_FREE2", "Benefit", "Fixed"));

        // ── rollback and re-apply ──
        await migrator.MigrateAsync(before);
        Assert.Equal(0L, await Scalar<long>(db, $"SELECT count(*) FROM information_schema.columns WHERE table_name='pay_components' AND column_name IN {NewColumns}"));
        Assert.Equal(1L, await Scalar<long>(db, "SELECT count(*) FROM pay_components WHERE code='BEN_OLD' AND name_en='Old benefit renamed'"));
        await migrator.MigrateAsync();
        Assert.Equal(4L, await Scalar<long>(db, $"SELECT count(*) FROM information_schema.columns WHERE table_name='pay_components' AND column_name IN {NewColumns}"));
    }

    private static string OldRow(string code, string type, string calc, string? extraColumns = null, string? extraValues = null) =>
        "INSERT INTO pay_components (id,tenant_id,code,name_en,name_ar,component_type,calc_method,created_at_utc,display_order,emit_when_zero,eosb_included," +
        "gosi_subject,is_active,is_deleted,is_family,is_statutory,is_system,is_taxable,wps_included" + (extraColumns is null ? "" : "," + extraColumns) + ") " +
        $"VALUES (gen_random_uuid(),'{Tenant}','{code}','{code}','{code}','{type}','{calc}',CURRENT_TIMESTAMP,0,false,false,false,true,false,false,false,false,false,false" +
        (extraValues is null ? "" : "," + extraValues) + ");";

    private static async Task Refused(ZayraDbContext db, string sql, string constraint)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Sql(db, sql));
        Assert.Contains(constraint, ex.ToString());
    }

    private static async Task Sql(ZayraDbContext db, string sql)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(ZayraDbContext db, string sql)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> Rows(ZayraDbContext db, string sql)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }
}
