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
/// </summary>
public interface IQiwaApiAdapter
{
    string AdapterName { get; }

    /// <summary>
    /// True ONLY for an adapter that actually files with Qiwa over the network.
    ///
    /// <para>DEFAULTS TO FALSE ON PURPOSE. Anything that has not explicitly declared itself live —
    /// the sandbox mock, every test double — is a simulation, and the product must say so. The
    /// defect this closes: the sandbox adapter returned <c>{"status":"synced"}</c>, the worker wrote
    /// <c>QiwaSyncStatuses.Synced</c> onto the employee and <c>Connected</c> onto the tenant
    /// connection, and a customer reading the screen believed their workforce had been filed with
    /// MHRSD when not one byte had left the process. A mock that announces itself is defensible; a
    /// mock that silently passes for the real thing is not.</para>
    ///
    /// <para>A default interface member rather than a required one so that adding the honesty flag
    /// cannot accidentally mark an existing test double as live: opting IN to "live" is a deliberate
    /// act, opting out is the safe default.</para>
    /// </summary>
    bool IsLiveIntegration => false;
    Task<QiwaApiResult> PushEmployeeAsync(string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct);
    Task<QiwaApiResult> GetEmployeeStatusAsync(string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct);
    Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct);
}
