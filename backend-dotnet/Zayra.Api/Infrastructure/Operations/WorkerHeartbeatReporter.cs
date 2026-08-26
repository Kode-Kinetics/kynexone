using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Operations;

public sealed class WorkerHeartbeatReporter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkerHeartbeatReporter> _log;
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public WorkerHeartbeatReporter(IServiceScopeFactory scopeFactory, ILogger<WorkerHeartbeatReporter> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public Task StartedAsync(string workerName, CancellationToken ct) =>
        TryWriteAsync(workerName, WorkerHeartbeatStatuses.Started, null, ct);

    public Task SucceededAsync(string workerName, CancellationToken ct) =>
        TryWriteAsync(workerName, WorkerHeartbeatStatuses.Healthy, null, ct);

    public Task FailedAsync(string workerName, Exception exception, CancellationToken ct) =>
        TryWriteAsync(workerName, WorkerHeartbeatStatuses.Failed, exception.GetType().Name, ct);

    private async Task TryWriteAsync(string workerName, string status, string? errorCode, CancellationToken ct)
    {
        try { await WriteAsync(workerName, status, errorCode, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _log.LogWarning(ex, "Could not persist {WorkerName} heartbeat.", workerName); }
    }

    private async Task WriteAsync(string workerName, string status, string? errorCode, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var now = DateTime.UtcNow;
        var row = await db.WorkerHeartbeats.SingleOrDefaultAsync(
            x => x.WorkerName == workerName && x.InstanceId == _instanceId, ct);
        if (row is null)
        {
            row = new WorkerHeartbeat
            {
                WorkerName = workerName,
                InstanceId = _instanceId,
                StartedAtUtc = now
            };
            db.WorkerHeartbeats.Add(row);
        }
        row.Status = status;
        row.LastAttemptAtUtc = now;
        row.UpdatedAtUtc = now;
        if (status == WorkerHeartbeatStatuses.Healthy)
        {
            row.LastSucceededAtUtc = now;
            row.LastErrorCode = string.Empty;
        }
        else if (status == WorkerHeartbeatStatuses.Failed)
        {
            row.LastFailedAtUtc = now;
            row.LastErrorCode = errorCode ?? "worker_failure";
        }
        await db.SaveChangesAsync(ct);
    }
}
