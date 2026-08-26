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
    Task<QiwaApiResult> PushEmployeeAsync(string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct);
    Task<QiwaApiResult> GetEmployeeStatusAsync(string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct);
    Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct);
}
