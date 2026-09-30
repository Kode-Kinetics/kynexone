using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Qiwa;

/// <summary>
/// Integration boundary for Saudi Arabia's Qiwa workforce platform.
///
/// IMPORTANT: No real Qiwa API calls are made yet.  This interface defines the
/// contract so the real implementation can be plugged in without changing callers.
/// The placeholder <see cref="QiwaIntegrationService"/> writes audit/log entries
/// and marks the sync as Pending — it does NOT call any external endpoint.
/// </summary>
public interface IQiwaIntegrationService
{
    /// <summary>
    /// Returns the Qiwa connection record for a tenant, or null if never configured.
    /// </summary>
    Task<QiwaTenantConnection?> GetConnectionStatusAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves or updates the Qiwa establishment configuration for a tenant.
    /// Writes an AdminAuditLog entry.
    /// </summary>
    Task<QiwaTenantConnection> UpsertConnectionAsync(Guid tenantId, QiwaConnectionRequest request, Guid? performedBy, string ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues an employee record for Qiwa sync.
    /// Creates a QiwaSyncLog row with Status=Pending and writes an AuditLog entry.
    /// The placeholder implementation does NOT call any external API.
    /// </summary>
    Task<QiwaSyncLog> EnqueueEmployeeSyncAsync(Guid tenantId, int employeeId, string direction, string triggerSource, Guid? triggeredBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paged list of sync attempts for a tenant, optionally filtered to one employee.
    /// </summary>
    Task<IReadOnlyList<QiwaSyncLog>> GetSyncLogsAsync(Guid tenantId, int? employeeId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether an employee has all required Qiwa fields populated.
    /// Returns a report of missing / incomplete fields without calling any external API.
    /// </summary>
    Task<QiwaReadinessReport> CheckEmployeeReadinessAsync(Guid tenantId, int employeeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Encrypts and stores the tenant's Qiwa OAuth2 client credentials.
    /// The plaintext secret is NEVER logged or returned.
    /// </summary>
    Task SaveApiCredentialAsync(Guid tenantId, string clientId, string plainTextSecret, string environment, Guid performedBy, string ip, CancellationToken ct = default);

    /// <summary>Tenant-wide Qiwa readiness summary: ready vs blocked employees with missing fields.</summary>
    Task<QiwaReadinessSummary> GetReadinessSummaryAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Resets a dead-lettered sync log back to Pending (RetryCount = 0) and audits it.</summary>
    Task RetryDeadLetterAsync(Guid tenantId, Guid syncLogId, Guid triggeredBy, CancellationToken ct = default);

    /// <summary>Aggregate Qiwa compliance figures for the Saudi compliance dashboard.</summary>
    Task<QiwaComplianceSummary> GetComplianceSummaryAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Enqueues all Qiwa-ready active employees for bulk sync in a single operation.
    /// Employees already in Pending status are skipped (not double-queued).
    /// Non-ready employees are silently omitted; callers should use
    /// <see cref="GetReadinessSummaryAsync"/> first to surface blocked employees.
    /// </summary>
    Task<QiwaBulkSyncResult> EnqueueBulkSyncAsync(Guid tenantId, string triggerSource, Guid? triggeredBy, CancellationToken ct = default);
}

// ── Request / response DTOs ───────────────────────────────────────────────────

public record QiwaConnectionRequest(
    string EstablishmentId,
    string EstablishmentName,
    string UnifiedOrganisationNumber,
    string Environment
);

/// <summary>
/// Full readiness report for one employee.
/// <para><c>IsReady</c> = all required fields present.</para>
/// <para><c>IsEligibleForSync</c> = IsReady AND employee is in an active employment status.</para>
/// </summary>
public record QiwaReadinessReport(
    int EmployeeId,
    string EmployeeCode,
    string FullName,
    bool IsReady,
    bool IsEligibleForSync,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Warnings,
    DateTime CheckedAtUtc
);

public record QiwaReadinessSummary(
    int TotalEmployees,
    int ReadyForSync,
    int BlockedFromSync,
    double ReadinessPercent,
    IReadOnlyList<QiwaReadinessReport> BlockedEmployees
);

/// <param name="LastSuccessfulSync">The last run the LIVE adapter filed with Qiwa. Never a simulation.</param>
/// <param name="LastSimulatedSync">The last run the sandbox simulator completed. Nothing was filed.</param>
public record QiwaComplianceSummary(
    string ConnectionStatus,
    DateTime? LastConnectedAt,
    double ReadinessPercent,
    int EmployeesBlocked,
    int FailedSyncCount,
    DateTime? LastSuccessfulSync,
    DateTime? LastSimulatedSync = null
);

/// <summary>Result of a bulk-sync enqueue request.</summary>
public record QiwaBulkSyncResult(
    int TotalReady,
    int Enqueued,
    int SkippedAlreadyPending,
    IReadOnlyList<int> EnqueuedEmployeeIds
);
