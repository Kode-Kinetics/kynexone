using Zayra.Api.Application.Auth;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Application.Expenses;

// ── Requests ──────────────────────────────────────────────────────────────────

/// <param name="Id">Existing line id when editing a draft (keeps its receipt); null for a new line.</param>
public record ExpenseClaimLineRequest(
    Guid? Id,
    DateOnly ExpenseDate,
    [Required, MaxLength(80)] string CategoryCode,
    decimal Amount,
    [Required, MaxLength(500)] string Description);

/// <summary>There is deliberately NO currency field: claims are in the legal entity's payroll currency.</summary>
public record SaveExpenseClaimRequest(
    [MaxLength(200)] string? Title,
    [Required] IReadOnlyList<ExpenseClaimLineRequest> Lines);

public record ExpenseDecisionRequest(
    [Required, RegularExpression("Approve|Reject", ErrorMessage = "Decision must be Approve or Reject.")] string Decision,
    [MaxLength(1000)] string? Comments);

public record ExpenseCategoryPolicyRequest(
    [Range(0, 10_000_000)] decimal? MaxAmountPerClaim,
    [Range(0, 10_000_000)] decimal? ReceiptRequiredAbove);

public record ScheduleExpensesRequest(Guid PayrollRunId, IReadOnlyList<Guid>? ClaimIds);

public sealed class ExpenseClaimQuery
{
    public string? Status { get; set; }
    public int? EmployeeId { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CategoryCode { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Search { get; set; }
    public Guid? PayrollRunId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

// ── Responses ─────────────────────────────────────────────────────────────────

public record ExpenseClaimLineDto(
    Guid Id,
    int LineNumber,
    DateOnly ExpenseDate,
    string CategoryCode,
    string CategoryName,
    decimal Amount,
    string Description,
    bool HasReceipt,
    string? ReceiptFileName,
    string? ReceiptContentType,
    long? ReceiptSizeBytes);

public record ExpenseClaimDto(
    Guid Id,
    string ClaimNumber,
    int EmployeeId,
    string EmployeeName,
    Guid? CompanyId,
    string Title,
    string Currency,
    decimal TotalAmount,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? SubmittedAtUtc,
    DateTime? DecidedAtUtc,
    string? RejectionReason,
    Guid? ApprovalRequestId,
    Guid? PayrollRunId,
    string? PayrollPeriod,
    DateTime? ScheduledAtUtc,
    DateTime? PaidAtUtc,
    IReadOnlyList<ExpenseClaimLineDto> Lines,
    ExpenseApprovalProgressDto? Approval = null);

/// <summary>Where a submitted claim is in its approval chain — read from the ApprovalRequest row.</summary>
public record ExpenseApprovalProgressDto(
    string Status,
    int CurrentStepOrder,
    string CurrentApproverName,
    string CurrentApproverRole,
    DateTime? DueAtUtc,
    bool CanDecide);

public record ExpenseCategoryDto(
    Guid Id,
    string Code,
    string NameEn,
    string NameAr,
    bool IsActive,
    decimal? MaxAmountPerClaim,
    decimal? ReceiptRequiredAbove);

public record ExpenseViolation(int? LineNumber, string Code, string Message);

public record ExpensePayoutItem(Guid ClaimId, string ClaimNumber, int EmployeeId, decimal Amount, string Outcome, string? Reason);

public record ExpensePayoutResult(Guid PayrollRunId, int Scheduled, int Skipped, IReadOnlyList<ExpensePayoutItem> Items);

// ── Errors ────────────────────────────────────────────────────────────────────

/// <summary>A policy or input violation — 422 with every violation listed, never a silent accept.</summary>
public sealed class ExpenseValidationException : Exception
{
    public ExpenseValidationException(IReadOnlyList<ExpenseViolation> violations)
        : base(violations.Count == 1 ? violations[0].Message : $"{violations.Count} problems must be fixed before this claim can be saved.")
        => Violations = violations;

    public ExpenseValidationException(string code, string message)
        : this(new[] { new ExpenseViolation(null, code, message) }) { }

    public IReadOnlyList<ExpenseViolation> Violations { get; }
}

/// <summary>A state conflict (wrong status, concurrent change, already paid) — 409.</summary>
public sealed class ExpenseConflictException : Exception
{
    public ExpenseConflictException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class ExpenseNotFoundException : Exception
{
    public ExpenseNotFoundException(string message) : base(message) { }
}

// ── Service ───────────────────────────────────────────────────────────────────

public interface IExpenseClaimService
{
    Task<IReadOnlyList<ExpenseCategoryDto>> GetCategoriesAsync(Guid tenantId, bool includeInactive, CancellationToken ct);
    Task<ExpenseCategoryDto> UpdateCategoryPolicyAsync(Guid tenantId, string code, ExpenseCategoryPolicyRequest request, CancellationToken ct);

    // Employee (self-service) — every call is pinned to the caller's own employee id.
    Task<PagedResult<ExpenseClaimDto>> ListOwnAsync(Guid tenantId, int employeeId, string? status, int page, int pageSize, CancellationToken ct);
    Task<ExpenseClaimDto?> GetOwnAsync(Guid tenantId, int employeeId, Guid claimId, CancellationToken ct);
    Task<ExpenseClaimDto> CreateDraftAsync(Guid tenantId, int employeeId, SaveExpenseClaimRequest request, CancellationToken ct);
    Task<ExpenseClaimDto> UpdateDraftAsync(Guid tenantId, int employeeId, Guid claimId, SaveExpenseClaimRequest request, CancellationToken ct);
    Task<ExpenseClaimDto> AttachReceiptAsync(Guid tenantId, int employeeId, Guid claimId, Guid lineId, IFormFile file, CancellationToken ct);
    Task<ExpenseClaimDto> SubmitAsync(Guid tenantId, int employeeId, Guid claimId, RequestContext context, CancellationToken ct);
    Task<ExpenseClaimDto> CancelDraftAsync(Guid tenantId, int employeeId, Guid claimId, CancellationToken ct);

    // Approvers / HR / finance.
    Task<PagedResult<ExpenseClaimDto>> ListAsync(Guid tenantId, ExpenseClaimQuery query, IReadOnlyCollection<int>? allowedEmployeeIds, CancellationToken ct);
    Task<ExpenseClaimDto?> GetAsync(Guid tenantId, Guid claimId, RequestContext? context, CancellationToken ct);
    Task<IReadOnlyList<ExpenseClaimDto>> GetApprovalInboxAsync(Guid tenantId, RequestContext context, CancellationToken ct);
    Task<ExpenseClaimDto> DecideAsync(Guid tenantId, Guid claimId, ExpenseDecisionRequest request, RequestContext context, CancellationToken ct);
    Task<ExpensePayoutResult> ScheduleForPayrollAsync(Guid tenantId, ScheduleExpensesRequest request, Guid? actorUserId, CancellationToken ct);
    Task<ExpenseClaimDto> UnscheduleAsync(Guid tenantId, Guid claimId, Guid? actorUserId, CancellationToken ct);

    Task<(byte[] Content, string ContentType, string FileName)> GetReceiptAsync(Guid tenantId, Guid claimId, Guid lineId, CancellationToken ct);
}
