using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// One role a designation (or, additively, a grade) is meant to carry. Exactly one of <see cref="DesignationId"/> /
/// <see cref="GradeId"/> is set (CHECK). Configured in Setup by a company administrator; never holds a privileged role
/// (ADMIN, or any role holding <c>security.manage</c>): that is refused when the mapping is saved and again when it is applied.
/// The designation's default access MODE lives on <see cref="Designation.DefaultAccessMode"/> (one fact, one place).
/// </summary>
public class AccessRoleMapping : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? DesignationId { get; set; }
    public Guid? GradeId { get; set; }
    public Guid RoleId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

/// <summary>
/// Marker: this role on this user was put there by a designation/grade mapping, so a later designation change may take it
/// away again. A role the user holds for any other reason (granted by hand, by an invitation's explicit list) has NO row here
/// and is never removed by the designation engine. One row per (user, role).
/// </summary>
public class DesignationRoleGrant : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    /// <summary>The designation whose mapping granted it (null when only the grade's mapping did).</summary>
    public Guid? DesignationId { get; set; }
    public Guid? GradeId { get; set; }
    public DateTime GrantedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? GrantedBy { get; set; }
}
