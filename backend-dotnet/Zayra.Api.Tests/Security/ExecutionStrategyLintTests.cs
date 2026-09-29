using System.Text;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Static ratchet for the retrying-execution-strategy defect class.
///
/// <para>Program.cs registers the DbContext with
/// <c>EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: 5s)</c>, so the ambient execution
/// strategy in production is <c>NpgsqlRetryingExecutionStrategy</c>. That strategy refuses a
/// user-initiated <c>BeginTransactionAsync</c> unless the entire unit runs inside
/// <c>Database.CreateExecutionStrategy().ExecuteAsync(...)</c>: it throws
/// <c>InvalidOperationException</c> BEFORE doing any work, and the generic exception handler turns
/// that into HTTP 400. An endpoint that takes a bare transaction is therefore dead 100% of the
/// time — not flaky, not slow, dead.</para>
///
/// <para>Four such endpoints shipped at once (POST /api/auth/refresh,
/// POST /api/setup/organization-structure-import/commit,
/// POST /api/performance/calibration/{cycleId}/adjust and the offer-acceptance path behind
/// PATCH /api/recruitment/offers/{id}/accept) and none of the 189 test files caught any of them,
/// because the integration fixture builds its contexts WITHOUT retry — and with no retrying
/// strategy a bare transaction is perfectly legal. Behavioural tests can only catch the sites they
/// happen to exercise; this lint catches all of them, including the ones not yet written.</para>
///
/// <para>The rule: every <c>BeginTransaction</c>/<c>BeginTransactionAsync</c> call in
/// <c>Zayra.Api</c> must be lexically enclosed by an <c>ExecuteAsync(... =&gt; ...)</c> delegate,
/// and a <c>CreateExecutionStrategy()</c> must appear above it in the same file. Enclosure is
/// decided structurally — comments and string literals are blanked out first, then brace depth is
/// tracked — so a mention of the strategy in a comment cannot launder a bare transaction.</para>
///
/// <para>Sibling of <see cref="BypassLintTests"/>, which is this repository's established pattern
/// for invariants that must hold at every site rather than at the sites a test remembers.</para>
/// </summary>
public class ExecutionStrategyLintTests
{
    // ── Path resolution ──────────────────────────────────────────────────────────

