using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>STUB (TDD red): door-guard helpers for cost accounting. Implemented in the slice-5 commit.</summary>
public static class CostAccountingGuards
{
    public static Task<bool> HasRunAwaitingFinanceOrLockAsync(ZayraDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct) => Task.FromResult(false);
    public static object LockedIntoApprovedRunBody => new { };
}

/// <summary>STUB (TDD red): identity segregation of duties. Implemented in the slice-5 commit.</summary>
public static class CostAccountingSod
{
    public const string ErrorCode = "";
    public static IReadOnlyCollection<string> RelevantActions { get; } = Array.Empty<string>();
    public static object RefusalBody => new { };
    public static Task<bool> HasConflictAsync(ZayraDbContext db, Guid tenantId, Guid companyId, string period, Guid userId, CancellationToken ct) => Task.FromResult(false);
    public static Task<bool> ApiKeyHasConflictAsync(ZayraDbContext db, Guid tenantId, Guid companyId, string period, Guid apiKeyId, CancellationToken ct) => Task.FromResult(false);
}
