using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// Limits on the anonymous device webhook (<c>POST /api/attendance/ingest</c>), read from configuration section
/// <c>Attendance:DeviceIngest</c> (environment: <c>Attendance__DeviceIngest__MaxBatchSize</c> ...). Every value has a safe
/// default, so an unconfigured environment is already bounded.
/// </summary>
public sealed class DeviceIngestOptions
{
    /// <summary>Most punches one request may carry. Above it the whole request is refused (HTTP 400, <c>batch_too_large</c>).</summary>
    public int MaxBatchSize { get; init; } = 500;
    /// <summary>A punch more than this many minutes ahead of server time is rejected (a wrong device clock, or abuse).</summary>
    public int MaxFutureMinutes { get; init; } = 10;
    /// <summary>A punch older than this many days is rejected. Generous on purpose: a device that was offline for months
    /// still delivers a backlog of weeks; a punch from months ago is not an attendance event, it is a mistake. Raise
    /// <c>Attendance:DeviceIngest:MaxPastDays</c> for a deliberate catch-up run; the default is short because device
    /// punches feed payroll.</summary>
    public int MaxPastDays { get; init; } = 45;
    /// <summary>How far a signed request's timestamp may differ from server time (replay window).</summary>
    public int SignatureWindowSeconds { get; init; } = 300;
    /// <summary>Requests per device key per minute (the <c>device_ingest</c> rate-limit policy).</summary>
    public int RequestsPerMinute { get; init; } = 120;
    /// <summary>Requests per client address per minute across ALL keys on the anonymous device endpoints (H2). The per-key
    /// limiter alone gives a sprayer a fresh bucket for every random key.</summary>
    public int RequestsPerMinutePerIp { get; init; } = 600;
    /// <summary>Failed (unknown-key / bad-signature) device requests one address may make per minute before it is refused
    /// without touching the database.</summary>
    public int FailedAuthPerMinutePerIp { get; init; } = 30;
    /// <summary>Server-side key material for per-device signing secrets. Null disables the signing feature (devices keep
    /// the static key). A device's secret is derived from this and the device id, so no secret is stored at rest.</summary>
    public string? SigningMasterKey { get; init; }

    /// <summary>Minimum master-key length in bytes. A short key makes every derived device secret guessable.</summary>
    public const int MinMasterKeyBytes = 32;

    /// <summary>Null/blank = signing disabled. A configured key shorter than <see cref="MinMasterKeyBytes"/> stops the
    /// host at startup rather than silently deriving weak secrets.</summary>
    public static string? ValidMasterKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (Encoding.UTF8.GetByteCount(key) < MinMasterKeyBytes)
            throw new InvalidOperationException(
                $"Attendance:DeviceIngest:SigningMasterKey must be at least {MinMasterKeyBytes} bytes. Generate one with 'openssl rand -hex 32'.");
        return key;
    }

    public static DeviceIngestOptions From(IConfiguration config)
    {
        var s = config.GetSection("Attendance:DeviceIngest");
        var d = new DeviceIngestOptions();
        return new DeviceIngestOptions
        {
            MaxBatchSize = Math.Clamp(s.GetValue("MaxBatchSize", d.MaxBatchSize), 1, 5000),
            MaxFutureMinutes = Math.Max(0, s.GetValue("MaxFutureMinutes", d.MaxFutureMinutes)),
            MaxPastDays = Math.Max(1, s.GetValue("MaxPastDays", d.MaxPastDays)),
            SignatureWindowSeconds = Math.Max(30, s.GetValue("SignatureWindowSeconds", d.SignatureWindowSeconds)),
            RequestsPerMinute = Math.Max(1, s.GetValue("RequestsPerMinute", d.RequestsPerMinute)),
            RequestsPerMinutePerIp = Math.Max(1, s.GetValue("RequestsPerMinutePerIp", d.RequestsPerMinutePerIp)),
            FailedAuthPerMinutePerIp = Math.Max(1, s.GetValue("FailedAuthPerMinutePerIp", d.FailedAuthPerMinutePerIp)),
            SigningMasterKey = ValidMasterKey(s["SigningMasterKey"]),
        };
    }
}

/// <summary>The raw material of a signed request: what the controller read off the wire.</summary>
public sealed record DeviceRequestEnvelope(string? Timestamp, string? Signature, string RawBody, string? BridgeVersion = null,
    string? Method = null, string? Path = null, string? Nonce = null);

/// <summary>A device presented a bad or missing signature, or one outside the replay window (HTTP 401).</summary>
public sealed class DeviceSignatureException(string message) : InvalidOperationException(message);

/// <summary>
/// Per-device request signing (v2). The scheme (matches <c>kynexbridge/src/signing.js</c>): headers
/// <c>X-Device-Timestamp</c> (unix seconds), <c>X-Device-Nonce</c> (unique per request) and
/// <c>X-Device-Signature: v2=&lt;hex HMAC-SHA256(secret, canonical)&gt;</c> where canonical is
/// <c>"v2\n{METHOD}\n{path}\n{timestamp}\n{nonce}\n{hex SHA-256(rawBody)}"</c>. Binding method and path stops a signed
/// ingest body verifying on heartbeat; the nonce (remembered for the replay window) stops a captured request being replayed.
/// The secret never leaves the server except once, at rotation.
/// </summary>
public static class DeviceSigning
{
    public const string Prefix = "v2=";

