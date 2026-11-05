using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class LoanBatchApproverMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand-only: two defaulted columns and one CHECK every existing row satisfies (mode SeparateApprover, cap 0), so
            // nothing changes for a company until an Admin opts in. The SQL is a frozen copy of LoanPolicySql.BatchApproverCheck:
            // never edit it here, write a new migration instead. ADD COLUMN / ADD CONSTRAINT take an ACCESS EXCLUSIVE lock that
            // would block loan reads behind it: fail fast (55P03) and re-run when quiet.
            migrationBuilder.Sql(LockTimeoutSql);

            migrationBuilder.AddColumn<string>(
                name: "batch_approver_mode",
                table: "loan_policies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "SeparateApprover");

            migrationBuilder.AddColumn<decimal>(
                name: "self_approval_max_amount",
                table: "loan_policies",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_policies__batch_approver",
                table: "loan_policies",
                sql: "batch_approver_mode IN ('SeparateApprover','SingleOperator') AND self_approval_max_amount >= 0 AND (batch_approver_mode = 'SingleOperator' OR self_approval_max_amount = 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_policies__batch_approver",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "batch_approver_mode",
                table: "loan_policies");

            migrationBuilder.DropColumn(
                name: "self_approval_max_amount",
                table: "loan_policies");
        }

        public const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s';";
    }
}
