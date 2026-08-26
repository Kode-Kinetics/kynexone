using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// POD-C3 — MID-MONTH PRORATION + RETRO/ARREARS. Strictly ADDITIVE.
    ///
    /// WHAT IT ADDS
    ///
    ///  1. payroll_arrears_lines — the retro/arrears sub-ledger. One row per (settling run, employee,
    ///     covered period, component), carrying the whole arithmetic that produced it: entitled, paid,
    ///     previously-settled, the amount, the assignment that caused it, whether it is GOSI-bearing, and
    ///     the earned-basis ceiling delta a compliance officer needs for an amended declaration. The
    ///     unique index is on (tenant, RUN, employee, covered period, component) — one line per run per
    ///     slot, which is the true invariant and the idempotency backstop for a re-Process. It is
    ///     deliberately NOT a global unique on (employee, period, component): two successive backdated
    ///     increments for the SAME covered period, settled in two different months, are legitimate and
    ///     must not be refused by an index.
    ///
    ///  2. payroll_employee_receivables — the PER-EMPLOYEE sub-ledger behind the aggregate DR 1420 that a
    ///     POD-B3 FundsDisbursed void posts. FinanceGlEntry has no employee dimension, which is exactly
    ///     why that receivable could never be netted into a replacement run; B3 assigned this to C3.
    ///
    ///  3. Proration witnesses on payroll_slips. Not decoration: under the KSA default
    ///     proration_gosi_base = 'FullMonth' the statutory base is the FULL monthly package while
    ///     basic_salary / housing_allowance carry the PRORATED wage, so the covered wage is NOT
    ///     reconstructible from the money columns alone. POD-A1's guarantee is "reconstruct expected from
    ///     the run's own persisted outputs" — these columns are what keeps that guarantee true.
    ///
    ///  4. settles_arrears / nets_prior_receivable on payroll_runs.
    ///
    ///  5. Idempotent data seeds: the DED:RECEIVABLE_RECOVERY GL driver (1420) and the five
    ///     payparameter.* client-rate ALLOW-LIST rows this pod's policy is written through.
    ///
    /// SAFETY FOR THE ~55 LIVE TENANTS
    ///
    ///  • Every column added is NULLABLE or DEFAULTED and every table is NEW: no backfill, no row
    ///    rewrite, no destructive statement anywhere in Up(). A pre-C3 payroll_slips row keeps
    ///    gosi_base_policy = NULL, which is precisely the flag that makes GosiReconciliationService
    ///    rebuild its statutory base from the money columns exactly as it did before this pod.
    ///
    ///  • settles_arrears backfills TRUE (matching the CLR model default) rather than EF's generated
    ///    false. A historical run is never re-processed so the value is inert for it, but a DRAFT run
    ///    created before this migration would otherwise silently never settle arrears.
    ///
    ///  • THE CLIENT-RATE SEEDS ARE LOAD-BEARING, NOT COSMETIC. RatesController's allow-list REFUSES any
    ///    key absent from client_rate_definitions, and it only falls back to the compiled defaults when a
    ///    tenant has ZERO rows. Every live tenant provisioned before this pod already HAS rows (the
    ///    original eight keys), and GlDriverSeeder runs only at tenant creation or on an explicit
    ///    seed-defaults call — so without these INSERTs the proration policy would be permanently
    ///    unwritable and every tenant would be stuck on the compiled default forever. NOT EXISTS-guarded,
    ///    so re-running (or overlapping with a seed-defaults call) is a no-op.
    ///
    ///  • The GL driver seed is likewise NOT EXISTS-guarded and correctness does not depend on it: the
    ///    compiled PayrollGlCatalog.Defaults fallback already resolves "1420 - Employee Overpayment
    ///    Receivable" for DED:RECEIVABLE_RECOVERY at posting time, and DeductionDriverKey resolves the
    ///    driver key without the store. The seed only makes the routing VISIBLE and REMAPPABLE per
    ///    company in the CoA UI, exactly as 20260804221216 did for 1420/1430 themselves.
    /// </summary>
    public partial class AddPayrollProrationAndArrears : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "arrears_amount",
                table: "payroll_slips",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "full_basic_salary",
                table: "payroll_slips",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "full_housing_allowance",
                table: "payroll_slips",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "full_transport_allowance",
                table: "payroll_slips",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gosi_base_policy",
                table: "payroll_slips",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_final_wage_month",
                table: "payroll_slips",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "paid_days",
                table: "payroll_slips",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "paid_from_date",
                table: "payroll_slips",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "paid_to_date",
                table: "payroll_slips",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "period_days",
                table: "payroll_slips",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "proration_basis",
                table: "payroll_slips",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "proration_denominator_days",
                table: "payroll_slips",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "proration_factor",
                table: "payroll_slips",
                type: "numeric(12,6)",
                precision: 12,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "nets_prior_receivable",
                table: "payroll_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfills TRUE to match PayrollRun.SettlesArrears's CLR default. See the class remarks.
            migrationBuilder.AddColumn<bool>(
                name: "settles_arrears",
                table: "payroll_runs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "payroll_arrears_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    covered_year = table.Column<int>(type: "integer", nullable: false),
                    covered_month = table.Column<int>(type: "integer", nullable: false),
                    component_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    entitled_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    paid_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    previously_settled_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    source_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_effective_date = table.Column<DateOnly>(type: "date", nullable: true),
                    is_gosi_bearing = table.Column<bool>(type: "boolean", nullable: false),
                    earned_basis_gosi_delta = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    basis = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    proration_factor = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_arrears_lines", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_employee_receivables",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    recovered_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    recovered_by_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_employee_receivables", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_arrears_lines_tenant_id_company_id",
                table: "payroll_arrears_lines",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_arrears_lines_tenant_id_employee_id_covered_year_co~",
                table: "payroll_arrears_lines",
                columns: new[] { "tenant_id", "employee_id", "covered_year", "covered_month" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_arrears_lines_tenant_id_payroll_run_id",
                table: "payroll_arrears_lines",
                columns: new[] { "tenant_id", "payroll_run_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_arrears_lines_tenant_id_payroll_run_id_employee_id_~",
                table: "payroll_arrears_lines",
                columns: new[] { "tenant_id", "payroll_run_id", "employee_id", "covered_year", "covered_month", "component_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_employee_receivables_tenant_id_company_id",
                table: "payroll_employee_receivables",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_employee_receivables_tenant_id_employee_id_status",
                table: "payroll_employee_receivables",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_employee_receivables_tenant_id_source_run_id",
                table: "payroll_employee_receivables",
                columns: new[] { "tenant_id", "source_run_id" });

            // ── Idempotent data seed 1: the DED:RECEIVABLE_RECOVERY posting driver ───────────────────
            // Category = 'Deduction' with an EXACT match on RECEIVABLE_RECOVERY so it is selected AHEAD
            // of the catch-all DED:OTHER, exactly the way DED:FIXED_DEDUCTION is. AccountType 'Asset'
            // because the credit RELIEVES the 1420 receivable the void recognised — routing a recovery to
            // 2199 would credit a liability nobody owes and leave 1420 ageing forever.
            migrationBuilder.Sql(@"
INSERT INTO gl_drivers (
    id, tenant_id, company_id, key, label, category, posting_side, account_type,
    default_code, default_name, match_source, match_mode, match_component_code,
    emits_employer_expense_pair, paired_expense_driver_key, is_system, is_active, sort_order, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, NULL, 'DED:RECEIVABLE_RECOVERY', 'Deduction — Overpayment Recovery',
    'Deduction', 'CR', 'Asset', '1420', 'Employee Overpayment Receivable',
    NULL, 'Exact', 'RECEIVABLE_RECOVERY', FALSE, NULL, TRUE, TRUE, 27, now()
FROM (SELECT DISTINCT tenant_id FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE) t
WHERE NOT EXISTS (
    SELECT 1 FROM gl_drivers d
    WHERE d.tenant_id = t.tenant_id AND d.company_id IS NULL AND d.key = 'DED:RECEIVABLE_RECOVERY'
);");

            // ── Idempotent data seed 2: the proration/arrears CLIENT-RATE ALLOW-LIST ─────────────────
            // Without these rows RatesController refuses every write of payparameter.proration_* for any
            // tenant that already has client_rate_definitions rows — i.e. every live tenant. See the
            // class remarks: this is the difference between a configurable policy and a compiled constant.
            migrationBuilder.Sql(@"
INSERT INTO client_rate_definitions (id, tenant_id, rate_key, rate_category, data_type, unit, min_value, max_value, description, is_active, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, v.rate_key, 'PayParameter', v.data_type, v.unit, v.min_value, v.max_value, v.description, TRUE, now()
FROM (SELECT DISTINCT tenant_id FROM client_rate_definitions) t
CROSS JOIN (VALUES
    ('payparameter.proration_basis',             'string',  'enum',   NULL::numeric, NULL::numeric,        'Mid-month proration basis: Calendar30 (default, day-rate = monthly/30) | CalendarActual | WorkingDays | None'),
    ('payparameter.proration_gosi_base',         'string',  'enum',   NULL::numeric, NULL::numeric,        'Social-insurance base in a joining/leaving month: FullMonth (KSA default) | Prorated'),
    ('payparameter.arrears_gosi_treatment',      'string',  'enum',   NULL::numeric, NULL::numeric,        'Statutory treatment of retro arrears: PeriodPaid (default) | None | PeriodEarned (refused — needs an amended-declaration workflow)'),
    ('payparameter.arrears_max_lookback_months', 'decimal', 'months', 0::numeric, 120::numeric,'How many past periods a retro/arrears settlement may reach back over (default 24)'),
    ('payparameter.prorated_components',         'string',  'list',   NULL::numeric, NULL::numeric,        'Comma-separated components proration applies to. Default BASIC,HOUSING,TRANSPORT,OTHER_ALLOWANCES,FIXED_DEDUCTION')
) AS v(rate_key, data_type, unit, min_value, max_value, description)
WHERE NOT EXISTS (
    SELECT 1 FROM client_rate_definitions c
    WHERE c.tenant_id = t.tenant_id AND c.rate_key = v.rate_key
);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove ONLY the rows this migration seeds (same discipline as 20260804221216).
            migrationBuilder.Sql("DELETE FROM gl_drivers WHERE company_id IS NULL AND is_system = TRUE AND key = 'DED:RECEIVABLE_RECOVERY';");
            migrationBuilder.Sql(@"DELETE FROM client_rate_definitions WHERE rate_key IN (
    'payparameter.proration_basis', 'payparameter.proration_gosi_base', 'payparameter.arrears_gosi_treatment',
    'payparameter.arrears_max_lookback_months', 'payparameter.prorated_components');");

            migrationBuilder.DropTable(
                name: "payroll_arrears_lines");

            migrationBuilder.DropTable(
                name: "payroll_employee_receivables");

            migrationBuilder.DropColumn(
                name: "arrears_amount",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "full_basic_salary",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "full_housing_allowance",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "full_transport_allowance",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "gosi_base_policy",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "is_final_wage_month",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "paid_days",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "paid_from_date",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "paid_to_date",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "period_days",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "proration_basis",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "proration_denominator_days",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "proration_factor",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "nets_prior_receivable",
                table: "payroll_runs");

            migrationBuilder.DropColumn(
                name: "settles_arrears",
                table: "payroll_runs");
        }
    }
}
