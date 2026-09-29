using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// THE DEFECT THESE PIN IS AN ABSENCE, NOT A WRONG ANSWER.
///
/// Before this branch, KsaNationalizationTracker was fully implemented, registered
/// in DI four times over (Program.cs :502 / :514 / :526 / :548), covered by pack
/// tests — and called by nothing. A customer could not reach a Saudization number
/// through any HTTP route, and the report catalogue had no Saudization entry at all.
///
/// Both assertions below are written against surfaces that exist on develop, so they
/// can be — and were — run in both directions:
///
///   develop @ 188620b
///     ReportCatalogue_MustOfferASaudizationReport                       FAILED
///     NitaqatBanding_MustBeReachableFromProductionCodeOutsideTheCountryPack  FAILED
///     → "Failed!  - Failed: 2, Passed: 0, Skipped: 0, Total: 2"
///
///   feat/ksa-nitaqat
///     → "Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2"
///
/// The second is a source scan rather than a behavioural assertion on purpose: the
/// thing that went wrong was not a bad calculation, it was a calculation nobody had
/// wired to a caller, and only a scan for callers catches that class of defect.
/// </summary>
public class NitaqatExposureTests
{
    /// <summary>
    /// The HR review recorded that there is no Saudization report at all despite the
    /// tracker existing. A Nitaqat band is the single most distinctly Saudi number an
    /// HRMS can produce; it belongs in the catalogue.
    /// </summary>
    [Fact]
    public void ReportCatalogue_MustOfferASaudizationReport()
    {
        var path = RepoFile("backend-dotnet/Zayra.Api/Controllers/Reports/ReportsController.cs");
        var catalogSource = File.ReadAllText(path);

        var start = catalogSource.IndexOf("public IActionResult GetCatalog()", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the catalogue lives in GetCatalog()");
        var end = catalogSource.IndexOf("return Ok(catalog);", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);

        var catalog = catalogSource[start..end];

        catalog.Should().Contain("saudization",
            "the report catalogue must offer a Saudization / Nitaqat report — a Saudi HR director "
            + "looks for it first, and the product computed a band internally while exposing none");

        // And the key must actually resolve, or the catalogue entry 404s at run time.
        var switchArm = catalogSource.IndexOf("\"compliance.saudization\" =>", StringComparison.Ordinal);
        switchArm.Should().BeGreaterThan(-1,
            "a catalogue key with no arm in ExecuteReportDataAsync returns NotFound when run");
    }

    /// <summary>
    /// A registered service with no caller is not a feature. This scan fails if the
    /// only places that know how to band an establishment are the country pack itself,
    /// its DI registrations and the test suite.
    /// </summary>
    [Fact]
    public void NitaqatBanding_MustBeReachableFromProductionCodeOutsideTheCountryPack()
    {
        var apiRoot = RepoDir("backend-dotnet/Zayra.Api");

        var callers = Directory
            .EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                     // The pack itself and the DI wiring are not callers.
                     && !f.Contains($"CountryPack{Path.DirectorySeparatorChar}")
                     && !f.EndsWith("Program.cs", StringComparison.Ordinal))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                var isController = f.Contains($"Controllers{Path.DirectorySeparatorChar}");
                var mentionsBanding =
                    text.Contains("Nitaqat", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Saudization", StringComparison.OrdinalIgnoreCase);
                var actuallyComputes =
                    text.Contains("GetStandingAsync", StringComparison.Ordinal)
                    || text.Contains("GetStandingAndRecordAsync", StringComparison.Ordinal)
                    || text.Contains("ResolveNationalizationTracker", StringComparison.Ordinal);
                return mentionsBanding && actuallyComputes && (isController || text.Contains("Service"));
            })
            .Select(f => Path.GetRelativePath(apiRoot, f))
            .OrderBy(x => x)
            .ToList();

        callers.Should().NotBeEmpty(
            "Nitaqat banding must be reachable from an HTTP route or an application service. "
            + "A fully implemented, four-times-DI-registered tracker that nothing calls is dead "
            + "code wearing a feature's clothes.");

        callers.Should().Contain(f => f.Contains("Controller"),
            "a customer reaches it over HTTP or not at all");
    }

    // ── Repo location ─────────────────────────────────────────────────────────

    private static string RepoFile(string relative)
    {
        var p = Path.Combine(RepoRoot(), relative);
        File.Exists(p).Should().BeTrue($"expected {relative} under the repository root");
        return p;
    }

    private static string RepoDir(string relative)
    {
        var p = Path.Combine(RepoRoot(), relative);
        Directory.Exists(p).Should().BeTrue($"expected {relative} under the repository root");
        return p;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend-dotnet")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must be able to find the repository root");
        return dir!.FullName;
    }
}
