using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Time-boxes the handover of one user approval authority to another for a named set of request types. @tier:T @owner:HR @retention:84m-keep
/// </summary>
public partial class ApprovalDelegation
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DelegatorUserId { get; set; }

    public Guid DelegateUserId { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public List<string> RequestTypes { get; set; } = null!;

    public string? Reason { get; set; }

    public bool IsActive { get; set; }

    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
