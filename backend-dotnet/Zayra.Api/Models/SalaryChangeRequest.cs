using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

/// <summary>
/// A salary package change waiting for a second person. HR (payroll.write) proposes it; a Finance approver
/// (payroll.approve) who is neither the maker nor the employee it is about applies it. Until then no
/// <see cref="EmployeeSalaryStructure"/> row exists, so payroll never sees an unapproved figure.
/// </summary>
public class SalaryChangeRequest : ITenantOwned, ICompanyScopedOperational
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    /// <summary>Groups the rows of one import so Finance can decide the file in one go; null for a single change.</summary>
    public Guid? BatchId { get; set; }
    /// <summary>Manual | Import.</summary>
    public string Source { get; set; } = "Manual";
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public Guid SalaryStructureId { get; set; }
    public decimal BasicSalary { get; set; }
    public decimal HousingAllowance { get; set; }
    public decimal TransportAllowance { get; set; }
    public decimal FoodAllowance { get; set; }
    public decimal MobileAllowance { get; set; }
    public decimal OtherAllowance { get; set; }
    public decimal FixedDeduction { get; set; }
    /// <summary>The proposed customer-defined cash allowances (<see cref="EmployeeSalaryAllowance"/> lines) as JSON, or null when the change proposes none.
    /// Staging only: they become <c>employee_salary_allowances</c> rows when Finance approves.</summary>
    public string? AllowancesJson { get; set; }
    /// <summary>The employee's written agreement to a pay reduction (a reference such as the signed amendment or the Qiwa request number), up to 200
    /// characters. Required for a change that lowers the employee's pay or any single pay item; whether a change IS a reduction is worked out against
    /// the salary record in force when it is read and decided, never stored, so it cannot go stale.</summary>
    public string? ReductionConsentReference { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public string Currency { get; set; } = string.Empty;
    /// <summary>The employee's active basic salary when the change was proposed, so the approver sees the movement.</summary>
    public decimal? PreviousBasicSalary { get; set; }

    public string Status { get; set; } = Pending;
    public Guid? RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? DecidedByUserId { get; set; }
    public string DecidedByName { get; set; } = string.Empty;
    public DateTime? DecidedAtUtc { get; set; }
    public string DecisionNote { get; set; } = string.Empty;
    /// <summary>The salary assignment this request produced once approved.</summary>
    public Guid? AppliedAssignmentId { get; set; }
}
