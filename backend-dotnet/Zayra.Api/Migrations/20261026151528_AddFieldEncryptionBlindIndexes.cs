using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Blind-index lookup indexes, built CONCURRENTLY (a plain CREATE INDEX blocks writes on employees for the whole build). That
    /// cannot run inside a transaction, so this migration holds nothing else and suppresses the migration transaction. IF NOT EXISTS
    /// makes a retry after a partial run safe; an interrupted build leaves an INVALID index that the DROP below clears.
    /// </summary>
    [DbContext(typeof(ZayraDbContext))]
    [Migration("20261026151528_AddFieldEncryptionBlindIndexes")]
    public partial class AddFieldEncryptionBlindIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_bank_iban;", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_employees_bidx_bank_iban ON employees (tenant_id, bank_iban_bidx) WHERE bank_iban_bidx IS NOT NULL;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_border_number;", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_employees_bidx_border_number ON employees (tenant_id, border_number_bidx) WHERE border_number_bidx IS NOT NULL;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_civil_id;", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_employees_bidx_civil_id ON employees (tenant_id, civil_id_bidx) WHERE civil_id_bidx IS NOT NULL;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_emirates_id;", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_employees_bidx_emirates_id ON employees (tenant_id, emirates_id_bidx) WHERE emirates_id_bidx IS NOT NULL;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_id_number;", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_employees_bidx_id_number ON employees (tenant_id, id_number_bidx) WHERE id_number_bidx IS NOT NULL;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_iqama_number;", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_employees_bidx_iqama_number ON employees (tenant_id, iqama_number_bidx) WHERE iqama_number_bidx IS NOT NULL;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_passport_number;", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_employees_bidx_passport_number ON employees (tenant_id, passport_number_bidx) WHERE passport_number_bidx IS NOT NULL;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_qid;", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_employees_bidx_qid ON employees (tenant_id, qid_bidx) WHERE qid_bidx IS NOT NULL;", suppressTransaction: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_bank_iban;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_border_number;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_civil_id;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_emirates_id;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_id_number;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_iqama_number;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_passport_number;", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_employees_bidx_qid;", suppressTransaction: true);
        }
    }
}
