using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Maps each payroll GL driver, optionally narrowed by company and cost centre, onto the debit and credit account codes owned by the customer ERP chart of accounts. @tier:T @owner:Finance @retention:tenant-lifecycle
/// </summary>
public partial class GlMapping
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? CompanyId { get; set; }

    public Guid? CostCenterId { get; set; }

    public string GlDriver { get; set; } = null!;

    public string DebitAccount { get; set; } = null!;

    public string CreditAccount { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
