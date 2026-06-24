using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddVoidTrackingToPayrollRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PayrollRun.cs added VoidReason/VoidedAt/VoidedByUserId/VoidedByName in commit 044dbba
            // but no migration was created. EF Core's SELECT includes all mapped properties →
            // PostgreSQL 42703 "column void_reason does not exist" → 500 on every runs query.
            // All four columns are nullable: populated only when Status == "Voided".
            //
            // Uses IF NOT EXISTS so the migration is idempotent — safe whether or not a prior
            // worktree session already added the columns manually.
            migrationBuilder.Sql(@"
                ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS void_reason            TEXT;
                ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS voided_at_utc         TIMESTAMPTZ;
                ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS voided_by_user_id     UUID;
                ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS voided_by_name        TEXT;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE payroll_runs DROP COLUMN IF EXISTS void_reason;
                ALTER TABLE payroll_runs DROP COLUMN IF EXISTS voided_at_utc;
                ALTER TABLE payroll_runs DROP COLUMN IF EXISTS voided_by_user_id;
                ALTER TABLE payroll_runs DROP COLUMN IF EXISTS voided_by_name;
            ");
        }
    }
}
