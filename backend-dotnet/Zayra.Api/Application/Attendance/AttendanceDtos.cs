using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Attendance;

/// <summary>
/// Safe projection of AttendanceDevice — AuthCredentialsJson is omitted; replaced by HasCredentials bool.
/// Raw device credentials must never be serialised in API responses. CustomHeadersJson can carry credentials
/// too (an API key header), so its VALUES are shown only to a caller who may configure devices
/// (attendance.bulk_import); anyone else gets the header names with <see cref="MaskedHeaderValue"/>.
/// </summary>
public record AttendanceDeviceDto(
    Guid Id,
    Guid TenantId,
    string DeviceName,
    string DeviceType,
    string Vendor,
    string SerialNumber,
    Guid? BranchId,
    string LocationName,
    string IpAddress,
    string EndpointUrl,
    int? Port,
    string ApiKeyReference,
    string SyncMethod,
    string SyncFrequency,
    string AuthType,
    string CustomHeadersJson,
    string DeviceParametersJson,
    string FieldMappingsJson,
    string Notes,
    string LastSyncStatus,
    DateTime? LastSyncAtUtc,
    string ErrorLog,
    bool IsActive,
    DateTime CreatedAtUtc,
    Guid? CreatedBy,
    DateTime? UpdatedAtUtc,
    Guid? UpdatedBy,
    bool IsDeleted,
    DateTime? DeletedAtUtc,
    Guid? DeletedBy,
    bool HasCredentials)
{
    /// <summary>Stands in for a configuration value the caller may not see. The device update path treats it
    /// as "unchanged", so a form that round-trips it cannot overwrite the stored value.</summary>
    public const string MaskedHeaderValue = "••••";

    /// <summary>The key that may see and change device configuration, including its secrets.</summary>
    public const string ConfigurePermission = "attendance.bulk_import";

    /// <summary>
    /// <paramref name="revealSecrets"/> is true only for a caller holding <see cref="ConfigurePermission"/>.
    /// Anyone else gets custom header names with masked values, secret-looking device parameters masked, and
    /// the endpoint URL (also wherever it is quoted in the error log) without its userinfo or query string.
    /// </summary>
    public static AttendanceDeviceDto Project(AttendanceDevice d, bool revealSecrets) => new(
        d.Id, d.TenantId, d.DeviceName, d.DeviceType, d.Vendor, d.SerialNumber,
        d.BranchId, d.LocationName, d.IpAddress,
        revealSecrets ? d.EndpointUrl : RedactEndpointUrl(d.EndpointUrl), d.Port, d.ApiKeyReference,
        d.SyncMethod, d.SyncFrequency, d.AuthType,
        revealSecrets ? d.CustomHeadersJson : MaskHeaderValues(d.CustomHeadersJson),
        revealSecrets ? d.DeviceParametersJson : MaskDeviceParameters(d.DeviceParametersJson),
        d.FieldMappingsJson, d.Notes, d.LastSyncStatus, d.LastSyncAtUtc,
        revealSecrets ? d.ErrorLog : RedactQuotedUrl(d.ErrorLog, d.EndpointUrl), d.IsActive,
        d.CreatedAtUtc, d.CreatedBy, d.UpdatedAtUtc, d.UpdatedBy, d.IsDeleted, d.DeletedAtUtc, d.DeletedBy,
        HasCredentials: !string.IsNullOrWhiteSpace(d.AuthCredentialsJson) && d.AuthCredentialsJson != "{}");

    /// <summary>Header names kept, every value replaced. Anything that is not a JSON object reveals nothing.</summary>
    public static string MaskHeaderValues(string? json) => MaskJsonValues(json, _ => true);

    /// <summary>Device parameters with every secret-looking value (by key name) replaced.</summary>
    public static string MaskDeviceParameters(string? json) => MaskJsonValues(json, IsSecretLookingKey);

    private static readonly string[] SecretKeyFragments =
    {
        "pass", "pwd", "secret", "token", "key", "auth", "credential", "signature", "sig", "cookie", "session",
        "private", "bearer", "cert", "salt",
    };

    internal static bool IsSecretLookingKey(string key) =>
        SecretKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase));

    private static string MaskJsonValues(string? json, Func<string, bool> maskKey)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (values is null) return "{}";
            return JsonSerializer.Serialize(values.ToDictionary(
                kv => kv.Key,
                kv => maskKey(kv.Key) ? JsonSerializer.SerializeToElement(MaskedHeaderValue) : kv.Value));
        }
        catch (JsonException) { return "{}"; }
    }

    /// <summary>
    /// The JSON to store on an update: an incoming value equal to <see cref="MaskedHeaderValue"/> keeps the
    /// stored value for that key (or drops the key if nothing is stored), so a masked read sent back on save
    /// never replaces a real credential with the mask. Used for custom headers and device parameters.
    /// </summary>
    public static string MergeMaskedJsonValues(string? stored, string incoming)
    {
        // Parsed, not string-searched: the mask may arrive JSON-escaped ("\u2022").
        Dictionary<string, JsonElement>? next;
        try { next = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(incoming); }
        catch (JsonException) { return incoming; }
        if (next is null || !next.Values.Any(IsMask)) return incoming;

        Dictionary<string, JsonElement> current;
        try { current = string.IsNullOrWhiteSpace(stored) ? new() : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stored) ?? new(); }
        catch (JsonException) { current = new(); }

        var merged = new Dictionary<string, JsonElement>();
        foreach (var (name, value) in next)
        {
            if (IsMask(value))
            {
                if (current.TryGetValue(name, out var kept)) merged[name] = kept;
                continue;
            }
            merged[name] = value;
        }
        return JsonSerializer.Serialize(merged);
    }

    /// <summary>
    /// The auth credentials to store on an update. The API never returns stored credentials (only
    /// <see cref="HasCredentials"/>), so an edit form saves them blank; storing that would wipe the device's
    /// login on every edit. A blank or masked value keeps the stored value for that field, and an empty object
    /// keeps the stored credentials whole. Switching the auth type (to "None" or another scheme) drops them.
    /// </summary>
    public static string MergeBlankCredentials(string? stored, string incoming, string authType, string? previousAuthType = null)
    {
        if (string.Equals(authType, "None", StringComparison.OrdinalIgnoreCase)) return "{}";
        // A different auth scheme: the stored credentials belong to the old one and are dropped.
        if (previousAuthType is not null && !string.Equals(previousAuthType, authType, StringComparison.OrdinalIgnoreCase))
            return incoming;
        Dictionary<string, JsonElement> current;
        try { current = string.IsNullOrWhiteSpace(stored) ? new() : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stored) ?? new(); }
        catch (JsonException) { return incoming; }
        if (current.Count == 0) return incoming;

        Dictionary<string, JsonElement>? next;
        try { next = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(incoming); }
        catch (JsonException) { return incoming; }
        if (next is null || next.Count == 0) return stored!;

        var merged = new Dictionary<string, JsonElement>(next);
        foreach (var (name, value) in next)
        {
            var blank = value.ValueKind is JsonValueKind.Null
                || (value.ValueKind == JsonValueKind.String && (string.IsNullOrEmpty(value.GetString()) || value.GetString() == MaskedHeaderValue));
            if (!blank) continue;
            if (current.TryGetValue(name, out var kept)) merged[name] = kept;
            else merged.Remove(name);
        }
        return JsonSerializer.Serialize(merged);
    }

    /// <summary>The URL without its userinfo (user:password@) or query string (?api_key=...), each replaced by
    /// the mask. A value that is not an absolute URL but carries '@' or '?' is masked whole.</summary>
    public static string RedactEndpointUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? string.Empty;
        // Only a real http(s) URL is taken apart; "user:pw@host" parses as an absolute URI with scheme "user".
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return url.Contains('@') || url.Contains('?') ? MaskedHeaderValue : url;
        if (string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)) return url;
        var userInfo = string.IsNullOrEmpty(uri.UserInfo) ? string.Empty : MaskedHeaderValue + "@";
        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var query = string.IsNullOrEmpty(uri.Query) ? string.Empty : "?" + MaskedHeaderValue;
        return $"{uri.Scheme}://{userInfo}{authority}{uri.AbsolutePath}{query}";
    }

    /// <summary>
    /// The endpoint URL to store on an update. If the incoming URL carries the mask (a redacted read sent back),
    /// the stored userinfo and query string are put back in its place, so the mask is never stored.
    /// </summary>
    public static string MergeMaskedEndpointUrl(string? stored, string incoming)
    {
        if (!incoming.Contains(MaskedHeaderValue, StringComparison.Ordinal)) return incoming;
        if (string.Equals(incoming, RedactEndpointUrl(stored), StringComparison.Ordinal)) return stored ?? string.Empty;
        Uri.TryCreate(stored?.Trim() ?? string.Empty, UriKind.Absolute, out var storedUri);
        var userInfo = string.IsNullOrEmpty(storedUri?.UserInfo) ? string.Empty : storedUri!.UserInfo + "@";
        var query = string.IsNullOrEmpty(storedUri?.Query) ? string.Empty : storedUri!.Query;
        return incoming
            .Replace(MaskedHeaderValue + "@", userInfo, StringComparison.Ordinal)
            .Replace("?" + MaskedHeaderValue, query, StringComparison.Ordinal)
            .Replace(MaskedHeaderValue, string.Empty, StringComparison.Ordinal);
    }

    internal static string RedactQuotedUrl(string? text, string? url)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(url)) return text ?? string.Empty;
        var redacted = RedactEndpointUrl(url);
        return redacted == url ? text : text.Replace(url, redacted, StringComparison.Ordinal);
    }

    private static bool IsMask(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() == MaskedHeaderValue;
}

