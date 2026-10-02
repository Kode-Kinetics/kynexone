using System.Reflection;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// Which commit this running API was built from, as reported by <c>/health/live</c>'s <c>commit</c>.
/// </summary>
/// <remarks>
/// <para>
/// On Render, <c>RENDER_GIT_COMMIT</c> is injected into the running instance and stays the first
/// source, unchanged — the deploy job verifies a release by it. Everywhere else the field used to read
/// <c>"local"</c>, so nothing outside Render could tell a fresh build from a stale image.
/// </para>
/// <para>
/// The second source is the build itself: MSBuild appends <c>+&lt;SourceRevisionId&gt;</c> to the
/// assembly's informational version, and CI passes <c>-p:SourceRevisionId=&lt;sha&gt;</c> (the compose
/// Dockerfile forwards <c>BUILD_COMMIT</c> the same way). A value baked into the assembly cannot be
/// claimed by whoever launches an old image, which is the point: the e2e preflight
/// (<c>frontend/e2e/preflight</c>) refuses to run a suite against an API that is not the build under
/// test. Register item F07.
/// </para>
/// </remarks>
public static class BuildInfo
{
    public const string Unknown = "local";

    private static readonly Lazy<string> Current = new(() => Resolve(
        Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT"),
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion));

    /// <summary>The commit of the running build, or <see cref="Unknown"/>.</summary>
    public static string Commit => Current.Value;

    /// <summary>Pure resolution, exposed for tests.</summary>
    public static string Resolve(string? renderCommit, string? informationalVersion)
    {
        if (!string.IsNullOrWhiteSpace(renderCommit)) return renderCommit.Trim();

        var plus = informationalVersion?.IndexOf('+') ?? -1;
        if (plus >= 0)
        {
            var revision = informationalVersion![(plus + 1)..].Trim();
            // Only a real git object id is a commit; anything else (empty, a label) is not evidence.
            if (revision.Length is >= 7 and <= 64 && revision.All(Uri.IsHexDigit)) return revision.ToLowerInvariant();
        }
        return Unknown;
    }
}
