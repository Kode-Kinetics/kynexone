using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

public class AttendanceService : IAttendanceService
{
    private readonly ZayraDbContext _db;
    private readonly INotificationService _notifications;
    private readonly IHttpClientFactory _httpClientFactory;
    // Per-request cache of the resolved tenant timezone so the per-employee/day loop
    // doesn't re-query localization settings on every call.
    private readonly Dictionary<Guid, TimeZoneInfo> _tzCache = new();

    public AttendanceService(ZayraDbContext db, INotificationService notifications, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _notifications = notifications;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Resolves the tenant's IANA timezone (e.g. "Asia/Riyadh") for converting shift
    /// wall-clock times to UTC. Falls back to UTC if unset or unrecognised.
    /// </summary>
    private async Task<TimeZoneInfo> ResolveTenantTimeZoneAsync(Guid tenantId, CancellationToken ct)
    {
        if (_tzCache.TryGetValue(tenantId, out var cached)) return cached;
        var tzId = await _db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Select(l => l.DefaultTimezone)
            .FirstOrDefaultAsync(ct);
        TimeZoneInfo tz;
        try { tz = string.IsNullOrWhiteSpace(tzId) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { tz = TimeZoneInfo.Utc; }
        _tzCache[tenantId] = tz;
        return tz;
    }

    public async Task<PagedResult<AttendanceDevice>> GetDevicesAsync(Guid tenantId, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.AttendanceDevices.Where(x => x.TenantId == tenantId && !x.IsDeleted).OrderBy(x => x.DeviceName);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<AttendanceDevice>(items, total, page, pageSize);
    }

    public Task<AttendanceDevice?> GetDeviceAsync(Guid tenantId, Guid id, CancellationToken ct) =>
        _db.AttendanceDevices.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);

    public async Task<AttendanceDevice> CreateDeviceAsync(Guid tenantId, AttendanceDeviceRequest request, RequestContext context, CancellationToken ct)
    {
        if (await _db.AttendanceDevices.AnyAsync(x => x.TenantId == tenantId && x.SerialNumber == request.SerialNumber && !x.IsDeleted, ct))
            throw new InvalidOperationException("Device serial number already exists.");

        var device = new AttendanceDevice { TenantId = tenantId, CreatedBy = context.UserId };
        ApplyDevice(device, request);
        _db.AttendanceDevices.Add(device);
        await Audit(tenantId, context, "attendance.device.created", "AttendanceDevice", device.Id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return device;
    }

    public async Task<AttendanceDevice?> UpdateDeviceAsync(Guid tenantId, Guid id, AttendanceDeviceRequest request, RequestContext context, CancellationToken ct)
    {
        var device = await GetDeviceAsync(tenantId, id, ct);
        if (device is null) return null;
        ApplyDevice(device, request);
        device.UpdatedAtUtc = DateTime.UtcNow;
        device.UpdatedBy = context.UserId;
        await Audit(tenantId, context, "attendance.device.updated", "AttendanceDevice", device.Id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return device;
    }

    public async Task<bool> DeleteDeviceAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken ct)
    {
        var device = await GetDeviceAsync(tenantId, id, ct);
        if (device is null) return false;
        device.IsDeleted = true;
        device.DeletedAtUtc = DateTime.UtcNow;
        device.DeletedBy = context.UserId;
        await Audit(tenantId, context, "attendance.device.deleted", "AttendanceDevice", device.Id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<AttendanceDeviceSyncLog?> TestConnectionAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken ct)
    {
        var device = await GetDeviceAsync(tenantId, id, ct);
        if (device is null) return null;

        string status;
        string errorMessage;

        if (!device.IsActive)
        {
            status = "Failed";
            errorMessage = "Device is marked inactive. Enable the device before testing.";
        }
        else if (device.SyncMethod?.Contains("Push", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Push devices call us; test validates the API key is configured
            var hasKey = !string.IsNullOrWhiteSpace(device.ApiKeyReference);
            status = hasKey ? "Success" : "Warning";
            errorMessage = hasKey
                ? "Push API device is configured. Key is set; the device will push punches to the webhook URL."
                : "No API key generated yet. Use Generate Key to create one and configure it on the biometric device.";
        }
        else if (device.SyncMethod?.Contains("Pull", StringComparison.OrdinalIgnoreCase) == true
                 && !string.IsNullOrWhiteSpace(device.EndpointUrl))
        {
            // Pull API: attempt an authenticated HTTP GET to the device's REST endpoint
            try
            {
                using var http = _httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                var url = BuildPollUrl(device);
                var req = BuildDeviceRequest(device, url);
                var response = await http.SendAsync(req, ct);
                status = response.IsSuccessStatusCode ? "Success" : $"HTTP {(int)response.StatusCode}";
                errorMessage = response.IsSuccessStatusCode
                    ? $"Device responded with HTTP {(int)response.StatusCode}. Connection OK. Auth: {device.AuthType ?? "None"}."
                    : $"Device returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Check endpoint URL and credentials.";
            }
            catch (TaskCanceledException)
            {
                status = "Failed";
                errorMessage = $"Connection timed out after 10 seconds. Check device IP/endpoint: {device.EndpointUrl}";
            }
            catch (Exception ex)
            {
                status = "Failed";
                errorMessage = $"Connection error: {ex.Message}";
            }
        }
        else if (!string.IsNullOrWhiteSpace(device.IpAddress))
        {
            // SDK / biometric device: TCP ping on standard biometric port (ZKTeco default: 4370)
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var portStr = device.EndpointUrl?.Contains(':') == true
                    ? device.EndpointUrl.Split(':').Last().Trim('/')
                    : null;
                var port = int.TryParse(portStr, out var p) ? p : 4370;
                using var tcpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tcpCts.CancelAfter(TimeSpan.FromSeconds(8));
                await tcp.ConnectAsync(device.IpAddress, port, tcpCts.Token);
                status = "Success";
                errorMessage = $"TCP connection to {device.IpAddress}:{port} succeeded.";
            }
            catch (Exception ex)
            {
                status = "Failed";
                errorMessage = $"TCP connection to {device.IpAddress} failed: {ex.Message}";
            }
        }
        else
        {
            status = "Warning";
            errorMessage = "No endpoint URL or IP address configured. Set an endpoint to enable real connectivity tests.";
        }

        var log = new AttendanceDeviceSyncLog
        {
            TenantId = tenantId,
            DeviceId = id,
            SyncMethod = "Test connection",
            Status = status,
            CompletedAtUtc = DateTime.UtcNow,
            ErrorMessage = errorMessage,
        };
        device.LastSyncStatus = status;
        device.LastSyncAtUtc = DateTime.UtcNow;
        device.ErrorLog = status == "Success" ? "" : errorMessage;
        _db.AttendanceDeviceSyncLogs.Add(log);
        await Audit(tenantId, context, "attendance.device.test_connection", "AttendanceDevice", id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return log;
    }

    public async Task<AttendanceDeviceSyncLog?> SyncDeviceAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken ct)
    {
        var device = await GetDeviceAsync(tenantId, id, ct);
        if (device is null) return null;

        string status;
        string errorMessage;
        int rawEventsReceived = 0;

        if (!device.IsActive)
        {
            status = "Failed";
            errorMessage = "Device is inactive. Cannot sync.";
        }
        else if (device.SyncMethod?.Contains("Push", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Push devices call us; manual sync is not applicable
            status = "Completed";
            errorMessage = "Push API device: sync is device-initiated. Configure the biometric device to POST punches to the webhook URL with the X-Device-Key header. No manual pull required.";
        }
        else if (device.SyncMethod?.Contains("Pull", StringComparison.OrdinalIgnoreCase) == true
                 && !string.IsNullOrWhiteSpace(device.EndpointUrl))
        {
            // Pull API: authenticated HTTP GET using device config (auth type, custom headers, device params)
            try
            {
                var devParams = TryParseJson(device.DeviceParametersJson);
                var timeoutSec = devParams.TryGetValue("timeout_seconds", out var ts) && int.TryParse(ts, out var t) ? t : 30;
                using var http = _httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(timeoutSec);
                var url = BuildPollUrl(device);
                var req = BuildDeviceRequest(device, url);
                var response = await http.SendAsync(req, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                {
                    status = "Completed";
                    rawEventsReceived = body.TrimStart().StartsWith('[') ? body.Split('{').Length - 1 : 0;
                    errorMessage = rawEventsReceived > 0
                        ? $"Device responded: {rawEventsReceived} potential records found. Configure field mappings to auto-process on next pull."
                        : $"Device endpoint responded HTTP 200. Configure 'poll_path' and field mappings in Device Parameters to capture records.";
                }
                else
                {
                    status = "Failed";
                    errorMessage = $"Device returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body[..Math.Min(500, body.Length)]}";
                }
            }
            catch (TaskCanceledException)
            {
                status = "Failed";
                errorMessage = $"Sync timed out. Increase timeout_seconds in Device Parameters or check endpoint: {device.EndpointUrl}";
            }
            catch (Exception ex)
            {
                status = "Failed";
                errorMessage = $"Sync error: {ex.Message}";
            }
        }
        else if (device.SyncMethod?.Contains("CSV", StringComparison.OrdinalIgnoreCase) == true
                 || device.SyncMethod?.Contains("SFTP", StringComparison.OrdinalIgnoreCase) == true
                 || device.SyncMethod?.Contains("Manual", StringComparison.OrdinalIgnoreCase) == true)
        {
            status = "Completed";
            errorMessage = "Manual sync method: upload attendance data via the CSV Import tab or SFTP pipeline.";
        }
        else
        {
            status = "Warning";
            errorMessage = "No endpoint URL configured for this sync method. Set an endpoint URL to enable pull sync.";
        }

        var log = new AttendanceDeviceSyncLog
        {
            TenantId = tenantId,
            DeviceId = id,
            SyncMethod = device.SyncMethod ?? "Unknown",
            Status = status,
            CompletedAtUtc = DateTime.UtcNow,
            RawEventsReceived = rawEventsReceived,
            ErrorMessage = errorMessage,
        };
        device.LastSyncStatus = status;
        device.LastSyncAtUtc = DateTime.UtcNow;
        device.ErrorLog = status == "Completed" ? "" : errorMessage;
        _db.AttendanceDeviceSyncLogs.Add(log);
        await Audit(tenantId, context, "attendance.device.sync_requested", "AttendanceDevice", id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return log;
    }

    public async Task<IReadOnlyCollection<AttendanceDeviceSyncLog>> GetSyncLogsAsync(Guid tenantId, Guid deviceId, CancellationToken ct) =>
        await _db.AttendanceDeviceSyncLogs.Where(x => x.TenantId == tenantId && x.DeviceId == deviceId).OrderByDescending(x => x.StartedAtUtc).Take(100).ToListAsync(ct);

    public async Task<AttendanceRawEvent> PushEventAsync(Guid tenantId, AttendanceRawEventRequest request, RequestContext context, CancellationToken ct)
    {
        var employee = await ResolveEmployee(tenantId, request.EmployeeId, request.EmployeeCode, ct);
        if (employee is null) throw new InvalidOperationException("Employee could not be mapped from attendance event.");
        var direction = NormalizeDirection(request.PunchDirection);
        var duplicate = await _db.AttendanceRawEvents.AnyAsync(x =>
            x.TenantId == tenantId && x.EmployeeId == employee.Id && x.PunchTimestampUtc == request.PunchTimestampUtc &&
            x.PunchDirection == direction && x.DeviceId == request.DeviceId, ct);
        if (duplicate) throw new InvalidOperationException("Duplicate attendance punch ignored.");

        var raw = new AttendanceRawEvent
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeCode,
            DeviceId = request.DeviceId,
            Source = Clean(request.Source, "API push"),
            PunchTimestampUtc = request.PunchTimestampUtc,
            PunchDirection = direction,
            LocationName = Clean(request.LocationName),
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            IpAddress = Clean(request.IpAddress ?? context.IpAddress),
            PhotoReference = Clean(request.PhotoReference),
            RawPayloadJson = CleanJson(request.RawPayloadJson),
            SyncBatchReference = Clean(request.SyncBatchReference),
            VerificationMethod = Clean(request.VerificationMethod, "API"),
            ConfidenceScore = request.ConfidenceScore,
            CreatedBy = context.UserId
        };
        _db.AttendanceRawEvents.Add(raw);
        await Audit(tenantId, context, "attendance.raw_event.created", "AttendanceRawEvent", raw.Id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return raw;
    }

    private static string HashKey(string key) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

    public async Task<DeviceKeyResult?> GenerateDeviceKeyAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken ct)
    {
        var device = await GetDeviceAsync(tenantId, id, ct);
        if (device is null) return null;
        var key = "knx_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        device.ApiKeyReference = HashKey(key); // store only the hash; plaintext returned once
        device.UpdatedAtUtc = DateTime.UtcNow;
        device.UpdatedBy = context.UserId;
        await Audit(tenantId, context, "attendance.device.key_generated", "AttendanceDevice", id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return new DeviceKeyResult(device.Id, device.DeviceName, key);
    }

    public async Task<DeviceIngestResult?> IngestByDeviceKeyAsync(string deviceKey, DeviceIngestRequest request, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceKey)) return null;
        var hash = HashKey(deviceKey.Trim());
        // IgnoreQueryFilters: this endpoint is [AllowAnonymous] — the device API key IS the
        // authentication token, so there is no JWT and DbContext._tenantId is null. The global
        // tenant filter is therefore already inactive; IgnoreQueryFilters here is explicit
        // documentation of that design. The lookup is intentionally cross-tenant: the device key
        // hash resolves which tenant owns the device. Every subsequent query in this method is
        // scoped to device.TenantId (a DB-sourced value, not a caller parameter).
        var device = await _db.AttendanceDevices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.ApiKeyReference == hash && !x.IsDeleted, ct);
        if (device is null || !device.IsActive) return null;
        var tenantId = device.TenantId;

        var syncLog = new AttendanceDeviceSyncLog { TenantId = tenantId, DeviceId = device.Id, SyncMethod = "Device push (webhook)", Status = "Started" };
        _db.AttendanceDeviceSyncLogs.Add(syncLog);

        var punches = request.Punches ?? Array.Empty<DeviceIngestPunch>();
        int accepted = 0, duplicates = 0, unmatched = 0;
        var matchedEmployees = new Dictionary<int, Employee>();
        var affectedDates = new HashSet<DateOnly>();

        foreach (var punch in punches)
        {
            var employee = await ResolveEmployee(tenantId, null, punch.EmployeeCode, ct);
            var direction = NormalizeDirection(punch.PunchDirection);
            // IgnoreQueryFilters: same null-tenantId context as above. Explicit
            // x.TenantId == tenantId predicate (where tenantId == device.TenantId, a DB value)
            // provides the tenant scope that the inactive global filter would otherwise give.
            var dup = await _db.AttendanceRawEvents.IgnoreQueryFilters().AnyAsync(x =>
                x.TenantId == tenantId && x.DeviceId == device.Id && x.PunchTimestampUtc == punch.PunchTimestampUtc &&
                x.PunchDirection == direction &&
                (employee == null ? x.EmployeeCode == punch.EmployeeCode : x.EmployeeId == employee.Id), ct);
            if (dup) { duplicates++; continue; }

            _db.AttendanceRawEvents.Add(new AttendanceRawEvent
            {
                TenantId = tenantId,
                EmployeeId = employee?.Id,
                EmployeeCode = employee?.EmployeeCode ?? Clean(punch.EmployeeCode),
                DeviceId = device.Id,
                Source = $"Device: {device.DeviceName}",
                PunchTimestampUtc = punch.PunchTimestampUtc,
                PunchDirection = direction,
                LocationName = Clean(device.LocationName),
                Latitude = punch.Latitude,
                Longitude = punch.Longitude,
                IpAddress = Clean(ip),
                PhotoReference = Clean(punch.PhotoReference),
                RawPayloadJson = CleanJson(punch.RawPayloadJson),
                SyncBatchReference = syncLog.Id.ToString(),
                VerificationMethod = Clean(punch.VerificationMethod, "Device"),
                ConfidenceScore = punch.ConfidenceScore,
            });
            accepted++;
            if (employee is null) unmatched++;
            else { matchedEmployees[employee.Id] = employee; affectedDates.Add(DateOnly.FromDateTime(punch.PunchTimestampUtc)); }
        }

        device.LastSyncStatus = "Completed";
        device.LastSyncAtUtc = DateTime.UtcNow;
        device.ErrorLog = unmatched > 0 ? $"{unmatched} punch(es) had no matching employee code." : "";
        syncLog.Status = "Completed";
        syncLog.CompletedAtUtc = DateTime.UtcNow;
        syncLog.RawEventsReceived = punches.Count;
        syncLog.RawEventsProcessed = accepted;
        await _db.SaveChangesAsync(ct);

        int processedDays = 0;
        if ((request.AutoProcess ?? true) && matchedEmployees.Count > 0 && affectedDates.Count > 0)
        {
            var policy = await _db.AttendancePolicies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive, ct) ?? DefaultPolicy(tenantId);
            var ctx = new RequestContext(ip, "device-ingest", null, tenantId);
            foreach (var emp in matchedEmployees.Values)
                foreach (var date in affectedDates)
                { await ProcessEmployeeDay(tenantId, emp, date, policy, ctx, ct); processedDays++; }
            await _db.SaveChangesAsync(ct);
        }

        return new DeviceIngestResult(punches.Count, accepted, duplicates, unmatched, processedDays, syncLog.Id);
    }

    public async Task<AttendanceImportBatch> ImportCsvAsync(Guid tenantId, ImportAttendanceRequest request, RequestContext context, CancellationToken ct)
    {
        var batch = new AttendanceImportBatch { TenantId = tenantId, FileName = request.FileName, CreatedBy = context.UserId, Status = "Processing" };
        _db.AttendanceImportBatches.Add(batch);
        var rows = request.CsvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rowNumber = 0;
        foreach (var row in rows)
        {
            rowNumber++;
            if (rowNumber == 1 && row.Contains("employee", StringComparison.OrdinalIgnoreCase)) continue;
            batch.TotalRows++;
            var cells = row.Split(',').Select(x => x.Trim()).ToArray();
            if (cells.Length < 3 || !DateTime.TryParse(cells[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var punchAt))
            {
                _db.AttendanceImportErrors.Add(new AttendanceImportError { TenantId = tenantId, ImportBatchId = batch.Id, RowNumber = rowNumber, RawRow = row, ErrorMessage = "Expected employeeCode,punchTimestamp,punchDirection." });
                batch.FailedRows++;
                continue;
            }
            try
            {
                await PushEventAsync(tenantId, new AttendanceRawEventRequest(null, cells[0], null, "CSV import", punchAt.ToUniversalTime(), cells[2], cells.ElementAtOrDefault(3), null, null, null, null, JsonSerializer.Serialize(cells), batch.Id.ToString(), cells.ElementAtOrDefault(4) ?? "CSV", null), context, ct);
                batch.ImportedRows++;
            }
            catch (Exception ex)
            {
                _db.AttendanceImportErrors.Add(new AttendanceImportError { TenantId = tenantId, ImportBatchId = batch.Id, RowNumber = rowNumber, RawRow = row, ErrorMessage = ex.Message });
                batch.FailedRows++;
            }
        }
        batch.Status = batch.FailedRows > 0 ? "CompletedWithErrors" : "Completed";
        await Audit(tenantId, context, "attendance.import.completed", "AttendanceImportBatch", batch.Id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return batch;
    }

    public async Task<PagedResult<AttendanceRawEvent>> GetRawEventsAsync(Guid tenantId, DateOnly? from, DateOnly? to, int? employeeId, bool? processed, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.AttendanceRawEvents.Where(x => x.TenantId == tenantId);
        if (from is not null)
        {
            var start = from.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(x => x.PunchTimestampUtc >= start);
        }
        if (to is not null)
        {
            var end = to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(x => x.PunchTimestampUtc < end);
        }
        if (employeeId is not null) query = query.Where(x => x.EmployeeId == employeeId);
        if (processed is not null) query = query.Where(x => x.IsProcessed == processed);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.PunchTimestampUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<AttendanceRawEvent>(items, total, page, pageSize);
    }

    public async Task<int> ProcessAsync(Guid tenantId, ProcessAttendanceRequest request, RequestContext context, CancellationToken ct)
    {
        var employees = await _db.Employees.Where(x => x.TenantId == tenantId && !x.IsDeleted && (request.EmployeeId == null || x.Id == request.EmployeeId)).ToListAsync(ct);
        var policy = await _db.AttendancePolicies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive, ct);
        if (policy is null)
        {
            policy = DefaultPolicy(tenantId);
            _db.AttendancePolicies.Add(policy);
            await _db.SaveChangesAsync(ct);
        }
        var processed = 0;
        for (var date = request.FromDate; date <= request.ToDate; date = date.AddDays(1))
        {
            foreach (var employee in employees)
            {
                await ProcessEmployeeDay(tenantId, employee, date, policy, context, ct);
                processed++;
            }
        }
        await Audit(tenantId, context, "attendance.processed", "AttendanceDailyRecord", $"{request.FromDate}:{request.ToDate}", ct);
        await _db.SaveChangesAsync(ct);
        return processed;
    }

    public async Task<PagedResult<AttendanceDailyDto>> GetDailyAsync(Guid tenantId, DateOnly? from, DateOnly? to, int? employeeId, string? status, int page, int pageSize, CancellationToken ct, IReadOnlyCollection<int>? scopeIds = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        from ??= today;
        to ??= today;
        var query = _db.AttendanceDailyRecords.Where(x => x.TenantId == tenantId && !x.IsDeleted && x.WorkDate >= from && x.WorkDate <= to);
        if (scopeIds is not null) query = query.Where(x => scopeIds.Contains(x.EmployeeId));
        else if (employeeId is not null) query = query.Where(x => x.EmployeeId == employeeId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.WorkDate).ThenBy(x => x.EmployeeName).Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.ToDto()).ToListAsync(ct);
        return new PagedResult<AttendanceDailyDto>(items, total, page, pageSize);
    }

    public async Task<IReadOnlyCollection<AttendanceMonthlyDto>> GetMonthlyAsync(Guid tenantId, int year, int month, int? employeeId, CancellationToken ct, IReadOnlyCollection<int>? scopeIds = null)
    {
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var query = _db.AttendanceDailyRecords.Where(x => x.TenantId == tenantId && x.WorkDate >= from && x.WorkDate <= to);
        if (scopeIds is not null) query = query.Where(x => scopeIds.Contains(x.EmployeeId));
        else if (employeeId is not null) query = query.Where(x => x.EmployeeId == employeeId);
        var records = await query.ToListAsync(ct);
        return records.GroupBy(x => new { x.EmployeeId, x.EmployeeName })
            .Select(g => new AttendanceMonthlyDto(g.Key.EmployeeId, g.Key.EmployeeName, g.Count(x => x.Status == "Present"), g.Count(x => x.Status == "Absent"), g.Count(x => x.LateMinutes > 0), g.Count(x => x.MissingPunch), g.Sum(x => x.OvertimeMinutes)))
            .OrderBy(x => x.EmployeeName).ToList();
    }

    public Task<AttendanceRawEvent> PunchAsync(Guid tenantId, WebPunchRequest request, string source, RequestContext context, CancellationToken ct) =>
        PushEventAsync(tenantId, new AttendanceRawEventRequest(request.EmployeeId, null, null, source, DateTime.UtcNow, request.PunchDirection, request.LocationName, request.Latitude, request.Longitude, context.IpAddress, null, null, "", source.Contains("mobile", StringComparison.OrdinalIgnoreCase) ? "Mobile" : "Web", null), context, ct);

    public async Task<AttendanceRegularizationRequest> CreateRegularizationAsync(Guid tenantId, RegularizationRequestDto request, RequestContext context, CancellationToken ct)
    {
        var hasPending = await _db.AttendanceRegularizationRequests
            .AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == request.EmployeeId
                && x.WorkDate == request.WorkDate && x.Status == "Submitted", ct);
        if (hasPending)
            throw new InvalidOperationException("A pending correction request already exists for this employee and date.");

        var reg = new AttendanceRegularizationRequest
        {
            TenantId = tenantId,
            EmployeeId = request.EmployeeId,
            WorkDate = request.WorkDate,
            RequestType = Clean(request.RequestType, "Missed punch"),
            RequestedInUtc = request.RequestedInUtc,
            RequestedOutUtc = request.RequestedOutUtc,
            Reason = Clean(request.Reason),
            Status = "Submitted",
            RequestedByUserId = context.UserId,
            PayrollLockChecked = await IsLocked(tenantId, request.WorkDate, ct)
        };
        _db.AttendanceRegularizationRequests.Add(reg);
        _db.AttendanceCorrectionApprovals.Add(new AttendanceCorrectionApproval { TenantId = tenantId, RegularizationRequestId = reg.Id, ApprovalLevel = "Manager" });
        _db.AttendanceCorrectionApprovals.Add(new AttendanceCorrectionApproval { TenantId = tenantId, RegularizationRequestId = reg.Id, ApprovalLevel = "HR" });
        await Audit(tenantId, context, "attendance.regularization.submitted", "AttendanceRegularizationRequest", reg.Id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        await _notifications.NotifyAsync(tenantId, null,
            "Correction Request Submitted",
            $"Attendance correction request submitted for {reg.WorkDate:yyyy-MM-dd} (employee {reg.EmployeeId}).",
            "AttendanceRegularizationRequest", reg.Id.ToString(), ct);
        return reg;
    }

    public async Task<PagedResult<AttendanceRegularizationRequest>> GetRegularizationAsync(Guid tenantId, int? employeeId, string? status, int page, int pageSize, CancellationToken ct, IReadOnlyCollection<int>? scopeIds = null)
    {
        var query = _db.AttendanceRegularizationRequests.Where(x => x.TenantId == tenantId);
        if (scopeIds is not null) query = query.Where(x => scopeIds.Contains(x.EmployeeId));
        else if (employeeId is not null) query = query.Where(x => x.EmployeeId == employeeId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<AttendanceRegularizationRequest>(items, total, page, pageSize);
    }

    public async Task<AttendanceRegularizationRequest?> ApproveRegularizationAsync(Guid tenantId, Guid id, RegularizationDecisionRequest request, RequestContext context, CancellationToken ct)
    {
        var reg = await _db.AttendanceRegularizationRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (reg is null) return null;
        if (context.UserId.HasValue && reg.RequestedByUserId == context.UserId)
            throw new InvalidOperationException("Cannot approve your own correction request.");
        if (reg.Status != "Submitted")
            throw new InvalidOperationException($"Request is in '{reg.Status}' status and cannot be approved.");
        if (reg.PayrollLockChecked)
            throw new InvalidOperationException("Attendance period is payroll locked.");
        var beforeStatus = reg.Status;
        reg.Status = "Approved";
        reg.DecidedAtUtc = DateTime.UtcNow;
        var approval = await _db.AttendanceCorrectionApprovals.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.RegularizationRequestId == id && x.ApprovalLevel == "HR", ct);
        if (approval is not null)
        {
            approval.Decision = "Approved";
            approval.Comments = Clean(request.Comments);
            approval.DecidedAtUtc = DateTime.UtcNow;
            approval.DecidedByUserId = context.UserId;
        }
        await ApplyRegularization(tenantId, reg, context, ct);
        await Audit(tenantId, context, "attendance.regularization.approved", "AttendanceRegularizationRequest", reg.Id.ToString(),
            JsonSerializer.Serialize(new { before = beforeStatus, after = "Approved" }), ct);
        await _db.SaveChangesAsync(ct);
        await _notifications.NotifyAsync(tenantId, reg.RequestedByUserId,
            "Correction Request Approved",
            $"Your attendance correction for {reg.WorkDate:yyyy-MM-dd} has been approved.",
            "AttendanceRegularizationRequest", reg.Id.ToString(), ct);
        return reg;
    }

    public async Task<AttendanceRegularizationRequest?> RejectRegularizationAsync(Guid tenantId, Guid id, RegularizationDecisionRequest request, RequestContext context, CancellationToken ct)
    {
        var reg = await _db.AttendanceRegularizationRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (reg is null) return null;
        if (reg.Status != "Submitted")
            throw new InvalidOperationException($"Request is in '{reg.Status}' status and cannot be rejected.");
        var beforeStatus = reg.Status;
        reg.Status = "Rejected";
        reg.DecidedAtUtc = DateTime.UtcNow;
        var approval = await _db.AttendanceCorrectionApprovals.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.RegularizationRequestId == id && x.ApprovalLevel == "Manager", ct);
        if (approval is not null)
        {
            approval.Decision = "Rejected";
            approval.Comments = Clean(request.Comments);
            approval.DecidedAtUtc = DateTime.UtcNow;
            approval.DecidedByUserId = context.UserId;
        }
        await Audit(tenantId, context, "attendance.regularization.rejected", "AttendanceRegularizationRequest", reg.Id.ToString(),
            JsonSerializer.Serialize(new { before = beforeStatus, after = "Rejected", reason = Clean(request.Comments) }), ct);
        await _db.SaveChangesAsync(ct);
        await _notifications.NotifyAsync(tenantId, reg.RequestedByUserId,
            "Correction Request Rejected",
            $"Your attendance correction for {reg.WorkDate:yyyy-MM-dd} has been rejected.",
            "AttendanceRegularizationRequest", reg.Id.ToString(), ct);
        return reg;
    }

    public async Task<AttendanceRegularizationRequest?> CancelRegularizationAsync(Guid tenantId, Guid id, string reason, RequestContext context, CancellationToken ct)
    {
        var reg = await _db.AttendanceRegularizationRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (reg is null) return null;
        if (reg.Status != "Submitted")
            throw new InvalidOperationException($"Request is in '{reg.Status}' status and cannot be cancelled.");
        var beforeStatus = reg.Status;
        reg.Status = "Cancelled";
        reg.DecidedAtUtc = DateTime.UtcNow;
        await Audit(tenantId, context, "attendance.regularization.cancelled", "AttendanceRegularizationRequest", reg.Id.ToString(),
            JsonSerializer.Serialize(new { before = beforeStatus, after = "Cancelled", reason }), ct);
        await _db.SaveChangesAsync(ct);
        await _notifications.NotifyAsync(tenantId, reg.RequestedByUserId,
            "Correction Request Cancelled",
            $"Attendance correction request for {reg.WorkDate:yyyy-MM-dd} has been cancelled.",
            "AttendanceRegularizationRequest", reg.Id.ToString(), ct);
        return reg;
    }

    public async Task<AttendanceDashboardDto> DashboardAsync(Guid tenantId, DateOnly date, CancellationToken ct)
    {
        var records = await _db.AttendanceDailyRecords.Where(x => x.TenantId == tenantId && x.WorkDate == date).ToListAsync(ct);
        var activeEmployees = await _db.Employees.CountAsync(x => x.TenantId == tenantId && x.Status == "Active" && !x.IsDeleted, ct);
        return new AttendanceDashboardDto(date, activeEmployees, records.Count(x => x.Status is "Present" or "Late" or "Half day"), records.Count(x => x.Status == "Absent"), records.Count(x => x.LateMinutes > 0), records.Count(x => x.MissingPunch), records.Count(x => x.OvertimeMinutes > 0), await _db.AttendanceDevices.CountAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.LastSyncStatus == "Failed", ct), await _db.AttendanceRegularizationRequests.CountAsync(x => x.TenantId == tenantId && x.Status == "Submitted", ct));
    }

