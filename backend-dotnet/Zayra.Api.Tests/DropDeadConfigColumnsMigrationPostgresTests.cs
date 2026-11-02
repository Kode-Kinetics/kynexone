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
/// Release 2 of 2: drops the 16 unread columns (11 on tenant_hr_configs, 5 on attendance_policies) that release 1 stopped
/// mapping. Irreversible for the stored values; Down re-adds each column with its original type, NOT NULL and default so
/// the schema (not the data) can be rolled back. Real migration chain, real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DropDeadConfigColumnsMigrationPostgresTests
{
    private const string MigrationSuffix = "_DropDeadConfigColumns";

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

    private static readonly string[] Types =
        [.. Expected.Select(e => e.Column switch
        {
            "late_threshold_minutes" or "absent_threshold_minutes" => "integer",
            "rounding_rule" => "text",
            _ => "boolean",
        })];

    [Fact]
    public async Task DropsTheSixteenColumns_KeepsEverythingElse_AndDownReAddsThemWithTheirTypesAndDefaults()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var thisMigration = all.SingleOrDefault(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        thisMigration.Should().NotBeNull("the migration that drops the unread columns exists");
        var previous = all[all.IndexOf(thisMigration!) - 1];
        previous.Should().EndWith("_StopMappingDeadConfigColumns", "release 2 follows release 1 directly");
        // Later migrations may follow (other releases land after it); the test moves to and from THIS migration explicitly,
        // so it only needs the drop to come after release 1, which the line above pins.
        // Present and ordered after its release-1 predecessor; later migrations may follow it (I-11).
        all.IndexOf(thisMigration!).Should().BeGreaterThan(all.IndexOf(previous));

        await migrator.MigrateAsync(previous);
        const string tenant = "'10000000-0000-0000-0000-0000000000d2'";
        await Exec(container, await InsertRowSql(container, "tenant_hr_configs", new() { ["tenant_id"] = tenant, ["use_dept_head_approval"] = "false" }));
        await Exec(container, await InsertRowSql(container, "attendance_policies", new() { ["tenant_id"] = tenant, ["code"] = "'KEEP'", ["grace_minutes"] = "7", ["late_threshold_minutes"] = "33" }));

        await migrator.MigrateAsync(thisMigration);
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(thisMigration, "the drop is applied");

        foreach (var (table, column, _) in Expected)
            (await Scalar<long>(container, $"SELECT count(*) FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}'"))
                .Should().Be(0, $"{table}.{column} is dropped");
        // Everything else survives, including the live engagement and establishment settings and the rows themselves.
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'tenant_hr_configs' AND column_name IN ('establishment_enforcement_mode','engagement_min_cohort_size','engagement_free_text_retention_months')")).Should().Be(3);
        (await Scalar<int>(container, "SELECT grace_minutes FROM attendance_policies WHERE code = 'KEEP'")).Should().Be(7);
        (await Scalar<long>(container, "SELECT count(*) FROM tenant_hr_configs")).Should().Be(1);
        // The current model (which never named them) still reads and writes both tables.
        db.TenantHrConfigs.Add(new TenantHrConfig { TenantId = Guid.NewGuid() });
        db.AttendancePolicies.Add(new AttendancePolicy { TenantId = Guid.NewGuid(), Code = "NEW", Name = "New policy" });
        await db.SaveChangesAsync();
        (await db.AttendancePolicies.CountAsync()).Should().Be(2);

        // Down: the schema comes back with the original type, NOT NULL and default; the dropped values do not.
        await migrator.MigrateAsync(previous);
        for (var i = 0; i < Expected.Length; i++)
        {
            var (table, column, expectedDefault) = Expected[i];
            (await Scalar<string>(container, $"SELECT data_type || '|' || is_nullable FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}'"))
                .Should().Be((Types[i] == "boolean" ? "boolean" : Types[i]) + "|NO", $"{table}.{column} is back, NOT NULL");
            (await Scalar<string?>(container, $"SELECT column_default FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}'"))
                .Should().Be(expectedDefault, $"{table}.{column} is back with its default");
        }
        (await Scalar<bool>(container, "SELECT use_dept_head_approval FROM tenant_hr_configs WHERE tenant_id = " + tenant)).Should().BeTrue("the dropped value is gone; the default is what comes back");
        (await Scalar<int>(container, "SELECT grace_minutes FROM attendance_policies WHERE code = 'KEEP'")).Should().Be(7);

        await migrator.MigrateAsync(thisMigration);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'attendance_policies' AND column_name = 'rounding_rule'")).Should().Be(0);
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
