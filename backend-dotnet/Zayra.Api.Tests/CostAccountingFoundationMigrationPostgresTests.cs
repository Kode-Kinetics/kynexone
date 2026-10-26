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
/// M1 of cost-centre accounting (v1-spec 1.1-1.10), <c>*_CostAccountingFoundation</c>, on the real migration chain and
/// real PostgreSQL. Expand-only and behaviour-free: it must apply over a database that already holds dangling
/// cost-centre ids, odd codes and legacy ledger rows (the NOT VALID constraints bind only new writes), backfill
/// <c>employees.cost_center_is_override</c>, leave every existing account on rule Optional, give the database the
/// keys and checks the contract names, and roll back and re-apply cleanly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CostAccountingFoundationMigrationPostgresTests
{
    private const string Suffix = "_CostAccountingFoundation";
    private const string CheckViolation = "23514", UniqueViolation = "23505", ForeignKeyViolation = "23503";

    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OtherTenant = Guid.Parse("10000000-0000-0000-0000-0000000000a2");
    private static readonly Guid CompanyA = Guid.Parse("20000000-0000-0000-0000-0000000000a1");
    private static readonly Guid CompanyB = Guid.Parse("20000000-0000-0000-0000-0000000000a2");
    private static readonly Guid CcA1 = Guid.Parse("30000000-0000-0000-0000-0000000000a1");
    private static readonly Guid CcA2 = Guid.Parse("30000000-0000-0000-0000-0000000000a2");
    private static readonly Guid CcB1 = Guid.Parse("30000000-0000-0000-0000-0000000000b1");
    private static readonly Guid CcGroup = Guid.Parse("30000000-0000-0000-0000-0000000000c1");
    private static readonly Guid CcOtherTenant = Guid.Parse("30000000-0000-0000-0000-0000000000d1");
    private static readonly Guid DeptA = Guid.Parse("40000000-0000-0000-0000-0000000000a1");
    private static readonly Guid DeptDangling = Guid.Parse("40000000-0000-0000-0000-0000000000a2");

    // table, constraint, validated after M1 (false = NOT VALID: binds new writes, leaves legacy rows alone)
    private static readonly (string Table, string Name, bool Validated)[] ForeignKeys =
    [
        ("cost_centers", "fk_cost_centers__parent", true),
        ("departments", "fk_departments__cost_center", false),
        ("positions", "fk_positions__cost_center", false),
        ("employees", "fk_employees__cost_center", false),
        ("timesheet_entries", "fk_timesheet_entries__cost_center", false),
        ("payroll_earnings", "fk_payroll_earnings__cost_center", false),
        ("payroll_deductions", "fk_payroll_deductions__cost_center", false),
        ("payroll_slips", "fk_payroll_slips__cost_center", false),
        ("eosb_provision_snapshots", "fk_eosb_provision_snapshots__cost_center", false),
        ("gl_account_mappings", "fk_gl_account_mappings__segment_cost_center", false),
        ("companies", "fk_companies__default_cost_center", true),
        ("finance_gl_entries", "fk_finance_gl_entries__cost_center", true),
    ];

    private static readonly (string Table, string Name, bool Validated)[] Checks =
    [
        ("cost_centers", "ck_cost_centers__functional_class", true),
        ("cost_centers", "ck_cost_centers__gl_segment_format", true),
        ("cost_centers", "ck_cost_centers__parent_not_self", true),
        ("gl_accounts", "ck_gl_accounts__cost_center_rule", true),
        ("gl_account_mappings", "ck_gl_account_mappings__functional_class", true),
        ("companies", "ck_companies__cost_centre_small_cell_min", true),
        ("payroll_earnings", "ck_payroll_earnings__cost_center_source", true),
        ("payroll_deductions", "ck_payroll_deductions__cost_center_source", true),
        ("finance_gl_entries", "ck_finance_gl_entries__cost_center_pair", true),
        ("finance_gl_entries", "ck_finance_gl_entries__cost_center_needs_company", true),
    ];

    [Fact]
    public async Task Migration_UpgradesALegacyDatabaseInPlace_AndGivesTheDatabaseItsKeysAndChecks()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var (migrator, this_, before) = await Chain(container);

        await migrator.MigrateAsync(before);
        await SeedLegacy(container);
        await migrator.MigrateAsync(this_);

        // ── Existing tenants are byte-identical: defaults, backfill, nothing validated against legacy rows ──────────
        (await Scalar<long>(container, "SELECT count(*) FROM gl_accounts WHERE cost_center_rule = 'Optional'")).Should().Be(1);
        (await Scalar<long>(container, "SELECT count(*) FROM companies WHERE cost_accounting_enabled = false AND default_cost_center_id IS NULL AND cost_centre_small_cell_min = 0")).Should().Be(2);
        (await Scalar<long>(container, "SELECT count(*) FROM payroll_runs WHERE cost_accounting_applied = false AND cost_center_resolution_hash IS NULL")).Should().Be(1);
        (await Scalar<long>(container, "SELECT count(*) FROM finance_gl_entries WHERE cost_center_id IS NULL AND cost_center_code IS NULL AND posting_key IS NULL")).Should().Be(1, "the legacy ledger row is untouched");
        (await Scalar<long>(container, "SELECT count(*) FROM cost_centers WHERE parent_id IS NULL AND functional_class IS NULL AND gl_segment IS NULL")).Should().Be(5);

        // The employee column is an override only where it differs from the department's (v1-spec 1.5).
        (await Overrides(container)).Should().Equal(new Dictionary<string, bool>
        {
            ["E-INHERITED"] = false,   // equals its department's cost centre: a copy the resolver made
            ["E-OVERRIDE"] = true,     // differs from its department's
            ["E-NO-DEPT"] = true,      // has a cost centre and no department to inherit from
            ["E-NO-CC"] = false,       // nothing to override
        });

        // ── Foreign keys and checks: right validated state, restrict, simple match ──────────────────────────────────
        foreach (var (table, name, validated) in ForeignKeys)
        {
            var row = await Row(container,
                "SELECT convalidated::text, confdeltype::text, confmatchtype::text FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid " +
                $"WHERE c.contype = 'f' AND c.conname = '{name}' AND t.relname = '{table}'");
            row.Should().NotBeNull($"{table} must carry foreign key {name}");
            row![0].Should().Be(validated ? "true" : "false", $"{name} validated state");
            row[1].Should().Be("r", $"{name} is ON DELETE RESTRICT");
            row[2].Should().Be("s", $"{name} is MATCH SIMPLE, so a NULL cost centre is skipped");
        }
        foreach (var (table, name, validated) in Checks)
        {
            var row = await Row(container,
                "SELECT convalidated::text FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid " +
                $"WHERE c.contype = 'c' AND c.conname = '{name}' AND t.relname = '{table}'");
            row.Should().NotBeNull($"{table} must carry check {name}");
            row![0].Should().Be(validated ? "true" : "false", $"{name} validated state");
        }
        (await Scalar<long>(container, "SELECT count(*) FROM pg_constraint WHERE conname IN ('ck_cost_centers__code_format', 'ck_finance_gl_entries__amount_nonnegative', 'ck_finance_gl_entries__has_account', 'ck_finance_gl_entries__currency_format')"))
            .Should().Be(0, "these four wait for M3: they would reject an UPDATE of a legacy row");
        (await Scalar<string>(container, "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'fk_finance_gl_entries__cost_center'"))
            .Should().Contain("(tenant_id, company_id, cost_center_id)").And.Contain("cost_centers(tenant_id, company_id, id)");
        (await Scalar<string>(container, "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'fk_companies__default_cost_center'"))
            .Should().Contain("(tenant_id, id, default_cost_center_id)").And.Contain("cost_centers(tenant_id, company_id, id)");
        (await Scalar<string>(container, "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'fk_departments__cost_center'"))
            .Should().Contain("(tenant_id, cost_center_id)").And.Contain("cost_centers(tenant_id, id)");
        (await Scalar<long>(container, "SELECT count(*) FROM pg_constraint WHERE contype = 'u' AND conname IN ('uq_cost_centers__tenant_id', 'uq_cost_centers__tenant_company_id')"))
            .Should().Be(2, "the two composite keys the foreign keys point at");

        // ── Indexes: the four the contract names; the old mapping unique is swapped, the FK columns stay unindexed ──
        var indexes = await Column(container, "SELECT indexname || ' :: ' || indexdef FROM pg_indexes WHERE schemaname = 'public'");
        indexes.Should().Contain(i => i.StartsWith("ix_finance_gl_entries_cc_period") && i.Contains("(tenant_id, cost_center_id, period)") && i.Contains("WHERE (cost_center_id IS NOT NULL)"));
        indexes.Should().Contain(i => i.StartsWith("ux_finance_gl_entries_posting_key") && i.Contains("UNIQUE") && i.Contains("(tenant_id, posting_key)") && i.Contains("WHERE (posting_key IS NOT NULL)"));
        indexes.Should().Contain(i => i.StartsWith("ux_cost_centers_scope_gl_segment") && i.Contains("UNIQUE") && i.Contains("NULLS NOT DISTINCT") && i.Contains("(tenant_id, company_id, gl_segment)") && i.Contains("gl_segment IS NOT NULL"));
        indexes.Should().Contain(i => i.StartsWith("ux_gl_account_mappings_scope_driver_class") && i.Contains("UNIQUE") && i.Contains("NULLS NOT DISTINCT") && i.Contains("(tenant_id, company_id, driver_key, functional_class)"));
        indexes.Should().NotContain(i => i.StartsWith("ux_gl_account_mappings_scope_driver ::"), "the old unique is replaced in the same migration");
        foreach (var table in new[] { "departments", "positions", "employees", "payroll_earnings", "payroll_deductions", "payroll_slips", "eosb_provision_snapshots" })
            indexes.Where(i => i.Contains($" ON public.{table} ")).Should().NotContain(i => i.Contains("cost_center_id"), $"{table}.cost_center_id is deliberately unindexed (v1-spec 1.6)");

        // ── Legacy rows survive; new writes are bound by what NOT VALID covers ─────────────────────────────────────
        await Exec(container, "UPDATE departments SET name_en = 'renamed' WHERE id = '" + DeptDangling + "'");
        var dangling = async () => await Exec(container, await Insert(container, "departments", Dept(Guid.NewGuid(), "D-NEW", Guid.NewGuid())));
        (await dangling.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(ForeignKeyViolation, "a new dangling cost centre id is refused even though the old one stays");
        var otherTenantRef = async () => await Exec(container, await Insert(container, "departments", Dept(Guid.NewGuid(), "D-XT", CcOtherTenant)));
        (await otherTenantRef.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(ForeignKeyViolation, "the composite (tenant_id, id) key rejects another tenant's cost centre");
        await Exec(container, await Insert(container, "departments", Dept(Guid.NewGuid(), "D-OK", CcA1)));
        var restrict = async () => await Exec(container, $"DELETE FROM cost_centers WHERE id = '{CcA1}'");
        (await restrict.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(ForeignKeyViolation, "ON DELETE RESTRICT");

        // ── cost_centers ─────────────────────────────────────────────────────────────────────────────────────────────
        await Expect(container, CheckViolation, $"UPDATE cost_centers SET parent_id = id WHERE id = '{CcA2}'");
        await Expect(container, ForeignKeyViolation, $"UPDATE cost_centers SET parent_id = '{CcOtherTenant}' WHERE id = '{CcA2}'");
        await Exec(container, $"UPDATE cost_centers SET parent_id = '{CcA1}' WHERE id = '{CcA2}'");
        await Expect(container, CheckViolation, $"UPDATE cost_centers SET functional_class = 'MARKETING' WHERE id = '{CcA1}'");
        await Exec(container, $"UPDATE cost_centers SET functional_class = 'CAPITALISABLE' WHERE id = '{CcA1}'"); // reserved, but storable in v1
        await Expect(container, CheckViolation, $"UPDATE cost_centers SET gl_segment = 'has space' WHERE id = '{CcA1}'");
        await Exec(container, $"UPDATE cost_centers SET gl_segment = '6100-OPS' WHERE id = '{CcA1}'");
        await Expect(container, UniqueViolation, $"UPDATE cost_centers SET gl_segment = '6100-OPS' WHERE id = '{CcA2}'");
        await Exec(container, $"UPDATE cost_centers SET gl_segment = '6100-OPS' WHERE id = '{CcB1}'"); // same segment, other company
        await Exec(container, $"UPDATE cost_centers SET is_deleted = true, gl_segment = '6100-OPS' WHERE id = '{CcA2}'"); // a deleted row frees the segment
        // Legacy rows that would fail the M3 checks stay editable and new ones are accepted: nothing here changes behaviour.
        await Exec(container, await Insert(container, "cost_centers", CostCentre(Guid.NewGuid(), CompanyA, "bad code!")));
        await Exec(container, $"UPDATE cost_centers SET name = 'renamed' WHERE id = '{CcGroup}'");

        // ── gl_accounts / gl_account_mappings ─────────────────────────────────────────────────────────────────────────
        await Expect(container, CheckViolation, "UPDATE gl_accounts SET cost_center_rule = 'Maybe'");
        foreach (var rule in new[] { "Required", "Optional", "NotAllowed" }) await Exec(container, $"UPDATE gl_accounts SET cost_center_rule = '{rule}'");
        await Exec(container, "UPDATE gl_accounts SET cost_center_rule = 'Optional'");
        await Expect(container, UniqueViolation, await Insert(container, "gl_account_mappings", Mapping("EARN:BASIC", null)), "still one mapping per driver when no class is given");
        await Exec(container, await Insert(container, "gl_account_mappings", Mapping("EARN:BASIC", "COGS")));
        await Expect(container, UniqueViolation, await Insert(container, "gl_account_mappings", Mapping("EARN:BASIC", "COGS")));
        await Expect(container, CheckViolation, await Insert(container, "gl_account_mappings", Mapping("EARN:BASIC", "NOPE")));
        await Expect(container, ForeignKeyViolation, $"UPDATE gl_account_mappings SET segment_cost_center_id = '{Guid.NewGuid()}' WHERE driver_key = 'EARN:BASIC' AND functional_class IS NULL");
        await Exec(container, $"UPDATE gl_account_mappings SET segment_cost_center_id = '{CcA1}' WHERE driver_key = 'EARN:BASIC' AND functional_class IS NULL");

        // ── companies ───────────────────────────────────────────────────────────────────────────────────────────────
        await Expect(container, ForeignKeyViolation, $"UPDATE companies SET default_cost_center_id = '{CcB1}' WHERE id = '{CompanyA}'", "another company's cost centre");
        await Expect(container, ForeignKeyViolation, $"UPDATE companies SET default_cost_center_id = '{CcGroup}' WHERE id = '{CompanyA}'", "a group-level (null-company) cost centre cannot be a company default");
        await Exec(container, $"UPDATE companies SET default_cost_center_id = '{CcA1}' WHERE id = '{CompanyA}'");
        await Expect(container, CheckViolation, $"UPDATE companies SET cost_centre_small_cell_min = -1 WHERE id = '{CompanyA}'");

        // ── finance_gl_entries ──────────────────────────────────────────────────────────────────────────────────────
        await Exec(container, await Insert(container, "finance_gl_entries", Entry(CompanyA, CcA1, "CC1", key: "k1")));
        await Expect(container, ForeignKeyViolation, await Insert(container, "finance_gl_entries", Entry(CompanyA, CcB1, "CCB")), "company A line, company B cost centre");
        await Expect(container, ForeignKeyViolation, await Insert(container, "finance_gl_entries", Entry(CompanyA, CcGroup, "GRP")), "a group-level cost centre cannot be stamped");
        await Expect(container, ForeignKeyViolation, await Insert(container, "finance_gl_entries", Entry(CompanyA, CcOtherTenant, "XT")), "another tenant's cost centre");
        await Expect(container, ForeignKeyViolation, await Insert(container, "finance_gl_entries", Entry(CompanyA, Guid.NewGuid(), "NONE")), "a dangling id");
        await Expect(container, CheckViolation, await Insert(container, "finance_gl_entries", Entry(null, CcA1, "CC1")), "a cost centre on a company-less row");
        await Expect(container, CheckViolation, await Insert(container, "finance_gl_entries", Entry(CompanyA, CcA1, null)), "id without its code snapshot");
        await Expect(container, CheckViolation, await Insert(container, "finance_gl_entries", Entry(CompanyA, null, "CC1")), "code snapshot without an id");
        await Expect(container, UniqueViolation, await Insert(container, "finance_gl_entries", Entry(CompanyA, CcA1, "CC1", key: "k1")), "duplicate (tenant_id, posting_key)");
        await Exec(container, await Insert(container, "finance_gl_entries", Entry(null, null, null, key: "k1", tenant: OtherTenant))); // another tenant may reuse a key
        await Exec(container, await Insert(container, "finance_gl_entries", Entry(CompanyA, null, null)));
        await Exec(container, await Insert(container, "finance_gl_entries", Entry(CompanyA, null, null))); // NULL keys never conflict
        // The legacy row (negative amount, lowercase currency, no accounts) takes an erp_* status update as before.
        await Exec(container, "UPDATE finance_gl_entries SET erp_posting_status = 'Posted' WHERE description = 'legacy'");

        // ── payroll tables ──────────────────────────────────────────────────────────────────────────────────────────
        await Expect(container, CheckViolation, "UPDATE payroll_earnings SET cost_center_source = 'Guess'");
        await Expect(container, CheckViolation, "UPDATE payroll_deductions SET cost_center_source = 'Guess'");
        foreach (var source in new[] { "Employee", "Position", "Department", "ParentDepartment", "CompanyDefault", "MappingFixed", "RolledUp" })
            await Exec(container, $"UPDATE payroll_earnings SET cost_center_source = '{source}', cost_center_id = '{CcA1}'");
        await Expect(container, ForeignKeyViolation, $"UPDATE payroll_slips SET cost_center_id = '{Guid.NewGuid()}'");
    }

    [Fact]
    public async Task Migration_GivesUpAfterLockTimeout_AndRollsBack_ThenReapplies_AndRestoresTheOldUniqueOnDown()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var (migrator, this_, before) = await Chain(container);
        await migrator.MigrateAsync(before);
        await SeedLegacy(container);

        // A long transaction holding companies: the DDL gives up after lock_timeout (55P03) instead of queueing behind it.
        await using (var blocker = new NpgsqlConnection(container.GetConnectionString()))
        {
            await blocker.OpenAsync();
            await using var tx = await blocker.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("SELECT count(*) FROM companies", blocker, tx)) await hold.ExecuteScalarAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var started = System.Diagnostics.Stopwatch.StartNew();
            var failure = await Record.ExceptionAsync(() => migrator.MigrateAsync(this_, cts.Token));
            started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "lock_timeout must end the wait, not the test's own cancellation");
            failure.Should().NotBeNull();
            Causes(failure!).OfType<PostgresException>().Should().Contain(e => e.SqlState == "55P03", failure!.ToString());
            await tx.RollbackAsync();
        }
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE column_name IN ('cost_accounting_enabled','cost_center_rule','posting_key')"))
            .Should().Be(0, "the timed-out migration left nothing behind");

        await migrator.MigrateAsync(this_);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE column_name IN ('cost_accounting_enabled','cost_center_rule','posting_key')")).Should().Be(3);

        // Down: every column and constraint is gone and the old (tenant, company, driver) unique is back.
        await migrator.MigrateAsync(before);
        (await Scalar<long>(container, "SELECT count(*) FROM information_schema.columns WHERE column_name IN ('cost_accounting_enabled','default_cost_center_id','cost_center_is_override','cost_center_rule','cost_center_resolution_hash','cost_accounting_applied','posting_key','gl_segment','functional_class','amount_scale','cost_center_source','cost_center_gl_segment','cost_center_code')"))
            .Should().Be(0);
        (await Scalar<long>(container, "SELECT count(*) FROM pg_constraint WHERE conname LIKE 'fk\\_%\\_\\_cost\\_center%' OR conname LIKE 'uq\\_cost\\_centers\\_\\_%' OR conname LIKE 'ck\\_cost\\_centers\\_\\_%' OR conname LIKE 'fk\\_%\\_\\_segment\\_cost\\_center'"))
            .Should().Be(0);
        (await Column(container, "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND indexname LIKE 'ux\\_%'"))
            .Should().Contain("ux_gl_account_mappings_scope_driver")
            .And.NotContain(["ux_gl_account_mappings_scope_driver_class", "ux_finance_gl_entries_posting_key", "ux_cost_centers_scope_gl_segment"]);
        await Expect(container, UniqueViolation, await Insert(container, "gl_account_mappings", Mapping("EARN:BASIC", null)));

        // Re-applied: the backfill and defaults come back identically.
        await migrator.MigrateAsync(this_);
        (await Overrides(container)).Should().Contain("E-OVERRIDE", true).And.Contain("E-INHERITED", false);
        (await PendingMigrations(container)).Should().NotContain(this_, "nothing at or before this migration is pending (later migrations may exist)");
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task SeedLegacy(PostgreSqlContainer container)
    {
        foreach (var (id, tenant, name) in new[] { (CompanyA, Tenant, "A"), (CompanyB, Tenant, "B") })
            await Exec(container, await Insert(container, "companies", new()
            {
                ["id"] = Q(id), ["tenant_id"] = Q(tenant), ["legal_name_en"] = $"'Co {name}'", ["registration_number"] = $"'R-{name}'",
                ["email_domain"] = $"'{name.ToLowerInvariant()}.example'", ["is_active"] = "true", ["is_deleted"] = "false",
            }));

        await Exec(container, await Insert(container, "cost_centers", CostCentre(CcA1, CompanyA, "CC-A1")));
        await Exec(container, await Insert(container, "cost_centers", CostCentre(CcA2, CompanyA, "CC-A2")));
        await Exec(container, await Insert(container, "cost_centers", CostCentre(CcB1, CompanyB, "CC-B1")));
        await Exec(container, await Insert(container, "cost_centers", CostCentre(CcGroup, null, " bad code")));        // legacy: breaks the new code format
        await Exec(container, await Insert(container, "cost_centers", CostCentre(CcOtherTenant, null, "CC-X", OtherTenant)));

        await Exec(container, await Insert(container, "departments", Dept(DeptA, "D-A", CcA1)));
        await Exec(container, await Insert(container, "departments", Dept(DeptDangling, "D-DANGLING", Guid.NewGuid())));  // legacy: dangling id

        await Exec(container, await Insert(container, "employees", Emp("E-INHERITED", DeptA, CcA1)));
        await Exec(container, await Insert(container, "employees", Emp("E-OVERRIDE", DeptA, CcA2)));
        await Exec(container, await Insert(container, "employees", Emp("E-NO-DEPT", null, CcA1)));
        await Exec(container, await Insert(container, "employees", Emp("E-NO-CC", DeptA, null)));

        var account = Guid.NewGuid();
        await Exec(container, await Insert(container, "gl_accounts", new() { ["id"] = Q(account), ["tenant_id"] = Q(Tenant), ["code"] = "'5001'", ["name"] = "'Basic'", ["account_type"] = "'Expense'", ["is_active"] = "true" }));
        await Exec(container, await Insert(container, "gl_account_mappings", new() { ["tenant_id"] = Q(Tenant), ["company_id"] = "NULL", ["driver_key"] = "'EARN:BASIC'", ["account_id"] = Q(account), ["is_active"] = "true" }));

        await Exec(container, await Insert(container, "payroll_runs", new() { ["tenant_id"] = Q(Tenant), ["company_id"] = Q(CompanyA), ["status"] = "'Draft'" }));
        await Exec(container, await Insert(container, "payroll_earnings", new() { ["tenant_id"] = Q(Tenant) }));
        await Exec(container, await Insert(container, "payroll_deductions", new() { ["tenant_id"] = Q(Tenant) }));
        await Exec(container, await Insert(container, "payroll_slips", new() { ["tenant_id"] = Q(Tenant), ["status"] = "'Draft'" }));

        // A legacy ledger row that would fail every NOT VALID check: it must survive the upgrade, and stay editable.
        await Exec(container, await Insert(container, "finance_gl_entries", new()
        {
            ["tenant_id"] = Q(Tenant), ["company_id"] = "NULL", ["amount"] = "-5", ["currency"] = "'sar'", ["debit_account"] = "''", ["credit_account"] = "''", ["description"] = "'legacy'",
        }));
    }

    private static Dictionary<string, string> CostCentre(Guid id, Guid? company, string code, Guid? tenant = null) => new()
    {
        ["id"] = Q(id), ["tenant_id"] = Q(tenant ?? Tenant), ["company_id"] = company is null ? "NULL" : Q(company.Value),
        ["code"] = $"'{code}'", ["name"] = $"'{code}'", ["is_active"] = "true", ["is_deleted"] = "false",
    };

    private static Dictionary<string, string> Dept(Guid id, string code, Guid? cc) => new()
    {
        ["id"] = Q(id), ["tenant_id"] = Q(Tenant), ["company_id"] = Q(CompanyA), ["code"] = $"'{code}'", ["name_en"] = $"'{code}'",
        ["cost_center_id"] = cc is null ? "NULL" : Q(cc.Value), ["is_deleted"] = "false",
    };

    private static int _employeeIds;

    private static Dictionary<string, string> Emp(string code, Guid? dept, Guid? cc) => new()
    {
        ["id"] = (++_employeeIds).ToString(), ["tenant_id"] = Q(Tenant), ["company_id"] = Q(CompanyA), ["employee_code"] = $"'{code}'", ["full_name"] = $"'{code}'",
        ["department_id"] = dept is null ? "NULL" : Q(dept.Value), ["cost_center_id"] = cc is null ? "NULL" : Q(cc.Value),
        ["public_id"] = Q(Guid.NewGuid()),
    };

    private static Dictionary<string, string> Mapping(string driver, string? @class)
    {
        var row = new Dictionary<string, string>
        {
            ["tenant_id"] = Q(Tenant), ["company_id"] = "NULL", ["driver_key"] = $"'{driver}'", ["is_active"] = "true",
            ["account_id"] = "(SELECT id FROM gl_accounts LIMIT 1)",
        };
        if (@class is not null) row["functional_class"] = $"'{@class}'";
        return row;
    }

    private static Dictionary<string, string> Entry(Guid? company, Guid? cc, string? ccCode, string? key = null, Guid? tenant = null,
        string amount = "100", string currency = "SAR", string debit = "5001", string credit = "2001") => new()
    {
        ["tenant_id"] = Q(tenant ?? Tenant), ["company_id"] = company is null ? "NULL" : Q(company.Value),
        ["amount"] = amount, ["currency"] = $"'{currency}'", ["debit_account"] = $"'{debit}'", ["credit_account"] = $"'{credit}'",
        ["cost_center_id"] = cc is null ? "NULL" : Q(cc.Value), ["cost_center_code"] = ccCode is null ? "NULL" : $"'{ccCode}'",
        ["posting_key"] = key is null ? "NULL" : $"'{key}'", ["description"] = "'row'",
    };

    private static string Q(Guid g) => $"'{g}'";

    private static async Task<Dictionary<string, bool>> Overrides(PostgreSqlContainer container)
    {
        var map = new Dictionary<string, bool>();
        foreach (var line in await Column(container, "SELECT employee_code || '=' || cost_center_is_override::text FROM employees ORDER BY employee_code"))
        { var p = line.Split('='); map[p[0]] = p[1] == "true"; }
        return map;
    }

    private static async Task<(IMigrator Migrator, string This, string Before)> Chain(PostgreSqlContainer container)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var all = db.Database.GetMigrations().ToList();
        var thisMigration = all.SingleOrDefault(m => m.EndsWith(Suffix, StringComparison.Ordinal));
        thisMigration.Should().NotBeNull($"a migration named *{Suffix} must exist (v1-spec 1.10, M1)");
        // The migration immediately before this one, whatever it is: a fixed name breaks as soon as another branch's lands.
        var before = all[all.IndexOf(thisMigration!) - 1];
        return (db.Database.GetInfrastructure().GetRequiredService<IMigrator>(), thisMigration!, before);
    }

    private static async Task<IReadOnlyList<string>> PendingMigrations(PostgreSqlContainer container)
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        return (await db.Database.GetPendingMigrationsAsync()).ToList();
    }

    private static IEnumerable<Exception> Causes(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException) yield return x;
    }

    private static async Task Expect(PostgreSqlContainer container, string sqlState, string sql, string because = "")
    {
        var act = async () => await Exec(container, sql);
        (await act.Should().ThrowAsync<PostgresException>(because)).Which.SqlState.Should().Be(sqlState, sql + " " + because);
    }

    private static Task<string> Insert(PostgreSqlContainer container, string table, Dictionary<string, string> overrides) => InsertRowSql(container, table, overrides);

    /// <summary>An INSERT filling every NOT NULL column without a default with a neutral value of its type, plus overrides.</summary>
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
                    "jsonb" or "json" => "'{}'",
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
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> Column(PostgreSqlContainer container, string sql)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(reader.GetString(0));
        return list;
    }

    private static async Task<string[]?> Row(PostgreSqlContainer container, string sql)
    {
        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetString(i)).ToArray();
    }
}
