using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.WorkWeek;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

public class AttendanceService : IAttendanceService
{
    private readonly ZayraDbContext _db;
    private readonly INotificationService _notifications;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WorkWeekService _workWeek;
    // KSA Labour Law Art. 98 — the Ramadan working-hours baseline. Constructed here rather than
    // injected for the same reason WorkWeekService is: this service is resolved in several places
    // with a hand-rolled constructor call, and widening the signature would break all of them.
    // StatutoryRuleReader memoizes per instance, so one instance per AttendanceService is correct.
    private readonly KsaWorkingHoursBaselineService _ksaWorkingHours;
    // Per-request cache of the resolved tenant timezone so the per-employee/day loop
    // doesn't re-query localization settings on every call.
    private readonly Dictionary<Guid, TimeZoneInfo> _tzCache = new();
    // Per-request cache of each legal entity's country code, for the same reason: the Art. 98
    // baseline is resolved once per employee-day and the company row never changes mid-run.
    private readonly Dictionary<Guid, string> _companyCountryCache = new();

    public AttendanceService(ZayraDbContext db, INotificationService notifications, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _notifications = notifications;
        _httpClientFactory = httpClientFactory;
        _workWeek = new WorkWeekService(db);
        _ksaWorkingHours = new KsaWorkingHoursBaselineService(
            new StatutoryRuleReader(db), new HijriDateService());
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

    /// <summary>
    /// The EMPLOYING COMPANY's country code — the jurisdiction whose labour law governs this
    /// employee's working hours.
    ///
    /// <para>KSA Art. 98 used to be gated on <c>Employee.CountryCode</c>, which is a PERSONAL field
    /// (the employee's own country) that defaults to <see cref="string.Empty"/> and is routinely
    /// never filled in. That gate was wrong in both directions at once: a Saudi company's employee
    /// with a blank or foreign country code was DENIED the reduced Ramadan baseline and therefore
    /// under-paid overtime, while an employee of a non-KSA entity who happened to carry "SA" on
    /// their personal record was GIVEN it and over-paid. Whether Art. 98 applies is a property of
    /// the employer's jurisdiction, exactly as it is for Art. 109 and Art. 117 — which resolve the
    /// company through <c>LeaveService.ResolveEmployeeCountryAsync</c>. This is the same resolution,
    /// so the three articles can no longer disagree about who is in KSA.</para>
    /// </summary>
    private async Task<string> ResolveCompanyCountryAsync(Guid tenantId, Guid? companyId, CancellationToken ct)
    {
        if (companyId is not Guid id) return string.Empty;
        if (_companyCountryCache.TryGetValue(id, out var cached)) return cached;

        // The company filter must be dropped, and it goes through ScopedBypass rather than a raw
        // .IgnoreQueryFilters() so the tenant predicate is re-applied for us and the intent is in
        // the type system rather than in a comment.
        var cc = await ScopedBypass.TenantWide(_db.Companies, tenantId,
            "Resolving an employee's legal entity in order to decide whether KSA Art. 98 Ramadan "
            + "reduced working hours apply is a SYSTEM/config read. It must succeed regardless of "
            + "the processing user's own company claims, because the article binds on the EMPLOYER's "
            + "jurisdiction, not on the caller's permissions — an HR user scoped to one entity must "
            + "not cause a different entity's employee to have overtime measured against the wrong "
            + "baseline. Only the country code is projected; no company data crosses the boundary. "
            + "Mirrors LeaveService.ResolveEmployeeCountryAsync, which does this for Art. 109/117.")
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => c.CountryCode)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        _companyCountryCache[id] = cc;
        return cc;
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
            var pullUrl = BuildPollUrl(device);
            // SSRF guard: the endpoint URL is tenant-supplied. Block internal/loopback/metadata targets.
            var (pullUrlOk, pullUrlReason) = await SsrfGuard.ValidateOutboundUrlAsync(pullUrl, ct);
            if (!pullUrlOk)
            {
                status = "Failed";
                errorMessage = $"Endpoint URL rejected: {pullUrlReason}";
            }
            else
            {
                try
                {
                    using var handler = SsrfGuard.CreateGuardedClientHandler(); // no auto-redirect (rebind defence)
                    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                    var req = BuildDeviceRequest(device, pullUrl);
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
        }
        else if (!string.IsNullOrWhiteSpace(device.IpAddress))
        {
            // SDK / biometric device: TCP ping on standard biometric port (ZKTeco default: 4370)
            // SSRF guard: device.IpAddress is tenant-supplied — block internal/loopback/metadata targets
            // so this cannot be used as an internal TCP port scanner.
            var (hostOk, hostReason) = await SsrfGuard.ValidateOutboundHostAsync(device.IpAddress, ct);
            if (!hostOk)
            {
                status = "Failed";
                errorMessage = $"Device address rejected: {hostReason}";
            }
            else
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
            var syncUrl = BuildPollUrl(device);
            // SSRF guard: the endpoint URL is tenant-supplied. Block internal/loopback/metadata targets.
            var (syncUrlOk, syncUrlReason) = await SsrfGuard.ValidateOutboundUrlAsync(syncUrl, ct);
            if (!syncUrlOk)
            {
                status = "Failed";
                errorMessage = $"Endpoint URL rejected: {syncUrlReason}";
            }
            else
            {
                try
                {
                    var devParams = TryParseJson(device.DeviceParametersJson);
                    var timeoutSec = devParams.TryGetValue("timeout_seconds", out var ts) && int.TryParse(ts, out var t) ? t : 30;
                    using var handler = SsrfGuard.CreateGuardedClientHandler(); // no auto-redirect (rebind defence)
                    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
                    var req = BuildDeviceRequest(device, syncUrl);
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
                        // Do NOT reflect the response body: with a user-controlled endpoint this would turn
                        // the SSRF into a full-read oracle. Status code only.
                        errorMessage = $"Device returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Check endpoint URL and credentials.";
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
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
            return await PushEventCoreAsync(tenantId, request, context, ct);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, ct);
            var created = await PushEventCoreAsync(tenantId, request, context, ct);
            await transaction.CommitAsync(ct);
            return created;
        });
    }

    private async Task<AttendanceRawEvent> PushEventCoreAsync(Guid tenantId, AttendanceRawEventRequest request, RequestContext context, CancellationToken ct)
    {
        var employee = await ResolveEmployee(tenantId, request.EmployeeId, request.EmployeeCode, ct);
        if (employee is null) throw new InvalidOperationException("Employee could not be mapped from attendance event.");
        var direction = NormalizeDirection(request.PunchDirection);

        // The nullable DeviceId in the unique index does not serialize two web/mobile writes on
        // PostgreSQL (NULL values are distinct). Serialize the exact logical punch before probing so
        // concurrent retries/double-clicks cannot both pass the read and insert two rows.
        if ((_db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
            && _db.Database.CurrentTransaction is not null)
        {
            var lockIdentity = $"attendance-punch:{tenantId:N}:{employee.Id}:{request.PunchTimestampUtc.ToUniversalTime().Ticks}:{direction}:{request.DeviceId?.ToString("N") ?? "self"}";
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockIdentity}, 0))", ct);
        }

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
        var tenantTimeZone = await ResolveTenantTimeZoneAsync(tenantId, ct);

