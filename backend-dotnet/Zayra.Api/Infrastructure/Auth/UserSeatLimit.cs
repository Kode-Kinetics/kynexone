using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// The plan's user allowance, checked before a login is created. Shared by User Management's create-user endpoint
/// and the create-company wizard's company super admin, so the wizard cannot mint a login the plan does not allow.
/// </summary>
public static class UserSeatLimit
{
    public sealed record Refusal(int Current, int Limit)
    {
        public const string Code = "user_limit_reached";
        public string Message => $"You have reached your user limit ({Current}/{Limit}). Please upgrade your subscription.";
    }

    /// <summary>Null when another active user fits the plan.</summary>
    public static async Task<Refusal?> CheckAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var sub = await db.TenantSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (sub is null || sub.MaxUsers <= 0) return null;
        var count = await db.Users.CountAsync(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted, ct);
        return count >= sub.MaxUsers ? new Refusal(count, sub.MaxUsers) : null;
    }
}
