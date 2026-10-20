using System.Text.Json;

namespace Zayra.Api.Application.Setup;

public sealed record PolicyExtractionRequest(Guid DocumentId, bool UseAi, CompanyProfile Profile, int ChunkIndex = 0);
public sealed record PolicyProposal(string Target, string Field, JsonElement Value, string SourceQuote, int SourceStart = 0, int SourceLength = 0);
public sealed record PolicyPartialProposal(string Target, string Field, JsonElement Value, string SourceQuote,
    int SourceStart, int SourceLength, List<string> MissingFields);
public sealed record PolicyExtractionIssue(string Section, string Kind, string Message,
    string? Id = null, string? Code = null, string? Target = null, string? Field = null,
    string? SourceQuote = null, int? SourceStart = null, int? SourceLength = null);
public sealed record PolicyReviewRequirement(string Id, string Section, string Summary, string Capability,
    string Status, string SourceClassification, string SourceQuote, int SourceStart, int SourceLength,
    string? Target = null, string? Field = null, List<string>? MissingFields = null);
/// <summary>Source-backed values for the existing task editor, never saved or activated by extraction.</summary>
public sealed record PolicyOnboardingTaskDraft(string Id, string TaskTitle, string TaskDescription, string Category,
    string? AssignedToName, int? DueOffsetDays, bool? IsMandatory, string? Condition,
    string SourceQuote, int SourceStart, int SourceLength);
public sealed record PolicyCoverage(string Section, string Status);
public sealed record PolicyExtractionResult(Guid DocumentId, string SourceHash, string Provider,
    List<PolicyProposal> Proposals, List<PolicyExtractionIssue> Issues, List<PolicyCoverage> Coverage, string Message,
    PolicyExtractionProgress? Progress = null, List<PolicyPartialProposal>? PartialProposals = null,
    List<PolicyReviewRequirement>? Requirements = null, List<PolicyOnboardingTaskDraft>? OnboardingTaskDrafts = null);
public sealed record PolicyExtractionProgress(int ChunkIndex, int ChunkCount, int SourceStart, int SourceLength,
    int TotalCharacters, int? NextChunkIndex, bool IsComplete);
public sealed record SetupPolicySourceReference(Guid DocumentId, string ContentSha256);
public sealed record SetupPolicyDocumentSource(Guid DocumentId, string ContentSha256, string Name, int? CharacterCount = null);
/// <summary>UserCompleted marks reviewed manual additions; the excerpt supports extracted values only.</summary>
public sealed record SetupPolicyFieldSource(Guid DocumentId, string ContentSha256, string Target, string Field,
    int SourceStart, int SourceLength, bool UserCompleted = false);
