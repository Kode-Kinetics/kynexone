using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Itemises a final settlement into its end-of-service, encashment, notice-pay and recovery components, frozen once the settlement is approved. @tier:C @owner:Finance @retention:84m-keep
/// </summary>
public partial class FinalSettlementLine
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SettlementId { get; set; }

    public Guid? LoanInstallmentId { get; set; }

    public string Kind { get; set; } = null!;

    public string? Description { get; set; }

    public decimal Amount { get; set; }

    public string? SourceType { get; set; }

    public Guid? SourceId { get; set; }

    public string? RulesVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public virtual FinalSettlement FinalSettlement { get; set; } = null!;

    public virtual LoanInstallment? LoanInstallment { get; set; }
}