public record AttendanceDeviceRequest(
    [Required, MaxLength(160)] string DeviceName,
    [Required, MaxLength(80)] string DeviceType,
    [Required, MaxLength(80)] string Vendor,
    [Required, MaxLength(120)] string SerialNumber,
    Guid? BranchId,
    string? LocationName,
    string? IpAddress,
    string? EndpointUrl,
    int? Port,
    string? ApiKeyReference,
    string? SyncMethod,
    string? SyncFrequency,
    // Flexible configuration — all stored as JSON so new device parameters need no schema change.
    // (Historical note: this used to say MissingTableCreator adds columns on first deploy. That type
    // was MySQL-only boot-time DDL, never wired on Postgres, and has been deleted. Schema changes go
    // through an EF migration and the pre-deploy migration gate — nothing issues DDL at startup.)
    string? AuthType,
    string? AuthCredentialsJson,
    string? CustomHeadersJson,
    string? DeviceParametersJson,
    string? FieldMappingsJson,
    string? Notes,
    bool IsActive = true);

public record AttendanceRawEventRequest(
    int? EmployeeId,
    string? EmployeeCode,
    Guid? DeviceId,
    string? Source,
    DateTime PunchTimestampUtc,
    string? PunchDirection,
    string? LocationName,
    decimal? Latitude,
    decimal? Longitude,
    string? IpAddress,
    string? PhotoReference,
    string? RawPayloadJson,
    string? SyncBatchReference,
    string? VerificationMethod,
    decimal? ConfidenceScore);

