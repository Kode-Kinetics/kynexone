using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Operations;

namespace Zayra.Api.Tests;

/// <summary>
/// Regression guards for the release that could neither ship nor roll back (2026-09-21).
///
/// <para>Three independent checks each claimed a protection none of them provided:</para>
/// <list type="number">
/// <item>render.yaml declared fourteen keys <c>sync: false</c> — "set this in the dashboard" —
/// and nothing verified any of them. <c>Proxy__KnownNetworks</c> was unset, the app's proxy guard
/// threw, and the pre-deploy migration job died ~10s in, twice, before reaching the database.</item>
/// <item>Three migrations from 2026-07-13 were hand-written without <c>[Migration]</c>, so EF
/// could not see them. 72 files on disk, 69 visible. <c>dotnet ef database update</c> exited 0
/// having skipped them, for 70 days.</item>
/// <item>The Dockerfile deletes <c>Migrations/</c>, so <c>GetPendingMigrationsAsync()</c> in the
/// deployed image compared the database against an empty set and returned zero pending for every
/// database, forever. <c>/health/ready</c> reported <c>ready</c> with twelve migrations missing.</item>
/// </list>
///
/// <para>Each test below fails on the historical input. The shell gates
/// (<c>scripts/check-migration-visibility.sh</c>, <c>scripts/check_render_env.py</c>) cover the
/// same ground in CI; these are the in-suite half, so a developer who never runs CI still trips
/// over them.</para>
/// </summary>
public class DeployHardeningTests
{
    // ── C. Migration visibility ──────────────────────────────────────────────────

    /// <summary>
    /// THE 70-DAY BUG. EF discovers migrations by scanning the assembly for
    /// <c>[Migration]</c>. A file without it is not a migration as far as any tool we own is
    /// concerned — not <c>database update</c>, not <c>--migrate</c>, not the readiness check.
    /// Measured before the fix: 72 files on disk, 69 discovered.
    /// </summary>
    [Fact]
    public void EveryMigrationFileOnDisk_IsDiscoverableByEfCore()
    {
        var migrationsDir = ResolveMigrationsDirectory();
        Assert.True(migrationsDir is not null,
            "Could not locate the Migrations directory; refusing to report a vacuous pass.");

        var onDisk = Directory.GetFiles(migrationsDir!, "*.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null
                        && !n.EndsWith(".Designer", StringComparison.Ordinal)
                        && !n.Contains("ModelSnapshot", StringComparison.Ordinal))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(onDisk);

        // The same discovery rule EF itself uses: a Migration subclass carrying [Migration].
        var discovered = typeof(ZayraDbContext).Assembly.GetTypes()
            .Select(t => t.GetCustomAttribute<Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Id)
            .ToHashSet(StringComparer.Ordinal);

        var invisible = onDisk.Where(id => !discovered.Contains(id)).ToList();

        Assert.True(invisible.Count == 0,
            $"{invisible.Count} migration file(s) exist on disk but carry no [Migration] attribute, so EF "
            + "will silently skip them and `dotnet ef database update` will still exit 0:\n  "
            + string.Join("\n  ", invisible)
            + "\n\nAdd [DbContextAttribute(typeof(ZayraDbContext))] and [Migration(\"<filename>\")], "
            + "or generate the migration with `dotnet ef migrations add`.");
    }

    /// <summary>
    /// The three specific files, named. If someone strips the attributes again, the test above
    /// says "a migration is invisible"; this one says which incident is repeating.
    /// </summary>
    [Theory]
    [InlineData("20260713061000_AddSalaryStructureEligibilityAndVersioning")]
    [InlineData("20260713062000_BackfillEmployeeChangeApprovalRequests")]
    [InlineData("20260713073000_AddApprovalQueueAccountability")]
    public void The20260713Migrations_AreVisibleToEfCore(string migrationId)
    {
        var discovered = typeof(ZayraDbContext).Assembly.GetTypes()
            .Select(t => t.GetCustomAttribute<Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Id)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(discovered.Contains(migrationId),
            $"{migrationId} is invisible to EF Core again. It was hand-written without "
            + "[Migration] in commit 15148d0 and went unapplied for 70 days.");
    }

