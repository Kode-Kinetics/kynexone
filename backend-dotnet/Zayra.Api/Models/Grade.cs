using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class Grade : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Band { get; set; } = string.Empty;
    public int Level { get; set; }

    // ── Pay scale (salary band for the grade) ──────────────────────────────
    public decimal MinSalary { get; set; }
    public decimal MidSalary { get; set; }
    public decimal MaxSalary { get; set; }
    public string Currency { get; set; } = "SAR";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}

/// <summary>
/// A default pay-structure line for a grade — the benefit breakdown (Basic, Housing,
/// Transport, Ticket, Insurance, Health, Other …). Used to seed an employee's salary
/// structure and to communicate the grade's standard package.
/// </summary>
public class GradePayScaleComponent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid GradeId { get; set; }
    public string ComponentCode { get; set; } = string.Empty;   // BASIC, HOUSING, TRANSPORT, TICKET, INSURANCE, HEALTH, OTHER…
    public string ComponentName { get; set; } = string.Empty;
    public string ComponentType { get; set; } = "Earning";      // Earning | Benefit | Deduction
    public string CalculationType { get; set; } = "Fixed";      // Fixed | PercentOfBasic
    public decimal Amount { get; set; }
    public decimal Percentage { get; set; }
    public bool IsTaxable { get; set; }
    public string Frequency { get; set; } = "Monthly";          // Monthly | Annual
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
