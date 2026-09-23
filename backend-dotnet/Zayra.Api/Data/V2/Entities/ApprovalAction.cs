using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Records each approve, reject, return, comment or escalate decision taken on an approval request, including the person it was taken on behalf of. @tier:T @owner:HR @retention:84m-keep
/// </summary>
public partial class ApprovalAction
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RequestId { get; set; }

    public Guid ActorUserId { get; set; }

    public Guid? OnBehalfOfUserId { get; set; }

    public string Action { get; set; } = null!;

    public int Step { get; set; }

    public string? Comment { get; set; }

    public DateTime ActedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ApprovalRequest ApprovalRequest { get; set; } = null!;

    public virtual User User { get; set; } = null!;

    public virtual User? UserNavigation { get; set; }
}
