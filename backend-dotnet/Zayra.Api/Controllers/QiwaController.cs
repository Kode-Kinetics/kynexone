using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

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
        "This server runs the Qiwa data check only: it makes no network calls and files nothing with "
        + "Qiwa or MHRSD. Saving a 'production' Qiwa configuration here would be stored but never "
        + "applied. Sending anything to Qiwa needs a signed Qiwa partner agreement recorded by the platform "
        + "operator. Until then, keep the connection as 'sandbox' and record changes in Qiwa itself.";

    /// <summary>Machine-readable code for "the OAuth credential form is switched off on this server".</summary>
    public const string CredentialsDisabledCode = "qiwa_credentials_disabled";

    public const string DataCheckNotice =
        "Qiwa data check: this checks your employee records against what Qiwa requires. Nothing is sent "
        + "to Qiwa or MHRSD, and no employee record is filed. Record contract and employee changes in "
        + "Qiwa itself.";

    private readonly IQiwaIntegrationService _qiwa;
    private readonly IQiwaApiAdapter _adapter;
    private readonly IConfiguration? _configuration;

    public QiwaController(IQiwaIntegrationService qiwa, IQiwaApiAdapter adapter, IConfiguration? configuration = null)
    {
        _qiwa = qiwa;
        _adapter = adapter;
        _configuration = configuration;
    }

    /// <summary>
    /// 501 when QIWA_USE_LIVE_ADAPTER asked for live calls and no partner agreement is on file. Every
    /// endpoint that would send something to Qiwa checks this first, so the refused adapter is never
    /// even asked.
    /// </summary>
    private IActionResult? RefuseIfLiveAdapterRefused()
    {
        if (_adapter is not RefusedLiveQiwaApiAdapter) return null;
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            code = QiwaLiveAdapterPolicy.RefusedCode,
            message = QiwaLiveAdapterPolicy.RefusedMessage,
            runtimeAdapter = _adapter.AdapterName,
            filesWithQiwa = false,
        });
    }

    /// <summary>Machine-readable code for "nothing can be sent to Qiwa from this server, so nothing is queued".</summary>
    public const string SyncUnavailableCode = "qiwa_sync_unavailable";

    public const string SyncUnavailableMessage =
        "Nothing is sent to Qiwa from this server, so there is nothing to queue or retry. Use the Qiwa data "
        + "check to see which employee records are incomplete, and record changes in Qiwa itself.";

    /// <summary>
    /// Sync, bulk sync and retry only make sense when the partner-agreement adapter is running. Without it,
    /// a queued item could only ever end in a dead letter (and flip the connection to ConfigurationError for
    /// want of credentials no one can enter), which reads as "Qiwa checks need attention" with nothing to
    /// fix. So they are refused up front: 501 from the refused-live adapter (its own code), 409 otherwise.
    /// </summary>
    private IActionResult? RefuseIfNothingCanBeSent()
    {
        if (RefuseIfLiveAdapterRefused() is { } refusedLive) return refusedLive;
        if (_adapter.IsLiveIntegration) return null;
        return Conflict(new
        {
            code = SyncUnavailableCode,
            message = SyncUnavailableMessage,
            runtimeAdapter = _adapter.AdapterName,
            filesWithQiwa = false,
        });
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
        var simulationNotice = live ? null : DataCheckNotice;
        // Operator-owned flag, default OFF. The OAuth form only makes sense once a partner agreement
        // exists; until then it invites customers to paste secrets into a path that files nothing.
        var credentialFormEnabled = QiwaLiveAdapterPolicy.CredentialFormEnabled(_configuration);
        var liveAdapterRefused = _adapter is RefusedLiveQiwaApiAdapter;

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
                integrationMode = QiwaSyncLogStatuses.ModeLabel(live),
                credentialFormEnabled,
                liveAdapterRefused,
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
            integrationMode = QiwaSyncLogStatuses.ModeLabel(live),
            credentialFormEnabled,
            liveAdapterRefused,
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
        if (string.Equals(request.Environment, "production", StringComparison.OrdinalIgnoreCase)
            && RefuseIfLiveAdapterRefused() is { } refusedLive) return refusedLive;

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

        if (!QiwaLiveAdapterPolicy.CredentialFormEnabled(_configuration))
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                code = CredentialsDisabledCode,
                message = "Qiwa API credentials are not accepted on this server. Live Qiwa calls need a signed "
                          + "partner agreement, and the platform operator has not switched credential entry on. "
                          + "Nothing was saved.",
                filesWithQiwa = false,
            });
        if (RefuseIfLiveAdapterRefused() is { } refusedLive) return refusedLive;

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
        var summary = await _qiwa.GetComplianceSummaryAsync(RequireTenant(), cancellationToken);
        var live = _adapter.IsLiveIntegration;
        // F09: the summary carries the mode of the RUNNING process, so a client can never render a
        // simulator's results without the label.
        return Ok(new
        {
            summary.ConnectionStatus,
            summary.LastConnectedAt,
            summary.ReadinessPercent,
            summary.EmployeesBlocked,
            summary.FailedSyncCount,
            summary.LastSuccessfulSync,
            summary.LastSimulatedSync,
            runtimeAdapter = _adapter.AdapterName,
            isLiveIntegration = live,
            integrationMode = QiwaSyncLogStatuses.ModeLabel(live),
        });
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
        if (RefuseIfNothingCanBeSent() is { } refused) return refused;

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
        if (RefuseIfNothingCanBeSent() is { } refused) return refused;

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
        if (RefuseIfNothingCanBeSent() is { } refused) return refused;

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

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var logs = await _qiwa.GetSyncLogsAsync(RequireTenant(), employeeId, page, pageSize, cancellationToken);
        // F09: a simulator run reads "Simulated", with its label, including rows written before the
        // worker stopped calling them "Success". filedWithQiwa is true only for a live acknowledgement.
        var data = logs.Select(l =>
        {
            var simulated = QiwaSyncLogStatuses.IsSimulated(l.Status, l.ResponsePayloadJson);
            return new
            {
                l.Id,
                l.TenantId,
                l.EmployeeId,
                l.Direction,
                Status = QiwaSyncLogStatuses.Normalise(l.Status, l.ResponsePayloadJson),
                StatusLabel = QiwaSyncLogStatuses.Describe(l.Status, l.ResponsePayloadJson),
                Simulated = simulated,
                FiledWithQiwa = !simulated && l.Status == QiwaSyncLogStatuses.Success,
                l.TriggerSource,
                // Never the stored request/response bodies: rows written before the scrubber hold the
                // employee's record as Qiwa echoed it. Status, label and error below are what a reader needs.
                l.HttpStatusCode,
                l.ErrorMessage,
                l.TriggeredBy,
                l.CreatedAtUtc,
                l.CompletedAtUtc,
                l.RetryCount,
                l.MaxRetries,
                l.LastRetriedAtUtc,
                l.DeadLetterReason,
            };
        });
        return Ok(new { page, pageSize, data });
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
