using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingP1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "create_idempotency_key",
                table: "employees",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "create_request_fingerprint",
                table: "employees",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "nationality_code",
                table: "employees",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "nationality_mapping_state",
                table: "employees",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "Unset");

            migrationBuilder.AddColumn<DateOnly>(
                name: "original_joining_date",
                table: "employees",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_employees_nationality_review",
                table: "employees",
                column: "tenant_id",
                filter: "nationality_mapping_state IN ('ProposedAuto','Unresolved') AND NOT is_deleted");

            migrationBuilder.CreateIndex(
                name: "ux_employees_tenant_create_idempotency_key",
                table: "employees",
                columns: new[] { "tenant_id", "create_idempotency_key" },
                unique: true,
                filter: "create_idempotency_key IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_employees__nationality_mapping_state",
                table: "employees",
                sql: "nationality_mapping_state IN ('Unset','Confirmed','ProposedAuto','Unresolved')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_employees_nationality_review",
                table: "employees");

            migrationBuilder.DropIndex(
                name: "ux_employees_tenant_create_idempotency_key",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees__nationality_mapping_state",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "create_idempotency_key",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "create_request_fingerprint",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "nationality_code",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "nationality_mapping_state",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "original_joining_date",
                table: "employees");
        }
    }
}
