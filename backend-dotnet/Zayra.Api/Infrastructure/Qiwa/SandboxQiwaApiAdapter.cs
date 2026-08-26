using Microsoft.Extensions.Logging;

namespace Zayra.Api.Infrastructure.Qiwa;

/// <summary>
/// In-memory mock of the Qiwa API used when no live credentials are configured
/// (environment = "sandbox") or when QIWA_USE_LIVE_ADAPTER is not "true".
///
/// It makes NO network calls.  Valid payloads return success; payloads with any
/// empty required field return a FIELD_MISSING error — mirroring real Qiwa
/// validation so the sync worker / retry logic can be exercised end-to-end.
/// </summary>
public sealed class SandboxQiwaApiAdapter : IQiwaApiAdapter
{
    private readonly ILogger<SandboxQiwaApiAdapter> _log;

    public SandboxQiwaApiAdapter(ILogger<SandboxQiwaApiAdapter> log) => _log = log;

    public string AdapterName => "sandbox";

    public Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct)
        // Sandbox never needs real auth; return a synthetic token.
        => Task.FromResult<string?>($"sandbox-token-{Guid.NewGuid():N}");

    public Task<QiwaApiResult> PushEmployeeAsync(string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct)
    {
        var missing = MissingFields(payload);
        if (missing.Count > 0)
        {
            _log.LogInformation("Sandbox Qiwa push rejected — missing fields: {Fields}", string.Join(",", missing));
            return Task.FromResult(new QiwaApiResult(
                false, "FIELD_MISSING",
                $"Required field missing: {string.Join(", ", missing)}",
                "{\"status\":\"rejected\",\"reason\":\"FIELD_MISSING\"}"));
        }

        return Task.FromResult(new QiwaApiResult(true, null, null, "{\"status\":\"synced\"}"));
    }

    public Task<QiwaApiResult> GetEmployeeStatusAsync(string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(establishmentId) || string.IsNullOrWhiteSpace(employeeIdNumber))
            return Task.FromResult(new QiwaApiResult(false, "FIELD_MISSING", "establishmentId and employeeIdNumber are required.", null));

        return Task.FromResult(new QiwaApiResult(true, null, null, "{\"status\":\"active\"}"));
    }

    private static List<string> MissingFields(QiwaEmployeePayload p)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(p.EmployeeCode))       missing.Add("employee_code");
        if (string.IsNullOrWhiteSpace(p.IdNumber))           missing.Add("id_number");
        if (string.IsNullOrWhiteSpace(p.IdType))             missing.Add("id_type");
        if (string.IsNullOrWhiteSpace(p.Nationality))        missing.Add("nationality");
        if (string.IsNullOrWhiteSpace(p.SaudiOrNonSaudi))    missing.Add("saudi_or_non_saudi");
        if (string.IsNullOrWhiteSpace(p.OccupationCode))     missing.Add("occupation_code");
        if (string.IsNullOrWhiteSpace(p.EstablishmentId))    missing.Add("establishment_id");
        if (string.IsNullOrWhiteSpace(p.WorkLocationId))     missing.Add("work_location_id");
        if (string.IsNullOrWhiteSpace(p.ContractReference))  missing.Add("contract_reference");
        return missing;
    }
}
