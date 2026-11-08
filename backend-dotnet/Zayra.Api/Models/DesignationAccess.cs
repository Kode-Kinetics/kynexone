using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// One existing role a designation is meant to carry (Setup → Designations → Access roles). Nothing new about roles or permissions:
/// the role is an ordinary tenant role from Setup → Roles. Never a privileged role (ADMIN, or any role holding <c>security.manage</c>)
/// and never a platform-wide one: refused when the mapping is saved and skipped again when it is applied.
/// An employee given the designation gets the role; the role is marked on their <see cref="UserRole.GrantedByDesignationId"/> so that a later
/// designation change can take it away again (and never a role granted by hand).
/// </summary>
public class DesignationAccessRole : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid DesignationId { get; set; }
    public Guid RoleId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Who configured it: the apply step re-checks that this person could still give the role.</summary>
    public Guid? CreatedBy { get; set; }
}
