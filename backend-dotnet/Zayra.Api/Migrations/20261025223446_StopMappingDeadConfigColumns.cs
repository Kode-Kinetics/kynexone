using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Release 1 of 2 for 16 columns that nothing reads: 11 switches on tenant_hr_configs and 5 settings on
    /// attendance_policies. The application stops mapping them (the model snapshot no longer contains them) and this
    /// migration gives each one a database default equal to the value the old C# initializer supplied, so an insert
    /// that no longer names them still satisfies NOT NULL. Nothing is dropped and no stored value changes: the release
    /// that is live while this runs still names every column, so the migration only ALTERs defaults. The drop is a
    /// separate, later release. Down only removes the defaults; the columns and their data are untouched.
    /// </summary>
    public partial class StopMappingDeadConfigColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN use_dept_head_approval SET DEFAULT true;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN use_hr_final_approval SET DEFAULT true;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN use_supervisor_before_manager SET DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN allow_dotted_line_approval SET DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN auto_create_dept_on_import SET DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN auto_create_designation_on_import SET DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN require_import_preview_before_commit SET DEFAULT true;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN allow_cross_dept_manager SET DEFAULT true;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN allow_cross_location_manager SET DEFAULT true;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN require_cost_center_for_payroll SET DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN require_grade_for_approval_policy SET DEFAULT false;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ALTER COLUMN late_threshold_minutes SET DEFAULT 15;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ALTER COLUMN absent_threshold_minutes SET DEFAULT 120;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ALTER COLUMN rounding_rule SET DEFAULT 'NearestMinute';");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ALTER COLUMN requires_overtime_approval SET DEFAULT true;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ALTER COLUMN allow_absence_to_leave_conversion SET DEFAULT true;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN use_dept_head_approval DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN use_hr_final_approval DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN use_supervisor_before_manager DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN allow_dotted_line_approval DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN auto_create_dept_on_import DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN auto_create_designation_on_import DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN require_import_preview_before_commit DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN allow_cross_dept_manager DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN allow_cross_location_manager DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN require_cost_center_for_payroll DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE tenant_hr_configs ALTER COLUMN require_grade_for_approval_policy DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ALTER COLUMN late_threshold_minutes DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ALTER COLUMN absent_threshold_minutes DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ALTER COLUMN rounding_rule DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ALTER COLUMN requires_overtime_approval DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE attendance_policies ALTER COLUMN allow_absence_to_leave_conversion DROP DEFAULT;");
        }
    }
}
