using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// ── W2-C: asset and equipment custody ──────────────────────────────────────────
//
// Three tables:
//   assets                     — the register: one row per physical item (laptop, phone, SIM, vehicle, …)
//   asset_assignments          — custody history. One row per period an employee held an item. A
//                                partial UNIQUE index on (asset_id) WHERE status = 'Active' makes
//                                "one holder at a time" a database fact, not a code convention.
//   asset_write_off_requests   — a request to write off a lost/damaged item, decided by the F1
//                                approval router (EntityName "AssetWriteOff"). Nothing changes on the
//                                asset until the workflow's FINAL step approves it.
//
// Category and condition vocabularies are MasterData types ("AssetCategory", "AssetCondition"), not tables.

public static class AssetStatuses
{
    public const string InStock = "InStock";
    public const string Assigned = "Assigned";
    public const string InRepair = "InRepair";
    public const string Retired = "Retired";
    public const string Lost = "Lost";

    public static readonly string[] All = [InStock, Assigned, InRepair, Retired, Lost];
    /// <summary>Terminal statuses: the item has left the register for good.</summary>
    public static bool IsTerminal(string status) => status is Retired or Lost;
}

public static class AssetAssignmentStatuses
{
    /// <summary>The employee holds the item now. At most one per asset (DB-enforced).</summary>
    public const string Active = "Active";
    public const string Returned = "Returned";
    public const string Transferred = "Transferred";
    /// <summary>Closed by an APPROVED write-off (lost / damaged beyond repair).</summary>
    public const string WrittenOff = "WrittenOff";
}

public static class AssetWriteOffStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

public static class AssetWriteOffKinds
{
    public const string Lost = "Lost";
    public const string Damaged = "Damaged";
}

/// <summary>One physical item in the tenant's asset register. Company-owned.</summary>
public class Asset : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>The legal entity that owns the item. Required on every write (operational scope).</summary>
    public Guid? CompanyId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? LocationId { get; set; }
    /// <summary>Free-text location detail (room, cabinet, warehouse bin).</summary>
    public string LocationNote { get; set; } = string.Empty;

    /// <summary>Tag / asset number printed on the label. Unique per tenant.</summary>
    public string AssetTag { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    /// <summary>MasterData "AssetCategory" value code (e.g. LAPTOP).</summary>
    public string CategoryCode { get; set; } = string.Empty;
    public string Make { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateOnly? PurchaseDate { get; set; }
    public decimal? PurchaseCost { get; set; }
    public string Currency { get; set; } = string.Empty;

    /// <summary>InStock | Assigned | InRepair | Retired | Lost — see <see cref="AssetStatuses"/>.</summary>
    public string Status { get; set; } = AssetStatuses.InStock;
    /// <summary>MasterData "AssetCondition" value code (NEW, GOOD, FAIR, DAMAGED, …).</summary>
    public string Condition { get; set; } = "GOOD";
    public string Notes { get; set; } = string.Empty;

    public DateTime? RetiredAtUtc { get; set; }
    public string RetirementReason { get; set; } = string.Empty;

    /// <summary>
    /// Optimistic concurrency token. Every custody operation increments it, so two writers that read the
    /// same state cannot both change it (e.g. retire racing an issue).
    /// </summary>
    public int Version { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>One custody period: who held the asset, from when, until when, in what condition.</summary>
public class AssetAssignment : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Copied from the asset at issue time.</summary>
    public Guid? CompanyId { get; set; }
    public Guid AssetId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string EmployeeCode { get; set; } = string.Empty;

    /// <summary>Active | Returned | Transferred | WrittenOff — see <see cref="AssetAssignmentStatuses"/>.</summary>
    public string Status { get; set; } = AssetAssignmentStatuses.Active;

    public DateOnly IssuedOn { get; set; }
    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? IssuedByUserId { get; set; }
    public string IssuedByName { get; set; } = string.Empty;
    public string ConditionOnIssue { get; set; } = string.Empty;
    public string IssueNotes { get; set; } = string.Empty;

    /// <summary>When the item is due back. Drives the due-soon and overdue reminders.</summary>
    public DateOnly? ExpectedReturnDate { get; set; }

    public DateOnly? ReturnedOn { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public string ClosedByName { get; set; } = string.Empty;
    public string ConditionOnReturn { get; set; } = string.Empty;
    public string ReturnNotes { get; set; } = string.Empty;
    /// <summary>For a Transferred row: the assignment that took over custody.</summary>
    public Guid? TransferredToAssignmentId { get; set; }
    /// <summary>For a WrittenOff row: the approved write-off that closed it.</summary>
    public Guid? WriteOffRequestId { get; set; }

    // Reminder ledger. Set only once the notification outbox holds the reminder (see AssetReturnReminderWorker).
    public DateTime? DueSoonReminderSentAtUtc { get; set; }
    public DateTime? OverdueReminderSentAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// A request to write off an asset (lost, or damaged beyond repair). Routed through the F1 approval engine
/// (<c>EntityName = "AssetWriteOff"</c>, <c>EntityId = Id</c>). Applied only when the approval request reaches
/// Approved — i.e. its FINAL step approved it.
/// </summary>
public class AssetWriteOffRequest : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid AssetId { get; set; }
    /// <summary>The custody period being written off, when the item was held by someone.</summary>
    public Guid? AssignmentId { get; set; }
    public int? EmployeeId { get; set; }
    /// <summary>Lost | Damaged.</summary>
    public string Kind { get; set; } = AssetWriteOffKinds.Lost;
    public string Reason { get; set; } = string.Empty;
    /// <summary>Pending | Approved | Rejected.</summary>
    public string Status { get; set; } = AssetWriteOffStatuses.Pending;
    public Guid? ApprovalRequestId { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAtUtc { get; set; }
    public string DecisionComments { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
