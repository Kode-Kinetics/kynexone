using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Infrastructure.Qiwa;

namespace Zayra.Api.Controllers;

/// <summary>
/// Qiwa integration management endpoints.
///
/// All routes are protected by the "qiwa_integration" feature flag via
/// FeatureFlagGuardFilter — a tenant must have that flag enabled before
/// these endpoints are accessible.  Fine-grained access is enforced per-endpoint
/// via the qiwa.read / qiwa.sync / qiwa.configure permissions (not role strings).
///
/// Real Qiwa API calls are performed by the background QiwaSyncWorker once
/// credentials are configured and the live adapter is enabled.
///
/// <para><b>PRODUCTION CONFIGURATION IS REFUSED, NOT ACCEPTED-AND-IGNORED.</b> Which adapter this
/// process runs is decided at startup by <c>QIWA_USE_LIVE_ADAPTER</c>, and nothing at runtime reads
/// the per-tenant <c>Environment</c> column to choose it. So accepting
/// <c>Environment = "production"</c> with a 200 while the process is running the sandbox simulator
/// stores configuration that no runtime path applies — the exact silent misconfiguration the F1
/// convergence removed, and the reason <c>ApprovalPoliciesController</c> answers 410 rather than
/// pretending. The same doctrine applies here: a request to be live that this process cannot honour
/// gets <c>501 Not Implemented</c>, a machine-readable code, and a pointer to what would make it
/// true. Nothing is written.</para>
/// </summary>
[ApiController]
[Route("api/qiwa")]
[Authorize]
public class QiwaController : ControllerBase
{
    /// <summary>Machine-readable code for "you asked for live Qiwa; this deployment is a simulator".</summary>
    public const string LiveNotConfiguredCode = "qiwa_live_adapter_not_configured";

    public const string LiveNotConfiguredMessage =
        "This deployment is running the Qiwa SANDBOX SIMULATOR, which makes no network calls and "
        + "files nothing with Qiwa or MHRSD. Saving a 'production' Qiwa configuration here would be "
        + "stored but never applied, and the compliance screens would report filings that never "
        + "happened. Set QIWA_USE_LIVE_ADAPTER=true on the API service and restart it, then save "
        + "production credentials. Until then, configure the connection as 'sandbox'.";

    private readonly IQiwaIntegrationService _qiwa;
    private readonly IQiwaApiAdapter _adapter;

    public QiwaController(IQiwaIntegrationService qiwa, IQiwaApiAdapter adapter)
    {
        _qiwa = qiwa;
        _adapter = adapter;
    }

