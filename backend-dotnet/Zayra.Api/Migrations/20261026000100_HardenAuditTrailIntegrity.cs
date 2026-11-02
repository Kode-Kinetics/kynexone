using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Audit-trail integrity, part 1 (transactional): append-only row triggers and a statement trigger that
    /// refuses table wipes on every audit table, the tenant-scoped erasure exemption, and a duplicate-fork
    /// PRECHECK that refuses to continue (nothing is modified) when the existing chain already holds forks.
    /// The unique fork-guard index is built by the next migration, concurrently. Raw SQL only;
    /// The SQL text lives in <c>AuditTrailGuardSql</c> (app code) because the production image compiles without Migrations/; these constants alias it.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20261026000100_HardenAuditTrailIntegrity")]
    public partial class HardenAuditTrailIntegrity : Migration
    {
        public const string GenericFunctionSql = AuditTrailGuardSql.GenericFunctionSql;

        public const string StatementGuardFunctionSql = AuditTrailGuardSql.StatementGuardFunctionSql;

        public const string PayrollFunctionSql = AuditTrailGuardSql.PayrollFunctionSql;

        public const string PayrollFunctionOriginalSql = AuditTrailGuardSql.PayrollFunctionOriginalSql;

        public const string CreateTriggersSql = AuditTrailGuardSql.CreateTriggersSql;

        public const string DropTriggersSql = AuditTrailGuardSql.DropTriggersSql;

        public const string ForkPrecheckSql = AuditTrailGuardSql.ForkPrecheckSql;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GenericFunctionSql);
            migrationBuilder.Sql(StatementGuardFunctionSql);
            migrationBuilder.Sql(CreateTriggersSql);
            migrationBuilder.Sql(PayrollFunctionSql);
            migrationBuilder.Sql(ForkPrecheckSql);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DropTriggersSql);
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_trail_no_wipe();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_trail_append_only();");
            migrationBuilder.Sql(PayrollFunctionOriginalSql);
        }
    }
}
