using Microsoft.Extensions.Configuration;

namespace Zayra.Api.Infrastructure.Government;

/// <summary>The four gates of a live exchange with a body (architecture 3.3). All must be true.</summary>
/// <param name="PartnerAgreement">G1, deployment: <c>Government:{Body}:PartnerAgreementReference</c> is recorded.</param>
/// <param name="TenantConsent">G2, tenant: the body's opt-in flag is on and its ConfigJson carries a consent record.</param>
/// <param name="Credential">G3, credential: a credential for (tenant, company, body, environment) is present, unexpired and opens.</param>
/// <param name="OperatorEnabled">G4, operator: the global kill switch <c>Government:{Body}:Enabled</c> is not false.</param>
public sealed record GovernmentLiveGates(bool PartnerAgreement, bool TenantConsent, bool Credential, bool OperatorEnabled);

/// <summary>The outcome of <see cref="GovernmentLivePolicy.Decide"/>. Refused carries a stable code and the first failing gate.</summary>
public sealed record GovernmentLiveDecision(string Body, bool IsOpen, string? ReasonCode)
{
    public string? RefusalCode => IsOpen ? null : GovernmentLivePolicy.RefusedCode;
}

/// <summary>
/// Whether this process may exchange data with a government or bank body for one tenant. FAILS CLOSED: any one gate false
/// refuses, and a refused decision opens no socket (<see cref="GovernmentHttp.ExchangeAsync"/> throws before it sends). The UI
/// then says "check only" or "file only", never "Filed". Generalises the Qiwa policy's gates; <c>QiwaLiveAdapterPolicy</c>
/// delegates its deployment half here.
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
    /// The "live writes" rung (sending something that changes state at the body, a payroll submission above all) is refused
    /// until the operator records the legal clearance: whether submitting payroll payments through a bank API on a customer's
    /// behalf is payment initiation under SAMA rules. No tenant setting, consent or credential can stand in for it.
    /// </summary>
    public const string LiveWritesNeedLegalClearance = "live_writes_need_legal_clearance";

    /// <summary>Deployment configuration only: a reference to the written legal opinion. Absent, every live write is refused.</summary>
    public const string LegalClearanceKey = "Government:LiveWrites:LegalClearanceReference";

    public static string PartnerAgreementKey(string body) => $"Government:{GovernmentBodies.Section(body)}:PartnerAgreementReference";

    public static string EnabledKey(string body) => $"Government:{GovernmentBodies.Section(body)}:Enabled";

    /// <summary>G1: the operator has recorded a partner agreement reference for this body in this deployment.</summary>
    public static bool HasPartnerAgreement(IConfiguration? config, string body) =>
        !string.IsNullOrWhiteSpace(config?[PartnerAgreementKey(body)]);

    /// <summary>G4: the operator's kill switch. Only an explicit non-true value closes; unset or blank is not a kill.</summary>
    public static bool OperatorEnabled(IConfiguration? config, string body)
    {
        var value = config?[EnabledKey(body)];
        return string.IsNullOrWhiteSpace(value) || (bool.TryParse(value, out var on) && on);
    }

    public static GovernmentLiveDecision Decide(string body, GovernmentLiveGates gates)
    {
        if (!GovernmentBodies.IsKnown(body)) throw new ArgumentOutOfRangeException(nameof(body), body, "Unknown government body.");
        var reason = !gates.OperatorEnabled ? OperatorDisabled
            : !gates.PartnerAgreement ? NoPartnerAgreement
            : !gates.TenantConsent ? TenantNotOptedIn
            : !gates.Credential ? NoCredential
            : null;
        return new GovernmentLiveDecision(body, reason is null, reason);
    }

    public static bool HasLegalClearance(IConfiguration? config) => !string.IsNullOrWhiteSpace(config?[LegalClearanceKey]);

    /// <summary>
    /// The decision for a live WRITE: the four gates, and then the recorded legal clearance. Reads (status polls) use
    /// <see cref="Decide(string, GovernmentLiveGates)"/>; anything that submits, registers or changes state at the body asks here.
    /// </summary>
    public static GovernmentLiveDecision DecideLiveWrite(IConfiguration? config, string body, GovernmentLiveGates gates)
    {
        var gate = Decide(body, gates);
        if (!gate.IsOpen) return gate;
        return HasLegalClearance(config) ? gate : new GovernmentLiveDecision(body, false, LiveWritesNeedLegalClearance);
    }

    /// <summary>The deployment half (G1 and G4) from configuration; the tenant half is supplied by the caller.</summary>
    public static GovernmentLiveDecision Decide(IConfiguration? config, string body, bool tenantConsent, bool credential) =>
        Decide(body, new GovernmentLiveGates(HasPartnerAgreement(config, body), tenantConsent, credential, OperatorEnabled(config, body)));
}
