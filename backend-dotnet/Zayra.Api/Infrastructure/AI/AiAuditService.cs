using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.AI;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.AI;

public sealed class AiAuditService : IAiAuditService
{
    private readonly ZayraDbContext _db;
    private readonly AiOptions _options;
    private readonly ILogger<AiAuditService> _logger;

    public AiAuditService(ZayraDbContext db, AiOptions options, AiRedactionService redaction, ILogger<AiAuditService> logger)
    {
        _db = db;
        _options = options;
        _logger = logger;
    }

    public async Task LogAsync(AiAuditEntry entry, CancellationToken cancellationToken)
    {
        var query = _options.LogPrompts ? entry.Query : entry.PromptSummary;

        try
        {
            var response = new AIHRQueryLog
            {
                TenantId = entry.TenantId,
                UserId = entry.UserId ?? Guid.Empty,
                EmployeeId = entry.EmployeeId,
                UserRole = entry.UserRole,
                Query = query,
                PromptHash = entry.PromptHash,
                PromptSummary = entry.PromptSummary,
                Response = entry.Response,
                IntentClassified = entry.IntentClassified,
                Module = entry.Module,
                WasBlocked = entry.WasBlocked,
                BlockedReason = entry.BlockedReason,
                Provider = entry.Provider,
                Model = entry.Model,
                ResponseStatus = entry.ResponseStatus,
                HumanReviewRequired = entry.HumanReviewRequired,
                IsAdvisoryLabelShown = entry.IsAdvisoryLabelShown,
                TokensUsed = entry.TokensUsed,
                PromptTokens = entry.PromptTokens,
                CompletionTokens = entry.CompletionTokens,
                ResponseTimeMs = entry.ResponseTimeMs,
                LoggedPrompt = _options.LogPrompts ? entry.Query : string.Empty
            };

            _db.AIHRQueryLogs.Add(response);
            await _db.SaveChangesAsync(cancellationToken);

            await UpdateUsageAsync(entry, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "AI audit logging failed for tenant {TenantId}, user {UserId}, intent {Intent}. Blocking AI response because audit persistence is mandatory.",
                entry.TenantId,
                entry.UserId,
                entry.IntentClassified);
            throw new InvalidOperationException("ai_audit_persistence_failed", ex);
        }
    }

    private async Task UpdateUsageAsync(AiAuditEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var yearMonth = int.Parse(DateTime.UtcNow.ToString("yyyyMM"));
            var usage = await _db.TenantAiUsages
                .FirstOrDefaultAsync(u => u.TenantId == entry.TenantId && u.YearMonth == yearMonth, cancellationToken);

            if (usage is null)
            {
                usage = new TenantAiUsage { TenantId = entry.TenantId, YearMonth = yearMonth };
                _db.TenantAiUsages.Add(usage);
            }

            usage.TokensUsed += entry.TokensUsed;
            usage.LastUpdatedUtc = DateTime.UtcNow;
            if (entry.WasBlocked) usage.BlockedCount++;
            else usage.RequestCount++;

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update AI usage counters for tenant {TenantId}.", entry.TenantId);
        }
    }
}
