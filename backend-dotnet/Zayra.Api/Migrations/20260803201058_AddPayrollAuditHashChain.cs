using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPayrollAuditHashChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "entry_hash",
                table: "payroll_audit_logs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "hash_algorithm",
                table: "payroll_audit_logs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // Legacy rows are re-sealed with this same value by PayrollAuditChainBackfill; the
                // default just keeps the column meaningful for any row observed pre-backfill.
                defaultValue: "SHA-256");

            migrationBuilder.AddColumn<string>(
                name: "previous_hash",
                table: "payroll_audit_logs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "seq",
                table: "payroll_audit_logs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_payroll_audit_logs_tenant_id_entry_hash",
                table: "payroll_audit_logs",
                columns: new[] { "tenant_id", "entry_hash" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_audit_logs_tenant_id_seq",
                table: "payroll_audit_logs",
                columns: new[] { "tenant_id", "seq" });

            // POD-A3 — DB-LEVEL append-only enforcement (defence-in-depth beyond the in-process
            // ChangeTracker guard, which cannot see set-based UPDATE/DELETE or raw SQL). A sealed
            // row (entry_hash <> '') is immutable and undeletable at the database. The ONLY mutation
            // permitted is the one-time backfill sealing a still-unsealed legacy row
            // (OLD.entry_hash = '' → NEW), which is exactly how PayrollAuditChainBackfill writes.
            // This is the evidentiary boundary an external auditor relies on; the app guard is only
            // the first line. (Postgres-only DDL — applied solely against the Npgsql provider; the
            // model-based test fixtures never run this migration.)
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION payroll_audit_logs_append_only() RETURNS trigger AS $$
BEGIN
    IF (TG_OP = 'DELETE') THEN
        RAISE EXCEPTION 'payroll_audit_logs is append-only: DELETE is not permitted (row id=%)', OLD.id
            USING ERRCODE = 'restrict_violation';
    END IF;
    -- UPDATE: permit only the backfill sealing an as-yet-unsealed legacy row.
    IF (OLD.entry_hash IS DISTINCT FROM '') THEN
        RAISE EXCEPTION 'payroll_audit_logs is append-only: sealed rows are immutable (row id=%)', OLD.id
            USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;");
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_payroll_audit_logs_append_only ON payroll_audit_logs;
CREATE TRIGGER trg_payroll_audit_logs_append_only
    BEFORE UPDATE OR DELETE ON payroll_audit_logs
    FOR EACH ROW EXECUTE FUNCTION payroll_audit_logs_append_only();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_payroll_audit_logs_append_only ON payroll_audit_logs;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS payroll_audit_logs_append_only();");

            migrationBuilder.DropIndex(
                name: "IX_payroll_audit_logs_tenant_id_entry_hash",
                table: "payroll_audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_payroll_audit_logs_tenant_id_seq",
                table: "payroll_audit_logs");

            migrationBuilder.DropColumn(
                name: "entry_hash",
                table: "payroll_audit_logs");

            migrationBuilder.DropColumn(
                name: "hash_algorithm",
                table: "payroll_audit_logs");

            migrationBuilder.DropColumn(
                name: "previous_hash",
                table: "payroll_audit_logs");

            migrationBuilder.DropColumn(
                name: "seq",
                table: "payroll_audit_logs");
        }
    }
}