    /// <summary>
    /// 501 when the caller asks to be live and this process cannot be. Null when the request is
    /// honourable. Deliberately NOT a 400: the request is well-formed and would be correct against
    /// a live deployment — it is this server that does not implement it.
    /// </summary>
    private IActionResult? RefuseIfLiveUnsupported(string? environment)
    {
        if (!string.Equals(environment?.Trim(), "production", StringComparison.OrdinalIgnoreCase))
            return null;
        if (_adapter.IsLiveIntegration) return null;

        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            code = LiveNotConfiguredCode,
            message = LiveNotConfiguredMessage,
            replacement = "PUT /api/qiwa/connection with environment='sandbox'",
            runtimeAdapter = _adapter.AdapterName,
            filesWithQiwa = false,
        });
    }

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>Returns the Qiwa connection configuration for the current tenant.</summary>
    [HttpGet("connection")]
    public async Task<IActionResult> GetConnection(CancellationToken cancellationToken)
    {
        if (!HasPermission("qiwa.read")) return Forbid();

        // The integration mode of the RUNNING PROCESS, not the stored Environment column. These
        // two disagreed silently before: a tenant row could say "production" while the process had
        // only ever run the simulator. The screen needs the truth about what will actually happen.
        var live = _adapter.IsLiveIntegration;
        var simulationNotice = live
            ? null
            : "Qiwa integration is running in SIMULATION. No employee record is filed with Qiwa or "
              + "MHRSD, and no request leaves this server. Set QIWA_USE_LIVE_ADAPTER=true and supply "
              + "live credentials to file for real.";

        var connection = await _qiwa.GetConnectionStatusAsync(RequireTenant(), cancellationToken);
        if (connection is null)
            return Ok(new
            {
                status = "Disconnected",
                configured = false,
                runtimeAdapter = _adapter.AdapterName,
                isLiveIntegration = live,
                filesWithQiwa = live,
                simulationNotice,
            });

        return Ok(new
        {
            connection.Id,
            connection.TenantId,
            connection.EstablishmentId,
            connection.EstablishmentName,
            connection.UnifiedOrganisationNumber,
            connection.Environment,
            connection.Status,
            connection.LastConnectedAtUtc,
            connection.LastCheckedAtUtc,
            configured = true,
            hasError    = connection.Status is "ConfigurationError" or "ApiError",
            connection.LastErrorMessage,
            runtimeAdapter = _adapter.AdapterName,
            isLiveIntegration = live,
            filesWithQiwa = live,
            simulationNotice,
            // Loud, specific case: the tenant believes it is configured for production and the
            // process cannot honour that. Stored configuration that no runtime path applies.
            configurationIgnored = !live
                && string.Equals(connection.Environment, "production", StringComparison.OrdinalIgnoreCase)
                ? LiveNotConfiguredMessage
                : null,
        });
    }

    /// <summary>Saves or updates the Qiwa establishment configuration for the tenant.</summary>
    [HttpPut("connection")]
    public async Task<IActionResult> UpsertConnection([FromBody] QiwaConnectionRequest request, CancellationToken cancellationToken)
    {
        if (!HasPermission("qiwa.configure")) return Forbid();

        if (string.IsNullOrWhiteSpace(request.EstablishmentId))
            return BadRequest(new { error = "establishment_id_required", message = "EstablishmentId is required." });

        if (request.Environment is not ("sandbox" or "production"))
            return BadRequest(new { error = "invalid_environment", message = "Environment must be 'sandbox' or 'production'." });

        // Refuse BEFORE the write. Storing a production connection this process will never honour
        // is how a customer comes to believe their workforce is filed when it is not.
        if (RefuseIfLiveUnsupported(request.Environment) is { } refusal) return refusal;

        try
        {
            var conn = await _qiwa.UpsertConnectionAsync(
                RequireTenant(), request, GetUserId(),
                HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                cancellationToken);

            return Ok(new { conn.Id, conn.EstablishmentId, conn.Environment, conn.Status });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Saves the tenant's Qiwa OAuth2 client credentials.  The secret is encrypted
    /// at rest and never returned.  Requires qiwa.configure.
    /// </summary>
    [HttpPut("credentials")]
    public async Task<IActionResult> SaveCredentials([FromBody] QiwaCredentialRequest request, CancellationToken cancellationToken)
    {
        if (!HasPermission("qiwa.configure")) return Forbid();

        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
            return BadRequest(new { error = "credentials_required", message = "ClientId and ClientSecret are required." });

        if (request.Environment is not ("sandbox" or "production"))
            return BadRequest(new { error = "invalid_environment", message = "Environment must be 'sandbox' or 'production'." });

        // Same refusal as the connection write, and for the same reason: accepting live credentials
        // that no runtime path will ever use is worse than refusing them. Note this also avoids
        // persisting a real production client secret into a deployment that cannot use it.
        if (RefuseIfLiveUnsupported(request.Environment) is { } refusal) return refusal;

        await _qiwa.SaveApiCredentialAsync(
            RequireTenant(), request.ClientId.Trim(), request.ClientSecret, request.Environment.Trim(),
            GetUserId() ?? Guid.Empty,
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            cancellationToken);

        // Never echo the secret back.
        return Ok(new { configured = true, environment = request.Environment, message = "Qiwa credentials saved and encrypted." });
    }

    // ── Employee readiness ────────────────────────────────────────────────────

    /// <summary>Returns a report of Qiwa-required fields that are missing for an employee.</summary>
    [HttpGet("employees/{employeeId:int}/readiness")]
    public async Task<IActionResult> GetEmployeeReadiness(int employeeId, CancellationToken cancellationToken)
    {
        if (!HasPermission("qiwa.read")) return Forbid();

        try
        {
            var report = await _qiwa.CheckEmployeeReadinessAsync(RequireTenant(), employeeId, cancellationToken);
            return Ok(report);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>Tenant-wide readiness summary: ready vs blocked employee counts.</summary>
    [HttpGet("readiness-summary")]
    public async Task<IActionResult> GetReadinessSummary(CancellationToken cancellationToken)
    {
        if (!HasPermission("qiwa.read")) return Forbid();
        return Ok(await _qiwa.GetReadinessSummaryAsync(RequireTenant(), cancellationToken));
    }

    /// <summary>Aggregate QIWA compliance summary for dashboards.</summary>
    [HttpGet("compliance-summary")]
    public async Task<IActionResult> GetComplianceSummary(CancellationToken cancellationToken)
    {
        if (!HasPermission("qiwa.read")) return Forbid();
        return Ok(await _qiwa.GetComplianceSummaryAsync(RequireTenant(), cancellationToken));
    }

    // ── Sync ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues a Qiwa sync attempt for a single employee (Status=Pending).
    /// The background worker performs the actual push.
    /// </summary>
    [HttpPost("employees/{employeeId:int}/sync")]
    public async Task<IActionResult> EnqueueSync(int employeeId, [FromQuery] string direction = "Push", CancellationToken cancellationToken = default)
    {
        if (!HasPermission("qiwa.sync")) return Forbid();

        if (direction is not ("Push" or "Pull"))
            return BadRequest(new { error = "invalid_direction", message = "Direction must be 'Push' or 'Pull'." });

        try
        {
            var log = await _qiwa.EnqueueEmployeeSyncAsync(
                RequireTenant(), employeeId, direction, "Manual", GetUserId(), cancellationToken);

            return Accepted(new
            {
                log.Id,
                log.EmployeeId,
                log.Direction,
                log.Status,
                log.TriggerSource,
                log.CreatedAtUtc,
                message = "Sync enqueued. The background worker will process it shortly."
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Enqueues all Qiwa-ready active employees for bulk sync.
    /// Already-pending employees are skipped; non-ready employees are omitted silently.
    /// Use GET /readiness-summary first to surface blocked employees.
    /// </summary>
    [HttpPost("sync/bulk")]
    public async Task<IActionResult> EnqueueBulkSync(CancellationToken cancellationToken)
    {
        if (!HasPermission("qiwa.sync")) return Forbid();

        var result = await _qiwa.EnqueueBulkSyncAsync(
            RequireTenant(), "ManualBulk", GetUserId(), cancellationToken);

        return Accepted(new
        {
            result.TotalReady,
            result.Enqueued,
            result.SkippedAlreadyPending,
            result.EnqueuedEmployeeIds,
            message = result.Enqueued == 0
                ? "No new employees were enqueued. All ready employees may already be pending."
                : $"{result.Enqueued} employee(s) enqueued for sync."
        });
    }

    /// <summary>Resets a dead-lettered sync log back to Pending for reprocessing.</summary>
    [HttpPost("sync-logs/{syncLogId:guid}/retry")]
    public async Task<IActionResult> RetryDeadLetter(Guid syncLogId, CancellationToken cancellationToken)
    {
        if (!HasPermission("qiwa.sync")) return Forbid();

        try
        {
            await _qiwa.RetryDeadLetterAsync(RequireTenant(), syncLogId, GetUserId() ?? Guid.Empty, cancellationToken);
            return Ok(new { syncLogId, status = "Pending", message = "Sync log reset for retry." });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    // ── Sync logs ─────────────────────────────────────────────────────────────

    /// <summary>Returns paginated Qiwa sync logs for the tenant, optionally filtered to one employee.</summary>
    [HttpGet("sync-logs")]
    public async Task<IActionResult> GetSyncLogs(
        [FromQuery] int? employeeId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (!HasPermission("qiwa.read")) return Forbid();

        pageSize = Math.Clamp(pageSize, 1, 100);
        var logs = await _qiwa.GetSyncLogsAsync(RequireTenant(), employeeId, page, pageSize, cancellationToken);
        return Ok(new { page, pageSize, data = logs });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Guid RequireTenant()
        => Guid.Parse(User.FindFirstValue("tenant_id") ?? throw new UnauthorizedAccessException("Tenant claim missing."));

    private Guid? GetUserId()
        => Guid.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"),
            out var id) ? id : null;

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
}

public record QiwaCredentialRequest(string ClientId, string ClientSecret, string Environment);
