using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>A designation-access rule refused (a privileged role, an unknown role or mode, a role above the caller). A 400 for the caller.</summary>
public sealed class DesignationAccessException(string message, string code) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>What one reconcile did to one employee's login.</summary>
public sealed record DesignationAccessOutcome(
    bool HasLogin,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    string? AccessModeBefore,
    string? AccessModeAfter,
    string? SkippedReason)
{
    public static DesignationAccessOutcome NoLogin { get; } = new(false, [], [], null, null, null);
    public bool Changed => Added.Count > 0 || Removed.Count > 0 || AccessModeBefore != AccessModeAfter;
}

/// <summary>The access a designation carries, for the Setup screens.</summary>
public sealed record DesignationAccessView(string? DefaultAccessMode, IReadOnlyList<string> Roles);

/// <summary>
/// THE ONE place designation/grade -> access is decided (see <see cref="Models.AccessRoleMapping"/>). Every path that gives an
/// employee a designation or grade calls <see cref="ReconcileAsync"/>; every path that creates a login asks
/// <see cref="MappedRolesForNewLoginAsync"/> instead of hard-coding the Employee role.
/// Saves nothing: the caller's unit of work commits it with the employee change that caused it (like the login provisioner).
/// </summary>
public sealed class DesignationAccessService
{
    public const string AppliedAction = "access.designation_access_applied";
    public const string SkippedAction = "access.designation_access_skipped";
    public const string ConfiguredAction = "organization.designation_access_configured";
    public const string GradeConfiguredAction = "organization.grade_access_configured";

    private readonly ZayraDbContext _db;
    public DesignationAccessService(ZayraDbContext db) => _db = db;

    /// <summary>The vocabulary a designation may default to: every access mode except NoLogin (that is "cannot sign in").</summary>
    public static IReadOnlyList<string> AllowedAccessModes { get; } =
    [
        AccessModes.FullPortal, AccessModes.EssOnly, AccessModes.ManagerPortal, AccessModes.HRPortal, AccessModes.PayrollPortal,
        AccessModes.FinancePortal, AccessModes.SupervisorPortal, AccessModes.ReadOnlyAuditor, AccessModes.Mobile, AccessModes.KioskOnly,
    ];

    // ── Configuration (Setup) ───────────────────────────────────────────────────────────────────────

    public Task SetDesignationMappingAsync(Guid tenantId, Designation designation, string? accessMode, IReadOnlyCollection<string>? roleNames,
        RequestContext actor, CancellationToken ct) => throw new NotImplementedException();

    public Task SetGradeMappingAsync(Guid tenantId, Grade grade, IReadOnlyCollection<string>? roleNames,
        RequestContext actor, CancellationToken ct) => throw new NotImplementedException();

    public Task<IReadOnlyDictionary<Guid, DesignationAccessView>> GetDesignationAccessAsync(Guid tenantId, IReadOnlyCollection<Guid> designationIds,
        CancellationToken ct) => throw new NotImplementedException();

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetGradeRolesAsync(Guid tenantId, IReadOnlyCollection<Guid> gradeIds,
        CancellationToken ct) => throw new NotImplementedException();

    // ── Applying ────────────────────────────────────────────────────────────────────────────────────

    public Task<DesignationAccessOutcome> ReconcileAsync(Guid tenantId, Employee employee, RequestContext actor, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<Role>> MappedRolesForNewLoginAsync(Guid tenantId, Employee employee, CancellationToken ct) =>
        throw new NotImplementedException();

    public void MarkGranted(Guid tenantId, Guid userId, Employee employee, IEnumerable<Role> roles, RequestContext actor) =>
        throw new NotImplementedException();

    public Task<string?> ActivationAccessModeAsync(Guid tenantId, Employee employee, CancellationToken ct) =>
        throw new NotImplementedException();
}
