using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zayra.Api.Infrastructure.Notifications;

/// <summary>
/// Expo Push adapter — the first REAL provider behind <see cref="IPushProvider"/>, replacing
/// <see cref="NullPushProvider"/>.
///
/// WHY EXPO AND NOT FCM/APNs. The mobile client
/// (<c>src/features/notifications/pushNotifications.ts</c>) registers with
/// <c>Notifications.getExpoPushTokenAsync()</c> and posts the resulting
/// <c>ExponentPushToken[...]</c> string to <c>POST /api/mobile/register-device</c>. That token is
/// only addressable through Expo's own relay; handing it to FCM or APNs would be rejected as a
/// malformed device token. Expo's send endpoint also needs NO FCM server key and NO APNs .p8 for
/// Expo Go and managed builds, so this adapter ships without any credential the company does not
/// already have — which is the only kind of provider that can land in a freeze week.
///
/// HOUSE RULES OBSERVED (same shape as <see cref="NullNotificationProvider"/> /
/// <c>SmtpEmailService</c>):
///   • ONE class + ONE DI line. No parallel delivery path: <see cref="PushChannelDispatcher"/>
///     still owns the per-device fan-out, the 10 s timeout, the exception firewall, the
///     NotificationDelivery ledger, DedupeKey uniqueness and RetryOnAmbiguous.
///   • NO vendor credential in code or in an appsettings default. Everything is read per tenant
///     from SystemSettings Category="Notifications" via
///     <see cref="INotificationProviderConfigReader"/>.
///   • An unconfigured tenant gets a VISIBLE <c>not_configured</c> delivery row, never an
///     exception and never silence — identical to the Null provider it replaces.
///
/// TIMEOUT CONTRACT. This class deliberately does NOT impose its own deadline. The dispatcher
/// wraps every call in a 10 s linked CancellationTokenSource; letting the resulting
/// OperationCanceledException propagate is what produces the correct <c>Ambiguous</c> outcome
/// (Expo may already have accepted the message). Swallowing it here would turn a real
/// "maybe delivered" into a false "failed". The named HttpClient's own timeout is set well above
/// 10 s in Program.cs for exactly this reason.
/// </summary>
public sealed class ExpoPushProvider : IPushProvider
{
    /// <summary>Named HttpClient registered in Program.cs. Its Timeout must stay ABOVE
    /// <see cref="ProviderBackedDispatcher.SendTimeout"/> so the dispatcher's CTS wins.</summary>
    public const string HttpClientName = "expo-push";

    public const string DefaultEndpoint = "https://exp.host/--/api/v2/push/send";

    /// <summary>The value a tenant must write to Notifications/Push.Provider to arm this adapter.</summary>
    public const string ProviderKey = "expo";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly INotificationProviderConfigReader _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ExpoPushProvider> _log;

    public ExpoPushProvider(INotificationProviderConfigReader config, IHttpClientFactory httpFactory,
        ILogger<ExpoPushProvider> log)
    {
        _config = config;
        _httpFactory = httpFactory;
        _log = log;
    }

    public string Name => ProviderKey;

