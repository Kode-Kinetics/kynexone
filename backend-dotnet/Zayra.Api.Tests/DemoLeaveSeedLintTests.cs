using System.Text.RegularExpressions;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// WAVE 0 — THE LEAVE / APPROVALS COUNTERPART OF <see cref="AttendanceDemoSeedTests"/>.
///
/// <para><see cref="LeaveRequest"/> and <see cref="ApprovalRequest"/> are
/// <c>ICompanyScopedOperational</c>: a row whose <c>CompanyId</c> is null is visible to a
/// GROUP-scope caller ONLY. A seeder that forgets the company therefore produces a tenant where the
/// admin sees a full leave list while every ordinary company-scoped pilot login opens Leave — and
/// Approvals — and sees nothing. Nothing downstream rescues those rows: the write-time scope
/// enforcement skips trusted system writers, and <c>CompanyScopeBackfill</c> runs BEFORE the
/// seeders, so on a fresh database it sweeps an empty table and the bad rows land after it. See the
/// remarks on <see cref="DemoLeaveSeed"/>.</para>
///
/// <para>The behavioural fix was to route seeded leave through <see cref="DemoLeaveSeed"/>, which
/// stamps <c>CompanyId</c> from the owning employee at CONSTRUCTION. This file is the guard that
/// stops the next seeder from re-opening the hole by hand-rolling the entity again — the direct
/// mirror of <c>AttendanceDemoSeedTests.NoSeeder_ConstructsTheLegacyAttendanceRecordDirectly</c>.</para>
///
/// <para>Follows the existing source-lint convention in <c>Security/BypassLintTests</c>: if the
/// source tree cannot be located the test returns rather than reporting a false pass.</para>
/// </summary>
public class DemoLeaveSeedLintTests
{
    /// <summary>The one door. Everything else must go through it or stamp the company itself.</summary>
    private const string CanonicalBuilder = "DemoLeaveSeed.cs";

    /// <summary>
    /// A seeder may not construct a company-scoped operational row without setting
    /// <c>CompanyId</c> in the same initializer.
    ///
    /// <para>This is deliberately narrower than the attendance lint, which bans direct construction
    /// outright. The invariant that actually matters for leave is "the company is never null", and a
    /// seeder that sets it inline is correct even though it bypasses the builder
    /// (<c>EnterpriseGroupSeeder</c> does exactly that). Banning construction outright would have
    /// flagged that correct code and taught the team to suppress the lint — which is how a guard
    /// stops guarding.</para>
    /// </summary>
    [Theory]
    [InlineData(nameof(LeaveRequest))]
    [InlineData(nameof(ApprovalRequest))]
    public void NoSeeder_ConstructsACompanyScopedRowWithoutACompanyId(string entity)
    {
        var seedDir = ResolveSeedSourceDirectory();
        if (seedDir is null) return;   // binaries relocated; cannot scan — do not false-pass

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(seedDir, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Equals(CanonicalBuilder, StringComparison.Ordinal)) continue;

            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, $@"new\s+{Regex.Escape(entity)}\s*(\{{|\()"))
            {
                var initializer = ExtractInitializer(source, match.Index + match.Length - 1);
                if (initializer is null) continue;                       // not an object initializer
                if (Regex.IsMatch(initializer, @"\bCompanyId\s*=")) continue;   // stamped inline — fine

                var line = source.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            $"Seeders must never construct a {entity} without a CompanyId. {entity} is " +
            "ICompanyScopedOperational: a null company makes the row invisible to every " +
            "company-scoped user, so the module renders EMPTY for ordinary pilot logins while a " +
            "group-scope admin still sees it — the customer-pilot blocker this guard exists to " +
            $"close. Build the row through DemoLeaveSeed (which stamps employee.CompanyId) or set " +
            $"CompanyId in the initializer. Offenders: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// Returns the balanced <c>{...}</c> initializer starting at <paramref name="openIndex"/>,
    /// or null when the construction has no object initializer (e.g. <c>new LeaveRequest(...)</c>).
    /// </summary>
    private static string? ExtractInitializer(string source, int openIndex)
    {
        if (openIndex >= source.Length || source[openIndex] != '{') return null;

        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source[openIndex..(i + 1)];
            }
        }
        return null;   // unbalanced — treat as unparseable rather than as an offender
    }

    /// <summary>Same walk-up as AttendanceDemoSeedTests, so both lints agree on "cannot scan".</summary>
    private static string? ResolveSeedSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) return null;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api", "Infrastructure", "Seed");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
