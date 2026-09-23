using System.Reflection;
using Zayra.Api.Data.V2.Domains;

namespace Zayra.Api.Tests;

/// <summary>
/// The constants classes in <c>Data/V2/Domains</c> against the CHECK constraints in a database
/// built from <c>Db/baseline</c>.
///
/// TARGET_SCHEMA.md §9 makes the constraint NAME load-bearing: "mirrored by a C# constants class
/// of the same name, the name is what CI compares the constants class against". §9.0 freezes the
/// 38 <c>chk_*</c> names as a closed legacy set that may never grow a thirty-ninth, and requires
/// every enumeration added from revision 7 onward to take a <c>ck_&lt;table&gt;__&lt;assertion&gt;</c>
/// name. These tests are that CI comparison: a value added to a CHECK without its constant, a
/// constant with no CHECK behind it, a renamed class, or a thirty-ninth <c>chk_</c> all fail here.
/// </summary>
[Trait("Category", "Integration")]
[Collection("KynexBaseline")]
public sealed class KynexDomainsTests
{
    private readonly KynexBaselineFixture _fx;

    public KynexDomainsTests(KynexBaselineFixture fx) => _fx = fx;

    private const string EnumeratedChecks =
        """
        SELECT rel.relname, con.conname, pg_get_constraintdef(con.oid)
        FROM pg_constraint con
        JOIN pg_class rel ON rel.oid = con.conrelid
        JOIN pg_namespace n ON n.oid = rel.relnamespace
        WHERE n.nspname = 'public' AND con.contype = 'c' AND NOT rel.relispartition
        """;

    private async Task<Dictionary<string, (string Table, List<string> Values, bool NullAllowed)>> ReadDomainsAsync()
    {
        var rows = await _fx.QueryAsync(
            EnumeratedChecks,
            r => (Table: r.GetString(0), Name: r.GetString(1), Definition: r.GetString(2)));

        var domains = new Dictionary<string, (string, List<string>, bool)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var parsed = CheckConstraint.ParseEnumeration(row.Definition);
            if (parsed is null)
            {
                continue; // a CHECK that asserts something other than a closed set
            }

            domains[row.Name] = (row.Table, parsed.Value.Values, parsed.Value.NullAllowed);
        }

