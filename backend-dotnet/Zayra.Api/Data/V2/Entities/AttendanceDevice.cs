using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Registers a biometric or terminal device at a branch with its API credential hash, sync watermark and replay-nonce window. @tier:C @owner:HR @retention:tenant-lifecycle
/// </summary>
public partial class AttendanceDevice
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public string Serial { get; set; } = null!;

    public string? Name { get; set; }

    public string? Model { get; set; }

    public string ApiKeyHash { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime? LastSeenAt { get; set; }

    public DateTime? SyncWatermark { get; set; }

    public string RecentNonces { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Branch Branch { get; set; } = null!;
}
