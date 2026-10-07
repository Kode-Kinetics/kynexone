using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Session-scoped advisory locks are banned in Zayra.Api (C# and SQL, any case).
///
/// <para>Production reaches Neon through its PgBouncer pooler in transaction mode. A
/// <c>pg_advisory_lock</c> is owned by whichever pooled server session ran it, the matching unlock
/// usually runs on another one (a no-op), and the lock leaks to every request that later draws
/// that session — re-entrantly, so it also stops excluding anyone. Direct-connection tests cannot
/// see this; <c>AdvisoryLockTransactionPoolingPostgresTests</c> shows it behind a real pooler.</para>
///
/// <para>Use <c>pg_advisory_xact_lock</c> inside the operation's transaction (enforced at runtime
/// by <c>AdvisoryXactLockGuardInterceptor</c>), or <c>TransactionHeldAdvisoryLease</c> when the
/// lock must span many transactions.</para>
/// </summary>
public sealed partial class SessionAdvisoryLockRatchetTests
{
    [GeneratedRegex(@"pg_(try_)?advisory_(lock|unlock)(_shared|_all)?\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex SessionLevelAdvisoryCall();

    [Fact]
    public void NoSessionScopedAdvisoryLockInApiSource()
    {
        var offenders = new List<string>();
        foreach (var (relative, code) in SourceScan.Files(SourceScan.ResolveApiRoot(), "*.cs", "*.sql"))
            foreach (Match m in SessionLevelAdvisoryCall().Matches(code))
                offenders.Add($"{relative}:{SourceScan.LineOf(code, m.Index)}: {m.Value}");

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
                     "SELECT pg_advisory_lock_shared(1)", "SELECT pg_advisory_unlock_all()", "select PG_ADVISORY_LOCK (1)",
                 })
            SessionLevelAdvisoryCall().IsMatch(call).Should().BeTrue(call);

        foreach (var call in new[] { "SELECT pg_advisory_xact_lock(1)", "SELECT pg_try_advisory_xact_lock(1)", "SELECT pg_advisory_xact_lock_shared(1)" })
            SessionLevelAdvisoryCall().IsMatch(call).Should().BeFalse(call);
    }

    [Fact]
    public void CommentStripping_HidesCommentsButNotCodeAfterAUrlLiteral()
    {
        SourceScan.StripCSharpComments("// pg_advisory_lock(1)").Should().NotContain("pg_advisory_lock");
        SourceScan.StripCSharpComments("/* pg_advisory_lock(1) */ x").Should().NotContain("pg_advisory_lock");
        SourceScan.StripCSharpComments("var u = \"http://x\"; Run(\"pg_advisory_lock(1)\");").Should().Contain("pg_advisory_lock(1)");
        SourceScan.StripSqlComments("-- pg_advisory_lock(1)\nSELECT 1;").Should().NotContain("pg_advisory_lock");
    }
}
