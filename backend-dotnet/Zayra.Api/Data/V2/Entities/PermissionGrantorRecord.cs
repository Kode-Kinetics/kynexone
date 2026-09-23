using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Records that a named user MAY grant permissions over a stated scope, with or without sub-delegation, until a date and for a reason — the authority behind a grant, not the grant itself. @tier:T @owner:Platform
/// </summary>
public partial class PermissionGrantorRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid GrantorUserId { get; set; }

    public Guid? GrantedByUserId { get; set; }

    public Guid? RevokedBy { get; set; }

    /// <summary>
    /// &apos;all&apos;, a module prefix, or an explicit key list. Sub-delegation may only narrow the parent scope (§10.11), checked in AccessManagementService.
    /// </summary>
    public string PermissionScope { get; set; } = null!;

    public bool CanSubDelegate { get; set; }

    public bool IsActive { get; set; }

    public string? Reason { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual User? User { get; set; }

    public virtual User? User1 { get; set; }

    public virtual User UserNavigation { get; set; } = null!;
}
