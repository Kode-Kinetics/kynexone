using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Qiwa;

/// <summary>
/// Background service that drains pending Qiwa sync logs every 30 seconds.
///
/// For each pending log (RetryCount &lt; MaxRetries) it loads the tenant's
/// connection + credentials, acquires an access token (cached), builds the
/// employee payload and pushes it through the configured <see cref="IQiwaApiAdapter"/>.
/// Failures are retried with exponential backoff; exhausted records are
/// dead-lettered.  Every state transition is audited.  All DB access is
/// tenant-scoped.
/// </summary>
public sealed class QiwaSyncWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IQiwaApiAdapter _adapter;
    private readonly QiwaOAuthTokenCache _tokenCache;
    private readonly IDataProtectionProvider _protectionProvider;
    private readonly ILogger<QiwaSyncWorker> _log;

    public QiwaSyncWorker(
        IServiceScopeFactory scopeFactory,
        IQiwaApiAdapter adapter,
        QiwaOAuthTokenCache tokenCache,
        IDataProtectionProvider protectionProvider,
        ILogger<QiwaSyncWorker> log)
    {
        _scopeFactory = scopeFactory;
        _adapter = adapter;
        _tokenCache = tokenCache;
        _protectionProvider = protectionProvider;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("QiwaSyncWorker started (adapter={Adapter}, poll={Poll}s).", _adapter.AdapterName, PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "QiwaSyncWorker iteration failed.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("QiwaSyncWorker stopping.");
    }

    internal async Task ProcessOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var protector = _protectionProvider.CreateProtector(QiwaIntegrationService.SecretPurpose);

        // Requeue Failed logs whose exponential backoff window has elapsed.
        // Backoff = 2^retryCount * 30s → 60s, 120s, 240s before each subsequent attempt.
        var now = DateTime.UtcNow;
        // SYSTEM CONTEXT: tenant scope intentionally bypassed — background worker has no HTTP
        // request context; must process logs for all tenants in a single pass.
        var failed = await db.QiwaSyncLogs
            .IgnoreQueryFilters()
            .Where(l => l.Status == QiwaSyncLogStatuses.Failed && l.RetryCount < l.MaxRetries && l.Direction == "Push")
            .ToListAsync(ct);
        foreach (var f in failed)
        {
            var backoffSeconds = Math.Pow(2, f.RetryCount) * 30;
            if (f.LastRetriedAtUtc is null || (now - f.LastRetriedAtUtc.Value).TotalSeconds >= backoffSeconds)
                f.Status = QiwaSyncLogStatuses.Pending;
        }
        if (failed.Count > 0) await db.SaveChangesAsync(ct);

        // SYSTEM CONTEXT: tenant scope intentionally bypassed — same reasoning as above.
        // Tenant scoping is re-applied per-group inside the foreach below.
        var pending = await db.QiwaSyncLogs
            .IgnoreQueryFilters()
            .Where(l => l.Status == QiwaSyncLogStatuses.Pending && l.RetryCount < l.MaxRetries && l.Direction == "Push")
            .OrderBy(l => l.CreatedAtUtc)
            .Take(50)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        foreach (var group in pending.GroupBy(l => l.TenantId))
        {
            var tenantId = group.Key;

            // Feature flag: absent row = enabled; explicit IsEnabled=false = skip.
            // SYSTEM CONTEXT: tenant scope intentionally bypassed; explicit TenantId predicate above.
            var featureFlag = await db.TenantFeatureFlags.IgnoreQueryFilters()
                .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FeatureKey == FeatureKeys.QiwaIntegration, ct);
            if (featureFlag is { IsEnabled: false })
            {
                _log.LogDebug(
                    "QiwaSyncWorker: qiwa_integration disabled for tenant {TenantId} — skipping.",
                    tenantId);
                continue;
            }

            // SYSTEM CONTEXT: tenant scope intentionally bypassed; explicit TenantId predicate above.
            var connection = await db.QiwaTenantConnections.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);
            // SYSTEM CONTEXT: tenant scope intentionally bypassed; explicit TenantId predicate above.
            var credential = await db.QiwaApiCredentials.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);

            // Acquire / reuse token for this tenant.
            string? token = _tokenCache.TryGet(tenantId);
            if (token is null)
            {
                var clientId     = credential?.ClientId;
                var clientSecret = SafeUnprotect(protector, credential?.EncryptedClientSecret);
                var environment  = connection?.Environment ?? credential?.Environment ?? "sandbox";

                // Validate credentials before calling the adapter.
                // Missing or un-decryptable secrets are a non-retryable configuration
                // error — dead-letter all pending logs for this tenant immediately so
                // we don't burn retries on an unfixable state, and surface the problem
                // via connection.Status so the admin can see it in the dashboard.
                if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                {
                    var reason = string.IsNullOrWhiteSpace(clientId)
                        ? "Missing QIWA client ID — credentials not configured for this tenant."
                        : "QIWA client secret missing or could not be decrypted.";

                    // SAFE: logs a status flag only, never any credential value.
                    _log.LogWarning(
                        "QiwaSyncWorker: incomplete credentials for tenant {TenantId} — skipping sync. Reason: {Reason}",
                        tenantId, reason);

                    if (connection is not null)
                    {
                        connection.Status           = QiwaConnectionStatuses.ConfigurationError;
                        connection.LastErrorMessage = reason;
                        connection.LastCheckedAtUtc = now;
                    }

                    foreach (var log in group)
                    {
                        log.ErrorMessage     = reason;
                        log.Status           = QiwaSyncLogStatuses.DeadLetter;
                        log.DeadLetterReason = reason.Length > 500 ? reason[..500] : reason;
                        log.CompletedAtUtc   = now;

                        Audit(db, tenantId, "qiwa.sync_missing_credentials", log.EmployeeId,
                            new { syncLogId = log.Id, reason });
                    }

                    await db.SaveChangesAsync(ct);
                    continue; // skip remaining logs for this tenant, advance to next group
                }

                // clientId and clientSecret are both validated non-null and non-empty here.
                // C# flow analysis via [NotNullWhen(false)] on IsNullOrWhiteSpace gives the
                // compiler proof that clientSecret cannot be null — CS8604 is resolved
                // without null-forgiving operators.
                token = await _adapter.AcquireAccessTokenAsync(clientId, clientSecret, environment, ct);
                if (token is not null)
                    _tokenCache.Set(tenantId, token, credential?.TokenExpiresAtUtc is { } e
                        ? (int)Math.Max(60, (e - DateTime.UtcNow).TotalSeconds)
                        : (int?)null);
            }

            foreach (var log in group)
            {
                await ProcessLogAsync(db, connection, token, log, tenantId, ct);
            }

            if (connection is not null)
            {
                connection.LastCheckedAtUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private async Task ProcessLogAsync(
        ZayraDbContext db, QiwaTenantConnection? connection, string? token,
        QiwaSyncLog log, Guid tenantId, CancellationToken ct)
    {
        // SYSTEM CONTEXT: tenant scope intentionally bypassed; explicit TenantId and IsDeleted
        // predicates below replace the global filter for this background-worker call site.
        var employee = await db.Employees.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == log.EmployeeId && !e.IsDeleted, ct);

        if (employee is null)
        {
            FailOrDeadLetter(db, log, employee, tenantId, "Employee not found or deleted.", "EMPLOYEE_NOT_FOUND");
            return;
        }

        // Company-scope hardening (Phase 2): a legal entity that has been deactivated must
        // not keep syncing to Qiwa — its establishment registration is per-company. Skip
        // (not dead-letter): the log resumes automatically if the company is reactivated.
        // SYSTEM CONTEXT: tenant scope intentionally bypassed; explicit TenantId predicate.
        if (employee.CompanyId is Guid employeeCompanyId)
        {
            var companyActive = await db.Companies.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(c => c.TenantId == tenantId && c.Id == employeeCompanyId && c.IsActive && !c.IsDeleted, ct);
            if (!companyActive)
            {
                _log.LogInformation(
                    "QiwaSyncWorker: skipping sync log {SyncLogId} — employee {EmployeeId} belongs to inactive company {CompanyId} in tenant {TenantId}.",
                    log.Id, log.EmployeeId, employeeCompanyId, tenantId);
                return;
            }
        }

        if (token is null)
        {
            FailOrDeadLetter(db, log, employee, tenantId, "Could not acquire Qiwa access token (missing/invalid credentials).", "NO_TOKEN");
            return;
        }

        // Pre-flight readiness check: dead-letter immediately if required fields are missing
        // so we don't burn retries on a state that won't resolve without an HR data fix.
        var readiness = QiwaIntegrationService.BuildReadinessReport(employee);
        if (!readiness.IsReady)
        {
            var reason = "Employee not Qiwa-ready — missing required fields: " +
                         string.Join("; ", readiness.BlockingReasons);
            log.Status           = QiwaSyncLogStatuses.DeadLetter;
            log.DeadLetterReason = reason.Length > 500 ? reason[..500] : reason;
            log.ErrorMessage     = reason;
            log.CompletedAtUtc   = DateTime.UtcNow;
            employee.QiwaSyncStatus = QiwaSyncStatuses.Error;
            Audit(db, tenantId, "qiwa.sync_not_ready", log.EmployeeId,
                new { syncLogId = log.Id, missingFields = readiness.MissingFields });
            _log.LogWarning(
                "QiwaSyncWorker: employee {EmployeeId} not Qiwa-ready, dead-lettering sync log {SyncLogId}.",
                log.EmployeeId, log.Id);
            return;
        }

        var establishmentId = !string.IsNullOrWhiteSpace(employee.EstablishmentId)
            ? employee.EstablishmentId
            : connection?.EstablishmentId ?? string.Empty;

        var payload = new QiwaEmployeePayload(
            employee.EmployeeCode, employee.IdNumber, employee.IdType,
            employee.Nationality, employee.SaudiOrNonSaudi, employee.OccupationCode,
            establishmentId, employee.WorkLocationId, employee.ContractReference);

        QiwaApiResult result;
        try
        {
            result = await _adapter.PushEmployeeAsync(token, payload, ct);
        }
        catch (Exception ex)
        {
            result = new QiwaApiResult(false, "EXCEPTION", ex.Message, null);
        }

        log.LastRetriedAtUtc = DateTime.UtcNow;
        log.ResponsePayloadJson = result.RawResponse;

        if (result.Success)
        {
            log.Status         = QiwaSyncLogStatuses.Success;
            log.CompletedAtUtc = DateTime.UtcNow;
            log.ErrorMessage   = null;
            employee.QiwaSyncStatus = QiwaSyncStatuses.Synced;
            if (connection is not null)
            {
                connection.Status = QiwaConnectionStatuses.Connected;
                connection.LastConnectedAtUtc = DateTime.UtcNow;
                connection.LastErrorMessage = null;
            }
            Audit(db, tenantId, "qiwa.sync_success", log.EmployeeId, new { syncLogId = log.Id });
            _log.LogInformation("Qiwa sync success: employee {EmployeeId} tenant {TenantId}", log.EmployeeId, tenantId);
        }
        else
        {
            var error = $"{result.ErrorCode}: {result.ErrorMessage}";
            FailOrDeadLetter(db, log, employee, tenantId, error, result.ErrorCode);
            if (connection is not null && result.ErrorCode is "NETWORK_ERROR" or "NO_TOKEN")
                connection.Status = QiwaConnectionStatuses.ApiError;
        }
    }

    private void FailOrDeadLetter(
        ZayraDbContext db, QiwaSyncLog log, Employee? employee, Guid tenantId, string error, string? errorCode)
    {
        log.RetryCount   += 1;
        log.ErrorMessage  = error;

        if (log.RetryCount >= log.MaxRetries)
        {
            log.Status           = QiwaSyncLogStatuses.DeadLetter;
            log.DeadLetterReason = error.Length > 500 ? error[..500] : error;
            log.CompletedAtUtc   = DateTime.UtcNow;
            if (employee is not null) employee.QiwaSyncStatus = QiwaSyncStatuses.Error;
            Audit(db, tenantId, "qiwa.sync_dead_letter", log.EmployeeId, new { syncLogId = log.Id, error, errorCode });
            _log.LogWarning("Qiwa sync dead-lettered: employee {EmployeeId} tenant {TenantId} after {Retries} retries", log.EmployeeId, tenantId, log.RetryCount);
        }
        else
        {
            log.Status = QiwaSyncLogStatuses.Failed;
            if (employee is not null) employee.QiwaSyncStatus = QiwaSyncStatuses.Error;
            Audit(db, tenantId, "qiwa.sync_failed", log.EmployeeId, new { syncLogId = log.Id, error, errorCode, retryCount = log.RetryCount });
            _log.LogInformation("Qiwa sync failed (will retry): employee {EmployeeId} tenant {TenantId} attempt {Retry}", log.EmployeeId, tenantId, log.RetryCount);
        }
    }

    private static void Audit(ZayraDbContext db, Guid tenantId, string action, int employeeId, object metadata)
    {
        db.AuditLogs.Add(new Domain.Entities.AuditLog
        {
            TenantId   = tenantId,
            Action     = action,
            EntityName = "Employee",
            EntityId   = employeeId.ToString(),
            Metadata   = System.Text.Json.JsonSerializer.Serialize(metadata),
        });
    }

    private string? SafeUnprotect(IDataProtector protector, string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return null;
        try { return protector.Unprotect(encrypted); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to decrypt Qiwa client secret.");
            return null;
        }
    }
}
