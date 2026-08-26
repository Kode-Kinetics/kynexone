using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Notifications;

/// <summary>
/// A resolved contact set for ONE person inside ONE tenant. Every field on it was read with an
/// explicit <c>TenantId ==</c> predicate and the owning row's TenantId was re-asserted, so a
/// notification can never be dispatched to a contact outside the owning tenant.
/// </summary>
public sealed record NotificationRecipient
{
    public required Guid TenantId { get; init; }
    public Guid? UserId { get; init; }
    public int? EmployeeId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public IReadOnlyList<PushTarget> PushTargets { get; init; } = [];
    /// <summary>False for terminated/resigned/inactive employees — they stop receiving short-channel pushes.</summary>
    public bool IsActiveEmployee { get; init; } = true;

    public string AudienceType => EmployeeId.HasValue
        ? NotificationAudiences.Employee
        : UserId.HasValue ? NotificationAudiences.User : NotificationAudiences.External;

    /// <summary>Stable, non-GUID recipient identity used inside the dedupe key.</summary>
    public string RecipientKey => EmployeeId.HasValue
        ? $"emp:{EmployeeId.Value}"
        : UserId.HasValue ? $"usr:{UserId.Value:N}" : $"ext:{Email?.Trim().ToLowerInvariant()}";
}

public interface INotificationRecipientResolver
{
    Task<NotificationRecipient?> ResolveAsync(ZayraDbContext db, Guid tenantId, Guid? userId, int? employeeId,
        CancellationToken ct);

    Task<NotificationRecipient?> ResolveByEmailAsync(ZayraDbContext db, Guid tenantId, string email,
        CancellationToken ct);
}

public sealed class NotificationRecipientResolver : INotificationRecipientResolver
{
    private static readonly string[] InactiveEmployeeStatuses =
        ["Terminated", "Resigned", "Inactive", "Exited", "Offboarded", "Retired", "Deceased"];

    /// <summary>
    /// Resolves the recipient from a user id and/or an employee id. Both directions are linked so
    /// a User-audience notification still reaches the employee's ESS/mobile feed and phone, and an
    /// Employee-audience notification still reaches the admin bell when a login exists.
    /// </summary>
    public async Task<NotificationRecipient?> ResolveAsync(ZayraDbContext db, Guid tenantId, Guid? userId,
        int? employeeId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || (userId is null && employeeId is null)) return null;

        // IgnoreQueryFilters + explicit tenant predicate: the delivery worker has no HttpContext, so
        // the ambient filter is bypassed there. Never rely on it in this path.
        var user = userId is null ? null : await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.Id == userId.Value && !u.IsDeleted)
            .Select(u => new { u.Id, u.TenantId, u.Email, u.FullName, u.PhoneNumber, u.IsActive })
            .FirstOrDefaultAsync(ct);

        // Pre-send tenant re-assertion (defence in depth against a future filter regression).
        if (user is not null && user.TenantId != tenantId) return null;

        var employeeQuery = db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted);
        employeeQuery = employeeId is not null
            ? employeeQuery.Where(e => e.Id == employeeId.Value)
            : employeeQuery.Where(e => e.UserAccountId == userId!.Value);

        var employee = await employeeQuery
            .Select(e => new { e.Id, e.TenantId, e.FullName, e.WorkEmail, e.PersonalEmail, e.Phone, e.Status, e.UserAccountId })
            .FirstOrDefaultAsync(ct);

        // Legacy linkage fallback: PayrollController joins Users↔Employees on u.Email == e.WorkEmail
        // because UserAccountId was not always populated. Mirror it, but only inside this tenant.
        if (employee is null && employeeId is null && !string.IsNullOrWhiteSpace(user?.Email))
        {
            var normalized = user.Email.Trim().ToUpperInvariant();
            // IgnoreQueryFilters is intentional: resolution runs in a worker/child scope with no ambient
            // tenant; the WHERE pins the tenant explicitly.
            employee = await db.Employees.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.TenantId == tenantId && !e.IsDeleted
                    && (e.WorkEmail.ToUpper() == normalized || e.PersonalEmail.ToUpper() == normalized))
                .Select(e => new { e.Id, e.TenantId, e.FullName, e.WorkEmail, e.PersonalEmail, e.Phone, e.Status, e.UserAccountId })
                .FirstOrDefaultAsync(ct);
        }

        if (employee is not null && employee.TenantId != tenantId) return null;
        if (user is null && employee is null) return null;

        var resolvedUserId = userId ?? employee?.UserAccountId;
        var pushTargets = employee is null
            ? []
            : await LoadPushTargetsAsync(db, tenantId, employee.Id, ct);

        return new NotificationRecipient
        {
            TenantId = tenantId,
            UserId = resolvedUserId,
            EmployeeId = employee?.Id,
            DisplayName = FirstNonEmpty(employee?.FullName, user?.FullName, user?.Email) ?? "Employee",
            Email = FirstNonEmpty(user?.Email, employee?.WorkEmail, employee?.PersonalEmail),
            Phone = FirstNonEmpty(employee?.Phone, user?.PhoneNumber),
            PushTargets = pushTargets,
            IsActiveEmployee = employee is null
                ? user?.IsActive ?? true
                : !InactiveEmployeeStatuses.Contains(employee.Status, StringComparer.OrdinalIgnoreCase),
        };
    }

    public async Task<NotificationRecipient?> ResolveByEmailAsync(ZayraDbContext db, Guid tenantId, string email,
        CancellationToken ct)
    {
        if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(email)) return null;
        var normalized = email.Trim().ToUpperInvariant();

        // IgnoreQueryFilters is intentional: as above — tenant pinned in the WHERE.
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == tenantId && !u.IsDeleted && u.Email.ToUpper() == normalized)
            .Select(u => new { u.Id })
            .FirstOrDefaultAsync(ct);
        if (user is not null) return await ResolveAsync(db, tenantId, user.Id, null, ct);

        // IgnoreQueryFilters is intentional: as above — tenant pinned in the WHERE.
        var employee = await db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted
                && (e.WorkEmail.ToUpper() == normalized || e.PersonalEmail.ToUpper() == normalized))
            .Select(e => new { e.Id })
            .FirstOrDefaultAsync(ct);
        return employee is null ? null : await ResolveAsync(db, tenantId, null, employee.Id, ct);
    }

    private static async Task<IReadOnlyList<PushTarget>> LoadPushTargetsAsync(ZayraDbContext db, Guid tenantId,
        int employeeId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: as above — tenant pinned in the WHERE.
        var devices = await db.EmployeeMobileDevices.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.EmployeeId == employeeId && d.PushToken != string.Empty)
            .Select(d => new { d.Id, d.PushToken, d.Platform })
            .ToListAsync(ct);
        return devices.Select(d => new PushTarget(d.Id, d.PushToken, d.Platform)).ToList();
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
