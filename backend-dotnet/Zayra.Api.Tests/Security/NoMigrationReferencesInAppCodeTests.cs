using System.Text.RegularExpressions;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The production Dockerfile deletes Migrations/ before `dotnet publish` (the shipped image carries only a
/// Migrations.manifest; CI applies migrations). Any application code that names a migration type therefore
/// compiles in every test lane yet breaks the production build — exactly what happened on 2026-10-11, when
/// GlJournalExportsController read an index name from AddErpCodesAndLiveExportIndex and the Render build of
/// #223 failed with CS0103. Shared names belong in app code (e.g. Data/DatabaseIndexNames.cs) that the
/// migration references, never the other way round.
/// </summary>
public class NoMigrationReferencesInAppCodeTests
{
    private static readonly Regex MigrationReference = new(
        @"\busing\s+Zayra\.Api\.Migrations\b|\bZayra\.Api\.Migrations\.[A-Z]|(?<![\w.])Migrations\.[A-Z][A-Za-z0-9_]*\.[A-Z]",
        RegexOptions.Compiled);

    [Fact]
    public void NoApplicationSourceOutsideMigrations_ReferencesAMigrationType()
    {
        var apiRoot = ResolveApiRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("Migrations/", StringComparison.Ordinal)
                || relative.Contains("obj/", StringComparison.Ordinal)
                || relative.Contains("bin/", StringComparison.Ordinal)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("///", StringComparison.Ordinal)) continue;
                if (MigrationReference.IsMatch(lines[i])) offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }
        offenders.Should().BeEmpty(
            "the production image is compiled WITHOUT Migrations/, so app code that names a migration type breaks the Render build");
    }

    [Theory]
    [InlineData("public const string X = Migrations.AddErpCodesAndLiveExportIndex.LiveScopeIndexName;", true)]
    [InlineData("ConstraintName: Zayra.Api.Migrations.AddErpCodesAndLiveExportIndex.ErpCompanyCodeIndexName,", true)]
    [InlineData("using Zayra.Api.Migrations;", true)]
    [InlineData("var fromAssembly = assemblyMigrations.ToList();", false)]
    [InlineData("if (compiledInMigrations.Count > 0) return null;", false)]
    [InlineData("public const string ResourceName = \"Zayra.Api.Migrations.manifest\";", false)]
    public void ThePatternCatchesTheRealShapes_AndNotLookalikes(string line, bool flagged)
        => MigrationReference.IsMatch(line).Should().Be(flagged);

    private static string ResolveApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) break;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException($"Could not locate the Zayra.Api source root from {AppContext.BaseDirectory}.");
    }
}
