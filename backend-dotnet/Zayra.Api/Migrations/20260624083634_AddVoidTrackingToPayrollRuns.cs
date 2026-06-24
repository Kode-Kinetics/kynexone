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
            // PayrollRun.cs added VoidReason/VoidedAtUtc/VoidedByUserId/VoidedByName but the
            // original migration that shipped them (20260624000006) had no Designer file, so it
            // carried no [Migration] attribute and EF never registered or applied it. The entity
            // model still maps the four columns, so every SELECT/INSERT on payroll_runs emitted
            // SQL referencing them → PostgreSQL 42703 "column void_reason does not exist" → 500 on
            // every payroll runs query AND on New Payroll Run. This migration is the properly
            // scaffolded replacement (Designer present, [Migration] attribute applied).
            //
            // Raw idempotent SQL (IF NOT EXISTS) so it is safe whether or not a prior worktree
            // session already added the columns manually on a given tenant database.
            //
            // NOTE: the scaffold also surfaced status/length, default-value, and a granted_by
            // rename as "pending" because earlier migrations left the model snapshot out of sync.
            // Those changes were already applied by 20260622000818 / 20260623080000 /
            // 20260624000002 / 20260624000003 / 20260624000005, so they are intentionally NOT
            // repeated here (re-running the granted_by rename would fail). Regenerating this
            // migration refreshed ZayraDbContextModelSnapshot.cs, which realigns the snapshot.
            migrationBuilder.Sql(@"
                ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS void_reason        TEXT;
                ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS voided_at_utc       TIMESTAMPTZ;
                ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS voided_by_user_id   UUID;
                ALTER TABLE payroll_runs ADD COLUMN IF NOT EXISTS voided_by_name      TEXT;
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
