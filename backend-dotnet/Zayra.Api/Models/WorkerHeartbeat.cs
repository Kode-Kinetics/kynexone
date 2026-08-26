namespace Zayra.Api.Models;

/// <summary>Tenant-neutral, per-process evidence that a production hosted worker is alive.</summary>
public sealed class WorkerHeartbeat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkerName { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string Status { get; set; } = WorkerHeartbeatStatuses.Started;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSucceededAtUtc { get; set; }
    public DateTime? LastFailedAtUtc { get; set; }
    public string LastErrorCode { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class WorkerHeartbeatStatuses
{
    public const string Started = "Started";
    public const string Healthy = "Healthy";
    public const string Failed = "Failed";
}

public static class ProductionWorkerNames
{
    public const string Qiwa = "qiwa-sync";
    public const string Notifications = "notification-delivery";
    public const string AiInsights = "ai-insights";
    public const string Reports = "report-schedules";
    public const string ComplianceReminders = "compliance-reminders";

    public static readonly IReadOnlyList<string> All =
        [Qiwa, Notifications, AiInsights, Reports, ComplianceReminders];
}
