using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPolicyDocumentPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                table: "policy_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content_sha256",
                table: "policy_documents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "effective_from_utc",
                table: "policy_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "effective_to_utc",
                table: "policy_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "publication_status",
                table: "policy_documents",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AddColumn<DateTime>(
                name: "published_at_utc",
                table: "policy_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "published_by_user_id",
                table: "policy_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_policy_documents_tenant_id_company_id",
                table: "policy_documents",
                columns: new[] { "tenant_id", "company_id" });

            migrationBuilder.CreateIndex(
                name: "IX_policy_documents_tenant_id_company_id_publication_status",
                table: "policy_documents",
                columns: new[] { "tenant_id", "company_id", "publication_status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Do not erase publication controls or source fingerprints already used by setup/audit.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM policy_documents
                        WHERE published_at_utc IS NOT NULL OR company_id IS NOT NULL
                           OR publication_status <> 'Draft' OR content_sha256 <> '') THEN
                        RAISE EXCEPTION 'Policy publication or source evidence exists. Preserve/export this history before an explicitly planned rollback.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropIndex(
                name: "IX_policy_documents_tenant_id_company_id",
                table: "policy_documents");

            migrationBuilder.DropIndex(
                name: "IX_policy_documents_tenant_id_company_id_publication_status",
                table: "policy_documents");

            migrationBuilder.DropColumn(
                name: "company_id",
                table: "policy_documents");

            migrationBuilder.DropColumn(
                name: "content_sha256",
                table: "policy_documents");

            migrationBuilder.DropColumn(
                name: "effective_from_utc",
                table: "policy_documents");

            migrationBuilder.DropColumn(
                name: "effective_to_utc",
                table: "policy_documents");

            migrationBuilder.DropColumn(
                name: "publication_status",
                table: "policy_documents");

            migrationBuilder.DropColumn(
                name: "published_at_utc",
                table: "policy_documents");

            migrationBuilder.DropColumn(
                name: "published_by_user_id",
                table: "policy_documents");
        }
    }
}
