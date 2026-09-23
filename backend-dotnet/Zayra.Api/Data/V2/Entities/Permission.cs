using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The platform permission catalogue, including the access.grant.* keys that govern delegated granting authority; retiring a permission is a migration, never a delete. @tier:R @owner:Platform @retention:Keep
/// </summary>
public partial class Permission
{
    public Guid Id { get; set; }

    public string Code { get; set; } = null!;

    public string Module { get; set; } = null!;

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}
