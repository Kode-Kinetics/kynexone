using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGlPeriodClose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gl_period_closes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    closed_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reopened_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reopened_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reopened_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reopen_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gl_period_closes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gl_period_closes_tenant_id_company_id",
                table: "gl_period_closes",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gl_period_closes_tenant_id_company_id_period",
                table: "gl_period_closes",
                columns: new[] { "tenant_id", "company_id", "period" });

            // Real per-scope UNIQUE with PG15 NULLS NOT DISTINCT semantics (Npgsql 8 cannot express it
            // fluently — same idiom as ux_pay_components_scope_code_type / ux_gl_drivers_scope_key).
            // Without NULLS NOT DISTINCT two group-wide closes (company_id IS NULL) for the same period
            // would NOT collide, allowing duplicate close rows and non-deterministic guard reads. Neon is
            // PG15+. This guarantees at most one close row per (tenant, company-or-group, period).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_gl_period_closes_scope_period " +
                "ON gl_period_closes (tenant_id, company_id, period) NULLS NOT DISTINCT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_gl_period_closes_scope_period;");
            migrationBuilder.DropTable(
                name: "gl_period_closes");
        }
    }
}
