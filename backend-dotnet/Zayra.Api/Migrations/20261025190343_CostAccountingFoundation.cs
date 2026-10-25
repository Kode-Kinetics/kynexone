using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class CostAccountingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand-only (v1-spec 1.10, M1): defaulted/nullable columns, CHECKs, partial indexes, raw-SQL keys. The SQL
            // below is a frozen copy of CostAccountingModelConfiguration: never edit it here, write a new migration.
            // ADD COLUMN / ADD CONSTRAINT take locks that would queue every reader of these tables behind them: fail fast
            // instead (55P03) and re-run when quiet. Transaction-scoped (EF runs a migration in one transaction).
            migrationBuilder.Sql(LockTimeoutSql);

            migrationBuilder.AddColumn<Guid>(
                name: "cost_center_id",
                table: "payroll_slips",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "cost_accounting_applied",
                table: "payroll_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "cost_center_resolution_hash",
                table: "payroll_runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cost_center_id",
                table: "payroll_earnings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_center_source",
                table: "payroll_earnings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cost_center_id",
                table: "payroll_deductions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_center_source",
                table: "payroll_deductions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "amount_scale",
                table: "gl_journal_exports",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_center_code",
                table: "gl_journal_export_lines",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_center_gl_segment",
                table: "gl_journal_export_lines",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_center_name",
                table: "gl_journal_export_lines",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_center_rule",
                table: "gl_accounts",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "Optional");

            migrationBuilder.AddColumn<string>(
                name: "functional_class",
                table: "gl_account_mappings",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_center_code",
                table: "finance_gl_entries",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_center_gl_segment",
                table: "finance_gl_entries",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cost_center_id",
                table: "finance_gl_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_center_name",
                table: "finance_gl_entries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "posting_key",
                table: "finance_gl_entries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cost_center_id",
                table: "eosb_provision_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "cost_center_is_override",
                table: "employees",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "functional_class",
                table: "cost_centers",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gl_segment",
                table: "cost_centers",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_id",
                table: "cost_centers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "cost_accounting_enabled",
                table: "companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<short>(
                name: "cost_centre_small_cell_min",
                table: "companies",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<Guid>(
                name: "default_cost_center_id",
                table: "companies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_payroll_earnings__cost_center_source",
                table: "payroll_earnings",
                sql: "cost_center_source IS NULL OR cost_center_source IN ('Employee', 'Position', 'Department', 'ParentDepartment', 'CompanyDefault', 'MappingFixed', 'RolledUp')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payroll_deductions__cost_center_source",
                table: "payroll_deductions",
                sql: "cost_center_source IS NULL OR cost_center_source IN ('Employee', 'Position', 'Department', 'ParentDepartment', 'CompanyDefault', 'MappingFixed', 'RolledUp')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_gl_accounts__cost_center_rule",
                table: "gl_accounts",
                sql: "cost_center_rule IN ('Required', 'Optional', 'NotAllowed')");

            migrationBuilder.CreateIndex(
                name: "ux_gl_account_mappings_scope_driver_class",
                table: "gl_account_mappings",
                columns: new[] { "tenant_id", "company_id", "driver_key", "functional_class" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_gl_account_mappings__functional_class",
                table: "gl_account_mappings",
                sql: "functional_class IS NULL OR functional_class IN ('COGS', 'SELLING', 'G_AND_A', 'R_AND_D', 'OPERATIONS', 'CAPITALISABLE')");

            migrationBuilder.CreateIndex(
                name: "ix_finance_gl_entries_cc_period",
                table: "finance_gl_entries",
                columns: new[] { "tenant_id", "cost_center_id", "period" },
                filter: "cost_center_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_finance_gl_entries_posting_key",
                table: "finance_gl_entries",
                columns: new[] { "tenant_id", "posting_key" },
                unique: true,
                filter: "posting_key IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_finance_gl_entries__cost_center_needs_company",
                table: "finance_gl_entries",
                sql: "cost_center_id IS NULL OR company_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_finance_gl_entries__cost_center_pair",
                table: "finance_gl_entries",
                sql: "(cost_center_id IS NULL) = (cost_center_code IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ux_cost_centers_scope_gl_segment",
                table: "cost_centers",
                columns: new[] { "tenant_id", "company_id", "gl_segment" },
                unique: true,
                filter: "gl_segment IS NOT NULL AND NOT is_deleted")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_cost_centers__functional_class",
                table: "cost_centers",
                sql: "functional_class IS NULL OR functional_class IN ('COGS', 'SELLING', 'G_AND_A', 'R_AND_D', 'OPERATIONS', 'CAPITALISABLE')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cost_centers__gl_segment_format",
                table: "cost_centers",
                sql: "gl_segment IS NULL OR gl_segment ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,29}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cost_centers__parent_not_self",
                table: "cost_centers",
                sql: "parent_id IS NULL OR parent_id <> id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_companies__cost_centre_small_cell_min",
                table: "companies",
                sql: "cost_centre_small_cell_min >= 0");

            // Existing rows keep today's meaning: a cost centre on an employee is an OVERRIDE only where it differs from the
            // department's. Equal to the department's = a copy made when the department changed (EmployeeOrgFieldResolver).
            migrationBuilder.Sql(
                "UPDATE employees e SET cost_center_is_override = true " +
                "WHERE e.cost_center_id IS NOT NULL " +
                "AND e.cost_center_id IS DISTINCT FROM " +
                "(SELECT d.cost_center_id FROM departments d WHERE d.tenant_id = e.tenant_id AND d.id = e.department_id);");

            // The keys every cost-centre foreign key points at. EF cannot model (tenant_id, company_id, id): company_id is nullable.
            migrationBuilder.Sql(AddUniqueSql("uq_cost_centers__tenant_id", "(tenant_id, id)"));
            migrationBuilder.Sql(AddUniqueSql("uq_cost_centers__tenant_company_id", "(tenant_id, company_id, id)"));

            // NOT VALID CHECKs: bind every new or updated row, never the rows that exist at upgrade time. Legacy offenders
            // are listed by the pre-migration audit query and repaired before M3 validates them.
            migrationBuilder.Sql(AddCheckNotValidSql("cost_centers", "ck_cost_centers__code_format", "code ~ " + CodePattern));
            migrationBuilder.Sql(AddCheckNotValidSql("finance_gl_entries", "ck_finance_gl_entries__amount_nonnegative", "amount >= 0"));
            migrationBuilder.Sql(AddCheckNotValidSql("finance_gl_entries", "ck_finance_gl_entries__has_account", "(debit_account <> '' OR credit_account <> '')"));
            migrationBuilder.Sql(AddCheckNotValidSql("finance_gl_entries", "ck_finance_gl_entries__currency_format", "currency ~ '^[A-Z]{3}$'"));

            // Foreign keys: composite and tenant-first, ON DELETE RESTRICT, MATCH SIMPLE (a NULL cost centre is skipped).
            // New columns / self reference / company-scoped keys are valid on creation; keys over columns that may already hold
            // dangling ids (departments, positions, employees, timesheets, mapping segment) are NOT VALID until M3.
            foreach (var (table, name, column) in NotValidCostCentreForeignKeys)
                migrationBuilder.Sql(AddForeignKeySql(table, name, $"(tenant_id, {column})", "cost_centers (tenant_id, id)", notValid: true));
            foreach (var (table, name, column) in NewColumnCostCentreForeignKeys)
                migrationBuilder.Sql(AddForeignKeySql(table, name, $"(tenant_id, {column})", "cost_centers (tenant_id, id)", notValid: true));
            migrationBuilder.Sql(AddForeignKeySql("cost_centers", "fk_cost_centers__parent", "(tenant_id, parent_id)", "cost_centers (tenant_id, id)", notValid: false));
            migrationBuilder.Sql(AddForeignKeySql("finance_gl_entries", "fk_finance_gl_entries__cost_center",
                "(tenant_id, company_id, cost_center_id)", "cost_centers (tenant_id, company_id, id)", notValid: false));
            migrationBuilder.Sql(AddForeignKeySql("companies", "fk_companies__default_cost_center",
                "(tenant_id, id, default_cost_center_id)", "cost_centers (tenant_id, company_id, id)", notValid: false));

            // The mapping unique is widened by the cost-centre class. The new unique index (created above) is identical to the old
            // one while every class is NULL, so uniqueness is never absent; the old one goes in the same transaction.
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_gl_account_mappings_scope_driver;");
        }

/// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Foreign keys first (they depend on the unique keys), then the old mapping unique back before the new one goes.
            foreach (var (table, name, _) in NotValidCostCentreForeignKeys.Concat(NewColumnCostCentreForeignKeys))
                migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS {name};");
            migrationBuilder.Sql("ALTER TABLE cost_centers DROP CONSTRAINT IF EXISTS fk_cost_centers__parent;");
            migrationBuilder.Sql("ALTER TABLE finance_gl_entries DROP CONSTRAINT IF EXISTS fk_finance_gl_entries__cost_center;");
            migrationBuilder.Sql("ALTER TABLE companies DROP CONSTRAINT IF EXISTS fk_companies__default_cost_center;");
            migrationBuilder.Sql("ALTER TABLE cost_centers DROP CONSTRAINT IF EXISTS uq_cost_centers__tenant_company_id;");
            migrationBuilder.Sql("ALTER TABLE cost_centers DROP CONSTRAINT IF EXISTS uq_cost_centers__tenant_id;");
            // Restored with the definition 20260729034732_RekeyGlUniquesForCompanyScope gave it. Fails if rows that differ only
            // by functional_class were written meanwhile: roll such data back first.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_gl_account_mappings_scope_driver " +
                "ON gl_account_mappings (tenant_id, company_id, driver_key) NULLS NOT DISTINCT;");
            foreach (var (table, name) in NotValidChecks)
                migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS {name};");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payroll_earnings__cost_center_source",
                table: "payroll_earnings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payroll_deductions__cost_center_source",
                table: "payroll_deductions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_gl_accounts__cost_center_rule",
                table: "gl_accounts");

            migrationBuilder.DropIndex(
                name: "ux_gl_account_mappings_scope_driver_class",
                table: "gl_account_mappings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_gl_account_mappings__functional_class",
                table: "gl_account_mappings");

            migrationBuilder.DropIndex(
                name: "ix_finance_gl_entries_cc_period",
                table: "finance_gl_entries");

            migrationBuilder.DropIndex(
                name: "ux_finance_gl_entries_posting_key",
                table: "finance_gl_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_finance_gl_entries__cost_center_needs_company",
                table: "finance_gl_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_finance_gl_entries__cost_center_pair",
                table: "finance_gl_entries");

            migrationBuilder.DropIndex(
                name: "ux_cost_centers_scope_gl_segment",
                table: "cost_centers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cost_centers__functional_class",
                table: "cost_centers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cost_centers__gl_segment_format",
                table: "cost_centers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cost_centers__parent_not_self",
                table: "cost_centers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_companies__cost_centre_small_cell_min",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "cost_center_id",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "cost_accounting_applied",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "cost_center_resolution_hash",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "cost_center_id",
                table: "payroll_earnings");

            migrationBuilder.DropColumn(
                name: "cost_center_source",
                table: "payroll_earnings");

            migrationBuilder.DropColumn(
                name: "cost_center_id",
                table: "payroll_deductions");

            migrationBuilder.DropColumn(
                name: "cost_center_source",
                table: "payroll_deductions");

            migrationBuilder.DropColumn(
                name: "amount_scale",
                table: "gl_journal_exports");

            migrationBuilder.DropColumn(
                name: "cost_center_code",
                table: "gl_journal_export_lines");

            migrationBuilder.DropColumn(
                name: "cost_center_gl_segment",
                table: "gl_journal_export_lines");

            migrationBuilder.DropColumn(
                name: "cost_center_name",
                table: "gl_journal_export_lines");

            migrationBuilder.DropColumn(
                name: "cost_center_rule",
                table: "gl_accounts");

            migrationBuilder.DropColumn(
                name: "functional_class",
                table: "gl_account_mappings");

            migrationBuilder.DropColumn(
                name: "cost_center_code",
                table: "finance_gl_entries");

            migrationBuilder.DropColumn(
                name: "cost_center_gl_segment",
                table: "finance_gl_entries");

            migrationBuilder.DropColumn(
                name: "cost_center_id",
                table: "finance_gl_entries");

            migrationBuilder.DropColumn(
                name: "cost_center_name",
                table: "finance_gl_entries");

            migrationBuilder.DropColumn(
                name: "posting_key",
                table: "finance_gl_entries");

            migrationBuilder.DropColumn(
                name: "cost_center_id",
                table: "eosb_provision_snapshots");

            migrationBuilder.DropColumn(
                name: "cost_center_is_override",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "functional_class",
                table: "cost_centers");

            migrationBuilder.DropColumn(
                name: "gl_segment",
                table: "cost_centers");

            migrationBuilder.DropColumn(
                name: "parent_id",
                table: "cost_centers");

            migrationBuilder.DropColumn(
                name: "cost_accounting_enabled",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "cost_centre_small_cell_min",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "default_cost_center_id",
                table: "companies");
        }
    

        // ── frozen SQL helpers ────────────────────────────────────────────────────────────────────────

        /// <summary>Transaction-scoped, as in AddMfaTrustedDevices.</summary>
        public const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s';";

        private const string CodePattern = "'^[A-Za-z0-9][A-Za-z0-9._-]{0,29}$'";

        /// <summary>Referencing columns that may already hold ids with no cost centre behind them (NOT VALID until M3).</summary>
        private static readonly (string Table, string Name, string Column)[] NotValidCostCentreForeignKeys =
        {
            ("departments", "fk_departments__cost_center", "cost_center_id"),
            ("positions", "fk_positions__cost_center", "cost_center_id"),
            ("employees", "fk_employees__cost_center", "cost_center_id"),
            ("timesheet_entries", "fk_timesheet_entries__cost_center", "cost_center_id"),
            ("gl_account_mappings", "fk_gl_account_mappings__segment_cost_center", "segment_cost_center_id"),
        };

        /// <summary>Columns this migration creates (all NULL): the contract still states NOT VALID, validated with the rest in M3.</summary>
        private static readonly (string Table, string Name, string Column)[] NewColumnCostCentreForeignKeys =
        {
            ("payroll_earnings", "fk_payroll_earnings__cost_center", "cost_center_id"),
            ("payroll_deductions", "fk_payroll_deductions__cost_center", "cost_center_id"),
            ("payroll_slips", "fk_payroll_slips__cost_center", "cost_center_id"),
            ("eosb_provision_snapshots", "fk_eosb_provision_snapshots__cost_center", "cost_center_id"),
        };

        private static readonly (string Table, string Name)[] NotValidChecks =
        {
            ("cost_centers", "ck_cost_centers__code_format"),
            ("finance_gl_entries", "ck_finance_gl_entries__amount_nonnegative"),
            ("finance_gl_entries", "ck_finance_gl_entries__has_account"),
            ("finance_gl_entries", "ck_finance_gl_entries__currency_format"),
        };

        // Each statement is guarded so the migration can be re-run against a partly applied database.
        private static string AddUniqueSql(string name, string columns) =>
            "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = '" + name + "' AND conrelid = 'cost_centers'::regclass) THEN " +
            "ALTER TABLE cost_centers ADD CONSTRAINT " + name + " UNIQUE " + columns + "; END IF; END $$;";

        private static string AddCheckNotValidSql(string table, string name, string expression) =>
            "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = '" + name + "' AND conrelid = '" + table + "'::regclass) THEN " +
            "ALTER TABLE " + table + " ADD CONSTRAINT " + name + " CHECK (" + expression + ") NOT VALID; END IF; END $$;";

        private static string AddForeignKeySql(string table, string name, string columns, string target, bool notValid) =>
            "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = '" + name + "' AND conrelid = '" + table + "'::regclass) THEN " +
            "ALTER TABLE " + table + " ADD CONSTRAINT " + name + " FOREIGN KEY " + columns + " REFERENCES " + target +
            " ON DELETE RESTRICT" + (notValid ? " NOT VALID" : "") + "; END IF; END $$;";
    }
}
