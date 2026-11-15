using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSalaryAdvancePayoutFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cancel_reason",
                table: "salary_advances",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "cancelled_at_utc",
                table: "salary_advances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "paid_at_utc",
                table: "salary_advances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "paid_by_user_id",
                table: "salary_advances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_reference",
                table: "salary_advances",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // One bank or cash reference evidences one payout in a company. The columns are new and nothing has been written to
            // payment_reference, so both indexes are created over empty values. NULL is distinct in a unique index, so the legacy
            // advance that has no company gets its own index (the payout's check treats "no company" as one bucket per tenant).
            migrationBuilder.CreateIndex(
                name: "ux_salary_advances_payment_reference",
                table: "salary_advances",
                columns: new[] { "tenant_id", "company_id", "payment_reference" },
                unique: true,
                filter: "payment_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_salary_advances_payment_reference_unattributed",
                table: "salary_advances",
                columns: new[] { "tenant_id", "payment_reference" },
                unique: true,
                filter: "company_id IS NULL AND payment_reference IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_salary_advances_payment_reference_unattributed",
                table: "salary_advances");

            migrationBuilder.DropIndex(
                name: "ux_salary_advances_payment_reference",
                table: "salary_advances");

            migrationBuilder.DropColumn(
                name: "cancel_reason",
                table: "salary_advances");

            migrationBuilder.DropColumn(
                name: "cancelled_at_utc",
                table: "salary_advances");

            migrationBuilder.DropColumn(
                name: "paid_at_utc",
                table: "salary_advances");

            migrationBuilder.DropColumn(
                name: "paid_by_user_id",
                table: "salary_advances");

            migrationBuilder.DropColumn(
                name: "payment_reference",
                table: "salary_advances");
        }
    }
}
