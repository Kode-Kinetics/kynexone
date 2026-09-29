using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Grants a role to a user, optionally narrowed to one company, branch or department and optionally time-boxed; NULL scope columns mean the whole tenant. @tier:T @owner:Platform
/// </summary>
public partial class UserRole
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid RoleId { get; set; }

    public Guid? ScopeCompanyId { get; set; }

    public Guid? ScopeBranchId { get; set; }

    public Guid? ScopeDepartmentId { get; set; }

    /// <summary>
    /// Who made THIS grant. Who MAY grant is a different fact and lives in permission_grantor_records (§2.B).
    /// </summary>
    public Guid? GrantedBy { get; set; }

    public DateTime GrantedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
