namespace Zayra.Api.Infrastructure.Government;

/// <summary>Muqeem (residency and visa services). No published or contracted API: capabilities only.</summary>
public interface IMuqeemProvider : IGovernmentProvider { }

/// <summary>GOSI (social insurance). No published or contracted API: capabilities only.</summary>
public interface IGosiProvider : IGovernmentProvider { }

/// <summary>Takamol (Qiwa platform services). No published or contracted API: capabilities only.</summary>
public interface ITakamolProvider : IGovernmentProvider { }

/// <summary>
/// The shape every placeholder provider shares (after <c>DisabledJawazatProvider</c>): no endpoint, no credentials, no portal
/// automation, never live, and a plain sentence saying so. A simulator is deliberately NOT written for these bodies: without a
/// specification it would freeze a guess, and a guess that looks like a filing is worse than an honest "not connected".
/// </summary>
public abstract class DisabledGovernmentProvider : IGovernmentProvider
{
    public const string UnavailableMessage =
        "This service is not connected. No contract, sandbox or credentials have been set up for it, so nothing is sent to it "
        + "or read from it. Record what you file in the official portal yourself.";

    public abstract string Body { get; }
    public string AdapterName => "disabled";
    public bool IsLiveIntegration => false;

    public Task<GovernmentProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
        Task.FromResult(new GovernmentProviderCapabilities(Body, "Disabled", false, Array.Empty<string>(), UnavailableMessage));
}

public sealed class DisabledMuqeemProvider : DisabledGovernmentProvider, IMuqeemProvider
{
    public override string Body => GovernmentBodies.Muqeem;
}

public sealed class DisabledGosiProvider : DisabledGovernmentProvider, IGosiProvider
{
    public override string Body => GovernmentBodies.Gosi;
}

public sealed class DisabledTakamolProvider : DisabledGovernmentProvider, ITakamolProvider
{
    public override string Body => GovernmentBodies.Takamol;
}
