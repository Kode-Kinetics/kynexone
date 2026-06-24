using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthorTypeToHRRequestComment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "author_name",
                table: "hr_request_comments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "author_type",
                table: "hr_request_comments",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Backfill legacy comments (created before two-sided threads existed) as
            // employee-authored so the thread renders sensibly and the "HR responded?"
            // indicator is not falsely tripped by pre-existing rows.
            migrationBuilder.Sql("UPDATE hr_request_comments SET author_type = 'Employee' WHERE author_type = '' OR author_type IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "author_name",
                table: "hr_request_comments");

            migrationBuilder.DropColumn(
                name: "author_type",
                table: "hr_request_comments");
        }
    }
}
