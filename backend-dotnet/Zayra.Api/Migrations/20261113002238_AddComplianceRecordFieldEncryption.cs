using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddComplianceRecordFieldEncryption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive only. Bounded wait: fail fast instead of queueing behind a long transaction and blocking every query on the table.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '15s';");

            migrationBuilder.AddColumn<byte[]>(
                name: "field_value_enc",
                table: "employee_compliance_records",
                type: "bytea",
                nullable: true);

            // The envelope-shape CHECK is added NOT VALID and then validated. Inside this migration's transaction the validation does not
            // shorten the exclusive lock the ALTER TABLE above already holds until commit, so it saves nothing on lock time. It is harmless
            // here because the column is new and every value is null, so the validation has no rows to check.
            migrationBuilder.Sql("ALTER TABLE employee_compliance_records ADD CONSTRAINT ck_employee_compliance_records_field_value_enc_shape CHECK (field_value_enc IS NULL OR (octet_length(field_value_enc) BETWEEN 30 AND 1024 AND get_byte(field_value_enc, 0) IN (1, 2) AND get_byte(field_value_enc, 1) > 0)) NOT VALID;");
            migrationBuilder.Sql("ALTER TABLE employee_compliance_records VALIDATE CONSTRAINT ck_employee_compliance_records_field_value_enc_shape;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_compliance_records_field_value_enc_shape",
                table: "employee_compliance_records");

            migrationBuilder.DropColumn(
                name: "field_value_enc",
                table: "employee_compliance_records");
        }
    }
}
