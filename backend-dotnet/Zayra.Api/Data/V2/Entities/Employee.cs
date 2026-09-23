using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The person: current identity, statutory identifiers, employment anchors and the PDPL lifecycle that lets an erasure anonymise the record in place while every dependent payroll row keeps its foreign key. @tier:T @owner:HR @retention:84-months-from-Separation-then-Anonymise
/// </summary>
public partial class Employee
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// Allocated from number_sequences. Retained through anonymisation so statutory payroll rows stay traceable (§12.3).
    /// </summary>
    public string EmployeeNumber { get; set; } = null!;

    /// <summary>
    /// Current-state PROJECTION maintained by EmployeeLifecycleService (§10.7). employee_assignments is authoritative for &quot;was this person employed on date D&quot; (§11.3).
    /// </summary>
    public string Status { get; set; } = null!;

    public string PrivacyStatus { get; set; } = null!;

    public string NameEn { get; set; } = null!;

    public string? NameAr { get; set; }

    /// <summary>
    /// Added to satisfy §19.4 H1, which indexes it; §2.D does not list it. See the note above this comment.
    /// </summary>
    public string? WorkEmail { get; set; }

    public string? Gender { get; set; }

    public DateOnly? Dob { get; set; }

    public string? NationalityCode { get; set; }

    public string? NationalId { get; set; }

    public string? IqamaNo { get; set; }

    public string? BorderNo { get; set; }

    public DateOnly? JoiningDate { get; set; }

    /// <summary>
    /// Drives the GOSI cohort (Legacy vs Entrant2024). NULL raises a BLOCKING payroll_issues row; the code never defaults to a cohort (§2.E).
    /// </summary>
    public DateOnly? GosiFirstRegisteredOn { get; set; }

    public bool WpsEligible { get; set; }

    public decimal? NitaqatWeightOverride { get; set; }

    public string? NitaqatWeightOverrideReason { get; set; }

    public DateOnly? EosServiceStartDate { get; set; }

    public decimal? EosPriorPaidAmount { get; set; }

    /// <summary>
    /// Projection of final_settlements.last_working_day, written when the settlement is approved; the settlement is authoritative (§11.3).
    /// </summary>
    public DateOnly? SeparationDate { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateOnly? RetentionUntil { get; set; }

    public DateTime? RedactedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
