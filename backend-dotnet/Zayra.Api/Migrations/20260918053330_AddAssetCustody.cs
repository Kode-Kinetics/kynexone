using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetCustody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    asset_tag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    serial_number = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    category_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    make = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    model = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    purchase_date = table.Column<DateOnly>(type: "date", nullable: true),
                    purchase_cost = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    condition = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    retired_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    retirement_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.id);
                    table.CheckConstraint("ck_assets_status", "status IN ('InStock','Assigned','InRepair','Retired','Lost')");
                });

            migrationBuilder.CreateTable(
                name: "asset_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    employee_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    employee_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: false),
                    issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    issued_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    condition_on_issue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issue_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    expected_return_date = table.Column<DateOnly>(type: "date", nullable: true),
                    returned_on = table.Column<DateOnly>(type: "date", nullable: true),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    condition_on_return = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    return_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    transferred_to_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    write_off_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    due_soon_reminder_sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    overdue_reminder_sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_assignments", x => x.id);
                    table.CheckConstraint("ck_asset_assignments_status", "status IN ('Active','Returned','Transferred','WrittenOff')");
                    table.ForeignKey(
                        name: "FK_asset_assignments_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_write_off_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    employee_id = table.Column<int>(type: "integer", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_comments = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_write_off_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_asset_write_off_requests_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_assignments_status_expected_return_date",
                table: "asset_assignments",
                columns: new[] { "status", "expected_return_date" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_assignments_tenant_id_asset_id_issued_at_utc",
                table: "asset_assignments",
                columns: new[] { "tenant_id", "asset_id", "issued_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_assignments_tenant_id_company_id",
                table: "asset_assignments",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_assignments_tenant_id_employee_id_status",
                table: "asset_assignments",
                columns: new[] { "tenant_id", "employee_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_asset_assignments_one_active_holder",
                table: "asset_assignments",
                column: "asset_id",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_asset_write_off_requests_approval_request_id",
                table: "asset_write_off_requests",
                column: "approval_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_asset_write_off_requests_tenant_id_company_id",
                table: "asset_write_off_requests",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_write_off_requests_tenant_id_employee_id",
                table: "asset_write_off_requests",
                columns: new[] { "tenant_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_write_off_requests_tenant_id_status",
                table: "asset_write_off_requests",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_asset_write_off_requests_one_pending",
                table: "asset_write_off_requests",
                column: "asset_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_assets_tenant_id_asset_tag",
                table: "assets",
                columns: new[] { "tenant_id", "asset_tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assets_tenant_id_company_id",
                table: "assets",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_tenant_id_serial_number",
                table: "assets",
                columns: new[] { "tenant_id", "serial_number" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_tenant_id_status",
                table: "assets",
                columns: new[] { "tenant_id", "status" });

            // Existing tenants get the same default AssetWriteOff workflow new tenants get from
            // TenantProvisioningBundle. Idempotent: skipped where the tenant already has an active
            // tenant-wide AssetWriteOff workflow or the code is taken.
            migrationBuilder.Sql(InstallDefaultWriteOffWorkflowSql);
        }

        /// <summary>One HR-approver step (HR Manager queue), final — same shape as the other seeded defaults.</summary>
        internal const string InstallDefaultWriteOffWorkflowSql = """
            WITH t AS (
                SELECT x.id AS tenant_id FROM tenants x
                WHERE NOT EXISTS (SELECT 1 FROM approval_workflows w
                                  WHERE w.tenant_id = x.id AND w.entity_name = 'AssetWriteOff' AND w.is_active
                                    AND w.department_id IS NULL AND w.grade_id IS NULL)
                  AND NOT EXISTS (SELECT 1 FROM approval_workflows w
                                  WHERE w.tenant_id = x.id AND w.code = 'ASSET-WRITEOFF-DEFAULT')
            ), ins AS (
                INSERT INTO approval_workflows
                    (id, tenant_id, code, name, entity_name, department_id, grade_id, is_default, is_active, created_at_utc)
                SELECT gen_random_uuid(), t.tenant_id, 'ASSET-WRITEOFF-DEFAULT', 'Default Asset Write-off Approval',
                       'AssetWriteOff', NULL, NULL, true, true, now()
                FROM t
                RETURNING id, tenant_id
            )
            INSERT INTO approval_workflow_steps
                (id, tenant_id, workflow_id, step_order, step_name, approver_role, approver_type,
                 specific_employee_id, escalation_after_hours, is_final_step)
            SELECT gen_random_uuid(), ins.tenant_id, ins.id, 1, 'HR Approval', 'HR Manager', 'HR', NULL, NULL, true
            FROM ins;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deactivate rather than delete: approval_requests may reference the workflow.
            migrationBuilder.Sql("UPDATE approval_workflows SET is_active = false WHERE code = 'ASSET-WRITEOFF-DEFAULT' AND entity_name = 'AssetWriteOff';");

            migrationBuilder.DropTable(
                name: "asset_assignments");

            migrationBuilder.DropTable(
                name: "asset_write_off_requests");

            migrationBuilder.DropTable(
                name: "assets");
        }
    }
}