    /// <summary>
    /// A <c>[Migration]</c> id that disagrees with its filename is worse than a missing one: EF
    /// records the id in <c>__EFMigrationsHistory</c>, so the file appears applied under a name
    /// nothing on disk matches, and the visibility gate reports a phantom forever.
    /// </summary>
    [Fact]
    public void EveryMigrationAttributeId_MatchesItsFileName()
    {
        var migrationsDir = ResolveMigrationsDirectory();
        Assert.True(migrationsDir is not null, "Could not locate the Migrations directory.");

        var onDisk = Directory.GetFiles(migrationsDir!, "*.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null
                        && !n.EndsWith(".Designer", StringComparison.Ordinal)
                        && !n.Contains("ModelSnapshot", StringComparison.Ordinal))
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

        var discovered = typeof(ZayraDbContext).Assembly.GetTypes()
            .Select(t => t.GetCustomAttribute<Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Id)
            .ToList();

        var phantoms = discovered.Where(id => !onDisk.Contains(id)).ToList();

        Assert.True(phantoms.Count == 0,
            "EF knows migration id(s) that no file on disk is named after — the [Migration] "
            + "argument must be the filename without .cs:\n  " + string.Join("\n  ", phantoms));
    }

    // ── D. The readiness gate must not report a comfortable zero ─────────────────

    /// <summary>
    /// THE NO-OP HEALTH GATE. With no migrations in the assembly and no manifest, the old code
    /// path produced <c>pendingMigrations: 0</c> — indistinguishable from a fully migrated
    /// database. It must now resolve to nothing, which the caller turns into the -1 unknown
    /// sentinel and <c>not_ready</c>.
    /// </summary>
    [Fact]
    public void ExpectedMigrations_IsEmpty_WhenNeitherAssemblyNorManifestKnowsAny()
    {
        var expected = ProductionReadinessEvidence.ResolveExpectedMigrations(Array.Empty<string>());

        // The suite always runs with Migrations/ present, so the manifest is absent here and the
        // fallback is genuinely empty. That emptiness is what makes the caller fail closed.
        Assert.True(expected.Count == 0 || MigrationManifest.IsPresent,
            "With no assembly migrations and no manifest, the expected set must be empty so the "
            + "readiness check reports unknown rather than zero pending.");
    }

    /// <summary>An empty expected set must resolve to <c>not_ready</c>, never <c>ready</c>.</summary>
    [Fact]
    public void ResolveStatus_IsNotReady_ForTheUnknownSentinel()
    {
        Assert.Equal("not_ready", ProductionReadinessEvidence.ResolveStatus(dbHealthy: true, pendingMigrations: -1));
        Assert.Equal("not_ready", ProductionReadinessEvidence.ResolveStatus(true, -1, workersHealthy: true));

        // And the honest positive case still works, so this is not a gate that can only fail.
        Assert.Equal("ready", ProductionReadinessEvidence.ResolveStatus(dbHealthy: true, pendingMigrations: 0));
    }

