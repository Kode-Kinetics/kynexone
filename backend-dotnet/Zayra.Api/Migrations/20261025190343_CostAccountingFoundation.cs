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
            migrationBuilder.Sql(UniqueKeysSql);

            // The four CHECKs that read existing columns (cost_centers.code format; finance_gl_entries amount, accounts, currency)
            // are deliberately NOT here: even NOT VALID, PostgreSQL re-checks a row on every UPDATE, so a legacy offender would
            // fail unrelated edits. They ship in M3 (docs/schema/M3_COST_ACCOUNTING_VALIDATION.md) after the audit returns zero.

            // Foreign keys: composite and tenant-first, ON DELETE RESTRICT, MATCH SIMPLE (a NULL cost centre is skipped).
            // Keys over columns that may already hold dangling ids, and over the new payroll columns, are NOT VALID until M3;
            // the self reference and the two company-scoped keys are valid on creation (their columns are new, all NULL).
            migrationBuilder.Sql(ForeignKeysSql);

            // The mapping unique is widened by the cost-centre class. The new unique index (created above) is identical to the old
            // one while every class is NULL, so uniqueness is never absent; the old one goes in the same transaction.
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_gl_account_mappings_scope_driver;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Foreign keys first (they depend on the unique keys), then the old mapping unique back before the new one goes.
            migrationBuilder.Sql(DropKeysSql);
            // Restored with the definition 20260729034732_RekeyGlUniquesForCompanyScope gave it. Fails if rows that differ only
            // by functional_class were written meanwhile: roll such data back first.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_gl_account_mappings_scope_driver " +
                "ON gl_account_mappings (tenant_id, company_id, driver_key) NULLS NOT DISTINCT;");

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
    

        // ── frozen SQL ────────────────────────────────────────────────────────────────────────────────
        // Whole statements as constants (read by DestructiveMigrationGuardTests). Each is guarded, so the migration can be
        // re-run against a partly applied database.

        /// <summary>Transaction-scoped, as in AddMfaTrustedDevices.</summary>
        public const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s';";

        private const string UniqueKeysSql = @"
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_cost_centers__tenant_id' AND conrelid = 'cost_centers'::regclass) THEN
    ALTER TABLE cost_centers ADD CONSTRAINT uq_cost_centers__tenant_id UNIQUE (tenant_id, id);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_cost_centers__tenant_company_id' AND conrelid = 'cost_centers'::regclass) THEN
    ALTER TABLE cost_centers ADD CONSTRAINT uq_cost_centers__tenant_company_id UNIQUE (tenant_id, company_id, id);
  END IF;
END $$;";

        private const string ForeignKeysSql = @"
DO $$ DECLARE r record; BEGIN
  FOR r IN SELECT * FROM (VALUES
    ('departments',              'fk_departments__cost_center',                '(tenant_id, cost_center_id)',                 'cost_centers (tenant_id, id)',             true),
    ('positions',                'fk_positions__cost_center',                  '(tenant_id, cost_center_id)',                 'cost_centers (tenant_id, id)',             true),
    ('employees',                'fk_employees__cost_center',                  '(tenant_id, cost_center_id)',                 'cost_centers (tenant_id, id)',             true),
    ('timesheet_entries',        'fk_timesheet_entries__cost_center',          '(tenant_id, cost_center_id)',                 'cost_centers (tenant_id, id)',             true),
    ('gl_account_mappings',      'fk_gl_account_mappings__segment_cost_center','(tenant_id, segment_cost_center_id)',         'cost_centers (tenant_id, id)',             true),
    ('payroll_earnings',         'fk_payroll_earnings__cost_center',           '(tenant_id, cost_center_id)',                 'cost_centers (tenant_id, id)',             true),
    ('payroll_deductions',       'fk_payroll_deductions__cost_center',         '(tenant_id, cost_center_id)',                 'cost_centers (tenant_id, id)',             true),
    ('payroll_slips',            'fk_payroll_slips__cost_center',              '(tenant_id, cost_center_id)',                 'cost_centers (tenant_id, id)',             true),
    ('eosb_provision_snapshots', 'fk_eosb_provision_snapshots__cost_center',   '(tenant_id, cost_center_id)',                 'cost_centers (tenant_id, id)',             true),
    ('cost_centers',             'fk_cost_centers__parent',                    '(tenant_id, parent_id)',                      'cost_centers (tenant_id, id)',             false),
    ('finance_gl_entries',       'fk_finance_gl_entries__cost_center',         '(tenant_id, company_id, cost_center_id)',     'cost_centers (tenant_id, company_id, id)', false),
    ('companies',                'fk_companies__default_cost_center',          '(tenant_id, id, default_cost_center_id)',     'cost_centers (tenant_id, company_id, id)', false)
  ) AS v(tbl, name, cols, target, not_valid) LOOP
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = r.name AND conrelid = r.tbl::regclass) THEN
      EXECUTE format('ALTER TABLE %I ADD CONSTRAINT %I FOREIGN KEY %s REFERENCES %s ON DELETE RESTRICT%s',
                     r.tbl, r.name, r.cols, r.target, CASE WHEN r.not_valid THEN ' NOT VALID' ELSE '' END);
    END IF;
  END LOOP;
END $$;";

        private const string DropKeysSql = @"
DO $$ DECLARE r record; BEGIN
  FOR r IN SELECT * FROM (VALUES
    ('departments',              'fk_departments__cost_center'),
    ('positions',                'fk_positions__cost_center'),
    ('employees',                'fk_employees__cost_center'),
    ('timesheet_entries',        'fk_timesheet_entries__cost_center'),
    ('gl_account_mappings',      'fk_gl_account_mappings__segment_cost_center'),
    ('payroll_earnings',         'fk_payroll_earnings__cost_center'),
    ('payroll_deductions',       'fk_payroll_deductions__cost_center'),
    ('payroll_slips',            'fk_payroll_slips__cost_center'),
    ('eosb_provision_snapshots', 'fk_eosb_provision_snapshots__cost_center'),
    ('cost_centers',             'fk_cost_centers__parent'),
    ('finance_gl_entries',       'fk_finance_gl_entries__cost_center'),
    ('companies',                'fk_companies__default_cost_center'),
    ('cost_centers',             'uq_cost_centers__tenant_company_id'),
    ('cost_centers',             'uq_cost_centers__tenant_id')
  ) AS v(tbl, name) LOOP
    EXECUTE format('ALTER TABLE %I DROP CONSTRAINT IF EXISTS %I', r.tbl, r.name);
  END LOOP;
END $$;";
    }
}
