namespace Zayra.Api.Application.Setup;

// A review is evidence of consideration, never authorization to execute a policy.
public sealed record PolicyReviewRequest(Guid CompanyId, Guid DocumentId, string SourceHash,
    string ItemType, string ItemKey, string Summary, string Disposition, string Comment,
    string? Owner = null, string? ApprovalReference = null, string? Target = null, string? Field = null,
    string? SourceQuote = null, int SourceStart = 0, int SourceLength = 0);

public sealed record PolicyReviewRecord(Guid Id, Guid CompanyId, Guid DocumentId, string SourceHash,
    string ItemType, string ItemKey, string Summary, string Disposition, string Comment,
    string? Owner, string? ApprovalReference, string? Target, string? Field, string? SourceQuote,
    int SourceStart, int SourceLength, Guid ReviewedByUserId, DateTime ReviewedAtUtc);
