using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeDraftIdNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand-only: a draft's Saudi National ID (what Employee.IdNumber holds), stored encrypted like the draft's other identity
            // numbers (id_number_enc; no blind index, a draft is never looked up by value). ADD COLUMN takes an ACCESS EXCLUSIVE lock that
            // would queue every draft read behind it: fail fast (55P03) and re-run when quiet. The default is constant, so no rewrite.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.AddColumn<string>(
                name: "id_number",
                table: "employee_drafts",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "id_number_enc",
                table: "employee_drafts",
                type: "bytea",
                nullable: true);

            // The envelope-shape CHECK, added NOT VALID and then validated (VALIDATE takes only SHARE UPDATE EXCLUSIVE), the same way
            // the field-encryption foundation adds it to a populated table.
            migrationBuilder.Sql("ALTER TABLE employee_drafts ADD CONSTRAINT ck_employee_drafts_id_number_enc_shape CHECK (id_number_enc IS NULL OR (octet_length(id_number_enc) BETWEEN 30 AND 1024 AND get_byte(id_number_enc, 0) IN (1, 2) AND get_byte(id_number_enc, 1) > 0)) NOT VALID;");
            migrationBuilder.Sql("ALTER TABLE employee_drafts VALIDATE CONSTRAINT ck_employee_drafts_id_number_enc_shape;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_drafts_id_number_enc_shape",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "id_number",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "id_number_enc",
                table: "employee_drafts");
        }
    }
}
