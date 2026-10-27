using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Audit-trail integrity, part 2 (NON-transactional): the unique fork guard on the central chain, built
    /// CONCURRENTLY so audit inserts are not blocked while the index builds. A failed concurrent build leaves
    /// an INVALID index; the first statement drops such a leftover so a re-run rebuilds it. If the index
    /// already exists and is valid (for example the operator built the explicit partial guard after
    /// accepting legacy forks) the build is a no-op.
    ///
    /// Deploy window: migrations run before the new build is approved. Until the new build is live, the OLD
    /// build can still fork on a race (its unlocked tail read); the unique index then rejects the second
    /// insert (23505) after its business change committed. Ship the migrations and the application as one
    /// gated step, or run this during a quiet period; the window is otherwise the old build's lifetime.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20261026000200_AddAuditChainForkGuardIndex")]
    public partial class AddAuditChainForkGuardIndex : Migration
    {
        public const string DropInvalidLeftoverSql = @"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_index i ON i.indexrelid = c.oid
               WHERE c.relname = 'ux_audit_logs_tenant_previous_hash' AND NOT i.indisvalid) THEN
        DROP INDEX ux_audit_logs_tenant_previous_hash;
    END IF;
END $$;";

        public const string CreateIndexSql = AuditTrailGuardSql.CreateForkGuardIndexSql;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DropInvalidLeftoverSql, suppressTransaction: true);
            migrationBuilder.Sql(CreateIndexSql, suppressTransaction: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_audit_logs_tenant_previous_hash;", suppressTransaction: true);
        }
    }
}