    /// <summary>
    /// True only when THIS tenant has explicitly declared Notifications/Push.Provider = "expo".
    ///
    /// Same honesty rule as <see cref="NullNotificationProvider.IsConfiguredAsync"/>: a tenant that
    /// declared some other vendor ("fcm", "apns") must read as NOT configured, because no adapter
    /// for it is deployed — claiming otherwise would convert a visible not_configured row into a
    /// silent black hole. No access token is required: Expo's send endpoint is open unless the
    /// project has opted into Enhanced Security, so Push.AccessToken stays optional.
    /// </summary>
    public async Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken ct)
    {
        var cfg = await _config.GetAsync(tenantId, ct);
        return string.Equals(cfg.Get("Push.Provider"), ProviderKey, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ProviderSendResult> SendAsync(ProviderMessage message, CancellationToken ct)
    {
        var cfg = await _config.GetAsync(message.TenantId, ct);
        var declared = cfg.Get("Push.Provider");

        if (!string.Equals(declared, ProviderKey, StringComparison.OrdinalIgnoreCase))
        {
            // Degrade to a durable, countable not_configured row — never throw.
            var reason = declared is null
                ? "No Push provider configured for this tenant (set Notifications/Push.Provider=expo)."
                : $"Tenant declared Push provider '{declared}', but only the 'expo' adapter is deployed.";
            return new ProviderSendResult(ProviderSendStatus.NotConfigured,
                ErrorCode: "provider_not_configured", ErrorMessage: reason);
        }

        // PushChannelDispatcher passes the device's push token as Destination, one call per device.
        var token = message.Destination?.Trim() ?? string.Empty;
        if (!IsExpoPushToken(token))
        {
            // A raw FCM/APNs token from some other client build can never be delivered by Expo.
            // TERMINAL, but deliberately NOT coded "unregistered": the dispatcher deletes device
            // rows on an unregistered verdict, and a token this adapter simply cannot address is
            // not evidence that the device is gone. The row survives and stays visible to an admin.
            return new ProviderSendResult(ProviderSendStatus.TerminalFailure,
                ErrorCode: "invalid_push_token",
                ErrorMessage: "Device token is not an Expo push token (expected ExponentPushToken[...]).");
        }

        var endpoint = cfg.Get("Push.Endpoint") ?? DefaultEndpoint;
        var accessToken = cfg.Get("Push.AccessToken");

        var payload = new ExpoPushMessage
        {
            To = token,
            Title = Truncate(message.Subject, 200),
            Body = Truncate(message.Body, 1000),
            Sound = "default",
            Priority = "high",
            // Android channels are created client-side in pushNotifications.ts ('default', 'approvals').
            ChannelId = string.Equals(message.Platform, "android", StringComparison.OrdinalIgnoreCase)
                ? "default"
                : null,
            // idempotencyKey: the dispatcher already makes this per-device unique. It is a SHA-256 hex
            // prefix (NotificationService.ComputeDedupeKey) — opaque, so no PII rides in the payload.
            // type/entityName/entityId (W2-D S7): routing data for the app's getNotificationRoute.
            Data = BuildRoutingData(message),
        };

        var client = _httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            // POSTED AS A ONE-ELEMENT ARRAY, NOT A BARE OBJECT. Verified against the live endpoint:
            // exp.host echoes the shape it was given — a bare object yields {"data":{...}} while an
            // array yields {"data":[...]}. The array is Expo's documented batch form and is the only
            // one with a stable response shape, so it is what we send. ParseTicket below still
            // tolerates the object form so a shape change cannot brick the channel.
            Content = JsonContent.Create(new[] { payload }, options: Json),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            // Connection refused / DNS / TLS — we never handed the message over, so a retry is safe.
            // (OperationCanceledException is intentionally NOT caught: see the timeout contract above.)
            return new ProviderSendResult(ProviderSendStatus.TransientFailure, ErrorCode: "network_error",
                ErrorMessage: NotificationBodyPolicy.ScrubProviderError(ex.Message));
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                return new ProviderSendResult(ProviderSendStatus.TransientFailure,
                    ErrorCode: $"http_{(int)response.StatusCode}",
                    ErrorMessage: $"Expo push endpoint returned {(int)response.StatusCode}.");

            if (!response.IsSuccessStatusCode)
                return new ProviderSendResult(ProviderSendStatus.TerminalFailure,
                    ErrorCode: $"http_{(int)response.StatusCode}",
                    ErrorMessage: $"Expo push endpoint rejected the request with {(int)response.StatusCode}.");

            ExpoPushTicket? ticket;
            try
            {
                var raw = await response.Content.ReadAsStringAsync(ct);
                ticket = ParseTicket(raw);
            }
            catch (JsonException ex)
            {
                // 200 with a body we cannot read: Expo may well have accepted it. Ambiguous, and
                // PushChannelDispatcher.RetryOnAmbiguous is true, so it will be retried safely.
                _log.LogWarning(ex, "Expo push: unreadable 200 response for tenant {TenantId}.", message.TenantId);
                return new ProviderSendResult(ProviderSendStatus.Ambiguous, ErrorCode: "unreadable_response",
                    ErrorMessage: "Expo returned 200 with a body that could not be parsed.");
            }

            if (ticket is null)
                return new ProviderSendResult(ProviderSendStatus.Ambiguous, ErrorCode: "no_ticket",
                    ErrorMessage: "Expo returned 200 with no push ticket.");

            if (string.Equals(ticket.Status, "ok", StringComparison.OrdinalIgnoreCase))
                return new ProviderSendResult(ProviderSendStatus.Sent, Reference: ticket.Id ?? string.Empty);

            return MapTicketError(ticket);
        }
    }

    /// <summary>
    /// Maps an Expo ticket error to the port's vocabulary.
    ///
    /// The "unregistered" wording is LOAD-BEARING: <see cref="PushChannelDispatcher"/> prunes a
    /// device row only when the terminal ErrorCode contains "unregistered", and Expo spells the
    /// condition "DeviceNotRegistered". Renaming this code silently disables dead-token pruning.
    /// </summary>
    private static ProviderSendResult MapTicketError(ExpoPushTicket ticket)
    {
        var detail = ticket.Details?.Error ?? string.Empty;
        var scrubbed = NotificationBodyPolicy.ScrubProviderError(ticket.Message ?? detail);

        return detail switch
        {
            "DeviceNotRegistered" => new ProviderSendResult(ProviderSendStatus.TerminalFailure,
                ErrorCode: "device_unregistered", ErrorMessage: scrubbed),
            "MessageRateExceeded" => new ProviderSendResult(ProviderSendStatus.TransientFailure,
                ErrorCode: "rate_limited", ErrorMessage: scrubbed),
            "MessageTooBig" => new ProviderSendResult(ProviderSendStatus.TerminalFailure,
                ErrorCode: "message_too_big", ErrorMessage: scrubbed),
            "MismatchSenderId" or "InvalidCredentials" => new ProviderSendResult(
                ProviderSendStatus.TerminalFailure, ErrorCode: "provider_credentials",
                ErrorMessage: scrubbed),
            _ => new ProviderSendResult(ProviderSendStatus.TerminalFailure,
                ErrorCode: string.IsNullOrEmpty(detail) ? "expo_error" : detail, ErrorMessage: scrubbed),
        };
    }

    /// <summary>
    /// Reads the single push ticket out of an Expo response, accepting BOTH documented shapes:
    /// <c>{"data":[{...}]}</c> (what a batch post returns, which is what we send) and
    /// <c>{"data":{...}}</c> (what a bare-object post returns). One device per call, so exactly one
    /// ticket is expected either way. Returns null when there is no ticket at all — the caller turns
    /// that into an Ambiguous result rather than guessing.
    /// </summary>
    internal static ExpoPushTicket? ParseTicket(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return null;
        using var doc = JsonDocument.Parse(rawBody);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

        var element = data.ValueKind switch
        {
            JsonValueKind.Array => data.GetArrayLength() > 0 ? data[0] : (JsonElement?)null,
            JsonValueKind.Object => data,
            _ => null,
        };
        return element is null ? null : element.Value.Deserialize<ExpoPushTicket>(Json);
    }

    // Event codes and entity names are business identifiers ("PAYSLIP_READY", "LeaveRequest.Notice",
    // "HRRequest"). Anything outside this shape is dropped rather than forwarded.
    private static readonly System.Text.RegularExpressions.Regex CodeShape =
        new(@"^[A-Za-z][A-Za-z0-9_.:\-]{0,63}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// W2-D (S7) — the Expo <c>data</c> object. It is shown to Expo's relay and, on some OS versions,
    /// readable from the lock screen, so it carries ONLY:
    ///   • idempotencyKey — opaque hash prefix;
    ///   • type          — the business event code, when it matches <see cref="CodeShape"/>;
    ///   • entityName    — the entity type name, same shape rule;
    ///   • entityId      — ONLY a GUID or a positive integer. Any other string (an email, a name,
    ///                      a date, an amount) is dropped, so a caller that put something readable
    ///                      in NotificationRequest.EntityId cannot leak it through this channel.
    /// No names, amounts or dates are ever copied from Subject/Body/RecipientName.
    /// </summary>
    internal static Dictionary<string, string> BuildRoutingData(ProviderMessage message)
    {
        var data = new Dictionary<string, string> { ["idempotencyKey"] = message.IdempotencyKey };
        if (IsCode(message.EventCode)) data["type"] = message.EventCode.Trim();
        if (IsCode(message.EntityName)) data["entityName"] = message.EntityName.Trim();
        if (IsOpaqueId(message.EntityId)) data["entityId"] = message.EntityId!.Trim();
        return data;
    }

    private static bool IsCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) && CodeShape.IsMatch(value.Trim());

    internal static bool IsOpaqueId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (Guid.TryParse(v, out _)) return true;
        return v.Length <= 18 && long.TryParse(v, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0;
    }

    /// <summary>Expo emits both spellings; both are valid and both must be accepted.</summary>
    internal static bool IsExpoPushToken(string token) =>
        !string.IsNullOrWhiteSpace(token)
        && token.EndsWith(']')
        && (token.StartsWith("ExponentPushToken[", StringComparison.Ordinal)
            || token.StartsWith("ExpoPushToken[", StringComparison.Ordinal));

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    // ── Wire contract ─────────────────────────────────────────────────────────

    private sealed class ExpoPushMessage
    {
        [JsonPropertyName("to")] public string To { get; init; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
        [JsonPropertyName("body")] public string Body { get; init; } = string.Empty;
        [JsonPropertyName("sound")] public string? Sound { get; init; }
        [JsonPropertyName("priority")] public string? Priority { get; init; }

        [JsonPropertyName("channelId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ChannelId { get; init; }

        [JsonPropertyName("data")] public Dictionary<string, string>? Data { get; init; }
    }

    internal sealed class ExpoPushTicket
    {
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
        [JsonPropertyName("details")] public ExpoPushTicketDetails? Details { get; init; }
    }

    internal sealed class ExpoPushTicketDetails
    {
        [JsonPropertyName("error")] public string? Error { get; init; }
    }
}
