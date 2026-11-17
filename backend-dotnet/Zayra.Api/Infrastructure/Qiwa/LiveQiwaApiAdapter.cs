using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zayra.Api.Infrastructure.Government;

namespace Zayra.Api.Infrastructure.Qiwa;

/// <summary>
/// Qiwa API client. UNVERIFIED: the endpoints below have never been confirmed against Qiwa. Only
/// constructed when QIWA_USE_LIVE_ADAPTER=true AND a Qiwa partner agreement is recorded
/// (see <see cref="QiwaLiveAdapterPolicy"/>); otherwise construction throws.
///
/// <para>Every request goes through <see cref="GovernmentHttp"/> under a live-write decision the caller got from
/// <see cref="QiwaLiveAdapterPolicy.DecideLiveWrite"/> for one tenant (agreement, the tenant's consent record, a credential,
/// the operator switch, and the legal clearance), to the allow-listed host of that decision's ENVIRONMENT (security R8). The
/// production host is the one this adapter always used; a sandbox host must be configured by the operator
/// (<c>Government:Qiwa:BaseUrls:sandbox</c>) and may not be the production host. A call without a decision sends nothing.</para>
///
/// OAuth2 (client_credentials): POST {base}/auth/realms/organizations/protocol/openid-connect/token
/// Employee sync: POST {base}/api/v1/establishments/{establishmentId}/employees
/// </summary>
public sealed class LiveQiwaApiAdapter : IQiwaApiAdapter
{
    /// <summary>A push or lookup that did not say which environment it is for: refused, nothing sent.</summary>
    public const string EnvironmentRequiredCode = "qiwa_environment_required";

    private const string TokenPath = "/auth/realms/organizations/protocol/openid-connect/token";

    private readonly GovernmentHttp _http;
    private readonly ILogger<LiveQiwaApiAdapter> _log;

    public LiveQiwaApiAdapter(IHttpClientFactory httpFactory, ILogger<LiveQiwaApiAdapter> log,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        // Defence in depth behind Program.cs: this adapter refuses to exist without a recorded Qiwa
        // partner agreement, so no composition root, test host or future refactor can switch on
        // unverified live calls by registering it directly.
        if (!QiwaLiveAdapterPolicy.HasPartnerAgreement(configuration))
            throw new InvalidOperationException(QiwaLiveAdapterPolicy.RefusedMessage);
        _http = new GovernmentHttp(httpFactory, configuration);
        _log = log;
    }

    public string AdapterName => "live";

    /// <summary>The one adapter in the system that really talks to Qiwa.</summary>
    public bool IsLiveIntegration => true;

    /// <summary>Refused: without a decision for one tenant there is no gate to check, so nothing is sent.</summary>
    public Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public async Task<string?> AcquireAccessTokenAsync(GovernmentLiveDecision decision, string clientId, string clientSecret, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (decision.Environment is not { } environment) return null;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            _log.LogWarning("Live Qiwa token request skipped — missing client credentials.");
            return null;
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "client_credentials",
            ["client_id"]     = clientId,
            ["client_secret"] = clientSecret,
        });

        try
        {
            using var resp = await _http.GrantTokenAsync(decision, environment, TokenPath, form, null, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Never the raw body: an identity provider may echo the client id or request back.
                // The OAuth "error" code is enough to tell bad credentials from an outage.
                _log.LogError("Qiwa token request failed: {Status} {OAuthError}", (int)resp.StatusCode, OAuthErrorCode(body));
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        }
        catch (GovernmentTransportRefusedException refused)
        {
            _log.LogWarning("Qiwa token request not sent: {RefusalCode}", refused.Code);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller (shutdown, lease loss) cancelled: not a Qiwa failure, so do not record one.
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Qiwa token acquisition threw.");
            return null;
        }
    }

    /// <summary>The RFC 6749 §5.2 <c>error</c> code from a token error response, or a placeholder.</summary>
    internal static string OAuthErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                var code = error.GetString() ?? string.Empty;
                // Spec codes are short snake_case tokens; anything else is not echoed.
                if (code.Length <= 64 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return code;
            }
        }
        catch (JsonException) { }
        return "unparsed";
    }

    /// <summary>Refused: without a decision for one tenant and environment there is no gate to check, so nothing is sent.</summary>
    public Task<QiwaApiResult> PushEmployeeAsync(string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(EnvironmentRequired());
    }

    /// <summary>Refused: without a decision for one tenant and environment there is no gate to check, so nothing is sent.</summary>
    public Task<QiwaApiResult> GetEmployeeStatusAsync(string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(EnvironmentRequired());
    }

    public async Task<QiwaApiResult> PushEmployeeAsync(GovernmentLiveDecision decision, string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey,
        CancellationToken ct, GovernmentExchangeIntentReceipt? intent = null)
    {
        ct.ThrowIfCancellationRequested();
        if (decision.Environment is not { } environment) return EnvironmentRequired();
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

        try
        {
            using var resp = await _http.ExchangeAsync(decision, environment, HttpMethod.Post,
                $"/api/v1/establishments/{Uri.EscapeDataString(payload.EstablishmentId)}/employees",
                new StringContent(json, Encoding.UTF8, "application/json"),
                req =>
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    // Stable across lease expiry/retry/restart. Qiwa (or the configured gateway) can
                    // collapse a replay that occurred after an ambiguous network/commit outcome.
                    req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("N"));
                }, ct, intent);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
                return new QiwaApiResult(true, null, null, body);

            return new QiwaApiResult(false, $"HTTP_{(int)resp.StatusCode}", $"Qiwa rejected employee push: {(int)resp.StatusCode}", body);
        }
        catch (GovernmentTransportRefusedException refused)
        {
            return new QiwaApiResult(false, refused.Code, refused.Message, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller (shutdown, lease loss) cancelled: not a Qiwa failure, so do not record one.
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Qiwa employee push threw.");
            return new QiwaApiResult(false, "NETWORK_ERROR", ex.Message, null);
        }
    }

    public async Task<QiwaApiResult> GetEmployeeStatusAsync(GovernmentLiveDecision decision, string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (decision.Environment is not { } environment) return EnvironmentRequired();
        try
        {
            using var resp = await _http.ExchangeAsync(decision, environment, HttpMethod.Get,
                $"/api/v1/establishments/{Uri.EscapeDataString(establishmentId)}/employees/{Uri.EscapeDataString(employeeIdNumber)}",
                null, req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return resp.IsSuccessStatusCode
                ? new QiwaApiResult(true, null, null, body)
                : new QiwaApiResult(false, $"HTTP_{(int)resp.StatusCode}", "Qiwa status lookup failed.", body);
        }
        catch (GovernmentTransportRefusedException refused)
        {
            return new QiwaApiResult(false, refused.Code, refused.Message, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller (shutdown, lease loss) cancelled: not a Qiwa failure, so do not record one.
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Qiwa status lookup threw.");
            return new QiwaApiResult(false, "NETWORK_ERROR", ex.Message, null);
        }
    }

    private static QiwaApiResult EnvironmentRequired() => new(false, EnvironmentRequiredCode,
        "This Qiwa call carried no live decision for a tenant and environment, so nothing was sent.", null);
}