    /// <summary>
    /// The unmeasured worker fleet must not IMPERSONATE a measured one.
    ///
    /// <para>BuildReadinessAsync only evaluates workers when the database is healthy AND migrations
    /// are in parity; otherwise it substitutes <c>Unavailable</c>. That placeholder used to report
    /// <c>MissingCount = 6</c> with every worker <c>"unavailable"</c> — indistinguishable from a
    /// genuinely dead fleet, and read as exactly that during the 2026-09-23 incident, while the real
    /// cause sat one field away in the same payload. Counts must stay zero and the status must say
    /// plainly that nothing was measured.</para>
    /// </summary>
    [Fact]
    public void UnmeasuredWorkerFleet_ReportsNoCounts_AndSaysItWasNotEvaluated()
    {
        var fleet = WorkerFleetReadiness.Unavailable;

        Assert.False(fleet.Healthy, "an unmeasured fleet must not satisfy readiness");
        Assert.Equal(0, fleet.MissingCount);
        Assert.Equal(0, fleet.HealthyCount);
        Assert.Equal(0, fleet.StartingCount);
        Assert.Equal(0, fleet.StaleCount);
        Assert.Equal(0, fleet.FailedCount);

        Assert.All(fleet.Workers, w => Assert.Equal("not_evaluated", w.Status));
        Assert.DoesNotContain(fleet.Workers, w => w.Status is "missing" or "unavailable");
        Assert.All(fleet.Workers, w => Assert.Null(w.UpdatedAtUtc));
    }

    /// <summary>The assembly's own migrations win when present; that is the un-stripped build.</summary>
    [Fact]
    public void ExpectedMigrations_PrefersTheAssemblyList()
    {
        var expected = ProductionReadinessEvidence.ResolveExpectedMigrations(new[] { "20260101000000_A", "20260102000000_B" });
        Assert.Equal(2, expected.Count);
        Assert.Contains("20260101000000_A", expected);
    }

    /// <summary>
    /// In THIS build Migrations/ is present, so the readiness check has a real, non-trivial list
    /// to diff against the database. If this ever returns nothing, the gate is a no-op again.
    /// </summary>
    [Fact]
    public void ExpectedMigrations_IsSubstantial_InAnUnstrippedBuild()
    {
        using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=u;Password=p").Options);

        var expected = ProductionReadinessEvidence.ResolveExpectedMigrations(db.Database.GetMigrations());

        Assert.True(expected.Count > 60,
            $"Only {expected.Count} migrations are known to this build. The readiness check diffs "
            + "this against __EFMigrationsHistory; a short list silently under-reports pending work.");
    }

    // ── D (cont). The manifest that keeps the stripped image honest ──────────────

    [Fact]
    public void MigrationManifest_ParsesIdsAndIgnoresCommentsAndBlanks()
    {
        var parsed = MigrationManifest.Parse("""
            # generated by Dockerfile

            20260102000000_B
            20260101000000_A
            20260102000000_B
            """);

        Assert.Equal(new[] { "20260101000000_A", "20260102000000_B" }, parsed);
    }

    [Fact]
    public void MigrationManifest_IsEmpty_WhenTheResourceIsAbsent()
    {
        // A build that keeps Migrations/ ships no manifest; the assembly list is used instead.
        Assert.Empty(MigrationManifest.Load(typeof(DeployHardeningTests).Assembly));
    }

    /// <summary>
    /// The manifest only works if the Dockerfile writes it BEFORE deleting the directory, and only
    /// matters if the build refuses to continue with an empty one. Both are single lines that a
    /// future edit could quietly drop, taking the readiness gate back to a permanent zero.
    /// </summary>
    [Fact]
    public void Dockerfile_RecordsTheMigrationManifestBeforeStrippingMigrations()
    {
        var dockerfile = ResolveRepoFile("Dockerfile");
        Assert.True(dockerfile is not null, "Could not locate the root Dockerfile.");
        var text = File.ReadAllText(dockerfile!);

        var manifestAt = text.IndexOf("Migrations.manifest", StringComparison.Ordinal);
        var stripAt = text.IndexOf("rm -rf Migrations", StringComparison.Ordinal);

        if (stripAt < 0) return; // Migrations are no longer stripped: the gate is real by default.

        Assert.True(manifestAt >= 0,
            "The Dockerfile deletes Migrations/ but never records Migrations.manifest. The deployed "
            + "image will report pendingMigrations: 0 for every database, which is how a release was "
            + "promoted against an un-migrated production DB.");
        Assert.True(manifestAt < stripAt,
            "Migrations.manifest must be written BEFORE `rm -rf Migrations` — afterwards there is "
            + "nothing left to enumerate.");
        Assert.Contains("test -s Migrations.manifest", text);
    }

