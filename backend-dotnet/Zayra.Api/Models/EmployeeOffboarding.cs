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
    /// <summary>
    /// S2-B2 — this is now an EFFECT, not a note. Setting it true runs the real revocation (deactivate
    /// the user, force AccessMode=NoLogin on every employee↔user link, revoke every live refresh token).
    /// It used to be a plain boolean while the only code that actually revoked anything ran inside
    /// <c>Complete</c> — so HR ticked "Access revoked" on the last working day and the ex-employee kept a
    /// working login for the whole settlement window. Because it is irreversible, it can only be cleared
    /// by rescinding the offboarding (which restores the login deliberately and audibly), never by
    /// un-ticking the box.
    /// </summary>
    public bool AccessRevoked { get; set; }
    /// <summary>S2-B2 — when the revocation actually ran, and who ran it.</summary>
    public DateTime? AccessRevokedAtUtc { get; set; }
    public Guid? AccessRevokedByUserId { get; set; }
    public bool KnowledgeHandover { get; set; }
    public bool FinalSettlementDone { get; set; }

    /// <summary>Backfill requisition raised for this vacancy, if any.</summary>
    public Guid? BackfillRequisitionId { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    // ── Rescind (S2-F4) ──────────────────────────────────────────────────────
    // A withdrawn resignation reinstates an employee and re-grants their login. That is an access
    // decision, and before this it left no trace at all: no actor, no time, no reason, no audit row.
    public DateTime? CancelledAtUtc { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }
}
