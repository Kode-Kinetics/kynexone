using System.Collections.Immutable;
using System.Reflection;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// The list of migration ids this build was compiled FROM, independent of whether the migration
/// classes survived into the runtime image.
/// </summary>
/// <remarks>
/// <para>
/// The production Dockerfile deletes <c>Migrations/</c> before publishing, because the ~70
/// <c>.Designer.cs</c> files embed a full model snapshot each (~1.4M lines of near-duplicate
/// model-builder code) and compiling them exceeded Render's 8 GB builder. That trade is still
/// worth making. What was NOT acceptable is what it did to the readiness gate:
/// <c>GetPendingMigrationsAsync()</c> compares the DB's history against the migrations found in
/// the assembly, and an assembly with zero migrations yields zero pending — for every database,
/// forever. <c>/health/ready</c> therefore reported <c>ready</c> with twelve migrations missing,
/// and render.yaml's promise that "a migration-bearing image deployed against an un-migrated DB is
/// NEVER promoted" was false for as long as the strip existed.
/// </para>
/// <para>
/// The manifest closes that hole without reintroducing the memory cost. The Docker build writes
/// the migration ids to a plain text file <em>before</em> deleting the directory, and that file is
/// embedded as a resource — a few KB of strings instead of a few hundred MB of C#. The readiness
/// check then has a real list of expected migrations to diff the database against even though the
/// classes are gone. The ids come from the filenames, so the manifest also covers migrations that
/// EF itself cannot see (the 2026-07-13 attribute-less three) — a second, independent backstop.
/// </para>
/// <para>
/// When the resource is absent — local runs, the test suite, CI, any build that keeps
/// <c>Migrations/</c> — this is empty and callers fall back to the assembly's own list. The one
/// state that must never be silently tolerated is BOTH being empty; see
/// <see cref="ProductionReadinessEvidence"/>, which fails closed there.
/// </para>
/// </remarks>
public static class MigrationManifest
{
    /// <summary>Resource name written by the Docker build; see Zayra.Api.csproj and ./Dockerfile.</summary>
    public const string ResourceName = "Zayra.Api.Migrations.manifest";

    private static readonly Lazy<ImmutableArray<string>> LazyIds = new(() => Load(typeof(MigrationManifest).Assembly));

    /// <summary>Migration ids recorded at build time, or empty when the build kept Migrations/.</summary>
    public static ImmutableArray<string> Ids => LazyIds.Value;

    /// <summary>True when this build shipped a manifest (i.e. the image had its migrations stripped).</summary>
    public static bool IsPresent => !Ids.IsEmpty;

    internal static ImmutableArray<string> Load(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return ImmutableArray<string>.Empty;

        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        return Parse(text);
    }

    /// <summary>One migration id per line; blanks and '#' comments ignored. Order is not significant.</summary>
    internal static ImmutableArray<string> Parse(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToImmutableArray();
}
