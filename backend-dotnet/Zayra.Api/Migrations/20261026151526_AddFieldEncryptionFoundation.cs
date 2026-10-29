using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldEncryptionFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Bounded waits: fail fast instead of queueing behind a long transaction and blocking every query on the table.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '15s';");

            migrationBuilder.AddColumn<byte[]>(
                name: "emirates_id_number_bidx",
                table: "visa_records",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "emirates_id_number_enc",
                table: "visa_records",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "iqama_number_bidx",
                table: "visa_records",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "iqama_number_enc",
                table: "visa_records",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "iban_enc",
                table: "sif_file_records",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "mol_id_enc",
                table: "sif_file_records",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "iban_enc",
                table: "payroll_payment_records",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "passport_number_bidx",
                table: "passport_records",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "passport_number_enc",
                table: "passport_records",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "iban_enc",
                table: "loan_disbursement_lines",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "bank_iban_bidx",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "bank_iban_enc",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "border_number_bidx",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "border_number_enc",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "civil_id_bidx",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "civil_id_enc",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "emirates_id_bidx",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "emirates_id_enc",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "id_number_bidx",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "id_number_enc",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "iqama_number_bidx",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "iqama_number_enc",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "passport_number_bidx",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "passport_number_enc",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "qid_bidx",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "qid_enc",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "wps_bank_details_enc",
                table: "employees",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "account_number_bidx",
                table: "employee_payroll_profiles",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "account_number_enc",
                table: "employee_payroll_profiles",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "iban_bidx",
                table: "employee_payroll_profiles",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "iban_enc",
                table: "employee_payroll_profiles",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "mol_id_bidx",
                table: "employee_payroll_profiles",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "mol_id_enc",
                table: "employee_payroll_profiles",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "bank_iban_enc",
                table: "employee_drafts",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "border_number_enc",
                table: "employee_drafts",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "civil_id_enc",
                table: "employee_drafts",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "emirates_id_enc",
                table: "employee_drafts",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "iqama_number_enc",
                table: "employee_drafts",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "passport_number_enc",
                table: "employee_drafts",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "qid_enc",
                table: "employee_drafts",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "wps_bank_details_enc",
                table: "employee_drafts",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "national_id_enc",
                table: "employee_dependents",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "field_encryption_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    wrapped_dek = table.Column<byte[]>(type: "bytea", nullable: true),
                    wrapped_blind_index_root = table.Column<byte[]>(type: "bytea", nullable: true),
                    kek_version = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    retired_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recovery_exported_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    recovery_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    row_version = table.Column<int>(type: "integer", nullable: false),
                    canary_enc = table.Column<byte[]>(type: "bytea", nullable: true),
                    root_check = table.Column<byte[]>(type: "bytea", nullable: true),
                    active_at_revoke = table.Column<bool>(type: "boolean", nullable: false),
                    revoke_requested_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoke_effective_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoke_requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_encryption_keys", x => x.id);
                    table.CheckConstraint("ck_field_encryption_keys_material", "(status = 'Revoked' AND wrapped_dek IS NULL AND wrapped_blind_index_root IS NULL) OR (status <> 'Revoked' AND wrapped_dek IS NOT NULL AND wrapped_blind_index_root IS NOT NULL)");
                    table.CheckConstraint("ck_field_encryption_keys_status", "status IN ('Active','Retired','Revoked')");
                    table.CheckConstraint("ck_field_encryption_keys_version", "key_version BETWEEN 1 AND 250");
                    table.CheckConstraint("ck_field_encryption_keys_revoke_pending", "(revoke_requested_at_utc IS NULL) = (revoke_effective_at_utc IS NULL)");
                });

            migrationBuilder.Sql("ALTER TABLE visa_records ADD CONSTRAINT ck_visa_records_emirates_id_number_bidx_pair CHECK (emirates_id_number_bidx IS NULL OR (emirates_id_number_enc IS NOT NULL AND octet_length(emirates_id_number_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE visa_records ADD CONSTRAINT ck_visa_records_emirates_id_number_enc_shape CHECK (emirates_id_number_enc IS NULL OR (octet_length(emirates_id_number_enc) BETWEEN 30 AND 1024 AND get_byte(emirates_id_number_enc, 0) IN (1, 2) AND get_byte(emirates_id_number_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE visa_records ADD CONSTRAINT ck_visa_records_iqama_number_bidx_pair CHECK (iqama_number_bidx IS NULL OR (iqama_number_enc IS NOT NULL AND octet_length(iqama_number_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE visa_records ADD CONSTRAINT ck_visa_records_iqama_number_enc_shape CHECK (iqama_number_enc IS NULL OR (octet_length(iqama_number_enc) BETWEEN 30 AND 1024 AND get_byte(iqama_number_enc, 0) IN (1, 2) AND get_byte(iqama_number_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE sif_file_records ADD CONSTRAINT ck_sif_file_records_iban_enc_shape CHECK (iban_enc IS NULL OR (octet_length(iban_enc) BETWEEN 30 AND 1024 AND get_byte(iban_enc, 0) IN (1, 2) AND get_byte(iban_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE sif_file_records ADD CONSTRAINT ck_sif_file_records_mol_id_enc_shape CHECK (mol_id_enc IS NULL OR (octet_length(mol_id_enc) BETWEEN 30 AND 1024 AND get_byte(mol_id_enc, 0) IN (1, 2) AND get_byte(mol_id_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE payroll_payment_records ADD CONSTRAINT ck_payroll_payment_records_iban_enc_shape CHECK (iban_enc IS NULL OR (octet_length(iban_enc) BETWEEN 30 AND 1024 AND get_byte(iban_enc, 0) IN (1, 2) AND get_byte(iban_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE passport_records ADD CONSTRAINT ck_passport_records_passport_number_bidx_pair CHECK (passport_number_bidx IS NULL OR (passport_number_enc IS NOT NULL AND octet_length(passport_number_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE passport_records ADD CONSTRAINT ck_passport_records_passport_number_enc_shape CHECK (passport_number_enc IS NULL OR (octet_length(passport_number_enc) BETWEEN 30 AND 1024 AND get_byte(passport_number_enc, 0) IN (1, 2) AND get_byte(passport_number_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE loan_disbursement_lines ADD CONSTRAINT ck_loan_disbursement_lines_iban_enc_shape CHECK (iban_enc IS NULL OR (octet_length(iban_enc) BETWEEN 30 AND 1024 AND get_byte(iban_enc, 0) IN (1, 2) AND get_byte(iban_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_bank_iban_bidx_pair CHECK (bank_iban_bidx IS NULL OR (bank_iban_enc IS NOT NULL AND octet_length(bank_iban_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_bank_iban_enc_shape CHECK (bank_iban_enc IS NULL OR (octet_length(bank_iban_enc) BETWEEN 30 AND 1024 AND get_byte(bank_iban_enc, 0) IN (1, 2) AND get_byte(bank_iban_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_border_number_bidx_pair CHECK (border_number_bidx IS NULL OR (border_number_enc IS NOT NULL AND octet_length(border_number_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_border_number_enc_shape CHECK (border_number_enc IS NULL OR (octet_length(border_number_enc) BETWEEN 30 AND 1024 AND get_byte(border_number_enc, 0) IN (1, 2) AND get_byte(border_number_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_civil_id_bidx_pair CHECK (civil_id_bidx IS NULL OR (civil_id_enc IS NOT NULL AND octet_length(civil_id_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_civil_id_enc_shape CHECK (civil_id_enc IS NULL OR (octet_length(civil_id_enc) BETWEEN 30 AND 1024 AND get_byte(civil_id_enc, 0) IN (1, 2) AND get_byte(civil_id_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_emirates_id_bidx_pair CHECK (emirates_id_bidx IS NULL OR (emirates_id_enc IS NOT NULL AND octet_length(emirates_id_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_emirates_id_enc_shape CHECK (emirates_id_enc IS NULL OR (octet_length(emirates_id_enc) BETWEEN 30 AND 1024 AND get_byte(emirates_id_enc, 0) IN (1, 2) AND get_byte(emirates_id_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_id_number_bidx_pair CHECK (id_number_bidx IS NULL OR (id_number_enc IS NOT NULL AND octet_length(id_number_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_id_number_enc_shape CHECK (id_number_enc IS NULL OR (octet_length(id_number_enc) BETWEEN 30 AND 1024 AND get_byte(id_number_enc, 0) IN (1, 2) AND get_byte(id_number_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_iqama_number_bidx_pair CHECK (iqama_number_bidx IS NULL OR (iqama_number_enc IS NOT NULL AND octet_length(iqama_number_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_iqama_number_enc_shape CHECK (iqama_number_enc IS NULL OR (octet_length(iqama_number_enc) BETWEEN 30 AND 1024 AND get_byte(iqama_number_enc, 0) IN (1, 2) AND get_byte(iqama_number_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_passport_number_bidx_pair CHECK (passport_number_bidx IS NULL OR (passport_number_enc IS NOT NULL AND octet_length(passport_number_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_passport_number_enc_shape CHECK (passport_number_enc IS NULL OR (octet_length(passport_number_enc) BETWEEN 30 AND 1024 AND get_byte(passport_number_enc, 0) IN (1, 2) AND get_byte(passport_number_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_qid_bidx_pair CHECK (qid_bidx IS NULL OR (qid_enc IS NOT NULL AND octet_length(qid_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_qid_enc_shape CHECK (qid_enc IS NULL OR (octet_length(qid_enc) BETWEEN 30 AND 1024 AND get_byte(qid_enc, 0) IN (1, 2) AND get_byte(qid_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employees ADD CONSTRAINT ck_employees_wps_bank_details_enc_shape CHECK (wps_bank_details_enc IS NULL OR (octet_length(wps_bank_details_enc) BETWEEN 30 AND 1024 AND get_byte(wps_bank_details_enc, 0) IN (1, 2) AND get_byte(wps_bank_details_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_payroll_profiles ADD CONSTRAINT ck_employee_payroll_profiles_account_number_bidx_pair CHECK (account_number_bidx IS NULL OR (account_number_enc IS NOT NULL AND octet_length(account_number_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_payroll_profiles ADD CONSTRAINT ck_employee_payroll_profiles_account_number_enc_shape CHECK (account_number_enc IS NULL OR (octet_length(account_number_enc) BETWEEN 30 AND 1024 AND get_byte(account_number_enc, 0) IN (1, 2) AND get_byte(account_number_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_payroll_profiles ADD CONSTRAINT ck_employee_payroll_profiles_iban_bidx_pair CHECK (iban_bidx IS NULL OR (iban_enc IS NOT NULL AND octet_length(iban_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_payroll_profiles ADD CONSTRAINT ck_employee_payroll_profiles_iban_enc_shape CHECK (iban_enc IS NULL OR (octet_length(iban_enc) BETWEEN 30 AND 1024 AND get_byte(iban_enc, 0) IN (1, 2) AND get_byte(iban_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_payroll_profiles ADD CONSTRAINT ck_employee_payroll_profiles_mol_id_bidx_pair CHECK (mol_id_bidx IS NULL OR (mol_id_enc IS NOT NULL AND octet_length(mol_id_bidx) = 32)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_payroll_profiles ADD CONSTRAINT ck_employee_payroll_profiles_mol_id_enc_shape CHECK (mol_id_enc IS NULL OR (octet_length(mol_id_enc) BETWEEN 30 AND 1024 AND get_byte(mol_id_enc, 0) IN (1, 2) AND get_byte(mol_id_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_drafts ADD CONSTRAINT ck_employee_drafts_bank_iban_enc_shape CHECK (bank_iban_enc IS NULL OR (octet_length(bank_iban_enc) BETWEEN 30 AND 1024 AND get_byte(bank_iban_enc, 0) IN (1, 2) AND get_byte(bank_iban_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_drafts ADD CONSTRAINT ck_employee_drafts_border_number_enc_shape CHECK (border_number_enc IS NULL OR (octet_length(border_number_enc) BETWEEN 30 AND 1024 AND get_byte(border_number_enc, 0) IN (1, 2) AND get_byte(border_number_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_drafts ADD CONSTRAINT ck_employee_drafts_civil_id_enc_shape CHECK (civil_id_enc IS NULL OR (octet_length(civil_id_enc) BETWEEN 30 AND 1024 AND get_byte(civil_id_enc, 0) IN (1, 2) AND get_byte(civil_id_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_drafts ADD CONSTRAINT ck_employee_drafts_emirates_id_enc_shape CHECK (emirates_id_enc IS NULL OR (octet_length(emirates_id_enc) BETWEEN 30 AND 1024 AND get_byte(emirates_id_enc, 0) IN (1, 2) AND get_byte(emirates_id_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_drafts ADD CONSTRAINT ck_employee_drafts_iqama_number_enc_shape CHECK (iqama_number_enc IS NULL OR (octet_length(iqama_number_enc) BETWEEN 30 AND 1024 AND get_byte(iqama_number_enc, 0) IN (1, 2) AND get_byte(iqama_number_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_drafts ADD CONSTRAINT ck_employee_drafts_passport_number_enc_shape CHECK (passport_number_enc IS NULL OR (octet_length(passport_number_enc) BETWEEN 30 AND 1024 AND get_byte(passport_number_enc, 0) IN (1, 2) AND get_byte(passport_number_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_drafts ADD CONSTRAINT ck_employee_drafts_qid_enc_shape CHECK (qid_enc IS NULL OR (octet_length(qid_enc) BETWEEN 30 AND 1024 AND get_byte(qid_enc, 0) IN (1, 2) AND get_byte(qid_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_drafts ADD CONSTRAINT ck_employee_drafts_wps_bank_details_enc_shape CHECK (wps_bank_details_enc IS NULL OR (octet_length(wps_bank_details_enc) BETWEEN 30 AND 1024 AND get_byte(wps_bank_details_enc, 0) IN (1, 2) AND get_byte(wps_bank_details_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.Sql("ALTER TABLE employee_dependents ADD CONSTRAINT ck_employee_dependents_national_id_enc_shape CHECK (national_id_enc IS NULL OR (octet_length(national_id_enc) BETWEEN 30 AND 1024 AND get_byte(national_id_enc, 0) IN (1, 2) AND get_byte(national_id_enc, 1) > 0)) NOT VALID;");

            migrationBuilder.CreateIndex(
                name: "ux_field_encryption_keys_one_active",
                table: "field_encryption_keys",
                column: "tenant_id",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_field_encryption_keys_tenant_version",
                table: "field_encryption_keys",
                columns: new[] { "tenant_id", "key_version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "field_encryption_keys");

            migrationBuilder.DropCheckConstraint(
                name: "ck_visa_records_emirates_id_number_bidx_pair",
                table: "visa_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_visa_records_emirates_id_number_enc_shape",
                table: "visa_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_visa_records_iqama_number_bidx_pair",
                table: "visa_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_visa_records_iqama_number_enc_shape",
                table: "visa_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sif_file_records_iban_enc_shape",
                table: "sif_file_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sif_file_records_mol_id_enc_shape",
                table: "sif_file_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payroll_payment_records_iban_enc_shape",
                table: "payroll_payment_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_passport_records_passport_number_bidx_pair",
                table: "passport_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_passport_records_passport_number_enc_shape",
                table: "passport_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_disbursement_lines_iban_enc_shape",
                table: "loan_disbursement_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_bank_iban_bidx_pair",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_bank_iban_enc_shape",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_border_number_bidx_pair",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_border_number_enc_shape",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_civil_id_bidx_pair",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_civil_id_enc_shape",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_emirates_id_bidx_pair",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_emirates_id_enc_shape",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_id_number_bidx_pair",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_id_number_enc_shape",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_iqama_number_bidx_pair",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_iqama_number_enc_shape",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_passport_number_bidx_pair",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_passport_number_enc_shape",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_qid_bidx_pair",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_qid_enc_shape",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employees_wps_bank_details_enc_shape",
                table: "employees");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_payroll_profiles_account_number_bidx_pair",
                table: "employee_payroll_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_payroll_profiles_account_number_enc_shape",
                table: "employee_payroll_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_payroll_profiles_iban_bidx_pair",
                table: "employee_payroll_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_payroll_profiles_iban_enc_shape",
                table: "employee_payroll_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_payroll_profiles_mol_id_bidx_pair",
                table: "employee_payroll_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_payroll_profiles_mol_id_enc_shape",
                table: "employee_payroll_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_drafts_bank_iban_enc_shape",
                table: "employee_drafts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_drafts_border_number_enc_shape",
                table: "employee_drafts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_drafts_civil_id_enc_shape",
                table: "employee_drafts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_drafts_emirates_id_enc_shape",
                table: "employee_drafts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_drafts_iqama_number_enc_shape",
                table: "employee_drafts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_drafts_passport_number_enc_shape",
                table: "employee_drafts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_drafts_qid_enc_shape",
                table: "employee_drafts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_drafts_wps_bank_details_enc_shape",
                table: "employee_drafts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_employee_dependents_national_id_enc_shape",
                table: "employee_dependents");

            migrationBuilder.DropColumn(
                name: "emirates_id_number_bidx",
                table: "visa_records");

            migrationBuilder.DropColumn(
                name: "emirates_id_number_enc",
                table: "visa_records");

            migrationBuilder.DropColumn(
                name: "iqama_number_bidx",
                table: "visa_records");

            migrationBuilder.DropColumn(
                name: "iqama_number_enc",
                table: "visa_records");

            migrationBuilder.DropColumn(
                name: "iban_enc",
                table: "sif_file_records");

            migrationBuilder.DropColumn(
                name: "mol_id_enc",
                table: "sif_file_records");

            migrationBuilder.DropColumn(
                name: "iban_enc",
                table: "payroll_payment_records");

            migrationBuilder.DropColumn(
                name: "passport_number_bidx",
                table: "passport_records");

            migrationBuilder.DropColumn(
                name: "passport_number_enc",
                table: "passport_records");

            migrationBuilder.DropColumn(
                name: "iban_enc",
                table: "loan_disbursement_lines");

            migrationBuilder.DropColumn(
                name: "bank_iban_bidx",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "bank_iban_enc",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "border_number_bidx",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "border_number_enc",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "civil_id_bidx",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "civil_id_enc",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "emirates_id_bidx",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "emirates_id_enc",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "id_number_bidx",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "id_number_enc",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "iqama_number_bidx",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "iqama_number_enc",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "passport_number_bidx",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "passport_number_enc",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "qid_bidx",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "qid_enc",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "wps_bank_details_enc",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "account_number_bidx",
                table: "employee_payroll_profiles");

            migrationBuilder.DropColumn(
                name: "account_number_enc",
                table: "employee_payroll_profiles");

            migrationBuilder.DropColumn(
                name: "iban_bidx",
                table: "employee_payroll_profiles");

            migrationBuilder.DropColumn(
                name: "iban_enc",
                table: "employee_payroll_profiles");

            migrationBuilder.DropColumn(
                name: "mol_id_bidx",
                table: "employee_payroll_profiles");

            migrationBuilder.DropColumn(
                name: "mol_id_enc",
                table: "employee_payroll_profiles");

            migrationBuilder.DropColumn(
                name: "bank_iban_enc",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "border_number_enc",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "civil_id_enc",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "emirates_id_enc",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "iqama_number_enc",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "passport_number_enc",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "qid_enc",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "wps_bank_details_enc",
                table: "employee_drafts");

            migrationBuilder.DropColumn(
                name: "national_id_enc",
                table: "employee_dependents");
        }
    }
}
