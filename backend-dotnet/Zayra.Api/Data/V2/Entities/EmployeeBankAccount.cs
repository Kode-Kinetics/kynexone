using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The effective-dated payment instruction an employee is paid to, so a WPS file filed last March can still be explained by the IBAN that was current then. @tier:T @owner:Finance @retention:Keep
/// </summary>
public partial class EmployeeBankAccount
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    /// <summary>
    /// Bounded to 34 and pattern-checked for SA IBANs by CHECK; the mod-97 checksum is enforced in the service (§13.3).
    /// </summary>
    public string Iban { get; set; } = null!;

    public string? BankCode { get; set; }

    public string? AccountHolderName { get; set; }

    public string? PaymentMethod { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Employee Employee { get; set; } = null!;
}
