using Microsoft.Extensions.Configuration;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Qiwa;

/// <summary>
/// Whether this process may talk to Qiwa at all. HARD-DISABLED by default.
///
/// <para>WHY. <see cref="LiveQiwaApiAdapter"/> targets <c>api.qiwa.tech</c> with an OAuth client-
/// credentials flow and an employee-push endpoint that nobody has verified against Qiwa: there is no
/// public Qiwa API for establishments, and access is gated on a partner agreement with MHRSD/Takamol.
/// Setting <c>QIWA_USE_LIVE_ADAPTER=true</c> alone used to switch it on, at which point the product would
/// have reported "Filed with Qiwa" on the strength of an unverified endpoint.</para>
///
/// <para>NOW. The live adapter is used only when BOTH <c>QIWA_USE_LIVE_ADAPTER=true</c> AND the
/// operator has recorded the partner agreement under <see cref="PartnerAgreementKey"/>. With the
/// switch on and no agreement, the process registers <see cref="RefusedLiveQiwaApiAdapter"/>: it never
/// opens a socket, every Qiwa write endpoint answers <c>501</c> with <see cref="RefusedCode"/>, and the
/// screens show the "Qiwa data check" wording. This is deliberately not a boot failure: a stray env var
/// must not take a live pilot down, but it must never make anything look filed.</para>
/// </summary>
public static class QiwaLiveAdapterPolicy
{
    public const string LiveSwitchEnvVar = "QIWA_USE_LIVE_ADAPTER";

    /// <summary>Operator-owned (platform) configuration. A reference to the signed Qiwa partner agreement.</summary>
    public const string PartnerAgreementKey = "Qiwa:PartnerAgreementReference";

    /// <summary>Operator-owned flag, default OFF: show the OAuth client-credential form in the tenant UI.</summary>
    public const string CredentialFormKey = "Qiwa:ShowCredentialForm";

    public const string RefusedCode = "qiwa_live_adapter_refused";

    public const string RefusedMessage =
        "Calls to Qiwa are switched off on this server. They need a signed Qiwa partner "
        + "agreement recorded by the platform operator, and none is on file. Use the Qiwa data check to "
        + "confirm your employee records are complete, and record changes in Qiwa itself.";

    public enum Mode { Sandbox, Live, RefusedLive }

    public static bool LiveRequested(IConfiguration? config) =>
        string.Equals(config?[LiveSwitchEnvVar] ?? Environment.GetEnvironmentVariable(LiveSwitchEnvVar), "true",
            StringComparison.OrdinalIgnoreCase);

    public static bool HasPartnerAgreement(IConfiguration? config) =>
        !string.IsNullOrWhiteSpace(config?[PartnerAgreementKey]);

    /// <summary>
    /// Which adapter this process registers. Not asked for: the simulator (today's default, unchanged). Asked for: the live
    /// adapter only with a recorded partner agreement, else the refusing one. Whether a live call may then be made for a
    /// tenant is <see cref="DecideLiveWrite"/>.
    /// </summary>
    public static Mode Decide(bool liveRequested, bool hasPartnerAgreement) =>
        !liveRequested ? Mode.Sandbox : hasPartnerAgreement ? Mode.Live : Mode.RefusedLive;

    /// <summary>
    /// The real four-part gate for a live Qiwa call for one tenant, plus the legal clearance every live write needs: G1 the
    /// recorded agreement (<see cref="PartnerAgreementKey"/>), G2 the tenant's consent record (opt-in <c>gov_qiwa_api</c>, same
    /// rules as every other body), G3 a usable credential, G4 the live switch AND <c>Government:Qiwa:Enabled</c>. Read per call,
    /// so the operator switch needs no restart.
    /// </summary>
    public static GovernmentLiveDecision DecideLiveWrite(IConfiguration? config, bool tenantConsent, bool credential, string environment) =>
        GovernmentLivePolicy.DecideLiveWrite(config, GovernmentBodies.Qiwa,
            new GovernmentLiveGates(
                PartnerAgreement: HasPartnerAgreement(config),
                TenantConsent: tenantConsent,
                Credential: credential,
                OperatorEnabled: LiveRequested(config) && GovernmentLivePolicy.OperatorEnabled(config, GovernmentBodies.Qiwa)),
            environment);

    /// <summary>G2 for Qiwa: the qiwa_integration module is not switched off AND the gov_qiwa_api consent record is on file.</summary>
    public static async Task<bool> HasTenantConsentAsync(Zayra.Api.Data.ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        // SYSTEM CONTEXT: callers (the sync worker) run without a request tenant; the TenantId predicate pins the read.
        var moduleOff = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
            db.TenantFeatureFlags, f => f.TenantId == tenantId && f.FeatureKey == FeatureKeys.QiwaIntegration && !f.IsEnabled, ct);
        return !moduleOff && await GovernmentOptIn.HasConsentAsync(db, tenantId, GovernmentBodies.Qiwa, ct);
    }

    public static Mode Decide(IConfiguration? config) => Decide(LiveRequested(config), HasPartnerAgreement(config));

    /// <summary>True only when an operator has deliberately switched the credential form on.</summary>
    public static bool CredentialFormEnabled(IConfiguration? config) =>
        bool.TryParse(config?[CredentialFormKey], out var on) && on;
}

/// <summary>
/// Registered when <c>QIWA_USE_LIVE_ADAPTER=true</c> but no partner agreement is on file. Makes no
/// network call, reports itself as not live, and fails every operation with
/// <see cref="QiwaLiveAdapterPolicy.RefusedCode"/>.
/// </summary>
public sealed class RefusedLiveQiwaApiAdapter : IQiwaApiAdapter
{
    public string AdapterName => "live-refused";
    public bool IsLiveIntegration => false;

    public Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task<QiwaApiResult> PushEmployeeAsync(string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct) =>
        Task.FromResult(new QiwaApiResult(false, QiwaLiveAdapterPolicy.RefusedCode, QiwaLiveAdapterPolicy.RefusedMessage, null));

    public Task<QiwaApiResult> GetEmployeeStatusAsync(string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct) =>
        Task.FromResult(new QiwaApiResult(false, QiwaLiveAdapterPolicy.RefusedCode, QiwaLiveAdapterPolicy.RefusedMessage, null));
}
