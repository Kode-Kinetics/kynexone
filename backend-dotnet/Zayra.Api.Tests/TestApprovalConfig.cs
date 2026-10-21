using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F1 — leave submission no longer invents an approver when a tenant has no approval workflow; it
/// raises ApprovalRouteNotConfiguredException. Tests that exercise leave mechanics (balances,
/// overlap, accrual, scope) rather than routing configure the same single-step default every
/// provisioned tenant receives (TenantProvisioningBundle: LEAVE-DEFAULT, one HR step).
/// </summary>
internal static class TestApprovalConfig
{
    /// <summary>Idempotently installs the provisioning-default leave workflow for a tenant.</summary>
    public static async Task<ApprovalWorkflow> EnsureDefaultLeaveWorkflowAsync(ZayraDbContext db, Guid tenantId, string approverType = "HR")
    {
        var existing = await db.ApprovalWorkflows.Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.EntityName == nameof(LeaveRequest) && w.IsActive
                && w.DepartmentId == null && w.GradeId == null);
        if (existing is not null) return existing;

        var workflow = new ApprovalWorkflow
        {
            TenantId = tenantId, Code = "LEAVE-DEFAULT", Name = "Default Leave Approval",
            EntityName = nameof(LeaveRequest), IsDefault = true, IsActive = true
        };
        workflow.Steps.Add(new ApprovalWorkflowStep
        {
            TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "Approval",
            ApproverType = approverType, ApproverRole = approverType == "HR" ? "HR Manager" : approverType, IsFinalStep = true
        });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync();
        // A provisioned tenant always has someone who can decide its leave; routing now refuses a submission
        // nobody could ever decide (ApprovalNoEligibleDeciderException), so the fixture has one too.
        await EnsureDeciderAsync(db, tenantId);
        return workflow;
    }

    /// <summary>On a relational provider users and roles reference the tenant row; make sure there is one.</summary>
    private static async Task EnsureTenantRowAsync(ZayraDbContext db, Guid tenantId)
    {
        if (!db.Database.IsRelational()) return;
        if (db.Tenants.Local.Any(t => t.Id == tenantId) || await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId)) return;
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test tenant", Slug = $"test-{tenantId:N}" });
        await db.SaveChangesAsync();
    }

    /// <summary>Deactivates a login: for a test that needs "nobody else can decide" AFTER a request was routed.</summary>
    public static async Task DeactivateAsync(ZayraDbContext db, Guid userId)
    {
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        user.IsActive = false;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Gives <paramref name="userId"/> an active login row in the tenant (idempotent), optionally holding
    /// <paramref name="roleName"/>. A reporting-line approver without an active login cannot act, so the router
    /// sends their step to the HR Manager queue; fixtures that mean "the manager decides" give the manager one.
    /// </summary>
    public static async Task EnsureLoginAsync(ZayraDbContext db, Guid tenantId, Guid userId, string? roleName = null)
    {
        await EnsureTenantRowAsync(db, tenantId);
        if (!db.Users.Local.Any(u => u.Id == userId) && !await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId))
        {
            var email = $"login.{userId:N}@example.test";
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = "Test login", Status = "Active", IsActive = true });
        }
        if (roleName is not null)
        {
            var normalized = roleName.Trim().ToUpperInvariant();
            var role = db.Roles.Local.FirstOrDefault(r => r.TenantId == tenantId && r.NormalizedName == normalized)
                ?? await db.Roles.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.NormalizedName == normalized);
            if (role is null)
            {
                role = new Role { TenantId = tenantId, Name = roleName.Trim(), NormalizedName = normalized, Description = roleName.Trim(), IsActive = true };
                db.Roles.Add(role);
            }
            var roleId = role.Id;
            if (!db.UserRoles.Local.Any(ur => ur.UserId == userId && ur.RoleId == roleId)
                && !await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId))
                db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        }
        // The login can decide approvals (routing counts the effective approvals.decide, as Decide does): a direct
        // Allow override, so it neither adds a role nor depends on one.
        if (!db.Set<UserPermissionOverride>().Local.Any(o => o.UserId == userId && o.PermissionKey == "approvals.decide")
            && !await db.Set<UserPermissionOverride>().IgnoreQueryFilters().AnyAsync(o => o.UserId == userId && o.PermissionKey == "approvals.decide"))
            db.Set<UserPermissionOverride>().Add(new UserPermissionOverride { TenantId = tenantId, UserId = userId, PermissionKey = "approvals.decide", Effect = "Allow", IsActive = true, Reason = "test decider" });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Idempotently gives the tenant one active user holding <paramref name="roleName"/> (default HR Manager): an
    /// eligible decider, so routing does not refuse a request as one nobody could ever decide. The user is linked
    /// to no employee and requested nothing, so no separation-of-duties bar touches them.
    /// </summary>
    public static async Task<Guid> EnsureDeciderAsync(ZayraDbContext db, Guid tenantId, string roleName = "HR Manager", params string[] permissions)
    {
        // A decider's role carries approvals.decide: routing counts effective permissions, as Decide does.
        permissions = permissions.Append("approvals.decide").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await EnsureTenantRowAsync(db, tenantId);
        var normalized = roleName.Trim().ToUpperInvariant();
        var role = await db.Roles.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.NormalizedName == normalized);
        if (role is null)
        {
            role = new Role { TenantId = tenantId, Name = roleName.Trim(), NormalizedName = normalized, Description = roleName.Trim(), IsActive = true };
            db.Roles.Add(role);
        }
        foreach (var key in permissions)
        {
            var permission = db.Permissions.Local.FirstOrDefault(p => p.Key == key)
                ?? await db.Permissions.FirstOrDefaultAsync(p => p.Key == key);
            if (permission is null)
            {
                permission = new Permission { Key = key, Module = "Test" };
                db.Permissions.Add(permission);
            }
            var roleId = role.Id;
            var permissionId = permission.Id;
            if (!db.RolePermissions.Local.Any(rp => rp.RoleId == roleId && rp.PermissionId == permissionId)
                && !await db.RolePermissions.AnyAsync(rp => rp.RoleId == roleId && rp.PermissionId == permissionId))
                db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionId = permissionId });
        }
        var email = $"decider.{normalized.Replace(' ', '-').ToLowerInvariant()}.{tenantId:N}@example.test";
        var existing = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Email == email);
        if (existing is not null) return existing.Id;
        var user = new User
        {
            TenantId = tenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(),
            FullName = $"{roleName.Trim()} decider", Status = "Active", IsActive = true,
        };
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>A LeaveService on the real router, with the tenant's default leave workflow installed.</summary>
    public static async Task<LeaveService> LeaveServiceAsync(ZayraDbContext db, Guid tenantId, string approverType = "HR")
    {
        await EnsureDefaultLeaveWorkflowAsync(db, tenantId, approverType);
        return new LeaveService(db, new ApprovalRouter(db));
    }
}
