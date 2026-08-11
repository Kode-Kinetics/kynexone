using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// POD-D4 — month-end hand-off. THREE NEW TABLES, CREATE-ONLY: zero ALTER, zero column added to any
    /// existing table, zero data backfill. Safe to apply to all ~55 live tenants with no downtime and
    /// nothing to migrate; every existing row and query is untouched.
    ///
    /// <para><b>gl_journal_exports / gl_journal_export_lines</b> — the journal artifact a client's ERP can
    /// actually ingest, plus its FROZEN line set. Nothing previously produced a journal file at all, and
    /// PayrollRun.ErpPostingStatus could be driven to "Posted" off a free-text string with no evidence.
    /// The lines table exists because a period is NOT a frozen row set (a void's contra is dated into the
    /// ORIGINAL period; loans and advances post continuously), so a download must regenerate from what was
    /// emitted, never from a re-filter — otherwise the file already handed to the ERP becomes
    /// un-reproducible exactly when an audit asks for it.</para>
    ///
    /// <para><b>bank_payment_confirmations</b> — one append-only row per (import, payment record). Required
    /// because PayrollPaymentRecord has eight columns and nowhere to put a bank reference, a return reason
    /// code or a value date, and adding columns to it would mean an ALTER on a live table plus a structural
    /// edit to a concurrently-owned model. Marking a record Paid/Returned needs no schema change
    /// (Status is untyped text).</para>
    /// </summary>
    public partial class AddGlJournalExportAndBankConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gl_journal_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    company_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    payroll_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    format_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    file_hash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    entry_count = table.Column<int>(type: "integer", nullable: false),
                    total_debits = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    total_credits = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    include_unattributed = table.Column<bool>(type: "boolean", nullable: false),
                    suppress_reversed_pairs = table.Column<bool>(type: "boolean", nullable: false),
                    filter_json = table.Column<string>(type: "json", nullable: false),
                    exported_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    exported_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    exported_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    erp_document_number = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    erp_posted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    confirmed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    confirmed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gl_journal_exports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gl_journal_export_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gl_journal_export_id = table.Column<Guid>(type: "uuid", nullable: false),
                    finance_gl_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    side = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    account_code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    account_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    accounting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    system_posted_date = table.Column<DateOnly>(type: "date", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    journal_ref = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    source_module = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_entity_ref = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    event_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    reversal_of_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_reversed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gl_journal_export_lines", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bank_payment_confirmations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    raw_outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    previous_status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    applied = table.Column<bool>(type: "boolean", nullable: false),
                    hold_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    confirmed_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    record_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    amount_mismatch = table.Column<bool>(type: "boolean", nullable: false),
                    bank_reference = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reason_text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    value_date = table.Column<DateOnly>(type: "date", nullable: true),
                    matched_by = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    import_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    source_file_hash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    parser_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    imported_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    imported_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    imported_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_payment_confirmations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gl_journal_exports_tenant_id_period",
                table: "gl_journal_exports",
                columns: new[] { "tenant_id", "period" });

            migrationBuilder.CreateIndex(
                name: "IX_gl_journal_exports_tenant_id_company_id_period",
                table: "gl_journal_exports",
                columns: new[] { "tenant_id", "company_id", "period" });

            migrationBuilder.CreateIndex(
                name: "IX_gl_journal_exports_tenant_id_payroll_run_id",
                table: "gl_journal_exports",
                columns: new[] { "tenant_id", "payroll_run_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gl_journal_exports_tenant_id_status",
                table: "gl_journal_exports",
                columns: new[] { "tenant_id", "status" });

            // The frozen set: a download regenerates from these rows in line_no order.
            migrationBuilder.CreateIndex(
                name: "IX_gl_journal_export_lines_tenant_id_gl_journal_export_id_line~",
                table: "gl_journal_export_lines",
                columns: new[] { "tenant_id", "gl_journal_export_id", "line_no" });

            // "which export(s) covered this ledger row?" — drives the ERP confirmation stamp and the
            // reconciliation view's exported-vs-posted coverage.
            migrationBuilder.CreateIndex(
                name: "IX_gl_journal_export_lines_tenant_id_finance_gl_entry_id",
                table: "gl_journal_export_lines",
                columns: new[] { "tenant_id", "finance_gl_entry_id" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_payment_confirmations_tenant_id_payment_batch_id_payme~",
                table: "bank_payment_confirmations",
                columns: new[] { "tenant_id", "payment_batch_id", "payment_record_id" });

            // Tenant-wide duplicate-file probe: one bank file must not be silently applied to several
            // batches. Deliberately NOT unique — a genuinely multi-batch file may be re-applied with an
            // explicit acknowledgement, and every application appends its own append-only rows.
            migrationBuilder.CreateIndex(
                name: "IX_bank_payment_confirmations_tenant_id_source_file_hash",
                table: "bank_payment_confirmations",
                columns: new[] { "tenant_id", "source_file_hash" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_payment_confirmations_tenant_id_import_batch_id",
                table: "bank_payment_confirmations",
                columns: new[] { "tenant_id", "import_batch_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "bank_payment_confirmations");
            migrationBuilder.DropTable(name: "gl_journal_export_lines");
            migrationBuilder.DropTable(name: "gl_journal_exports");
        }
    }
}
