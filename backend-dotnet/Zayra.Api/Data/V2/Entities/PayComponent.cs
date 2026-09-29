using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The catalogue of everything that can appear as a line on a payslip, each declaring whether it is GOSI-contributory, EOS-eligible, prorated and which GL driver it posts through. @tier:T @owner:Finance
/// </summary>
public partial class PayComponent
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Code { get; set; } = null!;

    public string Kind { get; set; } = null!;

    public string NameEn { get; set; } = null!;

    public string? NameAr { get; set; }

    public bool GosiContributory { get; set; }

    public bool EosEligible { get; set; }

    public bool Prorate { get; set; }

    public string? GlDriver { get; set; }

    /// <summary>
    /// Seeded per tenant at provisioning: BASIC, HOUSING, TRANSPORT, OT, GOSI_ANN_EE/ER, SANED_EE/ER, OH_ER, LOAN, ADVANCE, UNPAID_LEAVE, ABSENCE. A tenant may add components but not remove these.
    /// </summary>
    public bool IsSystem { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
