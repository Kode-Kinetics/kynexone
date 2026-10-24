using System.Text.Json;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Operations;

namespace Zayra.Api.Infrastructure.Provisioning;

/// <summary>
/// <c>dotnet Zayra.Api.dll --setup-organisation [setup.json]</c>: creates this installation's
/// organisation, first company and administrator, once, through
/// <see cref="IOrganisationProvisioningService"/>. It is the only way the organisation comes to exist.
/// </summary>
/// <remarks>
/// <para>Input comes from the JSON file (camelCase keys of <see cref="SetupInput"/>) and, for anything
/// the file leaves out, from <c>OrganisationSetup__*</c> configuration (environment variables). The
/// admin password is best kept off disk: leave it out of both and the command prompts for it without
/// echo. It is never logged.</para>
/// <para>Runs after the boot sequence (seeders included) and before the web server starts, so the
/// organisation is provisioned against the same global reference data as any boot would see.</para>
/// <para>Exit codes: 0 created; 1 refused (the reason is printed); 2 database not reachable or not migrated.</para>
/// </remarks>
public static class OrganisationSetupCommand
{
    public const string Argument = "--setup-organisation";
    public const string ConfigSection = "OrganisationSetup";

    public sealed record SetupInput
    {
        public string? OrganisationName { get; init; }
        public string? LegalName { get; init; }
        public string? HomeCountry { get; init; }
        public string? AdminEmail { get; init; }
        public string? AdminFullName { get; init; }
        public string? AdminPassword { get; init; }
    }

    public static bool ShouldHandle(string[] args) => args.Contains(Argument, StringComparer.Ordinal);

    /// <summary>The path after <c>--setup-organisation</c>, when one is given.</summary>
    public static string? FilePathFrom(string[] args)
    {
        var at = Array.IndexOf(args, Argument);
        return at >= 0 && at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[at + 1]
            : null;
    }

    /// <summary>File values first, then <c>OrganisationSetup:*</c> configuration for anything missing.</summary>
    public static SetupInput Read(IConfiguration configuration, string? json)
    {
        var file = string.IsNullOrWhiteSpace(json)
            ? new SetupInput()
            : JsonSerializer.Deserialize<SetupInput>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
              ?? new SetupInput();
        var section = configuration.GetSection(ConfigSection);
        string? Pick(string? fromFile, string key) =>
            !string.IsNullOrWhiteSpace(fromFile) ? fromFile.Trim()
            : string.IsNullOrWhiteSpace(section[key]) ? null : section[key]!.Trim();

        return new SetupInput
        {
            OrganisationName = Pick(file.OrganisationName, nameof(SetupInput.OrganisationName)),
            LegalName = Pick(file.LegalName, nameof(SetupInput.LegalName)),
            HomeCountry = Pick(file.HomeCountry, nameof(SetupInput.HomeCountry)),
            AdminEmail = Pick(file.AdminEmail, nameof(SetupInput.AdminEmail)),
            AdminFullName = Pick(file.AdminFullName, nameof(SetupInput.AdminFullName)),
            // Not trimmed: a password is what was typed.
            AdminPassword = !string.IsNullOrEmpty(file.AdminPassword) ? file.AdminPassword : section[nameof(SetupInput.AdminPassword)],
        };
    }

    public static async Task<int> RunAsync(
        IServiceProvider services,
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken ct = default)
    {
        output.WriteLine("[--setup-organisation] Setting up this KynexOne installation.");

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var readiness = await ProductionReadinessEvidence.BuildReadinessAsync(db, configuration, ct, includeDetail: false);
        if (!readiness.Dependencies.Database.Healthy)
        {
            await error.WriteLineAsync("[--setup-organisation] REFUSED: the database is not reachable. Check ConnectionStrings__Default.");
            return 2;
        }
        if (readiness.PendingMigrations != 0)
        {
            await error.WriteLineAsync(
                "[--setup-organisation] REFUSED: the database is not fully migrated "
                + (readiness.PendingMigrations < 0
                    ? "(this build cannot tell which migrations it expects)."
                    : $"({readiness.PendingMigrations} migration(s) pending).")
                + " Run `dotnet Zayra.Api.dll --migrate` first.");
            return 2;
        }

        var path = FilePathFrom(args);
        SetupInput input;
        try
        {
            input = Read(configuration, path is null ? null : await File.ReadAllTextAsync(path, ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            await error.WriteLineAsync($"[--setup-organisation] REFUSED: could not read '{path}': {ex.Message}");
            return 1;
        }

        if (string.IsNullOrEmpty(input.AdminPassword) && !Console.IsInputRedirected)
            input = input with { AdminPassword = PromptHidden(output, "Administrator password (min 10 characters): ") };

        var provisioning = scope.ServiceProvider.GetRequiredService<IOrganisationProvisioningService>();
        var result = await provisioning.ProvisionAsync(new OrganisationProvisioningRequest
        {
            OrganisationName = input.OrganisationName,
            LegalName = input.LegalName,
            HomeCountry = input.HomeCountry,
            AdminEmail = input.AdminEmail,
            AdminFullName = input.AdminFullName,
            AdminPassword = input.AdminPassword,
        }, ActorName(), ct);

        if (!result.Succeeded)
        {
            await error.WriteLineAsync($"[--setup-organisation] REFUSED: {result.Refusal}");
            return 1;
        }

        var organisation = result.Organisation!;
        output.WriteLine($"[--setup-organisation] Created organisation '{organisation.Name}'.");
        output.WriteLine($"[--setup-organisation] Organisation id: {organisation.OrganisationId}");
        output.WriteLine($"[--setup-organisation] Administrator: {organisation.AdminEmail}");
        return 0;
    }

    /// <summary>The audit actor: the setup command and the operating-system user who ran it.</summary>
    internal static string ActorName() => $"{OrganisationProvisioningService.SetupActor}:{Environment.UserName}";

    private static string PromptHidden(TextWriter output, string prompt)
    {
        output.Write(prompt);
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
        output.WriteLine();
        return buffer.ToString();
    }
}
