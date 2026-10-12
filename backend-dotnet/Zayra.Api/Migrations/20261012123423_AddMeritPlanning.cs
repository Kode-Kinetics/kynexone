using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMeritPlanning : Migration
    {
        // ── CMP raw SQL, declared here so DestructiveMigrationGuardTests can read it. PostgresFixture runs these. ──

        /// <summary>Fails the migration (and the deploy) with a clear message when duplicate active salary rows exist.
        /// Deletes nothing. The read-only listing query is Models.MeritPlanningSql.PreDeployDuplicateQuery.</summary>
        public const string PreflightDuplicateSalaryRows =
            "DO $$ DECLARE dup integer; BEGIN " +
            "SELECT count(*) INTO dup FROM (SELECT 1 FROM employee_salary_structures WHERE is_active " +
            "GROUP BY tenant_id, employee_id, effective_date HAVING count(*) > 1) d; " +
            "IF dup > 0 THEN RAISE EXCEPTION 'CMP pre-flight: % (tenant, employee, effective_date) group(s) have more than one ACTIVE " +
            "employee_salary_structures row. Nothing was changed. Run the read-only query in docs/acceptance/capability-closure/CMP_DESIGN.md " +
            "(Operator runbook, step 1), have HR decide which row is pay of record, deactivate the others through a reviewed change, then deploy again.', dup; " +
            "END IF; END $$;";

        /// <summary>One ACTIVE salary row per (tenant, employee, effective date), checked at COMMIT (see MeritPlanningSql).</summary>
        public const string AddSalaryRowExclusion =
            "ALTER TABLE employee_salary_structures ADD CONSTRAINT " + Zayra.Api.Models.MeritPlanningSql.SalaryRowExclusionName +
            " EXCLUDE USING btree (tenant_id WITH =, employee_id WITH =, effective_date WITH =) WHERE (is_active) DEFERRABLE INITIALLY DEFERRED;";

        /// <summary>Award effective dates are the 1st of a month. PostgreSQL-only (EXTRACT), so raw SQL, not a model CHECK.</summary>
        public const string AddEffectiveFirstOfMonthCheck =
            "ALTER TABLE comp_cycles ADD CONSTRAINT ck_comp_cycles__effective_first_of_month CHECK (EXTRACT(DAY FROM effective_date) = 1);";

        public const string DropSalaryRowExclusion =
            "ALTER TABLE employee_salary_structures DROP CONSTRAINT IF EXISTS " + Zayra.Api.Models.MeritPlanningSql.SalaryRowExclusionName + ";";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CMP pre-flight, FIRST: if production already holds two ACTIVE salary rows for one employee on one date,
            // fail the whole migration with a message (nothing created, nothing deleted). See CMP_DESIGN.md "Operator runbook".
            migrationBuilder.Sql(PreflightDuplicateSalaryRows);
            migrationBuilder.Sql(AddSalaryRowExclusion);

            migrationBuilder.CreateTable(
                name: "comp_cycles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    performance_period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    performance_period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    pay_component = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    hire_date_cutoff = table.Column<DateOnly>(type: "date", nullable: true),
                    eligible_statuses = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    eligibility_filters_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    minimum_eligible_days = table.Column<int>(type: "integer", nullable: false),
                    proration_rule = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    equity_includes_nationality = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    planning_opened_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    planning_opened_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    review_started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    review_started_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    applied_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    applied_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    retro_acknowledged_periods = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comp_cycles", x => x.id);
                    table.CheckConstraint("ck_comp_cycles__company_required", "company_id IS NOT NULL");
                    table.CheckConstraint("ck_comp_cycles__currency", "length(currency) = 3 AND upper(currency) = currency");
                    table.CheckConstraint("ck_comp_cycles__min_days", "minimum_eligible_days >= 0");
                    table.CheckConstraint("ck_comp_cycles__pay_component_basic", "pay_component = 'Basic'");
                    table.CheckConstraint("ck_comp_cycles__period_order", "performance_period_end >= performance_period_start");
                    table.CheckConstraint("ck_comp_cycles__proration_rule", "proration_rule IN ('None','EligibleDays')");
                    table.CheckConstraint("ck_comp_cycles__status", "status IN ('Draft','Planning','Review','Approved','Applied','Cancelled')");
                    table.ForeignKey(
                        name: "FK_comp_cycles_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pay_benchmark_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    licence_statement = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    survey_date = table.Column<DateOnly>(type: "date", nullable: false),
                    geography = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    pay_basis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_synthetic = table.Column<bool>(type: "boolean", nullable: false),
                    source_file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    source_file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    imported_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    imported_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    imported_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    retired_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pay_benchmark_sets", x => x.id);
                    table.CheckConstraint("ck_pay_benchmark_sets__file_hash", "length(source_file_sha256) = 64 AND lower(source_file_sha256) = source_file_sha256");
                    table.CheckConstraint("ck_pay_benchmark_sets__pay_basis", "pay_basis IN ('MonthlyBasic','MonthlyGross')");
                    table.CheckConstraint("ck_pay_benchmark_sets__provenance", "provider <> '' AND licence_statement <> '' AND length(geography) = 2 AND upper(geography) = geography AND length(currency) = 3 AND upper(currency) = currency");
                    table.CheckConstraint("ck_pay_benchmark_sets__row_count", "row_count > 0");
                    table.ForeignKey(
                        name: "FK_pay_benchmark_sets_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "comp_cycle_budgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pool_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    pool_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    fixed_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comp_cycle_budgets", x => x.id);
                    table.CheckConstraint("ck_comp_cycle_budgets__pool_type", "pool_type IN ('PercentOfEligibleBasic','FixedAmount')");
                    table.CheckConstraint("ck_comp_cycle_budgets__pool_value", "(pool_type = 'PercentOfEligibleBasic' AND pool_percent IS NOT NULL AND pool_percent >= 0 AND pool_percent <= 100 AND fixed_amount IS NULL) OR (pool_type = 'FixedAmount' AND fixed_amount IS NOT NULL AND fixed_amount >= 0 AND pool_percent IS NULL)");
                    table.ForeignKey(
                        name: "FK_comp_cycle_budgets_comp_cycles_cycle_id",
                        column: x => x.cycle_id,
                        principalTable: "comp_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_comp_cycle_budgets_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "comp_fx_rates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    rate_date = table.Column<DateOnly>(type: "date", nullable: false),
                    entered_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entered_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    entered_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comp_fx_rates", x => x.id);
                    table.CheckConstraint("ck_comp_fx_rates__currency", "length(from_currency) = 3 AND upper(from_currency) = from_currency");
                    table.CheckConstraint("ck_comp_fx_rates__rate_positive", "rate > 0");
                    table.CheckConstraint("ck_comp_fx_rates__source_present", "source <> ''");
                    table.ForeignKey(
                        name: "FK_comp_fx_rates_comp_cycles_cycle_id",
                        column: x => x.cycle_id,
                        principalTable: "comp_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comp_guidelines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rating = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    compa_ratio_min = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    compa_ratio_max = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    min_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    target_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    max_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comp_guidelines", x => x.id);
                    table.CheckConstraint("ck_comp_guidelines__band", "compa_ratio_min >= 0 AND (compa_ratio_max IS NULL OR compa_ratio_max > compa_ratio_min)");
                    table.CheckConstraint("ck_comp_guidelines__percent_order", "min_percent <= target_percent AND target_percent <= max_percent");
                    table.CheckConstraint("ck_comp_guidelines__percent_range", "min_percent >= 0 AND max_percent <= 100");
                    table.CheckConstraint("ck_comp_guidelines__rating_present", "rating <> ''");
                    table.ForeignKey(
                        name: "FK_comp_guidelines_comp_cycles_cycle_id",
                        column: x => x.cycle_id,
                        principalTable: "comp_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comp_worksheets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manager_employee_id = table.Column<int>(type: "integer", nullable: false),
                    scenario_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    revision_editor_ids = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, defaultValue: ""),
                    overspend_justification = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_comments = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comp_worksheets", x => x.id);
                    table.CheckConstraint("ck_comp_worksheets__revision", "revision >= 1");
                    table.CheckConstraint("ck_comp_worksheets__status", "status IN ('Draft','Submitted','Returned','Approved','Cancelled')");
                    table.CheckConstraint("ck_comp_worksheets__submitted_has_approval", "status NOT IN ('Submitted','Approved') OR approval_request_id IS NOT NULL");
                    table.CheckConstraint("ck_comp_worksheets__submitted_is_primary", "status IN ('Draft','Cancelled') OR is_primary");
                    table.ForeignKey(
                        name: "FK_comp_worksheets_comp_cycles_cycle_id",
                        column: x => x.cycle_id,
                        principalTable: "comp_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_comp_worksheets_employees_manager_employee_id",
                        column: x => x.manager_employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pay_benchmark_rows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_family = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    level = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    sample_size = table.Column<int>(type: "integer", nullable: false),
                    p25 = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    p50 = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    p75 = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    p90 = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grade_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pay_benchmark_rows", x => x.id);
                    table.CheckConstraint("ck_pay_benchmark_rows__keys_present", "job_family <> '' AND level <> ''");
                    table.CheckConstraint("ck_pay_benchmark_rows__percentiles_monotonic", "p25 > 0 AND p25 <= p50 AND p50 <= p75 AND p75 <= p90");
                    table.CheckConstraint("ck_pay_benchmark_rows__sample_size", "sample_size > 0");
                    table.ForeignKey(
                        name: "FK_pay_benchmark_rows_designations_designation_id",
                        column: x => x.designation_id,
                        principalTable: "designations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_pay_benchmark_rows_grades_grade_id",
                        column: x => x.grade_id,
                        principalTable: "grades",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_pay_benchmark_rows_pay_benchmark_sets_set_id",
                        column: x => x.set_id,
                        principalTable: "pay_benchmark_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comp_worksheet_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    worksheet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grade_id = table.Column<Guid>(type: "uuid", nullable: true),
                    designation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pay_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    fx_rate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    salary_row_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_basic = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    current_gross = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    current_basic_cycle = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    current_gross_cycle = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    compa_ratio = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    range_position = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    rating = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    eligible_days = table.Column<int>(type: "integer", nullable: false),
                    proration_factor = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    guideline_min_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    guideline_target_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    guideline_max_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    proposed_increase = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    proposed_percent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    proposed_as_percent = table.Column<bool>(type: "boolean", nullable: false),
                    prorated_increase = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    prorated_increase_pay = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    new_basic = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    new_gross = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    new_compa_ratio = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    out_of_guideline = table.Column<bool>(type: "boolean", nullable: false),
                    justification = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    flags = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    change_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    change_requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    apply_blocked_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reversal_change_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comp_worksheet_lines", x => x.id);
                    table.CheckConstraint("ck_comp_worksheet_lines__fx_rate_positive", "fx_rate IS NULL OR fx_rate > 0");
                    table.CheckConstraint("ck_comp_worksheet_lines__money_non_negative", "current_basic >= 0 AND current_gross >= 0 AND proposed_increase >= 0 AND prorated_increase >= 0 AND prorated_increase_pay >= 0 AND new_basic >= 0 AND new_gross >= 0");
                    table.CheckConstraint("ck_comp_worksheet_lines__out_of_guideline_justified", "NOT out_of_guideline OR change_request_id IS NULL OR (justification IS NOT NULL AND justification <> '')");
                    table.CheckConstraint("ck_comp_worksheet_lines__pay_currency", "length(pay_currency) = 3 AND upper(pay_currency) = pay_currency");
                    table.CheckConstraint("ck_comp_worksheet_lines__proration_factor", "proration_factor >= 0 AND proration_factor <= 1");
                    table.ForeignKey(
                        name: "FK_comp_worksheet_lines_comp_cycles_cycle_id",
                        column: x => x.cycle_id,
                        principalTable: "comp_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_comp_worksheet_lines_comp_worksheets_worksheet_id",
                        column: x => x.worksheet_id,
                        principalTable: "comp_worksheets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_comp_worksheet_lines_employee_change_requests_change_reques~",
                        column: x => x.change_request_id,
                        principalTable: "employee_change_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_comp_worksheet_lines_employee_change_requests_reversal_chan~",
                        column: x => x.reversal_change_request_id,
                        principalTable: "employee_change_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_comp_worksheet_lines_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_comp_cycle_budgets_cycle_id_department_id",
                table: "comp_cycle_budgets",
                columns: new[] { "cycle_id", "department_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_comp_cycle_budgets_department_id",
                table: "comp_cycle_budgets",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "IX_comp_cycle_budgets_tenant_id_company_id",
                table: "comp_cycle_budgets",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_comp_cycles_company_id",
                table: "comp_cycles",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_comp_cycles_tenant_id_company_id",
                table: "comp_cycles",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_comp_cycles_tenant_id_company_id_effective_date",
                table: "comp_cycles",
                columns: new[] { "tenant_id", "company_id", "effective_date" });

            migrationBuilder.CreateIndex(
                name: "IX_comp_fx_rates_cycle_id_from_currency",
                table: "comp_fx_rates",
                columns: new[] { "cycle_id", "from_currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_comp_fx_rates_tenant_id_company_id",
                table: "comp_fx_rates",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_comp_guidelines_cycle_id_rating_compa_ratio_min",
                table: "comp_guidelines",
                columns: new[] { "cycle_id", "rating", "compa_ratio_min" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_comp_guidelines_tenant_id_company_id",
                table: "comp_guidelines",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_comp_worksheet_lines_change_request_id",
                table: "comp_worksheet_lines",
                column: "change_request_id",
                filter: "change_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_comp_worksheet_lines_cycle_id_department_id",
                table: "comp_worksheet_lines",
                columns: new[] { "cycle_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_comp_worksheet_lines_employee_id",
                table: "comp_worksheet_lines",
                column: "employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_comp_worksheet_lines_reversal_change_request_id",
                table: "comp_worksheet_lines",
                column: "reversal_change_request_id",
                filter: "reversal_change_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_comp_worksheet_lines_tenant_id_company_id",
                table: "comp_worksheet_lines",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_comp_worksheet_lines_worksheet_id_employee_id",
                table: "comp_worksheet_lines",
                columns: new[] { "worksheet_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_comp_worksheet_lines__one_award",
                table: "comp_worksheet_lines",
                columns: new[] { "tenant_id", "cycle_id", "employee_id" },
                unique: true,
                filter: "change_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_comp_worksheets_approval_request_id",
                table: "comp_worksheets",
                column: "approval_request_id",
                filter: "approval_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_comp_worksheets_cycle_id_manager_employee_id_scenario_name",
                table: "comp_worksheets",
                columns: new[] { "cycle_id", "manager_employee_id", "scenario_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_comp_worksheets_manager_employee_id",
                table: "comp_worksheets",
                column: "manager_employee_id");

            migrationBuilder.CreateIndex(
                name: "IX_comp_worksheets_tenant_id_company_id",
                table: "comp_worksheets",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ux_comp_worksheets__one_primary",
                table: "comp_worksheets",
                columns: new[] { "cycle_id", "manager_employee_id" },
                unique: true,
                filter: "is_primary");

            migrationBuilder.CreateIndex(
                name: "IX_pay_benchmark_rows_designation_id",
                table: "pay_benchmark_rows",
                column: "designation_id");

            migrationBuilder.CreateIndex(
                name: "IX_pay_benchmark_rows_grade_id",
                table: "pay_benchmark_rows",
                column: "grade_id");

            migrationBuilder.CreateIndex(
                name: "IX_pay_benchmark_rows_set_id_job_family_level",
                table: "pay_benchmark_rows",
                columns: new[] { "set_id", "job_family", "level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pay_benchmark_rows_tenant_id_company_id",
                table: "pay_benchmark_rows",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_pay_benchmark_rows_tenant_id_designation_id",
                table: "pay_benchmark_rows",
                columns: new[] { "tenant_id", "designation_id" },
                filter: "designation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_pay_benchmark_rows_tenant_id_grade_id",
                table: "pay_benchmark_rows",
                columns: new[] { "tenant_id", "grade_id" },
                filter: "grade_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_pay_benchmark_sets_company_id",
                table: "pay_benchmark_sets",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "IX_pay_benchmark_sets_tenant_id_company_id",
                table: "pay_benchmark_sets",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_pay_benchmark_sets_tenant_id_company_id_is_active_survey_da~",
                table: "pay_benchmark_sets",
                columns: new[] { "tenant_id", "company_id", "is_active", "survey_date" });

            migrationBuilder.CreateIndex(
                name: "IX_pay_benchmark_sets_tenant_id_company_id_source_file_sha256",
                table: "pay_benchmark_sets",
                columns: new[] { "tenant_id", "company_id", "source_file_sha256" },
                unique: true);

            // PostgreSQL-only CHECK (EXTRACT): an award's effective date is the 1st of a month.
            migrationBuilder.Sql(AddEffectiveFirstOfMonthCheck);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DropSalaryRowExclusion);

            migrationBuilder.DropTable(
                name: "comp_cycle_budgets");

            migrationBuilder.DropTable(
                name: "comp_fx_rates");

            migrationBuilder.DropTable(
                name: "comp_guidelines");

            migrationBuilder.DropTable(
                name: "comp_worksheet_lines");

            migrationBuilder.DropTable(
                name: "pay_benchmark_rows");

            migrationBuilder.DropTable(
                name: "comp_worksheets");

            migrationBuilder.DropTable(
                name: "pay_benchmark_sets");

            migrationBuilder.DropTable(
                name: "comp_cycles");
        }
    }
}
