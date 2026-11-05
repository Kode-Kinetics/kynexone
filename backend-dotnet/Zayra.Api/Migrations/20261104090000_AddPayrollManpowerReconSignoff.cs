using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPayrollManpowerReconSignoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "process_version",
                table: "payroll_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "payroll_run_recon_signoffs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    process_version = table.Column<int>(type: "integer", nullable: false),
                    prior_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prior_process_version = table.Column<int>(type: "integer", nullable: true),
                    snapshot_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    signed_off_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    signed_off_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    signed_off_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    superseded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    superseded_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_run_recon_signoffs", x => x.id);
                    table.CheckConstraint("ck_payroll_run_recon_signoffs__reason", "\"superseded_reason\" IS NULL OR \"superseded_reason\" IN ('Reprocessed','Reopened','SentBack','Voided','Withdrawn','PriorChanged','InputsChanged')");
                    table.CheckConstraint("ck_payroll_run_recon_signoffs__status", "\"status\" IN ('Active','Superseded')");
                    table.CheckConstraint("ck_payroll_run_recon_signoffs__superseded_pair", "(\"status\" = 'Active' AND \"superseded_at_utc\" IS NULL AND \"superseded_reason\" IS NULL) OR (\"status\" = 'Superseded' AND \"superseded_at_utc\" IS NOT NULL AND \"superseded_reason\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_payroll_run_recon_signoffs_payroll_runs_payroll_run_id",
                        column: x => x.payroll_run_id,
                        principalTable: "payroll_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payroll_run_recon_explanations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    signoff_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_run_recon_explanations", x => x.id);
                    table.CheckConstraint("ck_payroll_run_recon_explanations__scope", "\"scope\" IN ('Item','Employee')");
                    table.ForeignKey(
                        name: "FK_payroll_run_recon_explanations_payroll_run_recon_signoffs_s~",
                        column: x => x.signoff_id,
                        principalTable: "payroll_run_recon_signoffs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_run_recon_explanations_signoff_id",
                table: "payroll_run_recon_explanations",
                column: "signoff_id");

            migrationBuilder.CreateIndex(
                name: "ux_payroll_run_recon_explanations__signoff_scope_key",
                table: "payroll_run_recon_explanations",
                columns: new[] { "tenant_id", "signoff_id", "scope", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payroll_run_recon_signoffs__run",
                table: "payroll_run_recon_signoffs",
                columns: new[] { "tenant_id", "payroll_run_id", "signed_off_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_run_recon_signoffs_payroll_run_id",
                table: "payroll_run_recon_signoffs",
                column: "payroll_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_payroll_run_recon_signoffs_tenant_id_company_id",
                table: "payroll_run_recon_signoffs",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "ux_payroll_run_recon_signoffs__run_active",
                table: "payroll_run_recon_signoffs",
                columns: new[] { "tenant_id", "payroll_run_id" },
                unique: true,
                filter: "\"status\" = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payroll_run_recon_explanations");

            migrationBuilder.DropTable(
                name: "payroll_run_recon_signoffs");

            migrationBuilder.DropColumn(
                name: "process_version",
                table: "payroll_runs");
        }
    }
}
