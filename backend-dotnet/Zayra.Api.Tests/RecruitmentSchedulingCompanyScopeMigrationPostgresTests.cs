using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Migrations;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

/// <summary>
/// The company dimension added to the recruitment scheduling records (interviews, feedback, assessments, requisitions, openings,
/// workforce plans) and to compliance renewals is additive, and its backfill follows each row's parent. A row with no parent that
/// resolves stays NULL, exactly as the candidates, applications and offers did (a NULL operational row is group-visible only, and the
/// startup CompanyScopeBackfill assigns the tenant default to the leftovers).
/// </summary>
public sealed class RecruitmentSchedulingCompanyScopeMigrationPostgresTests
{
    private const string MigrationSuffix = "_AddRecruitmentSchedulingCompanyScope";

    private static readonly string[] Tables =
        ["interview_schedules", "interview_feedbacks", "candidate_assessments", "manpower_requisitions", "job_openings", "workforce_plans", "compliance_renewals"];

    private static string MigrationFile()
    {
        var dir = Path.Combine(SourceScan.ResolveApiRoot(), "Migrations");
        return Directory.EnumerateFiles(dir, "*" + MigrationSuffix + ".cs").SingleOrDefault(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
               ?? throw new Xunit.Sdk.XunitException("No migration named *" + MigrationSuffix + " exists.");
    }

    [Fact]
    public void The_migration_adds_one_nullable_company_column_and_index_per_table_and_only_fills_missing_values()
    {
        var code = SourceScan.StripCSharpComments(File.ReadAllText(MigrationFile()));
        var up = code[code.IndexOf("void Up(", StringComparison.Ordinal)..code.IndexOf("void Down(", StringComparison.Ordinal)];

        foreach (var table in Tables)
        {
            var column = Regex.Match(up, @"AddColumn<Guid>\(\s*name: ""company_id"",\s*table: """ + table + @""",[^;]*?\);", RegexOptions.Singleline).Value;
            column.Should().NotBeEmpty(table).And.Contain("nullable: true").And.NotContain("defaultValue");
            up.Should().Contain($"IX_{table}_tenant_id_company_id");
        }
        up.Should().NotMatchRegex(@"\b(DropColumn|DropTable|RenameColumn|RenameTable|AlterColumn|DropIndex|DeleteData)\b");

        var sql = RecruitmentSchedulingBackfill();
        sql.Should().NotMatchRegex(@"(?i)\b(DELETE\s+FROM|DROP\s|TRUNCATE|ALTER\s)");
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            statement.Should().StartWith("UPDATE");
            statement.Should().Contain(".company_id IS NULL", "a backfill never overwrites an assigned company: " + statement);
            statement.Should().Contain(".tenant_id =", "a backfill never crosses tenants: " + statement);
        }
    }

    [Fact]
    public void The_migration_is_visible_to_ef_and_its_down_removes_what_it_added()
    {
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=u;Password=p").Options);
        db.Database.GetMigrations().Should().Contain(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        var code = SourceScan.StripCSharpComments(File.ReadAllText(MigrationFile()));
        var down = code[code.IndexOf("void Down(", StringComparison.Ordinal)..];
        foreach (var table in Tables) down.Should().Contain(table);
        down.Should().Contain("DropColumn").And.Contain("DropIndex");
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task The_backfill_follows_each_rows_parent_never_overwrites_never_crosses_tenants_and_leaves_orphans_null()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var thisMigration = all.Single(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        var previous = all[all.IndexOf(thisMigration) - 1];
        await migrator.MigrateAsync(previous);

        string G() => $"'{Guid.NewGuid()}'";
        string Unique() => $"'{Guid.NewGuid():N}'";
        var (t1, t2, cA, cB) = (G(), G(), G(), G());
        var (deptB, pubB, pubOther) = (G(), G(), G());
        var (appA, appUnattributed, openingA, openingViaRequisition, openingOrphan, openingForRequisition) = (G(), G(), G(), G(), G(), G());
        var (reqDept, reqEmployee, reqViaOpening, reqOrphan, reqOtherTenant) = (G(), G(), G(), G(), G());
        var (interviewA, interviewKept, interviewOrphan, missingInterview) = (G(), G(), G(), G());

        foreach (var t in new[] { t1, t2 }) await Exec(container, await InsertRowSql(container, "tenants", new() { ["id"] = t, ["slug"] = $"'{Guid.NewGuid():N}'" }));
        foreach (var c in new[] { cA, cB }) await Exec(container, await InsertRowSql(container, "companies", new() { ["id"] = c, ["tenant_id"] = t1, ["registration_number"] = $"'{Guid.NewGuid():N}'" }));
        await Exec(container, await InsertRowSql(container, "departments", new() { ["id"] = deptB, ["tenant_id"] = t1, ["company_id"] = cB }));
        await Exec(container, await InsertRowSql(container, "employees", new() { ["id"] = "9001", ["tenant_id"] = t1, ["company_id"] = cB, ["public_id"] = pubB }));

        await Exec(container, await InsertRowSql(container, "job_applications", new() { ["id"] = appA, ["tenant_id"] = t1, ["company_id"] = cA, ["job_opening_id"] = openingA }));
        await Exec(container, await InsertRowSql(container, "job_applications", new() { ["id"] = appUnattributed, ["tenant_id"] = t1, ["job_opening_id"] = openingOrphan }));

        await Exec(container, await InsertRowSql(container, "manpower_requisitions", new() { ["requisition_number"] = Unique(), ["id"] = reqDept, ["tenant_id"] = t1, ["department_id"] = deptB }));
        await Exec(container, await InsertRowSql(container, "manpower_requisitions", new() { ["requisition_number"] = Unique(), ["id"] = reqEmployee, ["tenant_id"] = t1, ["requested_by_employee_id"] = "9001" }));
        await Exec(container, await InsertRowSql(container, "manpower_requisitions", new() { ["requisition_number"] = Unique(), ["id"] = reqViaOpening, ["tenant_id"] = t1 }));
        await Exec(container, await InsertRowSql(container, "manpower_requisitions", new() { ["requisition_number"] = Unique(), ["id"] = reqOrphan, ["tenant_id"] = t1 }));
        await Exec(container, await InsertRowSql(container, "manpower_requisitions", new() { ["requisition_number"] = Unique(), ["id"] = reqOtherTenant, ["tenant_id"] = t2, ["department_id"] = deptB }));

        await Exec(container, await InsertRowSql(container, "job_openings", new() { ["job_code"] = Unique(), ["id"] = openingA, ["tenant_id"] = t1 }));
        await Exec(container, await InsertRowSql(container, "job_openings", new() { ["job_code"] = Unique(), ["id"] = openingViaRequisition, ["tenant_id"] = t1, ["requisition_id"] = reqDept }));
        await Exec(container, await InsertRowSql(container, "job_openings", new() { ["job_code"] = Unique(), ["id"] = openingOrphan, ["tenant_id"] = t1 }));
        await Exec(container, await InsertRowSql(container, "job_openings", new() { ["job_code"] = Unique(), ["id"] = openingForRequisition, ["tenant_id"] = t1, ["requisition_id"] = reqViaOpening }));
        await Exec(container, await InsertRowSql(container, "job_applications", new() { ["id"] = G(), ["tenant_id"] = t1, ["company_id"] = cA, ["job_opening_id"] = openingForRequisition }));

        await Exec(container, await InsertRowSql(container, "workforce_plans", new() { ["plan_code"] = Unique(), ["id"] = "'00000000-0000-0000-0000-0000000000a1'", ["tenant_id"] = t1, ["department_id"] = deptB }));
        await Exec(container, await InsertRowSql(container, "workforce_plans", new() { ["plan_code"] = Unique(), ["id"] = "'00000000-0000-0000-0000-0000000000a2'", ["tenant_id"] = t1 }));

        await Exec(container, await InsertRowSql(container, "interview_schedules", new() { ["id"] = interviewA, ["tenant_id"] = t1, ["application_id"] = appA }));
        await Exec(container, await InsertRowSql(container, "interview_schedules", new() { ["id"] = interviewKept, ["tenant_id"] = t1, ["application_id"] = appA }));
        await Exec(container, await InsertRowSql(container, "interview_schedules", new() { ["id"] = interviewOrphan, ["tenant_id"] = t1, ["application_id"] = appUnattributed }));
        await Exec(container, await InsertRowSql(container, "candidate_assessments", new() { ["id"] = "'00000000-0000-0000-0000-0000000000b1'", ["tenant_id"] = t1, ["application_id"] = appA }));
        await Exec(container, await InsertRowSql(container, "interview_feedbacks", new() { ["id"] = "'00000000-0000-0000-0000-0000000000c1'", ["tenant_id"] = t1, ["interview_schedule_id"] = interviewA, ["application_id"] = appUnattributed }));
        await Exec(container, await InsertRowSql(container, "interview_feedbacks", new() { ["id"] = "'00000000-0000-0000-0000-0000000000c2'", ["tenant_id"] = t1, ["interview_schedule_id"] = missingInterview, ["application_id"] = appA }));
        await Exec(container, await InsertRowSql(container, "compliance_renewals", new() { ["id"] = "'00000000-0000-0000-0000-0000000000d1'", ["tenant_id"] = t1, ["employee_id"] = pubB }));
        await Exec(container, await InsertRowSql(container, "compliance_renewals", new() { ["id"] = "'00000000-0000-0000-0000-0000000000d2'", ["tenant_id"] = t1, ["employee_id"] = pubOther }));

        await migrator.MigrateAsync(thisMigration);
        // A company assigned after the migration (by a user, or by an earlier run) is never overwritten, and a re-run changes nothing else.
        await Exec(container, $"UPDATE interview_schedules SET company_id = {cB} WHERE id = {interviewKept}");
        await Exec(container, RecruitmentSchedulingBackfill());

        async Task<string?> Company(string table, string id) =>
            (await Scalar<Guid?>(container, $"SELECT company_id FROM {table} WHERE id = {id}"))?.ToString();
        string Lit(string quoted) => quoted.Trim('\'');

        (await Company("manpower_requisitions", reqDept)).Should().Be(Lit(cB), "the owning department's company");
        (await Company("manpower_requisitions", reqEmployee)).Should().Be(Lit(cB), "else the requester's company");
        (await Company("manpower_requisitions", reqViaOpening)).Should().Be(Lit(cA), "else the company of the opening raised from it");
        (await Company("manpower_requisitions", reqOrphan)).Should().BeNull("no parent resolves: left for the startup default-company sweep");
        (await Company("manpower_requisitions", reqOtherTenant)).Should().BeNull("a department of another tenant is never used");

        (await Company("job_openings", openingA)).Should().Be(Lit(cA), "the company its applications already belong to");
        (await Company("job_openings", openingViaRequisition)).Should().Be(Lit(cB), "else the source requisition's company");
        (await Company("job_openings", openingForRequisition)).Should().Be(Lit(cA));
        (await Company("job_openings", openingOrphan)).Should().BeNull("its only application has no company either");

        (await Company("workforce_plans", "'00000000-0000-0000-0000-0000000000a1'")).Should().Be(Lit(cB));
        (await Company("workforce_plans", "'00000000-0000-0000-0000-0000000000a2'")).Should().BeNull();
        (await Company("interview_schedules", interviewA)).Should().Be(Lit(cA));
        (await Company("interview_schedules", interviewKept)).Should().Be(Lit(cB), "an assigned company is never overwritten");
        (await Company("interview_schedules", interviewOrphan)).Should().BeNull();
        (await Company("candidate_assessments", "'00000000-0000-0000-0000-0000000000b1'")).Should().Be(Lit(cA));
        (await Company("interview_feedbacks", "'00000000-0000-0000-0000-0000000000c1'")).Should().Be(Lit(cA), "its interview's company before its application's");
        (await Company("interview_feedbacks", "'00000000-0000-0000-0000-0000000000c2'")).Should().Be(Lit(cA), "its application's when the interview is gone");
        (await Company("compliance_renewals", "'00000000-0000-0000-0000-0000000000d1'")).Should().Be(Lit(cB), "the employee's company");
        (await Company("compliance_renewals", "'00000000-0000-0000-0000-0000000000d2'")).Should().BeNull("the employee does not resolve");

        foreach (var table in Tables)
            (await Scalar<long>(container, $"SELECT count(*) FROM {table}")).Should().BeGreaterThan(0, table + " still has its rows");

        await migrator.MigrateAsync(previous);
        foreach (var table in Tables)
            (await Scalar<long>(container, $"SELECT count(*) FROM information_schema.columns WHERE table_name = '{table}' AND column_name = 'company_id'"))
                .Should().Be(0, table + " loses the column on rollback");
    }

    private static string RecruitmentSchedulingBackfill() => AddRecruitmentSchedulingCompanyScope.BackfillSql;

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
                    "date" => "current_date",
                    "json" or "jsonb" or "ARRAY" => "'{}'",
                    "interval" => "'0 seconds'",
                    "time without time zone" => "'00:00'",
                    "bytea" => "'\\x00'",
                    _ => "'x'",
                });
            }
        foreach (var (name, value) in overrides.Where(o => !columns.Contains(o.Key))) { columns.Add(name); values.Add(value); }
        return new StringBuilder($"INSERT INTO {table} (").AppendJoin(", ", columns).Append(") VALUES (").AppendJoin(", ", values).Append(')').ToString();
    }
}
