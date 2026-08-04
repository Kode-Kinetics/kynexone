using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// POD-B2 — an operator's explicit, audited INTENT to include or exclude one employee from a payroll run.
///
/// Deliberately a separate table from <see cref="PayrollRunEmployee"/>: that one is run OUTPUT, wiped and
/// rebuilt inside the Process transaction (PayrollController.Process — the RemoveRange block), so intent
/// stored there would be destroyed on every re-process. Selections survive re-processing.
///
/// Semantics resolved by PayrollController.ResolveRunPopulationAsync:
///   • any Include row present  → ALLOW-LIST mode: population = eligible ∩ include   (non-Regular runs only)
///   • no Include row present   → DENY-LIST mode:  population = eligible
///   • minus every Exclude row, always
///
/// Regular runs are DENY-LIST ONLY — an Include row on the monthly run is rejected at the API, because one
/// stray Include row would silently reduce the month to one person and the partial unique index would then
/// block creating a second Regular run to catch everyone else.
///
/// <see cref="Outcome"/> is stamped by Process so an exclusion is visibly reported (never silently dropped),
/// alongside an EMPLOYEE_EXCLUDED_FROM_RUN validation result and an acknowledgement gate at Approve.
/// </summary>
public class PayrollRunEmployeeSelection : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Legal-entity scope — always set to the run's CompanyId on write, so the company write guard applies.</summary>
    public Guid? CompanyId { get; set; }

    public Guid PayrollRunId { get; set; }

    /// <summary>Employee PK (int), matching PayrollRunEmployee/PayrollSlip.</summary>
    public int EmployeeId { get; set; }

    /// <summary>"Include" | "Exclude" — see <see cref="PayrollRunSelectionModes"/>.</summary>
    public string Mode { get; set; } = PayrollRunSelectionModes.Exclude;

    /// <summary>Mandatory operator reason. A hold-out without a documented reason is not auditable.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// "Included" | "Excluded" | "NotEligible" — stamped by Process. Null until the run is first processed,
    /// which is why the run header also surfaces a PENDING selection count (an unprocessed Exclude row
    /// produces no validation result yet and would otherwise be invisible).
    /// </summary>
    public string? Outcome { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
