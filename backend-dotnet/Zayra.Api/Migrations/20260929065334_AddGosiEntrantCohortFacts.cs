using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// F02 — the Saudi GOSI entrant cohort becomes a person-level fact, and every payslip records the
    /// cohort and rate basis its statutory lines were computed on.
    ///
    /// <para><c>employees.gosi_first_registered_on</c> (date, NULL): the date GOSI first registered the
    /// person, recorded through the approval-gated change path. Same name and type as the V2 baseline
    /// column (Db/baseline/013_employees.sql). NULL = unknown.</para>
    ///
    /// <para><c>payroll_slips.gosi_cohort</c> (varchar(40), NULL) and <c>payroll_slips.statutory_basis</c>
    /// (varchar(1000), NULL): the calculation explanation, frozen at Process.</para>
    ///
    /// <para><b>No backfill, by design.</b> Every existing employee starts UNKNOWN — the cohort is never
    /// inferred — and every existing slip keeps NULL, which validation and GOSI reconciliation read as
    /// "computed without a cohort", i.e. exactly how it was computed. Purely additive and nullable, so the
    /// running code before this deploy is unaffected by the columns existing, and Down() drops them.</para>
    /// </summary>
    public partial class AddGosiEntrantCohortFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "gosi_cohort",
                table: "payroll_slips",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "statutory_basis",
                table: "payroll_slips",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "gosi_first_registered_on",
                table: "employees",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "gosi_cohort",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "statutory_basis",
                table: "payroll_slips");

            migrationBuilder.DropColumn(
                name: "gosi_first_registered_on",
                table: "employees");
        }
    }
}
