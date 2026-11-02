using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

/// <summary>
/// A manual leave-balance adjustment above the approval threshold, waiting for a second HR person. Leave converts to cash through
/// encashment, so a large grant by one person is a path to money. HR (leave.write) proposes it; a different HR approver
/// (leave.approve) who is not the employee it is about applies it. Until then the employee's balance is untouched.
/// Small adjustments (up to the tenant's threshold, default 5 days) are applied at once as before.
/// </summary>
public class LeaveBalanceAdjustmentRequest : ITenantOwned, ICompanyScopedOperational
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public int Year { get; set; }
    /// <summary>Days, signed: positive grants, negative deducts.</summary>
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;

    public string Status { get; set; } = Pending;
    public Guid? RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? DecidedByUserId { get; set; }
    public string DecidedByName { get; set; } = string.Empty;
    public DateTime? DecidedAtUtc { get; set; }
    public string DecisionNote { get; set; } = string.Empty;
}
