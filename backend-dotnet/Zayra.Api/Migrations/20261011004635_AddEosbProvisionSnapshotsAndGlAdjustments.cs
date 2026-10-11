using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEosbProvisionSnapshotsAndGlAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "eosb_provision_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    measured_at = table.Column<DateOnly>(type: "date", nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    obligation = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    carried_before = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    movement = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    is_catch_up = table.Column<bool>(type: "boolean", nullable: false),
                    wage_base = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    service_years = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    rule = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eosb_provision_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_eosb_provision_snapshots_payroll_runs_payroll_run_id",
                        column: x => x.payroll_run_id,
                        principalTable: "payroll_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gl_adjustment_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    accounting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    source_account = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    target_account = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    report_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gl_adjustment_requests", x => x.id);
                    table.CheckConstraint("ck_gl_adjustment_requests__kind", "kind IN ('EosbTrueUp','LegacyAbsenceReclass')");
                    table.CheckConstraint("ck_gl_adjustment_requests__status", "status IN ('Pending','Approved','Rejected')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_eosb_provision_snapshots_payroll_run_id",
                table: "eosb_provision_snapshots",
                column: "payroll_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_eosb_provision_snapshots_tenant_id_company_id",
                table: "eosb_provision_snapshots",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_eosb_provision_snapshots_tenant_id_company_id_period",
                table: "eosb_provision_snapshots",
                columns: new[] { "tenant_id", "company_id", "period" });

            migrationBuilder.CreateIndex(
                name: "IX_eosb_provision_snapshots_tenant_id_payroll_run_id",
                table: "eosb_provision_snapshots",
                columns: new[] { "tenant_id", "payroll_run_id" });

            migrationBuilder.CreateIndex(
                name: "ux_eosb_provision_snapshots_tenant_employee_period",
                table: "eosb_provision_snapshots",
                columns: new[] { "tenant_id", "employee_id", "period" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gl_adjustment_requests_tenant_id_company_id",
                table: "gl_adjustment_requests",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gl_adjustment_requests_tenant_id_company_id_kind_status",
                table: "gl_adjustment_requests",
                columns: new[] { "tenant_id", "company_id", "kind", "status" });

            // ── Idempotent data seed: the POD-C2 provisioning switch in the CLIENT-RATE ALLOW-LIST ──────
            // RatesController refuses any key missing from a tenant's client_rate_definitions, and every live
            // tenant already has rows, so without this the switch could never be turned on. Seeding the
            // DEFINITION changes nobody's books: with no company_rate_policies row the mode resolves Off.
            migrationBuilder.Sql(@"
INSERT INTO client_rate_definitions (id, tenant_id, rate_key, rate_category, data_type, unit, min_value, max_value, description, is_active, created_at_utc)
SELECT gen_random_uuid(), t.tenant_id, 'payparameter.eosb_provisioning', 'PayParameter', 'string', 'enum', NULL, NULL,
       'Monthly end-of-service provision at payroll Lock: Off (default) | StatutoryMonthly', TRUE, now()
FROM (SELECT DISTINCT tenant_id FROM client_rate_definitions) t
WHERE NOT EXISTS (
    SELECT 1 FROM client_rate_definitions c
    WHERE c.tenant_id = t.tenant_id AND c.rate_key = 'payparameter.eosb_provisioning'
);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove ONLY what this migration seeded. A company rate row for the key would be orphaned by the
            // allow-list removal, so those go too (they only ever switched provisioning on/off).
            migrationBuilder.Sql("DELETE FROM company_rate_policies WHERE rate_key = 'payparameter.eosb_provisioning';");
            migrationBuilder.Sql("DELETE FROM client_rate_definitions WHERE rate_key = 'payparameter.eosb_provisioning';");

            migrationBuilder.DropTable(
                name: "eosb_provision_snapshots");

            migrationBuilder.DropTable(
                name: "gl_adjustment_requests");
        }
    }
}
