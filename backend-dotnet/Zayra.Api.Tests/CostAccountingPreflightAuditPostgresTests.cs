using System.Text;
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
/// T-043: <c>scripts/cost-accounting-preflight-audit.sql</c> is the read-only gate in front of the production deploy of M1 (and of M3). It
/// counts, per table, rows whose cost-centre reference has no matching cost centre (the rows a NOT VALID foreign key would
/// reject on their next write) and rows that would violate each CHECK held back for M3. It must run BEFORE M1 (the new columns
/// do not exist yet, so they are skipped) and AFTER it (they are reported). Deploy is gated on every count being zero.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CostAccountingPreflightAuditPostgresTests
{
    private const string Suffix = "_CostAccountingFoundation";
    private static readonly Guid T = Guid.Parse("10000000-0000-0000-0000-0000000000e1");
    private static readonly Guid CoA = Guid.Parse("20000000-0000-0000-0000-0000000000e1");
    private static readonly Guid CoB = Guid.Parse("20000000-0000-0000-0000-0000000000e2");
    private static readonly Guid CcA = Guid.Parse("30000000-0000-0000-0000-0000000000e1");
    private static readonly Guid CcB = Guid.Parse("30000000-0000-0000-0000-0000000000e2");

    private static readonly string[] BeforeM1Labels =
    [
        "fk:departments.cost_center_id", "fk:positions.cost_center_id", "fk:employees.cost_center_id",
        "fk:timesheet_entries.cost_center_id", "fk:gl_account_mappings.segment_cost_center_id",
        "check:cost_centers.code_format", "check:finance_gl_entries.amount_nonnegative",
        "check:finance_gl_entries.has_account", "check:finance_gl_entries.currency_format",
    ];

    private static readonly string[] NewColumnLabels =
    [
        "fk:cost_centers.parent_id", "fk:companies.default_cost_center_id", "fk:payroll_earnings.cost_center_id",
        "fk:payroll_deductions.cost_center_id", "fk:payroll_slips.cost_center_id",
        "fk:eosb_provision_snapshots.cost_center_id", "fk:finance_gl_entries.cost_center_id",
    ];

    [Fact]
    public async Task Audit_ReportsOneRowPerSeededProblem_BeforeAndAfterM1_AndIgnoresCleanReferences()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var all = db.Database.GetMigrations().ToList();
        var thisMigration = all.SingleOrDefault(m => m.EndsWith(Suffix, StringComparison.Ordinal));
        thisMigration.Should().NotBeNull();
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var audit = File.ReadAllText(ScriptPath());

        await migrator.MigrateAsync(all[all.IndexOf(thisMigration!) - 1]);

        // Clean state first: valid references and well-formed rows are not counted.
        await Exec(container, await Insert(container, "companies", new() { ["id"] = Q(CoA), ["tenant_id"] = Q(T), ["legal_name_en"] = "'A'", ["registration_number"] = "'RA'", ["email_domain"] = "'a.example'" }));
        await Exec(container, await Insert(container, "cost_centers", new() { ["id"] = Q(CcA), ["tenant_id"] = Q(T), ["company_id"] = Q(CoA), ["code"] = "'CC-A'", ["name"] = "'A'" }));
        await Exec(container, await Insert(container, "departments", new() { ["tenant_id"] = Q(T), ["company_id"] = Q(CoA), ["code"] = "'D-OK'", ["name_en"] = "'ok'", ["cost_center_id"] = Q(CcA) }));
        await Exec(container, await Insert(container, "finance_gl_entries", Entry("100", "SAR", "5001", "2001")));
        var clean = await Run(container, audit);
        clean.Keys.Should().Contain(BeforeM1Labels);
        clean.Keys.Should().NotContain(NewColumnLabels, "those columns do not exist before M1");
        clean.Where(kv => kv.Value != 0).Should().BeEmpty("a clean database reports zero everywhere");

        // One problem per check. A dangling id has no cost centre at all; the other tenant's cost centre does not match either.
        var ghost = () => Q(Guid.NewGuid());
        await ExecReplica(container, await Insert(container, "departments", new() { ["tenant_id"] = Q(T), ["company_id"] = Q(CoA), ["code"] = "'D-BAD'", ["name_en"] = "'bad'", ["cost_center_id"] = ghost() }));
        await ExecReplica(container, await Insert(container, "positions", new() { ["tenant_id"] = Q(T), ["cost_center_id"] = ghost() }));
        await ExecReplica(container, await Insert(container, "employees", new() { ["id"] = "1", ["tenant_id"] = Q(T), ["employee_code"] = "'E1'", ["full_name"] = "'E1'", ["public_id"] = Q(Guid.NewGuid()), ["cost_center_id"] = ghost() }));
        await ExecReplica(container, await Insert(container, "timesheet_entries", new() { ["tenant_id"] = Q(T), ["cost_center_id"] = ghost() }));
        await ExecReplica(container, await Insert(container, "gl_account_mappings", new() { ["tenant_id"] = Q(T), ["driver_key"] = "'EARN:BASIC'", ["account_id"] = Q(Guid.NewGuid()), ["segment_cost_center_id"] = ghost() }));
        await Exec(container, await Insert(container, "cost_centers", new() { ["tenant_id"] = Q(T), ["company_id"] = Q(CoA), ["code"] = "' bad code'", ["name"] = "'x'" }));
        await Exec(container, await Insert(container, "finance_gl_entries", Entry("-5", "SAR", "5001", "2001")));
        await Exec(container, await Insert(container, "finance_gl_entries", Entry("5", "sar", "5001", "2001")));
        await Exec(container, await Insert(container, "finance_gl_entries", Entry("5", "SAR", "", "")));
        var before = await Run(container, audit);
        foreach (var label in BeforeM1Labels) before[label].Should().Be(1, label);

        // After M1 the new columns are audited too. Seeded with FK triggers off (the valid keys would refuse them).
        await migrator.MigrateAsync(thisMigration);
        await Exec(container, await Insert(container, "companies", new() { ["id"] = Q(CoB), ["tenant_id"] = Q(T), ["legal_name_en"] = "'B'", ["registration_number"] = "'RB'", ["email_domain"] = "'b.example'" }));
        await Exec(container, await Insert(container, "cost_centers", new() { ["id"] = Q(CcB), ["tenant_id"] = Q(T), ["company_id"] = Q(CoB), ["code"] = "'CC-B'", ["name"] = "'B'" }));
        await Exec(container, $"UPDATE companies SET default_cost_center_id = '{CcA}' WHERE id = '{CoA}'");   // clean: own company's cost centre
        var clean2 = await Run(container, audit);
        clean2.Keys.Should().Contain(BeforeM1Labels.Concat(NewColumnLabels));
        foreach (var label in NewColumnLabels) clean2[label].Should().Be(0, label);

        await ExecReplica(container, $"UPDATE cost_centers SET parent_id = '{Guid.NewGuid()}' WHERE id = '{CcA}'");
        await ExecReplica(container, $"UPDATE companies SET default_cost_center_id = '{CcB}' WHERE id = '{CoA}'");  // another company's cost centre
        await ExecReplica(container, await Insert(container, "payroll_earnings", new() { ["tenant_id"] = Q(T), ["cost_center_id"] = ghost() }));
        await ExecReplica(container, await Insert(container, "payroll_deductions", new() { ["tenant_id"] = Q(T), ["cost_center_id"] = ghost() }));
        await ExecReplica(container, await Insert(container, "payroll_slips", new() { ["tenant_id"] = Q(T), ["status"] = "'Draft'", ["cost_center_id"] = ghost() }));
        await ExecReplica(container, await Insert(container, "eosb_provision_snapshots", new() { ["tenant_id"] = Q(T), ["cost_center_id"] = ghost() }));
        await ExecReplica(container, await Insert(container, "finance_gl_entries", Entry("7", "SAR", "5001", "2001", $"{Q(CoA)}, 'CC-B', {Q(CcB)}")));  // company A line, company B cost centre
        var after = await Run(container, audit);
        foreach (var label in BeforeM1Labels) after[label].Should().Be(1, label);
        foreach (var label in NewColumnLabels) after[label].Should().Be(1, label);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> Entry(string amount, string currency, string debit, string credit, string? withCostCentre = null)
    {
        var row = new Dictionary<string, string>
        {
            ["tenant_id"] = Q(T), ["amount"] = amount, ["currency"] = $"'{currency}'", ["debit_account"] = $"'{debit}'",
            ["credit_account"] = $"'{credit}'", ["description"] = "'row'",
        };
        if (withCostCentre is not null)
        {
            var p = withCostCentre.Split(", ");
            row["company_id"] = p[0]; row["cost_center_code"] = p[1]; row["cost_center_id"] = p[2];
        }
        return row;
    }

    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "cost-accounting-preflight-audit.sql")))
        {
            if (File.Exists(Path.Combine(dir.FullName, "dotnet-tools.json"))) break;
            dir = dir.Parent;
        }
        var path = Path.Combine(dir!.FullName, "scripts", "cost-accounting-preflight-audit.sql");
        File.Exists(path).Should().BeTrue("the T-043 pre-deploy audit script must exist at scripts/cost-accounting-preflight-audit.sql");
        return path;
    }

    private static string Q(Guid g) => $"'{g}'";

    private static async Task<Dictionary<string, long>> Run(PostgreSqlContainer container, string sql)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(); // the script must be read-only: prove it by rolling back and by READ ONLY
        await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx)) await ro.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var map = new Dictionary<string, long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) map[reader.GetString(0)] = reader.GetInt64(1);
        return map;
    }

    private static async Task ExecReplica(PostgreSqlContainer container, string sql)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SET session_replication_role = replica; " + sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Exec(PostgreSqlContainer container, string sql)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> Insert(PostgreSqlContainer container, string table, Dictionary<string, string> overrides)
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
                    "date" => "current_date",
                    "jsonb" or "json" => "'{}'",
                    _ => "'x'",
                });
            }
        foreach (var (name, value) in overrides.Where(o => !columns.Contains(o.Key))) { columns.Add(name); values.Add(value); }
        return new StringBuilder($"INSERT INTO {table} (").AppendJoin(", ", columns).Append(") VALUES (").AppendJoin(", ", values).Append(')').ToString();
    }
}
