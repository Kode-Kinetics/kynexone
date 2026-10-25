using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Release 2 of 2: drops 16 columns that nothing reads (11 on tenant_hr_configs, 5 on attendance_policies). Release 1
    /// (StopMappingDeadConfigColumns) stopped mapping them and gave each a default; it must be live before this runs.
    /// The model snapshot is unchanged by this migration (it never contained them), so the drops are explicit SQL.
    /// The stored values are not recoverable after Up. Down re-adds every column with its original type, NOT NULL and
    /// default, so the schema can be rolled back; the values come back as the default.
    /// </summary>
    public partial class DropDeadConfigColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs DROP COLUMN IF EXISTS use_dept_head_approval;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs DROP COLUMN IF EXISTS use_hr_final_approval;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs DROP COLUMN IF EXISTS use_supervisor_before_manager;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs DROP COLUMN IF EXISTS allow_dotted_line_approval;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs DROP COLUMN IF EXISTS auto_create_dept_on_import;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs DROP COLUMN IF EXISTS auto_create_designation_on_import;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs DROP COLUMN IF EXISTS require_import_preview_before_commit;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs DROP COLUMN IF EXISTS allow_cross_dept_manager;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs DROP COLUMN IF EXISTS allow_cross_location_manager;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs DROP COLUMN IF EXISTS require_cost_center_for_payroll;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs DROP COLUMN IF EXISTS require_grade_for_approval_policy;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies DROP COLUMN IF EXISTS late_threshold_minutes;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies DROP COLUMN IF EXISTS absent_threshold_minutes;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies DROP COLUMN IF EXISTS rounding_rule;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies DROP COLUMN IF EXISTS requires_overtime_approval;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies DROP COLUMN IF EXISTS allow_absence_to_leave_conversion;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ADD COLUMN IF NOT EXISTS use_dept_head_approval boolean NOT NULL DEFAULT true;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ADD COLUMN IF NOT EXISTS use_hr_final_approval boolean NOT NULL DEFAULT true;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ADD COLUMN IF NOT EXISTS use_supervisor_before_manager boolean NOT NULL DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ADD COLUMN IF NOT EXISTS allow_dotted_line_approval boolean NOT NULL DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ADD COLUMN IF NOT EXISTS auto_create_dept_on_import boolean NOT NULL DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ADD COLUMN IF NOT EXISTS auto_create_designation_on_import boolean NOT NULL DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ADD COLUMN IF NOT EXISTS require_import_preview_before_commit boolean NOT NULL DEFAULT true;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ADD COLUMN IF NOT EXISTS allow_cross_dept_manager boolean NOT NULL DEFAULT true;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ADD COLUMN IF NOT EXISTS allow_cross_location_manager boolean NOT NULL DEFAULT true;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ADD COLUMN IF NOT EXISTS require_cost_center_for_payroll boolean NOT NULL DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ADD COLUMN IF NOT EXISTS require_grade_for_approval_policy boolean NOT NULL DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ADD COLUMN IF NOT EXISTS late_threshold_minutes integer NOT NULL DEFAULT 15;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ADD COLUMN IF NOT EXISTS absent_threshold_minutes integer NOT NULL DEFAULT 120;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ADD COLUMN IF NOT EXISTS rounding_rule text NOT NULL DEFAULT 'NearestMinute';");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ADD COLUMN IF NOT EXISTS requires_overtime_approval boolean NOT NULL DEFAULT true;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ADD COLUMN IF NOT EXISTS allow_absence_to_leave_conversion boolean NOT NULL DEFAULT true;");
        }
    }
}
