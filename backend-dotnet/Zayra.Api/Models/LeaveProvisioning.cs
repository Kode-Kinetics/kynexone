using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// One employee's annual-leave OBLIGATION as measured at a payroll month end, and the provision MOVEMENT the
/// Lock journal booked for it. The leave counterpart of <see cref="EosbProvisionSnapshot"/>.
///
/// <para><b>WHY A TABLE.</b> The GL line alone says how much moved; it cannot say how many unused days the
/// employee held or the day rate they were valued at, which is what an auditor re-performs
/// (days x rate = obligation). The snapshot is that witness, written in the same transaction as the Lock
/// journal and deleted by the run's void. The carried BALANCE is never read from here: it is
/// <see cref="Zayra.Api.Infrastructure.Payroll.LeaveProvisionLedger"/> over the ledger, so the snapshot is
/// evidence, not a second source of the balance.</para>
///
/// <para>Unique (tenant, employee, period): one measurement per employee per month, which is also what makes
/// a retried Lock unable to provide twice.</para>
/// </summary>
public class LeaveProvisionSnapshot : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>"YYYY-MM" — the pay period the obligation was measured at the END of.</summary>
    public string Period { get; set; } = string.Empty;
    public DateOnly MeasuredAt { get; set; }
    /// <summary>The run whose Lock journal carries the movement (and whose void removes this row).</summary>
    public Guid PayrollRunId { get; set; }
    /// <summary>Unused leave days payable at <see cref="MeasuredAt"/> — the very figure a settlement would encash.</summary>
    public decimal Days { get; set; }
    /// <summary>The day rate the days were valued at: the company's leave cash-out basis divided by 30.</summary>
    public decimal DailyRate { get; set; }
    /// <summary>The cash-out basis rule that produced <see cref="DailyRate"/>: "Gross", "Basic" or "BasicHousing".</summary>
    public string Basis { get; set; } = string.Empty;
    /// <summary>Σ days x rate, exactly as the settlement's encashment computes it.</summary>
    public decimal Obligation { get; set; }
    /// <summary>The provision already carried for this employee before this month's movement.</summary>
    public decimal CarriedBefore { get; set; }
    /// <summary>Obligation − CarriedBefore: positive = DR leave expense / CR leave provision, negative = the reverse.</summary>
    public decimal Movement { get; set; }
    /// <summary>First measurement for this employee — the movement is the opening catch-up to date.</summary>
    public bool IsCatchUp { get; set; }
    /// <summary>Empty until saved: <see cref="Zayra.Api.Infrastructure.Payroll.CompanyCurrencyDefaults"/> fills it with the employing company's currency.</summary>
    public string Currency { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
