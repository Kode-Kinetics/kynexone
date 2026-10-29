using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class SoftDeletedOrgRowsFreeTheirCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_grades_tenant_id_company_id_code",
                table: "grades");

            migrationBuilder.DropIndex(
                name: "IX_designations_tenant_id_company_id_code",
                table: "designations");

            migrationBuilder.DropIndex(
                name: "IX_departments_tenant_id_company_id_code",
                table: "departments");

            migrationBuilder.DropIndex(
                name: "IX_cost_centers_tenant_id_company_id_code",
                table: "cost_centers");

            migrationBuilder.DropIndex(
                name: "IX_branches_tenant_id_company_id_code",
                table: "branches");

            migrationBuilder.CreateIndex(
                name: "IX_grades_tenant_id_company_id_code",
                table: "grades",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true,
                filter: "NOT is_deleted")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_designations_tenant_id_company_id_code",
                table: "designations",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true,
                filter: "NOT is_deleted")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_departments_tenant_id_company_id_code",
                table: "departments",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true,
                filter: "NOT is_deleted")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_cost_centers_tenant_id_company_id_code",
                table: "cost_centers",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true,
                filter: "NOT is_deleted")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_branches_tenant_id_company_id_code",
                table: "branches",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true,
                filter: "NOT is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Up lets a soft-deleted row and a live row share a code. The full unique index cannot be rebuilt over such a pair, so
            // every soft-deleted row whose code is also held by a live row (or by a soft-deleted row with a smaller id) is renamed
            // first: CODE~<first 8 of its id>. The row is kept; only its code changes, and only for rows nobody can see.
            foreach (var table in new[] { "branches", "departments", "designations", "grades", "cost_centers" })
            {
                migrationBuilder.Sql($@"UPDATE {table} t SET code = left(t.code, 31) || '~' || left(t.id::text, 8)
WHERE t.is_deleted AND EXISTS (SELECT 1 FROM {table} o WHERE o.tenant_id = t.tenant_id AND o.company_id IS NOT DISTINCT FROM t.company_id
  AND o.code = t.code AND o.id <> t.id AND (NOT o.is_deleted OR o.id < t.id));");
            }

            migrationBuilder.DropIndex(
                name: "IX_grades_tenant_id_company_id_code",
                table: "grades");

            migrationBuilder.DropIndex(
                name: "IX_designations_tenant_id_company_id_code",
                table: "designations");

            migrationBuilder.DropIndex(
                name: "IX_departments_tenant_id_company_id_code",
                table: "departments");

            migrationBuilder.DropIndex(
                name: "IX_cost_centers_tenant_id_company_id_code",
                table: "cost_centers");

            migrationBuilder.DropIndex(
                name: "IX_branches_tenant_id_company_id_code",
                table: "branches");

            migrationBuilder.CreateIndex(
                name: "IX_grades_tenant_id_company_id_code",
                table: "grades",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_designations_tenant_id_company_id_code",
                table: "designations",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_departments_tenant_id_company_id_code",
                table: "departments",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_cost_centers_tenant_id_company_id_code",
                table: "cost_centers",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_branches_tenant_id_company_id_code",
                table: "branches",
                columns: new[] { "tenant_id", "company_id", "code" },
                unique: true);
        }
    }
}
