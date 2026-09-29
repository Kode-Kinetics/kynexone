using System.Web;

namespace Zayra.Api.Application.Common;

/// <summary>
/// Accepts a Postgres connection string in EITHER form and returns Npgsql keyword form.
///
/// <para><b>Why this exists.</b> Npgsql parses only keyword form
/// (<c>Host=…;Database=…;Username=…;Password=…</c>). Neon's console hands out URI form
/// (<c>postgresql://user:pass@host/db?sslmode=require</c>), and every managed-Postgres console
/// does the same. Pasting one where the other is expected does not fail with "wrong format" — it
/// fails with <c>ArgumentException: Couldn't set &lt;host&gt;/&lt;db&gt;?sslmode</c> and an inner
/// <c>KeyNotFoundException</c>, because Npgsql reads the whole URI as one unknown keyword.</para>
///
/// <para><b>What it cost.</b> <c>PROD_DATABASE_URL</c> was updated to a URI, and the next
/// <c>dotnet ef database update</c> in the deploy gate died on exactly that exception. The gate
/// that compares CI's target against the live service passed first — <c>check_migration_target.py</c>
/// already understands URIs — so every signal said the right database was about to be migrated, and
/// then the migration could not open it. The deploy was skipped and nothing shipped.</para>
///
/// <para><b>Why here rather than in the workflow.</b> This assembly has no
/// <c>IDesignTimeDbContextFactory</c>, so <c>dotnet ef</c> boots <c>Program.cs</c>'s host and reads
/// the same configuration the service does. Normalising at that one read fixes the migration path
/// and the runtime path together, which is the point: they are two separately-maintained copies of
/// one fact, and this is the only place they are guaranteed to agree.</para>
/// </summary>
public static class PostgresConnectionString
{
    /// <summary>Query parameters that map onto an Npgsql keyword. Anything else is DROPPED rather
    /// than passed through: an unrecognised keyword is the failure mode this class exists to
    /// prevent, and Neon appends <c>channel_binding</c>, which Npgsql has no keyword for.</summary>
    private static readonly Dictionary<string, string> KnownParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sslmode"] = "SSL Mode",
        ["application_name"] = "Application Name",
        ["connect_timeout"] = "Timeout",
    };

    /// <summary>
    /// Returns <paramref name="value"/> unchanged when it is already keyword form (or empty), and
    /// converts it when it is a <c>postgres://</c> / <c>postgresql://</c> URI.
    /// </summary>
    public static string Normalize(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0) return raw;

        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
         && !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return raw;

        // A malformed URI is returned untouched so the caller still gets Npgsql's own error with the
        // original text in it, rather than one this method invented from a half-parse.
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return raw;

        // No host means this is not a usable URI however well it parsed ("postgresql://" is a
        // legal Uri with an empty Host). Hand the original back so Npgsql reports what was actually
        // configured; returning the empty conversion would instead read as "no connection string
        // set" and take the missing-config branch, which names the wrong problem.
        if (string.IsNullOrEmpty(uri.Host)) return raw;

        var parts = new List<string> { $"Host={uri.Host}" };
        // -1 means the URI stated no port; Npgsql's own default (5432) then applies.
        if (uri.Port > 0) parts.Add($"Port={uri.Port}");

        var database = uri.AbsolutePath.TrimStart('/');
        if (database.Length > 0) parts.Add($"Database={Uri.UnescapeDataString(database)}");

        // Credentials are percent-encoded in a URI — a password containing '@' or '/' arrives as
        // %40 or %2F and must be decoded, or authentication fails with a correct-looking string.
        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo.Length > 0 && userInfo[0].Length > 0) parts.Add($"Username={Uri.UnescapeDataString(userInfo[0])}");
        if (userInfo.Length > 1 && userInfo[1].Length > 0) parts.Add($"Password={Uri.UnescapeDataString(userInfo[1])}");

        var query = HttpUtility.ParseQueryString(uri.Query);
        foreach (var key in query.AllKeys)
        {
            if (key is null) continue;
            if (!KnownParameters.TryGetValue(key, out var keyword)) continue;
            var v = query[key];
            if (string.IsNullOrWhiteSpace(v)) continue;
            parts.Add($"{keyword}={v}");
        }

        return string.Join(";", parts);
    }
}