    // ── E. --migrate must not depend on the web host ─────────────────────────────

    [Fact]
    public void MigrateOnlyEntryPoint_ClaimsTheMigrateArgument()
    {
        Assert.True(MigrateOnlyEntryPoint.ShouldHandle(new[] { "--migrate" }));
        Assert.True(MigrateOnlyEntryPoint.ShouldHandle(new[] { "--other", "--migrate" }));
        Assert.False(MigrateOnlyEntryPoint.ShouldHandle(Array.Empty<string>()));
        Assert.False(MigrateOnlyEntryPoint.ShouldHandle(new[] { "--purge-demo" }));
        Assert.False(MigrateOnlyEntryPoint.ShouldHandle(new[] { "--seed-sunday-demo-fixture" }));
    }

    /// <summary>
    /// THE PREVENTION. `--migrate` must be handled before <c>WebApplication.CreateBuilder</c>, so
    /// no web-host guard can stop a migration. The proxy guard at Program.cs:53 is the one that
    /// actually did: it threw on every pre-deploy run while Proxy__KnownNetworks was unset.
    /// </summary>
    [Fact]
    public void Program_HandlesMigrateBeforeBuildingTheWebHost()
    {
        var program = ResolveRepoFile(Path.Combine("backend-dotnet", "Zayra.Api", "Program.cs"));
        Assert.True(program is not null, "Could not locate Program.cs.");
        var text = File.ReadAllText(program!);

        // Anchor on the STATEMENTS, not on any mention. Both names appear in the surrounding
        // comments — an earlier version of this test matched the comment and failed on correct
        // code, which is its own small lesson about checks that look right.
        var migrateAt = text.IndexOf("if (MigrateOnlyEntryPoint.ShouldHandle(args))", StringComparison.Ordinal);
        var builderAt = text.IndexOf("var builder = WebApplication.CreateBuilder(args);", StringComparison.Ordinal);
        var proxyGuardAt = text.IndexOf("GetValue<bool>(\"Proxy:TrustForwardedHeaders\")", StringComparison.Ordinal);

        Assert.True(migrateAt >= 0, "Program.cs no longer short-circuits --migrate.");
        Assert.True(builderAt >= 0);
        Assert.True(migrateAt < builderAt,
            "--migrate must be handled BEFORE WebApplication.CreateBuilder, or every web-host "
            + "fail-fast guard becomes a precondition of migrating the database.");
        Assert.True(proxyGuardAt < 0 || migrateAt < proxyGuardAt,
            "--migrate must be handled before the reverse-proxy trust guard. That guard threw "
            + "InvalidOperationException on the Render pre-deploy job, twice, ~10s in, and the "
            + "migration never reached the database.");
    }

