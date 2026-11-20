using Microsoft.Extensions.Configuration;

namespace Zayra.Api.Infrastructure.Government;

/// <summary>The four gates of a live exchange with a body (architecture 3.3). All must be true. INTERNAL (E6): only the gate services compute these.</summary>
/// <param name="PartnerAgreement">G1, deployment: the body's partner agreement reference is recorded.</param>
/// <param name="TenantConsent">G2, tenant: the body's opt-in flag is on and its ConfigJson carries a server-stamped consent record.</param>
/// <param name="Credential">G3, credential: a credential for (tenant, company, body, environment) is present, unexpired and opens.</param>
/// <param name="OperatorEnabled">G4, operator: the global kill switch <c>Government:{Section}:Enabled</c> is not false.</param>
internal sealed record GovernmentLiveGates(bool PartnerAgreement, bool TenantConsent, bool Credential, bool OperatorEnabled);

/// <summary>
/// Who and where a decision is for (E5): the tenant, the company (null = tenant-wide) and the environment. A decision made for one
/// scope cannot be used for another: the transport and every bank channel compare it with the credential, the receipt and the call.
/// </summary>
public sealed record GovernmentDecisionScope(Guid TenantId, Guid? CompanyId, string Environment);

/// <summary>
/// The outcome of <see cref="GovernmentLivePolicy"/>. It can ONLY be made by the policy (internal constructor, no public way
/// to build or copy one open, nothing settable), so a transport that is handed one knows the gates were really asked, for this
/// tenant, company and environment. <see cref="AllowsWrites"/> is true only for a decision from
/// <see cref="GovernmentLivePolicy.DecideLiveWrite(IConfiguration?, GovernmentDecisionScope, string, GovernmentLiveGates)"/>
/// with the body's legal clearance recorded; <see cref="GovernmentHttp"/> refuses every non-GET without it.
/// </summary>
public sealed class GovernmentLiveDecision
{
    internal GovernmentLiveDecision(Guid tenantId, Guid? companyId, string body, bool isOpen, string? reasonCode, string environment, bool allowsWrites)
    {
        TenantId = tenantId;
        CompanyId = companyId;
        Body = body;
        IsOpen = isOpen;
        ReasonCode = reasonCode;
        Environment = environment;
        AllowsWrites = isOpen && allowsWrites;
    }

    public Guid TenantId { get; }
    /// <summary>The company the decision was made for, or null for a tenant-wide decision.</summary>
    public Guid? CompanyId { get; }
    public string Body { get; }
    public bool IsOpen { get; }
    /// <summary>The first closed gate, or null when open.</summary>
    public string? ReasonCode { get; }
    /// <summary>The environment the decision was made for (always set): it opens nothing in any other.</summary>
    public string Environment { get; }
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
    /// The PRODUCTION "live writes" rung (anything that changes state at the body, a payroll submission above all, and the token
    /// grants that precede it) is refused until the operator records, for that body and for production, the legal clearance:
    /// whether submitting payroll payments through a bank API on a customer's behalf is payment initiation under SAMA rules. No
    /// tenant setting, consent or credential can stand in for it. The SANDBOX rung (synthetic data) needs only the four gates.
    /// </summary>
    public const string LiveWritesNeedLegalClearance = "live_writes_need_legal_clearance";

    public static string PartnerAgreementKey(string body) => $"Government:{GovernmentBodies.Section(body)}:PartnerAgreementReference";

    public static string EnabledKey(string body) => $"Government:{GovernmentBodies.Section(body)}:Enabled";

    /// <summary>
    /// Deployment configuration only, per body AND per environment: a reference to the written legal opinion. Only the production
    /// key is ever read (<see cref="HasLegalClearance"/>), so a value under the sandbox key can never unlock production writes.
    /// </summary>
    public static string LegalClearanceKey(string body, string environment) =>
        $"Government:{GovernmentBodies.Section(body)}:LiveWrites:{environment}:LegalClearanceReference";

    /// <summary>G1: the operator has recorded a partner agreement reference for this body in this deployment.</summary>
    public static bool HasPartnerAgreement(IConfiguration? config, string body) =>
        !string.IsNullOrWhiteSpace(config?[PartnerAgreementKey(body)]);

