using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// ── Saved Reports ─────────────────────────────────────────────────────────────

public class SavedReport : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string ReportKey { get; set; } = string.Empty;   // e.g. "hr.headcount", "payroll.register"
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;    // HR, Payroll, Attendance, Leave, etc.
    public string FiltersJson { get; set; } = string.Empty; // serialized filter params
    public string ColumnsJson { get; set; } = string.Empty; // selected columns
    public bool IsShared { get; set; }
    public Guid CreatedBy { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Report Schedules ──────────────────────────────────────────────────────────

public class ReportSchedule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string ReportKey { get; set; } = string.Empty;
    public string ReportName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string FiltersJson { get; set; } = string.Empty;
    public string Frequency { get; set; } = "Monthly";      // Daily, Weekly, Monthly, Quarterly
    // Email is the only delivery method that works, and the only one ReportSchedulePolicy
    // accepts. The UI used to offer SFTP and Portal as well; picking either returned a 400 the
    // UI swallowed into "Failed to create schedule." Both are gone from the UI now.
    public string DeliveryMethod { get; set; } = "Email";
    public string Recipients { get; set; } = string.Empty;  // comma-separated emails

    /// <summary>
    /// csv / xlsx / json — see <c>ReportExportFormats</c>. Legacy rows read "Excel", which
    /// normalises to xlsx and now finally produces an actual workbook instead of a renamed CSV.
    /// </summary>
    public string ExportFormat { get; set; } = "xlsx";
    public bool IsActive { get; set; } = true;
    public DateTime? LastRunAtUtc { get; set; }
    public DateTime? NextRunAtUtc { get; set; }

    // ── Failure visibility (F3) ───────────────────────────────────────────────────────────
    // A schedule whose creator was deactivated or lost reports.schedule used to fail every
    // period, silently, forever: the only trace was a row in ReportExecutionLogs that nobody
    // reads. The HR manager who set up the monthly pack resigns and the pack just stops.

    /// <summary>Reset to 0 on any success. Non-zero means the last run(s) did not deliver.</summary>
    public int ConsecutiveFailureCount { get; set; }

    public DateTime? LastFailureAtUtc { get; set; }

    /// <summary>Shown verbatim in the schedules list so the reason does not need a log dive.</summary>
    public string LastFailureReason { get; set; } = string.Empty;

    /// <summary>
    /// Set when the failure is specifically that the schedule has no valid owner. Distinguished
    /// from a transient failure because the fix is different: somebody has to take the schedule
    /// over, and no amount of retrying will do it.
    /// </summary>
    public DateTime? OwnerInvalidatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

// ── Report Execution Log ──────────────────────────────────────────────────────

public class ReportExecutionLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ScheduleId { get; set; }
    public string ReportKey { get; set; } = string.Empty;
    public string ReportName { get; set; } = string.Empty;
    public string FiltersJson { get; set; } = string.Empty;
    public string ExportFormat { get; set; } = string.Empty;
    public string Status { get; set; } = "Success";         // Success, Failed
    public int RowCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FileUrl { get; set; }
    public Guid? RunBy { get; set; }
    public string RunByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public int DurationMs { get; set; }
}
