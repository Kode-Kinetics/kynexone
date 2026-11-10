namespace Zayra.Api.Domain.Entities;

public class UserRole
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    /// <summary>
    /// Set when the role was given because of the user's designation (see <c>DesignationAccessService</c>): the designation that granted it.
    /// Null = given by hand (an administrator, an invitation's explicit list), and never taken away by a designation change.
    /// Saving a user's roles by hand re-creates the rows, which makes every role manual.
    /// </summary>
    public Guid? GrantedByDesignationId { get; set; }
}
