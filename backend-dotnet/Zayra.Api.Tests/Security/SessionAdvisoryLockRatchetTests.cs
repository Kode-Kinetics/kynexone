using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Session-scoped advisory locks are banned in Zayra.Api.
///
/// <para>Production reaches Neon through its PgBouncer pooler in transaction mode. A
/// <c>pg_advisory_lock</c> is owned by whichever pooled server session ran it, the matching unlock
/// usually runs on another one (a no-op), and the lock leaks to every request that later draws
/// that session — re-entrantly, so it also stops excluding anyone. Direct-connection tests cannot
/// see this; <c>AdvisoryLockTransactionPoolingPostgresTests</c> shows it behind a real pooler.</para>
///
/// <para>Use <c>pg_advisory_xact_lock</c> inside the operation's transaction, or
/// <c>TransactionHeldAdvisoryLease</c> when the lock must span many transactions.</para>
/// </summary>
public sealed partial class SessionAdvisoryLockRatchetTests
{
    [GeneratedRegex(@"pg_(try_)?advisory_(lock|unlock)(_shared|_all)?\s*\(")]
    private static partial Regex SessionLevelAdvisoryCall();

    [Fact]
    public void NoSessionScopedAdvisoryLockInApiSource()
    {
        var apiRoot = ResolveApiRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal))
                continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (SessionLevelAdvisoryCall().IsMatch(lines[i]))
                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "a session advisory lock is not owned by the caller under Neon's transaction-mode pooler; " +
            "use pg_advisory_xact_lock inside the transaction or TransactionHeldAdvisoryLease.\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void PatternCatchesEverySessionLevelVariant_AndNoTransactionLevelOne()
    {
        foreach (var call in new[]
                 {
                     "SELECT pg_advisory_lock(1)", "SELECT pg_try_advisory_lock(1)", "SELECT pg_advisory_unlock(1)",
                     "SELECT pg_advisory_lock_shared(1)", "SELECT pg_advisory_unlock_all()",
                 })
            SessionLevelAdvisoryCall().IsMatch(call).Should().BeTrue(call);

        foreach (var call in new[] { "SELECT pg_advisory_xact_lock(1)", "SELECT pg_try_advisory_xact_lock(1)", "SELECT pg_advisory_xact_lock_shared(1)" })
            SessionLevelAdvisoryCall().IsMatch(call).Should().BeFalse(call);
    }

    private static string ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException(
            $"Could not locate the Zayra.Api source root from {AppContext.BaseDirectory}; this ratchet cannot pass without scanning it.");
    }
}
