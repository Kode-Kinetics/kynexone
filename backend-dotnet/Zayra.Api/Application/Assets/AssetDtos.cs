using System.ComponentModel.DataAnnotations;

namespace Zayra.Api.Application.Assets;

// ── Requests ────────────────────────────────────────────────────────────────────

public record AssetUpsertRequest(
    [Required, MaxLength(64)] string AssetTag,
    [MaxLength(200)] string? Name,
    [MaxLength(120)] string? SerialNumber,
    [MaxLength(64)] string? CategoryCode,
    [MaxLength(120)] string? Make,
    [MaxLength(120)] string? Model,
    DateOnly? PurchaseDate,
    decimal? PurchaseCost,
    [MaxLength(8)] string? Currency,
    [MaxLength(64)] string? Condition,
    Guid? CompanyId,
    Guid? BranchId,
    Guid? LocationId,
    [MaxLength(200)] string? LocationNote,
    [MaxLength(2000)] string? Notes);

public record IssueAssetRequest(
    int EmployeeId,
    DateOnly? IssuedOn,
    DateOnly? ExpectedReturnDate,
    [MaxLength(64)] string? Condition,
    [MaxLength(2000)] string? Notes);

public record ReturnAssetRequest(
    [Required, MaxLength(64)] string Condition,
    DateOnly? ReturnedOn,
    [MaxLength(2000)] string? Notes,
    /// <summary>True sends the returned item straight to InRepair instead of back to stock.</summary>
    bool SendToRepair = false);

public record TransferAssetRequest(
    int ToEmployeeId,
    DateOnly? ExpectedReturnDate,
    [MaxLength(64)] string? Condition,
    [MaxLength(2000)] string? Notes);

public record WriteOffAssetRequest(
    [Required, RegularExpression("Lost|Damaged", ErrorMessage = "Kind must be Lost or Damaged.")] string Kind,
    [Required, MinLength(5), MaxLength(2000)] string Reason);

public record RetireAssetRequest([Required, MinLength(5), MaxLength(1000)] string Reason);

public record RepairAssetRequest(
    [Required, RegularExpression("Send|Complete", ErrorMessage = "Action must be Send or Complete.")] string Action,
    [MaxLength(64)] string? Condition,
    [MaxLength(2000)] string? Notes);

public record ExtendReturnDateRequest(DateOnly? ExpectedReturnDate);

// ── Responses ───────────────────────────────────────────────────────────────────

public record AssetHolderDto(
    Guid AssignmentId, int EmployeeId, string EmployeeName, string EmployeeCode,
    DateOnly IssuedOn, DateOnly? ExpectedReturnDate, bool IsOverdue);

public record AssetListItemDto(
    Guid Id, string AssetTag, string Name, string SerialNumber, string CategoryCode,
    string Make, string Model, string Status, string Condition,
    Guid? CompanyId, Guid? BranchId, Guid? LocationId, string LocationNote,
    DateOnly? PurchaseDate, decimal? PurchaseCost, string Currency,
    AssetHolderDto? CurrentHolder, bool HasPendingWriteOff, int Version);

public record AssetAssignmentDto(
    Guid Id, Guid AssetId, string AssetTag, string AssetName, string CategoryCode,
    int EmployeeId, string EmployeeName, string EmployeeCode, string Status,
    DateOnly IssuedOn, DateTime IssuedAtUtc, string IssuedByName, string ConditionOnIssue, string IssueNotes,
    DateOnly? ExpectedReturnDate, DateOnly? ReturnedOn, DateTime? ClosedAtUtc, string ClosedByName,
    string ConditionOnReturn, string ReturnNotes, Guid? TransferredToAssignmentId, Guid? WriteOffRequestId,
    bool IsOverdue);

public record AssetWriteOffDto(
    Guid Id, Guid AssetId, Guid? AssignmentId, int? EmployeeId, string Kind, string Reason, string Status,
    Guid? ApprovalRequestId, string RequestedByName, DateTime RequestedAtUtc, DateTime? DecidedAtUtc,
    string DecisionComments);

public record AssetAuditEntryDto(string Action, DateTime AtUtc, Guid? UserId, string? Metadata);

public record AssetDetailDto(
    AssetListItemDto Asset,
    IReadOnlyList<AssetAssignmentDto> Assignments,
    IReadOnlyList<AssetWriteOffDto> WriteOffs,
    IReadOnlyList<AssetAuditEntryDto> AuditTrail);

public record AssetSummaryDto(
    int Total, IReadOnlyDictionary<string, int> ByStatus, int Overdue, int DueSoon,
    int PendingWriteOffs, IReadOnlyList<AssetCategoryCountDto> ByCategory);

public record AssetCategoryCountDto(string CategoryCode, int Count);

public record AssetLookupValueDto(string Code, string Label);

public record AssetLookupsDto(IReadOnlyList<AssetLookupValueDto> Categories, IReadOnlyList<AssetLookupValueDto> Conditions);

/// <summary>What an offboarding clearance needs to know about one leaver.</summary>
public record AssetClearanceDto(
    int EmployeeId,
    bool Clear,
    int OutstandingCount,
    IReadOnlyList<AssetAssignmentDto> Outstanding,
    IReadOnlyList<AssetWriteOffDto> PendingWriteOffs);

public record EmployeeAssetsDto(
    int EmployeeId,
    IReadOnlyList<AssetAssignmentDto> Current,
    IReadOnlyList<AssetAssignmentDto> History);

public record AssetPagedResult(int Total, int Page, int PageSize, IReadOnlyList<AssetListItemDto> Items);

/// <summary>A refused custody operation. <see cref="Status"/> is the HTTP status the API maps it to.</summary>
public sealed class AssetOperationException : InvalidOperationException
{
    public AssetOperationException(string code, string message, int status = 409, object? details = null)
        : base(message)
    {
        Code = code;
        Status = status;
        Details = details;
    }

    public string Code { get; }
    public int Status { get; }
    public object? Details { get; }
}
