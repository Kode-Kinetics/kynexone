using System.Text.Json;

namespace Zayra.Api.Application.Setup;

public sealed record PolicyExtractionRequest(Guid DocumentId, bool UseAi, CompanyProfile Profile);
public sealed record PolicyProposal(string Target, string Field, JsonElement Value, string SourceQuote, int SourceStart = 0, int SourceLength = 0);
public sealed record PolicyExtractionIssue(string Section, string Kind, string Message);
public sealed record PolicyCoverage(string Section, string Status);
public sealed record PolicyExtractionResult(Guid DocumentId, string SourceHash, string Provider,
    List<PolicyProposal> Proposals, List<PolicyExtractionIssue> Issues, List<PolicyCoverage> Coverage, string Message);
public sealed record SetupPolicySourceReference(Guid DocumentId, string ContentSha256);
public sealed record SetupPolicyFieldSource(Guid DocumentId, string ContentSha256, string Target, string Field, int SourceStart, int SourceLength);