    public async Task<IReadOnlyCollection<AttendanceDailyDto>> ReportDailyAsync(Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await _db.AttendanceDailyRecords.Where(x => x.TenantId == tenantId && x.WorkDate >= from && x.WorkDate <= to).OrderByDescending(x => x.WorkDate).Select(x => x.ToDto()).ToListAsync(ct);

    public Task<IReadOnlyCollection<AttendanceMonthlyDto>> ReportMonthlyAsync(Guid tenantId, int year, int month, CancellationToken ct) =>
        GetMonthlyAsync(tenantId, year, month, null, ct);

    public async Task<IReadOnlyCollection<AttendanceDailyDto>> ReportByStatusAsync(Guid tenantId, DateOnly from, DateOnly to, string status, CancellationToken ct) =>
        await _db.AttendanceDailyRecords.Where(x => x.TenantId == tenantId && x.WorkDate >= from && x.WorkDate <= to && x.Status == status).OrderByDescending(x => x.WorkDate).Select(x => x.ToDto()).ToListAsync(ct);

    public async Task<IReadOnlyCollection<AttendanceDailyDto>> ReportMissingPunchAsync(Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await _db.AttendanceDailyRecords.Where(x => x.TenantId == tenantId && x.WorkDate >= from && x.WorkDate <= to && x.MissingPunch).OrderByDescending(x => x.WorkDate).Select(x => x.ToDto()).ToListAsync(ct);

    public async Task<IReadOnlyCollection<AttendancePayrollSummaryDto>> PayrollSummaryAsync(Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var records = await _db.AttendanceDailyRecords.Where(x => x.TenantId == tenantId && x.WorkDate >= from && x.WorkDate <= to).ToListAsync(ct);
        return records
            .GroupBy(x => new { x.EmployeeId, x.EmployeeName })
            .Select(g => new AttendancePayrollSummaryDto(g.Key.EmployeeId, g.Key.EmployeeName, g.Sum(x => x.LateMinutes), g.Sum(x => x.EarlyExitMinutes), g.Count(x => x.Status == "Absent"), g.Sum(x => x.OvertimeMinutes), g.Any(x => x.IsPayrollLocked)))
            .OrderBy(x => x.EmployeeName).ToList();
    }

    public async Task<IReadOnlyCollection<AttendanceDeviceSyncDto>> DeviceSyncReportAsync(Guid tenantId, CancellationToken ct) =>
        await _db.AttendanceDevices.Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => new AttendanceDeviceSyncDto(x.Id, x.DeviceName, x.Vendor, x.LastSyncStatus, x.LastSyncAtUtc, x.ErrorLog)).ToListAsync(ct);

    public async Task<IReadOnlyCollection<AttendanceAIInsight>> GenerateInsightsAsync(Guid tenantId, CancellationToken ct)
    {
        var since = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-30));
        // Materialise first — EF Core cannot translate GroupBy with element access into SQL
        var missingRecords = await _db.AttendanceDailyRecords
            .Where(x => x.TenantId == tenantId && x.WorkDate >= since && x.MissingPunch)
            .Select(x => new { x.EmployeeId, x.EmployeeName })
            .ToListAsync(ct);
        var repeatedMissed = missingRecords
            .GroupBy(x => new { x.EmployeeId, x.EmployeeName })
            .Where(g => g.Count() >= 3)
            .ToList();
        foreach (var group in repeatedMissed)
        {
            var exists = await _db.AttendanceAIInsights.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == group.Key.EmployeeId && x.InsightType == "RepeatedMissedPunch" && !x.IsAcknowledged, ct);
            if (!exists)
            {
                _db.AttendanceAIInsights.Add(new AttendanceAIInsight
                {
                    TenantId = tenantId,
                    EmployeeId = group.Key.EmployeeId,
                    InsightType = "RepeatedMissedPunch",
                    Severity = "Medium",
                    Title = "Repeated missed punches",
                    Summary = $"{group.Key.EmployeeName} has {group.Count()} missed punch days in the last 30 days. Human review required before any payroll action.",
                    DataJson = JsonSerializer.Serialize(new { count = group.Count(), since })
                });
            }
        }
        await _db.SaveChangesAsync(ct);
        return await _db.AttendanceAIInsights.Where(x => x.TenantId == tenantId && !x.IsAcknowledged).OrderByDescending(x => x.CreatedAtUtc).Take(25).ToListAsync(ct);
    }

    private async Task ProcessEmployeeDay(Guid tenantId, Employee employee, DateOnly date, AttendancePolicy policy, RequestContext context, CancellationToken ct)
    {
        var start = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var events = await _db.AttendanceRawEvents.Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.PunchTimestampUtc >= start && x.PunchTimestampUtc < end).OrderBy(x => x.PunchTimestampUtc).ToListAsync(ct);
        var daily = await _db.AttendanceDailyRecords.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.WorkDate == date, ct);
        if (daily is null)
        {
            daily = new AttendanceDailyRecord { TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName, Department = employee.Department, Branch = employee.Branch, WorkDate = date };
            _db.AttendanceDailyRecords.Add(daily);
        }
        var inEvents = events.Where(x => x.PunchDirection is "In" or "Unknown").ToList();
        var outEvents = events.Where(x => x.PunchDirection is "Out").ToList();
        daily.FirstInUtc = inEvents.FirstOrDefault()?.PunchTimestampUtc ?? events.FirstOrDefault()?.PunchTimestampUtc;
        daily.LastOutUtc = outEvents.LastOrDefault()?.PunchTimestampUtc ?? (events.Count > 1 ? events.Last().PunchTimestampUtc : null);
        daily.MissingPunch = daily.FirstInUtc is null || daily.LastOutUtc is null;
        daily.BreakMinutes = daily.MissingPunch ? 0 : policy.BreakMinutes;
        daily.TotalWorkedMinutes = daily.FirstInUtc is not null && daily.LastOutUtc is not null ? Math.Max(0, (int)(daily.LastOutUtc.Value - daily.FirstInUtc.Value).TotalMinutes - policy.BreakMinutes) : 0;
        // Resolve the employee's scheduled shift for this date and convert its local
        // wall-clock start/end to UTC. Previously this hardcoded 09:00 *UTC* and ignored
        // the assigned shift entirely — for a GCC tenant (Asia/Riyadh) 09:00 UTC = noon
        // local, so every employee showed bogus late/early minutes.
        var tz = await ResolveTenantTimeZoneAsync(tenantId, ct);
        var shift = await _db.ShiftAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EmployeeId == employee.Id && a.AssignedDate == date)
            .Join(_db.ShiftDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId),
                  a => a.ShiftDefinitionId, d => d.Id, (a, d) => new { d.StartTime, d.EndTime })
            .FirstOrDefaultAsync(ct);
        var startLocalTime = shift?.StartTime ?? new TimeOnly(9, 0);
        // Local wall-clock shift start on this date (Unspecified kind → interpret in tenant tz).
        var startLocal = DateTime.SpecifyKind(date.ToDateTime(startLocalTime), DateTimeKind.Unspecified);
        var shiftStart = TimeZoneInfo.ConvertTimeToUtc(startLocal, tz);
        DateTime shiftEnd;
        if (shift is not null)
        {
            // Overnight shift (end <= start) rolls into the next calendar day.
            var endDate = shift.EndTime <= shift.StartTime ? date.AddDays(1) : date;
            var endLocal = DateTime.SpecifyKind(endDate.ToDateTime(shift.EndTime), DateTimeKind.Unspecified);
            shiftEnd = TimeZoneInfo.ConvertTimeToUtc(endLocal, tz);
        }
        else
        {
            shiftEnd = shiftStart.AddMinutes(policy.StandardWorkMinutes + policy.BreakMinutes);
        }
        daily.LateMinutes = daily.FirstInUtc is null ? 0 : Math.Max(0, (int)(daily.FirstInUtc.Value - shiftStart).TotalMinutes - policy.GraceMinutes);
        daily.EarlyExitMinutes = daily.LastOutUtc is null ? 0 : Math.Max(0, (int)(shiftEnd - daily.LastOutUtc.Value).TotalMinutes - policy.EarlyExitThresholdMinutes);
        daily.OvertimeMinutes = Math.Max(0, daily.TotalWorkedMinutes - policy.StandardWorkMinutes);
        daily.UndertimeMinutes = Math.Max(0, policy.StandardWorkMinutes - daily.TotalWorkedMinutes);
        daily.Status = daily.TotalWorkedMinutes == 0 ? "Absent" : daily.TotalWorkedMinutes < policy.HalfDayThresholdMinutes ? "Half day" : daily.LateMinutes > 0 ? "Late" : "Present";
        daily.ProcessedAtUtc = DateTime.UtcNow;
        daily.UpdatedAtUtc = DateTime.UtcNow;
        foreach (var raw in events) raw.IsProcessed = true;
        await UpsertLegacyRecord(tenantId, daily, ct);
        await UpsertImpacts(tenantId, daily, ct);
        await UpsertExceptions(tenantId, daily, ct);
    }

    private async Task ApplyRegularization(Guid tenantId, AttendanceRegularizationRequest reg, RequestContext context, CancellationToken ct)
    {
        if (reg.RequestedInUtc is not null)
            _db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = tenantId, EmployeeId = reg.EmployeeId, Source = "Manual HR correction", PunchTimestampUtc = reg.RequestedInUtc.Value, PunchDirection = "In", VerificationMethod = "Manual", RawPayloadJson = JsonSerializer.Serialize(reg), CreatedBy = context.UserId });
        if (reg.RequestedOutUtc is not null)
            _db.AttendanceRawEvents.Add(new AttendanceRawEvent { TenantId = tenantId, EmployeeId = reg.EmployeeId, Source = "Manual HR correction", PunchTimestampUtc = reg.RequestedOutUtc.Value, PunchDirection = "Out", VerificationMethod = "Manual", RawPayloadJson = JsonSerializer.Serialize(reg), CreatedBy = context.UserId });
        await ProcessAsync(tenantId, new ProcessAttendanceRequest(reg.WorkDate, reg.WorkDate, reg.EmployeeId), context, ct);
        var daily = await _db.AttendanceDailyRecords.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == reg.EmployeeId && x.WorkDate == reg.WorkDate, ct);
        if (daily is not null) daily.ManualCorrectionStatus = "Approved";
    }

    private async Task UpsertLegacyRecord(Guid tenantId, AttendanceDailyRecord daily, CancellationToken ct)
    {
        var record = await _db.AttendanceRecords.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == daily.EmployeeId && x.WorkDate == daily.WorkDate, ct);
        if (record is null)
        {
            record = new AttendanceRecord { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate };
            _db.AttendanceRecords.Add(record);
        }
        record.TimeIn = daily.FirstInUtc is null ? null : TimeOnly.FromDateTime(daily.FirstInUtc.Value);
        record.TimeOut = daily.LastOutUtc is null ? null : TimeOnly.FromDateTime(daily.LastOutUtc.Value);
        record.OvertimeHours = Math.Round(daily.OvertimeMinutes / 60m, 2);
        record.Status = daily.Status;
        record.Notes = daily.MissingPunch ? "Missing punch" : "";
    }

    private async Task UpsertImpacts(Guid tenantId, AttendanceDailyRecord daily, CancellationToken ct)
    {
        var existing = _db.AttendancePayrollImpacts.Where(x => x.TenantId == tenantId && x.EmployeeId == daily.EmployeeId && x.WorkDate == daily.WorkDate);
        _db.AttendancePayrollImpacts.RemoveRange(existing);
        if (daily.LateMinutes > 0) _db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate, ImpactType = "Late deduction", Minutes = daily.LateMinutes, DailyRecordId = daily.Id });
        if (daily.EarlyExitMinutes > 0) _db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate, ImpactType = "Early exit deduction", Minutes = daily.EarlyExitMinutes, DailyRecordId = daily.Id });
        if (daily.Status == "Absent") _db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate, ImpactType = "Absence deduction", Minutes = 480, DailyRecordId = daily.Id });
        if (daily.OvertimeMinutes > 0) _db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate, ImpactType = "Overtime payable", Minutes = daily.OvertimeMinutes, DailyRecordId = daily.Id });
    }

    private async Task UpsertExceptions(Guid tenantId, AttendanceDailyRecord daily, CancellationToken ct)
    {
        if (!daily.MissingPunch && daily.LateMinutes == 0) return;
        var type = daily.MissingPunch ? "MissingPunch" : "LateArrival";
        var exists = await _db.AttendanceExceptions.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == daily.EmployeeId && x.WorkDate == daily.WorkDate && x.ExceptionType == type && !x.IsResolved, ct);
        if (!exists) _db.AttendanceExceptions.Add(new AttendanceException { TenantId = tenantId, EmployeeId = daily.EmployeeId, DailyRecordId = daily.Id, WorkDate = daily.WorkDate, ExceptionType = type, Severity = daily.MissingPunch ? "High" : "Medium", Details = daily.MissingPunch ? "Missing in or out punch." : $"{daily.LateMinutes} late minutes." });
    }

    private async Task<Employee?> ResolveEmployee(Guid tenantId, int? employeeId, string? employeeCode, CancellationToken ct)
    {
        if (employeeId is not null) return await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted, ct);
        if (!string.IsNullOrWhiteSpace(employeeCode)) return await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeCode == employeeCode && !x.IsDeleted, ct);
        return null;
    }

    private async Task<bool> IsLocked(Guid tenantId, DateOnly date, CancellationToken ct) =>
        await _db.AttendanceLockPeriods.AnyAsync(x => x.TenantId == tenantId && x.PeriodStart <= date && x.PeriodEnd >= date && x.Status == "Locked", ct);

    private static AttendancePolicy DefaultPolicy(Guid tenantId) => new() { TenantId = tenantId, Code = "DEFAULT", Name = "Default attendance policy" };

    private static void ApplyDevice(AttendanceDevice device, AttendanceDeviceRequest request)
    {
        device.DeviceName = Clean(request.DeviceName);
        device.DeviceType = Clean(request.DeviceType);
        device.Vendor = Clean(request.Vendor);
        device.SerialNumber = Clean(request.SerialNumber);
        device.BranchId = request.BranchId;
        device.LocationName = Clean(request.LocationName);
        device.IpAddress = Clean(request.IpAddress);
        device.EndpointUrl = Clean(request.EndpointUrl);
        device.Port = request.Port;
        device.ApiKeyReference = Clean(request.ApiKeyReference);
        device.SyncMethod = Clean(request.SyncMethod, "Manual upload");
        device.SyncFrequency = Clean(request.SyncFrequency, "Manual");
        device.AuthType = Clean(request.AuthType, "None");
        device.AuthCredentialsJson = CleanJson(request.AuthCredentialsJson) ?? "{}";
        device.CustomHeadersJson = CleanJson(request.CustomHeadersJson) ?? "{}";
        device.DeviceParametersJson = CleanJson(request.DeviceParametersJson) ?? "{}";
        device.FieldMappingsJson = CleanJson(request.FieldMappingsJson) ?? "{}";
        device.Notes = Clean(request.Notes);
        device.IsActive = request.IsActive;
    }

    // Build an HttpRequestMessage with all auth and custom headers from the device config applied.
    private static HttpRequestMessage BuildDeviceRequest(AttendanceDevice device, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var creds = TryParseJson(device.AuthCredentialsJson);

        switch (device.AuthType?.ToLowerInvariant())
        {
            case "basicauth":
                if (creds.TryGetValue("username", out var u) && creds.TryGetValue("password", out var p))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{u}:{p}")));
                break;
            case "bearer":
                if (creds.TryGetValue("token", out var tok))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tok);
                break;
            case "customheader":
                if (creds.TryGetValue("headerName", out var hn) && creds.TryGetValue("headerValue", out var hv))
                    req.Headers.TryAddWithoutValidation(hn, hv);
                break;
            case "apikeyquery":
                // handled by caller — append to URL query string
                break;
        }

        foreach (var (k, v) in TryParseJson(device.CustomHeadersJson))
            req.Headers.TryAddWithoutValidation(k, v);

        return req;
    }

    private static Dictionary<string, string> TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch { return new(); }
    }

    // Resolve the effective poll URL: EndpointUrl + optional poll_path from DeviceParameters
    private static string BuildPollUrl(AttendanceDevice device)
    {
        var baseUrl = (device.EndpointUrl ?? "").TrimEnd('/');
        var devParams = TryParseJson(device.DeviceParametersJson);
        if (devParams.TryGetValue("poll_path", out var path) && !string.IsNullOrWhiteSpace(path))
            return baseUrl + "/" + path.TrimStart('/');
        return baseUrl;
    }

    private async Task Audit(Guid tenantId, RequestContext context, string action, string entity, string entityId, CancellationToken ct) =>
        await Audit(tenantId, context, action, entity, entityId, null, ct);

    private async Task Audit(Guid tenantId, RequestContext context, string action, string entity, string entityId, string? metadata, CancellationToken ct)
    {
        _db.AttendanceAuditLogs.Add(new AttendanceAuditLog { TenantId = tenantId, UserId = context.UserId, Action = action, EntityName = entity, EntityId = entityId, MetadataJson = metadata ?? "{}" });
        await Task.CompletedTask;
    }

    private static string NormalizeDirection(string? direction)
    {
        var clean = Clean(direction, "Unknown").Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        return clean.ToLowerInvariant() switch
        {
            "in" or "checkin" or "clockin" => "In",
            "out" or "checkout" or "clockout" => "Out",
            "breakin" => "BreakIn",
            "breakout" => "BreakOut",
            _ => "Unknown"
        };
    }

    private static string Clean(string? value, string fallback = "") => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string CleanJson(string? value) => string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim();
}
