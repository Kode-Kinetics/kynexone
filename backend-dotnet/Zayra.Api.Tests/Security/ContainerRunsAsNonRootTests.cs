using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Both API images (the root <c>Dockerfile</c> Render builds, and the compose/CI one under
/// <c>backend-dotnet/Zayra.Api</c>) used to run the process as root. They now drop to the base
/// image's unprivileged <c>app</c> user. This pins that the switch happens in the FINAL stage (a
/// <c>USER</c> in the build stage changes nothing at runtime), never names root, and that the one
/// writable path under /app is created before the switch.
/// </summary>
public sealed class ContainerRunsAsNonRootTests
{
    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("backend-dotnet/Zayra.Api/Dockerfile")]
    public void FinalStage_DropsToANonRootUserBeforeTheEntrypoint(string relativePath)
    {
        var path = ResolveRepoFile(relativePath);
        if (path is null && SkipOrFailWhenSourceMissing(relativePath)) return;

        var lines = File.ReadAllLines(path!)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        var finalFrom = lines.FindLastIndex(l => l.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase));
        finalFrom.Should().BeGreaterThanOrEqualTo(0);
        var finalStage = lines.Skip(finalFrom).ToList();

        var user = finalStage.FindLastIndex(l => l.StartsWith("USER ", StringComparison.OrdinalIgnoreCase));
        var entrypoint = finalStage.FindIndex(l => l.StartsWith("ENTRYPOINT", StringComparison.OrdinalIgnoreCase));
        user.Should().BeGreaterThan(0, $"{relativePath}: the runtime stage must set a USER");
        entrypoint.Should().BeGreaterThan(user, $"{relativePath}: USER must precede the ENTRYPOINT");

        var who = finalStage[user]["USER ".Length..].Trim();
        new[] { "root", "0", "0:0", "root:root" }.Should().NotContain(who);
        who.Should().Be("$APP_UID", "the aspnet base image's unprivileged uid, not a hard-coded guess");

        var storage = finalStage.FindIndex(l => l.Contains("/app/storage", StringComparison.Ordinal)
                                                && l.Contains("chown", StringComparison.Ordinal));
        storage.Should().BeInRange(0, user - 1,
            "the writable storage directory must be created and handed over while still root");
    }

    /// <summary>
    /// Skipping is only acceptable on a developer layout where the source tree is not beside the
    /// binaries. Under CI the tree is always there, so not finding it means the guard silently
    /// stopped guarding — that must fail.
    /// </summary>
    private static bool SkipOrFailWhenSourceMissing(string what)
    {
        var ci = Environment.GetEnvironmentVariable("CI");
        if (string.Equals(ci, "true", StringComparison.OrdinalIgnoreCase) || ci == "1"
            || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            throw new Xunit.Sdk.XunitException($"{what} was not found from {AppContext.BaseDirectory} under CI; the guard would pass vacuously.");
        return true;
    }

    private static string? ResolveRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
