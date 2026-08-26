using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Zayra.Api.Infrastructure.Qiwa;

/// <summary>
/// Real Qiwa API client.  Only used when QIWA_USE_LIVE_ADAPTER=true AND valid
/// credentials are configured per tenant.  Uses the named HttpClient "qiwa".
///
/// OAuth2 (client_credentials):
///   POST https://api.qiwa.tech/auth/realms/organizations/protocol/openid-connect/token
/// Employee sync:
///   POST https://api.qiwa.tech/api/v1/establishments/{establishmentId}/employees
/// </summary>
public sealed class LiveQiwaApiAdapter : IQiwaApiAdapter
{
    private const string TokenPath = "/auth/realms/organizations/protocol/openid-connect/token";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LiveQiwaApiAdapter> _log;

    public LiveQiwaApiAdapter(IHttpClientFactory httpFactory, ILogger<LiveQiwaApiAdapter> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public string AdapterName => "live";

    public async Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            _log.LogWarning("Live Qiwa token request skipped — missing client credentials.");
            return null;
        }

        var client = _httpFactory.CreateClient("qiwa");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "client_credentials",
            ["client_id"]     = clientId,
            ["client_secret"] = clientSecret,
        });

        try
        {
            using var resp = await client.PostAsync(TokenPath, form, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError("Qiwa token request failed: {Status} {Body}", (int)resp.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Qiwa token acquisition threw.");
            return null;
        }
    }

    public async Task<QiwaApiResult> PushEmployeeAsync(string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("qiwa");
        var json = JsonSerializer.Serialize(new
        {
            employee_code      = payload.EmployeeCode,
            id_number          = payload.IdNumber,
            id_type            = payload.IdType,
            nationality        = payload.Nationality,
            saudi_or_non_saudi = payload.SaudiOrNonSaudi,
            occupation_code    = payload.OccupationCode,
            work_location_id   = payload.WorkLocationId,
            contract_reference = payload.ContractReference,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/establishments/{Uri.EscapeDataString(payload.EstablishmentId)}/employees")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // Stable across lease expiry/retry/restart. Qiwa (or the configured gateway) can
        // collapse a replay that occurred after an ambiguous network/commit outcome.
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("N"));

        try
        {
            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
                return new QiwaApiResult(true, null, null, body);

            return new QiwaApiResult(false, $"HTTP_{(int)resp.StatusCode}", $"Qiwa rejected employee push: {(int)resp.StatusCode}", body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Qiwa employee push threw.");
            return new QiwaApiResult(false, "NETWORK_ERROR", ex.Message, null);
        }
    }

    public async Task<QiwaApiResult> GetEmployeeStatusAsync(string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("qiwa");
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/establishments/{Uri.EscapeDataString(establishmentId)}/employees/{Uri.EscapeDataString(employeeIdNumber)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return resp.IsSuccessStatusCode
                ? new QiwaApiResult(true, null, null, body)
                : new QiwaApiResult(false, $"HTTP_{(int)resp.StatusCode}", "Qiwa status lookup failed.", body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Qiwa status lookup threw.");
            return new QiwaApiResult(false, "NETWORK_ERROR", ex.Message, null);
        }
    }
}
