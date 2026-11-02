using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>AttendanceDevice became ICompanyScoped (its admin screens are company-filtered), so the company-scope
    /// convention adds the (tenant_id, company_id) index the filter relies on. Plain index: attendance_devices is a handful
    /// of rows per tenant, so the build is instantaneous.</summary>
    [DbContext(typeof(ZayraDbContext))]
    [Migration("20261026124000_AttendanceDeviceCompanyIndex")]
    public partial class AttendanceDeviceCompanyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_attendance_devices_tenant_id_company_id",
                table: "attendance_devices",
                columns: new[] { "tenant_id", "company_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_attendance_devices_tenant_id_company_id", table: "attendance_devices");
        }
    }
}
