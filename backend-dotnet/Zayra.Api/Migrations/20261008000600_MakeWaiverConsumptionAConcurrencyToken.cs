using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Model-only: <c>attendance_evidence.waiver_consumed_at_utc</c> becomes an EF concurrency token (the database
    /// backstop for "one waiver, one punch": the UPDATE that uses a waiver carries <c>WHERE waiver_consumed_at_utc IS
    /// NULL</c>). A concurrency token changes the SQL EF generates, not the schema, so there is no DDL: this migration
    /// exists only so the model snapshot matches the model. Empty Up and Down: forward-safe, re-runnable, and rolling it
    /// back changes nothing in the database. Ordered after 20261008000500 (selfie evidence) and the peer
    /// 20261008000300 (welcome codes); it touches neither.
    /// </summary>
    public partial class MakeWaiverConsumptionAConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
