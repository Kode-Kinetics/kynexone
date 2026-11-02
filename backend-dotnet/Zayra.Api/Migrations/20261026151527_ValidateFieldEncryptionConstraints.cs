using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Second half of the field-encryption envelope CHECKs: the previous migration added them NOT VALID (no table scan under an
    /// ACCESS EXCLUSIVE lock); VALIDATE only takes SHARE UPDATE EXCLUSIVE, so reads and writes continue while the existing rows are checked.
    /// </summary>
    [DbContext(typeof(ZayraDbContext))]
    [Migration("20261026151527_ValidateFieldEncryptionConstraints")]
    public partial class ValidateFieldEncryptionConstraints : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '15s';");
            migrationBuilder.Sql("ALTER TABLE visa_records VALIDATE CONSTRAINT ck_visa_records_emirates_id_number_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE visa_records VALIDATE CONSTRAINT ck_visa_records_emirates_id_number_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE visa_records VALIDATE CONSTRAINT ck_visa_records_iqama_number_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE visa_records VALIDATE CONSTRAINT ck_visa_records_iqama_number_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE sif_file_records VALIDATE CONSTRAINT ck_sif_file_records_iban_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE sif_file_records VALIDATE CONSTRAINT ck_sif_file_records_mol_id_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE payroll_payment_records VALIDATE CONSTRAINT ck_payroll_payment_records_iban_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE passport_records VALIDATE CONSTRAINT ck_passport_records_passport_number_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE passport_records VALIDATE CONSTRAINT ck_passport_records_passport_number_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE loan_disbursement_lines VALIDATE CONSTRAINT ck_loan_disbursement_lines_iban_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_bank_iban_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_bank_iban_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_border_number_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_border_number_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_civil_id_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_civil_id_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_emirates_id_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_emirates_id_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_id_number_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_id_number_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_iqama_number_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_iqama_number_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_passport_number_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_passport_number_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_qid_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_qid_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employees VALIDATE CONSTRAINT ck_employees_wps_bank_details_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employee_payroll_profiles VALIDATE CONSTRAINT ck_employee_payroll_profiles_account_number_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE employee_payroll_profiles VALIDATE CONSTRAINT ck_employee_payroll_profiles_account_number_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employee_payroll_profiles VALIDATE CONSTRAINT ck_employee_payroll_profiles_iban_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE employee_payroll_profiles VALIDATE CONSTRAINT ck_employee_payroll_profiles_iban_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employee_payroll_profiles VALIDATE CONSTRAINT ck_employee_payroll_profiles_mol_id_bidx_pair;");
            migrationBuilder.Sql("ALTER TABLE employee_payroll_profiles VALIDATE CONSTRAINT ck_employee_payroll_profiles_mol_id_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employee_drafts VALIDATE CONSTRAINT ck_employee_drafts_bank_iban_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employee_drafts VALIDATE CONSTRAINT ck_employee_drafts_border_number_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employee_drafts VALIDATE CONSTRAINT ck_employee_drafts_civil_id_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employee_drafts VALIDATE CONSTRAINT ck_employee_drafts_emirates_id_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employee_drafts VALIDATE CONSTRAINT ck_employee_drafts_iqama_number_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employee_drafts VALIDATE CONSTRAINT ck_employee_drafts_passport_number_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employee_drafts VALIDATE CONSTRAINT ck_employee_drafts_qid_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employee_drafts VALIDATE CONSTRAINT ck_employee_drafts_wps_bank_details_enc_shape;");
            migrationBuilder.Sql("ALTER TABLE employee_dependents VALIDATE CONSTRAINT ck_employee_dependents_national_id_enc_shape;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Validation is not undone: a validated constraint is simply a constraint.
        }
    }
}
