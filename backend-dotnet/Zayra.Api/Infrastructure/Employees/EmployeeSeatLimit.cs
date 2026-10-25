using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The licence's employee allowance: it caps ACTIVE employees, so it is checked on every transition INTO Active —
/// activation, draft approval, rehire, offboarding reinstatement and import — not only on create. Mirrors
/// <see cref="Zayra.Api.Infrastructure.Auth.UserSeatLimit"/>: a caller's <see cref="CheckAsync"/> is a preflight; the
/// check that holds is <see cref="EnsureSeatAsync"/>, the recount made under an organisation-wide advisory lock
/// inside the writer's transaction, so two activations racing for the last seat cannot both commit.
/// </summary>
public static class EmployeeSeatLimit
{
    public sealed record Refusal(int Current, int Limit)
    {
        public const string Code = "employee_limit_reached";
        public string Message => $"Your licence allows up to {Limit} active employees. You have {Current}. Contact your KynexOne supplier to extend it.";

        /// <summary>The 422 body every door answers with: <c>{ error, message, current, limit }</c>.</summary>
        public object Body => new { error = Code, message = Message, current = Current, limit = Limit };
    }

    /// <summary>Null when another active employee fits. <paramref name="maxEmployees"/> 0 = unlimited.</summary>
    public static async Task<Refusal?> CheckAsync(ZayraDbContext db, Guid tenantId, int maxEmployees, CancellationToken ct,
        int? excludeEmployeeId = null)
    {
        if (maxEmployees <= 0) return null;
        var count = await CountActiveAsync(db, tenantId, excludeEmployeeId, ct);
        return count >= maxEmployees ? new Refusal(count, maxEmployees) : null;
    }

    /// <summary>
    /// The holding check, for a writer about to make <paramref name="employeeId"/> Active. Takes the organisation's
    /// employee-seat advisory lock (transaction-scoped, so call it inside the writer's transaction; it is released at
    /// commit or rollback), then recounts. Throws <see cref="EmployeeSeatLimitReachedException"/> when no seat is left.
    /// </summary>
    public static async Task EnsureSeatAsync(ZayraDbContext db, Guid tenantId, int maxEmployees, int employeeId, CancellationToken ct)
    {
        if (maxEmployees <= 0) return;
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is not null)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({SeatLockKey(tenantId)}, 0))", ct);
        if (await CheckAsync(db, tenantId, maxEmployees, ct, excludeEmployeeId: employeeId) is { } refusal)
            throw new EmployeeSeatLimitReachedException(refusal);
    }

    internal static string SeatLockKey(Guid tenantId) => $"employee-seats:{tenantId:N}";

    private static Task<int> CountActiveAsync(ZayraDbContext db, Guid tenantId, int? excludeEmployeeId, CancellationToken ct) =>
        Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                "The licence caps active employees across the whole organisation, whatever companies the caller can see.")
            .CountAsync(e => e.Status == EmployeeStatuses.Active && !e.IsDeleted
                             && (excludeEmployeeId == null || e.Id != excludeEmployeeId), ct);
}

/// <summary>The licence's last employee seat was taken. An <see cref="InvalidOperationException"/> so every existing
/// catch still refuses it; the doors that know it answer 422 <c>employee_limit_reached</c>.</summary>
public sealed class EmployeeSeatLimitReachedException(EmployeeSeatLimit.Refusal refusal) : InvalidOperationException(refusal.Message)
{
    public EmployeeSeatLimit.Refusal Refusal { get; } = refusal;
}
