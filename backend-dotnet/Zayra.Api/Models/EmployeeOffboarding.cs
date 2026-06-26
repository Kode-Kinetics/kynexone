using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

/// <summary>
/// The separation lifecycle for an employee: resignation/termination → serving notice (Offboarded)
/// → final working day → Archived. Captures notice period, exit interview and an offboarding
/// checklist. One open record per employee at a time.
/// </summary>
public class EmployeeOffboarding : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string EmployeeCode { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;

    /// <summary>Resignation | Termination | EndOfContract | Retirement | Other.</summary>
    public string SeparationType { get; set; } = "Resignation";
    public string Reason { get; set; } = string.Empty;
    /// <summary>Date the resignation/termination was notified.</summary>
    public DateOnly NoticeDate { get; set; }
    public int NoticePeriodDays { get; set; }
    /// <summary>Final working day — defaults to NoticeDate + NoticePeriodDays.</summary>
    public DateOnly LastWorkingDay { get; set; }
    public bool RehireEligible { get; set; } = true;

    /// <summary>InProgress | Completed | Cancelled.</summary>
    public string Status { get; set; } = "InProgress";

    // ── Exit interview ───────────────────────────────────────────────────────
    /// <summary>Pending | Scheduled | Completed | Waived.</summary>
    public string ExitInterviewStatus { get; set; } = "Pending";
    public DateOnly? ExitInterviewDate { get; set; }
    /// <summary>Primary reason category captured at the exit interview (e.g. Compensation, Career, Management).</summary>
    public string ExitReasonCategory { get; set; } = string.Empty;
    /// <summary>Overall experience rating 1–5 (0 = not captured).</summary>
    public int ExitInterviewRating { get; set; }
    public string ExitInterviewNotes { get; set; } = string.Empty;

    // ── Offboarding checklist ────────────────────────────────────────────────
    public bool AssetsReturned { get; set; }
    public bool AccessRevoked { get; set; }
    public bool KnowledgeHandover { get; set; }
    public bool FinalSettlementDone { get; set; }

    /// <summary>Backfill requisition raised for this vacancy, if any.</summary>
    public Guid? BackfillRequisitionId { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
