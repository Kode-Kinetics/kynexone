using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// The recorded review of a payroll run's manpower and value reconciliation against the prior month. At most one is
/// <c>Active</c> per run; it is superseded (never edited, never deleted) when the figures it reviewed can change: a
/// re-process, reopen, send-back, void, a withdrawal, or a change to the comparison run.
///
/// <para>The bridge itself is derived from the run's slips and payslip lines on every read. This row freezes only its
/// fingerprint (<see cref="SnapshotHash"/>) and a summary (<see cref="SnapshotJson"/>: counts and totals, no pay by
/// employee), plus the process version it was signed against.</para>
/// </summary>
public class PayrollRunReconSignoff : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope, stamped from the run.</summary>
    public Guid? CompanyId { get; set; }
    public Guid PayrollRunId { get; set; }

    /// <summary><c>payroll_runs.process_version</c> when this was signed.</summary>
    public int ProcessVersion { get; set; }
    /// <summary>The comparison run, null when the run had none.</summary>
    public Guid? PriorRunId { get; set; }
    public int? PriorProcessVersion { get; set; }

    public string SnapshotHash { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = "{}";

    public Guid? SignedOffByUserId { get; set; }
    public string SignedOffByName { get; set; } = string.Empty;
    public DateTime SignedOffAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary><see cref="Zayra.Api.Infrastructure.Payroll.ManpowerRecon.ReconSignoffStatuses"/>.</summary>
    public string Status { get; set; } = "Active";
    public DateTime? SupersededAtUtc { get; set; }
    /// <summary><see cref="Zayra.Api.Infrastructure.Payroll.ManpowerRecon.ReconSupersededReasons"/>.</summary>
    public string? SupersededReason { get; set; }
    public string? Notes { get; set; }

    public List<PayrollRunReconExplanation> Explanations { get; set; } = new();
}

/// <summary>A written reason given at sign-off for one flagged item or for one unpaid or unexplained employee.</summary>
public class PayrollRunReconExplanation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SignoffId { get; set; }
    /// <summary><c>Item</c> or <c>Employee</c>.</summary>
    public string Scope { get; set; } = string.Empty;
    /// <summary>The item's component code, or the employee id.</summary>
    public string Key { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
