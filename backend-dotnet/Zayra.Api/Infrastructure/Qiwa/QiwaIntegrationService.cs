using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Qiwa;

/// <summary>
/// Placeholder implementation of <see cref="IQiwaIntegrationService"/>.
///
/// This service manages local Qiwa configuration and sync-log records only.
/// It does NOT make any external HTTP calls to the Qiwa API.  When the real
/// Qiwa API integration is ready, replace the body of EnqueueEmployeeSyncAsync
/// (and add a background worker) without touching callers or the interface.
/// </summary>
public sealed class QiwaIntegrationService : IQiwaIntegrationService
{
    private readonly ZayraDbContext _db;
    private readonly ILogger<QiwaIntegrationService> _log;
    private readonly IDataProtector _protector;

    /// <summary>Shared DataProtection purpose used to encrypt Qiwa client secrets.</summary>
    public const string SecretPurpose = "Zayra.Qiwa.ClientSecret.v1";

    private static readonly string[] RequiredFields =
    [
        nameof(Employee.SaudiOrNonSaudi),
        nameof(Employee.IdType),
        nameof(Employee.IdNumber),
        nameof(Employee.Nationality),
        nameof(Employee.OccupationCode),
        nameof(Employee.EstablishmentId),
        nameof(Employee.WorkLocationId),
        nameof(Employee.ContractReference),
    ];

    public QiwaIntegrationService(ZayraDbContext db, ILogger<QiwaIntegrationService> log, IDataProtectionProvider protectionProvider)
    {
        _db  = db;
        _log = log;
        _protector = protectionProvider.CreateProtector(SecretPurpose);
    }

    // ── Connection management ─────────────────────────────────────────────────

