using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// One request to use a fixed contract benefit (an education claim for a child, an air ticket, a count or amount of a customer's
/// own benefit) against the employee's FROZEN entitlement row for a period. It works the way a loan request does: the employee, or
/// HR for them, asks (Pending); Finance approves or rejects it; the requester may withdraw it while it is Pending (Cancelled). The
/// employee it is for never decides it, and whoever asked never approves it.
///
/// <para><b>Capability.</b> Benefit utilisation: entitlement (the frozen <see cref="EmployeeEntitlement"/> row, never today's grade
/// grid) vs used (Pending + Approved requests in the same benefit period) vs remaining. No money moves: nothing here reaches payroll,
/// the GL or payables.</para>
/// </summary>
public class BenefitUsageRequest : ITenantOwned, ICompanyScopedOperational
{
    /// <summary>Chosen by the client, so a retried submit is the same request.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>The frozen row's company. NOT NULL in the database.</summary>
    public Guid? CompanyId { get; set; }

    /// <summary>The int employees.id (the subject).</summary>
    public int EmployeeId { get; set; }

    /// <summary>Witness: the frozen employee_entitlements row the request is measured against.</summary>
    public Guid EntitlementId { get; set; }

    public string BenefitCode { get; set; } = string.Empty;

    /// <summary>The benefit period it counts in (Annual: the contract year; PerTerm: the term).</summary>
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    public DateOnly UsageDate { get; set; }

    /// <summary>The child (education) or family member the request is for; NULL = the employee.</summary>
    public Guid? DependantId { get; set; }

    /// <summary>A count (tickets, items). Exactly one of <see cref="Units"/> and <see cref="Amount"/> is set.</summary>
    public short? Units { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }

    public string? AcademicYear { get; set; }
    public string? ProviderName { get; set; }
    public string? EvidenceReference { get; set; }
    public string? Note { get; set; }

    public string Status { get; set; } = BenefitUsageStatuses.Pending;

    public Guid RequestedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Guid? DecidedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    /// <summary>Why it was rejected or withdrawn.</summary>
    public string? DecisionReason { get; set; }
}

/// <summary>Value set of <c>benefit_usage_requests.status</c>, the loan request's own words.</summary>
public static class BenefitUsageStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Pending, Approved, Rejected, Cancelled];

    /// <summary>What uses up entitlement: remaining = entitlement − (Pending + Approved).</summary>
    public static readonly string[] Counting = [Pending, Approved];
}
