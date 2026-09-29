using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// The <c>--migrate</c> path. Applies EF Core migrations and exits, without constructing the web
/// host.
/// </summary>
/// <remarks>
/// <para>
/// Applying a migration needs one thing: a connection string. It used to need far more, because
/// <c>--migrate</c> was handled near the END of Program.cs — after
/// <c>WebApplication.CreateBuilder(args)</c> and the entire service graph had been built. Every
/// fail-fast guard in that graph was therefore a precondition of migrating, including several with
/// nothing to do with the database.
/// </para>
/// <para>
/// That is not hypothetical. The Render pre-deploy job <c>dotnet Zayra.Api.dll --migrate</c> died
/// ~10s in, twice, at the reverse-proxy guard near the top of Program.cs:
/// <c>Proxy__TrustForwardedHeaders</c> was "true" while both trust keys were unset, so the process
/// threw before it ever opened a socket to the database. A proxy misconfiguration blocked a schema
/// migration, and with it the deploy and the rollback. The guard itself is correct — a web host
/// that trusts forwarded headers without a trust boundary is a spoofing risk — it simply has no
/// business gating a database migration.
/// </para>
/// <para>
/// So this path reads configuration, resolves the connection string, migrates, and returns. No
/// proxy handling, no JWT validation, no seed-admin guard, no storage provider, no CORS, no
/// authentication, no HTTP server. Fewer preconditions means fewer ways for the one operation that
/// must work during an incident to fail for an unrelated reason.
/// </para>
/// </remarks>
public static class MigrateOnlyEntryPoint
{
    public const string Argument = "--migrate";

    /// <summary>True when the process was invoked as a migration job.</summary>
    public static bool ShouldHandle(string[] args) => args.Contains(Argument, StringComparer.Ordinal);

    /// <summary>
    /// The same configuration sources the web host uses, minus anything that could fail: JSON
    /// files are optional and environment variables win. On Render, ConnectionStrings__Default is
    /// an environment variable, so this resolves without touching the filesystem at all.
    /// </summary>
    public static IConfiguration BuildConfiguration(string? contentRoot = null)
    {
        var root = contentRoot ?? AppContext.BaseDirectory;
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";

        return new ConfigurationBuilder()
            .SetBasePath(root)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }

