using System.Text.RegularExpressions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Owner's rule: every tenant, user, test account and demo account is created ONLY through the
/// platform admin. No data may enter the database through a seeder, fixture, demo runner or
/// boot-time side door. Reference data defined in code (permissions, statutory rules, pricing,
/// GL/pay-component defaults) and per-tenant provisioning defaults are the only exceptions —
/// see docs/DATA_ENTRY_PATHS.md.
///
/// <para>Test projects may still build their own fixtures; this only inspects the Zayra.Api
/// assembly and its Program.cs.</para>
/// </summary>
public class NoSideDoorDataTests
{
    private static readonly Regex SideDoorName =
        new(@"(Demo|Fixture|Sample)\w*(Seeder|Runner)$", RegexOptions.Compiled);

    private static readonly Regex SideDoorReference =
        new(@"\b\w*(Demo|Fixture|Sample)\w*(Seeder|Runner)\b", RegexOptions.Compiled);

    [Fact]
    public void ApiAssembly_ContainsNoDemoFixtureOrSampleSeederOrRunner()
    {
        var offenders = typeof(Program).Assembly
            .GetTypes()
            .Where(t => !t.Name.Contains('<'))                 // compiler-generated closures
            .Where(t => SideDoorName.IsMatch(StripGenericArity(t.Name)))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Demo/fixture/sample seeders and runners are banned from Zayra.Api — tenants and users are " +
            "created only through the platform admin (docs/DATA_ENTRY_PATHS.md). Found: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void ProgramCs_ReferencesNoDemoSeederRunnerFlagOrEnvVar()
    {
        var programPath = ResolveProgramCs();
        Assert.True(programPath is not null, "Could not locate Zayra.Api/Program.cs from the test output directory.");

        var source = File.ReadAllText(programPath!);
        var offenders = SideDoorReference.Matches(source).Select(m => m.Value).ToList();
        foreach (var banned in new[]
                 {
                     "SEED_DEMO_DATA", "SEED_ENTERPRISE_TEST_DATA", "SeedDemoData",
                     "--purge-demo", "--seed-sunday-demo-fixture", "EnsureCreated",
                 })
        {
            if (source.Contains(banned, StringComparison.Ordinal)) offenders.Add(banned);
        }

        Assert.True(offenders.Count == 0,
            "Program.cs must not wire a demo/fixture data path or create the schema outside migrations. Found: " +
            string.Join(", ", offenders.Distinct()));
    }

    private static string StripGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    private static string? ResolveProgramCs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Zayra.Api", "Program.cs");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
