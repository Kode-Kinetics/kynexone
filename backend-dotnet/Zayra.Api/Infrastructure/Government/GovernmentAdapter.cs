namespace Zayra.Api.Infrastructure.Government;

/// <summary>
/// The government and bank bodies KynexOne may one day exchange data with. A body is a stable lower-case key: it appears
/// in configuration (<c>Government:{Section}:*</c>), in the credential and exchange tables, and in the capabilities API.
/// </summary>
public static class GovernmentBodies
{
    public const string Qiwa = "qiwa";
    public const string Gosi = "gosi";
    public const string Muqeem = "muqeem";
    public const string Takamol = "takamol";
    public const string BankAnb = "bank_anb";

    public static readonly IReadOnlyList<string> All = [Qiwa, Gosi, Muqeem, Takamol, BankAnb];

    public static bool IsKnown(string? body) => body is not null && All.Contains(body, StringComparer.Ordinal);

    /// <summary>The configuration section name of a body (<c>Government:Gosi:...</c>).</summary>
    public static string Section(string body) => body switch
    {
        Qiwa => "Qiwa",
        Gosi => "Gosi",
        Muqeem => "Muqeem",
        Takamol => "Takamol",
        BankAnb => "BankAnb",
        _ => throw new ArgumentOutOfRangeException(nameof(body), body, "Unknown government body."),
    };
}

/// <summary>The environments a credential or base URL can belong to. Nothing else is accepted.</summary>
public static class GovernmentEnvironments
{
    public const string Sandbox = "sandbox";
    public const string Production = "production";

    public static readonly IReadOnlyList<string> All = [Sandbox, Production];

    public static bool IsKnown(string? environment) => environment is not null && All.Contains(environment, StringComparer.Ordinal);
}

/// <summary>
/// Marker for every adapter that talks, or could one day talk, to a government or bank body.
///
/// <para><b>The honesty rule.</b> <see cref="IsLiveIntegration"/> is true ONLY for an adapter that actually exchanges data with
/// the body over the network, and it DEFAULTS TO FALSE ON PURPOSE: anything that has not explicitly declared itself live (a
/// simulator, a disabled provider, every test double) is a simulation, and the product must say so. The defect this closes:
/// the Qiwa sandbox adapter returned <c>{"status":"synced"}</c>, the worker wrote <c>Synced</c> onto the employee and
/// <c>Connected</c> onto the tenant connection, and a customer reading the screen believed their workforce had been filed
/// when not one byte had left the process. A default interface member rather than a required one so that adding the flag
/// cannot accidentally mark an existing double as live: opting IN to "live" is a deliberate act.</para>
///
/// <para>Only a live adapter may produce a "synced", "submitted" or "accepted" state (GovernmentExchangeRecorder enforces it,
/// and so does a database CHECK on <c>government_exchanges</c>).</para>
/// </summary>
public interface IGovernmentAdapter
{
    /// <summary>The body this adapter talks to (<see cref="GovernmentBodies"/>).</summary>
    string Body { get; }

    string AdapterName { get; }

    /// <summary>True ONLY for an adapter that really exchanges data with the body. See the type remarks.</summary>
    bool IsLiveIntegration => false;
}

/// <summary>What a provider can do right now, in the Jawazat <c>DisabledJawazatProvider</c> shape.</summary>
public sealed record GovernmentProviderCapabilities(
    string Body, string Provider, bool IsAvailable, IReadOnlyList<string> SupportedOperations, string Message);

/// <summary>A provider for a body that has no published or contracted API yet: capabilities only, never an operation.</summary>
public interface IGovernmentProvider : IGovernmentAdapter
{
    Task<GovernmentProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct);
}
