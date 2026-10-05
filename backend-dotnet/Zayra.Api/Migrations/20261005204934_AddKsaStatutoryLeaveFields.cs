using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Two additive columns for KSA statutory special leave, both safe to add to a live table:
    /// <list type="bullet">
    /// <item><c>leave_requests.statutory_leave_kind</c> (nullable): the statutory kind a request was
    /// submitted as, stamped at submission and read at approval and cancellation.</item>
    /// <item><c>leave_policies.allows_hajj_beyond_statutory_eligibility</c> (default false): the
    /// company's explicit choice to grant Hajj leave beyond Art. 114's once-only, two-year rule.</item>
    /// </list>
    /// </summary>
    public partial class AddKsaStatutoryLeaveFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "statutory_leave_kind",
                table: "leave_requests",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "allows_hajj_beyond_statutory_eligibility",
                table: "leave_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "statutory_leave_kind",
                table: "leave_requests");

            migrationBuilder.DropColumn(
                name: "allows_hajj_beyond_statutory_eligibility",
                table: "leave_policies");
        }
    }
}