    public static string DeriveSecret(string masterKey, Guid deviceId, int version)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(masterKey),
            Encoding.UTF8.GetBytes($"kynexone-device-signing|v1|{deviceId:N}|{version}"));
        return "kxs_" + Convert.ToHexString(mac).ToLowerInvariant();
    }

    public static string Canonical(string method, string path, long timestampSeconds, string nonce, string rawBody) =>
        $"v2\n{method.ToUpperInvariant()}\n{path}\n{timestampSeconds}\n{nonce}\n"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();

    public static string Sign(string secret, long timestampSeconds, string nonce, string method, string path, string rawBody) =>
        Prefix + Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(Canonical(method, path, timestampSeconds, nonce, rawBody)))).ToLowerInvariant();

    /// <summary>Throws <see cref="DeviceSignatureException"/> unless the envelope carries a fresh, correct signature.
    /// Replay protection (nonce) is applied by the caller via <see cref="DeviceNonceCache"/> AFTER this succeeds.</summary>
    public static void Verify(string secret, DeviceRequestEnvelope envelope, DateTimeOffset now, int windowSeconds)
    {
        if (string.IsNullOrWhiteSpace(envelope.Signature) || string.IsNullOrWhiteSpace(envelope.Timestamp)
            || string.IsNullOrWhiteSpace(envelope.Nonce))
            throw new DeviceSignatureException("This device requires a signed request (X-Device-Timestamp, X-Device-Nonce and X-Device-Signature).");
        if (!long.TryParse(envelope.Timestamp, out var ts))
            throw new DeviceSignatureException("X-Device-Timestamp must be unix seconds.");
        if (envelope.Nonce.Length > 128)
            throw new DeviceSignatureException("X-Device-Nonce is too long.");
        if (Math.Abs(now.ToUnixTimeSeconds() - ts) > windowSeconds)
            throw new DeviceSignatureException("The request timestamp is outside the allowed window. Check the bridge host's clock.");
        var expected = Encoding.ASCII.GetBytes(Sign(secret, ts, envelope.Nonce, envelope.Method ?? string.Empty,
            envelope.Path ?? string.Empty, envelope.RawBody));
        var given = Encoding.ASCII.GetBytes(envelope.Signature.Trim().ToLowerInvariant());
        if (!CryptographicOperations.FixedTimeEquals(expected, given))
            throw new DeviceSignatureException("The request signature is invalid.");
    }
}

/// <summary>Remembers (device, nonce) for the replay window so a captured signed request cannot be sent again. In-process:
/// each API instance remembers its own; with several instances a replay to a different instance is bounded only by the
/// timestamp window (documented in DEVICE_INTEGRATION.md).</summary>
public sealed class DeviceNonceCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;
    public static readonly DeviceNonceCache Shared = new();

    /// <summary>True the first time a nonce is seen for the device inside the window; false on a replay.</summary>
    public bool TryRegister(Guid deviceId, string nonce, DateTimeOffset now, int windowSeconds)
    {
        var expiry = now.AddSeconds(windowSeconds * 2 + 1);   // the timestamp window is +/- windowSeconds
        if (now - _lastSweep > TimeSpan.FromSeconds(30))
        {
            _lastSweep = now;
            foreach (var kv in _seen) if (kv.Value < now) _seen.TryRemove(kv.Key, out _);
        }
        var key = $"{deviceId:N}|{nonce}";
        var added = true;
        _seen.AddOrUpdate(key, expiry, (_, old) => { if (old >= now) added = false; return added ? expiry : old; });
        return added;
    }
}

/// <summary>Idempotency key of one device punch. Derived from what the DEVICE reported (device, employee code as sent,
/// instant, direction), not from the matched employee, so it is the same whether or not the code is mapped yet — which is
/// what makes unmatched punches (employee id NULL, invisible to the old unique index) de-duplicate.</summary>
public static class DevicePunchKey
{
    public static string Compute(Guid deviceId, string employeeCode, DateTime punchUtc, string direction)
    {
        var utc = punchUtc.Kind == DateTimeKind.Local ? punchUtc.ToUniversalTime() : DateTime.SpecifyKind(punchUtc, DateTimeKind.Utc);
        var micros = utc.Ticks / 10;          // Postgres timestamptz keeps microseconds; sub-microsecond noise must not defeat the key
        var text = $"{deviceId:N}|{employeeCode.Trim().ToUpperInvariant()}|{micros}|{direction}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}

/// <summary>Counts failed device authentications per client address in a one-minute window so key spraying is cut off
/// before the key-hash lookup (H2).</summary>
public sealed class DeviceAuthFailureThrottle
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Window, int Count)> _fails = new(StringComparer.Ordinal);
    public static readonly DeviceAuthFailureThrottle Shared = new();

    private static long Window(DateTimeOffset now) => now.ToUnixTimeSeconds() / 60;

    public bool IsBlocked(string ip, DateTimeOffset now, int limit) =>
        _fails.TryGetValue(ip, out var v) && v.Window == Window(now) && v.Count >= limit;

    public void RecordFailure(string ip, DateTimeOffset now)
    {
        var w = Window(now);
        _fails.AddOrUpdate(ip, (w, 1), (_, v) => v.Window == w ? (w, v.Count + 1) : (w, 1));
        if (_fails.Count > 10_000)
            foreach (var kv in _fails) if (kv.Value.Window != w) _fails.TryRemove(kv.Key, out _);
    }
}
