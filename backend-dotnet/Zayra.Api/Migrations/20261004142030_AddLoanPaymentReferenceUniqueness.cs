using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLoanPaymentReferenceUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_loan_disbursement_batches_tenant_id_company_id_payment_refe~",
                table: "loan_disbursement_batches",
                columns: new[] { "tenant_id", "company_id", "payment_reference" },
                unique: true,
                filter: "payment_reference IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_loan_disbursement_batches_tenant_id_company_id_payment_refe~",
                table: "loan_disbursement_batches");
        }
    }
}
