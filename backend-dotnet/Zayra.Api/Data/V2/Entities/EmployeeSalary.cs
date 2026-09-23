using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The effective-dated salary structure — basic, housing, transport and any further components — that a payroll run resolves as of the period, in the employing company&apos;s currency. @tier:T @owner:Finance @retention:Keep
/// </summary>
public partial class EmployeeSalary
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public decimal Basic { get; set; }

    public decimal Housing { get; set; }

    public decimal Transport { get; set; }

    /// <summary>
    /// When true, housing is deemed at the statutory percentage of basic for the contributory wage rather than paid in cash (§2.E).
    /// </summary>
    public bool HousingInKind { get; set; }

    /// <summary>
    /// Amounts only. There is no per-row currency column anywhere; the currency is companies.currency_code (§13.2).
    /// </summary>
    public string? Components { get; set; }

    public string? ChangeReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
