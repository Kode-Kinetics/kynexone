using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// The company (and optionally the department) a performance cycle appraises. Launch enrols only that company's
    /// active employees; a cycle without a company keeps enrolling the whole tenant, which is allowed only where the
    /// tenant has a single company. Additive, nullable and not backfilled: existing cycles keep their enrolments.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20261117130000_AddPerformanceCycleCompanyScope")]
    public partial class AddPerformanceCycleCompanyScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "performance_cycles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "department_id",
                table: "performance_cycles",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "company_id",
                table: "performance_cycles");

            migrationBuilder.DropColumn(
                name: "department_id",
                table: "performance_cycles");
        }
    }
}
