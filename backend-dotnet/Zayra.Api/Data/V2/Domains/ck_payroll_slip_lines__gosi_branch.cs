// <manually written: TARGET_SCHEMA.md revision 7 SS9 row 37. NOT yet in Db/baseline. />
#pragma warning disable IDE1006 // the type name IS the constraint name
namespace Zayra.Api.Data.V2.Domains;

/// <summary>
/// Closed set behind <c>payroll_slip_lines.gosi_branch</c>, specified by TARGET_SCHEMA.md revision 7 SS9 row 37
/// as the CHECK constraint <c>ck_payroll_slip_lines__gosi_branch</c>.
/// <para>
/// <b>The baseline DDL does not yet declare this constraint</b>, so the column is currently
/// unconstrained <c>varchar(40)</c> in the database. It is listed in
/// <see cref="KynexDomains.DesignedButNotYetConstrained"/> rather than
/// <see cref="KynexDomains.All"/>, and KynexDomainsTests will start asserting its values against
/// pg_constraint the moment 020/021 declares it.
/// </para>
/// <para>
/// SS9 calls rows 37 and 38 the sharpest of the eight: gosi_branch and payer are frozen onto every
/// slip line so a slip can be recomputed years later, and trg_gosi_filing_totals pivots the seven
/// filing totals on exactly those two columns. Because SS11.2 makes that trigger a WARNING and not
/// a block, a value that matches no pivot arm contributes to no total and the filing is short by
/// exactly that line, with no signal naming the unroutable value. SANED keeps its capitals: it is
/// the acronym the scheme is published under and the spelling the trigger pivots on.
/// </para>
/// </summary>
public static class ck_payroll_slip_lines__gosi_branch
{
    public const string Table = "payroll_slip_lines";
    public const string Column = "gosi_branch";
    public const string ConstraintName = "ck_payroll_slip_lines__gosi_branch";
    public const bool NullAllowed = true;

    public const string Annuities = "Annuities";
    public const string SANED = "SANED";
    public const string OccupationalHazards = "OccupationalHazards";

    /// <summary>Every legal value, in the order SS9 row 37 declares them.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Annuities,
        SANED,
        OccupationalHazards,
    };

    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
