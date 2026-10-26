using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Release 1 of 2 for 16 columns nothing reads (11 on tenant_hr_configs, 5 on attendance_policies): the application stops
/// mapping them and the database gives each one the value the old C# initializer gave it. The columns are NOT NULL with no
/// default, so without this an insert that no longer names them would fail. The columns and every stored value stay; the
/// drop is a later release, after this one is live. Real migration chain, real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeadConfigColumnsMigrationPostgresTests
{
    private const string MigrationSuffix = "_StopMappingDeadConfigColumns";

    /// <summary>Column -> the default the old entity initializer supplied (verbatim from the pre-change entities).</summary>
    private static readonly (string Table, string Column, string Default)[] Expected =
    [
        ("tenant_hr_configs", "use_dept_head_approval", "true"),
        ("tenant_hr_configs", "use_hr_final_approval", "true"),
        ("tenant_hr_configs", "use_supervisor_before_manager", "false"),
        ("tenant_hr_configs", "allow_dotted_line_approval", "false"),
        ("tenant_hr_configs", "auto_create_dept_on_import", "false"),
        ("tenant_hr_configs", "auto_create_designation_on_import", "false"),
        ("tenant_hr_configs", "require_import_preview_before_commit", "true"),
        ("tenant_hr_configs", "allow_cross_dept_manager", "true"),
        ("tenant_hr_configs", "allow_cross_location_manager", "true"),
        ("tenant_hr_configs", "require_cost_center_for_payroll", "false"),
        ("tenant_hr_configs", "require_grade_for_approval_policy", "false"),
        ("attendance_policies", "late_threshold_minutes", "15"),
        ("attendance_policies", "absent_threshold_minutes", "120"),
        ("attendance_policies", "rounding_rule", "'NearestMinute'::text"),
        ("attendance_policies", "requires_overtime_approval", "true"),
        ("attendance_policies", "allow_absence_to_leave_conversion", "true"),
    ];

    [Fact]
    public async Task InsertsThroughTheCurrentModelSucceedOnTheFullyMigratedSchema()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync();

        db.TenantHrConfigs.Add(new TenantHrConfig { TenantId = Guid.NewGuid() });
        db.AttendancePolicies.Add(new AttendancePolicy { TenantId = Guid.NewGuid(), Code = "NEW", Name = "New policy" });
        await db.SaveChangesAsync();

        (await db.TenantHrConfigs.CountAsync()).Should().Be(1);
        (await db.AttendancePolicies.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TheCurrentModelCanInsertBothRows_TheDefaultsMatchTheOldInitializers_StoredValuesSurvive_AndItRollsBack()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var thisMigration = all.SingleOrDefault(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        thisMigration.Should().NotBeNull("the migration that gives the unmapped columns their defaults exists");
        var previous = all[all.IndexOf(thisMigration!) - 1];
        all.Last().Should().Be(thisMigration, "nothing may sort after it on this branch until release 2");

        // A row written by the old application, before this migration: every column explicit, none at its default.
        await migrator.MigrateAsync(previous);
        const string tenant = "'10000000-0000-0000-0000-0000000000d1'";
        await Exec(container, await InsertRowSql(container, "tenant_hr_configs", new() { ["tenant_id"] = tenant, ["use_dept_head_approval"] = "false", ["require_import_preview_before_commit"] = "false" }));
        await Exec(container, await InsertRowSql(container, "attendance_policies", new() { ["tenant_id"] = tenant, ["code"] = "'OLD'", ["late_threshold_minutes"] = "33", ["rounding_rule"] = "'Nearest15'" }));

        await migrator.MigrateAsync(thisMigration);

        // The new model never names the 16 columns, so these inserts only work because of the defaults.
        db.TenantHrConfigs.Add(new TenantHrConfig { TenantId = Guid.NewGuid() });
        db.AttendancePolicies.Add(new AttendancePolicy { TenantId = Guid.NewGuid(), Code = "NEW", Name = "New policy" });
        await db.SaveChangesAsync();

        foreach (var (table, column, expected) in Expected)
            (await Scalar<string?>(container, $"SELECT column_default FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}'"))
                .Should().Be(expected, $"{table}.{column} takes the value the old entity initializer gave it");

        // The columns are still there and hold what was stored; the rows written through the new model carry the defaults.
        (await Scalar<bool>(container, "SELECT use_dept_head_approval FROM tenant_hr_configs WHERE tenant_id = " + tenant)).Should().BeFalse();
        (await Scalar<bool>(container, "SELECT require_import_preview_before_commit FROM tenant_hr_configs WHERE tenant_id = " + tenant)).Should().BeFalse();
        (await Scalar<int>(container, "SELECT late_threshold_minutes FROM attendance_policies WHERE code = 'OLD'")).Should().Be(33);
        (await Scalar<string>(container, "SELECT rounding_rule FROM attendance_policies WHERE code = 'OLD'")).Should().Be("Nearest15");
        (await Scalar<int>(container, "SELECT absent_threshold_minutes FROM attendance_policies WHERE code = 'NEW'")).Should().Be(120);
        (await Scalar<bool>(container, "SELECT use_hr_final_approval FROM tenant_hr_configs WHERE tenant_id <> " + tenant)).Should().BeTrue();

        // The release that is live now still names every column; its query and insert shapes keep working.
        (await Scalar<long>(container, "SELECT count(*) FROM (SELECT use_dept_head_approval, use_hr_final_approval, use_supervisor_before_manager, allow_dotted_line_approval, auto_create_dept_on_import, auto_create_designation_on_import, require_import_preview_before_commit, allow_cross_dept_manager, allow_cross_location_manager, require_cost_center_for_payroll, require_grade_for_approval_policy FROM tenant_hr_configs) x"))
            .Should().Be(2);
        await Exec(container, "INSERT INTO attendance_policies (id, tenant_id, code, name, grace_minutes, late_threshold_minutes, early_exit_threshold_minutes, half_day_threshold_minutes, absent_threshold_minutes, standard_work_minutes, break_minutes, rounding_rule, requires_overtime_approval, allow_absence_to_leave_conversion, is_active, created_at_utc) VALUES (gen_random_uuid(), gen_random_uuid(), 'LIVE', 'Live shape', 10, 15, 15, 240, 120, 480, 60, 'NearestMinute', true, true, true, now())");

        // Rollback removes only the defaults: the columns stay and so does every value.
        await migrator.MigrateAsync(previous);
        foreach (var (table, column, _) in Expected)
            (await Scalar<string?>(container, $"SELECT column_default FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}'"))
                .Should().BeNull($"{table}.{column} is back to no default after Down");
        (await Scalar<int>(container, "SELECT late_threshold_minutes FROM attendance_policies WHERE code = 'OLD'")).Should().Be(33);

        await migrator.MigrateAsync(thisMigration);
        (await Scalar<string?>(container, "SELECT column_default FROM information_schema.columns WHERE table_name = 'attendance_policies' AND column_name = 'late_threshold_minutes'")).Should().Be("15");
    }

    private static async Task<string> InsertRowSql(PostgreSqlContainer container, string table, Dictionary<string, string> overrides)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t " +
            "AND is_nullable = 'NO' AND column_default IS NULL AND is_generated = 'NEVER' ORDER BY ordinal_position", conn);
        cmd.Parameters.AddWithValue("t", table);
        var columns = new List<string>(); var values = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                columns.Add(name);
                values.Add(overrides.TryGetValue(name, out var v) ? v : reader.GetString(1) switch
                {
                    "uuid" => "gen_random_uuid()",
                    "integer" or "smallint" or "bigint" or "numeric" or "double precision" or "real" => "0",
                    "boolean" => "false",
                    "timestamp with time zone" or "timestamp without time zone" => "now()",
                    _ => "'x'",
                });
            }
        foreach (var (name, value) in overrides.Where(o => !columns.Contains(o.Key))) { columns.Add(name); values.Add(value); }
        return new StringBuilder($"INSERT INTO {table} (").AppendJoin(", ", columns).Append(") VALUES (").AppendJoin(", ", values).Append(')').ToString();
    }

    private static async Task Exec(PostgreSqlContainer container, string sql)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(PostgreSqlContainer container, string sql)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? default! : (T)r;
    }
}