/// <summary>
/// An employee self-punch (punch/web, punch/mobile, punch/kiosk). Selfie attendance v2 adds the device's location
/// accuracy and mock flag (read by the server-side geofence) and <see cref="EvidenceId"/>, the opaque single-use id
/// returned by POST /api/attendance/evidence/selfie — never a storage key.
/// <para><see cref="VerificationMethod"/>, <see cref="ConfidenceScore"/> and <see cref="ClientBiometricVerified"/> are
/// accepted so older clients still bind, and are IGNORED: the server stores what it verified itself.</para>
/// <para><see cref="MockDetection"/> is <c>Supported</c> (Android: <see cref="LocationMocked"/> is meaningful) or
/// <c>Unsupported</c> (iOS cannot detect a mocked location). Under an enforced geofence a punch/mobile request with
/// neither field is from an old app and is refused.</para>
/// </summary>
public record WebPunchRequest(
    int EmployeeId,
    string PunchDirection,
    string? LocationName,
    decimal? Latitude,
    decimal? Longitude,
    decimal? AccuracyMeters = null,
    bool? LocationMocked = null,
    Guid? EvidenceId = null,
    string? VerificationMethod = null,
    decimal? ConfidenceScore = null,
    bool? ClientBiometricVerified = null,
    string? MockDetection = null);

