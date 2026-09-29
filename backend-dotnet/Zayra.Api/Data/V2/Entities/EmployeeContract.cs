using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The effective-dated employment contract — type, term, probation, contracted hours and notice period — with an optional pointer to the signed scan. @tier:T @owner:HR @retention:Keep
/// </summary>
public partial class EmployeeContract
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid? DocumentId { get; set; }

    public string? ContractType { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public DateOnly? StartDate { get; set; }

    /// <summary>
    /// Named end_date, not `end`: `end` is a reserved word (CONVENTIONS.md §1).
    /// </summary>
    public DateOnly? EndDate { get; set; }

    public DateOnly? ProbationEnd { get; set; }

    public decimal? WeeklyHours { get; set; }

    public int? NoticeDays { get; set; }

    /// <summary>
    /// Kept per §2.D. §18 says the external_system/external_id/external_synced_at trio should replace it — unreconciled in revision 6.
    /// </summary>
    public string? QiwaContractNo { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
