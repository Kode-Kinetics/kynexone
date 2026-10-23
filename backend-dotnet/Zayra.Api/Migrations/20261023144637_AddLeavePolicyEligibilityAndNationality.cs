using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Two customer-defined leave policy rules: months of service before the leave can be taken, and a
    /// locals/expats scope. Existing policies get 0 and 'Any', which is exactly their current behaviour.
    /// </summary>
    public partial class AddLeavePolicyEligibilityAndNationality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "minimum_service_months",
                table: "leave_policies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "nationality_scope",
                table: "leave_policies",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Any");

            migrationBuilder.AddCheckConstraint(
                name: "ck_leave_policies__minimum_service_months",
                table: "leave_policies",
                sql: "minimum_service_months BETWEEN 0 AND 600");

            migrationBuilder.AddCheckConstraint(
                name: "ck_leave_policies__nationality_scope",
                table: "leave_policies",
                sql: "nationality_scope IN ('Any', 'National', 'Expatriate')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM leave_policies WHERE minimum_service_months <> 0 OR nationality_scope <> 'Any') THEN
                        RAISE EXCEPTION 'Leave policies use a service requirement or a locals/expats scope. Review and clear them before rolling back.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_leave_policies__minimum_service_months",
                table: "leave_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_leave_policies__nationality_scope",
                table: "leave_policies");

            migrationBuilder.DropColumn(
                name: "minimum_service_months",
                table: "leave_policies");

            migrationBuilder.DropColumn(
                name: "nationality_scope",
                table: "leave_policies");
        }
    }
}
