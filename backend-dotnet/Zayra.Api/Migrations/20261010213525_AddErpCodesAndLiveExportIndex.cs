using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// GL/ERP hand-off: ERP company codes (companies.erp_company_code), ERP account strings
    /// (gl_accounts.external_code), per-line company/account freezing, export kind + replacement link, frozen
    /// formatter options, per-journal ERP outcomes, and the live-export guard.
    ///
    /// <para>LIVE-EXPORT GUARD. Two concurrent POST journal-exports for one scope both used to stay
    /// <c>Exported</c>. <see cref="CreateLiveScopeIndex"/> makes it one live export per tenant + company + period
    /// + run + format + currency. NULLS NOT DISTINCT (PG15+, Neon is 16) because a group export has company_id NULL
    /// and a period export has payroll_run_id NULL — with default NULL semantics the index would never fire for
    /// exactly the common cases. Npgsql 8 cannot express NULLS NOT DISTINCT, so it is raw SQL (precedent:
    /// RekeyGlUniquesForCompanyScope) and the test fixture applies the same constant. Existing duplicates are
    /// superseded first (newest stays live), or the index could not be created.</para>
    ///
    /// <para>Existing exports keep format_version 0 and regenerate through the frozen v1 formatters, so their
    /// recorded hash still verifies. Rollback: Down drops the index and columns; v1 code ignores them.</para>
    /// </summary>
    public partial class AddErpCodesAndLiveExportIndex : Migration
    {
        public const string LiveScopeIndexName = "ux_gl_journal_exports_live_scope";

        /// <summary>Older duplicate live exports of one scope become Superseded — exactly what the Create path
        /// would have done had the two requests not raced.</summary>
        public const string SupersedeDuplicateLiveExports = @"
UPDATE gl_journal_exports SET status = 'Superseded'
WHERE id IN (
  SELECT id FROM (
    SELECT id, row_number() OVER (
      PARTITION BY tenant_id, company_id, period, payroll_run_id, format_key, currency
      ORDER BY exported_at_utc DESC, created_at_utc DESC, id DESC) AS rn
    FROM gl_journal_exports WHERE status = 'Exported') ranked
  WHERE rn > 1);";

        public const string CreateLiveScopeIndex =
            "CREATE UNIQUE INDEX IF NOT EXISTS " + LiveScopeIndexName + " ON gl_journal_exports " +
            "(tenant_id, company_id, period, payroll_run_id, format_key, currency) NULLS NOT DISTINCT " +
            "WHERE status = 'Exported';";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "confirmed_journal_count",
                table: "gl_journal_exports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "format_options_json",
                table: "gl_journal_exports",
                type: "json",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<int>(
                name: "format_version",
                table: "gl_journal_exports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "journal_confirmations_json",
                table: "gl_journal_exports",
                type: "json",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "gl_journal_exports",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Full");

            migrationBuilder.AddColumn<Guid>(
                name: "replaces_export_id",
                table: "gl_journal_exports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "company_code",
                table: "gl_journal_export_lines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "external_account_code",
                table: "gl_journal_export_lines",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_company_id",
                table: "gl_journal_export_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_code",
                table: "gl_accounts",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erp_company_code",
                table: "companies",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_gl_journal_exports_tenant_id_confirmed_journal_count",
                table: "gl_journal_exports",
                columns: new[] { "tenant_id", "confirmed_journal_count" });

            migrationBuilder.Sql(SupersedeDuplicateLiveExports);
            migrationBuilder.Sql(CreateLiveScopeIndex);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS " + LiveScopeIndexName + ";");

            migrationBuilder.DropIndex(
                name: "IX_gl_journal_exports_tenant_id_confirmed_journal_count",
                table: "gl_journal_exports");

            migrationBuilder.DropColumn(
                name: "confirmed_journal_count",
                table: "gl_journal_exports");

            migrationBuilder.DropColumn(
                name: "format_options_json",
                table: "gl_journal_exports");

            migrationBuilder.DropColumn(
                name: "format_version",
                table: "gl_journal_exports");

            migrationBuilder.DropColumn(
                name: "journal_confirmations_json",
                table: "gl_journal_exports");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "gl_journal_exports");

            migrationBuilder.DropColumn(
                name: "replaces_export_id",
                table: "gl_journal_exports");

            migrationBuilder.DropColumn(
                name: "company_code",
                table: "gl_journal_export_lines");

            migrationBuilder.DropColumn(
                name: "external_account_code",
                table: "gl_journal_export_lines");

            migrationBuilder.DropColumn(
                name: "source_company_id",
                table: "gl_journal_export_lines");

            migrationBuilder.DropColumn(
                name: "external_code",
                table: "gl_accounts");

            migrationBuilder.DropColumn(
                name: "erp_company_code",
                table: "companies");
        }
    }
}
