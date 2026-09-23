using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Every validation finding and standing readiness gap that blocks or warns a payroll run, with the evidence behind it and — for warnings only — who waived it and why. @tier:C @owner:Finance @retention:Keep
/// </summary>
public partial class PayrollIssue
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// NULL = a standing gap that blocks ANY run for this employee (GOSI_COHORT_UNKNOWN, IBAN_MISSING, ORG_ESTABLISHMENT_MISSING, SALARY_HELD). Non-NULL = a finding of one run, which dies with a draft run.
    /// </summary>
    public Guid? RunId { get; set; }

    public Guid? EmployeeId { get; set; }

    public Guid? OverrideBy { get; set; }

    public string Code { get; set; } = null!;

    /// <summary>
    /// Block is NEVER overridable — enforced by CHECK, not by the UI (§2.F). Warn may be overridden with a recorded reason.
    /// </summary>
    public string Severity { get; set; } = null!;

    public string? GapType { get; set; }

    public string Message { get; set; } = null!;

    public string? Evidence { get; set; }

    public DateTime DetectedAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public string? OverrideReason { get; set; }

    public DateTime? OverrideAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Employee? Employee { get; set; }

    public virtual PayrollRun? PayrollRun { get; set; }

    public virtual User? User { get; set; }
}
