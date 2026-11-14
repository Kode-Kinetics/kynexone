using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Data-only. Launch item L1.2: account 5008 carries late/early AND loss-of-pay deductions (source Attendance) and 5009 carries approved
    /// unpaid leave (source Leave), but the seeded names said "Attendance &amp; Late" and "Unpaid Leave &amp; LOP". The names now say what
    /// the routing does; the routing, the codes, the account types and every balance are untouched.
    ///
    /// <para><b>Only seeded rows still holding the seeded name.</b> A gl_accounts row is renamed only when its code, its Expense type AND
    /// its exact seeded name all match, so a tenant's own wording (or an account that merely reuses the code) is never overwritten; the
    /// same predicate applies to the system driver's default_name. finance_gl_entries is never touched: journal lines store the account
    /// label text they were posted under, and posted history stays exactly as posted.</para>
    ///
    /// <para>Down restores the old seeded names with the mirror predicate. Arabic names are not stored (gl_accounts has no Arabic column).</para>
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20261113010000_RenameAbsenceContraAccounts")]
    public partial class RenameAbsenceContraAccounts : Migration
    {
        internal const string OldAttendance = "Attendance & Late Deductions (contra)";
        internal const string OldLeave = "Unpaid Leave & LOP Deductions (contra)";
        internal const string NewAttendance = "Absence, Late & Early Deductions (contra)";
        internal const string NewLeave = "Unpaid Leave Deductions (contra)";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Rename(migrationBuilder, "5008", "DED:ATTENDANCE", OldAttendance, NewAttendance);
            Rename(migrationBuilder, "5009", "DED:LEAVE", OldLeave, NewLeave);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Rename(migrationBuilder, "5008", "DED:ATTENDANCE", NewAttendance, OldAttendance);
            Rename(migrationBuilder, "5009", "DED:LEAVE", NewLeave, OldLeave);
        }

        private static void Rename(MigrationBuilder migrationBuilder, string code, string driverKey, string from, string to)
        {
            migrationBuilder.Sql($@"
UPDATE gl_accounts SET name = '{to}', updated_at_utc = now()
WHERE code = '{code}' AND name = '{from}' AND account_type = 'Expense';");
            migrationBuilder.Sql($@"
UPDATE gl_drivers SET default_name = '{to}', updated_at_utc = now()
WHERE key = '{driverKey}' AND is_system = TRUE AND default_code = '{code}' AND default_name = '{from}';");
        }
    }
}
