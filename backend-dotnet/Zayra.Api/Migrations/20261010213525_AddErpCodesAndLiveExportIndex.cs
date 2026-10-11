using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// GL/ERP hand-off: ERP company codes (companies.erp_company_code), ERP account strings
    /// (gl_accounts.external_code), per-line company/account freezing, export kind + replacement link, frozen
    /// formatter options, per-journal ERP outcomes, supersede evidence, and the live-export guard.
    ///
    /// <para>LIVE-EXPORT GUARD. Two concurrent POST journal-exports for one scope both used to stay
    /// <c>Exported</c>. <see cref="CreateLiveScopeIndex"/> makes it one live export per tenant + company + period
    /// + run + format + currency. NULLS NOT DISTINCT (PG15+, Neon is 16) because a group export has company_id NULL
    /// and a period export has payroll_run_id NULL — with default NULL semantics the index would never fire for
    /// exactly the common cases. Npgsql 8 cannot express NULLS NOT DISTINCT, so it is raw SQL (precedent:
    /// RekeyGlUniquesForCompanyScope) and the test fixture applies the same constant. Existing duplicates are
    /// superseded first (newest stays live), or the index could not be created; each retired row records why.</para>
    ///
    /// <para>The key deliberately does NOT include include_unattributed: a group export of company X that also
    /// carries the unattributed rows and a company-X export both hold company X's ledger rows, and two live files
    /// of the same rows is the double post this index exists to stop. The Create endpoint instead tells a
    /// company-scoped caller precisely that a group-level export must be decided first.</para>
    ///
    /// <para>ERP COMPANY CODE. <c>companies.erp_company_code</c> is unique per tenant, case-insensitively, among
    /// live companies (<see cref="CreateErpCompanyCodeIndex"/>): two legal entities sharing one code would be
    /// merged into one ERP document. The column is new here, so no existing row can collide.</para>
    ///
    /// <para>Existing exports keep format_version 0 and regenerate through the frozen v1 formatters, so their
    /// recorded hash still verifies.</para>
    ///
    /// <para>ROLLBACK. Redeploy the previous image WITHOUT running Down: the schema is backward compatible (old
    /// inserts take the column defaults). Down refuses (<see cref="RefuseRollbackWithErpEvidence"/>) once any export
    /// holds per-journal ERP outcomes or was produced in format version 2, because dropping these columns would
    /// destroy the record of which journals the ERP posted and make those files impossible to regenerate.</para>
    /// </summary>
    public partial class AddErpCodesAndLiveExportIndex : Migration
    {
        public const string LiveScopeIndexName = Zayra.Api.Application.Finance.GlJournalExportConstraints.LiveScopeIndexName;
        public const string ErpCompanyCodeIndexName = Zayra.Api.Application.Finance.GlJournalExportConstraints.ErpCompanyCodeIndexName;

        /// <summary>Fail fast instead of queueing live traffic behind ALTER TABLE companies (a hot table) while a long
        /// transaction (a payroll lock) holds a lock on it; the deploy can simply be retried. Precedent:
        /// AddEmployeeWelcomeCodes.</summary>
        public const string LockTimeout = "SET LOCAL lock_timeout = '5s';";

        /// <summary>Older duplicate live exports of one scope become Superseded — exactly what the Create path
        /// would have done had the two requests not raced. Each retired row records, on itself, which export was kept
        /// and that both files may have reached the ERP: these duplicates exist only because two requests raced, so
        /// the client may have imported both, and finance needs that lead. (The central audit log is hash-chained by
        /// the application, so a migration cannot append to it without breaking the chain.)</summary>
        public const string SupersedeDuplicateLiveExports = @"
WITH ranked AS (
  SELECT id,
         first_value(id) OVER w AS kept_id,
         row_number() OVER w AS rn
  FROM gl_journal_exports WHERE status = 'Exported'
  WINDOW w AS (PARTITION BY tenant_id, company_id, period, payroll_run_id, format_key, currency
               ORDER BY exported_at_utc DESC, created_at_utc DESC, id DESC))
UPDATE gl_journal_exports e
SET status = 'Superseded',
    superseded_by_export_id = r.kept_id,
    supersede_reason = 'Retired by the upgrade: a duplicate file of the same month created at the same moment as export '
                       || r.kept_id::text || '. Check whether your ERP imported both files before exporting again.'
FROM ranked r
WHERE e.id = r.id AND r.rn > 1;";

        public const string CreateLiveScopeIndex =
            "CREATE UNIQUE INDEX IF NOT EXISTS " + LiveScopeIndexName + " ON gl_journal_exports " +
            "(tenant_id, company_id, period, payroll_run_id, format_key, currency) NULLS NOT DISTINCT " +
            "WHERE status = 'Exported';";

        /// <summary>Backs the uniqueness pre-check in OrganizationSetupService against a race. Expression index
        /// (lower()) so 'sa01' and 'SA01' collide; a soft-deleted company releases its code.</summary>
        public const string CreateErpCompanyCodeIndex =
            "CREATE UNIQUE INDEX IF NOT EXISTS " + ErpCompanyCodeIndexName + " ON companies " +
            "(tenant_id, lower(erp_company_code)) WHERE erp_company_code IS NOT NULL AND NOT is_deleted;";

        /// <summary>Down would drop the only record of which journals the ERP posted (the double-post guard reads it)
        /// and the frozen per-line codes v2 files regenerate from. Refuse instead of destroying evidence.</summary>
        public const string RefuseRollbackWithErpEvidence = @"
DO $$
DECLARE
  per_journal integer;
  v2 integer;
BEGIN
  SELECT count(*) INTO per_journal FROM gl_journal_exports
   WHERE confirmed_journal_count > 0 OR status = 'PartiallyConfirmed'
      OR journal_confirmations_json::text NOT IN ('[]', '');
  SELECT count(*) INTO v2 FROM gl_journal_exports WHERE format_version >= 2;
  IF per_journal > 0 THEN
    RAISE EXCEPTION 'AddErpCodesAndLiveExportIndex cannot be rolled back: % journal export(s) hold per-journal ERP outcomes that only these columns record. Roll back by redeploying the previous image without running this Down migration.', per_journal;
  END IF;
  IF v2 > 0 THEN
    RAISE EXCEPTION 'AddErpCodesAndLiveExportIndex cannot be rolled back: % journal export(s) were produced in format version 2 and could no longer be regenerated to their recorded hash. Roll back by redeploying the previous image without running this Down migration.', v2;
  END IF;
END $$;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockTimeout);

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
                name: "supersede_reason",
                table: "gl_journal_exports",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "superseded_by_export_id",
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
            migrationBuilder.Sql(CreateErpCompanyCodeIndex);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LockTimeout);
            migrationBuilder.Sql(RefuseRollbackWithErpEvidence);

            migrationBuilder.Sql("DROP INDEX IF EXISTS " + ErpCompanyCodeIndexName + ";");
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
                name: "supersede_reason",
                table: "gl_journal_exports");

            migrationBuilder.DropColumn(
                name: "superseded_by_export_id",
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
