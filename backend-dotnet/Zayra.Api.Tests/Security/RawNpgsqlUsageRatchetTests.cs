using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Raw <c>NpgsqlConnection</c> / <c>NpgsqlCommand</c> (and the data-source / batch APIs) bypass every
/// EF interceptor, so nothing tenant- or lock-related that the DbContext enforces applies to them.
/// The one sanctioned use is <c>TransactionHeldAdvisoryLease</c>, which needs its own connection to
/// hold an advisory lock across the import's many transactions and touches no table. Anything else
/// goes through the DbContext.
/// </summary>
public sealed partial class RawNpgsqlUsageRatchetTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "Infrastructure/Data/TransactionHeldAdvisoryLease.cs",
    };

    [GeneratedRegex(@"\b(NpgsqlConnection|NpgsqlCommand|NpgsqlDataSource|NpgsqlDataSourceBuilder|NpgsqlBatch)\b")]
    private static partial Regex RawNpgsqlApi();

    [Fact]
    public void RawNpgsqlConnectionsAndCommands_OnlyInTheAdvisoryLease()
    {
        var offenders = new List<string>();
        var allowedSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relative, code) in SourceScan.Files(SourceScan.ResolveApiRoot(), "*.cs"))
            foreach (Match m in RawNpgsqlApi().Matches(code))
            {
                if (Allowed.Contains(relative)) { allowedSeen.Add(relative); continue; }
                offenders.Add($"{relative}:{SourceScan.LineOf(code, m.Index)}: {m.Value}");
            }

        offenders.Should().BeEmpty(
            "raw Npgsql connections and commands skip the EF interceptors; use the DbContext. " +
            "If a raw connection is truly required, add the file to Allowed with the reason.\n  " +
            string.Join("\n  ", offenders));
        allowedSeen.Should().BeEquivalentTo(Allowed, "an allowance for a file that no longer needs it must be removed");
    }
}
