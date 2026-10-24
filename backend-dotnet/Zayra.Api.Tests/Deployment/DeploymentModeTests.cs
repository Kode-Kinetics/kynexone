using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Infrastructure.Deployment;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests.Deployment;

/// <summary>
/// WP1: one testable answer to "which deployment am I". The hosted service (Saas) and a client
/// installation (Client) share one codebase; everything later that differs between them reads
/// <see cref="IDeploymentContext"/>, so the resolution rules below are the foundation of all of it.
/// </summary>
public class DeploymentModeTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    // ── The resolution table ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Client", false, DeploymentMode.Client)]
    [InlineData("client", false, DeploymentMode.Client)]
    [InlineData(" CLIENT ", true, DeploymentMode.Client)]
    [InlineData("Saas", false, DeploymentMode.Saas)]
    [InlineData("saas", true, DeploymentMode.Saas)]
    public void ExplicitMode_IsHonoured_InEveryEnvironment(string configured, bool isDevelopment, DeploymentMode expected)
    {
        DeploymentModeResolver.Resolve(Config(("Deployment:Mode", configured)), isDevelopment)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("DEDICATED_DEPLOYMENT")]
    [InlineData("CLIENT_DEPLOYMENT")]
    public void LegacyFlag_ResolvesToClient(string flag)
    {
        DeploymentModeResolver.Resolve(Config((flag, "true")), isDevelopment: false)
            .Should().Be(DeploymentMode.Client);
        DeploymentModeResolver.Resolve(Config((flag, "TRUE")), isDevelopment: true)
            .Should().Be(DeploymentMode.Client);
    }

    /// <summary>
    /// The hosted service's render.yaml carries DEDICATED_DEPLOYMENT=true from before this switch
    /// existed. The explicit key must win, or the hosted service would lose its platform console.
    /// </summary>
    [Fact]
    public void ExplicitSaas_WinsOverTheLegacyFlag()
    {
        DeploymentModeResolver.Resolve(
                Config(("Deployment:Mode", "Saas"), ("DEDICATED_DEPLOYMENT", "true")), isDevelopment: false)
            .Should().Be(DeploymentMode.Saas);
    }

    [Fact]
    public void LegacyFlagNotTrue_IsIgnored()
    {
        DeploymentModeResolver.Resolve(Config(("DEDICATED_DEPLOYMENT", "false"), ("CLIENT_DEPLOYMENT", "")), isDevelopment: true)
            .Should().Be(DeploymentMode.Saas);
    }

    [Fact]
    public void Development_WithNothingSet_DefaultsToSaas()
    {
        DeploymentModeResolver.Resolve(Config(), isDevelopment: true).Should().Be(DeploymentMode.Saas);
    }

    [Fact]
    public void Production_WithoutAMode_Throws()
    {
        var act = () => DeploymentModeResolver.Resolve(Config(), isDevelopment: false);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("[Deployment] Set Deployment__Mode to Client or Saas.");
    }

    [Theory]
    [InlineData("Hybrid")]
    [InlineData("1")]
    [InlineData("OnPrem")]
    public void UnknownMode_Throws_EvenInDevelopment(string configured)
    {
        var act = () => DeploymentModeResolver.Resolve(Config(("Deployment:Mode", configured)), isDevelopment: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Deployment__Mode*Client or Saas*");
    }

    [Fact]
    public void Context_ExposesTheMode()
    {
        var client = new DeploymentContext(DeploymentMode.Client);
        client.IsClient.Should().BeTrue();
        client.IsSaas.Should().BeFalse();

        var saas = new DeploymentContext(DeploymentMode.Saas);
        saas.IsClient.Should().BeFalse();
        saas.IsSaas.Should().BeTrue();
    }

    // ── Client-mode fail-fast ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("PLATFORM_ADMIN_PASSWORD")]
    [InlineData("PLATFORM_ADMIN_EMAIL")]
    public void ClientMode_WithAPlatformAdminVariable_Throws(string variable)
    {
        var act = () => DeploymentModeResolver.AssertSafeForMode(
            DeploymentMode.Client, Config((variable, "S0me-Long-Strong-Password!")));
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{variable}*");
    }

    [Fact]
    public void SaasMode_WithAPlatformAdminVariable_IsAllowed()
    {
        var act = () => DeploymentModeResolver.AssertSafeForMode(
            DeploymentMode.Saas,
            Config(("PLATFORM_ADMIN_PASSWORD", "x"), ("PLATFORM_ADMIN_EMAIL", "platform@example.com")));
        act.Should().NotThrow();
    }

    [Fact]
    public void ClientMode_WithoutPlatformAdminVariables_IsAllowed()
    {
        var act = () => DeploymentModeResolver.AssertSafeForMode(
            DeploymentMode.Client, Config(("PLATFORM_ADMIN_PASSWORD", "  ")));
        act.Should().NotThrow();
    }

    // ── --migrate needs no mode ───────────────────────────────────────────────────

    /// <summary>
    /// A migration job needs a connection string and nothing else. With no Deployment__Mode set,
    /// --migrate must get past every precondition and fail only at the (unroutable) database.
    /// </summary>
    [Fact]
    public async Task Migrate_RunsWithoutADeploymentMode()
    {
        var originalError = Console.Error;
        try
        {
            var captured = new StringWriter();
            Console.SetError(captured);

            var config = Config(("ConnectionStrings:Default",
                "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1;Command Timeout=1"));

            var exitCode = await MigrateOnlyEntryPoint.RunAsync(Array.Empty<string>(), new StringWriter(), config);

            exitCode.Should().Be(1, "the unroutable database is the only thing that may stop it");
            captured.ToString().Should().NotContain("Deployment");
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void Program_ResolvesTheModeAfterTheMigrateShortCircuit()
    {
        var program = ResolveRepoFile(Path.Combine("backend-dotnet", "Zayra.Api", "Program.cs"));
        program.Should().NotBeNull("Program.cs must be locatable from the test binary");
        var text = File.ReadAllText(program!);

        var migrateAt = text.IndexOf("if (MigrateOnlyEntryPoint.ShouldHandle(args))", StringComparison.Ordinal);
        var resolveAt = text.IndexOf("DeploymentModeResolver.Resolve(", StringComparison.Ordinal);
        var builderAt = text.IndexOf("var builder = WebApplication.CreateBuilder(args);", StringComparison.Ordinal);

        migrateAt.Should().BeGreaterThan(-1);
        resolveAt.Should().BeGreaterThan(migrateAt, "--migrate must not require Deployment__Mode");
        resolveAt.Should().BeGreaterThan(builderAt);
    }

    // ── The real composition ──────────────────────────────────────────────────────

    [Fact]
    public async Task ProgramComposition_InDevelopment_RegistersSaas()
    {
        var connectionString = $"Data Source=file:deploy-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var host = new AuthorizationPipelineHost(connectionString);
        _ = host.CreateClient();

        host.Services.GetRequiredService<IDeploymentContext>().Mode.Should().Be(DeploymentMode.Saas);
    }

    [Fact]
    public async Task ProgramComposition_ClientModeWithPlatformAdminPassword_RefusesToBoot()
    {
        var connectionString = $"Data Source=file:deploy-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var host = new AuthorizationPipelineHost(connectionString, hostSettings: new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = "Client",
            ["PLATFORM_ADMIN_PASSWORD"] = "A-Strong-Enough-Password-1234",
        });

        var act = () => host.CreateClient();
        act.Should().Throw<InvalidOperationException>().WithMessage("*PLATFORM_ADMIN_PASSWORD*");
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
