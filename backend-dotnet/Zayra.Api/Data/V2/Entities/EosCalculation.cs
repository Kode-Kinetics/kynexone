using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Computes an end-of-service award under the Labour Law articles that apply to the separation, freezing the resolved statutory bands so the figure can be reconstructed years later. @tier:T @owner:Finance @retention:84m-keep
/// </summary>
public partial class EosCalculation
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid? SettlementId { get; set; }

    public string Status { get; set; } = null!;

    public string SeparationReason { get; set; } = null!;

    public DateOnly CalculationDate { get; set; }

    public DateOnly ServiceStartDate { get; set; }

    public DateOnly ServiceEndDate { get; set; }

    public int ServiceDays { get; set; }

    public int ExcludedUnpaidDays { get; set; }

    public string? LastWageBasis { get; set; }

    public decimal EligibleWage { get; set; }

    public decimal Amount { get; set; }

    public decimal PriorPaidDeducted { get; set; }

    public string? RulesVersion { get; set; }

    public string RulesSnapshot { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
