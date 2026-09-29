using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Raw-SQL execution RATCHET — the blind spot beside
/// <see cref="QueryFilterBypassRatchetTests"/>.
///
/// <para>That ratchet counts <c>.IgnoreQueryFilters()</c> call sites, which is the only way to
/// drop the tenant and company filters <em>in LINQ</em>. It cannot see raw SQL, because raw SQL
/// never had the filters to begin with: <c>Database.ExecuteSqlRawAsync</c> hands a string to the
/// driver, and EF applies nothing to it.</para>
///
/// <para>That gap was not theoretical. <c>AuthSeeder</c> carried an unguarded
/// <c>UPDATE users SET is_group_scope = TRUE …</c> that ran across EVERY tenant on EVERY boot and
/// re-promoted deliberately de-scoped administrators to full group scope. It was a cross-tenant
/// privilege escalation written in the one dialect the isolation lint was structurally unable to
/// read, and it survived for as long as it did partly because the guard reported green.
/// See <c>AuthSeederScopeRestartTests</c> for the behaviour, and the comment at the deletion site.</para>
///
/// <para>This test is ADDITIVE. It does not read, relax or re-pin
/// <see cref="QueryFilterBypassRatchetTests"/>; that ratchet's counts are untouched and still
/// govern LINQ bypasses. This one pins the second dialect, so a new raw statement has to be
/// declared here to compile green.</para>
///
/// <para>THE RULE FOR NEW CODE: prefer LINQ, and where scope must genuinely be crossed, prefer
/// <c>Zayra.Api.Infrastructure.Data.ScopedBypass</c>, which forces the author to name the actor
/// and re-applies the restriction that must survive. Raw SQL that WRITES is the last resort: it
/// answers to no filter, so its own WHERE clause is the entire tenant boundary, and the author
/// must write that boundary by hand and prove it with a test.</para>
///
/// <para>Counts may only go DOWN without discussion. Raising one, or adding a file, means an
/// entry below saying what the statement does, which rows it can reach across tenants, and what
/// proves it cannot reach further.</para>
/// </summary>
public class RawSqlExecutionRatchetTests
{
    /// <summary>
    /// The EF APIs that execute an author-supplied non-query statement. The read-side
    /// counterparts (<c>SqlQueryRaw</c>, <c>FromSqlRaw</c>) are deliberately NOT here: they cannot
    /// modify a row, so the worst they can do is over-read, which the orphan and scope-resolution
    /// ratchets already cover. This ratchet is about writes that no filter can reach.
    /// </summary>
    private static readonly string[] WriteApis =
    [
        "ExecuteSqlRaw",            // covers ExecuteSqlRawAsync
        "ExecuteSqlInterpolated",   // covers ExecuteSqlInterpolatedAsync
    ];

    // file (relative to Zayra.Api) -> approved number of raw non-query SQL call sites.
    private static readonly Dictionary<string, int> ApprovedRawSqlCounts = new()
    {
        // ── Advisory locks. `SELECT pg_advisory_*lock(key)` writes no row at all; it is only
        // here because it goes through the same API. The key is derived per tenant/entity at
        // each site. No tenant-boundary risk. ────────────────────────────────────────────────
        ["Controllers/MigrationImportController.cs"] = 2,
        ["Data/ZayraDbContext.cs"] = 2,
        ["Infrastructure/Auth/AccessManagementService.cs"] = 2,
        ["Infrastructure/Finance/FinanceDecisionSerializer.cs"] = 1,
        ["Infrastructure/Organization/EstablishmentGuardService.cs"] = 1,

        // ── Real statements. Each one's WHERE clause IS its tenant boundary. ─────────────────

        // Interpolated, and parameterised on the tenant/employee being processed.
        ["Infrastructure/Attendance/AttendanceService.cs"] = 1,
        ["Infrastructure/Leave/LeaveService.cs"] = 1,

        // 2 -> 1. The survivor is the Admin role_permissions backfill: a set-based INSERT that is
        // cross-tenant BY DESIGN (every tenant's Admin role gets every permission — that is the
        // definition of the role) and is guarded by NOT EXISTS + ON CONFLICT DO NOTHING, so it
        // grants nothing a fresh tenant would not already get from EnsureTenantRolesAsync.
        //
        // The one that is GONE is the privilege escalation: an unguarded, unconditional
        // `UPDATE users SET is_group_scope = TRUE` over every tenant, re-run on every boot, which
        // silently widened administrators whose scope had been deliberately narrowed. Its
        // replacement is a COUNT(*) — read-only, and therefore correctly invisible to this
        // ratchet. If a future change needs to write here again, this number moves and that is
        // exactly the conversation this file exists to force.
        ["Infrastructure/Seed/AuthSeeder.cs"] = 1,
    };