    private static string ResolveSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) break;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate))
                return candidate;
        }

        // A guard that SKIPS when it cannot find the source is a false negative, not a safe
        // default: a CI layout change would silently disable it while still reporting green.
        throw new InvalidOperationException(
            "The Zayra.Api source root could not be resolved, so this guard would check NOTHING. "
            + "Treat an unresolvable path as a build failure, never a skip.");
    }

    // ── Scanner ──────────────────────────────────────────────────────────────────

    private static readonly string[] TransactionMarkers = { "BeginTransactionAsync(", "BeginTransaction(" };

    /// <summary>
    /// Blanks out every comment and string literal (line comments, block comments, regular,
    /// verbatim and interpolated-verbatim strings, and char literals), replacing their content
    /// with spaces so that offsets, line numbers and file length are preserved. Brace counting
    /// would otherwise be thrown off by the braces inside interpolated JSON literals such as
    /// <c>$"{{\"employeeId\":{id}}}"</c>, of which this codebase has several.
    /// </summary>
    internal static string Sanitize(string source)
    {
        var buffer = new StringBuilder(source);
        var n = source.Length;

        void Blank(int from, int to)
        {
            for (var k = from; k < to && k < n; k++)
                if (buffer[k] != '\n') buffer[k] = ' ';
        }

        int SkipQuoted(int start, bool verbatim)
        {
            var j = start;
            while (j < n)
            {
                if (!verbatim && source[j] == '\\') { j += 2; continue; }
                if (source[j] == '"')
                {
                    if (verbatim && j + 1 < n && source[j + 1] == '"') { j += 2; continue; }
                    return j + 1;
                }
                if (!verbatim && source[j] == '\n') return j;   // unterminated: stop at EOL
                j++;
            }
            return j;
        }

        var i = 0;
        while (i < n)
        {
            var c = source[i];
            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                var j = source.IndexOf('\n', i);
                if (j < 0) j = n;
                Blank(i, j); i = j;
            }
            else if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                var j = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                j = j < 0 ? n : j + 2;
                Blank(i, j); i = j;
            }
            else if (c == '@' && i + 1 < n && source[i + 1] == '"')
            {
                var j = SkipQuoted(i + 2, verbatim: true);
                Blank(i, j); i = j;
            }
            else if (c == '$' && i + 2 < n && source[i + 1] == '@' && source[i + 2] == '"')
            {
                var j = SkipQuoted(i + 3, verbatim: true);
                Blank(i, j); i = j;
            }
            else if (c == '"')
            {
                var j = SkipQuoted(i + 1, verbatim: false);
                Blank(i, j); i = j;
            }
            else if (c == '\'')
            {
                var j = i + 1;
                while (j < n)
                {
                    if (source[j] == '\\') { j += 2; continue; }
                    if (source[j] == '\'') { j++; break; }
                    if (source[j] == '\n') break;
                    j++;
                }
                Blank(i, j); i = j;
            }
            else i++;
        }

        return buffer.ToString();
    }

    internal sealed record TransactionSite(int LineNumber, string LineText, bool InsideExecutionStrategy);

    /// <summary>
    /// Finds every BeginTransaction call in <paramref name="source"/> and reports whether it is
    /// lexically enclosed by an <c>ExecuteAsync(... =&gt; ...)</c> block that a
    /// <c>CreateExecutionStrategy()</c> above it produced.
    /// </summary>
    internal static IReadOnlyList<TransactionSite> ScanTransactionSites(string source)
    {
        var sanitized = Sanitize(source);
        var rawLines = source.Replace("\r\n", "\n").Split('\n');
        var sanLines = sanitized.Replace("\r\n", "\n").Split('\n');

        // Character offset → line index, over the same normalised text the lines came from.
        var flat = sanitized.Replace("\r\n", "\n");
        var lineOf = new int[flat.Length + 1];
        var line = 0;
        for (var k = 0; k < flat.Length; k++)
        {
            lineOf[k] = line;
            if (flat[k] == '\n') line++;
        }
        lineOf[flat.Length] = line;

        var openBraceLines = new Stack<int>();
        var sites = new List<TransactionSite>();

        for (var off = 0; off < flat.Length; off++)
        {
            var c = flat[off];
            if (c == '{') { openBraceLines.Push(lineOf[off]); continue; }
            if (c == '}') { if (openBraceLines.Count > 0) openBraceLines.Pop(); continue; }

            var marker = TransactionMarkers.FirstOrDefault(m =>
                string.CompareOrdinal(flat, off, m, 0, m.Length) == 0);
            if (marker is null) continue;

            var lineIndex = lineOf[off];

            // Enclosed by an ExecuteAsync delegate? The lambda header sits on the line that opens
            // the block or just above it:
            //     await strategy.ExecuteAsync(async () =>
            //     {
            //         await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var enclosed = false;
            foreach (var openLine in openBraceLines)
            {
                var from = Math.Max(0, openLine - 3);
                var header = string.Join("\n", sanLines[from..(openLine + 1)]);
                if (header.Contains("ExecuteAsync(", StringComparison.Ordinal)
                    && header.Contains("=>", StringComparison.Ordinal))
                {
                    enclosed = true;
                    break;
                }
            }

            // ...and the strategy it runs on must actually be an execution strategy.
            var hasStrategy = false;
            for (var k = 0; k < lineIndex && k < sanLines.Length; k++)
            {
                if (sanLines[k].Contains("CreateExecutionStrategy()", StringComparison.Ordinal))
                {
                    hasStrategy = true;
                    break;
                }
            }

            sites.Add(new TransactionSite(
                lineIndex + 1,
                lineIndex < rawLines.Length ? rawLines[lineIndex].Trim() : string.Empty,
                enclosed && hasStrategy));

            off += marker.Length - 1;
        }

        return sites;
    }

    // ── 1. The ratchet ───────────────────────────────────────────────────────────

    /// <summary>
    /// Known, justified exceptions. Empty by design: every one of the fifteen transaction sites in
    /// Zayra.Api runs inside the execution strategy. A new entry needs a comment stating why the
    /// site can never run under a retrying strategy — "it is only reached from tests" is not a
    /// reason, because the production DbContext is the one that is registered with retry.
    /// </summary>
    private static readonly HashSet<string> AllowList = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void BeginTransaction_EverySiteMustRunInsideTheExecutionStrategy()
    {
        var sourceRoot = ResolveSourceRoot();
        var violations = new List<string>();
        var checkedSites = 0;

        foreach (var filePath in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            // bin/obj hold copies of generated and compiled sources; scanning them double-reports.
            var relative = Path.GetRelativePath(sourceRoot, filePath);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;

            foreach (var site in ScanTransactionSites(File.ReadAllText(filePath)))
            {
                checkedSites++;
                if (site.InsideExecutionStrategy) continue;
                var label = $"{relative}:{site.LineNumber}";
                if (AllowList.Contains(label)) continue;
                violations.Add($"  {label} — {site.LineText}");
            }
        }

        // If the scan finds nothing at all, the resolver pointed somewhere harmless and the guard
        // is checking nothing. Fail loudly rather than pass vacuously.
        checkedSites.Should().BeGreaterThan(0,
            "the lint must actually find the production transaction sites; zero means the source "
            + "root resolved to the wrong directory and this guard is inert");

        violations.Should().BeEmpty(
            "Program.cs registers the DbContext with EnableRetryOnFailure, so "
            + "NpgsqlRetryingExecutionStrategy refuses a user-initiated BeginTransaction and throws "
            + "InvalidOperationException before doing any work — the generic handler returns HTTP 400 "
            + "and the endpoint is dead 100% of the time.\n\n"
            + "Wrap the whole unit:\n"
            + "  var strategy = _db.Database.CreateExecutionStrategy();\n"
            + "  await strategy.ExecuteAsync(async () =>\n"
            + "  {\n"
            + "      if (attempt++ > 0) _db.ChangeTracker.Clear();   // a retry must restart from persisted state\n"
            + "      var tx = await _db.Database.BeginTransactionAsync(ct);\n"
            + "      try { /* unit */ await tx.CommitAsync(ct); }\n"
            + "      catch { try { await tx.RollbackAsync(ct); } catch { } throw; }\n"
            + "      finally { await tx.DisposeAsync(); }\n"
            + "  });\n\n"
            + "The delegate may run more than once, so reset per-attempt state: clear the change "
            + "tracker and reload, or a retry inserts duplicates — and rows a SaveChanges marked "
            + "Unchanged before its COMMIT was lost are never rewritten at all.");
    }

    // ── 2. Self-tests: prove the scanner detects, and does not over-detect ────────

    [Fact]
    public void Scanner_PlantedBareTransaction_IsDetected()
    {
        // The exact shape of the four shipped defects: a bare transaction in a method that has no
        // execution strategy anywhere.
        const string planted = """
            public async Task<IActionResult> Commit(CancellationToken ct)
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                _db.Things.Add(new Thing());
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Ok();
            }
            """;

        var sites = ScanTransactionSites(planted);

        sites.Should().ContainSingle("the planted bare transaction must be found");
        sites[0].InsideExecutionStrategy.Should().BeFalse(
            "a transaction with no enclosing CreateExecutionStrategy().ExecuteAsync(...) is exactly the defect");
    }

    [Fact]
    public void Scanner_StrategyNamedOnlyInAComment_DoesNotLaunderABareTransaction()
    {
        // The nastiest false negative a naive text scan would produce: the words are present, the
        // code is not. This is why the scanner blanks comments before deciding.
        const string planted = """
            public async Task Apply(CancellationToken ct)
            {
                // Runs inside Database.CreateExecutionStrategy().ExecuteAsync(...) — or so the comment claims.
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            """;

        var sites = ScanTransactionSites(planted);

        sites.Should().ContainSingle();
        sites[0].InsideExecutionStrategy.Should().BeFalse(
            "a comment mentioning the execution strategy must not satisfy the lint");
    }

    [Fact]
    public void Scanner_CompliantTransaction_IsNotFlagged()
    {
        // The reference fix's shape (AuthService.RefreshAsync). Must not be reported.
        const string compliant = """
            public async Task Apply(CancellationToken ct)
            {
                var strategy = _db.Database.CreateExecutionStrategy();
                var attempt = 0;
                await strategy.ExecuteAsync(async () =>
                {
                    if (attempt++ > 0) _db.ChangeTracker.Clear();
                    var tx = await _db.Database.BeginTransactionAsync(ct);
                    try
                    {
                        await _db.SaveChangesAsync(ct);
                        await tx.CommitAsync(ct);
                    }
                    catch
                    {
                        try { await tx.RollbackAsync(ct); } catch { }
                        throw;
                    }
                    finally
                    {
                        await tx.DisposeAsync();
                    }
                });
            }
            """;

        var sites = ScanTransactionSites(compliant);

        sites.Should().ContainSingle();
        sites[0].InsideExecutionStrategy.Should().BeTrue(
            "a transaction inside CreateExecutionStrategy().ExecuteAsync(...) is the correct pattern "
            + "and must never be reported — a lint that cries wolf on the twelve already-correct "
            + "sites would simply be disabled");
    }

    [Fact]
    public void Scanner_TransactionAfterAnUnrelatedStrategyBlock_IsStillFlagged()
    {
        // A strategy block earlier in the same method must not cover a bare transaction that sits
        // outside it: enclosure is decided by brace depth, not by proximity.
        const string planted = """
            public async Task Apply(CancellationToken ct)
            {
                var strategy = _db.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await _db.SaveChangesAsync(ct);
                });

                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await tx.CommitAsync(ct);
            }
            """;

        var sites = ScanTransactionSites(planted);

        sites.Should().ContainSingle();
        sites[0].InsideExecutionStrategy.Should().BeFalse(
            "the transaction sits after the strategy delegate closed, so it is not a retriable unit");
    }

    [Fact]
    public void Scanner_BracesInsideStringLiterals_DoNotConfuseEnclosureTracking()
    {
        // AuthService writes audit metadata as $"{{\"employeeId\":{id}}}". Counting those braces
        // would unbalance the stack and mis-report every later site in the file.
        const string compliant = """
            public async Task Apply(CancellationToken ct)
            {
                var strategy = _db.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    var metadata = $"{{\"employeeId\":{_id}}}";
                    var verbatim = @"a }} brace { in a verbatim string";
                    var tx = await _db.Database.BeginTransactionAsync(ct);
                    await tx.CommitAsync(ct);
                });
            }
            """;

        var sites = ScanTransactionSites(compliant);

        sites.Should().ContainSingle();
        sites[0].InsideExecutionStrategy.Should().BeTrue(
            "braces inside string literals must be ignored, or the brace stack drifts and the lint "
            + "reports phantom violations");
    }
}
