using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// The terms Finance last confirmed for a monthly cash allowance (ALW_*), kept on the row while a change to it waits for Finance again, so payroll
    /// keeps paying the confirmed terms instead of refusing the whole company's run. Additive and nullable. No backfill: a snapshot is written only when a
    /// confirmed allowance is changed (edit or Finance correction), and cleared when the change is confirmed; until then every row reads as today.
    /// </summary>
    [DbContext(typeof(ZayraDbContext))]
    [Migration("20261116231000_AddPayComponentLastConfirmedTerms")]
    public partial class AddPayComponentLastConfirmedTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_confirmed_terms",
                table: "pay_components",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_confirmed_terms",
                table: "pay_components");
        }
    }
}
