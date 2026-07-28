using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Batch A of the Establishment Matrix programme (transfer-integrity prerequisite, Conflict R7):
    /// resolved-ID columns on employee_transfer_requests so the HR transfer apply path works off
    /// IDs and can never again manufacture string-only (uncountable) employees. Legacy free-text
    /// columns are retained for display/back-compat.
    ///
    /// NOTE ON SCOPE: this migration was scaffolded with `dotnet ef migrations add`; the scaffold
    /// surfaced ~55 drift operations because the model snapshot had not been regenerated since
    /// several hand-written SQL migrations (benefits, identity provider, payroll opening balances,
    /// approval-queue accountability, ...). Every drift object was audited and confirmed covered by
    /// an existing idempotent hand-written migration, so the Up/Down here is trimmed to ONLY the
    /// four new columns, while the regenerated Designer/ModelSnapshot deliberately absorbs the
    /// drift — healing the snapshot so future `dotnet ef migrations add` scaffolds cleanly.
    /// Guarded SQL keeps the migration idempotent (house convention).
    /// </summary>
    public partial class AddEmployeeTransferResolvedIds : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE employee_transfer_requests ADD COLUMN IF NOT EXISTS current_department_id uuid NULL;
                ALTER TABLE employee_transfer_requests ADD COLUMN IF NOT EXISTS new_department_id uuid NULL;
                ALTER TABLE employee_transfer_requests ADD COLUMN IF NOT EXISTS new_branch_id uuid NULL;
                ALTER TABLE employee_transfer_requests ADD COLUMN IF NOT EXISTS new_designation_id uuid NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE employee_transfer_requests DROP COLUMN IF EXISTS new_designation_id;
                ALTER TABLE employee_transfer_requests DROP COLUMN IF EXISTS new_branch_id;
                ALTER TABLE employee_transfer_requests DROP COLUMN IF EXISTS new_department_id;
                ALTER TABLE employee_transfer_requests DROP COLUMN IF EXISTS current_department_id;
                """);
        }
    }
}
