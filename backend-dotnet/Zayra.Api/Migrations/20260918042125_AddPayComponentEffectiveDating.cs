using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPayComponentEffectiveDating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "gl_driver_key",
                table: "payroll_earnings",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gl_driver_key",
                table: "payroll_deductions",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "effective_from",
                table: "pay_components",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "effective_to",
                table: "pay_components",
                type: "date",
                nullable: true);

            // ── F2: re-key the per-scope UNIQUE to include the version start ────────────────────────────
            // A component is now a sequence of dated versions sharing (tenant, company, code, type). The old
            // UNIQUE (tenant, company, code, type) would forbid a second version, so it becomes
            // (tenant, company, code, type, effective_from) NULLS NOT DISTINCT — still one "since the
            // beginning" (NULL) version per scope, never two versions starting on the same day. Partial on
            // is_deleted = FALSE so a retired version never blocks re-creating one from the same date.
            // Same raw-SQL idiom as AddPayComponentDefinitions (Npgsql 8 cannot express NULLS NOT DISTINCT).
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_pay_components_scope_code_type;");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_pay_components_scope_code_type_from " +
                "ON pay_components (tenant_id, company_id, code, component_type, effective_from) NULLS NOT DISTINCT " +
                "WHERE is_deleted = FALSE;");

            // ── F2: existing tenants get the system catalog as REAL rows ──────────────────────────────────
            // Before F2 the only writer was PayComponentSeeder behind POST /api/finance/gl/seed-defaults and
            // the two platform create-tenant paths, so tenants created any other way (the bootstrap tenant,
            // the demo seeders, every tenant older than the seeder) hold ZERO rows and run on the compiled
            // PayComponentCatalog fallback. This backfill gives every tenant that has no tenant-default
            // SYSTEM row the 17 system rows, so the fallback becomes a genuine last resort.
            //
            // Output-neutral by proof: the golden master runs every scenario with the store EMPTY (compiled
            // fallback) and SEEDED and asserts byte-identical slips, lines, validation codes and GL.
            //
            // The VALUES are a FROZEN copy of PayComponentCatalog.SystemComponentSeeds as of this migration
            // (generated from the compiled catalog, not hand-typed); PayComponentMigrationBackfillTests fails
            // if the two ever diverge. Insert-if-absent per (tenant, NULL company, code, type), so a tenant's
            // own tenant-default row with a system code (e.g. a relabel) is never collided with or replaced.
            migrationBuilder.Sql(@"
INSERT INTO pay_components (
    id, tenant_id, company_id, code, name_en, name_ar, component_type, calc_method, structure_field, value,
    formula_expression, provider_key, is_taxable, gosi_subject, wps_included, eosb_included, gl_driver_key,
    emit_when_zero, is_family, display_order, is_system, is_statutory, is_active, is_deleted, created_at_utc,
    effective_from, effective_to)
SELECT gen_random_uuid(), t.id, NULL, s.code, s.name_en, s.name_ar, s.component_type, s.calc_method,
    s.structure_field, NULL, NULL, s.provider_key, s.is_taxable, s.gosi_subject, s.wps_included, s.eosb_included,
    s.gl_driver_key, s.emit_when_zero, s.is_family, s.display_order, TRUE, s.is_statutory, TRUE, FALSE, now(),
    NULL, NULL
FROM tenants t
CROSS JOIN (VALUES
" + BackfillValues + @"
) AS s(code, name_en, name_ar, component_type, calc_method, structure_field, provider_key, gl_driver_key,
       display_order, emit_when_zero, is_family, is_statutory, is_taxable, gosi_subject, wps_included, eosb_included)
WHERE NOT EXISTS (
    SELECT 1 FROM pay_components x
    WHERE x.tenant_id = t.id AND x.company_id IS NULL AND x.is_system = TRUE AND x.is_deleted = FALSE)
  AND NOT EXISTS (
    SELECT 1 FROM pay_components y
    WHERE y.tenant_id = t.id AND y.company_id IS NULL AND y.code = s.code
      AND y.component_type = s.component_type AND y.is_deleted = FALSE);");
        }

        /// <summary>F2 — frozen system catalog rows for the existing-tenant backfill: (code, name_en, name_ar,
        /// component_type, calc_method, structure_field, provider_key, gl_driver_key, display_order,
        /// emit_when_zero, is_family, is_statutory, is_taxable, gosi_subject, wps_included, eosb_included).
        ///
        /// <para>S1/A1 — HOUSING's <c>eosb_included</c> changed FALSE → TRUE here. Saudi Labour Law
        /// Art. 84 awards on the last wage and Art. 2 defines wage as basic plus all due increments, so
        /// the previous value was wrong as a statement of law. <c>FrozenBackfillValues_MatchTheCompiledCatalog</c>
        /// requires this constant to track <see cref="PayComponentCatalog"/> exactly, which is why the
        /// change lands in an already-authored migration rather than a new one.</para>
        ///
        /// <para><b>A database migrated BEFORE this change keeps <c>eosb_included = false</c> on its
        /// HOUSING rows.</b> That costs no money: <c>KsaEndOfServiceCalculator</c> applies basic + housing
        /// as a NON-CONFIGURABLE statutory floor, and the catalog flag can only ever raise a base above
        /// statute, never lower it. What a stale row does affect is reporting — the settlement's
        /// <c>IncludedComponents</c> list will not name HOUSING. Correct it with:
        /// <code>
        /// UPDATE pay_components SET eosb_included = TRUE
        ///  WHERE code = 'HOUSING' AND is_system = TRUE AND is_deleted = FALSE AND eosb_included = FALSE;
        /// </code>
        /// Deliberately not issued as a new migration from this stream: it is a data correction with no
        /// schema change and no money effect, and a model-snapshot touch collides with every branch
        /// currently in flight.</para></summary>
        public const string BackfillValues = @"
    ('BONUS', 'Bonus', 'مكافأة', 'Earning', 'Integration', NULL, 'Bonus', 'EARN:BONUS', 10, FALSE, TRUE, FALSE, FALSE, FALSE, TRUE, FALSE),
    ('ADJ', 'Adjustment', 'تسوية', 'Earning', 'Integration', NULL, 'Adjustment', NULL, 20, FALSE, TRUE, FALSE, FALSE, FALSE, TRUE, FALSE),
    ('BASIC', 'Basic salary', 'الراتب الأساسي', 'Earning', 'StructureField', 'BasicSalary', NULL, 'EARN:BASIC', 30, TRUE, FALSE, FALSE, TRUE, TRUE, TRUE, TRUE),
    ('HOUSING', 'Housing allowance', 'بدل السكن', 'Earning', 'StructureField', 'HousingAllowance', NULL, 'EARN:HOUSING', 40, FALSE, FALSE, FALSE, FALSE, TRUE, TRUE, TRUE),
    ('TRANSPORT', 'Transport allowance', 'بدل النقل', 'Earning', 'StructureField', 'TransportAllowance', NULL, 'EARN:TRANSPORT', 50, FALSE, FALSE, FALSE, FALSE, FALSE, TRUE, FALSE),
    ('OTHER_ALLOWANCES', 'Other allowances', 'بدلات أخرى', 'Earning', 'StructureField', 'OtherAllowancesComposite', NULL, 'EARN:OTHER_ALLOWANCES', 60, FALSE, FALSE, FALSE, FALSE, FALSE, TRUE, FALSE),
    ('OVERTIME', 'Overtime', 'العمل الإضافي', 'Earning', 'Integration', NULL, 'Overtime', 'EARN:OVERTIME', 70, FALSE, FALSE, FALSE, FALSE, FALSE, TRUE, FALSE),
    ('FIXED_DEDUCTION', 'Fixed deduction', 'خصم ثابت', 'Deduction', 'StructureField', 'FixedDeduction', NULL, 'DED:FIXED_DEDUCTION', 10, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE),
    ('INCOME_TAX', 'Income tax', 'ضريبة الدخل', 'Deduction', 'Integration', NULL, 'Tax', 'DED:TAX', 20, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE),
    ('ATTENDANCE', 'Late/early attendance deduction', 'خصم الحضور', 'Deduction', 'Integration', NULL, 'Attendance.Short', 'DED:ATTENDANCE', 30, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE),
    ('LOP_DEDUCTION', 'Loss of Pay', 'خصم الغياب', 'Deduction', 'Integration', NULL, 'Attendance.Lop', 'DED:ATTENDANCE', 40, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE),
    ('LEAVE', 'Leave deduction', 'خصم الإجازة', 'Deduction', 'Integration', NULL, 'Leave', 'DED:LEAVE', 50, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE),
    ('LOAN_EMI', 'Loan instalment', 'قسط القرض', 'Deduction', 'Integration', NULL, 'Loan', 'DED:LOAN', 60, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE),
    ('ADVANCE_EMI', 'Salary advance repayment', 'سداد السلفة', 'Deduction', 'Integration', NULL, 'Advance', 'DED:LOAN', 70, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE, FALSE),
    ('ADJ', 'Adjustment', 'تسوية', 'Deduction', 'Integration', NULL, 'Adjustment', NULL, 80, FALSE, TRUE, FALSE, FALSE, FALSE, FALSE, FALSE),
    ('STATUTORY_EE', 'Social insurance (Employee)', 'التأمينات (الموظف)', 'Deduction', 'Statutory', NULL, 'Statutory', 'DED:STATUTORY_EE', 90, FALSE, TRUE, TRUE, FALSE, TRUE, FALSE, FALSE),
    ('STATUTORY_ER', 'Social insurance (Employer)', 'التأمينات (صاحب العمل)', 'EmployerContribution', 'Statutory', NULL, 'Statutory', 'DED:STATUTORY_ER', 100, FALSE, TRUE, TRUE, FALSE, TRUE, FALSE, FALSE)";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema only. The backfilled system rows are left in place: they are output-neutral (golden
            // master) and identical to what POST /api/finance/gl/seed-defaults writes. Restoring the old
            // UNIQUE fails by design if a tenant already holds more than one dated version of a component —
            // collapse those versions first (keep one per scope) before rolling back.
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_pay_components_scope_code_type_from;");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_pay_components_scope_code_type " +
                "ON pay_components (tenant_id, company_id, code, component_type) NULLS NOT DISTINCT;");

            migrationBuilder.DropColumn(
                name: "gl_driver_key",
                table: "payroll_earnings");

            migrationBuilder.DropColumn(
                name: "gl_driver_key",
                table: "payroll_deductions");

            migrationBuilder.DropColumn(
                name: "effective_from",
                table: "pay_components");

            migrationBuilder.DropColumn(
                name: "effective_to",
                table: "pay_components");
        }
    }
}
