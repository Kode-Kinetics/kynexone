using System.Reflection;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// Which release and commit this running API was built from, as reported by <c>/health/live</c>'s
/// <c>version</c> and <c>commit</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both come from the build itself. The release image is built with <c>-p:Version=&lt;MAJOR.MINOR.PATCH&gt;</c>
/// (semantic version, implementation prompt WP10), and MSBuild appends <c>+&lt;SourceRevisionId&gt;</c> to the
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

    private static readonly string? InformationalVersion =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static readonly Lazy<string> Current = new(() => Resolve(null, InformationalVersion));

    /// <summary>The commit of the running build, or <see cref="Unknown"/>.</summary>
    public static string Commit => Current.Value;

    /// <summary>The release version (<c>MAJOR.MINOR.PATCH</c>) the build was stamped with; <c>0.0.0-dev</c> for a local build.</summary>
    public static string Version { get; } = ResolveVersion(InformationalVersion);

    /// <summary>The part of the informational version before <c>+&lt;commit&gt;</c>. Pure, exposed for tests.</summary>
    public static string ResolveVersion(string? informationalVersion)
    {
        var value = (informationalVersion ?? string.Empty).Trim();
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];
        return string.IsNullOrWhiteSpace(value) ? "0.0.0-dev" : value;
    }

    /// <summary>Pure resolution, exposed for tests. <paramref name="overrideCommit"/> wins when given.</summary>
    public static string Resolve(string? overrideCommit, string? informationalVersion)
    {
        if (!string.IsNullOrWhiteSpace(overrideCommit)) return overrideCommit.Trim();

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
