using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveSeparationUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A bare CREATE UNIQUE INDEX aborts with 23505 if any tenant already holds two live
            // separations for one employee — and until this index existed there was no constraint and no
            // lock, so a double-clicked POST /api/offboarding could produce exactly that. A failed
            // migration is a blocked deploy (or, where RunMigrationsOnStartup is true, a host that will
            // not boot), so this cancels the older duplicates first, keeping the newest — the same row
            // the service's OrderByDescending(CreatedAtUtc) lookup would have treated as the live one.
            migrationBuilder.Sql(@"
                UPDATE employee_offboardings o
                   SET status = 'Cancelled',
                       reason = COALESCE(NULLIF(o.reason, ''), '') ||
                                CASE WHEN COALESCE(o.reason, '') = '' THEN '' ELSE ' | ' END ||
                                'Superseded: duplicate live separation cancelled when the one-live-separation ' ||
                                'constraint was introduced. The newest record for this employee remains live.',
                       updated_at_utc = NOW()
                 WHERE o.status NOT IN ('Cancelled', 'Completed')
                   AND EXISTS (
                       SELECT 1 FROM employee_offboardings n
                        WHERE n.tenant_id = o.tenant_id
                          AND n.employee_id = o.employee_id
                          AND n.status NOT IN ('Cancelled', 'Completed')
                          AND (n.created_at_utc > o.created_at_utc
                            OR (n.created_at_utc = o.created_at_utc AND n.id > o.id))
                   );
            ");

            migrationBuilder.CreateIndex(
                name: "ix_employee_offboardings_live_per_employee",
                table: "employee_offboardings",
                columns: new[] { "tenant_id", "employee_id" },
                unique: true,
                filter: "status NOT IN ('Cancelled', 'Completed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_employee_offboardings_live_per_employee",
                table: "employee_offboardings");
        }
    }
}
