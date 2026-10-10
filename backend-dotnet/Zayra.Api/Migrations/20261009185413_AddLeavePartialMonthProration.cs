using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Opt-in calendar-day proration for partial monthly leave accrual periods. Existing
    /// policies remain full-month policies; no balances or posted ledger rows are changed.
    /// </summary>
    public partial class AddLeavePartialMonthProration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "prorate_partial_months",
                table: "leave_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM leave_policies WHERE prorate_partial_months) THEN
                        RAISE EXCEPTION 'Partial-month leave proration is configured. Review those policies and disable it explicitly before rolling back.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "prorate_partial_months",
                table: "leave_policies");
        }
    }
}