    /// <summary>
    /// The one genuine precondition. Returns null when unset so the caller can fail with a message
    /// that names the variable, rather than an Npgsql parse error.
    /// </summary>
    public static string? ResolveConnectionString(IConfiguration configuration)
    {
        var value = configuration.GetConnectionString("Default");
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Returns an error message when this build cannot apply migrations at all, or null when it
    /// can. FAIL CLOSED: `--migrate` must never exit 0 having applied nothing because it had
    /// nothing to apply.
    /// </summary>
    /// <remarks>
    /// The production Dockerfile deletes Migrations/ BEFORE `dotnet publish`, so the migration
    /// classes are never compiled into the shipped Zayra.Api.dll. Running
    /// `dotnet Zayra.Api.dll --migrate` inside that image calls MigrateAsync() against an assembly
    /// that knows of zero migrations: it applies nothing and exits 0. render.yaml declares exactly
    /// that command as its preDeployCommand, so the pre-deploy "migration step" could never have
    /// migrated anything — the same empty-set no-op that made /health/ready report a permanent
    /// pendingMigrations: 0. An operator reaching for --migrate during an incident must be told
    /// that, not handed a green exit. The embedded manifest lets the message say precisely how
    /// many migrations this build knows about but cannot apply.
    /// </remarks>
    internal static string? DescribeStrippedBuildFailure(
        IReadOnlyCollection<string> compiledInMigrations,
        System.Collections.Immutable.ImmutableArray<string> manifestIds)
    {
        if (compiledInMigrations.Count > 0) return null;

        return "[--migrate] FAILED: this build has NO migrations compiled into it, so it cannot "
            + "apply any. Nothing was changed. "
            + (manifestIds.IsEmpty
                ? "No build-time migration manifest was found either."
                : $"The build-time manifest records {manifestIds.Length} migration(s) that were "
                  + "stripped from this image (./Dockerfile `rm -rf Migrations`, which keeps the "
                  + "Render builder under its memory limit).")
            + " Apply migrations from a full source checkout instead: the CI `migrate-backend` job, "
            + "or `dotnet ef database update --project backend-dotnet/Zayra.Api/Zayra.Api.csproj`.";
    }

    /// <summary>Builds options for a migration-only context: the provider, and nothing else.</summary>
    public static DbContextOptions<ZayraDbContext> BuildOptions(string connectionString)
        => new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null))
            .Options;

    /// <summary>
    /// Applies pending migrations. Returns a process exit code: 0 success, 1 failure.
    /// </summary>
    /// <param name="configuration">
    /// Injected only by tests, so the "no connection string" path can be exercised without an
    /// ambient appsettings.json supplying one. Production passes null and reads the real sources.
    /// </param>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter? output = null,
        IConfiguration? configuration = null,
        CancellationToken ct = default)
    {
        var log = output ?? Console.Out;
        log.WriteLine("[--migrate] Migration-only mode: the web host is NOT constructed.");

        configuration ??= BuildConfiguration();
        var connectionString = ResolveConnectionString(configuration);
        if (connectionString is null)
        {
            // Fail loudly and name the variable. The previous failure mode was an
            // InvalidOperationException about proxy headers, which told an operator looking at a
            // failed migration job nothing useful about migrations.
            await Console.Error.WriteLineAsync(
                "[--migrate] FAILED: ConnectionStrings__Default is not set. " +
                "The migration job needs a connection string and nothing else. " +
                "Set it in the Render dashboard (Environment -> ConnectionStrings__Default).");
            return 1;
        }

        try
        {
            await using var db = new ZayraDbContext(BuildOptions(connectionString));

            // FAIL CLOSED WHEN THIS BUILD HAS NO MIGRATIONS TO APPLY.
            //
            // The production Dockerfile deletes Migrations/ BEFORE `dotnet publish`, so the
            // migration classes are never compiled into the shipped Zayra.Api.dll. Running
            // `dotnet Zayra.Api.dll --migrate` inside that image calls MigrateAsync() against an
            // assembly that knows of zero migrations: it applies nothing and exits 0. render.yaml
            // declares exactly that command as its preDeployCommand, so the pre-deploy "migration
            // step" could never have migrated anything — the same empty-set no-op that made
            // /health/ready report a permanent pendingMigrations: 0.
            //
            // An operator reaching for --migrate during an incident must be told this, not handed
            // a green exit. The embedded manifest lets us say precisely how many migrations this
            // build knows about but cannot apply.
            var strippedBuild = DescribeStrippedBuildFailure(
                db.Database.GetMigrations().ToList(), MigrationManifest.Ids);
            if (strippedBuild is not null)
            {
                await Console.Error.WriteLineAsync(strippedBuild);
                return 1;
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count == 0)
            {
                log.WriteLine("[--migrate] Database is already up to date; nothing to apply.");
                return 0;
            }

            log.WriteLine($"[--migrate] Applying {pending.Count} migration(s):");
            foreach (var id in pending) log.WriteLine($"[--migrate]   - {id}");

            await db.Database.MigrateAsync(ct);

            // Report the result rather than assuming it. A migration EF cannot see is one it will
            // not apply and will not mention, which is exactly how three of them stayed unapplied
            // for 70 days; scripts/check-migration-visibility.sh is the gate for that, and this
            // line is the runtime echo of it.
            var remaining = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (remaining.Count > 0)
            {
                await Console.Error.WriteLineAsync(
                    $"[--migrate] FAILED: {remaining.Count} migration(s) still pending after Migrate(): "
                    + string.Join(", ", remaining));
                return 1;
            }

            log.WriteLine("[--migrate] Complete. All migrations applied.");
            return 0;
        }
        catch (Exception ex)
        {
            // Type and message only. Npgsql exception text can carry host, database and username,
            // and a pre-deploy job's log is not a place to print them; the stack trace goes to the
            // operator's console but the connection string never does.
            await Console.Error.WriteLineAsync($"[--migrate] FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
