using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingP4Acknowledgements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "auth_method",
                table: "employee_policy_acknowledgements",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "certificate_document_id",
                table: "employee_policy_acknowledgements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "chain_seq",
                table: "employee_policy_acknowledgements",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "consent_text_sha256",
                table: "employee_policy_acknowledgements",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "consent_version",
                table: "employee_policy_acknowledgements",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "device_id",
                table: "employee_policy_acknowledgements",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "document_sha256",
                table: "employee_policy_acknowledgements",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "document_version",
                table: "employee_policy_acknowledgements",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ip_address",
                table: "employee_policy_acknowledgements",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "employee_policy_acknowledgements",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "method",
                table: "employee_policy_acknowledgements",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "legacy_click");

            migrationBuilder.AddColumn<string>(
                name: "prev_hash",
                table: "employee_policy_acknowledgements",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "record_hash",
                table: "employee_policy_acknowledgements",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "step_up_at",
                table: "employee_policy_acknowledgements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "step_up_jti",
                table: "employee_policy_acknowledgements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "typed_name",
                table: "employee_policy_acknowledgements",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "user_agent",
                table: "employee_policy_acknowledgements",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "witness_user_id",
                table: "employee_policy_acknowledgements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "integrity_flagged_at",
                table: "employee_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "signature_requested_by",
                table: "employee_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "signature_required_at",
                table: "employee_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_employee_policy_acknowledgements_certificate_document_id",
                table: "employee_policy_acknowledgements",
                column: "certificate_document_id");

            migrationBuilder.CreateIndex(
                name: "ux_ack_chain",
                table: "employee_policy_acknowledgements",
                columns: new[] { "tenant_id", "chain_seq" },
                unique: true,
                filter: "method <> 'legacy_click'");

            migrationBuilder.CreateIndex(
                name: "ux_ack_stepup_jti",
                table: "employee_policy_acknowledgements",
                column: "step_up_jti",
                unique: true,
                filter: "step_up_jti IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_epa__esign_evidence",
                table: "employee_policy_acknowledgements",
                sql: "method = 'legacy_click' OR (document_sha256 IS NOT NULL AND typed_name IS NOT NULL AND ip_address IS NOT NULL AND record_hash IS NOT NULL AND chain_seq IS NOT NULL AND step_up_jti IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_epa__method",
                table: "employee_policy_acknowledgements",
                sql: "method IN ('legacy_click','esign_ess','esign_hr_device')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_epa__witness",
                table: "employee_policy_acknowledgements",
                sql: "(method = 'esign_hr_device') = (witness_user_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_empdoc_sign_pending",
                table: "employee_documents",
                columns: new[] { "tenant_id", "employee_id", "signature_required_at" },
                filter: "signature_required_at IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_epa__certificate_document",
                table: "employee_policy_acknowledgements",
                column: "certificate_document_id",
                principalTable: "employee_documents",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Append-only evidence: an update is always refused, a delete only inside the retention purge, and the table
            // cannot be emptied in one statement. EF cannot model triggers, so the SQL lives in AckAppendOnlySql and the
            // tests and this migration share one text.
            migrationBuilder.Sql(Zayra.Api.Data.Esign.AckAppendOnlySql.CreateFunction);
            migrationBuilder.Sql(Zayra.Api.Data.Esign.AckAppendOnlySql.CreateTrigger);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Zayra.Api.Data.Esign.AckAppendOnlySql.Drop);

            migrationBuilder.DropForeignKey(
                name: "fk_epa__certificate_document",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropIndex(
                name: "IX_employee_policy_acknowledgements_certificate_document_id",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropIndex(
                name: "ux_ack_chain",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropIndex(
                name: "ux_ack_stepup_jti",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_epa__esign_evidence",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_epa__method",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_epa__witness",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropIndex(
                name: "ix_empdoc_sign_pending",
                table: "employee_documents");

            migrationBuilder.DropColumn(
                name: "auth_method",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "certificate_document_id",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "chain_seq",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "consent_text_sha256",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "consent_version",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "document_sha256",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "document_version",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "ip_address",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "language",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "method",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "prev_hash",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "record_hash",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "step_up_at",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "step_up_jti",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "typed_name",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "user_agent",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "witness_user_id",
                table: "employee_policy_acknowledgements");

            migrationBuilder.DropColumn(
                name: "integrity_flagged_at",
                table: "employee_documents");

            migrationBuilder.DropColumn(
                name: "signature_requested_by",
                table: "employee_documents");

            migrationBuilder.DropColumn(
                name: "signature_required_at",
                table: "employee_documents");
        }
    }
}