    public Task<QiwaTenantConnection?> GetConnectionStatusAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => _db.QiwaTenantConnections
              .AsNoTracking()
              .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken)!;

    public async Task<QiwaTenantConnection> UpsertConnectionAsync(
        Guid tenantId, QiwaConnectionRequest request, Guid? performedBy, string ipAddress,
        CancellationToken cancellationToken = default)
    {
        var existing = await _db.QiwaTenantConnections
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);

        if (existing is null)
        {
            existing = new QiwaTenantConnection { TenantId = tenantId, ConfiguredBy = performedBy };
            _db.QiwaTenantConnections.Add(existing);
        }

        var oldJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            existing.EstablishmentId,
            existing.Environment,
            existing.Status
        });

        existing.EstablishmentId             = request.EstablishmentId.Trim();
        existing.EstablishmentName           = request.EstablishmentName.Trim();
        existing.UnifiedOrganisationNumber   = request.UnifiedOrganisationNumber.Trim();
        existing.Environment                 = request.Environment.Trim();
        existing.Status                      = QiwaConnectionStatuses.Disconnected; // real check happens when API goes live
        existing.UpdatedAtUtc                = DateTime.UtcNow;

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = tenantId,
            EntityType      = "QiwaTenantConnection",
            EntityId        = existing.Id.ToString(),
            Action          = "Updated",
            OldValuesJson   = oldJson,
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new
            {
                request.EstablishmentId,
                request.Environment,
                Status = QiwaConnectionStatuses.Disconnected
            }),
            PerformedBy     = performedBy,
            PerformedByName = string.Empty,
            IpAddress       = ipAddress
        });

        await _db.SaveChangesAsync(cancellationToken);
        _log.LogInformation("Qiwa connection upserted for tenant {TenantId}, establishment {EstId}", tenantId, request.EstablishmentId);
        return existing;
    }

    // ── Sync log ──────────────────────────────────────────────────────────────

    public async Task<QiwaSyncLog> EnqueueEmployeeSyncAsync(
        Guid tenantId, int employeeId, string direction, string triggerSource, Guid? triggeredBy,
        CancellationToken cancellationToken = default)
    {
        // Cross-tenant guard: verify the employee belongs to this tenant.
        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Employee {employeeId} not found in this tenant.");

        // Readiness gate for Push syncs: reject non-ready employees so the worker
        // never wastes API quota or generates preventable FIELD_MISSING failures.
        if (direction == "Push")
        {
            var missing = MissingQiwaFields(employee);
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Employee {employeeId} is not Qiwa-ready. Missing fields: {string.Join(", ", missing)}. " +
                    "Fix the employee record before queuing for sync.");
        }

        var entry = new QiwaSyncLog
        {
            TenantId      = tenantId,
            EmployeeId    = employeeId,
            Direction     = direction,
            TriggerSource = triggerSource,
            Status        = QiwaSyncLogStatuses.Pending,
            TriggeredBy   = triggeredBy,
        };
        _db.QiwaSyncLogs.Add(entry);

        employee.QiwaSyncStatus = QiwaSyncStatuses.Pending;

        _db.AuditLogs.Add(new Domain.Entities.AuditLog
        {
            TenantId    = tenantId,
            UserId      = triggeredBy,
            Action      = "qiwa.sync_enqueued",
            EntityName  = "Employee",
            EntityId    = employeeId.ToString(),
            Metadata    = JsonSerializer.Serialize(new { direction, triggerSource, syncLogId = entry.Id }),
        });

        await _db.SaveChangesAsync(cancellationToken);

        _log.LogInformation(
            "Qiwa sync enqueued for employee {EmployeeId} (tenant {TenantId}), direction={Direction}, source={Source}",
            employeeId, tenantId, direction, triggerSource);

        return entry;
    }

    public Task<IReadOnlyList<QiwaSyncLog>> GetSyncLogsAsync(
        Guid tenantId, int? employeeId, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var q = _db.QiwaSyncLogs.AsNoTracking().Where(l => l.TenantId == tenantId);
        if (employeeId.HasValue) q = q.Where(l => l.EmployeeId == employeeId.Value);
        return q.OrderByDescending(l => l.CreatedAtUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken)
                .ContinueWith(t => (IReadOnlyList<QiwaSyncLog>)t.Result, cancellationToken);
    }

    // ── Readiness check ───────────────────────────────────────────────────────

    /// <summary>Employment statuses that are eligible for Qiwa sync.</summary>
    private static readonly IReadOnlySet<string> SyncEligibleStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        EmployeeStatuses.Active,
        EmployeeStatuses.Invited,
    };

    public async Task<QiwaReadinessReport> CheckEmployeeReadinessAsync(
        Guid tenantId, int employeeId, CancellationToken cancellationToken = default)
    {
        var emp = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Employee {employeeId} not found in this tenant.");

        return BuildReadinessReport(emp);
    }

    /// <summary>
    /// Builds a full readiness report for an employee:
    /// missing required fields, human-readable blocking reasons, non-blocking warnings,
    /// and eligibility for sync (ready + active employment status).
    /// </summary>
    public static QiwaReadinessReport BuildReadinessReport(Employee emp)
    {
        var missing  = MissingQiwaFields(emp);
        var blocking = MissingFieldsToReasons(missing);
        var warnings = new List<string>();

        // Employment-status eligibility: only Active/Invited employees should sync.
        var isEligibleStatus = SyncEligibleStatuses.Contains(emp.Status);
        if (!isEligibleStatus)
            warnings.Add($"Employee status '{emp.Status}' is not eligible for Qiwa sync. Only Active or Invited employees are synced.");

        // Non-blocking warnings (advisory only, do not block sync)
        if (string.IsNullOrWhiteSpace(emp.WorkPermitReference) && string.Equals(emp.SaudiOrNonSaudi, "NonSaudi", StringComparison.OrdinalIgnoreCase))
            warnings.Add("work_permit_reference is recommended for Non-Saudi employees (non-blocking).");

        var isReady         = missing.Count == 0;
        var isEligibleForSync = isReady && isEligibleStatus;

        return new QiwaReadinessReport(
            emp.Id, emp.EmployeeCode, emp.FullName,
            isReady, isEligibleForSync,
            missing, blocking, warnings,
            DateTime.UtcNow);
    }

    /// <summary>Returns snake_case field names for all Qiwa-required fields that are empty on an employee.</summary>
    public static List<string> MissingQiwaFields(Employee emp)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(emp.SaudiOrNonSaudi))    missing.Add("saudi_or_non_saudi");
        if (string.IsNullOrWhiteSpace(emp.IdType))             missing.Add("id_type");
        if (string.IsNullOrWhiteSpace(emp.IdNumber))           missing.Add("id_number");
        if (string.IsNullOrWhiteSpace(emp.Nationality))        missing.Add("nationality");
        if (string.IsNullOrWhiteSpace(emp.OccupationCode))     missing.Add("occupation_code");
        if (string.IsNullOrWhiteSpace(emp.EstablishmentId))    missing.Add("establishment_id");
        if (string.IsNullOrWhiteSpace(emp.WorkLocationId))     missing.Add("work_location_id");
        if (string.IsNullOrWhiteSpace(emp.ContractReference))  missing.Add("contract_reference");
        return missing;
    }

    private static List<string> MissingFieldsToReasons(IReadOnlyList<string> missingFields)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["saudi_or_non_saudi"] = "Saudi/Non-Saudi classification is required by Qiwa for all employees.",
            ["id_type"]            = "Government ID type (NationalId, Iqama, Passport, etc.) must be specified.",
            ["id_number"]          = "Government ID number matching the ID type must be provided.",
            ["nationality"]        = "Employee nationality is required for Qiwa registration.",
            ["occupation_code"]    = "ISCO-88 / Qiwa occupation code is required.",
            ["establishment_id"]   = "MOL establishment ID is required — map the employee to a Saudi establishment.",
            ["work_location_id"]   = "Qiwa work location ID within the establishment is required.",
            ["contract_reference"] = "Qiwa labour contract reference number is required.",
        };

        return missingFields
            .Select(f => map.TryGetValue(f, out var reason) ? reason : $"Field '{f}' is required.")
            .ToList();
    }

    // ── Credentials (encrypted) ───────────────────────────────────────────────

    public async Task SaveApiCredentialAsync(
        Guid tenantId, string clientId, string plainTextSecret, string environment,
        Guid performedBy, string ip, CancellationToken ct = default)
    {
        var encrypted = _protector.Protect(plainTextSecret);

        var cred = await _db.QiwaApiCredentials.FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);
        var isNew = cred is null;
        if (cred is null)
        {
            cred = new QiwaApiCredential { TenantId = tenantId };
            _db.QiwaApiCredentials.Add(cred);
        }

        cred.ClientId              = clientId;
        cred.EncryptedClientSecret = encrypted;
        cred.Environment           = environment;
        cred.CachedAccessToken     = string.Empty;
        cred.TokenExpiresAtUtc     = null;
        cred.UpdatedAtUtc          = DateTime.UtcNow;
        cred.UpdatedBy             = performedBy;

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId        = tenantId,
            EntityType      = "QiwaApiCredential",
            EntityId        = cred.Id.ToString(),
            Action          = isNew ? "Created" : "Updated",
            // Never log the secret — only metadata.
            NewValuesJson   = System.Text.Json.JsonSerializer.Serialize(new { clientId, environment, secretSet = true }),
            PerformedBy     = performedBy,
            PerformedByName = string.Empty,
            IpAddress       = ip
        });

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Qiwa credentials saved for tenant {TenantId} (env={Env})", tenantId, environment);
    }

    // ── Readiness summary ─────────────────────────────────────────────────────

    public async Task<QiwaReadinessSummary> GetReadinessSummaryAsync(Guid tenantId, CancellationToken ct = default)
    {
        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == EmployeeStatuses.Active)
            .ToListAsync(ct);

        var blocked = new List<QiwaReadinessReport>();
        foreach (var e in employees)
        {
            var report = BuildReadinessReport(e);
            if (!report.IsEligibleForSync)
                blocked.Add(report);
        }

        var total   = employees.Count;
        var ready   = total - blocked.Count;
        var percent = total == 0 ? 0 : Math.Round(ready * 100.0 / total, 1);

        return new QiwaReadinessSummary(total, ready, blocked.Count, percent, blocked);
    }

    // ── Bulk sync ─────────────────────────────────────────────────────────────

    public async Task<QiwaBulkSyncResult> EnqueueBulkSyncAsync(
        Guid tenantId, string triggerSource, Guid? triggeredBy, CancellationToken ct = default)
    {
        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == EmployeeStatuses.Active)
            .ToListAsync(ct);

        // Filter to only Qiwa-ready eligible employees.
        var readyIds = employees
            .Where(e => BuildReadinessReport(e).IsEligibleForSync)
            .Select(e => e.Id)
            .ToList();

        if (readyIds.Count == 0)
            return new QiwaBulkSyncResult(0, 0, 0, Array.Empty<int>());

        // Skip employees that already have a Pending sync log to prevent duplicates.
        var alreadyPending = await _db.QiwaSyncLogs
            .Where(l => l.TenantId == tenantId && readyIds.Contains(l.EmployeeId)
                        && l.Status == QiwaSyncLogStatuses.Pending && l.Direction == "Push")
            .Select(l => l.EmployeeId)
            .ToListAsync(ct);

        var toEnqueue = readyIds.Except(alreadyPending).ToList();
        var now = DateTime.UtcNow;

        foreach (var empId in toEnqueue)
        {
            _db.QiwaSyncLogs.Add(new QiwaSyncLog
            {
                TenantId      = tenantId,
                EmployeeId    = empId,
                Direction     = "Push",
                TriggerSource = triggerSource,
                Status        = QiwaSyncLogStatuses.Pending,
                TriggeredBy   = triggeredBy,
                CreatedAtUtc  = now,
            });
            _db.AuditLogs.Add(new Domain.Entities.AuditLog
            {
                TenantId   = tenantId,
                UserId     = triggeredBy,
                Action     = "qiwa.sync_enqueued",
                EntityName = "Employee",
                EntityId   = empId.ToString(),
                Metadata   = JsonSerializer.Serialize(new { direction = "Push", triggerSource, bulkSync = true }),
            });
        }

        // Bulk-update QiwaSyncStatus on the enqueued employees.
        var enqueueSet = new HashSet<int>(toEnqueue);
        var empRows = await _db.Employees
            .Where(e => e.TenantId == tenantId && toEnqueue.Contains(e.Id))
            .ToListAsync(ct);
        foreach (var e in empRows)
            e.QiwaSyncStatus = QiwaSyncStatuses.Pending;

        _db.AuditLogs.Add(new Domain.Entities.AuditLog
        {
            TenantId   = tenantId,
            UserId     = triggeredBy,
            Action     = "qiwa.bulk_sync_enqueued",
            EntityName = "Tenant",
            EntityId   = tenantId.ToString(),
            Metadata   = JsonSerializer.Serialize(new
            {
                enqueued            = toEnqueue.Count,
                totalReady          = readyIds.Count,
                skippedAlreadyPending = alreadyPending.Count,
                triggerSource,
            }),
        });

        await _db.SaveChangesAsync(ct);
        _log.LogInformation(
            "Qiwa bulk sync enqueued {Count}/{Total} employees for tenant {TenantId}",
            toEnqueue.Count, readyIds.Count, tenantId);

        return new QiwaBulkSyncResult(readyIds.Count, toEnqueue.Count, alreadyPending.Count, toEnqueue);
    }

    // ── Dead-letter retry ─────────────────────────────────────────────────────

    public async Task RetryDeadLetterAsync(Guid tenantId, Guid syncLogId, Guid triggeredBy, CancellationToken ct = default)
    {
        var log = await _db.QiwaSyncLogs.FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Id == syncLogId, ct)
            ?? throw new InvalidOperationException($"Sync log {syncLogId} not found in this tenant.");

        log.Status           = QiwaSyncLogStatuses.Pending;
        log.RetryCount       = 0;
        log.DeadLetterReason = null;
        log.ErrorMessage     = null;
        log.LastRetriedAtUtc = null;
        log.CompletedAtUtc   = null;

        _db.AuditLogs.Add(new Domain.Entities.AuditLog
        {
            TenantId   = tenantId,
            UserId     = triggeredBy,
            Action     = "qiwa.sync_retry_requested",
            EntityName = "QiwaSyncLog",
            EntityId   = syncLogId.ToString(),
            Metadata   = System.Text.Json.JsonSerializer.Serialize(new { employeeId = log.EmployeeId }),
        });

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Qiwa sync log {SyncLogId} reset for retry (tenant {TenantId})", syncLogId, tenantId);
    }

    // ── Compliance summary ────────────────────────────────────────────────────

    public async Task<QiwaComplianceSummary> GetComplianceSummaryAsync(Guid tenantId, CancellationToken ct = default)
    {
        var connection = await _db.QiwaTenantConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);

        var readiness = await GetReadinessSummaryAsync(tenantId, ct);

        var failedCount = await _db.QiwaSyncLogs
            .CountAsync(l => l.TenantId == tenantId &&
                             (l.Status == QiwaSyncLogStatuses.Failed
                              || (l.Status == QiwaSyncLogStatuses.DeadLetter
                                  && l.DeadLetterReason != QiwaSyncLogStatuses.MissingClientIdReason
                                  && l.DeadLetterReason != QiwaSyncLogStatuses.MissingSecretReason)), ct);

        var (lastFiled, lastSimulated) = await LastRunsAsync(_db, tenantId, ct);

        return new QiwaComplianceSummary(
            connection?.Status ?? "NotConfigured",
            connection?.LastConnectedAtUtc,
            readiness.ReadinessPercent,
            readiness.BlockedFromSync,
            failedCount,
            lastFiled,
            lastSimulated);
    }

    /// <summary>
    /// F09 — the last REAL filing and the last simulator run, kept apart. "Last successful sync"
    /// used to be the newest "Success" row, which under the sandbox adapter was a simulation: the
    /// dashboard showed a sync date for a workforce that had never been filed with Qiwa. Pre-F09
    /// rows are recognised by the simulator's envelope in the stored response.
    /// </summary>
    public static async Task<(DateTime? LastFiled, DateTime? LastSimulated)> LastRunsAsync(
        ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var marker = QiwaSyncLogStatuses.LegacySimulationMarker;
        var lastFiled = await db.QiwaSyncLogs.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.Status == QiwaSyncLogStatuses.Success
                        && (l.ResponsePayloadJson == null || !l.ResponsePayloadJson.Contains(marker)))
            .OrderByDescending(l => l.CompletedAtUtc)
            .Select(l => l.CompletedAtUtc)
            .FirstOrDefaultAsync(ct);
        var lastSimulated = await db.QiwaSyncLogs.AsNoTracking()
            .Where(l => l.TenantId == tenantId
                        && (l.Status == QiwaSyncLogStatuses.Simulated
                            || (l.Status == QiwaSyncLogStatuses.Success && l.ResponsePayloadJson != null
                                && l.ResponsePayloadJson.Contains(marker))))
            .OrderByDescending(l => l.CompletedAtUtc)
            .Select(l => l.CompletedAtUtc)
            .FirstOrDefaultAsync(ct);
        return (lastFiled, lastSimulated);
    }
}
