using Zayra.Api.Application.Common;
using Zayra.Api.Application.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Attendance;

public interface IAttendanceService
{
    Task<PagedResult<AttendanceDevice>> GetDevicesAsync(Guid tenantId, int page, int pageSize, CancellationToken ct);
    Task<AttendanceDevice?> GetDeviceAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<AttendanceDevice> CreateDeviceAsync(Guid tenantId, AttendanceDeviceRequest request, RequestContext context, CancellationToken ct);
    Task<AttendanceDevice?> UpdateDeviceAsync(Guid tenantId, Guid id, AttendanceDeviceRequest request, RequestContext context, CancellationToken ct);
    Task<bool> DeleteDeviceAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken ct);
    Task<AttendanceDeviceSyncLog?> TestConnectionAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken ct);
    Task<AttendanceDeviceSyncLog?> SyncDeviceAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken ct);
    Task<IReadOnlyCollection<AttendanceDeviceSyncLog>> GetSyncLogsAsync(Guid tenantId, Guid deviceId, CancellationToken ct);
    Task<DeviceKeyResult?> GenerateDeviceKeyAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken ct);
    Task<DeviceIngestResult?> IngestByDeviceKeyAsync(string deviceKey, DeviceIngestRequest request, string? ip, CancellationToken ct);

    Task<AttendanceRawEvent> PushEventAsync(Guid tenantId, AttendanceRawEventRequest request, RequestContext context, CancellationToken ct);
    Task<AttendanceImportBatch> ImportCsvAsync(Guid tenantId, ImportAttendanceRequest request, RequestContext context, CancellationToken ct);
    Task<PagedResult<AttendanceRawEvent>> GetRawEventsAsync(Guid tenantId, DateOnly? from, DateOnly? to, int? employeeId, bool? processed, int page, int pageSize, CancellationToken ct);

    Task<int> ProcessAsync(Guid tenantId, ProcessAttendanceRequest request, RequestContext context, CancellationToken ct);

    // ── F3: the per-employee unit the durable attendance job checkpoints on ──────────────────────
    /// <summary>The same guards <see cref="ProcessAsync"/> applies (range, payroll-locked period). Throws InvalidOperationException.</summary>
    Task ValidateProcessRangeAsync(Guid tenantId, DateOnly fromDate, DateOnly toDate, CancellationToken ct);
    /// <summary>Active policies for the tenant, creating the default policy exactly as <see cref="ProcessAsync"/> does. Saves.</summary>
    Task<IReadOnlyList<AttendancePolicy>> EnsureActivePoliciesAsync(Guid tenantId, CancellationToken ct);
    /// <summary>
    /// Processes every day in the range for ONE employee using the identical per-day logic as
    /// <see cref="ProcessAsync"/>. Stages changes only — does NOT save — so the caller can commit it
    /// atomically with a job checkpoint. Returns the number of employee-days processed.
    /// </summary>
    Task<int> ProcessEmployeeRangeAsync(Guid tenantId, Employee employee, IReadOnlyCollection<AttendancePolicy> policies,
        DateOnly fromDate, DateOnly toDate, RequestContext context, CancellationToken ct);
    Task<PagedResult<AttendanceDailyDto>> GetDailyAsync(Guid tenantId, DateOnly? from, DateOnly? to, int? employeeId, string? status, int page, int pageSize, CancellationToken ct, IReadOnlyCollection<int>? scopeIds = null);
    Task<IReadOnlyCollection<AttendanceMonthlyDto>> GetMonthlyAsync(Guid tenantId, int year, int month, int? employeeId, CancellationToken ct, IReadOnlyCollection<int>? scopeIds = null);
    Task<AttendanceRawEvent> PunchAsync(Guid tenantId, WebPunchRequest request, string source, RequestContext context, CancellationToken ct);

    Task<AttendanceRegularizationRequest> CreateRegularizationAsync(Guid tenantId, RegularizationRequestDto request, RequestContext context, CancellationToken ct);
    Task<PagedResult<AttendanceRegularizationRequest>> GetRegularizationAsync(Guid tenantId, int? employeeId, string? status, int page, int pageSize, CancellationToken ct, IReadOnlyCollection<int>? scopeIds = null);
    Task<AttendanceRegularizationRequest?> ApproveRegularizationAsync(Guid tenantId, Guid id, RegularizationDecisionRequest request, RequestContext context, CancellationToken ct);
    Task<AttendanceRegularizationRequest?> RejectRegularizationAsync(Guid tenantId, Guid id, RegularizationDecisionRequest request, RequestContext context, CancellationToken ct);
    Task<AttendanceRegularizationRequest?> CancelRegularizationAsync(Guid tenantId, Guid id, string reason, RequestContext context, CancellationToken ct);

    Task<AttendanceDashboardDto> DashboardAsync(Guid tenantId, DateOnly date, CancellationToken ct);
    Task<IReadOnlyCollection<AttendanceDailyDto>> ReportDailyAsync(Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyCollection<AttendanceMonthlyDto>> ReportMonthlyAsync(Guid tenantId, int year, int month, CancellationToken ct);
    Task<IReadOnlyCollection<AttendanceDailyDto>> ReportByStatusAsync(Guid tenantId, DateOnly from, DateOnly to, string status, CancellationToken ct);
    Task<IReadOnlyCollection<AttendanceDailyDto>> ReportMissingPunchAsync(Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyCollection<AttendancePayrollSummaryDto>> PayrollSummaryAsync(Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyCollection<AttendanceDeviceSyncDto>> DeviceSyncReportAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyCollection<AttendanceAIInsight>> GenerateInsightsAsync(Guid tenantId, CancellationToken ct);
}
