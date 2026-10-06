using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseAR2DependantsSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at_utc",
                table: "employee_dependents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "deleted_by",
                table: "employee_dependents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_deleted",
                table: "employee_dependents",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back would turn every soft-removed dependant back into a covered one (and silently change medical,
            // ticket and education coverage). Refuse while any exists: restore or purge them deliberately first (runbook).
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM employee_dependents WHERE is_deleted) THEN
                        RAISE EXCEPTION 'R2_DEPENDANTS_SOFT_DELETED: % removed dependant(s) exist; rolling back would make them covered again. See DEPLOY_ROLLBACK_RUNBOOK.md.',
                            (SELECT count(*) FROM employee_dependents WHERE is_deleted)
                            USING ERRCODE = '55000';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropColumn(
                name: "deleted_at_utc",
                table: "employee_dependents");

            migrationBuilder.DropColumn(
                name: "deleted_by",
                table: "employee_dependents");

            migrationBuilder.DropColumn(
                name: "is_deleted",
                table: "employee_dependents");
        }
    }
}
