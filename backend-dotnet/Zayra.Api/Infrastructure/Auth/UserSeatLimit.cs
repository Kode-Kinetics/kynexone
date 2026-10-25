using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// The licence's user allowance, checked before a login is created. Shared by User Management's create-user endpoint
/// and the create-company wizard's company super admin, so the wizard cannot mint a login the licence does not allow.
/// The callers' check is a preflight; the one that holds is the recount <see cref="AccessManagementService"/> makes
/// under the tenant row lock, which throws <see cref="UserSeatLimitReachedException"/>.
/// </summary>
public static class UserSeatLimit
{
    public sealed record Refusal(int Current, int Limit)
    {
        public const string Code = "user_limit_reached";
        public string Message => $"You have reached your licensed user limit ({Current}/{Limit}). Contact your KynexOne supplier to extend your licence.";

        /// <summary>The 422 body every door answers with: <c>{ error, message, current, limit }</c>.</summary>
        public object Body => new { error = Code, message = Message, current = Current, limit = Limit };
    }

    /// <summary>Null when another active user fits the licence. <paramref name="maxUsers"/> 0 = unlimited.</summary>
    public static async Task<Refusal?> CheckAsync(ZayraDbContext db, Guid tenantId, int maxUsers, CancellationToken ct)
    {
        if (maxUsers <= 0) return null;
        var count = await db.Users.CountAsync(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted, ct);
        return count >= maxUsers ? new Refusal(count, maxUsers) : null;
    }
}

/// <summary>The licence's last seat was taken between a caller's preflight and the locked create. An
/// <see cref="InvalidOperationException"/> so every existing catch still refuses it; the doors that know it map it to
/// the same 422 <c>user_limit_reached</c> their preflight returns.</summary>
public sealed class UserSeatLimitReachedException(UserSeatLimit.Refusal refusal) : InvalidOperationException(refusal.Message)
{
    public UserSeatLimit.Refusal Refusal { get; } = refusal;
}
