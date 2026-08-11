using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Wave 0 / G5 — startup-contract lint.
///
/// These guard two invariants that have no runtime test coverage because they are
/// properties of composition, not of behaviour. Both exist because of a real incident:
/// <c>NotificationService</c>'s constructor changed, and nothing — not one of 1,600
/// tests — noticed, because no code path resolved the graph until a request arrived.
///
///   1. The API must validate its DI container AT BOOT (ValidateOnBuild + ValidateScopes).
///      ValidateOnBuild turns a missing registration into a failed deploy instead of a
///      production 500. ValidateScopes catches a Singleton capturing a Scoped DbContext —
///      a pooled context shared across threads, and the classic source of "random"
///      concurrency corruption. Deleting either line silently removes the guard, so this
///      test fails if they are not present.
///
///   2. Tests must not hand-construct <c>NotificationService</c>. Every direct
///      construction is a copy of the composition root that drifts the moment the real
///      constructor changes — which is exactly what broke the build. The single approved
///      seam is <c>TestNotifications.For(db)</c>.
/// </summary>
public class StartupContractLintTests
{
    private static string? ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) return null;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? ResolveTestsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) return null;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api.Tests");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    public void Program_MustValidateDiContainerAtBoot()
    {
        var apiRoot = ResolveApiRoot();
        // D14: a guard that SKIPS when it cannot find the source is a false negative, not a safe
        // default — a CI layout change would silently disable every isolation lint while still
        // reporting green. Throwing is the only honest behaviour for a control, and it also
        // narrows the nullable so the scan below cannot be handed a null root.
        apiRoot = apiRoot ?? throw new InvalidOperationException(
            "The Zayra.Api source root could not be resolved, so this guard would check NOTHING. "
            + "Treat an unresolvable path as a build failure, never a skip.");

        var program = File.ReadAllText(Path.Combine(apiRoot, "Program.cs"));

        // Whitespace-tolerant: these lines are commonly alignment-padded (`ValidateScopes  = true`).
        program.Should().MatchRegex(@"ValidateOnBuild\s*=\s*true",
            "a missing DI registration must fail the deploy, not the first production request that needs it");
        program.Should().MatchRegex(@"ValidateScopes\s*=\s*true",
            "a Singleton capturing a Scoped DbContext must fail at boot, not corrupt data under concurrency");
    }

    [Fact]
    public void Tests_MustNotHandConstructNotificationService()
    {
        var testsRoot = ResolveTestsRoot();
        // D14: a guard that SKIPS when it cannot find the source is a false negative, not a safe
        // default — a CI layout change would silently disable every isolation lint while still
        // reporting green. Throwing is the only honest behaviour for a control, and it also
        // narrows the nullable so the scan below cannot be handed a null root.
        testsRoot = testsRoot ?? throw new InvalidOperationException(
            "The Zayra.Api.Tests source root could not be resolved, so this guard would check NOTHING. "
            + "Treat an unresolvable path as a build failure, never a skip.");

        // Assembled at runtime so this lint does not match its own source text.
        var forbidden = "new " + nameof(Zayra.Api.Infrastructure.Notifications.NotificationService) + "(";

        var offenders = Directory
            .EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            // The seam itself is the one legitimate construction site.
            .Where(f => !f.EndsWith("TestNotificationService.cs", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line))
                .Where(x => x.Text.Contains(forbidden, StringComparison.Ordinal)))
            .Select(x => $"  {x.File}:{x.Line}")
            .ToList();

        offenders.Should().BeEmpty(
            "hand-constructing NotificationService duplicates the composition root and silently rots when its " +
            "constructor changes (it already broke the whole test project once). Use TestNotifications.For(db), " +
            "or resolve INotificationService from a real container.");
    }
}
