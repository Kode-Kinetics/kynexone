using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class LoanMinInstallments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand-only: two defaulted columns and two CHECKs every existing row satisfies with the default 1 — the CHECK
            // only ties a minimum to max_installments once one is set (> 1), so a legacy max_installments of 0 (the column's
            // database default on loan_policies) cannot fail this migration. The SQL is a frozen copy of
            // LoanTypeSql.MinInstallmentsCheck: never edit it here, write a new migration instead.
            // ADD COLUMN / ADD CONSTRAINT take an ACCESS EXCLUSIVE lock that would block loan reads behind it: fail fast
            // instead (55P03) and re-run when quiet.
            migrationBuilder.Sql(LockTimeoutSql);

            migrationBuilder.AddColumn<int>(
                name: "min_installments",
                table: "loan_types",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "min_installments",
                table: "loan_policies",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_types__min_installments",
                table: "loan_types",
                sql: "min_installments >= 1 AND (min_installments = 1 OR min_installments <= max_installments)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_policies__min_installments",
                table: "loan_policies",
                sql: "min_installments >= 1 AND (min_installments = 1 OR min_installments <= max_installments)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_types__min_installments",
                table: "loan_types");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_policies__min_installments",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "min_installments",
                table: "loan_types");

            migrationBuilder.DropColumn(
                name: "min_installments",
                table: "loan_policies");
        }

        /// <summary>Transaction-scoped (EF runs each migration in one transaction), as in AddMfaTrustedDevices.</summary>
        public const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s';";
    }
}
