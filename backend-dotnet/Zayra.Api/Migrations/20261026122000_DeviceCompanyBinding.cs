using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// M4 - binds a device to its company. EXPAND only: one nullable column, metadata-only on Postgres. Existing devices that
    /// sit on a branch are backfilled from the branch's company; devices with no branch stay NULL (tenant-wide resolution, the
    /// behaviour they had). Down drops the column.
    /// </summary>
    public partial class DeviceCompanyBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "company_id", table: "attendance_devices", type: "uuid", nullable: true);
            migrationBuilder.Sql(
                "UPDATE attendance_devices d SET company_id = b.company_id FROM branches b " +
                "WHERE d.branch_id = b.id AND d.tenant_id = b.tenant_id AND d.company_id IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "company_id", table: "attendance_devices");
        }
    }
}
