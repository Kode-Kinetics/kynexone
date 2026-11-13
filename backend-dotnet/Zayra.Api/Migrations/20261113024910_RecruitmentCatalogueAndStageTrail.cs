using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class RecruitmentCatalogueAndStageTrail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "grade_id",
                table: "manpower_requisitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "grade_id",
                table: "job_openings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "from_stage",
                table: "application_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "to_stage",
                table: "application_events",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "grade_id",
                table: "manpower_requisitions");

            migrationBuilder.DropColumn(
                name: "grade_id",
                table: "job_openings");

            migrationBuilder.DropColumn(
                name: "from_stage",
                table: "application_events");

            migrationBuilder.DropColumn(
                name: "to_stage",
                table: "application_events");
        }
    }
}