    /// <summary>
    /// Migrations are excluded. A migration is raw SQL by definition and by design; it runs once,
    /// under an operator, against a schema rather than a request, and
    /// <c>DeployHardeningTests</c> governs it instead.
    /// </summary>
    private const string ExcludedDirectory = "Migrations";

    private static string ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) break;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }

        // A guard that SKIPS when it cannot find its input is a false negative, not a safe
        // default: a CI layout change would silently disable it while still reporting green.
        throw new InvalidOperationException(
            $"Could not locate the Zayra.Api source root from {AppContext.BaseDirectory}. " +
            "This ratchet cannot pass without scanning it.");
    }

    private static Dictionary<string, int> ScanActual(string apiRoot)
    {
        var actual = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith(ExcludedDirectory + "/", StringComparison.Ordinal)) continue;
            if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                relative.Contains("/bin/", StringComparison.Ordinal) ||
                relative.StartsWith("obj/", StringComparison.Ordinal) ||
                relative.StartsWith("bin/", StringComparison.Ordinal)) continue;

            var count = File.ReadAllLines(file)
                .Count(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                            && WriteApis.Any(api => line.Contains(api, StringComparison.Ordinal)));
            if (count > 0) actual[relative] = count;
        }
        return actual;
    }

    [Fact]
    public void NoNewRawSqlWriteMayBeIntroduced()
    {
        var actual = ScanActual(ResolveApiRoot());

        var offenders = actual
            .Where(kv => !ApprovedRawSqlCounts.TryGetValue(kv.Key, out var approved) || kv.Value > approved)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => ApprovedRawSqlCounts.TryGetValue(kv.Key, out var approved)
                ? $"{kv.Key}: {kv.Value} raw non-query SQL call sites, approved {approved}"
                : $"{kv.Key}: {kv.Value} raw non-query SQL call sites, NOT on the approved list")
            .ToList();

        offenders.Should().BeEmpty(
            "raw SQL carries no tenant or company query filter, so each statement's own WHERE clause " +
            "is the entire isolation boundary. Prefer LINQ, or ScopedBypass where scope must genuinely " +
            "be crossed. If raw SQL is truly required, add an entry to ApprovedRawSqlCounts in " +
            "RawSqlExecutionRatchetTests stating what the statement writes, which tenants' rows it can " +
            "reach, and the test that proves it cannot reach further.\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The approved list must not outrun reality. A slot pinned above the real count is a
    /// silently pre-authorised future raw write — the exact thing this file exists to prevent, and
    /// a mistake <see cref="QueryFilterBypassRatchetTests"/> has already had to correct once.
    /// </summary>
    [Fact]
    public void ApprovedRawSqlCountsAreNotInflated()
    {
        var actual = ScanActual(ResolveApiRoot());

        var stale = ApprovedRawSqlCounts
            .Where(kv => !actual.TryGetValue(kv.Key, out var found) || found < kv.Value)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => actual.TryGetValue(kv.Key, out var found)
                ? $"{kv.Key}: approved {kv.Value}, actually {found} — lower the approved count"
                : $"{kv.Key}: approved {kv.Value}, file has none (or is gone) — remove the entry")
            .ToList();

        stale.Should().BeEmpty(
            "the ratchet may only ever go DOWN, and an approved count above the real one pre-authorises " +
            "a raw write nobody reviewed.\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    /// The specific regression. AuthSeeder must never again execute a raw statement that widens a
    /// user's scope; <c>AuthSeederScopeRestartTests</c> proves the behaviour, this proves the
    /// dialect cannot come back unnoticed even for a tenant no test happens to create.
    /// </summary>
    [Fact]
    public void AuthSeederExecutesNoRawScopeWidening()
    {
        var source = File.ReadAllText(Path.Combine(ResolveApiRoot(), "Infrastructure/Seed/AuthSeeder.cs"));

        var statements = source
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(line => line.Contains("is_group_scope", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim())
            .ToList();

        statements.Should().NotContain(
            line => line.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("SET ", StringComparison.OrdinalIgnoreCase),
            "AuthSeeder may READ is_group_scope to report a stranded administrator, but it must never " +
            "write it: scope is an authorisation decision that belongs to an authenticated admin behind " +
            "AccessController's audit trail, not to an unattended boot path that runs on every deploy.");
    }
}
