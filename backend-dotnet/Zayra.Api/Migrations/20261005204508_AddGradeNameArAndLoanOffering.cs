using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGradeNameArAndLoanOffering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Slice L1 fix round — additive only. is_offered defaults TRUE so every existing policy keeps offering
            // its loan type; name_ar is optional (readers fall back to name).
            migrationBuilder.AddColumn<bool>(
                name: "is_offered",
                table: "loan_policies",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "name_ar",
                table: "grades",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping is_offered would silently re-offer every switched-off loan type; refuse over that evidence.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM loan_policies WHERE NOT is_offered) OR EXISTS (SELECT 1 FROM grades WHERE name_ar IS NOT NULL) THEN
                        RAISE EXCEPTION 'Company loan offerings or Arabic grade names exist. Preserve them and use a forward corrective migration.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "is_offered",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "name_ar",
                table: "grades");
        }
    }
}
