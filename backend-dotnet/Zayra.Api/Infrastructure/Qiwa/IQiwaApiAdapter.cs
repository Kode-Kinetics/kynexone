using Zayra.Api.Infrastructure.Government;

namespace Zayra.Api.Infrastructure.Qiwa;

/// <summary>
/// Payload pushed to Qiwa when registering / updating an employee record.
/// All fields are required by Qiwa for a successful employee sync.
/// </summary>
public record QiwaEmployeePayload(
    string EmployeeCode, string IdNumber, string IdType,
    string Nationality, string SaudiOrNonSaudi, string OccupationCode,
    string EstablishmentId, string WorkLocationId, string ContractReference);

/// <summary>Normalised result of any Qiwa API operation.</summary>
public record QiwaApiResult(bool Success, string? ErrorCode, string? ErrorMessage, string? RawResponse);

/// <summary>
/// Abstraction over the Qiwa workforce platform API.  Two implementations exist:
/// a <see cref="SandboxQiwaApiAdapter"/> (default, no network) and a
/// <see cref="LiveQiwaApiAdapter"/> (real HTTP calls, used when QIWA_USE_LIVE_ADAPTER=true).
///
/// <para>A government adapter (<see cref="IGovernmentAdapter"/>): the honesty rule lives there. True ONLY for an adapter that
/// actually files with Qiwa over the network; the sandbox mock and every test double default to false, because a mock that
/// silently passes for the real thing (the sandbox once wrote <c>Synced</c> onto employees) is not defensible.</para>
/// </summary>
public interface IQiwaApiAdapter : IGovernmentAdapter
{
    /// <summary>Every Qiwa adapter talks to the Qiwa body. <c>AdapterName</c> and the honesty flag
    /// <c>IsLiveIntegration</c> (default FALSE: see <see cref="IGovernmentAdapter"/>) are inherited from the marker.</summary>
    string IGovernmentAdapter.Body => GovernmentBodies.Qiwa;

    Task<QiwaApiResult> PushEmployeeAsync(string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct);
    Task<QiwaApiResult> GetEmployeeStatusAsync(string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct);
    Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct);

    /// <summary>
    /// The token grant under a live-write decision from <see cref="QiwaLiveAdapterPolicy"/> (it carries the tenant's environment).
    /// The live adapter sends only through that decision; adapters that never leave the process ignore it.
    /// </summary>
    Task<string?> AcquireAccessTokenAsync(GovernmentLiveDecision decision, string clientId, string clientSecret, CancellationToken ct)
        => AcquireAccessTokenAsync(clientId, clientSecret, decision.Environment ?? GovernmentEnvironments.Sandbox, ct);

    /// <summary>
    /// The employee push under a live-write decision and the recorder's receipt for the committed intent of this push (a live
    /// adapter sends nothing without it). See <see cref="AcquireAccessTokenAsync(GovernmentLiveDecision, string, string, CancellationToken)"/>.
    /// </summary>
    Task<QiwaApiResult> PushEmployeeAsync(GovernmentLiveDecision decision, string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey,
        CancellationToken ct, GovernmentExchangeIntentReceipt? intent = null)
        => PushEmployeeAsync(accessToken, payload, idempotencyKey, ct);

    /// <summary>The status lookup under a decision for the tenant's environment.</summary>
    Task<QiwaApiResult> GetEmployeeStatusAsync(GovernmentLiveDecision decision, string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct)
        => GetEmployeeStatusAsync(accessToken, establishmentId, employeeIdNumber, ct);
}