        foreach (var punch in punches)
        {
            // Anonymous device webhook: no principal exists to scope by, so the company filter is
            // dropped deliberately and the tenant re-applied from the authenticated device's row.
            var employee = await ResolveEmployee(tenantId, null, punch.EmployeeCode, ct, bypassCompanyFilter: true);
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
            else
            {
                matchedEmployees[employee.Id] = employee;
                var localPunch = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(punch.PunchTimestampUtc, DateTimeKind.Utc), tenantTimeZone);
                affectedDates.Add(DateOnly.FromDateTime(localPunch));
            }
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
                {
                    if (await IsLocked(tenantId, date, ct)) continue;
                    await ProcessEmployeeDay(tenantId, emp, date, policy, ctx, ct);
                    processedDays++;
                }
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
        await ValidateProcessRangeAsync(tenantId, request.FromDate, request.ToDate, ct);

        var employees = await _db.Employees.Where(x => x.TenantId == tenantId && !x.IsDeleted
            && x.Status == EmployeeStatuses.Active
            && (request.EmployeeId == null || x.Id == request.EmployeeId)).ToListAsync(ct);
        var policies = await EnsureActivePoliciesAsync(tenantId, ct);
        var processed = 0;
        for (var date = request.FromDate; date <= request.ToDate; date = date.AddDays(1))
        {
            foreach (var employee in employees)
            {
                var policy = ResolveAttendancePolicy(employee, policies);
                await ProcessEmployeeDay(tenantId, employee, date, policy, context, ct);
                processed++;
            }
        }
        await Audit(tenantId, context, "attendance.processed", "AttendanceDailyRecord", $"{request.FromDate}:{request.ToDate}", ct);
        await _db.SaveChangesAsync(ct);
        return processed;
    }

    public async Task ValidateProcessRangeAsync(Guid tenantId, DateOnly fromDate, DateOnly toDate, CancellationToken ct)
    {
        if (fromDate > toDate)
            throw new InvalidOperationException("From date must be on or before To date.");
        if (toDate.DayNumber - fromDate.DayNumber > 366)
            throw new InvalidOperationException("Attendance processing is limited to 367 days per request.");
        if (await _db.AttendanceLockPeriods.AnyAsync(x => x.TenantId == tenantId
                && x.Status == "Locked" && x.PeriodStart <= toDate && x.PeriodEnd >= fromDate, ct))
            throw new InvalidOperationException("Attendance cannot be processed for a payroll-locked period.");
    }

    public async Task<IReadOnlyList<AttendancePolicy>> EnsureActivePoliciesAsync(Guid tenantId, CancellationToken ct)
    {
        var policies = await _db.AttendancePolicies
            .Where(x => x.TenantId == tenantId && x.IsActive)
            .ToListAsync(ct);
        if (policies.Count == 0)
        {
            var policy = DefaultPolicy(tenantId);
            _db.AttendancePolicies.Add(policy);
            await _db.SaveChangesAsync(ct);
            policies.Add(policy);
        }
        return policies;
    }

    public async Task<int> ProcessEmployeeRangeAsync(Guid tenantId, Employee employee, IReadOnlyCollection<AttendancePolicy> policies,
        DateOnly fromDate, DateOnly toDate, RequestContext context, CancellationToken ct)
    {
        if (employee.TenantId != tenantId)
            throw new InvalidOperationException("Employee does not belong to the tenant being processed.");
        var policy = ResolveAttendancePolicy(employee, policies);
        var days = 0;
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            await ProcessEmployeeDay(tenantId, employee, date, policy, context, ct);
            days++;
        }
        return days;
    }

    public async Task<PagedResult<AttendanceDailyDto>> GetDailyAsync(Guid tenantId, DateOnly? from, DateOnly? to, int? employeeId, string? status, int page, int pageSize, CancellationToken ct, IReadOnlyCollection<int>? scopeIds = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        // A PARAMETERLESS call defaults to a trailing window, not to today alone. Attendance only
        // exists for days that have HAPPENED — the demo seeders stop at yesterday, and a live
        // tenant's row appears only once that day's punches are processed — so a from==to==today
        // default made the unfiltered endpoint return an empty page on every tenant every morning.
        // A trailing window always contains the most recent real data, and because the projection
        // below is ordered by WorkDate descending, daily() with no arguments now yields the latest
        // records first, which is what "show me recent attendance" callers actually need.
        // An explicit from/to is untouched — both are still honoured exactly as before.
        from ??= today.AddDays(-60);
        to ??= today;
        var query = _db.AttendanceDailyRecords.Where(x => x.TenantId == tenantId && !x.IsDeleted && x.WorkDate >= from && x.WorkDate <= to);
        if (scopeIds is not null) query = query.Where(x => scopeIds.Contains(x.EmployeeId));
        else if (employeeId is not null) query = query.Where(x => x.EmployeeId == employeeId);
        if (string.Equals(status, "PendingApproval", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.Status == "Submitted" || x.Status == "PendingHRApproval");
        else if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
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

    public async Task<AttendanceRawEvent> PunchAsync(Guid tenantId, WebPunchRequest request, string source, RequestContext context, CancellationToken ct)
    {
        var punchedAtUtc = DateTime.UtcNow;
        var raw = await PushEventAsync(tenantId,
            new AttendanceRawEventRequest(request.EmployeeId, null, null, source, punchedAtUtc,
                request.PunchDirection, request.LocationName, request.Latitude, request.Longitude,
                context.IpAddress, null, null, "",
                source.Contains("mobile", StringComparison.OrdinalIgnoreCase) ? "Mobile" : "Web", null),
            context, ct);

        var employee = await ResolveEmployee(tenantId, request.EmployeeId, null, ct)
            ?? throw new InvalidOperationException("Employee could not be mapped from attendance event.");
        var workDate = await ResolvePunchWorkDateAsync(tenantId, employee.Id, punchedAtUtc, ct);
        if (!await IsLocked(tenantId, workDate, ct))
        {
            var policies = await _db.AttendancePolicies
                .Where(x => x.TenantId == tenantId && x.IsActive)
                .ToListAsync(ct);
            if (policies.Count == 0)
            {
                var defaultPolicy = DefaultPolicy(tenantId);
                _db.AttendancePolicies.Add(defaultPolicy);
                policies.Add(defaultPolicy);
            }
            await ProcessEmployeeDay(tenantId, employee, workDate,
                ResolveAttendancePolicy(employee, policies), context, ct);
            await _db.SaveChangesAsync(ct);
        }

        return raw;
    }

    public async Task<AttendanceRegularizationRequest> CreateRegularizationAsync(Guid tenantId, RegularizationRequestDto request, RequestContext context, CancellationToken ct)
    {
        var employee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == request.EmployeeId && !e.IsDeleted, ct);
        if (employee is null)
            throw new InvalidOperationException("Employee not found.");

        var hasPending = await _db.AttendanceRegularizationRequests
            .AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == request.EmployeeId
                && x.WorkDate == request.WorkDate && x.Status == "Submitted", ct);
        if (hasPending)
            throw new InvalidOperationException("A pending correction request already exists for this employee and date.");

        var reg = new AttendanceRegularizationRequest
        {
            TenantId = tenantId,
            CompanyId = employee.CompanyId,
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
        if (reg.Status != "Submitted" && reg.Status != "PendingHRApproval")
            throw new InvalidOperationException($"Request is in '{reg.Status}' status and cannot be approved.");
        if (await IsLocked(tenantId, reg.WorkDate, ct))
            throw new InvalidOperationException("Attendance period is payroll locked.");
        var beforeStatus = reg.Status;
        var activeLevel = reg.Status == "Submitted" ? "Manager" : "HR";
        var approval = await _db.AttendanceCorrectionApprovals.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.RegularizationRequestId == id && x.ApprovalLevel == activeLevel, ct);
        if (approval is not null)
        {
            approval.Decision = "Approved";
            approval.Comments = Clean(request.Comments);
            approval.DecidedAtUtc = DateTime.UtcNow;
            approval.DecidedByUserId = context.UserId;
        }

        if (activeLevel == "Manager")
        {
            reg.Status = "PendingHRApproval";
            await Audit(tenantId, context, "attendance.regularization.manager_approved", "AttendanceRegularizationRequest", reg.Id.ToString(),
                JsonSerializer.Serialize(new { before = beforeStatus, after = reg.Status }), ct);
            await _db.SaveChangesAsync(ct);
            await _notifications.NotifyAsync(tenantId, null,
                "Correction Request Pending HR",
                $"Attendance correction for {reg.WorkDate:yyyy-MM-dd} is pending HR approval.",
                "AttendanceRegularizationRequest", reg.Id.ToString(), ct);
            return reg;
        }

        reg.Status = "Approved";
        reg.DecidedAtUtc = DateTime.UtcNow;
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
        if (reg.Status != "Submitted" && reg.Status != "PendingHRApproval")
            throw new InvalidOperationException($"Request is in '{reg.Status}' status and cannot be rejected.");
        var beforeStatus = reg.Status;
        reg.Status = "Rejected";
        reg.DecidedAtUtc = DateTime.UtcNow;
        var activeLevel = beforeStatus == "Submitted" ? "Manager" : "HR";
        var approval = await _db.AttendanceCorrectionApprovals.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.RegularizationRequestId == id && x.ApprovalLevel == activeLevel, ct);
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
        var activeIds = _db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted
            && e.Status == EmployeeStatuses.Active).Select(e => e.Id);
        var records = await _db.AttendanceDailyRecords
            .Where(x => x.TenantId == tenantId && x.WorkDate == date && activeIds.Contains(x.EmployeeId))
            .ToListAsync(ct);
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
        var tz = await ResolveTenantTimeZoneAsync(tenantId, ct);
        // Resolve the employee's scheduled shift for this date and convert its local
        // wall-clock start/end to UTC. Previously this hardcoded 09:00 *UTC* and ignored
        // the assigned shift entirely — for a GCC tenant (Asia/Riyadh) 09:00 UTC = noon
        // local, so every employee showed bogus late/early minutes.
        var shift = await _db.ShiftAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EmployeeId == employee.Id && a.AssignedDate == date)
            .Join(_db.ShiftDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId),
                  a => a.ShiftDefinitionId, d => d.Id, (a, d) => new { d.StartTime, d.EndTime })
            .FirstOrDefaultAsync(ct);
        var isOvernightShift = shift is not null && shift.EndTime <= shift.StartTime;
        var startLocalBoundary = isOvernightShift
            ? DateTime.SpecifyKind(date.ToDateTime(shift!.StartTime), DateTimeKind.Unspecified)
            : DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var endLocalBoundary = isOvernightShift
            ? DateTime.SpecifyKind(date.AddDays(1).ToDateTime(shift!.EndTime), DateTimeKind.Unspecified)
            : DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var start = TimeZoneInfo.ConvertTimeToUtc(startLocalBoundary, tz);
        var end = TimeZoneInfo.ConvertTimeToUtc(endLocalBoundary, tz);
        var events = await _db.AttendanceRawEvents.Where(x => x.TenantId == tenantId
            && x.EmployeeId == employee.Id && x.PunchTimestampUtc >= start && x.PunchTimestampUtc < end)
            .OrderBy(x => x.PunchTimestampUtc).ToListAsync(ct);
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
        var workWeek = await _workWeek.ResolveAsync(tenantId, employee.CompanyId,
            string.IsNullOrWhiteSpace(employee.CountryCode) ? null : employee.CountryCode, ct);
        var isRestDay = workWeek.IsWeekend(date.DayOfWeek);
        var isPublicHoliday = await _db.PublicHolidays.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.Date == date && !x.IsOptional, ct);
        var approvedLeave = await _db.LeaveRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.Status == "Approved"
                && x.StartDate <= date && x.EndDate >= date)
            .Select(x => x.LeaveTypeName)
            .FirstOrDefaultAsync(ct);

        daily.LateMinutes = daily.FirstInUtc is null ? 0 : Math.Max(0, (int)(daily.FirstInUtc.Value - shiftStart).TotalMinutes - policy.GraceMinutes);
        daily.EarlyExitMinutes = daily.LastOutUtc is null ? 0 : Math.Max(0, (int)(shiftEnd - daily.LastOutUtc.Value).TotalMinutes - policy.EarlyExitThresholdMinutes);

        // KSA Labour Law Art. 98 — during Ramadan the actual working hours for Muslims are reduced to
        // 6 hours a day, and Art. 98 cuts HOURS, not wages: the monthly salary is unchanged. So the
        // overtime threshold for a Ramadan day is 6 hours, and the two hours that used to be ordinary
        // time become overtime at the Art. 107 rate. Ramadan is resolved through the Um al-Qura
        // calendar, never a Gregorian range — it moves ~11 days earlier each Gregorian year.
        // Non-KSA companies and non-Ramadan dates get policy.StandardWorkMinutes unchanged.
        // Gate on the EMPLOYING COMPANY's country, not on Employee.CountryCode. The latter is a
        // personal field that defaults empty, so gating on it both missed employees of a KSA entity
        // and leaked the reduction to employees of a non-KSA one. Art. 109 and Art. 117 already
        // resolve the company; Art. 98 now does the same. See ResolveCompanyCountryAsync.
        var employerCountry = await ResolveCompanyCountryAsync(tenantId, employee.CompanyId, ct);
        var hoursBaseline = await _ksaWorkingHours.ResolveDailyAsync(
            employerCountry, date, policy.StandardWorkMinutes, ct);
        var baselineMinutes = hoursBaseline.DailyMinutes;

        daily.OvertimeMinutes = Math.Max(0, daily.TotalWorkedMinutes - baselineMinutes);
        daily.UndertimeMinutes = Math.Max(0, baselineMinutes - daily.TotalWorkedMinutes);
        if (daily.FirstInUtc is null && daily.TotalWorkedMinutes == 0 && !string.IsNullOrWhiteSpace(approvedLeave))
        {
            daily.Status = "On leave";
            daily.MissingPunch = false;
            daily.LateMinutes = daily.EarlyExitMinutes = daily.UndertimeMinutes = 0;
        }
        else if (daily.FirstInUtc is null && daily.TotalWorkedMinutes == 0 && isPublicHoliday)
        {
            daily.Status = "Public holiday";
            daily.MissingPunch = false;
            daily.LateMinutes = daily.EarlyExitMinutes = daily.UndertimeMinutes = 0;
        }
        else if (daily.FirstInUtc is null && daily.TotalWorkedMinutes == 0 && isRestDay)
        {
            daily.Status = "Rest day";
            daily.MissingPunch = false;
            daily.LateMinutes = daily.EarlyExitMinutes = daily.UndertimeMinutes = 0;
        }
        else
        {
            daily.Status = daily.FirstInUtc is not null && daily.LastOutUtc is null ? "Present"
                : daily.TotalWorkedMinutes == 0 ? "Absent"
                : daily.TotalWorkedMinutes < policy.HalfDayThresholdMinutes ? "Half day"
                : daily.LateMinutes > 0 ? "Late" : "Present";
        }
        daily.ProcessedAtUtc = DateTime.UtcNow;
        daily.UpdatedAtUtc = DateTime.UtcNow;
        foreach (var raw in events) raw.IsProcessed = true;
        await UpsertLegacyRecord(tenantId, employee.CompanyId, daily, ct);
        // An absent day costs a day's hours. The literal 480 below is DELIBERATELY retained for every
        // ordinary day, including for a tenant whose AttendancePolicy is not 480: making the absence
        // deduction follow the policy generally is a defensible fix, but it would move money for
        // non-KSA tenants — upward, for anyone configured above 8 hours — and that is outside this
        // change's remit. Only the Ramadan case is corrected here, and only downward.
        var absenceMinutes = hoursBaseline.IsRamadan ? Math.Min(baselineMinutes, 480) : 480;
        await UpsertImpacts(tenantId, daily, absenceMinutes, ct);
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

    /// <summary>
    /// WAVE 1 B1 (round 2) — THE SECOND HALF OF THE DEVICE-INGEST DEFECT.
    ///
    /// <para><c>AttendanceRecord</c> is <c>ICompanyScopedOperational</c>, so a row whose
    /// <c>CompanyId</c> is null is invisible to every company-scoped user — the "poison default" the
    /// operational tier exists to prevent. This method used to resolve that company by RE-QUERYING
    /// <c>_db.Employees</c> through the ambient filter. On the <c>[AllowAnonymous]</c> device-ingest
    /// path that filter denies everything (empty company scope), so the lookup returned null and every
    /// legacy row created from a device punch was born invisible.</para>
    ///
    /// <para>Nothing rescued it downstream. <c>ZayraDbContext.EnforceCompanyScopeOnWritesAsync</c>
    /// returns early when there is no tenant claim — a device webhook has none — so its server-side
    /// stamping, including the "follow the owning employee's company" branch, never ran on this path.</para>
    ///
    /// <para>Before the ingest fix this was latent: device punches matched nothing, so no legacy rows
    /// were created at all. Fixing the match is what STARTED creating them, which is why both halves
    /// have to ship together.</para>
    ///
    /// <para>The company is now HANDED IN by the caller, taken from the <c>Employee</c> that
    /// <c>ProcessEmployeeDay</c> already holds — the same entity the punch was matched against. There
    /// is deliberately no employee query in scope here to re-introduce the bug with, and no new
    /// <c>IgnoreQueryFilters</c>: the value is already in memory, correctly resolved, on both paths.</para>
    /// </summary>
    /// <param name="employeeCompanyId">
    /// The owning employee's company, from the caller's already-resolved entity. Null is legitimate
    /// (an employee with no company, or a tenant with no company dimension yet) and passes through
    /// unchanged — this method must not invent a company it was not given.
    /// </param>
    private async Task UpsertLegacyRecord(Guid tenantId, Guid? employeeCompanyId, AttendanceDailyRecord daily, CancellationToken ct)
    {
        var record = await _db.AttendanceRecords.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == daily.EmployeeId && x.WorkDate == daily.WorkDate, ct);
        if (record is null)
        {
            record = new AttendanceRecord { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate };
            _db.AttendanceRecords.Add(record);
        }
        // ??= not =: an existing row's company is never reassigned here. EnforceCompanyScopeOnWritesAsync
        // throws company_reassignment_blocked on exactly that, and repairing a null is the only
        // transition this path is allowed to make.
        record.CompanyId ??= employeeCompanyId;
        record.TimeIn = daily.FirstInUtc is null ? null : TimeOnly.FromDateTime(daily.FirstInUtc.Value);
        record.TimeOut = daily.LastOutUtc is null ? null : TimeOnly.FromDateTime(daily.LastOutUtc.Value);
        record.OvertimeHours = Math.Round(daily.OvertimeMinutes / 60m, 2);
        record.Status = daily.Status;
        record.Notes = daily.MissingPunch ? "Missing punch" : "";
    }

    /// <param name="absenceMinutes">What one absent day costs in minutes: 480 on an ordinary day, or the
    /// reduced KSA Art. 98 Ramadan baseline (360) on a Ramadan day. Deducting a full 480 for a 6-hour
    /// Ramadan day over-deducts by a third. See the call site for why the ordinary-day literal stays.</param>
    private Task UpsertImpacts(Guid tenantId, AttendanceDailyRecord daily, int absenceMinutes, CancellationToken ct)
    {
        var existing = _db.AttendancePayrollImpacts.Where(x => x.TenantId == tenantId && x.EmployeeId == daily.EmployeeId && x.WorkDate == daily.WorkDate);
        _db.AttendancePayrollImpacts.RemoveRange(existing);
        if (daily.LateMinutes > 0) _db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate, ImpactType = "Late deduction", Minutes = daily.LateMinutes, DailyRecordId = daily.Id });
        if (daily.EarlyExitMinutes > 0) _db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate, ImpactType = "Early exit deduction", Minutes = daily.EarlyExitMinutes, DailyRecordId = daily.Id });
        if (daily.Status == "Absent") _db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate, ImpactType = "Absence deduction", Minutes = absenceMinutes > 0 ? absenceMinutes : 480, DailyRecordId = daily.Id });
        if (daily.OvertimeMinutes > 0) _db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate, ImpactType = "Overtime payable", Minutes = daily.OvertimeMinutes, DailyRecordId = daily.Id });
        return Task.CompletedTask;
    }

    private async Task UpsertExceptions(Guid tenantId, AttendanceDailyRecord daily, CancellationToken ct)
    {
        if (!daily.MissingPunch && daily.LateMinutes == 0) return;
        var type = daily.MissingPunch ? "MissingPunch" : "LateArrival";
        var exists = await _db.AttendanceExceptions.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == daily.EmployeeId && x.WorkDate == daily.WorkDate && x.ExceptionType == type && !x.IsResolved, ct);
        if (!exists) _db.AttendanceExceptions.Add(new AttendanceException { TenantId = tenantId, EmployeeId = daily.EmployeeId, DailyRecordId = daily.Id, WorkDate = daily.WorkDate, ExceptionType = type, Severity = daily.MissingPunch ? "High" : "Medium", Details = daily.MissingPunch ? "Missing in or out punch." : $"{daily.LateMinutes} late minutes." });
    }

    /// <summary>
    /// WAVE 1 B1 — DEVICE INGEST WAS SILENTLY MATCHING NOTHING IN PRODUCTION.
    ///
    /// <para>This is reached from <c>IngestByDeviceKeyAsync</c>, an <c>[AllowAnonymous]</c> webhook where
    /// the device API key is the credential and there is no JWT. Two reads on that path already carry a
    /// documented <c>IgnoreQueryFilters()</c> for exactly that reason — the device lookup and the
    /// duplicate-event probe. This one was missed, and it is the only read on the path whose entity is
    /// <c>ICompanyScopedOperational</c> as well as tenant-owned.</para>
    ///
    /// <para>That distinction is what made it fail. The COMPANY clause of the read filter is independent
    /// of the system-scope bypass: it is <c>_isGroupScope || (CompanyId != null &amp;&amp; _companyScopeIds
    /// .Contains(...))</c>, both of which derive from the request principal. An anonymous request in
    /// Production — where <c>EntityScopeOptions.ResolveStrictMode</c> forces strict — resolves to an
    /// EMPTY company scope, so this query matched zero rows for every punch. Every punch was therefore
    /// recorded as unmatched, <c>processedDays</c> stayed 0, and no daily attendance record was ever
    /// created from a device. The raw events were stored, so the data is recoverable by reprocessing.</para>
    ///
    /// <para>The tenant is <c>device.TenantId</c> — a value read from the database after authenticating
    /// the device key, never anything the caller supplied — so re-applying it explicitly here is the same
    /// scope the global filter would have given, and no wider.</para>
    /// </summary>
    /// <param name="bypassCompanyFilter">
    /// TRUE only on the anonymous device-webhook path. This parameter exists because an earlier fix
    /// applied the bypass unconditionally and thereby created a CROSS-COMPANY WRITE on the
    /// AUTHENTICATED path: <c>AttendanceController.PushEvent</c> pre-checks with a FILTERED lookup, so a
    /// target in another company resolved to null, its <c>Forbid()</c> was skipped as a result, and an
    /// unfiltered lookup here then found the row and recorded a punch against another company's
    /// employee. Employee ids are sequential ints, so that was trivially enumerable.
    ///
    /// <para>The bypass is legitimate ONLY where there is no principal to scope by — the device key is
    /// the credential and <paramref name="tenantId"/> comes from the authenticated device's own row.
    /// On any authenticated path the ambient company filter is the control and must stay on.</para>
    /// </param>
    private async Task<Employee?> ResolveEmployee(
        Guid tenantId, int? employeeId, string? employeeCode, CancellationToken ct,
        bool bypassCompanyFilter = false)
    {
        // IgnoreQueryFilters is intentional and GATED: only the anonymous device webhook may drop the
        // company filter, because only there is the absence of a principal the reason it matched nothing.
        // The tenant is re-applied explicitly in every predicate below.
        var employees = bypassCompanyFilter ? _db.Employees.IgnoreQueryFilters() : _db.Employees.AsQueryable();

        if (employeeId is not null)
            return await employees
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted
                    && x.Status == EmployeeStatuses.Active, ct);
        if (!string.IsNullOrWhiteSpace(employeeCode))
            return await employees
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeCode == employeeCode && !x.IsDeleted
                    && x.Status == EmployeeStatuses.Active, ct);
        return null;
    }

    private async Task<DateOnly> ResolvePunchWorkDateAsync(Guid tenantId, int employeeId, DateTime punchUtc, CancellationToken ct)
    {
        var tz = await ResolveTenantTimeZoneAsync(tenantId, ct);
        var utc = DateTime.SpecifyKind(punchUtc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        var localDate = DateOnly.FromDateTime(local);
        var previousDate = localDate.AddDays(-1);
        var previousOvernightEnd = await _db.ShiftAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EmployeeId == employeeId && a.AssignedDate == previousDate)
            .Join(_db.ShiftDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId),
                a => a.ShiftDefinitionId, d => d.Id,
                (a, d) => new { d.StartTime, d.EndTime })
            .Where(s => s.EndTime <= s.StartTime)
            .Select(s => (TimeOnly?)s.EndTime)
            .FirstOrDefaultAsync(ct);

        return previousOvernightEnd.HasValue && TimeOnly.FromDateTime(local) <= previousOvernightEnd.Value
            ? previousDate
            : localDate;
    }

    private async Task<bool> IsLocked(Guid tenantId, DateOnly date, CancellationToken ct) =>
        await _db.AttendanceLockPeriods.AnyAsync(x => x.TenantId == tenantId && x.PeriodStart <= date && x.PeriodEnd >= date && x.Status == "Locked", ct);

    private static AttendancePolicy DefaultPolicy(Guid tenantId) => new() { TenantId = tenantId, Code = "DEFAULT", Name = "Default attendance policy" };

    private static AttendancePolicy ResolveAttendancePolicy(Employee employee, IReadOnlyCollection<AttendancePolicy> policies) =>
        policies
            .Where(p =>
                (!p.BranchId.HasValue || p.BranchId == employee.BranchId) &&
                (!p.DepartmentId.HasValue || p.DepartmentId == employee.DepartmentId) &&
                (!p.GradeId.HasValue || p.GradeId == employee.GradeId))
            .OrderByDescending(p =>
                (p.BranchId.HasValue ? 4 : 0) +
                (p.DepartmentId.HasValue ? 3 : 0) +
                (p.GradeId.HasValue ? 2 : 0))
            .ThenBy(p => p.Code)
            .FirstOrDefault()
        ?? policies.OrderBy(p => p.Code).First();

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
