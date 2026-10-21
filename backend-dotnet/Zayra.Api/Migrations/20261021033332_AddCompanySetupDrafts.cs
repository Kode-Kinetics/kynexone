using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanySetupDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "unified_national_number",
                table: "companies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "company_setup_drafts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    step = table.Column<short>(type: "smallint", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "jsonb", nullable: false),
                    result_company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_setup_drafts", x => x.id);
                    table.CheckConstraint("ck_company_setup_drafts__body_size", "octet_length(body::text) <= 131072");
                    table.CheckConstraint("ck_company_setup_drafts__status", "status IN ('Open', 'Submitted', 'Discarded')");
                    table.CheckConstraint("ck_company_setup_drafts__step", "step BETWEEN 1 AND 7");
                    table.CheckConstraint("ck_company_setup_drafts__submitted_pair", "(status = 'Submitted') = (submitted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_company_setup_drafts__version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_company_setup_drafts_companies_result_company_id",
                        column: x => x.result_company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_company_setup_drafts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_company_setup_drafts_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_company_setup_drafts__tenant_status_updated",
                table: "company_setup_drafts",
                columns: new[] { "tenant_id", "status", "updated_at_utc" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_company_setup_drafts_created_by_user_id",
                table: "company_setup_drafts",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_company_setup_drafts_result_company_id",
                table: "company_setup_drafts",
                column: "result_company_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "company_setup_drafts");

            migrationBuilder.DropColumn(
                name: "unified_national_number",
                table: "companies");
        }
    }
}
