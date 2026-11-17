using Microsoft.Extensions.Configuration;

namespace Zayra.Api.Infrastructure.Government;

/// <summary>The four gates of a live exchange with a body (architecture 3.3). All must be true.</summary>
/// <param name="PartnerAgreement">G1, deployment: the body's partner agreement reference is recorded.</param>
/// <param name="TenantConsent">G2, tenant: the body's opt-in flag is on and its ConfigJson carries a server-stamped consent record.</param>
/// <param name="Credential">G3, credential: a credential for (tenant, company, body, environment) is present, unexpired and opens.</param>
/// <param name="OperatorEnabled">G4, operator: the global kill switch <c>Government:{Section}:Enabled</c> is not false.</param>
public sealed record GovernmentLiveGates(bool PartnerAgreement, bool TenantConsent, bool Credential, bool OperatorEnabled);

/// <summary>
/// The outcome of <see cref="GovernmentLivePolicy"/>. It can ONLY be made by the policy (internal constructor, no public way
/// to build or copy one open), so a transport that is handed one knows the gates were really asked.
/// <see cref="AllowsWrites"/> is true only for a decision from <see cref="GovernmentLivePolicy.DecideLiveWrite(IConfiguration?, string, GovernmentLiveGates, string?)"/>
/// with the body's legal clearance recorded; <see cref="GovernmentHttp"/> refuses every non-GET without it.
/// </summary>
public sealed class GovernmentLiveDecision
{
    internal GovernmentLiveDecision(string body, bool isOpen, string? reasonCode, string? environment, bool allowsWrites)
    {
        Body = body;
        IsOpen = isOpen;
        ReasonCode = reasonCode;
        Environment = environment;
        AllowsWrites = isOpen && allowsWrites;
    }

    public string Body { get; }
    public bool IsOpen { get; }
    /// <summary>The first closed gate, or null when open.</summary>
    public string? ReasonCode { get; }
    /// <summary>The environment the decision was made for, or null for "any" (a read check not tied to one credential).</summary>
    public string? Environment { get; }
    /// <summary>True only for an open live-write decision: the four gates AND the recorded legal clearance.</summary>
    public bool AllowsWrites { get; }
    public string? RefusalCode => IsOpen ? null : GovernmentLivePolicy.RefusedCode;
}

/// <summary>
/// Whether this process may exchange data with a government or bank body for one tenant. FAILS CLOSED: any one gate false
/// refuses, and a refused decision opens no socket (<see cref="GovernmentHttp.ExchangeAsync"/> throws before it sends). The UI
/// then says "check only" or "file only", never "Filed". Generalises the Qiwa policy's gates; <c>QiwaLiveAdapterPolicy</c>
/// builds its decisions here.
/// </summary>
public static class GovernmentLivePolicy
{
    public const string RefusedCode = "government_live_refused";

    // Reason codes, in the order the gates are reported (the first failing one wins).
    public const string OperatorDisabled = "operator_disabled";
    public const string NoPartnerAgreement = "no_partner_agreement";
    public const string TenantNotOptedIn = "tenant_not_opted_in";
    public const string NoCredential = "no_credential";

    /// <summary>
    /// The "live writes" rung (anything that changes state at the body, a payroll submission above all, and the token grants
    /// that precede it) is refused until the operator records, for that body, the legal clearance: whether submitting payroll
    /// payments through a bank API on a customer's behalf is payment initiation under SAMA rules. No tenant setting, consent
    /// or credential can stand in for it.
    /// </summary>
    public const string LiveWritesNeedLegalClearance = "live_writes_need_legal_clearance";

    public static string PartnerAgreementKey(string body) => $"Government:{GovernmentBodies.Section(body)}:PartnerAgreementReference";

    public static string EnabledKey(string body) => $"Government:{GovernmentBodies.Section(body)}:Enabled";

    /// <summary>Deployment configuration only, per body: a reference to the written legal opinion. Absent, every live write is refused.</summary>
    public static string LegalClearanceKey(string body) => $"Government:{GovernmentBodies.Section(body)}:LiveWrites:LegalClearanceReference";

    /// <summary>G1: the operator has recorded a partner agreement reference for this body in this deployment.</summary>
    public static bool HasPartnerAgreement(IConfiguration? config, string body) =>
        !string.IsNullOrWhiteSpace(config?[PartnerAgreementKey(body)]);

    /// <summary>G4: the operator's kill switch. Only an explicit non-true value closes; unset or blank is not a kill.</summary>
    public static bool OperatorEnabled(IConfiguration? config, string body)
    {
        var value = config?[EnabledKey(body)];
        return string.IsNullOrWhiteSpace(value) || (bool.TryParse(value, out var on) && on);
    }

    public static bool HasLegalClearance(IConfiguration? config, string body) => !string.IsNullOrWhiteSpace(config?[LegalClearanceKey(body)]);

    /// <summary>The decision for a READ (a status poll): the four gates.</summary>
    public static GovernmentLiveDecision Decide(string body, GovernmentLiveGates gates, string? environment = null)
    {
        var reason = FirstClosedGate(body, gates, environment);
        return new GovernmentLiveDecision(body, reason is null, reason, environment, allowsWrites: false);
    }

    /// <summary>The read decision with the deployment half (G1 and G4) from configuration; the tenant half from the caller.</summary>
    public static GovernmentLiveDecision Decide(IConfiguration? config, string body, bool tenantConsent, bool credential, string? environment = null) =>
        Decide(body, DeploymentGates(config, body, tenantConsent, credential), environment);

    /// <summary>
    /// The decision for a live WRITE: the four gates, then the body's recorded legal clearance. Anything that submits, registers,
    /// grants a token or changes state at the body asks here, and only this decision lets <see cref="GovernmentHttp"/> send a non-GET.
    /// </summary>
    public static GovernmentLiveDecision DecideLiveWrite(IConfiguration? config, string body, GovernmentLiveGates gates, string? environment = null)
    {
        var reason = FirstClosedGate(body, gates, environment);
        if (reason is null && !HasLegalClearance(config, body)) reason = LiveWritesNeedLegalClearance;
        return new GovernmentLiveDecision(body, reason is null, reason, environment, allowsWrites: reason is null);
    }

    /// <summary>The write decision with the deployment half from configuration.</summary>
    public static GovernmentLiveDecision DecideLiveWrite(IConfiguration? config, string body, bool tenantConsent, bool credential, string? environment = null) =>
        DecideLiveWrite(config, body, DeploymentGates(config, body, tenantConsent, credential), environment);

    private static GovernmentLiveGates DeploymentGates(IConfiguration? config, string body, bool tenantConsent, bool credential) =>
        new(HasPartnerAgreement(config, body), tenantConsent, credential, OperatorEnabled(config, body));

    private static string? FirstClosedGate(string body, GovernmentLiveGates gates, string? environment)
    {
        if (!GovernmentBodies.IsKnown(body)) throw new ArgumentOutOfRangeException(nameof(body), body, "Unknown government body.");
        if (environment is not null && !GovernmentEnvironments.IsKnown(environment))
            throw new ArgumentOutOfRangeException(nameof(environment), environment, "Unknown environment.");
        return !gates.OperatorEnabled ? OperatorDisabled
            : !gates.PartnerAgreement ? NoPartnerAgreement
            : !gates.TenantConsent ? TenantNotOptedIn
            : !gates.Credential ? NoCredential
            : null;
    }
}
