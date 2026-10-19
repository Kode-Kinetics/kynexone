using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class OfferCompensationPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "transport_allowance",
                table: "offer_letters",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)",
                oldPrecision: 12,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "other_allowances",
                table: "offer_letters",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)",
                oldPrecision: 12,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "housing_allowance",
                table: "offer_letters",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)",
                oldPrecision: 12,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "gross_salary",
                table: "offer_letters",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)",
                oldPrecision: 12,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "basic_salary",
                table: "offer_letters",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)",
                oldPrecision: 12,
                oldScale: 2);

            migrationBuilder.AddColumn<decimal>(
                name: "annual_recurring_cash",
                table: "offer_letters",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "compensation_currency",
                table: "offer_letters",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "compensation_precision",
                table: "offer_letters",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "compensation_snapshot_json",
                table: "offer_letters",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "offer_policy_version",
                table: "offer_letters",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "one_time_cash",
                table: "offer_letters",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "offered_salary",
                table: "job_applications",
                type: "numeric(19,3)",
                precision: 19,
                scale: 3,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)",
                oldPrecision: 12,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_companies_tenant_id_id",
                table: "companies",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "offer_company_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    precision = table.Column<int>(type: "integer", nullable: false),
                    required_component_codes_json = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_company_policies", x => x.id);
                    table.CheckConstraint("ck_offer_company_policy_precision", "precision BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_offer_company_policies_companies_tenant_id_company_id",
                        columns: x => new { x.tenant_id, x.company_id },
                        principalTable: "companies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_offer_company_policies_tenant_id_company_id",
                table: "offer_company_policies",
                columns: new[] { "tenant_id", "company_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "offer_company_policies");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_companies_tenant_id_id",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "annual_recurring_cash",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "compensation_currency",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "compensation_precision",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "compensation_snapshot_json",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "offer_policy_version",
                table: "offer_letters");

            migrationBuilder.DropColumn(
                name: "one_time_cash",
                table: "offer_letters");

            migrationBuilder.AlterColumn<decimal>(
                name: "transport_allowance",
                table: "offer_letters",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "other_allowances",
                table: "offer_letters",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "housing_allowance",
                table: "offer_letters",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "gross_salary",
                table: "offer_letters",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "basic_salary",
                table: "offer_letters",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "offered_salary",
                table: "job_applications",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(19,3)",
                oldPrecision: 19,
                oldScale: 3,
                oldNullable: true);
        }
    }
}
