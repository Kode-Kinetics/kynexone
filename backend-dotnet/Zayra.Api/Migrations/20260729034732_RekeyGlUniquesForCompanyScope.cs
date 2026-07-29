using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Creates the real per-company UNIQUE indexes with PG15 NULLS NOT DISTINCT semantics.
    /// EFCore/Npgsql 8 cannot express NULLS NOT DISTINCT via the fluent model, so the model
    /// declares only NON-unique helper indexes (see ZayraDbContext) and true uniqueness is created
    /// here in raw SQL. Without NULLS NOT DISTINCT, PG treats each NULL company_id as distinct — two
    /// tenant-default rows (company_id IS NULL) with the same code/key would NOT collide, producing
    /// duplicate defaults and non-deterministic GL resolution. Neon is PG15+.
    /// </summary>
    public partial class RekeyGlUniquesForCompanyScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_gl_accounts_scope_code " +
                "ON gl_accounts (tenant_id, company_id, code) NULLS NOT DISTINCT;");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_gl_account_mappings_scope_driver " +
                "ON gl_account_mappings (tenant_id, company_id, driver_key) NULLS NOT DISTINCT;");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_gl_drivers_scope_key " +
                "ON gl_drivers (tenant_id, company_id, key) NULLS NOT DISTINCT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_gl_accounts_scope_code;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_gl_account_mappings_scope_driver;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_gl_drivers_scope_key;");
        }
    }
}
