using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Every document attached to a person — iqama, passport, visa, work permit, contract scan, issued letter, sick note — with its expiry, its version chain and the file that holds the blob. @tier:T @owner:HR @retention:84-months-from-Expiry-then-Purge
/// </summary>
public partial class EmployeeDocument
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid? FileId { get; set; }

    public Guid? TemplateId { get; set; }

    /// <summary>
    /// Renewal chain. SET NULL on delete so a chain survives a removed predecessor (§8 row 50); indexed parent-side in revision 6 (§19.4).
    /// </summary>
    public Guid? SupersedesId { get; set; }

    /// <summary>
    /// Present only for sick notes. FK deferred to the cross-domain constraints pass: leave_requests is domain J.
    /// </summary>
    public Guid? LeaveRequestId { get; set; }

    public string? DocumentNumber { get; set; }

    public string? LetterNumber { get; set; }

    public string? VerificationCode { get; set; }

    public string DocType { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateOnly? IssueDate { get; set; }

    /// <summary>
    /// Read by the expiry-reminder job straight into notifications. There is deliberately no reminder table (§2.D).
    /// </summary>
    public DateOnly? ExpiryDate { get; set; }

    public string? IssuingCountry { get; set; }

    public int Version { get; set; }

    public int? TemplateVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual DocumentTemplate? DocumentTemplate { get; set; }

    public virtual Employee Employee { get; set; } = null!;

    public virtual ICollection<EmployeeContract> EmployeeContracts { get; set; } = new List<EmployeeContract>();

    public virtual EmployeeDocument? EmployeeDocumentNavigation { get; set; }

    public virtual File? File { get; set; }

    public virtual ICollection<EmployeeDocument> InverseEmployeeDocumentNavigation { get; set; } = new List<EmployeeDocument>();

    public virtual LeaveRequest? LeaveRequest { get; set; }
}
