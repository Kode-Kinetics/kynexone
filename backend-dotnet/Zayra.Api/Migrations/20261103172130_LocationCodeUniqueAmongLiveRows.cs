using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <inheritdoc />
    public partial class LocationCodeUniqueAmongLiveRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_locations_tenant_id_code",
                table: "locations");

            migrationBuilder.CreateIndex(
                name: "IX_locations_tenant_id_code",
                table: "locations",
                columns: new[] { "tenant_id", "code" },
                unique: true,
                filter: "NOT is_deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The older index also counted deleted rows, so a deleted location that shares its code with another row (live or deleted) would
            // make the rollback fail. Each such deleted row gets a suffix from its own id (live rows keep their codes, deleted rows that clash
            // with nothing are left alone); no row is removed. Re-applying the migration does not undo the suffix, which is harmless on a deleted row.
            migrationBuilder.Sql(
                "UPDATE locations l SET code = l.code || '~' || left(replace(l.id::text, '-', ''), 8) " +
                "WHERE l.is_deleted AND EXISTS (SELECT 1 FROM locations o WHERE o.tenant_id = l.tenant_id AND o.code = l.code AND o.id <> l.id);");

            migrationBuilder.DropIndex(
                name: "IX_locations_tenant_id_code",
                table: "locations");

            migrationBuilder.CreateIndex(
                name: "IX_locations_tenant_id_code",
                table: "locations",
                columns: new[] { "tenant_id", "code" },
                unique: true);
        }
    }
}
