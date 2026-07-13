using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

public static class PositionStatuses
{
    public const string Open = "Open";
    public const string Filled = "Filled";
    public const string Frozen = "Frozen";
    public const string Closed = "Closed";
}

/// <summary>
/// Budgeted workforce slot. An employee is assigned to a position rather than only
/// to independent master-data values, so headcount and eligibility are enforceable.
/// </summary>
public class Position : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? CostCenterId { get; set; }
    public Guid? DesignationId { get; set; }
    public Guid? GradeId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public decimal Fte { get; set; } = 1m;
    public decimal BudgetedMonthlyCost { get; set; }
    public string Currency { get; set; } = "SAR";
    public string Status { get; set; } = PositionStatuses.Open;
    public int? IncumbentEmployeeId { get; set; }
    public DateOnly EffectiveFrom { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly? EffectiveTo { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
}
