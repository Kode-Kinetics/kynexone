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
    /// <summary>Stands in for a custom header value the caller may not see. The device update path treats it
    /// as "unchanged", so a form that round-trips it cannot overwrite the stored credential.</summary>
    public const string MaskedHeaderValue = "••••";

    /// <summary>The key that may see and change device configuration, including header values.</summary>
    public const string ConfigurePermission = "attendance.bulk_import";

    public static AttendanceDeviceDto Project(AttendanceDevice d, bool revealHeaderValues) => new(
        d.Id, d.TenantId, d.DeviceName, d.DeviceType, d.Vendor, d.SerialNumber,
        d.BranchId, d.LocationName, d.IpAddress, d.EndpointUrl, d.Port, d.ApiKeyReference,
        d.SyncMethod, d.SyncFrequency, d.AuthType,
        revealHeaderValues ? d.CustomHeadersJson : MaskHeaderValues(d.CustomHeadersJson), d.DeviceParametersJson,
        d.FieldMappingsJson, d.Notes, d.LastSyncStatus, d.LastSyncAtUtc, d.ErrorLog, d.IsActive,
        d.CreatedAtUtc, d.CreatedBy, d.UpdatedAtUtc, d.UpdatedBy, d.IsDeleted, d.DeletedAtUtc, d.DeletedBy,
        HasCredentials: !string.IsNullOrWhiteSpace(d.AuthCredentialsJson) && d.AuthCredentialsJson != "{}");

    /// <summary>Header names kept, every value replaced. Anything that is not a JSON object reveals nothing.</summary>
    public static string MaskHeaderValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            return headers is null ? "{}" : JsonSerializer.Serialize(headers.Keys.ToDictionary(k => k, _ => MaskedHeaderValue));
        }
        catch (JsonException) { return "{}"; }
    }

    /// <summary>
    /// The custom headers to store on an update: an incoming value equal to <see cref="MaskedHeaderValue"/>
    /// keeps the stored value for that header (or drops the header if nothing is stored), so a masked read
    /// sent back on save never replaces a real credential with the mask.
    /// </summary>
    public static string MergeMaskedHeaderValues(string? stored, string incoming)
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

public record WebPunchRequest(int EmployeeId, string PunchDirection, string? LocationName, decimal? Latitude, decimal? Longitude);

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