/// <summary>
/// What the server verified for a punch, handed to the write so it is stored with the raw event.
/// <see cref="GeofenceFellBackToAllSites"/> and <see cref="MockDetectionUnavailable"/> are audited with the punch: the
/// employee matched no site of their own, or their phone (iOS) cannot report a mocked location.
/// </summary>
public sealed record PunchVerification(string Method, Guid? EvidenceId, string? GeofenceSite, double? DistanceMeters,
    bool GeofenceFellBackToAllSites = false, bool MockDetectionUnavailable = false)
{
    public static readonly PunchVerification Unverified = new(Zayra.Api.Models.AttendanceVerificationMethods.None, null, null, null);
}

// ── Device-key-authenticated ingest (generic webhook connector) ──────────────
public record DeviceIngestPunch(
    string EmployeeCode,
    DateTime PunchTimestampUtc,
    string? PunchDirection,
    string? VerificationMethod,
    decimal? ConfidenceScore,
    decimal? Latitude,
    decimal? Longitude,
    string? PhotoReference,
    string? RawPayloadJson);

public record DeviceIngestRequest(IReadOnlyList<DeviceIngestPunch> Punches, bool? AutoProcess);

public record DeviceIngestResult(int Received, int Accepted, int Duplicates, int Unmatched, int Processed, Guid SyncLogId);

public record DeviceKeyResult(Guid DeviceId, string DeviceName, string ApiKey);

public record ImportAttendanceRequest(string FileName, string CsvContent);
public record ProcessAttendanceRequest(DateOnly FromDate, DateOnly ToDate, int? EmployeeId);
public record RegularizationRequestDto(int EmployeeId, DateOnly WorkDate, string RequestType, DateTime? RequestedInUtc, DateTime? RequestedOutUtc, string Reason);
public record RegularizationDecisionRequest(string Comments);

public record AttendanceDailyDto(
    Guid Id,
    int EmployeeId,
    string EmployeeName,
    string Department,
    string Branch,
    DateOnly WorkDate,
    DateTime? FirstInUtc,
    DateTime? LastOutUtc,
    int TotalWorkedMinutes,
    int LateMinutes,
    int EarlyExitMinutes,
    int OvertimeMinutes,
    int UndertimeMinutes,
    bool MissingPunch,
    string Status,
    string ManualCorrectionStatus,
    bool IsPayrollLocked);

public record AttendanceDashboardDto(
    DateOnly Date,
    int ActiveEmployees,
    int Present,
    int Absent,
    int Late,
    int MissingPunch,
    int OvertimeEmployees,
    int DeviceErrors,
    int PendingRegularizations);

public record AttendanceMonthlyDto(int EmployeeId, string EmployeeName, int PresentDays, int AbsentDays, int LateDays, int MissingPunchDays, int OvertimeMinutes);
public record AttendancePayrollSummaryDto(int EmployeeId, string EmployeeName, int LateMinutes, int EarlyExitMinutes, int AbsenceDays, int OvertimeMinutes, bool HasLockedRecords);
public record AttendanceDeviceSyncDto(Guid DeviceId, string DeviceName, string Vendor, string Status, DateTime? LastSyncAtUtc, string ErrorLog);

public static class AttendanceMappings
{
    public static AttendanceDailyDto ToDto(this AttendanceDailyRecord r) => new(
        r.Id, r.EmployeeId, r.EmployeeName, r.Department, r.Branch, r.WorkDate, r.FirstInUtc, r.LastOutUtc,
        r.TotalWorkedMinutes, r.LateMinutes, r.EarlyExitMinutes, r.OvertimeMinutes, r.UndertimeMinutes,
        r.MissingPunch, r.Status, r.ManualCorrectionStatus, r.IsPayrollLocked);
}
