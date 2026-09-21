using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests;

/// <summary>
/// D3 — a CONSUMER guard for the retention lifecycle columns, in the same idiom as
/// <c>OrphanEntityRatchetTests</c>: this repository's structural defect is a value that is written and
/// never read, and <c>Employee.RetentionUntilUtc</c> was the purest instance of it. The product stamped
/// a retention deadline on every deleted employee, displayed it in a list, and had nothing anywhere that
/// acted on it — so the deadline passed and the passport numbers stayed.
///
/// <para><b>WHAT "CONSUMED" MEANS HERE, and why the bar is where it is.</b> A reference from the
/// controller that WRITES the column proves nothing: <c>EmployeesController</c> both sets
/// <c>RetentionUntilUtc</c> and projects it into a DTO, and neither makes the deadline enforceable. The
/// guard therefore requires a reference from OUTSIDE <c>Controllers/</c> — a background or service path,
/// which is the only kind of code that can act on a date when it arrives. That is exactly the line the
/// old behaviour failed and the new behaviour passes.</para>
///
/// <para>This test deliberately references no retention TYPE, only source text, so it compiles and runs
/// unchanged against the branch point — which is what makes its "fails before, passes after" meaningful
/// rather than tautological.</para>
/// </summary>
public sealed class RetentionColumnConsumerTests
{
    /// <summary>
    /// Columns that exist only to drive a retention decision. Each must be read by something that is not
    /// the write path.
    /// </summary>
    private static readonly string[] RetentionLifecycleColumns =
    [
        "RetentionUntilUtc",
        "RedactedAtUtc",
    ];

    [Fact]
    public void EveryRetentionLifecycleColumnHasAnEnforcementConsumerOutsideTheControllerThatWritesIt()
    {
        var apiRoot = ResolveApiRoot()
            ?? throw new InvalidOperationException(
                "The Zayra.Api source root could not be resolved, so this guard would check NOTHING. "
                + "Treat an unresolvable path as a build failure, never a skip.");

        var sources = Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path: Relative(apiRoot, f), Text: StripComments(File.ReadAllText(f))))
            .ToList();

        // Anti-vacuous-pass guard: a wrong root must fail, never report green on an empty scan.
        sources.Should().HaveCountGreaterThan(200,
            "the scan must have found the real source tree, not an empty or wrong directory");

        var violations = new List<string>();
        foreach (var column in RetentionLifecycleColumns)
        {
            // EF's per-migration Designer snapshots each mention every column and say nothing about
            // whether anything reads it; they would drown the failure message in 40 irrelevant paths.
            var referencing = sources
                .Where(s => s.Text.Contains(column, StringComparison.Ordinal))
                .Select(s => s.Path)
                .Where(p => !p.StartsWith("Migrations/", StringComparison.Ordinal))
                .ToList();

            var enforcement = referencing
                .Where(p => !p.StartsWith("Models/", StringComparison.Ordinal)
                            && !p.StartsWith("Data/", StringComparison.Ordinal)
                            && !p.StartsWith("Migrations/", StringComparison.Ordinal)
                            && !p.StartsWith("Controllers/", StringComparison.Ordinal))
                .ToList();

            if (enforcement.Count == 0)
                violations.Add(
                    $"  Employee.{column} is referenced by [{(referencing.Count == 0 ? "nothing at all" : string.Join(", ", referencing))}] "
                    + "and by no enforcement path outside Controllers/. A retention column that only the "
                    + "write path touches is a deadline nothing acts on.");
        }

        violations.Should().BeEmpty(
            "a stored retention deadline with no consumer is the defect this codebase keeps reproducing: "
            + "the model, the migration, the write and the display all ship, and the one line that makes "
            + "the value mean something never does.\n\n" + string.Join("\n", violations));
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>A name mentioned only in a comment is not a consumer.</summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        source = Regex.Replace(source, @"^[ \t]*///?.*$", " ", RegexOptions.Multiline);
        return Regex.Replace(source, @"(?<=[^:]|^)//(?!/).*$", " ", RegexOptions.Multiline);
    }

    private static string? ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) return null;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
