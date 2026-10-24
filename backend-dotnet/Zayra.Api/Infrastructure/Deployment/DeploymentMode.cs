namespace Zayra.Api.Infrastructure.Deployment;

/// <summary>
/// Which product this process is. <see cref="Saas"/> is the vendor's hosted service (platform
/// console, pricing, many tenants). <see cref="Client"/> is the product as sold: one installation in
/// the client's own environment, one organisation, no vendor control plane.
/// </summary>
public enum DeploymentMode
{
    Saas,
    Client,
}

/// <summary>The single source of truth for "which deployment am I". Registered as a singleton.</summary>
public interface IDeploymentContext
{
    DeploymentMode Mode { get; }
    bool IsClient { get; }
    bool IsSaas { get; }
}

public sealed class DeploymentContext : IDeploymentContext
{
    public DeploymentContext(DeploymentMode mode) => Mode = mode;

    public DeploymentMode Mode { get; }
    public bool IsClient => Mode == DeploymentMode.Client;
    public bool IsSaas => Mode == DeploymentMode.Saas;
}

/// <summary>
/// Resolves <see cref="DeploymentMode"/> from configuration, and refuses configurations a client
/// installation must never boot with.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order:
/// 1. <c>Deployment:Mode</c> (<c>Deployment__Mode</c>), case-insensitive <c>Client</c> / <c>Saas</c>.
/// 2. the legacy flags <c>DEDICATED_DEPLOYMENT=true</c> / <c>CLIENT_DEPLOYMENT=true</c> → Client.
/// 3. Development → Saas, so developer machines, docker-compose and CI keep working unchanged.
/// 4. anything else throws — like the JWT fail-fast, a production process must say what it is.
/// </para>
/// <para>
/// The explicit key wins over the legacy flags on purpose. The hosted service's render.yaml sets
/// <c>DEDICATED_DEPLOYMENT=true</c> (it predates this switch and meant "no demo seeding"), so it
/// pins <c>Deployment__Mode=Saas</c> next to it; without that it would resolve to Client.
/// </para>
/// <para>
/// <c>--migrate</c> never reaches this: it is handled at the top of Program.cs before the web host
/// is built, and a migration job needs only a connection string (see MigrateOnlyEntryPoint).
/// </para>
/// </remarks>
public static class DeploymentModeResolver
{
    public const string ConfigKey = "Deployment:Mode";

    /// <summary>Environment variables that would mint a vendor super-admin. Forbidden in Client mode.</summary>
    public static readonly string[] ForbiddenClientVariables = { "PLATFORM_ADMIN_PASSWORD", "PLATFORM_ADMIN_EMAIL" };

    /// <summary>
    /// Resolves the mode. Reads the legacy flags from <paramref name="configuration"/>, which carries
    /// environment variables in the real host and an in-memory collection in tests.
    /// </summary>
    public static DeploymentMode Resolve(IConfiguration configuration, bool isDevelopment)
    {
        var configured = configuration[ConfigKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // By name only: Enum.TryParse would also accept "1".
            var name = Enum.GetNames<DeploymentMode>()
                .FirstOrDefault(n => string.Equals(n, configured.Trim(), StringComparison.OrdinalIgnoreCase));
            if (name is not null)
                return Enum.Parse<DeploymentMode>(name);
            throw new InvalidOperationException(
                $"[Deployment] Deployment__Mode is '{configured}'. Set Deployment__Mode to Client or Saas.");
        }

        if (IsTrue(configuration["DEDICATED_DEPLOYMENT"]) || IsTrue(configuration["CLIENT_DEPLOYMENT"]))
            return DeploymentMode.Client;

        if (isDevelopment)
            return DeploymentMode.Saas;

        throw new InvalidOperationException("[Deployment] Set Deployment__Mode to Client or Saas.");
    }

    /// <summary>
    /// Client-mode fail-fast. A client installation has no platform team, so a variable that would
    /// bootstrap a cross-tenant platform Owner is a misconfiguration, not a convenience.
    /// </summary>
    public static void AssertSafeForMode(DeploymentMode mode, IConfiguration configuration)
    {
        if (mode != DeploymentMode.Client) return;

        var set = ForbiddenClientVariables
            .Where(name => !string.IsNullOrWhiteSpace(configuration[name]))
            .ToList();
        if (set.Count > 0)
            throw new InvalidOperationException(
                $"[Deployment] Client mode refuses to boot with {string.Join(" / ", set)} set: a client "
                + "installation never has a platform owner. Unset it; the installation administrator is "
                + "created with --setup-installation.");
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