    /// <summary>
    /// The migrate path reads configuration the same way the host does, but nothing it reads may
    /// be mandatory except the connection string.
    /// </summary>
    [Fact]
    public void MigrateOnlyEntryPoint_ResolvesTheConnectionStringAndNothingElse()
    {
        var withConnection = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=db;Database=x;Username=u;Password=p",
                // Deliberately absent: Jwt__SigningKey, SeedAdmin__Password, Storage__*,
                // Proxy__KnownNetworks. None of them is a precondition of migrating.
            }).Build();

        Assert.Equal("Host=db;Database=x;Username=u;Password=p",
            MigrateOnlyEntryPoint.ResolveConnectionString(withConnection));

        // The one real precondition, reported as null so the caller can name the variable.
        Assert.Null(MigrateOnlyEntryPoint.ResolveConnectionString(
            new ConfigurationBuilder().Build()));
        Assert.Null(MigrateOnlyEntryPoint.ResolveConnectionString(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "   " }).Build()));
    }

    /// <summary>
    /// A missing connection string must exit NON-ZERO and name the variable. The old failure was
    /// an InvalidOperationException about proxy headers, which told an operator staring at a dead
    /// migration job nothing about migrations; the pre-deploy job must say what is actually wrong.
    /// </summary>
    [Fact]
    public async Task MigrateOnlyEntryPoint_FailsLoudly_WithNoConnectionString()
    {
        var originalError = Console.Error;
        try
        {
            var captured = new StringWriter();
            Console.SetError(captured);

            var output = new StringWriter();
            var exitCode = await MigrateOnlyEntryPoint.RunAsync(
                Array.Empty<string>(),
                output,
                // Injected empty: the suite's own appsettings.json sits next to the test binary and
                // would otherwise supply a connection string, so this path could never be reached.
                new ConfigurationBuilder().Build());

            Assert.Equal(1, exitCode);
            Assert.Contains("ConnectionStrings__Default", captured.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// The whole point of E: the migrate path must not read any web-host configuration. A config
    /// carrying ONLY a connection string — no JWT key, no seed-admin password, no storage
    /// settings, and the exact proxy misconfiguration that killed the pre-deploy job — must get
    /// past every precondition and fail only at the database connection itself.
    /// </summary>
    [Fact]
    public async Task MigrateOnlyEntryPoint_NeedsNothingBeyondTheConnectionString()
    {
        var originalError = Console.Error;
        try
        {
            var captured = new StringWriter();
            Console.SetError(captured);

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                // THE INCIDENT CONFIG: forwarding on, no trust boundary. Fatal to the web host.
                ["Proxy:TrustForwardedHeaders"] = "true",
                // A deliberately unroutable host, so the run reaches the DB step and stops there.
                ["ConnectionStrings:Default"] =
                    "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1;Command Timeout=1",
            }).Build();

            var exitCode = await MigrateOnlyEntryPoint.RunAsync(Array.Empty<string>(), new StringWriter(), config);

            // Exit 1 from the DB attempt, NOT from a proxy guard. The distinction is the fix.
            Assert.Equal(1, exitCode);
            var stderr = captured.ToString();
            Assert.DoesNotContain("Proxy forwarding is enabled", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("SeedAdmin", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("Jwt", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// THE FOURTH NO-OP, found while fixing the first three. render.yaml's preDeployCommand is
    /// <c>dotnet Zayra.Api.dll --migrate</c>, which runs INSIDE the published image — and that
    /// image has had Migrations/ deleted before <c>dotnet publish</c>, so the classes were never
    /// compiled in. MigrateAsync() against an assembly with zero migrations applies nothing and
    /// exits 0. The declared pre-deploy migration step could never have migrated anything.
    /// It must now fail closed and say so.
    /// </summary>
    [Fact]
    public void MigrateOnlyEntryPoint_FailsClosed_WhenTheBuildHasNoMigrationsCompiledIn()
    {
        var stripped = MigrateOnlyEntryPoint.DescribeStrippedBuildFailure(
            Array.Empty<string>(),
            ImmutableArray.Create("20260101000000_A", "20260102000000_B"));

        Assert.NotNull(stripped);
        Assert.Contains("FAILED", stripped!, StringComparison.Ordinal);
        Assert.Contains("2 migration(s)", stripped, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", stripped, StringComparison.Ordinal);
        // It must point at a path that actually works, not just refuse.
        Assert.Contains("migrate-backend", stripped, StringComparison.Ordinal);

        // No manifest either: still fail, still explain.
        var blind = MigrateOnlyEntryPoint.DescribeStrippedBuildFailure(
            Array.Empty<string>(), ImmutableArray<string>.Empty);
        Assert.NotNull(blind);
        Assert.Contains("No build-time migration manifest", blind!, StringComparison.Ordinal);

        // And a normal build is NOT blocked — otherwise this is a gate that can only fail.
        Assert.Null(MigrateOnlyEntryPoint.DescribeStrippedBuildFailure(
            new[] { "20260101000000_A" }, ImmutableArray<string>.Empty));
    }

    // ── A. Required env vars, and F. comments that must stay true ────────────────

    /// <summary>
    /// The env-var gate parses render.yaml for <c>sync: false</c>. If the blueprint stops
    /// declaring the key that caused the outage, the gate quietly stops checking for it.
    /// </summary>
    [Fact]
    public void RenderYaml_StillDeclaresTheKeyThatCausedTheOutage()
    {
        var renderYaml = ResolveRepoFile("render.yaml");
        Assert.True(renderYaml is not null, "Could not locate render.yaml.");
        var required = ParseSyncFalseKeys(File.ReadAllText(renderYaml!));

        Assert.Contains("Proxy__KnownNetworks", required);
        Assert.True(required.Count >= 14,
            $"render.yaml declares only {required.Count} `sync: false` keys; the gate had 14. "
            + "A key that stops being declared stops being verified.");
    }

    /// <summary>
    /// F. THE DISPROVEN CLAIM. render.yaml asserted that a migration-bearing image deployed
    /// against an un-migrated DB is "NEVER promoted". The strip made that false, and a release
    /// shipped on the strength of it. The words must not come back without the mechanism.
    /// </summary>
    [Fact]
    public void RenderYaml_DoesNotRepeatTheDisprovenPromotionGuarantee()
    {
        var renderYaml = ResolveRepoFile("render.yaml");
        Assert.True(renderYaml is not null);
        var text = File.ReadAllText(renderYaml!);

        Assert.DoesNotContain("is NEVER promoted", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pre-deploy comment used to list the seed-admin and JWT guards as the preconditions of
    /// `--migrate` and omit the proxy guard, which fires first and is the one that actually broke
    /// it. The migrate path no longer runs any of them; the comment must not reintroduce the list.
    /// </summary>
    [Fact]
    public void RenderYaml_DoesNotClaimMigrateRunsTheBuilderTimeGuards()
    {
        var renderYaml = ResolveRepoFile("render.yaml");
        Assert.True(renderYaml is not null);
        var text = File.ReadAllText(renderYaml!);

        Assert.DoesNotContain("the builder-time seed-admin/JWT fail-fast guards", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// G. THE PROPERTIES THE BLUEPRINT DID NOT MENTION. render.yaml declared the plan, the
    /// health-check path and the deploy trigger, so a reviewer could reasonably read it as "this
    /// is the service". It was not. The live service also had a 10 GB disk mounted at /var/data,
    /// one instance, and a region — none of them in the file, so none of them in any diff.
    ///
    /// <para>The disk is the dangerous one in both directions. A blueprint sync reconciles the
    /// service toward this file, and a disk absent from the file could be DETACHED, which
    /// destroys it and everything on it with no undo. Meanwhile keeping it forces every deploy to
    /// stop the old instance before starting the new one — Render will not overlap instances that
    /// share a disk — so each release has a hard 502 window.</para>
    ///
    /// <para>These values are a copy of the live service, not a proposal. If one legitimately
    /// changes, change it here and in the dashboard together.</para>
    /// </summary>
    [Fact]
    public void RenderYaml_DeclaresTheInfrastructureTheLiveServiceActuallyHas()
    {
        var renderYaml = ResolveRepoFile("render.yaml");
        Assert.True(renderYaml is not null, "Could not locate render.yaml.");
        var text = File.ReadAllText(renderYaml!);

        Assert.True(Regex.IsMatch(text, @"(?m)^\s*region:\s*oregon\s*$"),
            "render.yaml must declare `region: oregon` — the region the service actually runs in, "
            + "fixed at creation and NOT a GCC/KSA jurisdiction. Undeclared, nothing in the "
            + "repository states where personal data is processed.");

        Assert.True(Regex.IsMatch(text, @"(?m)^\s*numInstances:\s*1\s*$"),
            "render.yaml must declare `numInstances: 1` so a blueprint sync cannot silently "
            + "rescale the service.");

        Assert.True(Regex.IsMatch(text, @"(?m)^\s*disk:\s*$"),
            "render.yaml must declare the disk. The live service mounts one; a blueprint that "
            + "does not mention it can detach it, and detaching a Render disk destroys it.");
        Assert.True(Regex.IsMatch(text, @"(?m)^\s*mountPath:\s*/var/data\s*$"),
            "The declared disk must keep the live mountPath /var/data.");
        Assert.True(Regex.IsMatch(text, @"(?m)^\s*sizeGB:\s*10\s*$"),
            "The declared disk must keep the live size of 10 GB. Render can grow a disk but "
            + "cannot shrink one, so a smaller number here is a sync that fails, not a resize.");
    }

    /// <summary>
    /// H. THE GATE THAT MUST STAY UNPROTECTED IN ORDER TO WORK. secret-scope-gate asks GitHub for
    /// the production credentials from a job with no <c>environment:</c>; if GitHub hands one
    /// over, that secret is readable without any approval. Adding <c>environment: production</c>
    /// to this job — which looks like an obvious hardening — would make every probe come back
    /// empty and turn the gate into a permanent, meaningless pass.
    /// </summary>
    [Fact]
    public void Ci_SecretScopeGateRunsWithoutAnEnvironment()
    {
        var ci = ResolveRepoFile(Path.Combine(".github", "workflows", "ci.yml"));
        Assert.True(ci is not null, "Could not locate .github/workflows/ci.yml.");
        var text = File.ReadAllText(ci!);

        Assert.Contains("check_secret_scope.py", text, StringComparison.Ordinal);

        var job = ExtractJobBlock(text, "secret-scope-gate");
        Assert.True(job is not null, "ci.yml no longer defines a `secret-scope-gate` job.");
        Assert.False(Regex.IsMatch(job!, @"(?m)^\s{4}environment:"),
            "secret-scope-gate must NOT declare `environment:`. Its entire measurement is what an "
            + "UNPROTECTED job can read; protecting it would hide the answer and pass forever.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Text of one top-level job in a workflow file, or null. Jobs are indented 2 spaces.</summary>
    private static string? ExtractJobBlock(string workflow, string jobName)
    {
        var lines = workflow.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("  " + jobName + ":", StringComparison.Ordinal));
        if (start < 0) return null;

        var end = lines.Length;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"^  [A-Za-z0-9_-]+:")) { end = i; break; }
        }
        return string.Join('\n', lines[start..end]);
    }

    /// <summary>Mirrors scripts/check_render_env.py's parse_required_keys, incl. inline comments.</summary>
    private static List<string> ParseSyncFalseKeys(string yaml)
    {
        var required = new List<string>();
        string? pending = null;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            line = Regex.Replace(line, @"\s+#.*$", string.Empty).Trim();
            if (line.Length == 0) continue;

            var keyMatch = Regex.Match(line, @"^-?\s*key:\s*(\S+)$");
            if (keyMatch.Success) { pending = keyMatch.Groups[1].Value.Trim('"', '\''); continue; }
            if (pending is null) continue;

            if (Regex.IsMatch(line, @"^sync:\s*false$"))
            {
                if (!required.Contains(pending)) required.Add(pending);
                pending = null;
            }
            else if (Regex.IsMatch(line, @"^(value|fromService|fromGroup|fromDatabase|generateValue):"))
            {
                pending = null;
            }
        }
        return required;
    }

    /// <summary>Walk up from the test binary to the repo root. Returns null rather than passing vacuously.</summary>
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

    private static string? ResolveMigrationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "Zayra.Api", "Migrations");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
