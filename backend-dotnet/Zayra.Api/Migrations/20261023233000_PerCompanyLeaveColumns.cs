using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Leave per company, step 1 — expand only (owner decision 2026-10-23: companies in a group are separate legal
    /// entities, "separate everything"). <c>leave_types</c> gains <c>company_id</c> (composite FK (tenant_id, company_id) →
    /// companies, RESTRICT) and its code uniqueness moves from (tenant, code) to (tenant, company_id, code) NULLS NOT
    /// DISTINCT. <c>leave_policies.company_id</c> already existed (it meant "applies only to this company"; NULL meant
    /// tenant-wide); it gains the same composite FK, and <c>legacy_group_default</c> records which policies were tenant-wide
    /// before the backfill so their precedence below company-specific policies is kept. The data backfill is the next
    /// migration, BackfillLeavePerCompany.
    ///
    /// <para>A policy whose company_id names no company of its tenant (an orphan) would fail the new FK and could never
    /// match an employee. It is archived and unassigned first, with a <c>leave_audit_logs</c> row each;
    /// docs/runbooks/org-per-company-precheck.sql counts them before deploy.</para>
    ///
    /// <para>Down refuses (unique violation on the restored (tenant, code) index) once a per-company copy exists — restore
    /// the pre-deploy database branch instead.</para>
    /// </summary>
    public partial class PerCompanyLeaveColumns : Migration
    {
        /// <summary>Archives and unassigns policies whose company is not a company of their tenant. Idempotent.</summary>
        public const string OrphanPolicySql = """
INSERT INTO leave_audit_logs (id, tenant_id, entity_type, entity_id, action, old_value, new_value, performed_by_name, reason, created_at_utc)
SELECT md5('olp:orphan:' || p.id::text)::uuid, p.tenant_id, 'LeavePolicy', p.id::text, 'CompanyOrphanArchived',
       'company_id=' || p.company_id::text || '; status=' || p.status, 'company_id=; status=Archived',
       'system: migration PerCompanyLeaveColumns',
       'The policy named a company that is not a company of this workspace, so it matched no employee. Archived and unassigned before the per-company foreign key.',
       now()
  FROM leave_policies p
 WHERE p.company_id IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM companies c WHERE c.tenant_id = p.tenant_id AND c.id = p.company_id)
ON CONFLICT (id) DO NOTHING;
UPDATE leave_policies p SET company_id = NULL, status = 'Archived', updated_at_utc = now()
 WHERE p.company_id IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM companies c WHERE c.tenant_id = p.tenant_id AND c.id = p.company_id);
""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_leave_types_tenant_id_code",
                table: "leave_types");

            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "leave_types",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "legacy_group_default",
                table: "leave_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_leave_types_tenant_id_company_id",
                table: "leave_types",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_types_tenant_id_company_id_code",
                table: "leave_types",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.Sql(OrphanPolicySql);

            migrationBuilder.AddForeignKey(
                name: "FK_leave_policies_companies_tenant_id_company_id",
                table: "leave_policies",
                columns: new[] { "tenant_id", "company_id" },
                principalTable: "companies",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_leave_types_companies_tenant_id_company_id",
                table: "leave_types",
                columns: new[] { "tenant_id", "company_id" },
                principalTable: "companies",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_leave_policies_companies_tenant_id_company_id",
                table: "leave_policies");

            migrationBuilder.DropForeignKey(
                name: "FK_leave_types_companies_tenant_id_company_id",
                table: "leave_types");

            migrationBuilder.DropIndex(
                name: "IX_leave_types_tenant_id_company_id",
                table: "leave_types");

            migrationBuilder.DropIndex(
                name: "IX_leave_types_tenant_id_company_id_code",
                table: "leave_types");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "leave_types");

            migrationBuilder.DropColumn(
                name: "legacy_group_default",
                table: "leave_policies");

            migrationBuilder.CreateIndex(
                name: "IX_leave_types_tenant_id_code",
                table: "leave_types",
                columns: new[] { "tenant_id", "code" },
                unique: true);
        }
    }
}
