using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The session epoch (users.session_epoch) is raised by the ZayraDbContext save hook when a login's password, factor or
/// active state changes. That hook only sees changes that go through SaveChanges. An <c>ExecuteUpdate</c> or a raw SQL
/// statement that writes one of those columns on <c>users</c> skips it, so the user's existing sessions would keep
/// refreshing after the change. This ratchet pins every such write to an allowlist with its reason.
///
/// <para>The trigger columns are the ones <c>ZayraDbContext.TrustRevokeReason</c> reads: <c>password_hash</c>,
/// <c>mfa_secret_encrypted</c>, <c>mfa_enabled</c>, <c>status</c>, <c>is_active</c>, <c>is_deleted</c> and
/// <c>access_mode</c> (NoLogin). A new write either goes through SaveChanges or, if it truly must bypass it, raises the
/// epoch itself (<c>TenantSessionSecurity.EndAllSessions</c> in the same unit of work) and is added here with its reason.</para>
/// </summary>
public sealed partial class SessionEpochTriggerColumnWriteRatchetTests
{
    // file (relative to Zayra.Api) -> approved number of bulk/raw writes of a trigger column on users.
    private static readonly Dictionary<string, int> Approved = new(StringComparer.Ordinal)
    {
        // AuthService.UpgradePasswordHashAsync: after a successful sign-in, re-hashes the SAME password at today's work
        // factor with a compare-and-set (WHERE password_hash = the verified hash). The credential does not change, so
        // it must not end the user's other sessions or rotate their stamp; that is why it bypasses SaveChanges.
        ["Infrastructure/Auth/AuthService.cs"] = 1,
    };

    private const string TriggerProperties = "PasswordHash|MfaSecretEncrypted|MFAEnabled|Status|IsActive|IsDeleted|AccessMode";
    private const string TriggerColumns = "password_hash|mfa_secret_encrypted|mfa_enabled|status|is_active|is_deleted|access_mode";

    [GeneratedRegex(@"\.Users\b|Set<(?:Zayra\.Api\.)?(?:Domain\.Entities\.)?User>\s*\(")]
    private static partial Regex TargetsUsers();

    [GeneratedRegex(@"SetProperty\(\s*\w+\s*=>\s*\w+\.(?:" + TriggerProperties + @")\b")]
    private static partial Regex SetsTriggerProperty();

    [GeneratedRegex(@"UPDATE\s+(?:public\.)?""?users""?\s+SET\b[^;]*?\b(?:" + TriggerColumns + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RawUpdateOfTriggerColumn();

    [Fact]
    public void NoNewBulkOrRawWriteOfASessionTriggerColumn()
    {
        var actual = ScanActual(ResolveApiRoot());
        var offenders = actual
            .Where(kv => !Approved.TryGetValue(kv.Key, out var approved) || kv.Value.Count > approved)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}: {kv.Value.Count} (approved {(Approved.TryGetValue(kv.Key, out var a) ? a : 0)}):\n      "
                + string.Join("\n      ", kv.Value))
            .ToList();

        offenders.Should().BeEmpty(
            "a bulk or raw write of a password, factor or active-state column on users bypasses the save hook that ends " +
            "the user's sessions (users.session_epoch). Write it through SaveChanges, or raise the epoch in the same unit " +
            "of work and add the site to Approved with its reason.\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The allowlist must not outrun reality, and the scanner must still find the one known site.</summary>
    [Fact]
    public void ApprovedCountsMatchTheSource()
    {
        var actual = ScanActual(ResolveApiRoot());
        var stale = Approved
            .Where(kv => !actual.TryGetValue(kv.Key, out var found) || found.Count != kv.Value)
            .Select(kv => $"{kv.Key}: approved {kv.Value}, found {(actual.TryGetValue(kv.Key, out var f) ? f.Count : 0)}")
            .ToList();
        stale.Should().BeEmpty("an approved slot above the real count pre-authorises a write nobody reviewed, and a "
            + "count of zero for the known rehash site means the scanner went blind.\n  " + string.Join("\n  ", stale));
    }

    [Theory]
    [InlineData("await _db.Users.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);", 1)]
    [InlineData("await db.Set<User>().Where(x => x.Id == id)\n    .ExecuteUpdateAsync(s => s\n        .SetProperty(u => u.MfaSecretEncrypted, (string?)null), ct);", 1)]
    [InlineData("await _db.Users.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastLoginAtUtc, now), ct);", 0)]
    [InlineData("await _db.PlatformUsers.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.PasswordHash, h), ct);", 0)]
    [InlineData("await _db.Employees.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, \"Left\"), ct);", 0)]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"UPDATE users SET status = 'Suspended' WHERE id = {0}\", id);", 1)]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"UPDATE \\\"users\\\" SET last_login_at_utc = now() WHERE id = {0}\", id);", 0)]
    public void TheScannerSeesWhatItMustAndOnlyThat(string source, int expected)
        => Scan(source).Should().HaveCount(expected);

    private static Dictionary<string, List<string>> ScanActual(string apiRoot)
    {
        var actual = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("Migrations/", StringComparison.Ordinal)
                || relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
                continue;
            var found = Scan(File.ReadAllText(file));
            if (found.Count > 0) actual[relative] = found;
        }
        return actual;
    }

    /// <summary>Each finding is one statement: an ExecuteUpdate on users that sets a trigger property, or a raw UPDATE users of a trigger column.</summary>
    private static List<string> Scan(string source)
    {
        var code = string.Join('\n', source.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var findings = new List<string>();

        for (var at = code.IndexOf("ExecuteUpdate", StringComparison.Ordinal); at >= 0;
             at = code.IndexOf("ExecuteUpdate", at + 1, StringComparison.Ordinal))
        {
            var start = code.LastIndexOfAny([';', '{', '}'], at) + 1;
            var end = code.IndexOf(';', at);
            if (end < 0) end = code.Length;
            var statement = code[start..end];
            if (TargetsUsers().IsMatch(statement) && SetsTriggerProperty().IsMatch(statement))
                findings.Add(Squash(statement));
        }
        foreach (Match match in RawUpdateOfTriggerColumn().Matches(code))
            findings.Add(Squash(match.Value));
        return findings;
    }

    private static string Squash(string statement)
    {
        var flat = Regex.Replace(statement, @"\s+", " ").Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
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
        // A guard that skips when it cannot find its input is a false negative, not a safe default.
        throw new InvalidOperationException($"Could not locate the Zayra.Api source root from {AppContext.BaseDirectory}.");
    }
}