    /// <summary>G4: the operator's kill switch. Only an explicit non-true value closes; unset or blank is not a kill.</summary>
    public static bool OperatorEnabled(IConfiguration? config, string body)
    {
        var value = config?[EnabledKey(body)];
        return string.IsNullOrWhiteSpace(value) || (bool.TryParse(value, out var on) && on);
    }

    /// <summary>The production legal clearance for this body. The only clearance key that is read.</summary>
    public static bool HasLegalClearance(IConfiguration? config, string body) =>
        !string.IsNullOrWhiteSpace(config?[LegalClearanceKey(body, GovernmentEnvironments.Production)]);

    /// <summary>The decision for a READ (a status poll) for one scope: the four gates. INTERNAL (E6).</summary>
    internal static GovernmentLiveDecision Decide(GovernmentDecisionScope scope, string body, GovernmentLiveGates gates)
    {
        RequireScope(scope);
        var reason = FirstClosedGate(body, gates, scope.Environment);
        return new GovernmentLiveDecision(scope.TenantId, scope.CompanyId, body, reason is null, reason, scope.Environment, allowsWrites: false);
    }

    /// <summary>The read decision with the deployment half (G1 and G4) from configuration; the tenant half from the caller.</summary>
    internal static GovernmentLiveDecision Decide(IConfiguration? config, GovernmentDecisionScope scope, string body, bool tenantConsent, bool credential) =>
        Decide(scope, body, DeploymentGates(config, body, tenantConsent, credential));

    /// <summary>
    /// The decision for a live WRITE for one scope: the four gates and, for production only, the body's recorded legal
    /// clearance. Anything that submits, registers, grants a token or changes state at the body asks here, and only this decision
    /// lets <see cref="GovernmentHttp"/> send a non-GET. The decision can be used for no other tenant, company or environment. INTERNAL (E6).
    /// </summary>
    internal static GovernmentLiveDecision DecideLiveWrite(IConfiguration? config, GovernmentDecisionScope scope, string body, GovernmentLiveGates gates)
    {
        RequireScope(scope);
        var reason = FirstClosedGate(body, gates, scope.Environment);
        if (reason is null && scope.Environment == GovernmentEnvironments.Production && !HasLegalClearance(config, body))
            reason = LiveWritesNeedLegalClearance;
        return new GovernmentLiveDecision(scope.TenantId, scope.CompanyId, body, reason is null, reason, scope.Environment, allowsWrites: reason is null);
    }

    /// <summary>The write decision with the deployment half from configuration.</summary>
    internal static GovernmentLiveDecision DecideLiveWrite(IConfiguration? config, GovernmentDecisionScope scope, string body, bool tenantConsent, bool credential) =>
        DecideLiveWrite(config, scope, body, DeploymentGates(config, body, tenantConsent, credential));

    private static void RequireScope(GovernmentDecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.TenantId == Guid.Empty) throw new ArgumentException("A decision names its tenant.", nameof(scope));
        if (scope.CompanyId == Guid.Empty) throw new ArgumentException("A decision's company is null (tenant-wide) or a real company.", nameof(scope));
        if (!GovernmentEnvironments.IsKnown(scope.Environment))
            throw new ArgumentOutOfRangeException(nameof(scope), scope.Environment, "A decision names its environment.");
    }

    private static GovernmentLiveGates DeploymentGates(IConfiguration? config, string body, bool tenantConsent, bool credential) =>
        new(HasPartnerAgreement(config, body), tenantConsent, credential, OperatorEnabled(config, body));

    private static string? FirstClosedGate(string body, GovernmentLiveGates gates, string environment)
    {
        if (!GovernmentBodies.IsKnown(body)) throw new ArgumentOutOfRangeException(nameof(body), body, "Unknown government body.");
        if (!GovernmentEnvironments.IsKnown(environment)) throw new ArgumentOutOfRangeException(nameof(environment), environment, "Unknown environment.");
        return !gates.OperatorEnabled ? OperatorDisabled
            : !gates.PartnerAgreement ? NoPartnerAgreement
            : !gates.TenantConsent ? TenantNotOptedIn
            : !gates.Credential ? NoCredential
            : null;
    }
}
