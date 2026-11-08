using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// G-013 (owner decision): the GroupDraftPlatformApproval company-creation mode is retired. It created Draft companies that could
    /// never be activated (the platform approve endpoint was disabled). Every tenant still holding it is moved to the default mode
    /// (GroupSelfServiceWithinLimit) and one platform audit row is written per tenant moved. Data only: no schema change, no row is
    /// deleted, and existing Draft companies stay readable (a platform admin activates them from the console).
    /// </summary>
    public partial class RetireDraftApprovalMode : Migration
    {
        /// <summary>
        /// Idempotent by construction: the UPDATE matches only tenants still on the retired mode, and the audit rows are written only
        /// for the rows that UPDATE returned, so a second run matches nothing and writes nothing. Frozen copy: never edit it here,
        /// write a new migration instead.
        /// </summary>
        public const string RetireSql = @"
WITH moved AS (
    UPDATE tenants
       SET company_creation_mode = 'GroupSelfServiceWithinLimit'
     WHERE company_creation_mode = 'GroupDraftPlatformApproval'
 RETURNING id)
INSERT INTO admin_audit_logs
       (id, tenant_id, company_id, entity_type, entity_id, action, old_values_json, new_values_json,
        performed_by, performed_by_name, ip_address, created_at_utc)
SELECT gen_random_uuid(), id, NULL, 'Tenant', id::text, 'CompanyCreationModeRetired',
       jsonb_build_object('mode', 'GroupDraftPlatformApproval')::text, jsonb_build_object('mode', 'GroupSelfServiceWithinLimit')::text,
       NULL, 'system:migration RetireDraftApprovalMode', '', now()
  FROM moved;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RetireSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty: the retired mode cannot be chosen any more, so there is nothing to restore.
        }
    }
}