        return domains;
    }

    [Fact]
    public async Task EveryEnumeratedCheck_HasAConstantsClassOfTheSameName_WithTheSameValues()
    {
        var inDatabase = await ReadDomainsAsync();
        var inCode = KynexDomains.All.ToDictionary(d => d.ConstraintName, StringComparer.Ordinal);

        var missing = inDatabase.Keys.Except(inCode.Keys, StringComparer.Ordinal).ToList();
        var extra = inCode.Keys.Except(inDatabase.Keys, StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            "CHECK constraints with no constants class (§9 requires one per closed set): " + string.Join(", ", missing));
        Assert.True(
            extra.Count == 0,
            "constants classes with no CHECK constraint behind them: " + string.Join(", ", extra));

        foreach (var (name, (table, values, nullAllowed)) in inDatabase)
        {
            var domain = inCode[name];
            Assert.Equal(table, domain.Table);
            Assert.Equal(values, domain.Values);
            Assert.Equal(nullAllowed, domain.NullAllowed);
        }
    }

    [Fact]
    public void EveryConstantsClass_IsNamedExactlyForItsConstraint()
    {
        var types = typeof(KynexDomains).Assembly.GetTypes()
            .Where(t => t.Namespace == "Zayra.Api.Data.V2.Domains" && t != typeof(KynexDomains)
                        && t.IsClass && t.IsAbstract && t.IsSealed)
            .ToList();

        Assert.NotEmpty(types);
        foreach (var type in types)
        {
            var declared = (string)type.GetField("ConstraintName", BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null)!;
            Assert.Equal(declared, type.Name);
        }

        var registered = KynexDomains.All.Concat(KynexDomains.DesignedButNotYetConstrained)
            .Select(d => d.ConstraintName)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            types.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal),
            registered.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// §9.0: the <c>chk_</c> spelling is a closed legacy set of exactly 38 constraints — §9's 36
    /// rows, with row 3b split across auth_sessions and auth_tokens. No thirty-ninth is ever added;
    /// everything from revision 7 onward is <c>ck_</c>.
    /// </summary>
    [Fact]
    public async Task TheFrozenChkSet_IsExactlyThirtyEight_AndHasNotGrown()
    {
        var inDatabase = await ReadDomainsAsync();
        var chk = inDatabase.Keys.Where(k => k.StartsWith("chk_", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(38, chk.Count);
        Assert.Equal(chk, KynexDomains.All.Where(d => d.ConstraintName.StartsWith("chk_", StringComparison.Ordinal))
            .Select(d => d.ConstraintName).OrderBy(k => k, StringComparer.Ordinal));

        // Everything else follows the ck_<table>__<assertion> rule.
        var rest = inDatabase.Keys.Where(k => !k.StartsWith("chk_", StringComparison.Ordinal)).ToList();
        Assert.Empty(rest.Where(k => !k.StartsWith("ck_", StringComparison.Ordinal) || !k.Contains("__", StringComparison.Ordinal)));
    }

    /// <summary>
    /// §9 rows 39–44, the six of revision 7's eight that the baseline DDL does declare. Named one
    /// by one rather than counted, because a count passes when the wrong six are present.
    /// </summary>
    [Theory]
    [InlineData("ck_overtime_requests__ot_type", "Normal,WeeklyOff,PublicHoliday,Ramadan")]
    [InlineData("ck_attendance_punches__direction", "In,Out")]
    [InlineData("ck_final_settlements__separation_type", "Resignation,Termination,EndOfContract,Retirement,Death,Abscond")]
    [InlineData("ck_notification_deliveries__channel", "Email,Sms,Push")]
    [InlineData("ck_retention_purge_audits__outcome", "Applied,Skipped,Failed")]
    [InlineData("ck_eos_calculations__separation_reason", "Art84,Art85,Art87,Art77")]
    public async Task Rows39To44_AreConstrainedExactlyAsRevision7Rules(string constraint, string values)
    {
        var inDatabase = await ReadDomainsAsync();
        Assert.True(inDatabase.ContainsKey(constraint), constraint + " is not in the baseline");
        Assert.Equal(values, string.Join(",", inDatabase[constraint].Values));
        Assert.Equal(values, string.Join(",", KynexDomains.All.Single(d => d.ConstraintName == constraint).Values));
    }

    /// <summary>
    /// §9 rows 37 and 38 — the GOSI branch and payer vocabulary, which §9 calls the sharpest of the
    /// eight: trg_gosi_filing_totals pivots the seven filing totals on exactly these two columns and
    /// §11.2 makes its variance a WARNING, so a value outside the set routes to no total and the
    /// filing is silently short rather than loudly wrong.
    ///
    /// They were declared in 020 on 2026-09-23. This asserts the constraint exists on BOTH tables
    /// that carry the vocabulary, that NULL stays legal (the rule families with no branch or payer),
    /// and that the database and the constants agree value for value.
    /// </summary>
    [Fact]
    public async Task Rows37And38_AreConstrainedOnBothTables_AndAgreeWithTheConstants()
    {
        var inDatabase = await ReadDomainsAsync();
        string[] expected =
        [
            "ck_statutory_rules__gosi_branch", "ck_payroll_slip_lines__gosi_branch",
            "ck_statutory_rules__payer",       "ck_payroll_slip_lines__gosi_payer",
        ];

        foreach (var name in expected)
        {
            Assert.True(inDatabase.ContainsKey(name), $"{name} is not declared in the database");
            var domain = KynexDomains.All.Single(d => d.ConstraintName == name);
            Assert.Equal(inDatabase[name].Values, domain.Values);
            Assert.Equal(inDatabase[name].Table, domain.Table);
            Assert.True(domain.NullAllowed, $"{name} must permit NULL — the families with no branch or payer leave it empty");
        }

        Assert.Equal(["Annuities", "SANED", "OccupationalHazards"],
                     KynexDomains.All.Single(d => d.ConstraintName == "ck_statutory_rules__gosi_branch").Values);
        Assert.Equal(["Employee", "Employer"],
                     KynexDomains.All.Single(d => d.ConstraintName == "ck_statutory_rules__payer").Values);

        // The list they graduated from stays, empty, as the home for the next such gap.
        Assert.Empty(KynexDomains.DesignedButNotYetConstrained);
    }

    /// <summary>Parses the two shapes PostgreSQL prints for a closed-set CHECK.</summary>
    private static class CheckConstraint
    {
        private static readonly System.Text.RegularExpressions.Regex Plain = new(
            @"^CHECK \(\(\((?<col>\w+)\)::text = ANY \(\(ARRAY\[(?<vals>.*)\]\)::text\[\]\)\)\)$");

        private static readonly System.Text.RegularExpressions.Regex Nullable = new(
            @"^CHECK \(\(\((?<col>\w+) IS NULL\) OR \(\((?<col2>\w+)\)::text = ANY \(\(ARRAY\[(?<vals>.*)\]\)::text\[\]\)\)\)\)$");

        private static readonly System.Text.RegularExpressions.Regex Value = new(
            @"'((?:[^']|'')*)'::character varying");

        public static (List<string> Values, bool NullAllowed)? ParseEnumeration(string definition)
        {
            var nullAllowed = false;
            var match = Plain.Match(definition);
            if (!match.Success)
            {
                match = Nullable.Match(definition);
                if (!match.Success || match.Groups["col"].Value != match.Groups["col2"].Value)
                {
                    return null;
                }

                nullAllowed = true;
            }

            var values = Value.Matches(match.Groups["vals"].Value)
                .Select(m => m.Groups[1].Value.Replace("''", "'"))
                .ToList();

            return values.Count == 0 ? null : (values, nullAllowed);
        }
    }
}
